using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Theming;

namespace ProsimCompanion.App.Hosting;

/// <summary>
/// Serves the pilot's uploaded airline logos (<see cref="ThemeLogoStore"/>) to the header
/// and the Appearance page. Read-only and page-adjacent, so it mirrors the page-serving
/// auth policy (the onboarded-browser cookie; loopback passes). The URL carries the store
/// version as a cache-buster, so responses may be cached freely.
/// </summary>
public static class ThemeLogoEndpoints
{
    public static void MapThemeLogoApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet("/api/theme-logo/{slug}", GetLogo);
        endpoints.MapGet("/api/airline-logo/{code}", GetAirlineLogo);
    }

    private static IResult GetAirlineLogo(
        HttpContext context,
        string code,
        AirlineLogoStore logos,
        IOptionsMonitor<WebUiOptions> webUiOptions)
    {
        if (!ApiTokenAuth.IsAuthorized(context, requireTokenOnLoopback: false, webUiOptions.CurrentValue))
        {
            return Results.Unauthorized();
        }

        var path = logos.FindFile(code);
        return path is null
            ? Results.NotFound()
            : Results.File(path, ThemeLogoStore.ContentType(path));
    }

    private static IResult GetLogo(
        HttpContext context,
        string slug,
        ThemeLogoStore logos,
        IOptionsMonitor<WebUiOptions> webUiOptions)
    {
        if (!ApiTokenAuth.IsAuthorized(context, requireTokenOnLoopback: false, webUiOptions.CurrentValue))
        {
            return Results.Unauthorized();
        }

        var path = logos.FindFileBySlug(slug);
        return path is null
            ? Results.NotFound()
            : Results.File(path, ThemeLogoStore.ContentType(path));
    }
}
