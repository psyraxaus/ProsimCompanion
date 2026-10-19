using System.Globalization;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Web.Components;

/// <summary>The departure board's controls and rows.</summary>
/// <param name="StartTextId">The start button's word: Start / Running / Complete.</param>
/// <param name="StartDisabled">Once started the button is inert (the sequence is idempotent anyway).</param>
/// <param name="CallNextDisabled">Call next needs a running, unfinished sequence.</param>
/// <param name="SimbriefVerdict">The last SimBrief reload verdict line (issue #174), or null before one.</param>
/// <param name="Rows">The service rows in configured order; empty shows the "no sequence" line.</param>
public sealed record DepartureBoardView(
    string StartTextId,
    bool StartDisabled,
    bool CallNextDisabled,
    string? SimbriefVerdict,
    IReadOnlyList<BoardRowView> Rows);

/// <summary>One departure-board row: the service, its lamp, its stage word and the hold reason.</summary>
public sealed record BoardRowView(string ServiceId, string Lamp, string StageTextId, string? Detail);

/// <summary>The de-icing section (2026-10-09): the verdict row, the inputs line, the open
/// question (null when none stands) and the raw LVAR line.</summary>
public sealed record DeiceView(
    string Lamp,
    string VerdictTextId,
    string Reason,
    string Inputs,
    DeiceQuestionView? Question,
    DisplayText Lvars);

/// <summary>An open de-icing question: the FO's words and the "open / re-asked · counts as no at" state line.</summary>
public sealed record DeiceQuestionView(string Prompt, DisplayText State);

/// <summary>One line of the raw services table.</summary>
public sealed record ServiceTableRow(
    string DisplayName,
    string WireState,
    bool Waiting,
    string MappedState,
    string StageTextId,
    bool CanTrigger,
    string ProgressText);

/// <summary>One automation decision line, time already local.</summary>
public sealed record DecisionRowView(string Time, string Action, string Reason);

/// <summary>One recent-command line, time already local, the result pill toned by outcome.</summary>
public sealed record CommandRowView(string Time, string Verb, string Args, string Tone, string Code);

/// <summary>
/// The GSX status panel's presentation module (the landing section of the GSX tab, #18):
/// diagnostics and de-ice snapshots in, row and label records out; the panel's
/// markup renders them and keeps only its controls. The stage wording and lamps are shared
/// with the header's NEXT SERVICE pill and the Flight Status services card. Second adapter
/// of the seam <see cref="FlightStatusPresentation"/> opened — two adapters make it real.
/// </summary>
public static class GsxPresentation
{
    /// <summary>The English stage word. Kept beside <see cref="StageTextId"/> for the callers
    /// that build a one-line English summary (the header's NEXT SERVICE pill); a page that
    /// draws the word localises it through <c>T[StageTextId(stage)]</c> (ADR-0014).</summary>
    public static string StageLabel(GsxServiceStage stage) => stage switch
    {
        GsxServiceStage.Waiting => "Waiting",
        GsxServiceStage.Held => "Holding",
        GsxServiceStage.Skipped => "Skipped",
        GsxServiceStage.Called => "Called",
        GsxServiceStage.Requested => "Requested",
        GsxServiceStage.Active => "Active",
        GsxServiceStage.Completed => "Completed",
        _ => stage.ToString(),
    };

    /// <summary>The pack id of the stage word (<c>gsxSettings.stage.*</c>); the English text
    /// behind each id equals <see cref="StageLabel"/>.</summary>
    public static string StageTextId(GsxServiceStage stage) => stage switch
    {
        GsxServiceStage.Held => "gsxSettings.stage.held",
        GsxServiceStage.Skipped => "gsxSettings.stage.skipped",
        GsxServiceStage.Called => "gsxSettings.stage.called",
        GsxServiceStage.Requested => "gsxSettings.stage.requested",
        GsxServiceStage.Active => "gsxSettings.stage.active",
        GsxServiceStage.Completed => "gsxSettings.stage.completed",
        _ => "gsxSettings.stage.waiting",
    };

    /// <summary>Annunciator-lamp class per stage: off while waiting, amber for held, pulsing
    /// amber while called/requested (in motion), pulsing green while active, steady green when
    /// completed, dark for skipped.</summary>
    public static string StageLamp(GsxServiceStage stage) => stage switch
    {
        GsxServiceStage.Completed => "lamp lamp-green",
        GsxServiceStage.Active => "lamp lamp-green lamp-pulse",
        GsxServiceStage.Called or GsxServiceStage.Requested => "lamp lamp-amber lamp-pulse",
        GsxServiceStage.Held => "lamp lamp-amber",
        GsxServiceStage.Skipped => "lamp lamp-dark",
        _ => "lamp lamp-off",
    };

