using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Commands;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Gsx;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Crew;

/// <summary>
/// The interphone hail dialogues (ADR-0006 / issue #51): "cockpit to ground" is answered by
/// the ground crew, "cockpit to crew/cabin" by the purser, then a narrow listening window
/// opens for the request (shared catalog <see cref="GsxVoicePhrases"/> — the same named
/// commands as every other surface). The mic is borrowed (<see cref="IMicOwnership"/>) so a
/// running spoken checklist holds and resumes untouched. Ground replies honour the ACP INT
/// receive latch like the purser honours CAB: select INT before calling, or the reply waits
/// out the grace and plays anyway — a hail is never lost.
/// <para>
/// Transmit side (issue #72): with <c>speech.acpTransmitGating</c> on, a hail is only
/// ACCEPTED while the captain ACP transmit selector points at the hailed channel (INT for
/// ground, CAB for the cabin) — first miss earns a one-time FO coaching line, later misses
/// go unanswered — and moving the selector off the channel mid-dialogue hangs up. Decisions
/// live in <see cref="AcpHailGateCore"/>; an Unknown transmit state accepts as before.
/// </para>
/// </summary>
public sealed class CrewHailService : IVoiceFeature, IDisposable
{
    private static readonly string[] GroundHails =
        ["cockpit to ground", "flight deck to ground"];

    private static readonly string[] CabinHails =
        ["cockpit to crew", "flight deck to crew", "cockpit to cabin", "flight deck to cabin"];

    // One coaching line per session (AcpHailGateCore tracks the spend) — after that an
    // unkeyed hail is simply not answered, like the real interphone.
    private const string GroundCoachingText =
        "Select the interphone on the audio panel first, captain.";
    private const string CabinCoachingText =
        "Select the cabin channel on the audio panel first, captain.";

    private readonly IMicOwnership _mic;
    private readonly ISpeechArbiter _arbiter;
    private readonly GsxVoiceService _gsxVoice;
    private readonly IAcpChannel _acp;
    private readonly IOptionsMonitor<GsxOptions> _gsxOptions;
    private readonly IOptionsMonitor<GroundCrewOptions> _groundOptions;
    private readonly IOptionsMonitor<CabinOptions> _cabinOptions;
    private readonly IOptionsMonitor<SpeechOptions> _speechOptions;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<CrewHailService> _logger;
    private readonly AcpHailGateCore _hailGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private int _busy;

    public CrewHailService(
        IMicOwnership mic,
        ISpeechArbiter arbiter,
        GsxVoiceService gsxVoice,
        IAcpChannel acp,
        IOptionsMonitor<GsxOptions> gsxOptions,
        IOptionsMonitor<GroundCrewOptions> groundOptions,
        IOptionsMonitor<CabinOptions> cabinOptions,
        IOptionsMonitor<SpeechOptions> speechOptions,
        JsonlEventLog eventLog,
        ILogger<CrewHailService> logger)
    {
        ArgumentNullException.ThrowIfNull(mic);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(gsxVoice);
        ArgumentNullException.ThrowIfNull(acp);
        ArgumentNullException.ThrowIfNull(gsxOptions);
        ArgumentNullException.ThrowIfNull(groundOptions);
        ArgumentNullException.ThrowIfNull(cabinOptions);
        ArgumentNullException.ThrowIfNull(speechOptions);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _mic = mic;
        _arbiter = arbiter;
        _gsxVoice = gsxVoice;
        _acp = acp;
        _gsxOptions = gsxOptions;
        _groundOptions = groundOptions;
        _cabinOptions = cabinOptions;
        _speechOptions = speechOptions;
        _eventLog = eventLog;
        _logger = logger;
    }

    public bool Enabled => _gsxOptions.CurrentValue.VoiceControlEnabled;

    public IEnumerable<string> Phrases => GroundHails.Concat(CabinHails);

