using System.Collections;
using System.Reflection;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Localization;
using ProsimCompanion.Core.Tests.Configuration;
using ProsimCompanion.Core.Tests.Localization;
using ProsimCompanion.Web.Components;
using Xunit;

namespace ProsimCompanion.Core.Tests.Web;

/// <summary>
/// The fence that replaces "did I remember the control?" (candidate C3, owner rule 2026-08-14):
/// every editable option property on every registered section — nested blocks and list items
/// included — carries an <see cref="OptionFieldAttribute"/>, or is named below with the reason
/// it has no field. Every description must then resolve: its label (and any explicit hint or
/// fold) in the English pack, its choice list in the catalog or the page, its Browse… folder
/// list, and its disabled-when rule against a fresh instance. A new option without a
/// description fails here, not on a pilot's tablet.
/// </summary>
public sealed class OptionFieldCompletenessTests
{
    /// <summary>Properties deliberately without a field, as <c>Type.Property</c> or <c>Type.*</c>.
    /// A new entry is a visible review decision — say why.</summary>
    private static readonly IReadOnlyDictionary<string, string> NotOnAPage = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Edited on the no-dirty-bar pages with immediate writes (Logs, Appearance), not through a draft.
        ["LoggingOptions.*"] = "Logs page, immediate writes",
        ["DiagnosticsOptions.*"] = "Logs page, immediate writes",
        ["TelemetryApiOptions.*"] = "Logs page, immediate writes",
        ["WebUiOptions.ShowAdvancedSettings"] = "the settings rail's own switch (AdvancedSettingsStore)",
        ["WebUiOptions.ShowNextServiceInHeader"] = "Appearance page, applies at once",
        ["WebUiOptions.Theme"] = "Appearance page theme grid, applies at once",
        ["WebUiOptions.Language"] = "Appearance page language picker, applies at once",
        ["WebUiOptions.AccessToken"] = "generated secret, never edited",
        // Written by other surfaces or by the app itself.
        ["FlightStatusOptions.TileOrder"] = "dragged on the Flight Status page itself",
        ["GsxOptions.AutoPca"] = "legacy bool behind pcaMode (GsxPageRules.EffectivePcaMode)",
        ["GsxOptions.CateringDoorLearned"] = "written by GsxDoorService from GSX's service-door toggle; the Catering door hint shows it",
        ["SpeechOptions.PttKey"] = "legacy flat PTT field, migrated into PttBinding",
        ["SpeechOptions.PttJoystickDevice"] = "legacy flat PTT field",
        ["SpeechOptions.PttJoystickButton"] = "legacy flat PTT field",
        ["SpeechOptions.PttJoystickDeviceName"] = "legacy flat PTT field",
        ["SpeechOptions.AtcMuteKey"] = "legacy flat PTT field",
        ["SpeechOptions.AtcMuteJoystickDevice"] = "legacy flat PTT field",
        ["SpeechOptions.AtcMuteJoystickButton"] = "legacy flat PTT field",
        ["SpeechOptions.AtcMuteJoystickDeviceName"] = "legacy flat PTT field",
        ["PttBindingOptions.*"] = "the Set / Clear capture row on the Listening card",
        ["WakeOnLanOptions.*"] = "legacy briefing.llmWakeOnLan, migrated into wakeOnLan.targets",
        ["NotificationEventSwitches.*"] = "the ten tick boxes of a target card, one row",
        // Consumer-only tuning that never had a control (predates the 2026-08-14 rule); each is a
        // candidate for a field, not a fence to keep.
        ["SpeechOptions.AsrTimeoutMs"] = "no control yet",
        ["SpeechOptions.VadMinSpeechMs"] = "no control yet",
        ["SpeechOptions.VadPreRollMs"] = "no control yet",
        ["SpeechOptions.VadMaxUtteranceMs"] = "no control yet",
        ["SpeechOptions.NoSpeechCeiling"] = "no control yet",
        ["SpeechOptions.ConfidenceFloor"] = "no control yet",
        ["SpeechOptions.ListeningTone"] = "no control yet",
        ["HumanizeOptions.GapJitter"] = "no control yet",
        ["HumanizeOptions.HoldJitter"] = "no control yet",
        ["HumanizeOptions.ThinkPauseChance"] = "no control yet",
        ["HumanizeOptions.ThinkPauseMinMs"] = "no control yet",
        ["HumanizeOptions.ThinkPauseMaxMs"] = "no control yet",
        ["GsxOptions.VoiceControlEnabled"] = "no control yet",
        ["GsxOptions.AnchorDepartureGate"] = "no control yet",
        ["FlightDataOptions.SimbriefFetchAttempts"] = "no control yet",
        ["ProsimOptions.MomentaryPressHoldMs"] = "no control yet",
        ["ProsimOptions.MomentaryPressGapMs"] = "no control yet",
        ["ProsimOptions.SdkStallSeconds"] = "no control yet",
        ["ProsimOptions.PushSilenceWarnSeconds"] = "no control yet",
        ["CommandApiOptions.RequireTokenOnLoopback"] = "no control yet",
        ["SopOptions.CalloutPollIntervalMs"] = "no control yet",
        ["BriefingFmsDatarefs.*"] = "no control yet (dataref names vary per ProSim build)",
        ["CalloutSetting.*"] = "SOP callout wording, no card yet",
        ["SpeedCalloutSetting.*"] = "SOP callout wording, no card yet",
        ["RadioCalloutSetting.*"] = "SOP callout wording, no card yet",
        ["AltitudeCallout.*"] = "SOP altitude callouts, no card yet",
        ["OneThousandToGoSetting.*"] = "SOP callout wording, no card yet",
        ["FlowCheckSetting.*"] = "twelve flow-monitor blocks share this type; only GearStillDown.Enabled has a row, kept on the page",
        ["FlowMonitorOptions.Enabled"] = "no control yet",
        ["FlowMonitorOptions.RateLimitSeconds"] = "no control yet",
        ["FlowMonitorOptions.FlapsCleanAboveAglFt"] = "no control yet",
        ["SopWeatherOptions.*"] = "weather advisories, no card yet",
        ["ApproachGate.Name"] = "derived from the height (SopOptions.SyncApproachGateNames)",
        // List-row types edited by their own table rows, not field components.
        ["DepartureServiceStep.*"] = "the departure order table",
        ["FlapPlacard.*"] = "the flap placard table",
        ["AudioAppMapping.*"] = "the CoreAudio mapping table",
        ["VoiceMeeterTargetMapping.*"] = "the VoiceMeeter mapping table",
        ["MixerMapping.*"] = "the remote mixer mapping table",
        // Cockpit switches (2026-10-24): the dataref is picked from the catalogue dropdown, look
        // and press mode are the grid line's own dropdowns, the three colours are swatch inputs —
        // all custom controls on the Cockpit page's grid lines.
        ["CockpitSwitchRow.DataRef"] = "the catalogue picker on the Cockpit Switches page",
        ["CockpitSwitchRow.Look"] = "a dropdown on the Cockpit Switches page's grid line",
        ["CockpitSwitchRow.PressMode"] = "a dropdown on the Cockpit Switches page's grid line",
        ["CockpitSwitchRow.UpperColor"] = "a colour swatch on the Cockpit Switches page",
        ["CockpitSwitchRow.LowerColor"] = "a colour swatch on the Cockpit Switches page",
        ["CockpitSwitchRow.BezelColor"] = "a colour swatch on the Cockpit Switches page",
        ["SkalarkiOptions.EnabledDevices"] = "the board tick boxes on the Skalarki settings page",
        ["SkalarkiOptions.DeviceLabels"] = "the per-board label fields on the Skalarki settings page",
        ["MixerAgentOptions.PanelStrips"] = "the per-agent status-panel tick boxes (#178)",
        ["MixerAgentOptions.PanelBuses"] = "the per-agent status-panel tick boxes (#178)",
        ["AircraftProfile.*"] = "the Aircraft Profiles page's own editor",
    };

    private static LanguagePack English { get; } = LanguagePack.Combine(
        "en",
        Directory.EnumerateFiles(Path.Combine(TestLocalizer.ShippedPackRoot(), "en"), "*.json").OrderBy(f => f, StringComparer.Ordinal).Select(f => LanguagePack.Load(f, "en")).ToList(),
        out _);

    /// <summary>One editable property as the walk found it: on a fresh instance chain so the
    /// disabled-when rule can be evaluated.</summary>
    private sealed record Found(PropertyInfo Property, IReadOnlyList<object> OwnerChain)
    {
        public string Name => $"{Property.DeclaringType?.Name}.{Property.Name}";
    }

    [Fact]
    public void EveryEditableOptionProperty_HasADescription_OrIsNamedAsNotOnAPage()
    {
        var missing = new List<string>();
        var described = new HashSet<string>(StringComparer.Ordinal);
        foreach (var found in Walk())
        {
            if (OptionFieldCatalog.Describe(found.Property) is not null)
            {
                described.Add(found.Name);
            }
            else if (!IsExempt(found.Name))
            {
                missing.Add(found.Name);
            }
        }

        var stale = NotOnAPage.Keys
            .Where(entry => !entry.EndsWith(".*", StringComparison.Ordinal) && described.Contains(entry))
            .ToList();

        Assert.True(missing.Count == 0,
            "Every option a page edits is declared once on its property with [OptionField]; these have neither a description nor a reason in NotOnAPage:\n"
            + string.Join('\n', missing.Distinct().OrderBy(n => n, StringComparer.Ordinal)));
        Assert.True(stale.Count == 0, "NotOnAPage names properties that now have a description — drop them:\n" + string.Join('\n', stale));
    }

    [Fact]
    public void EveryDescription_ResolvesItsTexts_InTheEnglishPack()
    {
        var problems = new List<string>();
        foreach (var found in Walk())
        {
            if (OptionFieldCatalog.Describe(found.Property) is not { } description || description.Kind == FieldKind.RangeUpper)
            {
                continue;
            }

            var ids = OptionFieldTexts.Resolve(description, English.Strings.ContainsKey);
            Check(found.Name, "label", ids.Label);
            Check(found.Name, "hint", description.Spec.Hint);
            Check(found.Name, "more", description.Spec.More);
            Check(found.Name, "blank label", description.Spec.BlankLabel);
            Check(found.Name, "empty-list hint", description.Spec.EmptyListHint);
            if (description.Spec.Placeholder is { } literal && English.Strings.ContainsKey(description.TextId + ".placeholder"))
            {
                problems.Add($"{found.Name}: has both a literal placeholder ('{literal}') and a pack text at {description.TextId}.placeholder");
            }
        }

        Assert.True(problems.Count == 0, "Field descriptions and the en pack disagree:\n" + string.Join('\n', problems));

        void Check(string name, string what, string? id)
        {
            if (id is not null && !English.Strings.ContainsKey(id))
            {
                problems.Add($"{name}: {what} text id '{id}' is not in the en pack");
            }
        }
    }

    [Fact]
    public void EveryDescription_NamesKnownChoiceLists_FolderLists_AndResolvableDependencies()
    {
        var problems = new List<string>();
        foreach (var found in Walk())
        {
            if (OptionFieldCatalog.Describe(found.Property) is not { } description)
            {
                continue;
            }

            if (description.Kind == FieldKind.Select && !FieldChoiceCatalog.IsKnown(description.Spec.Choices!))
            {
                problems.Add($"{found.Name}: choice list '{description.Spec.Choices}' is neither in FieldChoiceCatalog nor a known runtime list");
            }

            if (description.Spec.BrowseStartIn is { } folders && !BrowseFolders.IsKnown(folders))
            {
                problems.Add($"{found.Name}: BrowseStartIn '{folders}' is not an ExpectedFolders list");
            }

            if (description.Spec.BrowseStartIn is not null && !FieldValidation.Describe(description.Spec.TextKind).IsPath)
            {
                problems.Add($"{found.Name}: BrowseStartIn on a text kind without a Browse… dialog ({description.Spec.TextKind})");
            }

            try
            {
                _ = FieldDependency.AllHold(description.Dependencies, found.OwnerChain);
            }
            catch (InvalidOperationException ex)
            {
                problems.Add($"{found.Name}: {ex.Message}");
            }
        }

        Assert.True(problems.Count == 0, "Field descriptions name things that do not exist:\n" + string.Join('\n', problems));
    }

    private static bool IsExempt(string name)
        => NotOnAPage.ContainsKey(name) || NotOnAPage.ContainsKey(name[..name.IndexOf('.', StringComparison.Ordinal)] + ".*");

    /// <summary>Every editable property of every registered section, with a fresh instance chain.</summary>
    private static IEnumerable<Found> Walk()
    {
        var seenTypes = new HashSet<Type>();
        foreach (var section in SettingsDefaultsWriterTests.CoreRegistry().Sections)
        {
            foreach (var found in Walk(section.CreateDefaults(), [], seenTypes))
            {
                yield return found;
            }
        }
    }

    private static IEnumerable<Found> Walk(object instance, IReadOnlyList<object> owners, HashSet<Type> seenTypes)
    {
        var type = instance.GetType();
        if (!seenTypes.Add(type))
        {
            yield break; // a shared nested type is reported once
        }

        var chain = owners.Append(instance).ToList();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var propertyType = property.PropertyType;
            var simple = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
            if (simple.IsPrimitive || simple.IsEnum || simple == typeof(string) || simple == typeof(decimal))
            {
                if (property.CanWrite && property.SetMethod?.IsPublic == true)
                {
                    yield return new Found(property, chain);
                }

                continue;
            }

            if (typeof(IDictionary).IsAssignableFrom(propertyType))
            {
                continue; // JSON-edited power-user structures
            }

            var value = property.GetValue(instance);
            if (value is IEnumerable items)
            {
                var itemType = propertyType.IsGenericType ? propertyType.GetGenericArguments()[0] : null;
                if (itemType is not null && IsOptionObject(itemType))
                {
                    // A list item is edited on its own: its rule chain starts at the item.
                    var sample = items.Cast<object?>().FirstOrDefault(i => i is not null) ?? Activator.CreateInstance(itemType);
                    if (sample is not null)
                    {
                        foreach (var found in Walk(sample, [], seenTypes))
                        {
                            yield return found;
                        }
                    }
                }

                continue;
            }

            if (value is not null && IsOptionObject(value.GetType()))
            {
                foreach (var found in Walk(value, chain, seenTypes))
                {
                    yield return found;
                }
            }
        }
    }

    private static bool IsOptionObject(Type type)
        => type.IsClass && type != typeof(string) && type.Namespace?.StartsWith("ProsimCompanion", StringComparison.Ordinal) is true;
}
