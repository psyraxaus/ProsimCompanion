using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Speech;
using ProsimCompanion.Core.State;
using ProsimCompanion.Web.Components;

namespace ProsimCompanion.Web.Settings;

/// <summary>
/// The logic of the Voice First Officer settings page that is genuinely page-level — rules
/// between two properties, a one-way migration, the lists only this page can build — kept
/// as plain C# so xunit reaches it (candidate C3, 2026-10-19). Per-property trims and case
/// folds are NOT here: they are declared on each property's <see cref="OptionFieldAttribute"/>
/// and applied by the draft on save. The page itself is left with ordered grouping and the
/// capture / listing calls that need its services.
/// </summary>
public static class SpeechPageRules
{
    /// <summary>What the page does to its drafts after their declared normalisation and before
    /// the diff: the FO voice fills its provider's empty fallback field, the legacy flat PTT
    /// fields are blanked, and the approach gates keep unique names.</summary>
    public static void BeforeSave(SpeechOptions speech, SopOptions sop)
    {
        ArgumentNullException.ThrowIfNull(speech);
        ArgumentNullException.ThrowIfNull(sop);
        FillProviderFallbackFromFoVoice(speech);
        BlankLegacyPttFields(speech);
        // The monitor latches fired gates by name — keep names unique and in step with heights.
        SopOptions.SyncApproachGateNames(sop.ApproachGates);
    }

    /// <summary>The FO voice names a provider: that provider must read "configured" (ElevenLabs
    /// and Google need a voice for that), so an empty fallback field takes the same id.</summary>
    public static void FillProviderFallbackFromFoVoice(SpeechOptions speech)
    {
        ArgumentNullException.ThrowIfNull(speech);
        var fo = VoiceRef.Parse(speech.FoVoice);
        if (!fo.HasProvider || fo.IsEmpty)
        {
            return;
        }

        switch (fo.Provider)
        {
            case "kokoro" when string.IsNullOrWhiteSpace(speech.KokoroVoice):
                speech.KokoroVoice = fo.Id;
                break;
            case "elevenlabs" when string.IsNullOrWhiteSpace(speech.ElevenLabsVoiceId):
                speech.ElevenLabsVoiceId = fo.Id;
                break;
            case "google" when string.IsNullOrWhiteSpace(speech.GoogleVoice):
                speech.GoogleVoice = fo.Id;
                break;
            case "winrt" when string.IsNullOrWhiteSpace(speech.WinRtVoice):
                speech.WinRtVoice = fo.Id;
                break;
            case "sapi" when string.IsNullOrWhiteSpace(speech.SapiVoice):
                speech.SapiVoice = fo.Id;
                break;
        }
    }

    /// <summary>Legacy flat PTT fields blanked: the typed bindings are authoritative from the
    /// first save on (they were migrated into the draft at load). The diff only writes these
    /// when the file still carries values.</summary>
    public static void BlankLegacyPttFields(SpeechOptions speech)
    {
        ArgumentNullException.ThrowIfNull(speech);
        speech.PttKey = "";
        speech.AtcMuteKey = "";
        speech.PttJoystickDevice = null;
        speech.PttJoystickButton = null;
        speech.PttJoystickDeviceName = "";
        speech.AtcMuteJoystickDevice = null;
        speech.AtcMuteJoystickButton = null;
        speech.AtcMuteJoystickDeviceName = "";
    }

    /// <summary>Picking a SayIntentions PTT switch also sets the ATC mute to the same switch
    /// while that is still Off — the FO must not listen while the pilot talks to ATC. Both
    /// drafts save in the one Save.</summary>
    public static void CoupleAtcMuteToSiPtt(SayIntentionsOptions sayIntentions, SpeechOptions speech)
    {
        ArgumentNullException.ThrowIfNull(sayIntentions);
        ArgumentNullException.ThrowIfNull(speech);
        if (sayIntentions.PttSource != "none" && speech.AtcMuteSource == "none")
        {
            speech.AtcMuteSource = sayIntentions.PttSource;
        }
    }

