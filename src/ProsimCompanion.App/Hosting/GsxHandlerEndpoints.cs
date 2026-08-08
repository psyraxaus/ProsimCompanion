using System.IO;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.App.Hosting;

/// <summary>
/// The in-sim GSX handler script's endpoints (ported from Prosim2GSX's GsxMenuController).
/// GSX's Couatl Python sandbox has no HTTP POST primitive, so events arrive as GETs with
/// query params, and the body must always be valid JSON (<c>null</c>) for the handler's
/// fetchJson. The script targets 127.0.0.1 — loopback bypasses the LAN token middleware, so
/// no auth exemption is required. Events are observability: they feed the Flight Status
/// "Last Handler Event" row and the session event log; automation stays driven by the
/// Remote API + LVARs (predecessor phase-1 rule, kept deliberately).
/// </summary>
public static class GsxHandlerEndpoints
{
    /// <summary>The 13 events the shipped gsx_handler.py can emit — anything else is ignored
    /// (defence against a modified script spamming the log).</summary>
    private static readonly HashSet<string> KnownEvents = new(StringComparer.Ordinal)
    {
        "aircraftEngaged", "aircraftDisengaged", "gateReset",
        "boardingRequested", "deboardingRequested", "refuelingRequested",
        "cateringRequested", "departureRequested",
        "jetwayConnected", "jetwayDisconnected",
        "bypassPinConnected", "bypassPinDisconnected",
        "deicingAction",
    };

    public static void MapGsxHandlerApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/gsxmenu/events", (
            string? e,
            string? r,
            GsxDiagnosticsStore diagnostics,
            JsonlEventLog eventLog) =>
        {
            if (e is not null && KnownEvents.Contains(e))
            {
                diagnostics.RecordHandlerEvent(new GsxHandlerEventView(DateTimeOffset.UtcNow, e, r ?? ""));
                eventLog.Record("gsx-handler", new { @event = e, reason = r });
            }
            return Results.Json<object?>(null);
        });

        endpoints.MapGet("/api/gsxmenu/flight-info", (OfpStore ofpStore) =>
        {
            var ofp = ofpStore.Current;
            if (ofp is null)
            {
                return Results.Json<object?>(null);
            }
            return Results.Json<object?>(new
            {
                callsign = ofp.Callsign,
                flightNumber = ofp.FlightNumber.Length > 0 ? ofp.FlightNumber : ofp.Ident,
                origin = ofp.OriginIcao,
                destination = ofp.DestinationIcao,
            });
        });
    }

    /// <summary>Keeps the deployed handler scripts' port constant in sync with the web port —
    /// the predecessor's GsxHandlerSync rule: without it, a non-default port silently kills
    /// the in-sim bridge. Runs once at startup (a port change needs a restart anyway).</summary>
    public static void SyncHandlerPort(int port, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var airplanesDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Virtuali",
            "Airplanes");
        if (!Directory.Exists(airplanesDir))
        {
            return; // GSX not installed — nothing to sync
        }

        var portLine = $"PROSIMCOMPANION_PORT = {port}";
        var portRegex = new Regex(@"^PROSIMCOMPANION_PORT\s*=\s*\d+", RegexOptions.Multiline);
        foreach (var handlerPath in Directory.EnumerateFiles(airplanesDir, "gsx_handler.py", SearchOption.AllDirectories))
        {
            try
            {
                var text = File.ReadAllText(handlerPath);
                if (!portRegex.IsMatch(text))
                {
                    continue; // someone else's handler (e.g. an old Prosim2GSX one) — leave it
                }

                var updated = portRegex.Replace(text, portLine, 1);
                if (!string.Equals(updated, text, StringComparison.Ordinal))
                {
                    File.WriteAllText(handlerPath, updated);
                    logger.LogInformation("GSX handler port synced to {Port} in {Path}", port, handlerPath);
                }
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "GSX handler port sync failed for {Path}", handlerPath);
            }
        }
    }
}
