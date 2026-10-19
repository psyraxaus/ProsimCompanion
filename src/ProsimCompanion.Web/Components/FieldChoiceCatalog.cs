using System.Globalization;
using Microsoft.Extensions.Localization;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Web.Components;

/// <summary>
/// The static dropdown lists the settings pages offer, by the name a field description gives
/// in <see cref="OptionFieldAttribute.Choices"/> (candidate C3). Each list is built per call
/// from the circuit's localizer (ADR-0014: the language can change under an open page), with
/// the values the option property stores — camelCase enum names, GSX's own menu words, ICAO
/// units — and the labels the packs translate. Lists that depend on runtime data (the audio
/// devices) are named in <see cref="RuntimeNames"/> and handed to the field by its page.
/// </summary>
public static class FieldChoiceCatalog
{
    private static readonly Dictionary<string, Func<IStringLocalizer, IReadOnlyList<(string Value, string Label)>>> Lists =
        new(StringComparer.Ordinal)
        {
            // ---- Voice First Officer ----
            ["recognitionMode"] = T => [("pushToTalk", T["speechSettings.options.recognitionMode.pushToTalk"].Value), ("continuous", T["speechSettings.options.recognitionMode.continuous"].Value)],
            ["pilotSeat"] = T => [("left", T["speechSettings.options.seat.left"].Value), ("right", T["speechSettings.options.seat.right"].Value)],
            ["llmApi"] = T => [("openAi", T["speechSettings.options.llmApi.openAi"].Value), ("ollama", T["speechSettings.options.llmApi.ollama"].Value)],
            ["asrApi"] = T => [("fasterWhisper", T["speechSettings.options.asrApi.fasterWhisper"].Value), ("whisperCpp", T["speechSettings.options.asrApi.whisperCpp"].Value)],
            ["vadEngine"] = T => [("silero", T["speechSettings.options.vad.silero"].Value), ("rms", T["speechSettings.options.vad.rms"].Value)],
            ["procedureSource"] = T => [("auto", T["speechSettings.options.procedureSource.auto"].Value), ("manual", T["speechSettings.options.procedureSource.manual"].Value), ("flightJson", T["speechSettings.options.procedureSource.flightJson"].Value)],
            ["sterileNormalPolicy"] = T => [("allow", T["speechSettings.options.normalPolicy.allow"].Value), ("defer", T["speechSettings.options.normalPolicy.defer"].Value), ("suppress", T["speechSettings.options.normalPolicy.suppress"].Value)],
            ["apiKeySource"] = T => [("auto", T["speechSettings.options.apiKeySource.auto"].Value), ("manual", T["speechSettings.options.apiKeySource.manual"].Value)],
            // Phraseology names are the standards' own acronyms — the same in every language.
            ["phraseology"] = _ => [("icao", "ICAO"), ("faa", "FAA")],
            ["personaExperience"] = T => [("junior", T["speechSettings.options.experience.junior"].Value), ("standard", T["speechSettings.options.experience.standard"].Value), ("senior", T["speechSettings.options.experience.senior"].Value)],
            ["personaFormality"] = T => [("casual", T["speechSettings.options.formality.casual"].Value), ("standard", T["speechSettings.options.formality.standard"].Value), ("formal", T["speechSettings.options.formality.formal"].Value)],
            ["debriefVerbosity"] = T => [("full", T["speechSettings.options.verbosity.full"].Value), ("brief", T["speechSettings.options.verbosity.brief"].Value)],
            // SayIntentions PTT relay (2026-10-10). Values match SayIntentionsPttRelay.SourceRef; the
            // LVAR names are the ones the SI client lists under Controls → "Map PTT inside the sim".
            ["pttSwitch"] = T =>
            [
                ("none", T["speechSettings.options.pttSwitch.off"].Value),
                ("captainSidestick", T["speechSettings.options.pttSwitch.captainSidestick"].Value),
                ("foSidestick", T["speechSettings.options.pttSwitch.foSidestick"].Value),
                ("captainHandMic", T["speechSettings.options.pttSwitch.captainHandMic"].Value),
                ("foHandMic", T["speechSettings.options.pttSwitch.foHandMic"].Value),
                ("observerHandMic", T["speechSettings.options.pttSwitch.observerHandMic"].Value),
            ],
            ["siPttLvar"] = T =>
            [
                ("L:SIAI_CONTROL_PTT_COM", T["speechSettings.options.siPtt.com"].Value),
                ("L:SIAI_CONTROL_PTT_COM1", T["speechSettings.options.siPtt.com1"].Value),
                ("L:SIAI_CONTROL_PTT_COM2", T["speechSettings.options.siPtt.com2"].Value),
                ("L:SIAI_CONTROL_PTT_INTERCOM1", T["speechSettings.options.siPtt.intercom1"].Value),
                ("L:SIAI_CONTROL_PTT_INTERCOM2", T["speechSettings.options.siPtt.intercom2"].Value),
                ("L:SIAI_CONTROL_PTT_INTERCOM3", T["speechSettings.options.siPtt.intercom3"].Value),
                ("L:SIAI_CONTROL_PTT_GROUP", T["speechSettings.options.siPtt.group"].Value),
            ],
            ["elevenLabsModel"] = T => [("eleven_flash_v2_5", T["speechSettings.options.elevenLabsModel.flash"].Value), ("eleven_v3", T["speechSettings.options.elevenLabsModel.v3"].Value)],
            ["elevenLabsFormat"] = T => [("mp3_44100_128", T["speechSettings.options.elevenLabsFormat.mp3"].Value), ("pcm_24000", T["speechSettings.options.elevenLabsFormat.pcm"].Value)],
            // The flap handle steps, labelled the way the placard table names them (UP, 1, 1+F, 2, 3, FULL).
            ["flapLever"] = _ => [.. Enumerable.Range(0, 6).Select(handle => (handle.ToString(CultureInfo.InvariantCulture), SopOptions.FlapHandleLabel(handle)))],

            // ---- Ground Services ----
            ["refuelMethod"] = T => [("fixedRate", T["gsxSettings.refuel.methodOptions.fixed"].Value), ("dynamicRate", T["gsxSettings.refuel.methodOptions.dynamic"].Value)],
            ["refuelCall"] = T => [("automatic", T["gsxSettings.refuel.callOptions.automatic"].Value), ("onFuelConfirmed", T["gsxSettings.refuel.callOptions.onFuelConfirmed"].Value)],
            // GSX's own menu wording for the fluid and the concentration — shown as GSX shows it.
            ["deIceFluid"] = _ => [("Type I", "Type I"), ("Type II", "Type II"), ("Type IV", "Type IV")],
            ["deIceConcentration"] = _ => [("75", "75 / 25"), ("100", "100")],
            ["deiceAutoRequest"] = T =>
            [
                (GsxDeiceOptions.AutoRequestOff, T["gsxSettings.deice.autoRequestOptions.off"].Value),
                (GsxDeiceOptions.AutoRequestAsk, T["gsxSettings.deice.autoRequestOptions.ask"].Value),
                (GsxDeiceOptions.AutoRequestAuto, T["gsxSettings.deice.autoRequestOptions.auto"].Value),
            ],
            ["deiceAskWhen"] = T =>
            [
                (GsxDeiceOptions.AskWhenStepIsNext, T["gsxSettings.deice.askWhenOptions.stepIsNext"].Value),
                (GsxDeiceOptions.AskWhenAfterBoardingComplete, T["gsxSettings.deice.askWhenOptions.afterBoarding"].Value),
            ],
            ["crewAnswer"] = T =>
            [
                ("Nobody", T["gsxSettings.questions.crewOptions.nobody"].Value), ("Crew", T["gsxSettings.questions.crewOptions.crew"].Value),
                ("Pilots", T["gsxSettings.questions.crewOptions.pilots"].Value), ("Both", T["gsxSettings.questions.crewOptions.both"].Value),
            ],
            ["groundPrepActivation"] = T =>
            [
                ("auto", T["gsxSettings.automation.groundPrepOptions.auto"].Value), ("voice", T["gsxSettings.automation.groundPrepOptions.voice"].Value),
                (GsxOptions.GroundPrepActivationVoiceThenAuto, T["gsxSettings.automation.groundPrepOptions.voiceThenAuto"].Value),
            ],
            ["removeStairs"] = T => [("never", T["gsxSettings.options.never"].Value), ("always", T["gsxSettings.options.always"].Value), ("onlyJetway", T["gsxSettings.options.onlyJetway"].Value)],
            ["pcaMode"] = T => [("never", T["gsxSettings.options.never"].Value), ("always", T["gsxSettings.options.always"].Value), ("onlyJetway", T["gsxSettings.options.onlyJetway"].Value)],
            ["tugAnswer"] = T => [("ignore", T["gsxSettings.questions.tugOptions.ignore"].Value), ("no", T["gsxSettings.questions.tugOptions.no"].Value), ("yes", T["gsxSettings.questions.tugOptions.yes"].Value)],
            ["pushbackMode"] = T =>
            [
                ("auto", T["gsxSettings.pushbackDirection.options.auto"].Value), ("ask", T["gsxSettings.pushbackDirection.options.ask"].Value),
                ("tailLeft", T["gsxSettings.pushbackDirection.options.tailLeft"].Value), ("tailRight", T["gsxSettings.pushbackDirection.options.tailRight"].Value),
                ("straight", T["gsxSettings.pushbackDirection.options.straight"].Value),
            ],
            ["tugPushback"] = T =>
            [
                ("never", T["gsxSettings.options.never"].Value),
                ("afterDepartureServices", T["gsxSettings.questions.tugPushbackOptions.afterDepartureServices"].Value),
                ("afterFinalLoadsheet", T["gsxSettings.questions.tugPushbackOptions.afterFinalLoadsheet"].Value),
            ],

            // ---- Audio Control ----
            ["audioBackend"] = T => [("coreAudio", T["audioSettings.general.backend.coreAudio"].Value), ("voiceMeeter", T["audioSettings.general.backend.voiceMeeter"].Value)],
            ["coreAudioAcp"] = T => [("captain", T["audioSettings.coreAudio.acp.captain"].Value), ("firstOfficer", T["audioSettings.coreAudio.acp.firstOfficer"].Value), ("observer", T["audioSettings.coreAudio.acp.observer"].Value)],
            ["deviceFlow"] = T => [("render", T["audioSettings.coreAudio.deviceFlow.render"].Value), ("capture", T["audioSettings.coreAudio.deviceFlow.capture"].Value), ("all", T["audioSettings.coreAudio.deviceFlow.all"].Value)],
            ["deviceState"] = T =>
            [
                ("active", T["audioSettings.coreAudio.deviceState.active"].Value), ("disabled", T["audioSettings.coreAudio.deviceState.disabled"].Value),
                ("notPresent", T["audioSettings.coreAudio.deviceState.notPresent"].Value), ("unplugged", T["audioSettings.coreAudio.deviceState.unplugged"].Value),
                ("all", T["audioSettings.coreAudio.deviceState.all"].Value),
            ],

            // ---- Display & Flight Data ----
            ["unitSource"] = T => [("app", T["appSettings.display.unitSource.app"].Value), ("aircraft", T["appSettings.display.unitSource.aircraft"].Value)],
            // Units, not prose — the same in every language.
            ["unit"] = _ => [("kg", "kg"), ("lb", "lb")],

            // ---- Notifications: service names, not prose ----
            ["notificationKind"] = _ => [("ntfy", "ntfy"), ("discord", "Discord"), ("webhook", "Webhook (JSON POST)")],
        };

    /// <summary>The lists only a page can build (detected audio devices); a description that
    /// names one of these gets its rows from the page's <c>Choices</c> parameter.</summary>
    public static IReadOnlySet<string> RuntimeNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "inputDevices",
        "outputDevices",
    };

    /// <summary>Every static list name, for the completeness test.</summary>
    public static IReadOnlyCollection<string> Names => Lists.Keys.ToArray();

    /// <summary>True when <paramref name="name"/> is a static list or a known runtime list.</summary>
    public static bool IsKnown(string name) => Lists.ContainsKey(name) || RuntimeNames.Contains(name);

    /// <summary>The rows of a static list in the circuit's language; null for a runtime list or
    /// an unknown name (the renderer then insists on the page's <c>Choices</c>).</summary>
    public static IReadOnlyList<(string Value, string Label)>? Resolve(string name, IStringLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(localizer);
        return Lists.TryGetValue(name, out var build) ? build(localizer) : null;
    }
}
