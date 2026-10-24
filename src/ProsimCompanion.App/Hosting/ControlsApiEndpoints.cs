using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft.Switches;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.App.Hosting;

/// <summary>One catalogue position of a switch.</summary>
public sealed record ControlsPosition(int Value, string Label);

/// <summary>
/// A configured cockpit switch as the Stream Deck key draws it. Deliberately WITHOUT the
/// dataref (review 2026-10-25): the plugin addresses a row by <paramref name="Id"/> only, so
/// the wire never carries a dataref name in either direction (ADR-0015).
/// </summary>
public sealed record ControlsSwitch(
    string Id,
    string Name,
    CockpitSwitchLook Look,
    CockpitPressMode PressMode,
    string UpperLegend,
    string LowerLegend,
    string UpperColor,
    string LowerColor,
    string BezelColor,
    IReadOnlyList<ControlsPosition> Positions,
    bool ShowUnlitLegend = true);

/// <summary>A vJoy relay view row.</summary>
public sealed record ControlsView(string Name, int Button);

/// <summary>The whole <c>GET /api/controls</c> payload: both features' master switches and
/// their rows, so a key can say "switches off" instead of guessing from an empty list.</summary>
public sealed record ControlsResponse(
    bool SwitchesEnabled,
    IReadOnlyList<ControlsSwitch> Switches,
    bool ViewsEnabled,
    IReadOnlyList<ControlsView> Views);

/// <summary>
/// <c>GET /api/controls</c> (2026-10-24): the configured cockpit switches and camera views, for
/// the Stream Deck plugin's Property Inspector dropdowns and key faces. Configuration, not
/// state — the plugin reads it when a key's settings open and on connect; the live values ride
/// in <c>/api/status</c>. Same gate as the status feed: 404 while <c>commandApi.enabled</c> is
/// off, token even on loopback by default.
/// </summary>
public static class ControlsApiEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Maps <c>GET /api/controls</c>. Called once from the composition root.</summary>
    public static void MapControlsApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet("/api/controls", GetControls);
    }

    private static IResult GetControls(
        HttpContext context,
        IOptionsMonitor<CommandApiOptions> apiOptions,
        IOptionsMonitor<WebUiOptions> webUiOptions,
        IOptionsMonitor<CockpitSwitchesOptions> cockpit,
        IOptionsMonitor<InputRelayOptions> inputRelay)
    {
        if (!apiOptions.CurrentValue.Enabled)
        {
            return Results.NotFound();
        }

        if (!ApiTokenAuth.IsAuthorized(context, apiOptions.CurrentValue.RequireTokenOnLoopback, webUiOptions.CurrentValue))
        {
            return Results.Json(
                new { reason = "Access token required (Authorization: Bearer <token>, or the web UI cookie)." },
                Json,
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var catalog = context.RequestServices.GetService<SwitchCatalogStore>()?.Snapshot();
        return Results.Json(Build(cockpit.CurrentValue, inputRelay.CurrentValue, catalog), Json);
    }

    /// <summary>Pure assembly, unit-tested without HTTP.</summary>
    public static ControlsResponse Build(CockpitSwitchesOptions cockpit, InputRelayOptions inputRelay, SwitchCatalogSnapshot? catalog)
    {
        ArgumentNullException.ThrowIfNull(cockpit);
        ArgumentNullException.ThrowIfNull(inputRelay);

        var switches = cockpit.Rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Id) && !string.IsNullOrWhiteSpace(r.DataRef))
            .Select(r => new ControlsSwitch(
                r.Id.Trim(),
                string.IsNullOrWhiteSpace(r.Name) ? r.Id.Trim() : r.Name.Trim(),
                r.Look,
                r.PressMode,
                r.UpperLegend,
                r.LowerLegend,
                CockpitSwitchLogic.IsHexColor(r.UpperColor) ? r.UpperColor.Trim() : CockpitSwitchRow.DefaultUpperColor,
                CockpitSwitchLogic.IsHexColor(r.LowerColor) ? r.LowerColor.Trim() : CockpitSwitchRow.DefaultLowerColor,
                CockpitSwitchLogic.IsHexColor(r.BezelColor) ? r.BezelColor.Trim() : "",
                catalog?.Find(r.DataRef)?.Values.Select(v => new ControlsPosition(v.Value, v.Label)).ToList() ?? [],
                r.ShowUnlitLegend))
            .ToList();

        var views = inputRelay.Views
            .Where(v => !string.IsNullOrWhiteSpace(v.Name))
            .Select(v => new ControlsView(v.Name.Trim(), v.Button))
            .ToList();

        return new ControlsResponse(cockpit.Enabled, switches, inputRelay.Enabled, views);
    }
}