    public bool ValueParse => false;

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    public bool TryHandle(string utterance)
    {
        if (!_gsxOptions.CurrentValue.VoiceControlEnabled || string.IsNullOrWhiteSpace(utterance))
        {
            return false;
        }

        var text = utterance.Trim();
        var ground = GroundHails.Any(p => string.Equals(p, text, StringComparison.OrdinalIgnoreCase));
        var cabin = !ground && CabinHails.Any(p => string.Equals(p, text, StringComparison.OrdinalIgnoreCase));
        if (!ground && !cabin)
        {
            return false;
        }

        if (ground && !_groundOptions.CurrentValue.Enabled)
        {
            return false;
        }

        if (cabin && !_cabinOptions.CurrentValue.Enabled)
        {
            return false;
        }

        // Transmit gate (issue #72): the hail phrase is understood either way (consumed),
        // but with the selector off the channel nobody hears it — the crew does not answer.
        // One atomic snapshot (campaign #85): the key can't release between the two reads.
        var speech = _speechOptions.CurrentValue;
        var required = ground ? AcpTransmitTarget.Intercom : AcpTransmitTarget.Cabin;
        var transmit = _acp.Transmit;
        var decision = _hailGate.EvaluateHail(
            speech.AcpTransmitGating,
            speech.AcpIntKeyRequired,
            transmit.Target,
            transmit.IntKeyPushed,
            required);
        if (decision != HailGateDecision.Accept)
        {
            _logger.LogInformation(
                "Hail to {Channel} not keyed — ACP transmit on {Selected}, {Required} required",
                ground ? "ground" : "cabin", transmit.Target, required);
            _eventLog.Record("crew.hail.notkeyed", new
            {
                channel = ground ? "ground" : "cabin",
                selected = transmit.Target.ToString(),
                required = required.ToString(),
            });
            if (decision == HailGateDecision.Coach)
            {
                _ = SpeakAsync(
                    ground ? GroundCoachingText : CabinCoachingText,
                    "crew.hail.coach", SpeechRole.FirstOfficer);
            }

            return true;
        }

        _ = RunHailAsync(ground);
        return true;
    }

