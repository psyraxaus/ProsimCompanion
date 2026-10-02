using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.VoiceReference;
using ProsimCompanion.Speech.Gsx;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.VoiceReference;

/// <summary>
/// Composes the Voice Reference (issue #136) from the LIVE grammar: every registered
/// <see cref="IVoiceFeature"/> (its current <c>Enabled</c> and <c>Phrases</c>), the checklist
/// start phrases, the drill triggers, commands.json and atc-requests.json. The wording comes
/// from <see cref="VoiceReferenceDescribers"/>, but a described phrase the feature no longer
/// contributes is dropped and a live phrase the table does not know still appears
/// (undescribed) — the drawer can be incomplete in words, never wrong about the grammar.
/// </summary>
public sealed class VoiceReferenceBuilder : IVoiceReference
{
    private const string SettingsFo = "Settings → Voice First Officer";

    private readonly IReadOnlyList<IVoiceFeature> _features;
    private readonly IChecklistDefinitionSource _checklists;
    private readonly Abnormals.IDrillSource _failures;
    private readonly IOptionsMonitor<SpeechOptions> _speech;
    private readonly IOptionsMonitor<GroundCrewOptions> _groundCrew;
    private readonly IOptionsMonitor<CabinOptions> _cabin;
    private readonly ILogger<VoiceReferenceBuilder> _logger;
    private readonly IFreeFormQuestionHandler? _questions;

    public VoiceReferenceBuilder(
        IEnumerable<IVoiceFeature> features,
        IChecklistDefinitionSource checklists,
        Abnormals.IDrillSource failures,
        IOptionsMonitor<SpeechOptions> speech,
        IOptionsMonitor<GroundCrewOptions> groundCrew,
        IOptionsMonitor<CabinOptions> cabin,
        ILogger<VoiceReferenceBuilder> logger,
        IFreeFormQuestionHandler? questions = null)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(checklists);
        ArgumentNullException.ThrowIfNull(failures);
        ArgumentNullException.ThrowIfNull(speech);
        ArgumentNullException.ThrowIfNull(groundCrew);
        ArgumentNullException.ThrowIfNull(cabin);
        ArgumentNullException.ThrowIfNull(logger);

