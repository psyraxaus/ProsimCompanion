using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;
using ProsimCompanion.Core.VoiceReference;

namespace ProsimCompanion.Speech.Callouts;

/// <summary>Which anti-ice system the pilot called.</summary>
public enum AntiIceSystem
{
    Engine,
    Wing,
}

/// <summary>A pilot's anti-ice call as heard: the system and the state announced.</summary>
public sealed record AntiIceCall(AntiIceSystem System, bool On);

/// <summary>
/// The pure half of the FO's anti-ice call response (owner request 2026-10-25: the threat
/// brief says "icing conditions, engine anti-ice", the pilot switches it on and says "engine
/// anti-ice on" — and nothing answered). The FO acknowledges and checks the overhead switches
/// (0 Normal = off, 1 Pushed = on): agreement earns "checked", a disagreement names the switch
/// it sees differently, unknown switches (ProSim gone) earn a plain acknowledgement. Verbal
/// only — nothing is written.
/// </summary>
public static class AntiIceCallDecider
{
    /// <summary>Every phrase this feature answers, in the spellings the ASR produces for the
    /// same words ("anti-ice", "anti ice", "antiice"). Hyphens vanish in normalisation, so the
    /// hyphenated forms are what the drawer shows and the plain forms what the grammar hears.</summary>
    public static IReadOnlyList<string> Phrases { get; } =
    [
        "engine anti-ice on", "engine anti-ice off",
        "engine anti ice on", "engine anti ice off",
        "engine antiice on", "engine antiice off",
        "engines anti-ice on", "engines anti-ice off",
        "wing anti-ice on", "wing anti-ice off",
        "wing anti ice on", "wing anti ice off",
        "wing antiice on", "wing antiice off",
        "anti-ice on", "anti-ice off", "anti ice on", "anti ice off", "antiice on", "antiice off",
    ];

    /// <summary>Reads the call out of a NORMALIZED utterance; null when it is not one. A bare
    /// "anti-ice on" means the engines — that is the call the threat brief asks for.</summary>
    public static AntiIceCall? Classify(string normalized)
    {
        ArgumentNullException.ThrowIfNull(normalized);

        var text = normalized.Replace("antiice", "anti ice", StringComparison.Ordinal)
            .Replace("anti icing", "anti ice", StringComparison.Ordinal);
        if (!text.Contains("anti ice", StringComparison.Ordinal))
        {
            return null;
        }

        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var on = tokens.Contains("on", StringComparer.Ordinal);
        var off = tokens.Contains("off", StringComparer.Ordinal);
        if (on == off)
        {
            return null; // neither or both — not a call
        }

        var system = tokens.Contains("wing", StringComparer.Ordinal) ? AntiIceSystem.Wing : AntiIceSystem.Engine;
        return new AntiIceCall(system, on);
    }

    /// <summary>The FO's line. Switch values are 0 off / 1 on; null = unknown (no ProSim).</summary>
    public static string Respond(AntiIceCall call, int? engine1, int? engine2, int? wing)
    {
        ArgumentNullException.ThrowIfNull(call);

        var state = call.On ? "on" : "off";
        if (call.System == AntiIceSystem.Wing)
        {
            return wing switch
            {
                null => $"Wing anti-ice {state}.",
                _ when (wing == 1) == call.On => $"Wing anti-ice {state}, checked.",
                _ => $"Wing anti-ice {state}... I show it {(wing == 1 ? "on" : "off")}.",
            };
        }

        if (engine1 is null && engine2 is null)
        {
            return $"Engine anti-ice {state}.";
        }

        var oneAgrees = engine1 is null || (engine1 == 1) == call.On;
        var twoAgrees = engine2 is null || (engine2 == 1) == call.On;
        if (oneAgrees && twoAgrees)
        {
            return $"Engine anti-ice {state}, checked.";
        }

        var seen = call.On ? "off" : "on";
        var which = (oneAgrees, twoAgrees) switch
        {
            (false, false) => "both engines",
            (false, true) => "engine one",
            _ => "engine two",
        };
        return $"Engine anti-ice {state}... I show {which} {seen}.";
    }
}

