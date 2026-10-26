using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Diagnostics;
using ProsimCompanion.Core.Diagnostics.Ecam;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.App.Hosting;

/// <summary>
/// <c>GET /api/diagnostics/bundle?sessions=5&amp;days=3&amp;wire=false</c>: streams the support
/// zip built by <see cref="DiagnosticsBundleBuilder"/>. Gated exactly like the telemetry API
/// (same <c>telemetryApi.enabled</c> switch, same token-or-cookie check) — it is the same
/// read-only evidence surface, just packaged. The Logs page links here directly: the
/// onboarded browser cookie authenticates, so no token ever appears in a URL. The temp zip is
/// opened delete-on-close, so it disappears when the response finishes (or aborts).
/// <para>
/// <c>GET /api/diagnostics/snapshot</c>: the current ECAM diagnostics snapshot as JSON (enums
/// by name), same gating — what a future iPad display or the Stream Deck renders from.
/// </para>
/// </summary>
public static class DiagnosticsEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapDiagnosticsApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet("/api/diagnostics/bundle", GetBundle);
        endpoints.MapGet("/api/diagnostics/snapshot", GetSnapshot);
    }

    private static IResult GetSnapshot(
        HttpContext context,
        IOptionsMonitor<TelemetryApiOptions> apiOptions,
        IOptionsMonitor<WebUiOptions> webUiOptions,
        DiagnosticsStore store)
    {
        if (Gate(context, apiOptions, webUiOptions) is { } refusal)
        {
            return refusal;
        }

        return Results.Text(SnapshotJson(store.Snapshot()), "application/json");
    }

    /// <summary>The snapshot document the endpoint returns. Exposed for tests: enums must come
    /// out as names ("caution"), never as numbers, because the consumers are scripts.</summary>
    public static string SnapshotJson(DiagnosticsSnapshot snapshot) => EcamJson.Serialize(snapshot);

    private static async Task<IResult> GetBundle(
        int? sessions,
        int? days,
        bool? wire,
        HttpContext context,
        IOptionsMonitor<TelemetryApiOptions> apiOptions,
        IOptionsMonitor<WebUiOptions> webUiOptions,
        IOptionsMonitor<DiagnosticsOptions> diagnosticsOptions,
        DiagnosticsBundleBuilder builder,
        IAppBuildInfo build,
        ConnectionStatusStore status,
        DiagnosticsStore diagnostics,
        DiagEventLog diagnosticEvents,
        ILogger<DiagnosticsBundleBuilder> logger,
        CancellationToken ct)
    {
        if (Gate(context, apiOptions, webUiOptions) is { } refusal)
        {
            return refusal;
        }

        var defaults = diagnosticsOptions.CurrentValue;
        var request = new DiagnosticsBundleRequest(
            UserDataPaths.Sessions,
            UserDataPaths.Logs,
            Path.Combine(AppContext.BaseDirectory, "config", "settings.json"),
            build)
        {
            SessionCount = Math.Clamp(sessions ?? defaults.BundleSessionCount, DiagnosticsOptions.MinSessions, DiagnosticsOptions.MaxSessions),
            LogDays = Math.Clamp(days ?? defaults.BundleLogDays, DiagnosticsOptions.MinLogDays, DiagnosticsOptions.MaxLogDays),
            IncludeWireTrace = wire ?? defaults.IncludeWireTrace,
            DependencyLines = DependencyLines(status),
            DiagnosticsSnapshot = diagnostics.Snapshot(),
            DiagnosticsEvents = diagnosticEvents.Snapshot(),
        };

        string zipPath;
        try
        {
            zipPath = await builder.BuildAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Diagnostics bundle could not be built");
            return Results.Problem("The diagnostics bundle could not be built; see the app log.", statusCode: 500);
        }

        var stream = new FileStream(
            zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        return Results.File(stream, "application/zip", Path.GetFileName(zipPath), enableRangeProcessing: false);
    }

    /// <summary>The sibling-API gate order: a disabled surface 404s before any auth probing.</summary>
    private static IResult? Gate(
        HttpContext context,
        IOptionsMonitor<TelemetryApiOptions> apiOptions,
        IOptionsMonitor<WebUiOptions> webUiOptions)
    {
        if (!apiOptions.CurrentValue.Enabled)
        {
            return Results.NotFound();
        }

        if (!ApiTokenAuth.IsAuthorized(
                context, apiOptions.CurrentValue.RequireTokenOnLoopback, webUiOptions.CurrentValue))
        {
            return Results.Json(
                new { reason = "Access token required (Authorization: Bearer <token>, or the web UI cookie)." },
                Json,
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return null;
    }

    /// <summary>Which optional dependencies were present, from the status store — cheap, no
    /// probing. A subsystem that is off for a stated reason carries it ("ProSim: Disabled —
    /// the ProSim SDK … does not match", issue #158). Exposed for tests.</summary>
    public static IReadOnlyList<string> DependencyLines(ConnectionStatusStore status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return
        [
            .. status.Snapshot().Select(pair => status.ReasonOf(pair.Key) is { } reason
                ? $"{pair.Key}: {pair.Value} — {reason}"
                : $"{pair.Key}: {pair.Value}"),
        ];
    }
}
