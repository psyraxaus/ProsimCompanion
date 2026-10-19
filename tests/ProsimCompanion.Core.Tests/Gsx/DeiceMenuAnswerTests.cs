using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Tests.Speech;
using ProsimCompanion.Gsx;
using ProsimCompanion.Gsx.Menu;
using ProsimCompanion.Gsx.Mirror;
using ProsimCompanion.Gsx.Protocol;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>
/// Issue #166 (EFHK 2026-10-10): GSX's "Select fluid type" / "Select concentration" menus
/// had no handler (the catalogue knew only the combined "Select de-icing type"), so GSX
/// sprayed its default while the holdover card armed with the configured fluid; and the
/// "Ice warning" offer was answered Yes against the captain's declined question.
/// </summary>
public sealed class DeiceMenuAnswerTests : IDisposable
{
    private readonly FakeGsxApi _api = new();
    private readonly FakePhaseSource _phases = new();
    private readonly GsxDiagnosticsStore _diagnostics = new();
    private readonly JsonlEventLog _eventLog = SpeechTestSupport.TempEventLog();
    private readonly GsxQuestionDispatcher _dispatcher = new(NullLogger<GsxQuestionDispatcher>.Instance);
    private readonly GroundOpsSignals _signals = new();
    private readonly DeiceRequestStore _deice;
    private readonly DeiceActivityStore _activity;
    private readonly GsxOptions _options = new() { AutoDeIce = true, DeIceFluidType = "Type IV", DeIceConcentration = "75" };

    public DeiceMenuAnswerTests()
    {
        _deice = new DeiceRequestStore(_signals);
        _activity = new DeiceActivityStore(_signals);
        var options = new FakeOptionsMonitor(_options);
        var executor = new GsxMenuIntentExecutor(
            _api,
            new GsxMenuOpener(_api, Moq.Mock.Of<ISimVars>(), NullLogger<GsxMenuOpener>.Instance),
            options,
            NullLogger<GsxMenuIntentExecutor>.Instance);
        var catalog = new GsxQuestionCatalog(
            _api, executor, options, _diagnostics, _phases, _eventLog,
            NullLogger<GsxQuestionCatalog>.Instance, deiceRequest: _deice, deiceActivity: _activity);
        catalog.RegisterAll(_dispatcher);

        _api.OnCommand = (verb, _) =>
        {
            if (verb == "menu.pick")
            {
                _api.Mirror.ApplyState("menuShown", JsonValue.Create(false)); // GSX dismissed the menu
            }

            return new GsxCommandResult(true, "ok", null, null);
        };
    }

