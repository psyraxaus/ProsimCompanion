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
