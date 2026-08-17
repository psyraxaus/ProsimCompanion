using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.App.Hosting;

/// <summary>One telemetry file as listed over the API.</summary>
public sealed record TelemetryFileView(string Name, long SizeBytes, DateTimeOffset LastWriteUtc);

/// <summary>Answer to <c>GET /api/telemetry/summary</c>. <see cref="Version"/> is the probe
/// catalog's build canary — an evaluator checks it before trusting event shapes.</summary>
public sealed record TelemetrySummary(
    string Version,
    int SessionFileCount,
    TelemetryFileView? CurrentSession,
    TelemetryFileView? CurrentLog);

/// <summary>
/// Read-only <c>/api/telemetry/*</c> (issue #94): serves the session event logs and rolling
/// app/wire logs to the flight-verification workflow (docs/agents/flight-verification.md), so
/// evidence can be pulled over the LAN — including mid-flight — instead of hand-copied off
/// the sim PC. Strictly read-only and confined to the sessions/logs directories: a requested
/// name must exactly match an enumerated file, so path traversal is impossible by
/// construction, and nothing else (settings.json in particular) is reachable. Gated like the
/// sibling APIs — <c>telemetryApi.enabled</c> answers 404 while off, LAN callers need the web
/// access token — except loopback passes by default (read-only surface; see
/// <see cref="TelemetryApiOptions.RequireTokenOnLoopback"/>).
/// </summary>
public static class TelemetryApiEndpoints
{
    /// <summary>Default and maximum tail sizes for log downloads — the wire trace grew to
    /// 24 MB in one smoke-test session and must never ship whole by accident.</summary>
    public const int DefaultTailKb = 256;
    public const int MaxTailKb = 4096;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static void MapTelemetryApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/telemetry/summary", GetSummary);
        endpoints.MapGet("/api/telemetry/sessions", ListSessions);
        endpoints.MapGet("/api/telemetry/sessions/{name}", GetSession);
        endpoints.MapGet("/api/telemetry/logs", ListLogs);
        endpoints.MapGet("/api/telemetry/logs/{name}", GetLogTail);
    }

    private static IResult GetSummary(
        HttpContext context,
        IOptionsMonitor<TelemetryApiOptions> apiOptions,
        IOptionsMonitor<WebUiOptions> webUiOptions)
        => Gate(context, apiOptions, webUiOptions) ?? Results.Json(
            BuildSummary(ListFiles(UserDataPaths.Sessions), ListFiles(UserDataPaths.Logs)),
            Json);

    private static IResult ListSessions(
        HttpContext context,
        IOptionsMonitor<TelemetryApiOptions> apiOptions,
        IOptionsMonitor<WebUiOptions> webUiOptions)
        => Gate(context, apiOptions, webUiOptions)
            ?? Results.Json(ListFiles(UserDataPaths.Sessions), Json);

    private static IResult GetSession(
        string name,
        HttpContext context,
        IOptionsMonitor<TelemetryApiOptions> apiOptions,
        IOptionsMonitor<WebUiOptions> webUiOptions)
    {
        if (Gate(context, apiOptions, webUiOptions) is { } refusal)
        {
            return refusal;
        }

        // Session files are small (tens of KB) — served whole, no tail parameter.
        return TryResolve(UserDataPaths.Sessions, name, out var path)
            ? Results.Text(ReadShared(path), "application/x-ndjson")
            : Results.NotFound();
    }

    private static IResult ListLogs(
        HttpContext context,
        IOptionsMonitor<TelemetryApiOptions> apiOptions,
        IOptionsMonitor<WebUiOptions> webUiOptions)
        => Gate(context, apiOptions, webUiOptions)
            ?? Results.Json(ListFiles(UserDataPaths.Logs), Json);

    private static IResult GetLogTail(
        string name,
        int? tailKb,
        HttpContext context,
        IOptionsMonitor<TelemetryApiOptions> apiOptions,
        IOptionsMonitor<WebUiOptions> webUiOptions)
    {
        if (Gate(context, apiOptions, webUiOptions) is { } refusal)
        {
            return refusal;
        }

        if (!TryResolve(UserDataPaths.Logs, name, out var path))
        {
            return Results.NotFound();
        }

        var clamped = Math.Clamp(tailKb ?? DefaultTailKb, 1, MaxTailKb);
        return Results.Text(ReadTail(path, clamped), "text/plain");
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

    /// <summary>Pure over the listings — exposed for tests.</summary>
    public static TelemetrySummary BuildSummary(
        IReadOnlyList<TelemetryFileView> sessions,
        IReadOnlyList<TelemetryFileView> logs)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(logs);

        var version = typeof(TelemetryApiEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return new TelemetrySummary(
            plus > 0 ? version[..plus] : version,
            sessions.Count,
            sessions.FirstOrDefault(),
            // The wire trace also lives here — the newest APP log is the one the probes read.
            logs.FirstOrDefault(log => !log.Name.Contains("-wire-", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Newest first; an absent directory is an empty list (degrade, not fail).</summary>
    public static IReadOnlyList<TelemetryFileView> ListFiles(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return [.. new DirectoryInfo(directory)
            .EnumerateFiles()
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => new TelemetryFileView(file.Name, file.Length, file.LastWriteTimeUtc))];
    }

    /// <summary>Resolves a requested name against the directory's ACTUAL files — the requested
    /// string is never combined into a path unless it exactly matches an enumerated file name,
    /// so traversal sequences cannot resolve to anything. Exposed for tests.</summary>
    public static bool TryResolve(string directory, string requestedName, out string fullPath)
    {
        ArgumentNullException.ThrowIfNull(directory);
        fullPath = "";
        if (string.IsNullOrWhiteSpace(requestedName) || !Directory.Exists(directory))
        {
            return false;
        }

        var match = new DirectoryInfo(directory)
            .EnumerateFiles()
            .FirstOrDefault(file => string.Equals(file.Name, requestedName, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return false;
        }

        fullPath = match.FullName;
        return true;
    }

    /// <summary>Whole-file read that tolerates the app still writing it.</summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Last <paramref name="tailKb"/> KB of a live log file. When the window starts
    /// mid-line the partial first line is dropped — a CMTrace/JSONL consumer must never see a
    /// torn record. Exposed for tests.</summary>
    public static string ReadTail(string path, int tailKb)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var window = (long)tailKb * 1024;
        var truncated = stream.Length > window;
        if (truncated)
        {
            stream.Seek(-window, SeekOrigin.End);
        }

        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        if (!truncated)
        {
            return text;
        }

        var firstBreak = text.IndexOf('\n', StringComparison.Ordinal);
        return firstBreak >= 0 ? text[(firstBreak + 1)..] : text;
    }
}
