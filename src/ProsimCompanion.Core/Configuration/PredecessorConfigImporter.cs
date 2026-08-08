using System.IO;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// One-shot importers for the predecessors' config files (roadmap Phase 7): Prosim2GSX's
/// <c>%APPDATA%\Prosim2GSX\AppConfig.json</c> (PascalCase keys, per-profile GSX behaviour) and
/// Prosim2FO's <c>{install}\config\settings.json</c> (camelCase, our own schema's ancestor).
/// Focus is on the settings that are painful to re-enter — endpoints, paths, keys, bindings and
/// the GSX behaviour block — not on timing knobs, whose shipped defaults are already the
/// predecessor-proven values. Every read is defensive: a missing or malformed key is skipped,
/// never fatal. Runs once, marker-guarded (<c>predecessorImport</c> section in settings.json);
/// delete that section to re-run.
/// </summary>
public static class PredecessorConfigImporter
{
    /// <summary>Where Prosim2GSX keeps its config (fixed by its installer).</summary>
    public static string DefaultProsim2GsxConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Prosim2GSX",
        "AppConfig.json");

    /// <summary>Prosim2FO probe list — its Inno installer defaulted to the per-user programs
    /// folder; settings.json lives beside its exe under config\.</summary>
    public static IReadOnlyList<string> DefaultProsim2FoSettingsPaths =>
    [
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Prosim2FO", "config", "settings.json"),
        @"C:\Prosim2FO\config\settings.json",
    ];

    /// <summary>Marker-guarded first-run import: probes both predecessors and imports whatever
    /// exists. Returns true when anything was imported. The marker is written either way so a
    /// clean machine is not re-probed every start.</summary>
    public static bool TryImportOnFirstRun(JsonSettingsFile settings, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        var alreadyDone = false;
        settings.Update(root => alreadyDone = root["predecessorImport"] is not null);
        if (alreadyDone)
        {
            return false;
        }

        var importedGsx = false;
        var importedFo = false;
        try
        {
            if (File.Exists(DefaultProsim2GsxConfigPath))
            {
                importedGsx = ImportProsim2Gsx(settings, DefaultProsim2GsxConfigPath, logger);
            }

            var foPath = DefaultProsim2FoSettingsPaths.FirstOrDefault(File.Exists);
            if (foPath is not null)
            {
                importedFo = ImportProsim2Fo(settings, foPath, logger);
            }
        }
        catch (Exception ex)
        {
            // Import is a convenience — a failure must never block startup.
            logger.LogWarning(ex, "Predecessor config import failed — starting with defaults");
        }

        settings.Update(root => root["predecessorImport"] = new JsonObject
        {
            ["completedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["prosim2gsx"] = importedGsx,
            ["prosim2fo"] = importedFo,
        });
        return importedGsx || importedFo;
    }

    /// <summary>Imports Prosim2GSX's AppConfig.json (SDK path, VoiceMeeter, audio mappings,
    /// saved FOB, and the default aircraft profile's GSX behaviour block).</summary>
    public static bool ImportProsim2Gsx(JsonSettingsFile settings, string configPath, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        if (JsonNode.Parse(File.ReadAllText(configPath)) is not JsonObject old)
        {
            return false;
        }

        settings.Update(root =>
        {
            var prosim = JsonSettingsFile.GetOrCreateSection(root, ProsimOptions.SectionName);
            CopyString(old, "ProSimSdkPath", prosim, "sdkPath");
            CopyString(old, "ProSimSdkHostname", prosim, "host");

            var audio = JsonSettingsFile.GetOrCreateSection(root, AudioOptions.SectionName);
            if (ReadBool(old, "UseVoiceMeeter") == true)
            {
                audio["backend"] = "voiceMeeter";
            }
            CopyString(old, "VoiceMeeterDllPath", audio, "voiceMeeterDllPath");
            if (old["AudioDeviceBlacklist"] is JsonArray blacklist && blacklist.Count > 0)
            {
                audio["deviceBlacklist"] = new JsonArray([.. blacklist
                    .Select(item => item?.GetValue<string>())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => JsonValue.Create(item))]);
            }
            if (old["AudioMappings"] is JsonArray mappings && mappings.Count > 0)
            {
                var converted = new JsonArray();
                foreach (var mapping in mappings.OfType<JsonObject>())
                {
                    var channel = ChannelName(ReadInt(mapping, "Channel") ?? 0);
                    var binary = ReadString(mapping, "Binary");
                    if (channel is null || string.IsNullOrWhiteSpace(binary))
                    {
                        continue;
                    }
                    converted.Add(new JsonObject
                    {
                        ["channel"] = channel,
                        ["binary"] = binary,
                        ["device"] = ReadString(mapping, "Device") ?? "",
                        ["useLatch"] = ReadBool(mapping, "UseLatch") ?? true,
                        ["onlyActive"] = ReadBool(mapping, "OnlyActive") ?? true,
                    });
                }
                if (converted.Count > 0)
                {
                    audio["appMappings"] = converted;
                }
            }

            var gsx = JsonSettingsFile.GetOrCreateSection(root, GsxOptions.SectionName);
            if (old["FuelFobSaved"] is JsonObject fob && fob.Count > 0)
            {
                gsx["fuelFobSaved"] = fob.DeepClone();
            }
            CopyNumber(old, "FuelResetDefaultKg", gsx, "fuelResetDefaultKg");
            CopyNumber(old, "ProsimWeightBag", gsx, "weightPerBagKg");

            if (FindDefaultProfile(old) is { } profile)
            {
                ImportGsxProfile(profile, gsx);
            }
        });

        logger.LogInformation("Imported Prosim2GSX settings from {Path}", configPath);
        return true;
    }

    /// <summary>The Prosim2GSX per-profile behaviour block → our flat gsx section (the same
    /// mapping the parity port established; int tri-states become our string enums).</summary>
    private static void ImportGsxProfile(JsonObject profile, JsonNode gsx)
    {
        CopyBool(profile, "SkipFollowMe", gsx, "skipFollowMe");
        CopyBool(profile, "SkipCrewQuestion", gsx, "skipCrewBoardingQuestion");
        CopyBool(profile, "OperatorAutoSelect", gsx, "autoSelectOperator");
        CopyBool(profile, "FuelSaveLoadFob", gsx, "fuelSaveLoadFob");
        CopyBool(profile, "RandomizePax", gsx, "randomizePaxNoShows");
        CopyNumber(profile, "ChancePerSeat", gsx, "noShowChancePerSeat");
        CopyBool(profile, "SkipFuelOnTankering", gsx, "skipRefuelOnTankering");
        CopyBool(profile, "RefuelFinishOnHose", gsx, "refuelFinishOnHose");
        CopyNumber(profile, "RefuelRateKgSec", gsx, "refuelRateKgPerSec");
        CopyNumber(profile, "RefuelTimeTargetSeconds", gsx, "refuelTimeTargetSeconds");
        gsx["refuelMethod"] = ReadInt(profile, "RefuelMethod") == 1 ? "dynamicRate" : "fixedRate";
        CopyBool(profile, "CallReposition", gsx, "autoReposition");
        CopyBool(profile, "ConnectGpuWithApuRunning", gsx, "connectGpuWithApuRunning");
        CopyBool(profile, "PcaOverride", gsx, "pcaOverride");
        gsx["pcaMode"] = TriState(ReadInt(profile, "ConnectPca"), "never", "always", "onlyJetway");
        CopyBool(profile, "GradualGroundEquipRemoval", gsx, "gradualGroundEquipRemoval");
        CopyNumber(profile, "ChockDelayMin", gsx, "chockDelayMinSec");
        CopyNumber(profile, "ChockDelayMax", gsx, "chockDelayMaxSec");

        CopyBool(profile, "DoorStairHandling", gsx, "doorStairHandling");
        CopyBool(profile, "DoorCargoHandling", gsx, "doorCargoHandling");
        CopyBool(profile, "DoorCateringHandling", gsx, "doorCateringHandling");
        CopyBool(profile, "DoorOpenBoardActive", gsx, "doorOpenOnBoardingActive");
        CopyBool(profile, "DoorsCargoKeepOpenOnLoaded", gsx, "keepCargoDoorsOpenAfterLoad");
        CopyBool(profile, "DoorsCargoKeepOpenOnUnloaded", gsx, "keepCargoDoorsOpenAfterUnload");
        CopyBool(profile, "CloseDoorsOnFinal", gsx, "closeDoorsOnFinal");

        CopyBool(profile, "CallJetwayStairsOnPrep", gsx, "autoConnectJetwayOrStairs");
        CopyBool(profile, "CallJetwayStairsDuringDeparture", gsx, "callJetwayStairsDuringDeparture");
        CopyBool(profile, "CallJetwayStairsOnArrival", gsx, "callJetwayStairsOnArrival");
        CopyBool(profile, "RemoveJetwayStairsOnFinal", gsx, "removeJetwayStairsOnFinal");
        // (sic) "Depature" is the predecessor's real JSON key.
        gsx["removeStairsAfterDeparture"] = TriState(
            ReadInt(profile, "RemoveStairsAfterDepature"), "never", "always", "onlyJetway");

        gsx["tugQuestionAnswer"] = TriState(ReadInt(profile, "AttachTugDuringBoarding"), "ignore", "no", "yes");
        gsx["callPushbackWhenTugAttached"] = TriState(
            ReadInt(profile, "CallPushbackWhenTugAttached"), "never", "afterDepartureServices", "afterFinalLoadsheet");
        gsx["pushbackPreference"] = TriState(ReadInt(profile, "PushbackPreference"), "straight", "tailLeft", "tailRight");
        CopyBool(profile, "SequenceOnBeacon", gsx, "beaconPushbackSequenceEnabled");
        CopyBool(profile, "CallPushbackOnBeacon", gsx, "callPushbackOnBeacon");
        CopyNumber(profile, "SeqDoorsCloseDelayMin", gsx, "seqDoorsCloseDelayMinSec");
        CopyNumber(profile, "SeqDoorsCloseDelayMax", gsx, "seqDoorsCloseDelayMaxSec");
        CopyNumber(profile, "SeqJetwayRetractDelayMin", gsx, "seqJetwayRetractDelayMinSec");
        CopyNumber(profile, "SeqJetwayRetractDelayMax", gsx, "seqJetwayRetractDelayMaxSec");
        CopyNumber(profile, "SeqGpuDisconnectDelayMin", gsx, "seqGpuDisconnectDelayMinSec");
        CopyNumber(profile, "SeqGpuDisconnectDelayMax", gsx, "seqGpuDisconnectDelayMaxSec");
        CopyBool(profile, "CallDeboardOnArrival", gsx, "autoCallDeboardOnArrival");

        CopyStringList(profile, "OperatorPreferences", gsx, "operatorPreferences");
        CopyStringList(profile, "CompanyHubs", gsx, "companyHubs");

        if (profile["DepartureServices"] is JsonObject services && services.Count > 0)
        {
            var steps = new JsonArray();
            foreach (var (_, value) in services.OrderBy(pair => int.TryParse(pair.Key, out var i) ? i : int.MaxValue))
            {
                if (value is not JsonObject step)
                {
                    continue;
                }
                var serviceName = ServiceName(ReadInt(step, "ServiceType") ?? 0);
                if (serviceName is null)
                {
                    continue;
                }
                steps.Add(new JsonObject
                {
                    ["service"] = serviceName,
                    ["activation"] = ActivationName(ReadInt(step, "ServiceActivation") ?? 2),
                    ["constraint"] = ConstraintName(ReadInt(step, "ServiceConstraint") ?? 0),
                    ["minimumFlightMinutes"] = ParseDurationMinutes(ReadString(step, "MinimumFlightDuration")),
                });
            }
            if (steps.Count > 0)
            {
                gsx["departureServices"] = steps;
            }
        }
    }

    /// <summary>Imports Prosim2FO's settings.json (ProSim connection, TTS endpoints/voices,
    /// recognition endpoint + keyboard bindings, LLM endpoint, nav data, SayIntentions).</summary>
    public static bool ImportProsim2Fo(JsonSettingsFile settings, string settingsPath, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        if (JsonNode.Parse(File.ReadAllText(settingsPath)) is not JsonObject old)
        {
            return false;
        }

        settings.Update(root =>
        {
            var prosim = JsonSettingsFile.GetOrCreateSection(root, ProsimOptions.SectionName);
            if (old["prosim"] is JsonObject oldProsim)
            {
                CopyString(oldProsim, "hostname", prosim, "host");
                CopyString(oldProsim, "sdkPath", prosim, "sdkPath");
                CopyString(oldProsim, "apiKey", prosim, "apiKey");
            }

            var speech = JsonSettingsFile.GetOrCreateSection(root, SpeechOptions.SectionName);
            if (old["audio"] is JsonObject oldAudio)
            {
                CopyString(oldAudio, "outputDevice", speech, "outputDevice");
                CopyString(oldAudio, "inputDevice", speech, "inputDevice");
                if (oldAudio["tts"] is JsonObject tts)
                {
                    CopyBool(tts, "localOnly", speech, "localOnly");
                    CopyBool(tts, "intercomFilter", speech, "intercomFilter");
                    CopyBool(tts, "listeningTone", speech, "listeningTone");
                    CopyNumber(tts, "volume", speech, "volume");
                    if (tts["local"] is JsonObject local)
                    {
                        CopyString(local, "baseUrl", speech, "kokoroBaseUrl");
                        CopyString(local, "model", speech, "kokoroModel");
                        CopyString(local, "voice", speech, "kokoroVoice");
                        CopyNumber(local, "timeoutMs", speech, "kokoroTimeoutMs");
                    }
                    if (tts["google"] is JsonObject google)
                    {
                        CopyString(google, "credentialsPath", speech, "googleKeyFilePath");
                        CopyString(google, "voice", speech, "googleVoice");
                    }
                }
            }

            if (old["recognition"] is JsonObject recognition)
            {
                CopyString(recognition, "mode", speech, "recognitionMode");
                CopyNumber(recognition, "confidenceThreshold", speech, "recognitionConfidenceThreshold");
                if (recognition["snapping"] is JsonObject snapping)
                {
                    CopyNumber(snapping, "threshold", speech, "snappingThreshold");
                }
                if (recognition["lanAsr"] is JsonObject lanAsr
                    && ReadString(lanAsr, "endpoint") is { Length: > 0 } endpoint)
                {
                    // Their key stored the full /transcribe endpoint; ours is the base URL.
                    speech["asrBaseUrl"] = endpoint.EndsWith("/transcribe", StringComparison.OrdinalIgnoreCase)
                        ? endpoint[..^"/transcribe".Length]
                        : endpoint;
                }
                // Keyboard PTT carries over; joystick bindings don't (their GUID device ids
                // have no mapping to our winmm indices — re-bind on the Speech settings page).
                if (recognition["pushToTalkBinding"] is JsonObject ptt
                    && string.Equals(ReadString(ptt, "kind"), "keyboard", StringComparison.OrdinalIgnoreCase))
                {
                    CopyString(ptt, "key", speech, "pttKey");
                }
            }

            if (old["atcMute"] is JsonObject atcMute
                && atcMute["binding"] is JsonObject muteBinding
                && string.Equals(ReadString(muteBinding, "kind"), "keyboard", StringComparison.OrdinalIgnoreCase))
            {
                CopyString(muteBinding, "key", speech, "atcMuteKey");
            }

            var briefing = JsonSettingsFile.GetOrCreateSection(root, BriefingOptions.SectionName);
            if (old["llm"] is JsonObject llm)
            {
                CopyBool(llm, "enabled", briefing, "llmEnabled");
                CopyString(llm, "baseUrl", briefing, "llmBaseUrl");
                CopyString(llm, "apiKey", briefing, "llmApiKey");
                CopyString(llm, "model", briefing, "llmModel");
                CopyNumber(llm, "maxTokens", briefing, "llmMaxTokens");
                CopyNumber(llm, "timeoutSeconds", briefing, "llmTimeoutSeconds");
            }
            if (old["navData"] is JsonObject navData)
            {
                CopyString(navData, "dfdPath", briefing, "dfdPath");
            }

            var sayIntentions = JsonSettingsFile.GetOrCreateSection(root, SayIntentionsOptions.SectionName);
            if (old["sayIntentions"] is JsonObject si)
            {
                CopyBool(si, "enabled", sayIntentions, "enabled");
                CopyString(si, "apiKeySource", sayIntentions, "apiKeySource");
                CopyString(si, "manualApiKey", sayIntentions, "manualApiKey");
                CopyBool(si, "autoTuneFrequency", sayIntentions, "autoTuneFrequency");
                CopyString(si, "phraseology", sayIntentions, "phraseology");
            }

            var persona = JsonSettingsFile.GetOrCreateSection(root, PersonaOptions.SectionName);
            if (old["persona"] is JsonObject oldPersona)
            {
                CopyBool(oldPersona, "enabled", persona, "enabled");
                CopyString(oldPersona, "name", persona, "name");
                CopyString(oldPersona, "experience", persona, "experience");
                CopyString(oldPersona, "formality", persona, "formality");
                CopyNumber(oldPersona, "chattiness", persona, "chattiness");
            }
        });

        logger.LogInformation("Imported Prosim2FO settings from {Path}", settingsPath);
        return true;
    }

    // ---- helpers -------------------------------------------------------------------------

    private static JsonObject? FindDefaultProfile(JsonObject old)
        => old["AircraftProfiles"] is JsonArray profiles
            ? profiles.OfType<JsonObject>().FirstOrDefault(p =>
                    string.Equals(ReadString(p, "Name"), "default", StringComparison.OrdinalIgnoreCase))
                ?? profiles.OfType<JsonObject>().FirstOrDefault()
            : null;

    private static string TriState(int? value, string zero, string one, string two) => value switch
    {
        1 => one,
        2 => two,
        _ => zero,
    };

    private static string? ChannelName(int channel) => channel switch
    {
        0 => "vhf1",
        1 => "vhf2",
        2 => "vhf3",
        3 => "hf1",
        4 => "hf2",
        5 => "intercom",
        6 => "cabin",
        7 => "pa",
        _ => null,
    };

    private static string? ServiceName(int serviceType) => serviceType switch
    {
        2 => "Refueling",
        3 => "Catering",
        4 => "Boarding",
        8 => "GPU",
        9 => "Water",
        10 => "Lavatory",
        13 => "Cleaning",
        6 => "DeIce",
        _ => null, // Reposition/Pushback/Jetway/Stairs are not departure-queue services here
    };

    private static string ActivationName(int activation) => activation switch
    {
        0 => "skip",
        1 => "manual",
        3 => "afterRequested",
        4 => "afterActive",
        5 => "afterPrevCompleted",
        6 => "afterAllCompleted",
        _ => "afterCalled",
    };

    private static string ConstraintName(int constraint) => constraint switch
    {
        1 => "firstLeg",
        2 => "turnAround",
        3 => "companyHub",
        4 => "nonCompanyHub",
        _ => "always",
    };

    private static int ParseDurationMinutes(string? duration)
        => TimeSpan.TryParse(duration, System.Globalization.CultureInfo.InvariantCulture, out var span)
            ? (int)span.TotalMinutes
            : 0;

    private static string? ReadString(JsonObject node, string key)
        => node[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool? ReadBool(JsonObject node, string key)
        => node[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    private static int? ReadInt(JsonObject node, string key)
        => node[key] is JsonValue value && value.TryGetValue<double>(out var number) ? (int)number : null;

    private static void CopyString(JsonObject from, string fromKey, JsonNode to, string toKey)
    {
        if (ReadString(from, fromKey) is { Length: > 0 } text)
        {
            to[toKey] = text;
        }
    }

    private static void CopyBool(JsonObject from, string fromKey, JsonNode to, string toKey)
    {
        if (ReadBool(from, fromKey) is { } flag)
        {
            to[toKey] = flag;
        }
    }

    private static void CopyNumber(JsonObject from, string fromKey, JsonNode to, string toKey)
    {
        if (from[fromKey] is JsonValue value && value.TryGetValue<double>(out var number))
        {
            to[toKey] = number;
        }
    }

    private static void CopyStringList(JsonObject from, string fromKey, JsonNode to, string toKey)
    {
        if (from[fromKey] is JsonArray list && list.Count > 0)
        {
            to[toKey] = new JsonArray([.. list
                .Select(item => item?.GetValue<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => JsonValue.Create(item))]);
        }
    }
}
