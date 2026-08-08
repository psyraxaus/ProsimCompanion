using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Configuration;
using Xunit;

namespace ProsimCompanion.Core.Tests.Configuration;

public sealed class PredecessorConfigImporterTests
{
    private static (JsonSettingsFile Settings, string Path) TempSettings()
    {
        var dir = Directory.CreateTempSubdirectory("pc-import-").FullName;
        var path = Path.Combine(dir, "settings.json");
        return (new JsonSettingsFile(path), path);
    }

    private static JsonObject Root(string path)
        => (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;

    [Fact]
    public void Prosim2Gsx_ImportsSdkPath_ProfileBlock_AndDepartureServices()
    {
        var (settings, path) = TempSettings();
        var source = Path.Combine(Path.GetDirectoryName(path)!, "AppConfig.json");
        File.WriteAllText(source, """
        {
          "ProSimSdkPath": "C:\\prosim\\prosim-system\\ProSimSDK.dll",
          "UseVoiceMeeter": true,
          "VoiceMeeterDllPath": "C:\\VB\\VoicemeeterRemote64.dll",
          "FuelFobSaved": { "Prosim A320": 4321.5 },
          "AudioMappings": [
            { "Channel": 0, "Device": "", "Binary": "vPilot", "UseLatch": true, "OnlyActive": true },
            { "Channel": 5, "Device": "Speakers", "Binary": "Couatl64_MSFS", "UseLatch": false, "OnlyActive": true }
          ],
          "AircraftProfiles": [
            {
              "Name": "default",
              "ConnectPca": 2,
              "AttachTugDuringBoarding": 2,
              "CallPushbackWhenTugAttached": 1,
              "RemoveStairsAfterDepature": 1,
              "RefuelMethod": 1,
              "RefuelRateKgSec": 30.0,
              "PushbackPreference": 2,
              "SkipFuelOnTankering": false,
              "DepartureServices": {
                "0": { "ServiceType": 13, "ServiceActivation": 2, "ServiceConstraint": 2, "MinimumFlightDuration": "00:00:00" },
                "1": { "ServiceType": 3, "ServiceActivation": 5, "ServiceConstraint": 3, "MinimumFlightDuration": "00:45:00" },
                "2": { "ServiceType": 4, "ServiceActivation": 6, "ServiceConstraint": 0, "MinimumFlightDuration": "00:00:00" }
              }
            }
          ]
        }
        """);

        var imported = PredecessorConfigImporter.ImportProsim2Gsx(settings, source, NullLogger.Instance);
        Assert.True(imported);

        var root = Root(path);
        Assert.Equal("C:\\prosim\\prosim-system\\ProSimSDK.dll", (string?)root["prosim"]?["sdkPath"]);
        Assert.Equal("voiceMeeter", (string?)root["audio"]?["backend"]);
        Assert.Equal(4321.5, (double?)root["gsx"]?["fuelFobSaved"]?["Prosim A320"]);
        Assert.Equal("onlyJetway", (string?)root["gsx"]?["pcaMode"]);
        Assert.Equal("yes", (string?)root["gsx"]?["tugQuestionAnswer"]);
        Assert.Equal("afterDepartureServices", (string?)root["gsx"]?["callPushbackWhenTugAttached"]);
        Assert.Equal("always", (string?)root["gsx"]?["removeStairsAfterDeparture"]);
        Assert.Equal("dynamicRate", (string?)root["gsx"]?["refuelMethod"]);
        Assert.Equal(30.0, (double?)root["gsx"]?["refuelRateKgPerSec"]);
        Assert.Equal("tailRight", (string?)root["gsx"]?["pushbackPreference"]);
        Assert.False((bool?)root["gsx"]?["skipRefuelOnTankering"]);

        var mappings = (JsonArray?)root["audio"]?["appMappings"];
        Assert.NotNull(mappings);
        Assert.Equal(2, mappings!.Count);
        Assert.Equal("intercom", (string?)mappings[1]?["channel"]);
        Assert.False((bool?)mappings[1]?["useLatch"]);

        var steps = (JsonArray?)root["gsx"]?["departureServices"];
        Assert.NotNull(steps);
        Assert.Equal(3, steps!.Count);
        Assert.Equal("Cleaning", (string?)steps[0]?["service"]);
        Assert.Equal("turnAround", (string?)steps[0]?["constraint"]);
        Assert.Equal("Catering", (string?)steps[1]?["service"]);
        Assert.Equal("afterPrevCompleted", (string?)steps[1]?["activation"]);
        Assert.Equal(45, (int?)steps[1]?["minimumFlightMinutes"]);
        Assert.Equal("Boarding", (string?)steps[2]?["service"]);
        Assert.Equal("afterAllCompleted", (string?)steps[2]?["activation"]);
    }

    [Fact]
    public void Prosim2Fo_ImportsConnection_Tts_Recognition_Llm_AndPersona()
    {
        var (settings, path) = TempSettings();
        var source = Path.Combine(Path.GetDirectoryName(path)!, "fo-settings.json");
        File.WriteAllText(source, """
        {
          "prosim": { "hostname": "192.168.1.10", "sdkPath": "C:\\prosim\\ProSimSDK.dll" },
          "audio": {
            "outputDevice": "Headset",
            "tts": {
              "localOnly": true,
              "local": { "baseUrl": "http://192.168.1.50:8880", "voice": "bm_lewis", "timeoutMs": 2000 },
              "google": { "credentialsPath": "C:\\keys\\google.json" }
            }
          },
          "recognition": {
            "mode": "continuous",
            "lanAsr": { "endpoint": "http://192.168.1.50:8000/transcribe" },
            "pushToTalkBinding": { "kind": "keyboard", "key": "F12" }
          },
          "llm": { "enabled": true, "baseUrl": "http://localhost:3000/api", "model": "llama3" },
          "navData": { "dfdPath": "C:\\navdata\\dfd.s3db" },
          "persona": { "enabled": true, "name": "Alex", "experience": "senior", "chattiness": 2 }
        }
        """);

        var imported = PredecessorConfigImporter.ImportProsim2Fo(settings, source, NullLogger.Instance);
        Assert.True(imported);

        var root = Root(path);
        Assert.Equal("192.168.1.10", (string?)root["prosim"]?["host"]);
        Assert.Equal("C:\\prosim\\ProSimSDK.dll", (string?)root["prosim"]?["sdkPath"]);
        Assert.Equal("Headset", (string?)root["speech"]?["outputDevice"]);
        Assert.True((bool?)root["speech"]?["localOnly"]);
        Assert.Equal("http://192.168.1.50:8880", (string?)root["speech"]?["kokoroBaseUrl"]);
        Assert.Equal("bm_lewis", (string?)root["speech"]?["kokoroVoice"]);
        Assert.Equal("C:\\keys\\google.json", (string?)root["speech"]?["googleKeyFilePath"]);
        Assert.Equal("continuous", (string?)root["speech"]?["recognitionMode"]);
        // The /transcribe suffix is stripped — ours stores the base URL.
        Assert.Equal("http://192.168.1.50:8000", (string?)root["speech"]?["asrBaseUrl"]);
        Assert.Equal("F12", (string?)root["speech"]?["pttKey"]);
        Assert.True((bool?)root["briefing"]?["llmEnabled"]);
        Assert.Equal("llama3", (string?)root["briefing"]?["llmModel"]);
        Assert.Equal("C:\\navdata\\dfd.s3db", (string?)root["briefing"]?["dfdPath"]);
        Assert.True((bool?)root["persona"]?["enabled"]);
        Assert.Equal("Alex", (string?)root["persona"]?["name"]);
        Assert.Equal(2, (int?)root["persona"]?["chattiness"]);
    }

    [Fact]
    public void FirstRunImport_IsMarkerGuarded()
    {
        var (settings, path) = TempSettings();

        // No predecessor files exist at the probe paths on the test box... but even if they
        // did, the second call must be a no-op because the marker was written.
        _ = PredecessorConfigImporter.TryImportOnFirstRun(settings, NullLogger.Instance);
        var marker = Root(path)["predecessorImport"];
        Assert.NotNull(marker);

        var second = PredecessorConfigImporter.TryImportOnFirstRun(settings, NullLogger.Instance);
        Assert.False(second);
    }
}
