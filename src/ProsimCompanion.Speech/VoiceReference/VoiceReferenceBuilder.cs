using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.VoiceReference;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.VoiceReference;

/// <summary>
/// Composes the Voice Reference (issue #136) from the LIVE grammar: every feature in the
/// <see cref="VoiceFeatureRoster"/> (its current <c>Enabled</c> and <c>Phrases</c>), the
/// checklist start phrases, the drill triggers, commands.json and atc-requests.json. The
/// wording comes from each feature's own <see cref="VoiceFeatureDeclaration"/> — as pack text
/// ids, never English (ADR-0014) — but a described phrase the feature no longer contributes is
/// dropped and a live phrase the rows do not know still appears (undescribed): the drawer can
/// be incomplete in words, never wrong about the grammar. This builder knows no feature type;
/// what used to be a describer dictionary and a four-type switch here is now thirty
/// declarations beside their features.
/// </summary>
public sealed class VoiceReferenceBuilder : IVoiceReference
{
    private const string Always = "voiceReference.always.";
    private const string Checklists = "voiceReference.group.checklists.";
    private const string DrillsGroup = "voiceReference.group.drills.";
    private const string FoQuestions = "voiceReference.group.foQuestions.";
    private const string CruiseQuery = "voiceReference.group.cabinCruiseQuery.";

    private readonly VoiceFeatureRoster _roster;
    private readonly IChecklistDefinitionSource _checklists;
    private readonly Abnormals.IDrillSource _failures;
    private readonly IOptionsMonitor<SpeechOptions> _speech;
    private readonly IOptionsMonitor<GroundCrewOptions> _groundCrew;
    private readonly IOptionsMonitor<CabinOptions> _cabin;
    private readonly ILogger<VoiceReferenceBuilder> _logger;
    private readonly IFreeFormQuestionHandler? _questions;

    public VoiceReferenceBuilder(
        VoiceFeatureRoster roster,
        IChecklistDefinitionSource checklists,
        Abnormals.IDrillSource failures,
        IOptionsMonitor<SpeechOptions> speech,
        IOptionsMonitor<GroundCrewOptions> groundCrew,
        IOptionsMonitor<CabinOptions> cabin,
        ILogger<VoiceReferenceBuilder> logger,
        IFreeFormQuestionHandler? questions = null)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(checklists);
        ArgumentNullException.ThrowIfNull(failures);
        ArgumentNullException.ThrowIfNull(speech);
        ArgumentNullException.ThrowIfNull(groundCrew);
        ArgumentNullException.ThrowIfNull(cabin);
        ArgumentNullException.ThrowIfNull(logger);

        _roster = roster;
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
        foreach (var feature in _roster.All)
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
        groups.Add(CruiseQueryReplies());