    public static string ReadinessTone(string readiness) => readiness switch
    {
        "Ready" => "tone-ok",
        "ConnectedGsxNotRunning" => "tone-warn",
        _ => "tone-bad",
    };

    // ── Status section ────────────────────────────────────────────────────────────────

    /// <summary>The Status section, top to bottom: the readiness pill, then the
    /// connection/mirror facts as monospace values.</summary>
    public static IReadOnlyList<StatusRow> StatusRows(GsxDiagnosticsSnapshot view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return
        [
            StatusRow.Pill("readiness", "gsxSettings.status.readiness", ReadinessTone(view.Readiness), DisplayText.Raw(view.Readiness)),
            StatusRow.Mono("automationPhase", "gsxSettings.status.automationPhase", view.AutomationPhase),
            StatusRow.Mono("capabilities", "gsxSettings.status.capabilities", view.Capabilities.Count == 0 ? null : string.Join(", ", view.Capabilities)),
            StatusRow.Mono("airport", "gsxSettings.status.airport", view.AirportIcao),
            StatusRow.Mono("gateContext", "gsxSettings.status.gateContext", view.GateContextKey),
            StatusRow.Mono("session", "gsxSettings.status.session", view.StartupSid),
        ];
    }

    // ── Arrival gate ──────────────────────────────────────────────────────────────────

    /// <summary>The armed/last gate request summary, or the "no request" line.</summary>
    public static DisplayText GateRequest(GsxDiagnosticsSnapshot view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return DisplayText.RawOrNull(view.GateRequest) ?? DisplayText.Id("gsxSettings.arrivalGate.noRequest");
    }

    // The suffix-variant buttons (issue #156) are the gate instance of the open-question card
    // (CaptainsQuestionPresentation, C2) — pick one there, on the OFP page, or by telling the
    // FO; the first goes out at taxi-in otherwise.

    // ── Departure board ───────────────────────────────────────────────────────────────

    /// <summary>The board: button words and states from the departure control, the SimBrief
    /// verdict (issue #174 — RowMissing means this GSX shows no SimBrief row on any page the
    /// walk read, said once per app session and never retried for that plan), and one row per
    /// configured service with its lamp. Hold reasons are full sentences — the markup's
    /// row-with-detail track ellipsizes them; the hover title carries the whole thing.</summary>
    public static DepartureBoardView DepartureBoard(GsxDiagnosticsSnapshot view, bool started, bool complete)
    {
        ArgumentNullException.ThrowIfNull(view);
        var simbrief = view.SimbriefReload is { } reload
            ? reload.RowMissing ? reload.Detail : $"{reload.Status} — {reload.Detail}"
            : null;
        return new DepartureBoardView(
            StartTextId: complete ? "gsxSettings.departureBoard.complete"
                : started ? "gsxSettings.departureBoard.running"
                : "gsxSettings.departureBoard.start",
            StartDisabled: started,
            CallNextDisabled: !started || complete,
            SimbriefVerdict: simbrief,
            Rows: [.. view.ServiceBoard.Select(row => new BoardRowView(row.ServiceId, StageLamp(row.Stage), StageTextId(row.Stage), row.Detail))]);
    }

    /// <summary>The manual SimBrief reload's outcome line (2026-10-09), shown under the
    /// buttons until the next click.</summary>
    public static DisplayText ReloadOutcome(GsxSimbriefReloadOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return outcome.Status switch
        {
            GsxSimbriefReloadStatus.Reloaded => DisplayText.Id("gsxSettings.departureBoard.reloaded", outcome.Detail),
            GsxSimbriefReloadStatus.SentUnconfirmed => DisplayText.Id("gsxSettings.departureBoard.sent", outcome.Detail),
            _ => DisplayText.Id("gsxSettings.departureBoard.notPerformed", outcome.Detail),
        };
    }

    // ── De-icing ──────────────────────────────────────────────────────────────────────

