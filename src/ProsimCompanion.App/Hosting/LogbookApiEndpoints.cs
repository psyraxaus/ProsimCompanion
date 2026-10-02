using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Logbook;

namespace ProsimCompanion.App.Hosting;

/// <summary>
/// <c>GET /api/logbook/export.csv</c> (issue #146): the pilot's logbook as a CSV download
/// (<see cref="LogbookCsv"/>). The Logbook page's Export button links here directly — the
/// onboarded browser cookie authenticates, so no token ever appears in a URL (the diagnostics
/// bundle's pattern). Read-only, so loopback passes as it does for the pages themselves; a
/// LAN caller needs the web access token (bearer header or the cookie). No enable switch: it
/// serves the same rows the page already shows to the same caller.
/// </summary>
public static class LogbookApiEndpoints
{
    public const string ExportRoute = "/api/logbook/export.csv";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapLogbookApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet(ExportRoute, ExportCsv);
    }

    private static IResult ExportCsv(
        HttpContext context,
        IOptionsMonitor<WebUiOptions> webUiOptions,
        ILogbookService logbook)
    {
        if (!ApiTokenAuth.IsAuthorized(context, requireTokenOnLoopback: false, webUiOptions.CurrentValue))
        {
            return Results.Json(
                new { reason = "Access token required (Authorization: Bearer <token>, or the web UI cookie)." },
                Json,
                statusCode: StatusCodes.Status401Unauthorized);
        }

        // No BOM: the content is ASCII unless an abnormal title is not, and the charset is
        // declared on the response.
        var csv = LogbookCsv.Build(logbook.Flights);
        return Results.File(
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(csv),
            "text/csv; charset=utf-8",
            LogbookCsv.FileName(DateTimeOffset.UtcNow));
    }
}