        // Tab order, then the described features in roster (dispatch) order.
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
        return feature.Declaration.Description switch
        {
            DescribedVoiceReference rows => [FromRows(feature, rows, live)],
            ComposedVoiceReference composed => composed.Compose(feature, live, new VoiceReferenceContext(
                _speech.CurrentValue, _groundCrew.CurrentValue, _cabin.CurrentValue)),
            _ => throw new UnreachableException("VoiceReferenceDescription is a closed hierarchy"),
        };
    }

    /// <summary>The declared rows against the live grammar: a row survives while at least one
    /// of its phrase names is still contributed; every live phrase no row names is listed
    /// undescribed; a switched-off feature is dimmed with its gate's reason.</summary>
    private static VoiceReferenceGroup FromRows(IVoiceFeature feature, DescribedVoiceReference description, List<string> live)
    {
        var entries = new List<VoiceReferenceEntry>();
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in description.Rows)
        {
            var present = new List<string>();
            foreach (var name in row.Phrases)
            {
                var matches = live.Where(p => Matches(name, p)).ToList();
                if (matches.Count == 0)
                {
                    continue;
                }

                present.Add(name);
                foreach (var match in matches)
                {
                    covered.Add(match);
                }
            }

            if (present.Count > 0)
            {
                entries.Add(new VoiceReferenceEntry(
                    present, new VoiceReferenceText(row.WhatTextId), row.Answer, row.Speaker ?? description.Speaker, row.Badges, row.ValueHint));
            }
        }

        // Live phrases nobody described: still listed, so the grammar is never hidden.
        foreach (var phrase in live.Where(p => !covered.Contains(p)))
        {
            entries.Add(new VoiceReferenceEntry([phrase], null, Speaker: description.Speaker));
        }

        return new VoiceReferenceGroup(
            description.Id, new VoiceReferenceText(description.TitleTextId), description.Tab, description.Speaker,
            feature.Enabled,
            feature.Enabled ? null : feature.Declaration.Gate.OffReason ?? new VoiceReferenceText(VoiceReferenceTextIds.SwitchedOff),
            entries);
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
                entries.Add(new VoiceReferenceEntry(starts, new(Checklists + "start", [definition.Checklist])));
            }
        }

        return new VoiceReferenceGroup("checklists", new(Checklists + "title"), VoiceReferenceTab.Checklists,
            VoiceSpeaker.FirstOfficer, true, null, entries);
    }

    private VoiceReferenceGroup Drills()
    {
        var entries = _failures.Drills
            .Where(d => d.Triggers.Count > 0)
            .Select(d => new VoiceReferenceEntry(d.Triggers, new(DrillsGroup + "rehearse", [d.Title])))
            .ToList();
        return new VoiceReferenceGroup("drills", new(DrillsGroup + "title"), VoiceReferenceTab.Checklists,
            VoiceSpeaker.FirstOfficer, true, null, entries);
    }

    /// <summary>Free-form questions (issue #149): not grammar — example questions and the
    /// rule that makes an utterance one. Listed dimmed with the reason while off.</summary>
    private VoiceReferenceGroup Questions()
    {
        var options = _speech.CurrentValue.FoQuestions;
        var enabled = _questions is { Enabled: true };
        var leadIns = options.LeadInList().Select(l => l.ToLowerInvariant()).ToList();
        var reason = _questions is null ? new VoiceReferenceText("voiceReference.off.notInBuild")
            : !options.Enabled ? new VoiceReferenceText("voiceReference.off.foQuestions", ["speech.foQuestions.enabled"])
            : null;
        var minimum = options.MinimumWords.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var entries = new List<VoiceReferenceEntry>
        {
            new(["what is our fuel on board"], new(FoQuestions + "fuel"), "Fuel on board is six point two tonnes, against a planned landing fuel of three point one."),
            new(["how long to top of descent"], new(FoQuestions + "tod"), "About twenty five minutes to top of descent."),
            new(["tell me the destination weather"], new(FoQuestions + "weather"), "Schiphol: wind two seven zero at twelve knots, visibility ten kilometres or more, ceiling two thousand five hundred feet."),
            new(["what time do we land"], new(FoQuestions + "eta"), "Estimated arrival is fourteen zero five zulu."),
            new(["are we above the minimum takeoff fuel"], new(FoQuestions + "compare"), "Yes — six point two tonnes on board against a minimum of five point eight."),
            new(leadIns.Count > 0 ? leadIns : ["question"],
                new(FoQuestions + "leadIn", [minimum, Speech.Questions.FoQuestionCore.DontHaveThat]),
                null, Badges: [VoiceBadges.Value], ValueHint: "‹your question›"),
        };
        if (options.SmallTalk)
        {
            // Issue #152: small talk — any sentence long enough, flight questions still strict.
            entries.Add(new(["who is better, chelsea or arsenal"], new(FoQuestions + "smallTalk.football"), "Captain, I fly for a living — but I'd never say Arsenal in this cockpit."));
            entries.Add(new(["tell me a fun fact"], new(FoQuestions + "smallTalk.funFact"), "Octopuses have three hearts, Captain. Two of them stop when it swims."));
            entries.Add(new(["any sentence of " + minimum + " words or more"], new(FoQuestions + "smallTalk.any"), null, Badges: [VoiceBadges.Value], ValueHint: "‹anything›"));
        }

        if (options.WhereAreWe)
        {
            // Issue #153: the place below, from the offline atlas, then a fact or two.
            entries.Add(new(["what are we flying over", "where are we"],
                new(options.WikipediaFacts ? FoQuestions + "whereAreWe.wikipedia" : FoQuestions + "whereAreWe.model"),
                "We're over the Alps in northern Italy, about 30 miles north of Turin. Nearest town is Aosta, 12 miles out on the left. Aosta still has its Roman walls and arch."));
            entries.Add(new(["what is that city on the left", "what country is this"], new(FoQuestions + "whereAreWe.side"), null));
        }

        return new VoiceReferenceGroup("foQuestions", new(FoQuestions + "title"), VoiceReferenceTab.FirstOfficer,
            VoiceSpeaker.FirstOfficer, enabled, reason, entries);
    }

    /// <summary>The purser's cruise query (2026-10-09): not a phrase the pilot starts — the
    /// purser calls — but the drawer shows what an answer in the window can be, on the Cabin
    /// tab, dimmed with the settings path while off.</summary>
    private VoiceReferenceGroup CruiseQueryReplies()
    {
        var cabin = _cabin.CurrentValue;
        var enabled = cabin.Enabled && cabin.CruiseQuery;
        var reason = enabled ? null
            : !cabin.Enabled ? new VoiceReferenceText(Crew.CrewHailVoiceReference.OffCabinCrew, ["cabin.enabled"])
            : new VoiceReferenceText("voiceReference.off.cruiseQuery", ["cabin.cruiseQuery"]);
        var window = cabin.CruiseQueryWindowSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new VoiceReferenceGroup("cabinCruiseQuery", new(CruiseQuery + "title"), VoiceReferenceTab.Cabin,
            VoiceSpeaker.Purser, enabled, reason,
            [
                new VoiceReferenceEntry(["about forty minutes", "on time", "ten minutes late"],
                    new(CruiseQuery + "eta", [cabin.CruiseQueryText, window]),
                    cabin.CruiseQueryEtaAckText, VoiceSpeaker.Purser, [VoiceBadges.Cab, VoiceBadges.Dialogue]),
                new VoiceReferenceEntry(["smooth", "light chop", "expect some bumps"],
                    new(CruiseQuery + "ride"), cabin.CruiseQueryRideAckText, VoiceSpeaker.Purser, [VoiceBadges.Dialogue]),
                new VoiceReferenceEntry(["nothing to report", "no change"],
                    new(CruiseQuery + "other"), cabin.CruiseQueryGenericAckText, VoiceSpeaker.Purser, [VoiceBadges.Dialogue]),
                new VoiceReferenceEntry(["(say nothing)"], new(CruiseQuery + "silence"), cabin.CruiseQueryNoReplyText, VoiceSpeaker.Purser),
            ]);
    }

    /// <summary>The router's global checklist commands — in every grammar, no feature owns them.</summary>
    private static readonly IReadOnlyList<VoiceReferenceEntry> AlwaysAvailable =
    [
        new([VoiceCommands.Skip, VoiceCommands.SkipItem], new(Always + "skip")),
        new([VoiceCommands.SayAgain, VoiceCommands.Repeat], new(Always + "sayAgain")),
        new([VoiceCommands.Hold, VoiceCommands.Standby], new(Always + "hold")),
        new([VoiceCommands.Resume, VoiceCommands.Continue], new(Always + "resume")),
        new([VoiceCommands.Cancel], new(Always + "cancel")),
        new([VoiceCommands.Restart], new(Always + "restart")),
        new(["negative", "disregard", "cancel", "belay that"], new(Always + "negative")),
    ];

    // ---- helpers ----------------------------------------------------------------------------

    private static List<string> LivePhrases(IVoiceFeature feature)
        => [.. feature.Phrases.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>A row's phrase name matches a live phrase when equal, or when the live phrase is
    /// the name plus a value ("landing stats for egll", "change arrival runway zero four left").</summary>
    internal static bool Matches(string name, string live)
        => string.Equals(name, live, StringComparison.OrdinalIgnoreCase)
            || live.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase);

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