    /// <summary>The de-icing policy row (2026-10-09): the pack id of what the Status section
    /// shows for the verdict (<c>gsxSettings.deice.verdict.*</c>), with the captain's answer
    /// folded in where one was given.</summary>
    public static string DeiceVerdictTextId(DeiceRequestSnapshot deice)
    {
        ArgumentNullException.ThrowIfNull(deice);
        if (deice.RequestThisCycle)
        {
            return "gsxSettings.deice.verdict.requested";
        }

        if (deice.Declined is not null)
        {
            return "gsxSettings.deice.verdict.declined";
        }

        return deice.Verdict switch
        {
            DeicePolicyVerdict.Off => "gsxSettings.deice.verdict.off",
            DeicePolicyVerdict.NoData => "gsxSettings.deice.verdict.noData",
            DeicePolicyVerdict.NotRequired => "gsxSettings.deice.verdict.notRequired",
            DeicePolicyVerdict.Ask => "gsxSettings.deice.verdict.ask",
            DeicePolicyVerdict.Request => "gsxSettings.deice.verdict.requested",
            _ => "gsxSettings.deice.verdict.pending",
        };
    }

    public static string DeiceVerdictLamp(DeicePolicyVerdict verdict) => verdict switch
    {
        DeicePolicyVerdict.Request => "lamp lamp-green",
        DeicePolicyVerdict.Ask => "lamp lamp-amber lamp-pulse",
        DeicePolicyVerdict.NotRequired => "lamp lamp-dark",
        DeicePolicyVerdict.NoData => "lamp lamp-amber",
        _ => "lamp lamp-off",
    };

    /// <summary>"OAT −2 °C · snow · EGLL" — the figures the verdict used; dashes when unknown.</summary>
    public static string DeiceInputs(DeiceRequestSnapshot deice)
    {
        ArgumentNullException.ThrowIfNull(deice);
        var oat = deice.OatC is { } o ? $"OAT {Math.Round(o):0} °C" : "OAT —";
        var icao = string.IsNullOrWhiteSpace(deice.Icao) ? "—" : deice.Icao;
        return $"{oat} · {deice.Precip} · {icao}";
    }

    /// <summary>The whole de-icing section. The question shows only while it stands and its
    /// TTL has not passed at <paramref name="nowUtc"/>; the LVAR line reads GSX's own
    /// <c>L:FSDT_GSX_DEICING_STATE</c> / <c>_TYPE</c> pair, the dash until SimConnect has
    /// reported them.</summary>
    public static DeiceView Deice(DeiceRequestSnapshot deice, GsxDiagnosticsSnapshot view, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(deice);
        ArgumentNullException.ThrowIfNull(view);
        DeiceQuestionView? question = null;
        if (deice.QuestionOpen(nowUtc) && deice.Question is { } open)
        {
            question = new DeiceQuestionView(open.Prompt, DisplayText.Id("gsxSettings.deiceStatus.questionState",
                DisplayText.Id(open.Attempt > 1 ? "gsxSettings.deiceStatus.reAsked" : "gsxSettings.deiceStatus.open"),
                // A de-icing question always carries a TTL (the definition's default or the option).
                (open.ExpiresAtUtc ?? open.AskedAtUtc).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)));
        }

        return new DeiceView(
            Lamp: DeiceVerdictLamp(deice.Verdict),
            VerdictTextId: DeiceVerdictTextId(deice),
            Reason: deice.Reason,
            Inputs: DeiceInputs(deice),
            Question: question,
            Lvars: view.DeiceLvars is { } lv
                ? DisplayText.Id("gsxSettings.deiceStatus.lvarsValue", lv.StateLabel, lv.FluidType)
                : DisplayText.Dash);
    }

    // ── Raw diagnostics (ADR-0010: the wire tables are for triage, not for flying) ────

    /// <summary>The services table, ordered by id. Stage is the latched view — diverging
    /// from Mapped after a quick service finishes is the latch doing its job (issue #29).</summary>
    public static IReadOnlyList<ServiceTableRow> ServiceTable(GsxDiagnosticsSnapshot view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return [.. view.Services.OrderBy(s => s.Id).Select(service => new ServiceTableRow(
            service.DisplayName,
            service.SemanticState ?? "—",
            service.Waiting,
            service.MappedState,
            StageTextId(service.Stage),
            service.CanTrigger,
            service.ProgressText ?? ""))];
    }

    /// <summary>The automation decisions, newest first as the store keeps them.</summary>
    public static IReadOnlyList<DecisionRowView> Decisions(GsxDiagnosticsSnapshot view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return [.. view.RecentDecisions.Select(decision => new DecisionRowView(
            decision.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            decision.Action,
            decision.Reason))];
    }

    /// <summary>The recent commands with their outcome pill.</summary>
    public static IReadOnlyList<CommandRowView> Commands(GsxDiagnosticsSnapshot view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return [.. view.RecentCommands.Select(command => new CommandRowView(
            command.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            command.Verb,
            command.Args,
            command.Ok ? "tone-ok" : "tone-bad",
            command.Code))];
    }
}