    /// <summary>One-way migration: pre-binding configs carried flat key/joystick fields; the
    /// first page load synthesizes bindings from them (joystick wins when both were set —
    /// hardware buttons were the deliberate choice). The synthesized binding dirties the
    /// draft, so the bar prompts for the save that persists it; the legacy keys are blanked
    /// at save time in <see cref="BlankLegacyPttFields"/>.</summary>
    public static void MigrateLegacyBindings(SpeechOptions draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!draft.PttBinding.IsSet)
        {
            if (draft.PttJoystickButton is not null)
            {
                draft.PttBinding = new PttBindingOptions
                {
                    Kind = "joystickButton",
                    JoystickDevice = draft.PttJoystickDevice,
                    JoystickDeviceName = draft.PttJoystickDeviceName,
                    Button = draft.PttJoystickButton,
                };
            }
            else if (!string.IsNullOrWhiteSpace(draft.PttKey))
            {
                draft.PttBinding = new PttBindingOptions { Kind = "keyboard", Key = draft.PttKey.Trim() };
            }
        }

        if (!draft.AtcMuteBinding.IsSet)
        {
            if (draft.AtcMuteJoystickButton is not null)
            {
                draft.AtcMuteBinding = new PttBindingOptions
                {
                    Kind = "joystickButton",
                    JoystickDevice = draft.AtcMuteJoystickDevice,
                    JoystickDeviceName = draft.AtcMuteJoystickDeviceName,
                    Button = draft.AtcMuteJoystickButton,
                };
            }
            else if (!string.IsNullOrWhiteSpace(draft.AtcMuteKey))
            {
                draft.AtcMuteBinding = new PttBindingOptions { Kind = "keyboard", Key = draft.AtcMuteKey.Trim() };
            }
        }
    }

    /// <summary>True for a binding on a joystick button.</summary>
    public static bool IsJoystick(PttBindingOptions binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return binding.IsSet && binding.Kind.Equals("joystickButton", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The rows of an audio-device dropdown: the empty-value default first, then every
    /// detected device; a saved device that is not currently detected (unplugged, renamed)
    /// stays selectable so opening the page never silently rewrites the setting.</summary>
    /// <param name="defaultLabel">The row for the empty value ("System default").</param>
    /// <param name="devices">The detected device names.</param>
    /// <param name="current">The saved value.</param>
    /// <param name="notDetected">Builds the label of a saved device that is not in the list.</param>
    public static IReadOnlyList<(string Value, string Label)> DeviceChoices(
        string defaultLabel, IReadOnlyList<string> devices, string current, Func<string, string> notDetected)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(notDetected);
        var options = new List<(string, string)> { ("", defaultLabel) };
        foreach (var name in devices)
        {
            options.Add((name, name));
        }

        if (!string.IsNullOrWhiteSpace(current)
            && !devices.Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            options.Add((current, notDetected(current)));
        }

        return options;
    }

    /// <summary>The voice picker's groups: provider order = chain order; regions with "English"
    /// first (the only ones that make a usable First Officer), then alphabetical; rows as the
    /// provider listed them. A provider with no list (not configured, not answered) has no group.</summary>
    public static IReadOnlyList<VoiceGroup> BuildVoiceGroups(
        IEnumerable<string> providers, IReadOnlyDictionary<string, IReadOnlyList<VoiceEntry>> lists)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(lists);
        var groups = new List<VoiceGroup>();
        foreach (var provider in providers)
        {
            if (!lists.TryGetValue(provider, out var voices) || voices.Count == 0)
            {
                continue;
            }

            var byRegion = voices
                .GroupBy(v => v.Region, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key.Contains("English", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var region in byRegion)
            {
                groups.Add(new VoiceGroup($"{VoiceRef.DisplayName(provider)} · {region.Key}", region.ToArray()));
            }
        }

        return groups;
    }
}
