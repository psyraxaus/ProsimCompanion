using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Callouts;

/// <summary>
/// The pure decision core for spoken engine-start and flap calls (issue #67), extracted from
/// the feature so the below/above-placard and already-running branches are testable without
/// timers or DI. All mappings use the aircraft.flap.positionHandle scale
/// (0=Up 1=F1 2=1+F 3=F2 4=F3 5=F4 — NOT the S_FC_FLAPS scale), the same keying
/// <see cref="CalloutsEngine.Placards"/> reads <c>SopOptions.FlapPlacards</c> with, so a
/// user-edited placard table drives both the reactive advisory and this spoken check.
/// </summary>
public static class EngineFlapCallDecider
{
    /// <summary>Spoken flap position → flap HANDLE position. "two" is handle 3 because
    /// handle 2 is the 1+F takeoff config the crew never calls for by number.</summary>
    private static readonly IReadOnlyDictionary<string, int> FlapWordToHandle =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["one"] = 1,
            ["two"] = 3,
            ["three"] = 4,
            ["full"] = 5,
        };

    /// <summary>All phrases this feature answers — contributed to the closed grammar so the
    /// offline engine hears them too.</summary>
    public static IReadOnlyList<string> Phrases { get; } =
    [
        "starting engine one", "starting engine two",
        "start engine one", "start engine two",
        "engine one start", "engine two start",
        "flaps one", "flaps two", "flaps three", "flaps full",
        "flaps up", "flaps zero",
    ];

    /// <summary>Decides the FO's verbal response to a NORMALIZED utterance; null when the
    /// utterance is not an engine-start or flap call (the router keeps looking). Verbal only
    /// by design — no dataref is ever written.</summary>
    /// <param name="normalized"><see cref="CommandMatcher.Normalize"/>d utterance.</param>
    /// <param name="snapshot">Current flight data (IAS, flap handle, engine states).</param>
    /// <param name="placards">Max IAS per flap handle position (SopOptions.FlapPlacards).</param>
    public static string? Decide(
        string normalized, FlightDataSnapshot snapshot, IReadOnlyList<FlapPlacard> placards)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(placards);

        if (TryEngineNumber(normalized, out var engineWord))
        {
            var running = engineWord == "one"
                ? IsRunning(snapshot.RawEngine1State)
                : IsRunning(snapshot.RawEngine2State);
            return running
                ? $"Engine {engineWord} is already running."
                : $"Engine {engineWord}.";
        }

        if (normalized is "flaps up" or "flaps zero")
        {
            // Retraction: plain acknowledgement — there is no placard to exceed by
            // RETRACTING, and clean-speed logic is out of scope for the first cut.
            return "Flaps up.";
        }

        foreach (var (word, handle) in FlapWordToHandle)
        {
            if (normalized != $"flaps {word}")
            {
                continue;
            }

            var placard = placards.FirstOrDefault(p => p.FlapHandle == handle);
            if (placard is null)
            {
                // User-edited placard table without this handle — acknowledge without
                // claiming a check that never happened.
                return $"Flaps {word}.";
            }

            var ias = (int)Math.Round(snapshot.IndicatedAirspeedKt);
            return ias > placard.MaxKt
                ? $"Negative — speed {ias}, flaps {word} limit is {placard.MaxKt}."
                : $"Speed checked, flaps {word}.";
        }

        return null;
    }

    private static bool TryEngineNumber(string normalized, out string engineWord)
    {
        foreach (var word in (string[])["one", "two"])
        {
            if (normalized == $"starting engine {word}"
                || normalized == $"start engine {word}"
                || normalized == $"engine {word} start")
            {
                engineWord = word;
                return true;
            }
        }

        engineWord = "";
        return false;
    }

    /// <summary>The descriptive engine-state string is the discriminator ("off"/"starting"/
    /// "running"); a missing or unexpected string reads as not running, so the FO gives the
    /// plain acknowledgement rather than a wrong correction.</summary>
    private static bool IsRunning(string? engineState)
        => string.Equals(engineState, "running", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Voice feature for pilot engine-start and flap calls (issue #67): "starting engine two" is
/// acknowledged (with a gentle correction when that engine is already running), and
/// "flaps one/two/three/full" is answered with a placard speed check against the REQUESTED
/// handle position ("Speed checked, flaps two." / "Negative — speed 240, …"). Speeds are
/// written as plain digits — AviationSpeech normalizes numbers downstream in the TTS path.
///
/// VERBAL ONLY, deliberately: the FO never moves the flap lever or the engine masters.
/// Lever actuation is deferred pending a write-safety review — S_FC_FLAPS is not on the
/// write allow-list, and the write-safety rule (dataref-first, allow-listed) must be settled
/// before this feature is allowed to touch the aircraft.
/// </summary>
public sealed class EngineFlapCallFeature : IVoiceFeature
{
    private readonly IOptionsMonitor<SpeechOptions> _speechOptions;
    private readonly IOptionsMonitor<SopOptions> _sopOptions;
    private readonly IFlightDataSource _source;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<EngineFlapCallFeature> _logger;

    public EngineFlapCallFeature(
        IOptionsMonitor<SpeechOptions> speechOptions,
        IOptionsMonitor<SopOptions> sopOptions,
        IFlightDataSource source,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<EngineFlapCallFeature> logger)
    {
        ArgumentNullException.ThrowIfNull(speechOptions);
        ArgumentNullException.ThrowIfNull(sopOptions);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _speechOptions = speechOptions;
        _sopOptions = sopOptions;
        _source = source;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
    }

    /// <summary>Disabled contributes NO phrases, so the closed offline grammar shrinks with
    /// the option instead of hearing calls the feature would then ignore.</summary>
    public bool Enabled => _speechOptions.CurrentValue.EngineFlapCallouts;

    public IEnumerable<string> Phrases => EngineFlapCallDecider.Phrases;

    public bool ValueParse => false;

    public bool TryHandle(string utterance)
    {
        ArgumentNullException.ThrowIfNull(utterance);
        if (!_speechOptions.CurrentValue.EngineFlapCallouts)
        {
            return false;
        }

        var response = EngineFlapCallDecider.Decide(
            CommandMatcher.Normalize(utterance), _source.Sample(), _sopOptions.CurrentValue.FlapPlacards);
        if (response is null)
        {
            return false;
        }

        _logger.LogDebug("Engine/flap call \"{Utterance}\" -> \"{Response}\"", utterance, response);
        _eventLog.Record("crewcall.answered", new { utterance, response });
        _ = _arbiter.EnqueueAsync(new SpeechRequest(response, SpeechPriority.Normal, Tag: "crewCall"));
        return true;
    }
}