        _features = [.. features];
        _checklists = checklists;
        _failures = failures;
        _speech = speech;
        _groundCrew = groundCrew;
        _cabin = cabin;
        _logger = logger;
        _questions = questions;
    }

    public VoiceReferenceSnapshot Build()
    {
        var groups = new List<VoiceReferenceGroup>();
        foreach (var feature in _features)
        {
            try
            {
                groups.AddRange(Describe(feature));
            }
            catch (Exception ex)
            {
                // One feature's phrase getter failing (a file-backed one mid-reload) must not
                // blank the whole drawer.
                _logger.LogDebug(ex, "Voice reference skipped {Feature}", feature.GetType().Name);
            }
        }

        groups.Add(ChecklistStarts());
        groups.Add(Drills());
        groups.Add(Questions());

        // Tab order, then the described features in registration order.
        var ordered = groups
            .OrderBy(g => g.Tab)
            .ThenBy(g => g.Enabled ? 0 : 1)
            .ToList();

        return new VoiceReferenceSnapshot(ordered, AlwaysAvailable, PttBindingText(_speech.CurrentValue));
    }

    // ---- per-feature composition ---------------------------------------------------------

    private IEnumerable<VoiceReferenceGroup> Describe(IVoiceFeature feature)
    {
        var live = LivePhrases(feature);
        switch (feature)
        {
            case GsxVoiceService:
                return GsxGroups(feature, live);
            case Crew.CrewHailService:
                return HailGroups(feature, live);
            case Commands.ConfiguredVoiceCommands configured:
                return [ConfiguredGroup(configured, live)];
            case SayIntentions.SayIntentionsService sayIntentions:
                return [AtcGroup(sayIntentions, live)];
        }

        if (VoiceReferenceDescribers.ByFeatureType.TryGetValue(feature.GetType(), out var describer))
        {
            return [FromDescriber(feature, describer, live)];
        }

        // Unknown feature type: everything it contributes, undescribed, on the FO tab.
        return
        [
            new VoiceReferenceGroup(
                feature.GetType().Name, feature.GetType().Name, VoiceReferenceTab.FirstOfficer,
                VoiceSpeaker.FirstOfficer, feature.Enabled, feature.Enabled ? null : "switched off",
                [.. live.Select(p => new VoiceReferenceEntry([p], ""))]),
        ];
    }

    private static VoiceReferenceGroup FromDescriber(IVoiceFeature feature, FeatureDescriber describer, List<string> live)
    {
        var entries = new List<VoiceReferenceEntry>();
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in describer.Entries)
        {
            var present = new List<string>();
            foreach (var key in entry.Phrases)
            {
                var matches = live.Where(p => Matches(key, p)).ToList();
                if (matches.Count == 0)
                {
                    continue;
                }

                present.Add(key);
                foreach (var match in matches)
                {
                    covered.Add(match);
                }
            }

            if (present.Count > 0)
            {
                entries.Add(new VoiceReferenceEntry(
                    present, entry.What, entry.Answer, entry.Speaker ?? describer.Speaker, entry.Badges, entry.ValueHint));
            }
        }

        // Live phrases nobody described: still listed, so the grammar is never hidden.
        foreach (var phrase in live.Where(p => !covered.Contains(p)))
        {
            entries.Add(new VoiceReferenceEntry([phrase], "", Speaker: describer.Speaker));
        }

        return new VoiceReferenceGroup(
            describer.Id, describer.Title, describer.Tab, describer.Speaker,
            feature.Enabled, feature.Enabled ? null : describer.DisabledReason ?? "switched off", entries);
    }

    /// <summary>The GSX single-shot phrases, grouped by the command they run, straight from
    /// <see cref="GsxVoicePhrases"/> — the answer is the FO's real confirmation line. The
    /// cabin-flavoured boarding phrases (purser ack) go to the Cabin tab.</summary>
    private static IEnumerable<VoiceReferenceGroup> GsxGroups(IVoiceFeature feature, List<string> live)
    {
        var reason = feature.Enabled ? null : "gsx.voiceControlEnabled is off — Settings → Ground Services";
        var ground = new List<VoiceReferenceEntry>();
        var cabin = new List<VoiceReferenceEntry>();

        var start = GsxVoicePhrases.StartGroundServicesPhrases.Where(p => live.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
        if (start.Count > 0)
        {
            ground.Add(new VoiceReferenceEntry(start,
                "Start the departure sequence (or call the next service once it is running).", "Ground services started."));
        }

        foreach (var byCommand in GsxVoicePhrases.Bindings.GroupBy(b => (b.Value.Command, b.Value.CabinAck)))
        {
            var phrases = byCommand.Select(b => b.Key).Where(p => live.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
            if (phrases.Count == 0)
            {
                continue;
            }

            var binding = byCommand.First().Value;
            if (binding.CabinAck)
            {
                cabin.Add(new VoiceReferenceEntry(phrases, "Boarding — the FO requests it, the purser confirms.",
                    "Boarding underway.", VoiceSpeaker.Purser));
            }
            else
            {
                ground.Add(new VoiceReferenceEntry(phrases, CommandWhat(binding.Command), binding.SuccessPhrase));
            }
        }

        yield return new VoiceReferenceGroup("gsx", "Ground services", VoiceReferenceTab.Ground,
            VoiceSpeaker.FirstOfficer, feature.Enabled, reason, ground);
        yield return new VoiceReferenceGroup("gsxCabin", "Boarding", VoiceReferenceTab.Cabin,
            VoiceSpeaker.Purser, feature.Enabled, reason, cabin);
    }

    private static string CommandWhat(string command) => command switch
    {
        "gsx.forceNextService" => "The INT/RAD smart button — call the next service now.",
        "gsx.requestBoarding" => "Request boarding.",
        "gsx.requestRefuel" => "Order the fuel truck.",
        "gsx.confirmFuel" => "Confirm the block fuel, then order the truck (or a top-up).",
        "gsx.requestCatering" => "Request catering.",
        "gsx.requestPushback" => "Request pushback (beacon sequence permitting).",
        "gsx.requestDeice" => "Request de-icing.",
        _ => command,
    };

    /// <summary>The two hails, each on its crew's tab, with the live reply text and the ACP
    /// channel rule from the options.</summary>
    private IEnumerable<VoiceReferenceGroup> HailGroups(IVoiceFeature feature, List<string> live)
    {
        var groundHails = live.Where(p => p.EndsWith(" to ground", StringComparison.OrdinalIgnoreCase)).ToList();
        var cabinHails = live.Where(p => !groundHails.Contains(p)).ToList();
        var gating = _speech.CurrentValue.AcpTransmitGating;
        var ground = _groundCrew.CurrentValue;
        var cabin = _cabin.CurrentValue;

        if (groundHails.Count > 0)
        {
            var enabled = feature.Enabled && ground.Enabled;
            yield return new VoiceReferenceGroup("hailGround", "Hail the ground crew", VoiceReferenceTab.Ground,
                VoiceSpeaker.GroundCrew, enabled,
                enabled ? null : feature.Enabled ? $"groundCrew.enabled is off — {SettingsFo} → Ground Crew" : "gsx.voiceControlEnabled is off",
                [
                    new VoiceReferenceEntry(groundHails,
                        gating
                            ? "Select INT on the audio panel, hail, then say any ground-services phrase in the window."
                            : "Hail, then say any ground-services phrase in the window.",
                        ground.HailReplyText, VoiceSpeaker.GroundCrew, gating ? [VoiceReferenceDescribers.Int, VoiceReferenceDescribers.Dialogue] : [VoiceReferenceDescribers.Dialogue]),
                    new VoiceReferenceEntry([.. GsxVoicePhrases.CancelPhrases], "End the hail without a request.", ground.StandingByText, VoiceSpeaker.GroundCrew),
                ]);
        }

        if (cabinHails.Count > 0)
        {
            var enabled = feature.Enabled && cabin.Enabled;
            yield return new VoiceReferenceGroup("hailCabin", "Hail the cabin crew", VoiceReferenceTab.Cabin,
                VoiceSpeaker.Purser, enabled,
                enabled ? null : feature.Enabled ? $"cabin.enabled is off — {SettingsFo} → Cabin Crew" : "gsx.voiceControlEnabled is off",
                [
                    new VoiceReferenceEntry(cabinHails,
                        gating
                            ? "Select CAB on the audio panel, hail, then ask for boarding in the window."
                            : "Hail, then ask for boarding in the window.",
                        cabin.HailReplyText, VoiceSpeaker.Purser, gating ? [VoiceReferenceDescribers.Cab, VoiceReferenceDescribers.Dialogue] : [VoiceReferenceDescribers.Dialogue]),
                ]);
        }
    }

    private static VoiceReferenceGroup ConfiguredGroup(Commands.ConfiguredVoiceCommands configured, List<string> live)
    {
        var entries = configured.Commands
            .Select(c => (Phrases: c.Phrases.Where(p => live.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList(), Say: c.Say))
            .Where(c => c.Phrases.Count > 0)
            .Select(c => new VoiceReferenceEntry(c.Phrases, "", c.Say))
            .ToList();
        return new VoiceReferenceGroup("commandsJson", "From commands.json", VoiceReferenceTab.FirstOfficer,
            VoiceSpeaker.FirstOfficer, configured.Enabled, null, entries);
    }

    private static VoiceReferenceGroup AtcGroup(SayIntentions.SayIntentionsService sayIntentions, List<string> live)
    {
        var entries = sayIntentions.Requests
            .Select(r => (Phrases: r.Phrases.Where(p => live.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList(), Request: r))
            .Where(r => r.Phrases.Count > 0)
            .Select(r => new VoiceReferenceEntry(r.Phrases,
                r.Request.Station.Length > 0 ? $"To {r.Request.Station}." : "",
                r.Request.Icao.Length > 0 ? r.Request.Icao : null, VoiceSpeaker.Atc))
            .ToList();
        return new VoiceReferenceGroup("atc", "ATC requests (SayIntentions)", VoiceReferenceTab.Atc, VoiceSpeaker.Atc,
            sayIntentions.Enabled, sayIntentions.Enabled ? null : $"SayIntentions is off — {SettingsFo} → SayIntentions", entries);
    }

    private VoiceReferenceGroup ChecklistStarts()
    {
        var entries = new List<VoiceReferenceEntry>();
        foreach (var definition in _checklists.Definitions(ChecklistService.DefaultSetName))
        {
            // The router adds a "request …" twin for every start phrase; one row says so.
            var starts = UtteranceRouter.StartPhrases(definition)
                .Where(p => !p.StartsWith("request ", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.ToLowerInvariant())
                .ToList();
            if (starts.Count > 0)
            {
                entries.Add(new VoiceReferenceEntry(starts, $"Starts the {definition.Checklist} checklist ('request …' works too)."));
            }
        }

        return new VoiceReferenceGroup("checklists", "Checklists", VoiceReferenceTab.Checklists,
            VoiceSpeaker.FirstOfficer, true, null, entries);
    }

    private VoiceReferenceGroup Drills()
    {
        var entries = _failures.Drills
            .Where(d => d.Triggers.Count > 0)
            .Select(d => new VoiceReferenceEntry(d.Triggers, $"Rehearse the {d.Title} memory items."))
            .ToList();
        return new VoiceReferenceGroup("drills", "Memory drills", VoiceReferenceTab.Checklists,
            VoiceSpeaker.FirstOfficer, true, null, entries);
    }

    /// <summary>Free-form questions (issue #149): not grammar — example questions and the
    /// rule that makes an utterance one. Listed dimmed with the reason while off.</summary>
    private VoiceReferenceGroup Questions()
    {
        var options = _speech.CurrentValue.FoQuestions;
        var enabled = _questions is { Enabled: true };
        var leadIns = options.LeadInList().Select(l => l.ToLowerInvariant()).ToList();
        var reason = _questions is null ? "not available in this build"
            : !options.Enabled ? $"speech.foQuestions.enabled is off — {SettingsFo} → Briefings & LLM → Ask the First Officer"
            : null;
        var minimum = options.MinimumWords.ToString(System.Globalization.CultureInfo.InvariantCulture);
        IReadOnlyList<VoiceReferenceEntry> entries =
        [
            new(["what is our fuel on board"], "Fuel on board and the planned figures.", "Fuel on board is six point two tonnes, against a planned landing fuel of three point one."),
            new(["how long to top of descent"], "Minutes to the 3:1 top-of-descent estimate.", "About twenty five minutes to top of descent."),
            new(["tell me the destination weather"], "The destination METAR as the hero card shows it.", "Schiphol: wind two seven zero at twelve knots, visibility ten kilometres or more, ceiling two thousand five hundred feet."),
            new(["what time do we land"], "The ground-speed ETA.", "Estimated arrival is fourteen zero five zulu."),
            new(["are we above the minimum takeoff fuel"], "Anything the fact sheet can compare.", "Yes — six point two tonnes on board against a minimum of five point eight."),
            new(leadIns.Count > 0 ? leadIns : ["question"],
                $"Any question starting with one of these lead-ins and at least {minimum} words long. Answered only from live facts; the FO says \"{ProsimCompanion.Speech.Questions.FoQuestionCore.DontHaveThat}\" when the facts do not cover it. Needs the LAN speech server.",
                null, Badges: [VoiceReferenceDescribers.Value], ValueHint: "‹your question›"),
        ];
        return new VoiceReferenceGroup("foQuestions", "Ask the First Officer", VoiceReferenceTab.FirstOfficer,
            VoiceSpeaker.FirstOfficer, enabled, reason, entries);
    }

    /// <summary>The router's global checklist commands — in every grammar, no feature owns them.</summary>
    private static readonly IReadOnlyList<VoiceReferenceEntry> AlwaysAvailable =
    [
        new([VoiceCommands.Skip, VoiceCommands.SkipItem], "Skip the current checklist item."),
        new([VoiceCommands.SayAgain, VoiceCommands.Repeat], "Repeat the current item."),
        new([VoiceCommands.Hold, VoiceCommands.Standby], "Hold the checklist."),
        new([VoiceCommands.Resume, VoiceCommands.Continue], "Resume a held checklist."),
        new([VoiceCommands.Cancel], "Cancel the running checklist."),
        new([VoiceCommands.Restart], "Restart the active checklist."),
        new(["negative", "disregard", "cancel", "belay that"], "Cancel a pending FCU, radio or MCDU action."),
    ];

    // ---- helpers ----------------------------------------------------------------------------

    private static List<string> LivePhrases(IVoiceFeature feature)
        => [.. feature.Phrases.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>A describer key matches a live phrase when equal, or when the live phrase is
    /// the key plus a value ("landing stats for egll", "change arrival runway zero four left").</summary>
    internal static bool Matches(string key, string live)
        => string.Equals(key, live, StringComparison.OrdinalIgnoreCase)
            || live.StartsWith(key + " ", StringComparison.OrdinalIgnoreCase);

    /// <summary>The FO push-to-talk binding as the drawer prints it.</summary>
    internal static string PttBindingText(SpeechOptions options)
    {
        var binding = options.PttBinding;
        if (binding.IsSet)
        {
            return binding.Kind.Equals("keyboard", StringComparison.OrdinalIgnoreCase)
                ? $"key {binding.Key}"
                : $"{(binding.JoystickDeviceName.Length > 0 ? binding.JoystickDeviceName : $"joystick {binding.JoystickDevice}")} · button {binding.Button}";
        }

        if (options.PttKey.Length > 0)
        {
            return $"key {options.PttKey}";
        }

        if (options.PttJoystickButton is { } button)
        {
            return $"{(options.PttJoystickDeviceName.Length > 0 ? options.PttJoystickDeviceName : $"joystick {options.PttJoystickDevice}")} · button {button}";
        }

        return "not bound — continuous listening";
    }
}
