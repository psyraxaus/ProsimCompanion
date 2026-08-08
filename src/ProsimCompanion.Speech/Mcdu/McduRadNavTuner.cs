using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Briefings;
using ProsimCompanion.Speech.Recognition;
using ProsimCompanion.Speech.Roles;

namespace ProsimCompanion.Speech.Mcdu;

/// <summary>
/// RAD NAV auto-tune (Prosim2FO semantics). On "tune the ILS" the FO sets the ILS frequency
/// for the resolved arrival runway on the CDU2 RADIO NAV page: it looks the frequency up in
/// the DFD nav data, announces intent with a cancel-window pause, opens RAD NAV, types the
/// frequency and selects the ILS/FREQ field (LSK3L), then reads the page back to confirm —
/// saying "unable" and stopping if anything doesn't verify. Gated like the FCU executor:
/// nothing is pressed unless the box is armed and the FO is pilot flying; "my aircraft" or
/// "negative" aborts. Degrades to a spoken explanation when ProSim, the DFD, or an ILS for
/// the runway is absent.
/// </summary>
public sealed class McduRadNavTuner : IVoiceFeature, IDisposable
{
    private static readonly string[] TunePhrases =
    [
        "tune the ils", "set the ils", "set up the ils", "tune the radios", "set the radios",
        "tune the localizer", "tune radio nav",
    ];

    private static readonly string[] CancelWords = ["negative", "disregard", "cancel", "belay that"];

    private const string RadNavKey = "RAD_NAV";
    private const string RadNavTitle = "RADIO NAV";

    // ARCHAEOLOGY (Prosim2FO, 2025): LSK3L is the ILS/FREQ field on the A320 RAD NAV page —
    // rows 1/2 are VOR1/VOR2, row 3 left is ILS.
    private const int IlsFreqRow = 3;

    private readonly ProcedureSource _procedures;
    private readonly DfdNavDataProvider _navData;
    private readonly IMcduReader _reader;
    private readonly IMcduActuator _actuator;
    private readonly RoleManager _roles;
    private readonly ISpeechArbiter _arbiter;
    private readonly IOptionsMonitor<McduOptions> _options;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<McduRadNavTuner> _logger;

    private readonly Action _onRoleChanged;
    private readonly SemaphoreSlim _execGate = new(1, 1);
    private readonly object _gate = new();
    private CancellationTokenSource? _pending;

    public McduRadNavTuner(
        ProcedureSource procedures,
        DfdNavDataProvider navData,
        IMcduReader reader,
        IMcduActuator actuator,
        RoleManager roles,
        ISpeechArbiter arbiter,
        IOptionsMonitor<McduOptions> options,
        JsonlEventLog eventLog,
        ILogger<McduRadNavTuner> logger)
    {
        ArgumentNullException.ThrowIfNull(procedures);
        ArgumentNullException.ThrowIfNull(navData);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(actuator);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _procedures = procedures;
        _navData = navData;
        _reader = reader;
        _actuator = actuator;
        _roles = roles;
        _arbiter = arbiter;
        _options = options;
        _eventLog = eventLog;
        _logger = logger;

        // "my aircraft" (any handover) aborts a running tune.
        _onRoleChanged = () => CancelPending();
        _roles.Changed += _onRoleChanged;
    }

    public IEnumerable<string> Phrases => TunePhrases;

    public bool ValueParse => false;

