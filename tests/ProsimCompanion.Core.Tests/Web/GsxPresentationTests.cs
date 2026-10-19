using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Tests.Localization;
using ProsimCompanion.Web.Components;
using Xunit;

namespace ProsimCompanion.Core.Tests.Web;

/// <summary>
/// The GSX status panel's presentation module — the second adapter of the status-row seam
/// (architecture review candidate 6). Pins the rows, lamps and text ids of the Status,
/// Arrival Gate, Departure Board and De-Icing sections and the three diagnostics tables.
/// </summary>
public sealed class GsxPresentationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 10, 45, 0, TimeSpan.Zero);

    private static readonly IStringLocalizer T = new ServiceCollection().AddTestLocalizer()
        .BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IStringLocalizer>();

    private static readonly Regex Clock = new(@"^\d\d:\d\d:\d\d$", RegexOptions.CultureInvariant);
    private static readonly Regex ClockMillis = new(@"^\d\d:\d\d:\d\d\.\d\d\d$", RegexOptions.CultureInvariant);

    // ---- stage vocabulary (shared with the header's NEXT SERVICE pill) ----

    [Theory]
    [InlineData(GsxServiceStage.Waiting, "Waiting", "gsxSettings.stage.waiting", "lamp lamp-off")]
    [InlineData(GsxServiceStage.Held, "Holding", "gsxSettings.stage.held", "lamp lamp-amber")]
    [InlineData(GsxServiceStage.Skipped, "Skipped", "gsxSettings.stage.skipped", "lamp lamp-dark")]
    [InlineData(GsxServiceStage.Called, "Called", "gsxSettings.stage.called", "lamp lamp-amber lamp-pulse")]
    [InlineData(GsxServiceStage.Requested, "Requested", "gsxSettings.stage.requested", "lamp lamp-amber lamp-pulse")]
    [InlineData(GsxServiceStage.Active, "Active", "gsxSettings.stage.active", "lamp lamp-green lamp-pulse")]
    [InlineData(GsxServiceStage.Completed, "Completed", "gsxSettings.stage.completed", "lamp lamp-green")]
    public void Stage_WordIdAndLamp(GsxServiceStage stage, string label, string textId, string lamp)
    {
        Assert.Equal(label, GsxPresentation.StageLabel(stage));
        Assert.Equal(textId, GsxPresentation.StageTextId(stage));
        Assert.Equal(label, T[textId].Value);     // the English pack word equals the header's English label
        Assert.Equal(lamp, GsxPresentation.StageLamp(stage));
    }

    // ---- Status section ----

    [Fact]
    public void StatusRows_ReadinessPillThenTheMonoFacts()
    {
        var view = GsxDiagnosticsSnapshot.Empty with
        {
            Readiness = "Ready",
            AutomationPhase = "Departure",
            Capabilities = ["services", "menu"],
            AirportIcao = "EGLL",
            GateContextKey = "EGLL/B12",
            StartupSid = "sid-7",
        };

        var rows = GsxPresentation.StatusRows(view);

        Assert.Equal(["readiness", "automationPhase", "capabilities", "airport", "gateContext", "session"], rows.Select(r => r.Id));
        Assert.Equal(["gsxSettings.status.readiness", "gsxSettings.status.automationPhase", "gsxSettings.status.capabilities", "gsxSettings.status.airport", "gsxSettings.status.gateContext", "gsxSettings.status.session"],
            rows.Select(r => r.LabelTextId));
        Assert.Equal(StatusRowKind.Pill, rows[0].Kind);
        Assert.Equal("tone-ok", rows[0].Tone);
        Assert.Equal("Ready", rows[0].Value.Literal);
        Assert.All(rows.Skip(1), r => Assert.Equal(StatusRowKind.Value, r.Kind));
        Assert.Equal("Departure", rows[1].Value.Literal);
        Assert.Equal("services, menu", rows[2].Value.Literal);
        Assert.Equal("EGLL", rows[3].Value.Literal);
        Assert.Equal("EGLL/B12", rows[4].Value.Literal);
        Assert.Equal("sid-7", rows[5].Value.Literal);
    }

    [Fact]
    public void StatusRows_Empty_IsDashesAndABadPill()
    {
        var rows = GsxPresentation.StatusRows(GsxDiagnosticsSnapshot.Empty);

        Assert.Equal("tone-bad", rows[0].Tone);
        Assert.Equal("Disconnected", rows[0].Value.Literal);
        Assert.Equal("Inactive", rows[1].Value.Literal);
        Assert.All(rows.Skip(2), r => Assert.Equal("—", r.Value.Resolve(T)));
    }

    [Theory]
    [InlineData("Ready", "tone-ok")]
    [InlineData("ConnectedGsxNotRunning", "tone-warn")]
    [InlineData("Disconnected", "tone-bad")]
    [InlineData("anything else", "tone-bad")]
    public void ReadinessTone_Table(string readiness, string tone)
        => Assert.Equal(tone, GsxPresentation.ReadinessTone(readiness));

    // ---- Arrival gate ----

    [Fact]
    public void GateRequest_TextOrTheNoRequestLine()
    {
        Assert.Equal("gsxSettings.arrivalGate.noRequest", GsxPresentation.GateRequest(GsxDiagnosticsSnapshot.Empty).TextId);
        Assert.Equal("B12: Confirmed — confirmed as B12", GsxPresentation.GateRequest(GsxDiagnosticsSnapshot.Empty with { GateRequest = "B12: Confirmed — confirmed as B12" }).Literal);
    }

    // The gate suffix-variant buttons are the gate instance of the open-question card (C2):
    // see CaptainsQuestionPresentationTests.

    // ---- Departure board ----

    [Fact]
    public void DepartureBoard_ButtonsFollowTheSequenceState()
    {
        var idle = GsxPresentation.DepartureBoard(GsxDiagnosticsSnapshot.Empty, started: false, complete: false);
        Assert.Equal("gsxSettings.departureBoard.start", idle.StartTextId);
        Assert.False(idle.StartDisabled);
        Assert.True(idle.CallNextDisabled);
        Assert.Null(idle.SimbriefVerdict);
        Assert.Empty(idle.Rows);

        var running = GsxPresentation.DepartureBoard(GsxDiagnosticsSnapshot.Empty, true, false);
        Assert.Equal("gsxSettings.departureBoard.running", running.StartTextId);
        Assert.True(running.StartDisabled);
        Assert.False(running.CallNextDisabled);

        var complete = GsxPresentation.DepartureBoard(GsxDiagnosticsSnapshot.Empty, true, true);
        Assert.Equal("gsxSettings.departureBoard.complete", complete.StartTextId);
        Assert.True(complete.StartDisabled);
        Assert.True(complete.CallNextDisabled);
    }

    [Fact]
    public void DepartureBoard_RowsCarryLampStageAndHoldReason()
    {
        var view = GsxDiagnosticsSnapshot.Empty with
        {
            ServiceBoard =
            [
                new GsxServiceBoardRow("Refueling", GsxServiceStage.Completed, null),
                new GsxServiceBoardRow("Boarding", GsxServiceStage.Held, "waiting for the fuel confirmation"),
            ],
        };

        var board = GsxPresentation.DepartureBoard(view, true, false);

        Assert.Equal(["Refueling", "Boarding"], board.Rows.Select(r => r.ServiceId));
        Assert.Equal(["lamp lamp-green", "lamp lamp-amber"], board.Rows.Select(r => r.Lamp));
        Assert.Equal(["gsxSettings.stage.completed", "gsxSettings.stage.held"], board.Rows.Select(r => r.StageTextId));
        Assert.Null(board.Rows[0].Detail);
        Assert.Equal("waiting for the fuel confirmation", board.Rows[1].Detail);
    }

    [Fact]
    public void DepartureBoard_SimbriefVerdict_IssueOneSevenFour()
    {
        var normal = GsxPresentation.DepartureBoard(GsxDiagnosticsSnapshot.Empty with { SimbriefReload = new GsxSimbriefReloadView(T0, "Reloaded", "handler data patched", false) }, false, false);
        Assert.Equal("Reloaded — handler data patched", normal.SimbriefVerdict);

        var missing = GsxPresentation.DepartureBoard(GsxDiagnosticsSnapshot.Empty with { SimbriefReload = new GsxSimbriefReloadView(T0, "NotPerformed", "no SimBrief row on any page", true) }, false, false);
        Assert.Equal("no SimBrief row on any page", missing.SimbriefVerdict);
    }

    [Theory]
    [InlineData(GsxSimbriefReloadStatus.Reloaded, "gsxSettings.departureBoard.reloaded")]
    [InlineData(GsxSimbriefReloadStatus.SentUnconfirmed, "gsxSettings.departureBoard.sent")]
    [InlineData(GsxSimbriefReloadStatus.NotPerformed, "gsxSettings.departureBoard.notPerformed")]
    public void ReloadOutcome_PicksTheLine(GsxSimbriefReloadStatus status, string textId)
    {
        var line = GsxPresentation.ReloadOutcome(new GsxSimbriefReloadOutcome(status, "detail here"));

        Assert.Equal(textId, line.TextId);
        Assert.Equal("detail here", line.Arguments[0]);
    }

    // ---- De-icing ----

    [Theory]
    [InlineData(DeicePolicyVerdict.Pending, "gsxSettings.deice.verdict.pending", "lamp lamp-off")]
    [InlineData(DeicePolicyVerdict.Off, "gsxSettings.deice.verdict.off", "lamp lamp-off")]
    [InlineData(DeicePolicyVerdict.NoData, "gsxSettings.deice.verdict.noData", "lamp lamp-amber")]
    [InlineData(DeicePolicyVerdict.NotRequired, "gsxSettings.deice.verdict.notRequired", "lamp lamp-dark")]
    [InlineData(DeicePolicyVerdict.Ask, "gsxSettings.deice.verdict.ask", "lamp lamp-amber lamp-pulse")]
    [InlineData(DeicePolicyVerdict.Request, "gsxSettings.deice.verdict.requested", "lamp lamp-green")]
    public void Deice_VerdictWordAndLamp(DeicePolicyVerdict verdict, string textId, string lamp)
    {
        var deice = DeiceRequestSnapshot.Empty with { Verdict = verdict };

        var view = GsxPresentation.Deice(deice, GsxDiagnosticsSnapshot.Empty, T0);

        Assert.Equal(textId, view.VerdictTextId);
        Assert.Equal(lamp, view.Lamp);
        Assert.Equal("not evaluated yet", view.Reason);
        Assert.Null(view.Question);
        Assert.Equal("—", view.Lvars.Resolve(T));
    }

    [Fact]
    public void Deice_TheCaptainsAnswerBeatsTheVerdict()
    {
        var asked = DeiceRequestSnapshot.Empty with { Verdict = DeicePolicyVerdict.Ask };
        Assert.Equal("gsxSettings.deice.verdict.requested", GsxPresentation.DeiceVerdictTextId(asked.WithAsking(CaptainsQuestionRules.Accept(asked.Asking, "web", T0))));
        Assert.Equal("gsxSettings.deice.verdict.declined", GsxPresentation.DeiceVerdictTextId(asked.WithAsking(CaptainsQuestionRules.Decline(asked.Asking, "web", "captain said no", T0))));
    }

    [Fact]
    public void Deice_InputsLine()
    {
        Assert.Equal("OAT -2 °C · snow · EGLL", GsxPresentation.DeiceInputs(DeiceRequestSnapshot.Empty with { OatC = -2.4, Precip = "snow", Icao = "EGLL" }));
        Assert.Equal("OAT — · unknown · —", GsxPresentation.DeiceInputs(DeiceRequestSnapshot.Empty));
    }

    [Fact]
    public void Deice_QuestionShowsWhileOpen_WithItsAttemptAndExpiry()
    {
        var question = new CaptainsQuestion("deice", T0, "Request de-icing?", TimeSpan.FromMinutes(5), 1, [], null);
        var asked = (DeiceRequestSnapshot.Empty with { Verdict = DeicePolicyVerdict.Ask }).WithAsking(CaptainsQuestionState.Empty with { Question = question });

        var open = GsxPresentation.Deice(asked, GsxDiagnosticsSnapshot.Empty, T0.AddMinutes(1));
        Assert.Equal("Request de-icing?", open.Question!.Prompt);
        Assert.Equal("gsxSettings.deiceStatus.questionState", open.Question.State.TextId);
        Assert.Equal("gsxSettings.deiceStatus.open", ((DisplayText)open.Question.State.Arguments[0]).TextId);
        Assert.Matches(@"^\d\d:\d\d$", (string)open.Question.State.Arguments[1]);
        Assert.StartsWith("open · counts as no at ", open.Question.State.Resolve(T), StringComparison.Ordinal);

        var reAsked = GsxPresentation.Deice(asked.WithAsking(asked.Asking with { Question = question with { Attempt = 2 } }), GsxDiagnosticsSnapshot.Empty, T0.AddMinutes(1));
        Assert.Equal("gsxSettings.deiceStatus.reAsked", ((DisplayText)reAsked.Question!.State.Arguments[0]).TextId);

        var expired = GsxPresentation.Deice(asked, GsxDiagnosticsSnapshot.Empty, T0.AddMinutes(6));
        Assert.Null(expired.Question);
    }

    [Fact]
    public void Deice_LvarLine_OnceSimConnectHasReported()
    {
        var view = GsxPresentation.Deice(DeiceRequestSnapshot.Empty,
            GsxDiagnosticsSnapshot.Empty with { DeiceLvars = new GsxDeiceLvarView(T0, 5, "Active", 2) }, T0);

        Assert.Equal("gsxSettings.deiceStatus.lvarsValue", view.Lvars.TextId);
        Assert.Equal("Active", view.Lvars.Arguments[0]);
        Assert.Equal(2, view.Lvars.Arguments[1]);
        Assert.Equal("state Active, fluid type 2", view.Lvars.Resolve(T));
    }

    // ---- diagnostics tables ----

    [Fact]
    public void ServiceTable_OrderedById_WithTheLatchedStage()
    {
        var view = GsxDiagnosticsSnapshot.Empty with
        {
            Services =
            [
                new GsxServiceView("Refueling", "Refuel", null, "Available", true, false, null, GsxServiceStage.Completed),
                new GsxServiceView("Boarding", "Boarding", "requested", "Requested", false, true, "12 / 150", GsxServiceStage.Requested),
            ],
        };

        var rows = GsxPresentation.ServiceTable(view);

        Assert.Equal(["Boarding", "Refuel"], rows.Select(r => r.DisplayName));
        Assert.Equal("requested", rows[0].WireState);
        Assert.True(rows[0].Waiting);
        Assert.Equal("Requested", rows[0].MappedState);
        Assert.Equal("gsxSettings.stage.requested", rows[0].StageTextId);
        Assert.False(rows[0].CanTrigger);
        Assert.Equal("12 / 150", rows[0].ProgressText);
        Assert.Equal("—", rows[1].WireState);
        Assert.False(rows[1].Waiting);
        Assert.Equal("gsxSettings.stage.completed", rows[1].StageTextId);    // latched completed over mapped "Available" (#29)
        Assert.True(rows[1].CanTrigger);
        Assert.Equal("", rows[1].ProgressText);
    }

    [Fact]
    public void Decisions_AndCommands_FormatTheClockAndToneTheOutcome()
    {
        var view = GsxDiagnosticsSnapshot.Empty with
        {
            RecentDecisions = [new GsxDecisionView(T0, "call Boarding", "fuel confirmed")],
            RecentCommands = [new GsxCommandView(T0, "trigger", "Boarding", true, "OK"), new GsxCommandView(T0, "menu", "open", false, "TIMEOUT")],
        };

        var decisions = GsxPresentation.Decisions(view);
        Assert.Single(decisions);
        Assert.Matches(Clock, decisions[0].Time);
        Assert.Equal("call Boarding", decisions[0].Action);
        Assert.Equal("fuel confirmed", decisions[0].Reason);

        var commands = GsxPresentation.Commands(view);
        Assert.Equal(2, commands.Count);
        Assert.Matches(ClockMillis, commands[0].Time);
        Assert.Equal("trigger", commands[0].Verb);
        Assert.Equal("Boarding", commands[0].Args);
        Assert.Equal("tone-ok", commands[0].Tone);
        Assert.Equal("OK", commands[0].Code);
        Assert.Equal("tone-bad", commands[1].Tone);
        Assert.Equal("TIMEOUT", commands[1].Code);
    }
}