    public void Dispose()
    {
        _dispatcher.Dispose();
        _deice.Dispose();
        _activity.Dispose();
        _eventLog.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private Task ShowAsync(string title, params string[] entries)
    {
        _api.Mirror.ApplyState("menu", new JsonObject
        {
            ["title"] = title,
            ["entries"] = new JsonArray([.. entries.Select(e => JsonValue.Create(e))]),
        });
        _api.Mirror.ApplyState("menuShown", JsonValue.Create(true));
        return _dispatcher.OnMenuUpdatedAsync(true, title);
    }

    private int? PickedIndex() => _api.Commands.Where(c => c.Verb == "menu.pick").Select(c => (int?)c.Args?["index"]?.GetValue<int>()).LastOrDefault();

    // ---- Pure matchers ----

    [Theory]
    [InlineData("Type IV", "Type IV", true)]
    [InlineData("Type IV", "Type IV (100%)", true)]
    [InlineData("Type IV", "Type I", false)]
    [InlineData("Type IV", "Type II", false)]
    [InlineData("Type I", "Type I", true)]
    [InlineData("Type I", "Type II", false)]
    [InlineData("Type I", "Type IV", false)]
    [InlineData("Type II", "Type II", true)]
    [InlineData("Type II", "Type III", false)]
    [InlineData("Type II", "type ii fluid", true)]
    public void FluidTypePattern_MatchesTheRomanNumeralOnAWordBoundary(string configured, string entry, bool expected)
        => Assert.Equal(expected, GsxQuestionCatalog.FluidTypePattern(configured).IsMatch(entry));

    [Theory]
    [InlineData("75", "75%", true)]
    [InlineData("75", "75 %", true)]
    [InlineData("75", "75/25", true)]
    [InlineData("75", "Type IV 75%", true)]
    [InlineData("75", "175%", false)]
    [InlineData("75", "100%", false)]
    [InlineData("100", "100%", true)]
    [InlineData("100", "1000", false)]
    public void ConcentrationPattern_MatchesTheFigure_NotInsideALongerNumber(string configured, string entry, bool expected)
        => Assert.Equal(expected, GsxQuestionCatalog.ConcentrationPattern(configured).IsMatch(entry));

    [Theory]
    [InlineData("Type I", 1)]
    [InlineData("Type II (75%)", 2)]
    [InlineData("Type III", 3)]
    [InlineData("Type IV", 4)]
    [InlineData("Heated water", null)]
    public void FluidTypeOf_ReadsTheEntry(string entry, int? expected)
        => Assert.Equal(expected, GsxQuestionCatalog.FluidTypeOf(entry));

    [Theory]
    [InlineData("75%", 75)]
    [InlineData("100 %", 100)]
    [InlineData("50/50 mix", 50)]
    [InlineData("Type IV", null)]
    public void ConcentrationOf_ReadsTheEntry(string entry, int? expected)
        => Assert.Equal(expected, GsxQuestionCatalog.ConcentrationOf(entry));

    // ---- Ice warning obeys the verdict ----

    [Fact]
    public void IceWarningAnswer_FollowsTheDecision()
    {
        var now = DateTimeOffset.UtcNow;
        var open = (DeiceRequestSnapshot.Empty with { Verdict = DeicePolicyVerdict.Ask }).WithAsking(CaptainsQuestionState.Empty with
        {
            Question = new CaptainsQuestion("deice", now, "Request it?", DeiceRequestSnapshot.QuestionTtl, 1, [], null),
        });
        Assert.Null(GsxQuestionCatalog.IceWarningAnswer(open, now));
        Assert.False(GsxQuestionCatalog.IceWarningAnswer(open.WithAsking(CaptainsQuestionRules.Decline(open.Asking, "voice", "captain said 'negative'", now)), now));
        Assert.True(GsxQuestionCatalog.IceWarningAnswer(open.WithAsking(CaptainsQuestionRules.Accept(open.Asking, "voice", now)), now));
        Assert.True(GsxQuestionCatalog.IceWarningAnswer(DeiceRequestSnapshot.Empty with { Verdict = DeicePolicyVerdict.Off }, now));
        Assert.True(GsxQuestionCatalog.IceWarningAnswer(DeiceRequestSnapshot.Empty with { Verdict = DeicePolicyVerdict.NotRequired }, now));
    }

    [Fact]
    public async Task IceWarning_AfterADecline_AnswersNo_AndCloses()
    {
        _deice.Publish(DeicePolicyVerdict.Ask, "snow", -2, "snow", "EFHK", "Request it?");
        _deice.Decline("voice", "captain said 'negative'");

        await ShowAsync("Ice warning: do you request the de-icing treatment?", "Yes", "No");

        Assert.Equal(1, PickedIndex());
        Assert.Contains(_api.Commands, c => c.Verb == "menu.close");
        Assert.Contains(_diagnostics.Snapshot().RecentDecisions, d => d.Action == "de-icing offer" && d.Reason.Contains("captain declined", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IceWarning_WhileTheQuestionStands_IsLeftForTheUser()
    {
        _deice.Publish(DeicePolicyVerdict.Ask, "snow", -2, "snow", "EFHK", "Request it?");

        await ShowAsync("Ice warning: do you request the de-icing treatment?", "Yes", "No");

        Assert.DoesNotContain(_api.Commands, c => c.Verb == "menu.pick");
        Assert.Contains(_diagnostics.Snapshot().RecentDecisions, d => d.Action == "de-icing offer" && d.Reason.Contains("still open", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IceWarning_WhenAccepted_AnswersYes()
    {
        _deice.Publish(DeicePolicyVerdict.Ask, "snow", -2, "snow", "EFHK", "Request it?");
        _deice.Accept("web");

        await ShowAsync("Ice warning: do you request the de-icing treatment?", "Yes", "No");

        Assert.Equal(0, PickedIndex());
    }

    [Fact]
    public async Task IceWarning_WithAutoDeIceOff_IsLeftForTheUser_EvenWhenAccepted()
    {
        _options.AutoDeIce = false;
        _deice.Accept("web");

        await ShowAsync("Ice warning: do you request the de-icing treatment?", "Yes", "No");

        Assert.DoesNotContain(_api.Commands, c => c.Verb == "menu.pick");
    }

    // ---- The two fluid menus ----

    [Fact]
    public async Task FluidTypeMenu_PicksTheConfiguredType_AndRecordsIt()
    {
        await ShowAsync("Select fluid type", "Type I", "Type II", "Type IV");

        Assert.Equal(2, PickedIndex());
        Assert.Equal(4, _activity.Snapshot().MenuFluidType);
        Assert.Contains(_diagnostics.Snapshot().RecentDecisions, d => d.Action == "de-icing fluidType menu" && d.Reason.Contains("picked 'Type IV'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConcentrationMenu_PicksTheConfiguredFigure_AndRecordsIt()
    {
        await ShowAsync("Select concentration", "100%", "75%", "50%");

        Assert.Equal(1, PickedIndex());
        Assert.Equal(75, _activity.Snapshot().MenuConcentration);
    }

    [Fact]
    public async Task FluidMenus_AreAnswered_WhenTheAppRequestedDeicing_EvenWithAutoDeIceOff()
    {
        _options.AutoDeIce = false;
        _deice.Publish(DeicePolicyVerdict.Request, "snow", -2, "snow", "EFHK", "Request it?");

        await ShowAsync("Select fluid type", "Type I", "Type IV");

        Assert.Equal(1, PickedIndex());
    }

    [Fact]
    public async Task FluidMenus_AreLeftForTheUser_WhenNeitherAutoDeIceNorTheAppAsked()
    {
        _options.AutoDeIce = false;

        await ShowAsync("Select fluid type", "Type I", "Type IV");

        Assert.DoesNotContain(_api.Commands, c => c.Verb == "menu.pick");
        Assert.Null(_activity.Snapshot().MenuFluidType);
    }

    [Fact]
    public async Task FluidTypeMenu_WithNoMatchingEntry_IsLeftForTheUser_NamingTheEntries()
    {
        await ShowAsync("Select fluid type", "Type I", "Type II");

        Assert.DoesNotContain(_api.Commands, c => c.Verb == "menu.pick");
        Assert.Contains(_diagnostics.Snapshot().RecentDecisions, d => d.Action == "de-icing fluidType menu" && d.Reason.Contains("[Type I | Type II]", StringComparison.Ordinal));
    }

    private sealed class FakeGsxApi : IGsxRemoteApi
    {
#pragma warning disable CS0067
        public event Action<GsxReadiness>? ReadinessChanged;
#pragma warning restore CS0067

        public GsxReadiness Readiness { get; set; } = GsxReadiness.Ready;
        public GsxStateMirror Mirror { get; } = new();
        public List<(string Verb, JsonObject? Args)> Commands { get; } = [];
        public Func<string, JsonObject?, GsxCommandResult>? OnCommand { get; set; }

        public bool HasCapability(string token) => true;

        public Task<GsxCommandResult> SendCommandAsync(string verb, JsonObject? args, CancellationToken cancellationToken = default)
        {
            Commands.Add((verb, args));
            return Task.FromResult(OnCommand?.Invoke(verb, args) ?? new GsxCommandResult(true, "ok", null, null));
        }
    }

    private sealed class FakeOptionsMonitor(GsxOptions value) : IOptionsMonitor<GsxOptions>
    {
        public GsxOptions CurrentValue { get; } = value;
        public GsxOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<GsxOptions, string?> listener) => null;
    }
}
