using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ProsimCompanion.App.Hosting;

/// <summary>
/// <c>GET /api/app/boot</c>: the running process's boot id (issue #97). A Blazor circuit
/// dies with the server, but a tab whose WebSocket died SILENTLY keeps rendering and ignores
/// every click with no overlay — the client-side watchdog polls this id and hard-reloads the
/// moment a different process is answering. Gated only by <see cref="LanTokenMiddleware"/>
/// (loopback free, LAN cookie/bearer): it reveals nothing but a random GUID, and it must
/// work wherever the page itself works.
/// </summary>
public static class AppBootEndpoints
{
    /// <summary>Fresh per process start — sameness across polls means "same server".</summary>
    public static readonly string BootId = Guid.NewGuid().ToString("n");

    public static void MapAppBoot(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/app/boot", () => Results.Json(new { bootId = BootId }));
    }
}
