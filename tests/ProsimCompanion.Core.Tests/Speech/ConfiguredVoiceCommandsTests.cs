using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Speech.Commands;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

public sealed class ConfiguredVoiceCommandsTests
{
    // ---- fakes -------------------------------------------------------------------------

    /// <summary>Records writes and serves subscription reads from a settable value table.</summary>
    private sealed class FakeDataRefs : IProsimDataRefs
    {
        public List<(string Name, object? Value)> Writes { get; } = [];
        public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);

        public IDataRefSubscription Subscribe(string name, DataRefTier tier) => new FakeSub(this, name);

        public Task WriteAsync(string name, object? value, CancellationToken cancellationToken = default)
        {
            lock (Writes)
            {
                Writes.Add((name, value));
            }

            return Task.CompletedTask;
        }

        public Task PressMomentaryAsync(string name, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        private sealed class FakeSub(FakeDataRefs owner, string name) : IDataRefSubscription
        {
            public string Name => name;
            public object? RawValue => owner.Values.TryGetValue(name, out var v) ? v : null;
            public bool IsStale => false;
            public DateTimeOffset? LastUpdatedUtc => null;

            public event EventHandler? ValueChanged
            {
                add { }
                remove { }
            }

            public T GetValue<T>(T fallback)
                => RawValue is null
                    ? fallback
                    : (T)Convert.ChangeType(RawValue, typeof(T), CultureInfo.InvariantCulture);

            public void Dispose()
            {
            }
        }
    }

    private readonly FakeDataRefs _dataRefs = new();
    private readonly FakeArbiter _arbiter = new();

    private ConfiguredVoiceCommands CreateService()
    {
        var briefing = new Mock<IOptionsMonitor<BriefingOptions>>();
        briefing.SetupGet(m => m.CurrentValue).Returns(new BriefingOptions());
        return new ConfiguredVoiceCommands(
            _dataRefs,
            _arbiter,
            SpeechTestSupport.TempEventLog(),
            new SpokenTokenSource(_dataRefs, briefing.Object, NullLogger<SpokenTokenSource>.Instance),
            NullLogger<ConfiguredVoiceCommands>.Instance);
    }

    private ConfiguredVoiceCommands CreateService(string json)
    {
        var service = CreateService();
        service.Load(VoiceCommandConfigParser.Parse(json).Set);
        return service;
    }

