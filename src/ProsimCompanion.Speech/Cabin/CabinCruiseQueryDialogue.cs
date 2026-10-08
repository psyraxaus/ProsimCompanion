using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Cabin;

/// <summary>How one cruise query ended, for the shell and the tests.</summary>
public enum CruiseQueryOutcome
{
    /// <summary>The microphone was borrowed by another dialogue — nothing rang, the shell retries.</summary>
    MicBusy,

    /// <summary>The purser asked and the captain answered (the kind says what with).</summary>
    Replied,

    /// <summary>The purser asked and the window closed in silence.</summary>
    NoReply,

    /// <summary>Shutdown or the shell's cancel while the dialogue ran.</summary>
    Cancelled,
}

/// <summary>The dialogue's result: the outcome, what was heard and how it was read.</summary>
public sealed record CruiseQueryResult(CruiseQueryOutcome Outcome, string? Heard, CruiseReplyKind Kind);

/// <summary>
/// The purser's cruise query dialogue (Prosim2FO "Prompt F"): borrow the mic, ring and wait
/// for CAB (the shell's chime + latch-or-grace, handed in as a step so this class owns no
/// datarefs), ask in the purser voice, listen for the captain's free reply, acknowledge. The
/// mic is borrowed the way the hails borrow it (<see cref="IMicOwnership"/>): routing stands
/// down, a running checklist holds and resumes on release. With the LAN transcriber the
/// window is free-form (the raw sentence reaches the classifier untouched); on the offline
/// closed-grammar engine the window carries <see cref="CabinCruiseQueryCore.OfflineGrammar"/>
/// instead, so "on time" and "light chop" still work there. Speech and session events only —
/// the reply is never parsed into the aircraft, a radio or GSX.
/// </summary>
public sealed class CabinCruiseQueryDialogue
{
    public const string QueryEvent = "cabin.cruise-query";
    public const string ReplyEvent = "cabin.cruise-reply";
    public const string Tag = "cabin.cruise-query";

    private readonly IMicOwnership _mic;
    private readonly ISpeechArbiter _arbiter;
    private readonly IOptionsMonitor<CabinOptions> _options;
    private readonly IFlightPhaseSource _flight;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<CabinCruiseQueryDialogue> _logger;
    private readonly IRecognitionWindow? _window;

    public CabinCruiseQueryDialogue(
        IMicOwnership mic,
        ISpeechArbiter arbiter,
        IOptionsMonitor<CabinOptions> options,
        IFlightPhaseSource flight,
        JsonlEventLog eventLog,
        ILogger<CabinCruiseQueryDialogue> logger,
        IRecognitionWindow? window = null)
    {
        ArgumentNullException.ThrowIfNull(mic);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _mic = mic;
        _arbiter = arbiter;
        _options = options;
        _flight = flight;
        _eventLog = eventLog;
        _logger = logger;
        _window = window;
    }

    /// <summary>Runs the whole exchange. <paramref name="ringAndWait"/> is the shell's chime +
    /// CAB wait, run only once the mic is ours — a busy mic must not ring the cabin.</summary>
    public async Task<CruiseQueryResult> RunAsync(Func<CancellationToken, Task> ringAndWait, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ringAndWait);

        IDisposable borrow;
        try
        {
            borrow = _mic.Borrow("cabin-cruise-query");
        }
        catch (InvalidOperationException)
        {
            _logger.LogInformation("Purser cruise query deferred — another dialogue owns the microphone");
            return new CruiseQueryResult(CruiseQueryOutcome.MicBusy, null, CruiseReplyKind.Silence);
        }

        using (borrow)
        {
            try
            {
                return await RunDialogueAsync(ringAndWait, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new CruiseQueryResult(CruiseQueryOutcome.Cancelled, null, CruiseReplyKind.Silence);
            }
        }
    }

    private async Task<CruiseQueryResult> RunDialogueAsync(Func<CancellationToken, Task> ringAndWait, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var freeForm = _window?.FreeFormCapable ?? true;
        var window = TimeSpan.FromSeconds(Math.Max(3, options.CruiseQueryWindowSeconds));

        await ringAndWait(cancellationToken).ConfigureAwait(false);

        _eventLog.Record(QueryEvent, new
        {
            text = options.CruiseQueryText,
            windowSeconds = (int)window.TotalSeconds,
            freeForm,
            phase = _flight.CurrentPhase.ToString(),
        });
        _logger.LogInformation("Purser cruise query: \"{Text}\" ({Window} s window, free-form {FreeForm})",
            options.CruiseQueryText, (int)window.TotalSeconds, freeForm);

        // Awaited to the terminal outcome so the window opens after the question, not under
        // it. Still valid only in the cruise: a descent that began in the queue drops it.
        await _arbiter.EnqueueAsync(new SpeechRequest(
            options.CruiseQueryText, SpeechPriority.Normal, Ttl: TimeSpan.FromSeconds(45),
            IsStillValid: () => _flight.CurrentPhase == FlightPhase.Cruise,
            Tag: Tag, Role: SpeechRole.Purser), cancellationToken).ConfigureAwait(false);

        var heard = await _mic.ListenAsync(
            freeForm ? [] : CabinCruiseQueryCore.OfflineGrammar, window, cancellationToken).ConfigureAwait(false);
        var kind = CabinCruiseQueryCore.Classify(heard);
        var ack = CabinCruiseQueryCore.Acknowledgement(kind, options);

        _eventLog.Record(ReplyEvent, new { heard, kind = CabinCruiseQueryCore.Name(kind), ack });
        _logger.LogInformation("Purser cruise reply: {Kind} (\"{Heard}\") -> \"{Ack}\"", kind, heard ?? "", ack);

        await _arbiter.EnqueueAsync(new SpeechRequest(
            ack, SpeechPriority.Normal, Ttl: TimeSpan.FromSeconds(45), Tag: Tag, Role: SpeechRole.Purser),
            cancellationToken).ConfigureAwait(false);

        return new CruiseQueryResult(
            kind == CruiseReplyKind.Silence ? CruiseQueryOutcome.NoReply : CruiseQueryOutcome.Replied, heard, kind);
    }
}
