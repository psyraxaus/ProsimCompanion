using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Sync;

namespace ProsimCompanion.App.Hosting;

/// <summary>Wire state of one departure service on the status board.</summary>
public enum StatusServiceState
{
    NotAvailable,
    Callable,
    Requested,
    Active,
    Completed,
    Skipped,
}

public sealed record StatusConnections(bool Prosim, bool Msfs, bool Gsx);

public sealed record StatusServiceRow(string Type, StatusServiceState State, string Detail);

public sealed record StatusGsx(
    bool AutomationActive,
    string? NextService,
    IReadOnlyList<StatusServiceRow> Services,
    double? RefuelPercent,
    int? PaxBoarded,
    int? PaxTotal,
    int? PaxRemaining);

public sealed record StatusChecklist(string Name, string Item, int Index, int Count);

public sealed record StatusResponse(
    string Phase,
    StatusConnections Connections,
    StatusGsx? Gsx,
    StatusChecklist? Checklist);

/// <summary>
/// Read-only <c>GET /api/status</c> for the Stream Deck plugin's live key faces: flight phase,
/// connection states and the GSX/checklist at-a-glance view, assembled from the existing
/// in-memory stores (no sim round-trips — every read is a cached value). Mirrors
/// <see cref="CommandApiEndpoints"/> exactly on gating: the <c>commandApi.enabled</c> switch
/// answers 404 while off, and the web access token is required even from loopback unless
/// <see cref="CommandApiOptions.RequireTokenOnLoopback"/> is turned off.
/// </summary>
public static class StatusApiEndpoints
{
    /// <summary>camelCase + string enums — the same contract as the command API
    /// (docs/integrations/command-api.md).</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static void MapStatusApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/status", GetStatus);
    }

    private static IResult GetStatus(
        HttpContext context,
        IOptionsMonitor<CommandApiOptions> apiOptions,
        IOptionsMonitor<WebUiOptions> webUiOptions)
    {
        // Same order as the command API: a disabled surface 404s before any auth probing.
        if (!apiOptions.CurrentValue.Enabled)
        {
            return Results.NotFound();
        }

        if (!IsAuthorized(context, apiOptions.CurrentValue, webUiOptions.CurrentValue))
        {
            return Results.Json(
                new { reason = "Access token required (Authorization: Bearer <token>, or the web UI cookie)." },
                Json,
                statusCode: StatusCodes.Status401Unauthorized);
        }

        // Every pillar is optional (degrade-not-fail): resolve with GetService and let the
        // builder null out what is absent.
        var services = context.RequestServices;
        var departure = services.GetService<IGsxDepartureControl>();
        var response = BuildStatus(
            services.GetService<IFlightPhaseSource>()?.CurrentPhase,
            services.GetService<ConnectionStatusStore>()?.Snapshot() ?? [],
            departure,
            departure is null ? null : services.GetService<GsxDiagnosticsStore>()?.Snapshot(),
            services.GetService<GsxRefuelSync>()?.ProgressPercent,
            services.GetService<GsxBoardingSync>()?.PaxBoarded,
            services.GetService<GsxBoardingSync>()?.PaxTotal,
            services.GetService<GsxBoardingSync>()?.PaxRemaining,
            services.GetService<ChecklistService>()?.ActiveView());

        return Results.Json(response, Json);
    }

    /// <summary>Pure DTO assembly — separated from the endpoint so tests exercise it with fake
    /// store values and no HTTP plumbing.</summary>
    public static StatusResponse BuildStatus(
        FlightPhase? phase,
        IReadOnlyList<KeyValuePair<string, ConnectionState>> connectionStates,
        IGsxDepartureControl? departure,
        GsxDiagnosticsSnapshot? gsxSnapshot,
        double? refuelPercent,
        int? paxBoarded,
        int? paxTotal,
        int? paxRemaining,
        ChecklistView? checklist)
    {
        ArgumentNullException.ThrowIfNull(connectionStates);

        var connections = new StatusConnections(
            IsConnected(connectionStates, Subsystems.Prosim),
            IsConnected(connectionStates, Subsystems.SimConnect),
            IsConnected(connectionStates, Subsystems.Gsx));

        StatusGsx? gsx = null;
        if (departure is not null)
        {
            var rows = ServiceRows(gsxSnapshot);
            gsx = new StatusGsx(
                AutomationActive: departure.Started && !departure.Complete,
                NextService: NextService(gsxSnapshot),
                Services: rows,
                RefuelPercent: refuelPercent,
                PaxBoarded: paxBoarded,
                PaxTotal: paxTotal,
                PaxRemaining: paxRemaining);
        }

        StatusChecklist? checklistStatus = null;
        if (checklist is not null)
        {
            var active = checklist.ActiveIndex >= 0 && checklist.ActiveIndex < checklist.Items.Count
                ? checklist.Items[checklist.ActiveIndex].Label
                : "";
            checklistStatus = new StatusChecklist(
                checklist.Name,
                active,
                checklist.ActiveIndex,
                checklist.Items.Count);
        }

        return new StatusResponse(
            JsonNamingPolicy.CamelCase.ConvertName((phase ?? FlightPhase.Unknown).ToString()),
            connections,
            gsx,
            checklistStatus);
    }

    private static bool IsConnected(
        IReadOnlyList<KeyValuePair<string, ConnectionState>> states,
        string subsystem)
        => states.Any(pair =>
            string.Equals(pair.Key, subsystem, StringComparison.OrdinalIgnoreCase)
            && pair.Value == ConnectionState.Connected);

    /// <summary>Departure board rows (configured order) first, then every mirrored service the
    /// board does not cover. The board only carries configured departure services, so
    /// board-else-mirror dropped jetway/stairs/GPU/deice from the payload entirely once
    /// automation published a board — blanking their Stream Deck keys (issue #31). Mirror rows
    /// use the latched Stage, so a finished quick service stays completed (issue #29).</summary>
    private static IReadOnlyList<StatusServiceRow> ServiceRows(GsxDiagnosticsSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return [];
        }

        var rows = new List<StatusServiceRow>(
            snapshot.ServiceBoard.Select(row => new StatusServiceRow(
                row.ServiceId,
                MapStage(row.Stage),
                row.Detail ?? "")));

        var seen = new HashSet<string>(
            snapshot.ServiceBoard.Select(row => row.ServiceId),
            StringComparer.OrdinalIgnoreCase);
        rows.AddRange(snapshot.Services
            .Where(service => seen.Add(service.Id))
            .Select(service => new StatusServiceRow(
                service.Id,
                MapStage(service.Stage),
                service.ProgressText ?? "")));

        return rows;
    }

    /// <summary>The next service the sequencer would call: the first board row still waiting
    /// or holding. Called/Requested/Active rows are already on their way.</summary>
    private static string? NextService(GsxDiagnosticsSnapshot? snapshot)
        => snapshot?.ServiceBoard
            .FirstOrDefault(row => row.Stage is GsxServiceStage.Waiting or GsxServiceStage.Held)
            ?.ServiceId;

    /// <summary>Waiting maps to callable, not notAvailable: a service the sequencer has not
    /// reached (or GSX simply offers outside the board) is still manually callable, and
    /// notAvailable dims its Stream Deck key.</summary>
    private static StatusServiceState MapStage(GsxServiceStage stage) => stage switch
    {
        GsxServiceStage.Waiting => StatusServiceState.Callable,
        GsxServiceStage.Held => StatusServiceState.Callable,
        GsxServiceStage.Called => StatusServiceState.Requested,
        GsxServiceStage.Requested => StatusServiceState.Requested,
        GsxServiceStage.Active => StatusServiceState.Active,
        GsxServiceStage.Completed => StatusServiceState.Completed,
        GsxServiceStage.Skipped => StatusServiceState.Skipped,
        _ => StatusServiceState.NotAvailable,
    };

    /// <summary>Same credential checks as <see cref="CommandApiEndpoints"/> (bearer token or
    /// onboarded-browser cookie, fixed-time compared; token-even-on-loopback by default).</summary>
    private static bool IsAuthorized(
        HttpContext context,
        CommandApiOptions apiOptions,
        WebUiOptions webUiOptions)
    {
        var remote = context.Connection.RemoteIpAddress;
        var isLoopback = remote is null || IPAddress.IsLoopback(remote);
        if (isLoopback && !apiOptions.RequireTokenOnLoopback)
        {
            return true;
        }

        var token = webUiOptions.AccessToken;
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        const string bearerPrefix = "Bearer ";
        var authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)
            && TokensEqual(authorization[bearerPrefix.Length..].Trim(), token))
        {
            return true;
        }

        return context.Request.Cookies.TryGetValue(LanTokenMiddleware.CookieName, out var cookie)
            && TokensEqual(cookie, token);
    }

    private static bool TokensEqual(string presented, string expected)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented),
            Encoding.UTF8.GetBytes(expected));
}
