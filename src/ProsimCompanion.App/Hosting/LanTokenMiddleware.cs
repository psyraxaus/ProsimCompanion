using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.App.Hosting;

/// <summary>
/// Gate for non-loopback clients: loopback always passes (the local UI can never be locked out);
/// LAN clients must present the access token once — via the QR/onboarding link's
/// <c>?token=</c> query — after which a cookie carries it (including over the Blazor circuit's
/// WebSocket). Comparisons are fixed-time.
/// </summary>
public sealed class LanTokenMiddleware
{
    private const string CookieName = "prosimcompanion-token";
    private const string QueryName = "token";

    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<WebUiOptions> _options;

    public LanTokenMiddleware(RequestDelegate next, IOptionsMonitor<WebUiOptions> options)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(options);
        _next = next;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null || IPAddress.IsLoopback(remote))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var token = _options.CurrentValue.AccessToken;
        if (string.IsNullOrEmpty(token))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync(
                "LAN access is not configured on this ProsimCompanion instance.").ConfigureAwait(false);
            return;
        }

        if (context.Request.Cookies.TryGetValue(CookieName, out var cookie) && TokensEqual(cookie, token))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (context.Request.Query.TryGetValue(QueryName, out var presented) && TokensEqual(presented.ToString(), token))
        {
            context.Response.Cookies.Append(CookieName, token, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                MaxAge = TimeSpan.FromDays(30),
            });

            // Strip the token from the address bar.
            context.Response.Redirect(context.Request.Path.HasValue ? context.Request.Path.Value! : "/");
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsync(
            "Access token required. Scan the QR code shown in the ProsimCompanion window on the sim PC.")
            .ConfigureAwait(false);
    }

    private static bool TokensEqual(string presented, string expected)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented),
            Encoding.UTF8.GetBytes(expected));
}