    public bool TryHandle(string utterance)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return false;
        }

        var text = CommandMatcher.Normalize(utterance);
        if (CancelWords.Contains(text))
        {
            // The cancelled task's own catch speaks "Disregard." — no double acknowledgement.
            return CancelPending(); // false: nothing pending, let others see the cancel word
        }

        if (!TunePhrases.Contains(text))
        {
            return false;
        }

        _ = TuneIlsAsync(requirePilotFlying: true);
        return true;
    }

    /// <summary>
    /// Tunes the ILS for the resolved arrival. <paramref name="requirePilotFlying"/> enforces
    /// the FO-is-PF gate (true for voice); a future web/API trigger passes false since the
    /// click is itself the authorization. Always requires the box to be armed.
    /// </summary>
    public async Task TuneIlsAsync(bool requirePilotFlying, CancellationToken cancellationToken = default)
    {
        // Degraded-mode ladder: explain the most fundamental missing piece first.
        if (!_reader.IsDisplayAvailable)
        {
            Speak("Unable — I can't see the M C D U; ProSim isn't connected.");
            _eventLog.Record("mcdu.radnav.refused", new { reason = "no-display" });
            return;
        }

        if (!_actuator.IsArmed)
        {
            Speak("Unable — the M C D U isn't armed.");
            _eventLog.Record("mcdu.radnav.refused", new { reason = "not-armed" });
            return;
        }

        if (requirePilotFlying && !_roles.IsFoPilotFlying)
        {
            Speak("You're pilot flying — the radios are yours.");
            return;
        }

        // Resolve the arrival and its ILS from the DFD nav data.
        ProcedureIdentifiers ids;
        NavDataFacts facts;
        try
        {
            ids = _procedures.Resolve(departure: false);
            facts = ids.Airport is null ? NavDataFacts.None : _navData.Lookup(ids.Airport, ids.Runway);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RAD NAV: resolving the arrival failed");
            Speak("Unable — I couldn't resolve the arrival.");
            return;
        }

        if (ids.Airport is null || ids.Runway is null)
        {
            Speak("Unable — I don't have an arrival runway.");
            _eventLog.Record("mcdu.radnav.unable", new { reason = "no-arrival" });
            return;
        }

        if (facts.IlsFrequencyMhz is not { } frequencyMhz)
        {
            Speak($"Unable — I don't have an ILS frequency for {McduSpeech.SpeakRunway(ids.Runway)}.");
            _eventLog.Record("mcdu.radnav.unable", new { reason = "no-ils", ids.Airport, ids.Runway });
            return;
        }

        var frequency = frequencyMhz.ToString("0.00", CultureInfo.InvariantCulture);
        var ident = string.IsNullOrWhiteSpace(facts.IlsIdent) ? null : facts.IlsIdent.Trim();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? previous;
        lock (_gate)
        {
            previous = _pending;
            _pending = cts;
        }

        previous?.Cancel();
        var token = cts.Token;

        await _execGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Speak($"Setting up the ILS for {McduSpeech.SpeakRunway(ids.Runway)}"
                + $"{(ident is null ? "" : $", {McduSpeech.SpeakLetters(ident)}")}, {McduSpeech.SpeakFrequency(frequency)}.");
            _eventLog.Record("mcdu.radnav.announce", new { freq = frequency, ident, ids.Airport, ids.Runway });

            await Task.Delay(VerifyPause(), token).ConfigureAwait(false);
            if (requirePilotFlying && !_roles.IsFoPilotFlying)
            {
                Speak("You have control — radios are yours.");
                _eventLog.Record("mcdu.radnav.aborted", new { reason = "role-changed" });
                return;
            }

            // Open RADIO NAV.
            if (!await _actuator.GoToPageAsync(RadNavKey, RadNavTitle, token).ConfigureAwait(false))
            {
                Speak("Unable — I couldn't open the radio nav page.");
                _eventLog.Record("mcdu.radnav.unable", new { reason = "no-page" });
                return;
            }

            // Type the frequency into the scratchpad (TypeAsync verifies it landed there).
            if (!await _actuator.TypeAsync(frequency, token).ConfigureAwait(false))
            {
                Speak("Unable — the frequency didn't enter correctly.");
                _eventLog.Record("mcdu.radnav.unable", new { reason = "type-mismatch", freq = frequency });
                return;
            }

            // Select the ILS/FREQ field, then read the page back to confirm: still on RAD NAV,
            // the frequency (or ident — ProSim swaps in the ident once tuned) shows in a row,
            // and the scratchpad no longer holds the typed frequency.
            await _actuator.PressLskAsync(IlsFreqRow, right: false, token).ConfigureAwait(false);
            var settled = await _actuator.WaitSettledAsync(token).ConfigureAwait(false);

            var onPage = settled.Title.Contains(RadNavTitle, StringComparison.OrdinalIgnoreCase);
            var shows = RowsContain(settled, frequency) || (ident is not null && RowsContain(settled, ident));
            var cleared = !settled.Scratchpad.Replace(" ", "")
                .Contains(frequency.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);

            if (onPage && shows && cleared)
            {
                Speak($"ILS tuned, {McduSpeech.SpeakFrequency(frequency)}.");
                _eventLog.Record("mcdu.radnav.tuned", new { freq = frequency, ident });
            }
            else
            {
                Speak("Unable — check the radio nav page.");
                _eventLog.Record("mcdu.radnav.unable",
                    new { reason = "verify-failed", freq = frequency, title = settled.Title, onPage, shows, cleared });
            }
        }
        catch (OperationCanceledException)
        {
            Speak("Disregard.");
            _eventLog.Record("mcdu.radnav.cancelled", new { freq = frequency });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RAD NAV tune failed");
            Speak("Unable — radio nav set-up failed.");
        }
        finally
        {
            _execGate.Release();
            lock (_gate)
            {
                if (ReferenceEquals(_pending, cts))
                {
                    _pending = null;
                }
            }

            cts.Dispose();
        }
    }

    /// <summary>True when any row's data contains <paramref name="text"/> ignoring spaces —
    /// pure, so the verification rule is testable without a box.</summary>
    public static bool RowsContain(McduPage page, string text)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(text);
        var needle = text.Replace(" ", "");
        return page.Rows.Any(r => r.Data.Replace(" ", "").Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        _roles.Changed -= _onRoleChanged;
        CancelPending();
        _execGate.Dispose();
    }

    private TimeSpan VerifyPause()
        => TimeSpan.FromSeconds(Math.Max(0, _options.CurrentValue.VerifyPauseSeconds));

    private bool CancelPending()
    {
        CancellationTokenSource? pending;
        lock (_gate)
        {
            pending = _pending;
            _pending = null;
        }

        pending?.Cancel();
        return pending is not null;
    }

    private void Speak(string text)
        => _ = _arbiter.EnqueueAsync(new SpeechRequest(text, SpeechPriority.Normal, Tag: "mcdu"));
}