/// <summary>Answers the pilot's engine / wing anti-ice calls against the overhead switches.
/// Shares the engine &amp; flap call switch (<c>speech.engineFlapCallouts</c>): same kind of
/// crew call, same place in the settings.</summary>
public sealed class AntiIceCallFeature : IVoiceFeature, IDisposable
{
    private readonly IOptionsMonitor<SpeechOptions> _speechOptions;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<AntiIceCallFeature> _logger;
    private readonly IDataRefSubscription<int> _engine1;
    private readonly IDataRefSubscription<int> _engine2;
    private readonly IDataRefSubscription<int> _wing;

    public AntiIceCallFeature(
        IOptionsMonitor<SpeechOptions> speechOptions,
        IProsimDataRefs dataRefs,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<AntiIceCallFeature> logger)
    {
        ArgumentNullException.ThrowIfNull(speechOptions);
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _speechOptions = speechOptions;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
        _engine1 = dataRefs.Subscribe(ProsimDataRefNames.OhPneumaticEng1AntiIce);
        _engine2 = dataRefs.Subscribe(ProsimDataRefNames.OhPneumaticEng2AntiIce);
        _wing = dataRefs.Subscribe(ProsimDataRefNames.OhPneumaticWingAntiIce);
    }

    /// <summary>Roster declaration: right after the engine &amp; flap calls it shares a switch with.</summary>
    public static VoiceFeatureDeclaration Declared { get; } = new()
    {
        Precedence = 85,
        Gate = VoiceFeatureGate.ByOption("speech.engineFlapCallouts", VoiceReferenceTextIds.OffSpeechFo),
        Description = new DescribedVoiceReference("antiIce", "voiceReference.group.antiIce.title", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer,
        [
            new(["engine anti-ice on", "engine anti-ice off"], "voiceReference.group.antiIce.engine", "Engine anti-ice on, checked."),
            new(["wing anti-ice on", "wing anti-ice off"], "voiceReference.group.antiIce.wing", "Wing anti-ice on, checked."),
        ]),
    };

    VoiceFeatureDeclaration IVoiceFeature.Declaration => Declared;

    public bool Enabled => _speechOptions.CurrentValue.EngineFlapCallouts;

    public IEnumerable<string> Phrases => AntiIceCallDecider.Phrases;

    public bool ValueParse => false;

    /// <summary>Words only — an acknowledgement; nothing here touches the aircraft.</summary>
    public bool AcknowledgeOnly => true;

    public bool TryHandle(string utterance)
    {
        ArgumentNullException.ThrowIfNull(utterance);
        if (!_speechOptions.CurrentValue.EngineFlapCallouts)
        {
            return false;
        }

        var call = AntiIceCallDecider.Classify(CommandMatcher.Normalize(utterance));
        if (call is null)
        {
            return false;
        }

        var response = AntiIceCallDecider.Respond(call, Read(_engine1), Read(_engine2), Read(_wing));
        _logger.LogDebug("Anti-ice call \"{Utterance}\" -> \"{Response}\"", utterance, response);
        _eventLog.Record("crewcall.answered", new
        {
            utterance,
            response,
            system = call.System.ToString(),
            said = call.On ? "on" : "off",
            engine1 = Read(_engine1),
            engine2 = Read(_engine2),
            wing = Read(_wing),
        });
        _ = _arbiter.EnqueueAsync(new SpeechRequest(response, SpeechPriority.Normal, Tag: "crewCall"));
        return true;
    }

    public void Dispose()
    {
        _engine1.Dispose();
        _engine2.Dispose();
        _wing.Dispose();
    }

    private static int? Read(IDataRefSubscription<int> sub)
        => sub is { IsStale: false, RawValue: not null } ? sub.Value : null;
}