    private async Task RunHailAsync(bool ground)
    {
        // One dialogue at a time — a second hail while one runs is dropped, not queued (the
        // user is mid-conversation; queuing a stale hail would answer into silence).
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        try
        {
            IDisposable borrow;
            try
            {
                borrow = _mic.Borrow("crew-hail");
            }
            catch (InvalidOperationException)
            {
                _logger.LogDebug("Hail ignored — another dialogue owns the microphone");
                return;
            }

            using (borrow)
            {
                await RunDialogueAsync(ground).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown — fine
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Crew hail dialogue failed");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private async Task RunDialogueAsync(bool ground)
    {
        var role = ground ? SpeechRole.GroundCrew : SpeechRole.Purser;
        var tag = ground ? "ground.hail" : "cabin.hail";
        var reply = ground ? _groundOptions.CurrentValue.HailReplyText : _cabinOptions.CurrentValue.HailReplyText;
        var grammar = ground ? GsxVoicePhrases.GroundHailGrammar : GsxVoicePhrases.CabinHailGrammar;
        var standingBy = _groundOptions.CurrentValue.StandingByText;
        var required = ground ? AcpTransmitTarget.Intercom : AcpTransmitTarget.Cabin;

        _eventLog.Record("crew.hail", new { channel = ground ? "ground" : "cabin" });

        // "Latched to the call" (issue #72): while the dialogue runs, moving the transmit
        // selector off the channel hangs up. Linked to shutdown so both cancel the listen;
        // the ObjectDisposedException guard covers a selector change racing dialogue end.
        using var hangUp = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        void OnTransmitChanged(object? sender, EventArgs e)
        {
            if (AcpHailGateCore.ShouldHangUp(
                    _speechOptions.CurrentValue.AcpTransmitGating, _acp.Transmit.Target, required))
            {
                try
                {
                    hangUp.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // dialogue already over
                }
            }
        }

        _acp.Changed += OnTransmitChanged;
        try
        {
            // Catch a selector that moved between hail acceptance and dialogue start.
            OnTransmitChanged(this, EventArgs.Empty);

            // Receive-latch realism (decision 4): the crew answers on their channel — you hear
            // them once INT (ground) / CAB (purser) is selected on any ACP, or after the grace.
            // The latch-or-grace wait is the ACP channel module's (campaign #85).
            if (ground && _groundOptions.CurrentValue.RequireIntChannel)
            {
                await _acp.AwaitReceiveAsync(
                    AcpChannelKind.Intercom,
                    TimeSpan.FromSeconds(_groundOptions.CurrentValue.IntChannelGraceSeconds),
                    _shutdown.Token).ConfigureAwait(false);
            }
            else if (!ground && _cabinOptions.CurrentValue.RequireCabChannel)
            {
                await _acp.AwaitReceiveAsync(
                    AcpChannelKind.Cabin,
                    TimeSpan.FromSeconds(_cabinOptions.CurrentValue.CabChannelGraceSeconds),
                    _shutdown.Token).ConfigureAwait(false);
            }

            if (hangUp.IsCancellationRequested)
            {
                // Hung up before the crew answered — no reply, nothing to stand by for.
                _eventLog.Record("crew.hail.hangup", new { channel = ground ? "ground" : "cabin" });
                return;
            }

            await _arbiter.EnqueueAsync(new SpeechRequest(
                reply, SpeechPriority.Normal, Ttl: TimeSpan.FromSeconds(30), Tag: tag, Role: role))
                .ConfigureAwait(false);

            var timeout = TimeSpan.FromSeconds(Math.Max(3, _groundOptions.CurrentValue.HailListenTimeoutSeconds));
            string? heard;
            try
            {
                heard = await _mic.ListenAsync(grammar, timeout, hangUp.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested)
            {
                // The pilot moved the selector off the channel mid-call — end politely on
                // the existing dialogue-end path (they may still have the REC latch open).
                _eventLog.Record("crew.hail.hangup", new { channel = ground ? "ground" : "cabin" });
                await SpeakAsync(standingBy, tag, role).ConfigureAwait(false);
                return;
            }

            if (heard is null
                || GsxVoicePhrases.CancelPhrases.Any(c => string.Equals(c, heard, StringComparison.OrdinalIgnoreCase)))
            {
                await SpeakAsync(standingBy, tag, role).ConfigureAwait(false);
                return;
            }

            _eventLog.Record("crew.hail.request", new { channel = ground ? "ground" : "cabin", heard });

            if (GsxVoicePhrases.StartGroundServicesPhrases.Any(
                    p => string.Equals(p, heard, StringComparison.OrdinalIgnoreCase)))
            {
                await HandleStartAsync(tag, role).ConfigureAwait(false);
                return;
            }

            if (GsxVoicePhrases.Bindings.TryGetValue(heard, out var binding))
            {
                await HandleServiceRequestAsync(binding, tag, role).ConfigureAwait(false);
                return;
            }

            // A grammar phrase we do not map (should not happen — the grammar IS the catalog).
            await SpeakAsync(standingBy, tag, role).ConfigureAwait(false);
        }
        finally
        {
            // Detach BEFORE the using-dispose of hangUp runs, so a late selector change
            // cannot Cancel a disposed source (the ODE guard is belt-and-braces for an
            // in-flight handler on the SDK thread).
            _acp.Changed -= OnTransmitChanged;
        }
    }

    private async Task HandleStartAsync(string tag, SpeechRole role)
    {
        var result = await _gsxVoice.ExecuteAsync("gsx.startDepartureServices").ConfigureAwait(false);
        var line = result.Outcome switch
        {
            CommandOutcome.Success => "Copied — commencing ground services.",
            CommandOutcome.AlreadySatisfied => "Ground services are already underway.",
            _ => null,
        };

        if (line is not null)
        {
            await SpeakAsync(line, tag, role).ConfigureAwait(false);
        }
        else
        {
            // Refusals are system explanations — the FO relays them, not the crew.
            await SpeakAsync(result.Reason, tag + ".refused", SpeechRole.FirstOfficer).ConfigureAwait(false);
        }
    }

    private async Task HandleServiceRequestAsync(GsxVoiceBinding binding, string tag, SpeechRole role)
    {
        var result = await _gsxVoice.ExecuteAsync(binding.Command).ConfigureAwait(false);
        _logger.LogInformation(
            "Hail request {Command} → {Outcome}: {Reason}", binding.Command, result.Outcome, result.Reason);

        switch (result.Outcome)
        {
            case CommandOutcome.Success:
                await SpeakAsync(binding.CrewAckPhrase, tag, role).ConfigureAwait(false);
                break;
            case CommandOutcome.AlreadySatisfied:
                // The reasons read naturally as crew speech ("Refueling is already
                // requested — crew en route.").
                await SpeakAsync(result.Reason, tag, role).ConfigureAwait(false);
                break;
            default:
                await SpeakAsync(result.Reason, tag + ".refused", SpeechRole.FirstOfficer).ConfigureAwait(false);
                break;
        }
    }

    private Task SpeakAsync(string text, string tag, SpeechRole role)
        => _arbiter.EnqueueAsync(new SpeechRequest(
            text, SpeechPriority.Normal, Ttl: TimeSpan.FromMinutes(1), Tag: tag, Role: role));
}