    private static void WaitForDispatch(Func<bool> condition)
        => SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5));

    // ---- JSON parsing ------------------------------------------------------------------

    private const string ApOneJson = """
        {
          "//": "top-level note",
          "commands": [
            {
              "//": "per-command note properties are ignored",
              "phrases": [ "autopilot one", "autopilot on" ],
              "say": "Autopilot one",
              "steps": [ { "dataref": "system.switches.S_FCU_AP1", "press": 1, "restore": 0, "holdMs": 1 } ]
            }
          ]
        }
        """;

    [Fact]
    public void Parse_ReadsCommandsPhrasesAndAllowList()
    {
        var result = VoiceCommandConfigParser.Parse(ApOneJson);

        var command = Assert.Single(result.Set.Commands);
        Assert.Equal("Autopilot one", command.Say);
        var step = Assert.Single(command.Steps);
        Assert.Equal("system.switches.S_FCU_AP1", step.Dataref);
        Assert.Equal(1, step.Press);
        Assert.Equal(0, step.Restore);
        Assert.Contains("autopilot one", result.Set.Phrases);
        Assert.Contains("autopilot on", result.Set.Phrases);
        Assert.Contains("system.switches.S_FCU_AP1", result.Set.WriteAllowList);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Parse_StepDefaults_HoldAndDelay()
    {
        var result = VoiceCommandConfigParser.Parse("""
            { "commands": [ { "phrases": [ "x" ], "steps": [ { "dataref": "system.switches.S_FCU_LOC", "press": 1 } ] } ] }
            """);

        var step = Assert.Single(Assert.Single(result.Set.Commands).Steps);
        Assert.Equal(150, step.HoldMs);
        Assert.Equal(0, step.DelayMs);
        Assert.Null(step.Restore);
    }

    [Fact]
    public void Parse_SkipsPlaceholderPhrases()
    {
        var result = VoiceCommandConfigParser.Parse("""
            {
              "commands": [
                {
                  "phrases": [ "«fill me in»", "real phrase" ],
                  "steps": [ { "dataref": "system.switches.S_FCU_AP1", "press": 1, "restore": 0 } ]
                }
              ]
            }
            """);

        Assert.Equal(["real phrase"], result.Set.Phrases);
    }

    [Fact]
    public void Parse_DuplicatePhrase_FirstDefinitionWins_WithWarning()
    {
        var result = VoiceCommandConfigParser.Parse("""
            {
              "commands": [
                { "phrases": [ "do it" ], "say": "first", "steps": [] },
                { "phrases": [ "Do It" ], "say": "second", "steps": [] }
              ]
            }
            """);

        Assert.True(result.Set.TryResolve("do it", out var resolved));
        Assert.Equal("first", resolved.Definition.Say);
        Assert.Contains(result.Warnings, w => w.Contains("Duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parse_MalformedJson_Throws()
        => Assert.ThrowsAny<System.Text.Json.JsonException>(
            () => VoiceCommandConfigParser.Parse("{ not json"));

    [Fact]
    public void Parse_ShippedFile_LoadsEveryCommandFamily()
    {
        // The repo's shipped config — guards the schema and the 15 command families.
        var path = Path.Combine(FindRepoRoot(), "src", "ProsimCompanion.App", "config", "commands.json");
        var result = VoiceCommandConfigParser.Parse(File.ReadAllText(path));

        Assert.Equal(15, result.Set.Commands.Count);
        Assert.Empty(result.Warnings);
        Assert.Contains("set standard", result.Set.Phrases);
        Assert.Contains("set qnh", result.Set.Phrases);
        Assert.Contains("altimeter check", result.Set.Phrases);
        Assert.Contains("activate approach phase", result.Set.Phrases);
        Assert.Contains("clear rad nav", result.Set.Phrases);
        Assert.Contains("copy active to secondary", result.Set.Phrases);
        // Every step dataref of the shipped file must clear the code-level gate.
        Assert.All(
            result.Set.Commands.SelectMany(c => c.Steps),
            s => Assert.True(VoiceCommandWriteGate.IsAllowed(s.Dataref), s.Dataref));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProsimCompanion.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir.FullName;
    }

    // ---- phrase matching ---------------------------------------------------------------

    [Theory]
    [InlineData("autopilot one")]
    [InlineData("  Autopilot ONE  ")]
    [InlineData("autopilot one!")]
    public void TryResolve_MatchesNormalized(string utterance)
    {
        var set = VoiceCommandConfigParser.Parse(ApOneJson).Set;

        Assert.True(set.TryResolve(utterance, out var resolved));
        Assert.Equal("Autopilot one", resolved.Definition.Say);
    }

    [Fact]
    public void TryResolve_UnknownPhrase_IsFalse()
    {
        var set = VoiceCommandConfigParser.Parse(ApOneJson).Set;

        Assert.False(set.TryResolve("autopilot three", out _));
    }

    // ---- write allow-list enforcement ----------------------------------------------------

    [Theory]
    [InlineData("system.switches.S_FCU_AP1", true)]
    [InlineData("system.switches.S_CDU2_KEY_PERF", true)]
    [InlineData("system.switches.S_CDU1_KEY_LSK1L", true)]
    [InlineData("aircraft.refuel.fuelTarget", false)]
    [InlineData("doors.entry.left.fwd", false)]
    [InlineData("system.analog.A_FCU_SPEED", false)]
    [InlineData("system.switches.S_OH_ADIRS_IR1", false)]
    [InlineData("", false)]
    public void WriteGate_AllowsOnlyFcuButtonsAndMcduKeys(string dataref, bool expected)
        => Assert.Equal(expected, VoiceCommandWriteGate.IsAllowed(dataref));

    [Fact]
    public void Parse_CommandOutsideGate_IsBlockedAndKeptOutOfAllowList()
    {
        var result = VoiceCommandConfigParser.Parse("""
            {
              "commands": [
                {
                  "phrases": [ "open the fuel valve" ],
                  "say": "done",
                  "steps": [ { "dataref": "aircraft.refuel.refuelingActive", "press": 1 } ]
                }
              ]
            }
            """);

        Assert.True(result.Set.TryResolve("open the fuel valve", out var resolved));
        Assert.NotNull(resolved.BlockReason);
        Assert.DoesNotContain("aircraft.refuel.refuelingActive", result.Set.WriteAllowList);
        Assert.Contains(result.Warnings, w => w.Contains("aircraft.refuel.refuelingActive", StringComparison.Ordinal));
    }

    [Fact]
    public void BlockedCommand_SpeaksRefusal_WritesNothing()
    {
        var service = CreateService("""
            {
              "commands": [
                {
                  "phrases": [ "open the fuel valve" ],
                  "steps": [ { "dataref": "aircraft.refuel.refuelingActive", "press": 1 } ]
                }
              ]
            }
            """);

        Assert.True(service.TryHandle("open the fuel valve"));
        WaitForDispatch(() => _arbiter.Requests.Count > 0);

        Assert.Empty(_dataRefs.Writes);
        var spoken = Assert.Single(_arbiter.Requests);
        Assert.Contains("not permitted", spoken.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExecutionValidatesEveryWriteAgainstTheLoadedAllowList()
    {
        // A hand-built set whose allow-list does NOT contain the step's dataref — the
        // execution-time check must refuse before any write reaches the aircraft.
        var command = new VoiceCommandDefinition
        {
            Phrases = ["sneaky"],
            Steps = [new VoiceCommandStep { Dataref = "system.switches.S_FCU_AP1", Press = 1 }],
        };
        var set = new VoiceCommandSet(
            [command],
            new Dictionary<string, ResolvedVoiceCommand>(StringComparer.Ordinal)
            {
                ["sneaky"] = new(command, null),
            },
            new HashSet<string>(StringComparer.Ordinal));
        var service = CreateService();
        service.Load(set);

        Assert.True(service.TryHandle("sneaky"));
        WaitForDispatch(() => _arbiter.Requests.Count > 0);

        Assert.Empty(_dataRefs.Writes);
    }

    // ---- execution ---------------------------------------------------------------------

    [Fact]
    public void StepCommand_WritesPressThenRestore_ThenSpeaks()
    {
        var service = CreateService("""
            {
              "commands": [
                {
                  "phrases": [ "autopilot one" ],
                  "say": "Autopilot one",
                  "steps": [ { "dataref": "system.switches.S_FCU_AP1", "press": 1, "restore": 0, "holdMs": 1 } ]
                }
              ]
            }
            """);

        Assert.True(service.TryHandle("autopilot one"));
        WaitForDispatch(() => _arbiter.Requests.Count > 0);

        (string, int)[] expectedWrites =
            [("system.switches.S_FCU_AP1", 1), ("system.switches.S_FCU_AP1", 0)];
        Assert.Equal(expectedWrites, _dataRefs.Writes.Select(w => (w.Name, (int)w.Value!)).ToArray());
        var spoken = Assert.Single(_arbiter.Requests);
        Assert.Equal("Autopilot one", spoken.Text);
        Assert.Equal("voice.command", spoken.Tag);
    }

    [Fact]
    public void MultiStepCommand_PressesKeysInFileOrder()
    {
        var service = CreateService("""
            {
              "commands": [
                {
                  "phrases": [ "copy active to secondary" ],
                  "say": "Active copied to secondary",
                  "steps": [
                    { "dataref": "system.switches.S_CDU2_KEY_SEC_FPLN", "press": 1, "restore": 0, "holdMs": 1, "delayMs": 1 },
                    { "dataref": "system.switches.S_CDU2_KEY_LSK1L",    "press": 1, "restore": 0, "holdMs": 1 }
                  ]
                }
              ]
            }
            """);

        Assert.True(service.TryHandle("copy active to secondary"));
        WaitForDispatch(() => _arbiter.Requests.Count > 0);

        Assert.Equal(
            [
                "system.switches.S_CDU2_KEY_SEC_FPLN", "system.switches.S_CDU2_KEY_SEC_FPLN",
                "system.switches.S_CDU2_KEY_LSK1L", "system.switches.S_CDU2_KEY_LSK1L",
            ],
            _dataRefs.Writes.Select(w => w.Name).ToArray());
    }

    [Fact]
    public void PlaceholderStep_SpeaksNotConfigured_WritesNothing()
    {
        var service = CreateService("""
            {
              "commands": [
                { "phrases": [ "mystery action" ], "steps": [ { "dataref": "«mcdu key»", "press": 1 } ] }
              ]
            }
            """);

        Assert.True(service.TryHandle("mystery action"));
        WaitForDispatch(() => _arbiter.Requests.Count > 0);

        Assert.Empty(_dataRefs.Writes);
        var spoken = Assert.Single(_arbiter.Requests);
        Assert.Contains("not configured", spoken.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownPhrase_IsNotConsumed()
    {
        var service = CreateService(ApOneJson);

        Assert.False(service.TryHandle("request a limousine"));
        Assert.Empty(_arbiter.Requests);
        Assert.Empty(_dataRefs.Writes);
    }

    // ---- spoken queries ----------------------------------------------------------------

    private const string AltimeterJson = """
        { "commands": [ { "phrases": [ "altimeter check" ], "say": "Altimeter, {altimeter}.", "steps": [] } ] }
        """;

    [Fact]
    public void Query_Altimeter_SpeaksHpaDigits()
    {
        _dataRefs.Values["system.gates.B_FCU_EFIS2_BARO_STD"] = false;
        _dataRefs.Values["system.switches.S_FCU_EFIS2_BARO_MODE"] = 1; // hPa
        _dataRefs.Values["system.numerical.N_FCU_EFIS2_BARO_HPA"] = 1013.0;
        var service = CreateService(AltimeterJson);

        Assert.True(service.TryHandle("altimeter check"));
        WaitForDispatch(() => _arbiter.Requests.Count > 0);

        var spoken = Assert.Single(_arbiter.Requests);
        Assert.Equal("Altimeter, one zero one three.", spoken.Text);
        Assert.Empty(_dataRefs.Writes); // a query never writes
    }

    [Fact]
    public void Query_Altimeter_OnStd_SaysStandard()
    {
        _dataRefs.Values["system.gates.B_FCU_EFIS2_BARO_STD"] = true;
        var service = CreateService(AltimeterJson);

        Assert.True(service.TryHandle("altimeter check"));
        WaitForDispatch(() => _arbiter.Requests.Count > 0);

        Assert.Equal("Altimeter, standard.", Assert.Single(_arbiter.Requests).Text);
    }

    [Fact]
    public void Query_VSpeeds_SpeaksMcduPerfValues()
    {
        _dataRefs.Values["aircraft.fms.perf.takeOff.v1"] = 135.0;
        _dataRefs.Values["aircraft.fms.perf.takeOff.vr"] = 138.0;
        _dataRefs.Values["aircraft.fms.perf.takeOff.v2"] = 141.0;
        var service = CreateService("""
            { "commands": [ { "phrases": [ "v speeds" ], "say": "V one {v1}, rotate {vr}, V two {v2}.", "steps": [] } ] }
            """);

        Assert.True(service.TryHandle("v speeds"));
        WaitForDispatch(() => _arbiter.Requests.Count > 0);

        Assert.Equal(
            "V one one three five, rotate one three eight, V two one four one.",
            Assert.Single(_arbiter.Requests).Text);
    }

    // ---- token formatting (pure) --------------------------------------------------------

    [Fact]
    public void Formatting_Altimeter_InHgReadsHundredths()
        => Assert.Equal("two niner niner two",
            SpokenValueFormatting.Altimeter(std: false, hpaMode: false, hpa: 0, inches: 29.92));

    [Fact]
    public void Formatting_Altimeter_NoData_IsUnavailable()
        => Assert.Equal("unavailable",
            SpokenValueFormatting.Altimeter(std: null, hpaMode: true, hpa: 0, inches: 0));

    [Fact]
    public void Formatting_Speed_ZeroIsNotSet_NullIsUnavailable()
    {
        Assert.Equal("not set", SpokenValueFormatting.Speed(0));
        Assert.Equal("unavailable", SpokenValueFormatting.Speed(null));
        Assert.Equal("one four zero", SpokenValueFormatting.Speed(140));
    }

    [Theory]
    [InlineData("16R", "one six right")]
    [InlineData("34L", "three four left")]
    [InlineData("09C", "zero niner center")]
    [InlineData("27", "two seven")]
    [InlineData(null, "unavailable")]
    [InlineData("  ", "unavailable")]
    public void Formatting_RunwayToWords(string? runway, string expected)
        => Assert.Equal(expected, SpokenValueFormatting.Runway(runway));

    [Fact]
    public void Formatting_ApplyTokens_LeavesUnknownBracesAlone()
    {
        var values = CommandTokenValues.Unavailable with { V1 = "one two three" };

        Assert.Equal(
            "V one one two three {mystery}",
            SpokenValueFormatting.ApplyTokens("V one {v1} {mystery}", values));
    }
}
