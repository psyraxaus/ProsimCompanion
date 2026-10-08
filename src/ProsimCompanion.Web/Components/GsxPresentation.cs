using ProsimCompanion.Core.State;

namespace ProsimCompanion.Web.Components;

/// <summary>
/// Shared display mapping for GSX states — one place for the stage/readiness wording and pill
/// tones used by both the Flight Status dashboard and the GSX diagnostics page.
/// </summary>
public static class GsxPresentation
{
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

    public static string StageTone(GsxServiceStage stage) => stage switch
    {
        GsxServiceStage.Completed => "tone-ok",
        GsxServiceStage.Active or GsxServiceStage.Requested => "tone-active",
        GsxServiceStage.Called or GsxServiceStage.Held => "tone-warn",
        _ => "tone-neutral",
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

    /// <summary>The de-icing policy row (2026-10-09): what the Status section shows for the
    /// verdict, with the captain's answer folded in where one was given.</summary>
    public static string DeiceVerdictLabel(DeiceRequestSnapshot deice)
    {
        ArgumentNullException.ThrowIfNull(deice);
        if (deice.RequestThisCycle)
        {
            return "De-icing requested";
        }

        if (deice.Declined is not null)
        {
            return "Declined";
        }

        return deice.Verdict switch
        {
            DeicePolicyVerdict.Pending => "Not evaluated yet",
            DeicePolicyVerdict.Off => "Off",
            DeicePolicyVerdict.NoData => "No data",
            DeicePolicyVerdict.NotRequired => "Not required",
            DeicePolicyVerdict.Ask => "Asking the captain",
            DeicePolicyVerdict.Request => "De-icing requested",
            _ => deice.Verdict.ToString(),
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

    public static string ReadinessTone(string readiness) => readiness switch
    {
        "Ready" => "tone-ok",
        "ConnectedGsxNotRunning" => "tone-warn",
        _ => "tone-bad",
    };

    public static string ConnectionTone(ConnectionState state) => state switch
    {
        ConnectionState.Connected => "tone-ok",
        ConnectionState.Connecting => "tone-warn",
        ConnectionState.Disabled => "tone-neutral",
        _ => "tone-bad",
    };

    public static string ConnectionDot(ConnectionState state) => state switch
    {
        ConnectionState.Connected => "dot-ok",
        ConnectionState.Connecting => "dot-warn",
        ConnectionState.Disabled => "dot-neutral",
        _ => "dot-bad",
    };
}
