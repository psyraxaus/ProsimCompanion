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
/// WebSocket). Stateless API clients may instead send <c>Authorization: Bearer</c> on every
/// request (issue #96). Comparisons are fixed-time.
/// </summary>
public sealed class LanTokenMiddleware
{
    /// <summary>Shared with <see cref="CommandApiEndpoints"/> so an onboarded browser session's
    /// cookie also authorises command-API calls.</summary>
    internal const string CookieName = "prosimcompanion-token";
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

        // Pure API clients authenticate with the bearer header alone (issue #96): the API
        // endpoints always advertised it, but this middleware ran first and demanded the
        // cookie dance. No cookie is minted — a stateless client stays stateless.
        const string bearerPrefix = "Bearer ";
        var authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)
            && TokensEqual(authorization[bearerPrefix.Length..].Trim(), token))
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

            // Strip the token from the address bar — but ONLY the token (issue #96: the
            // whole query was dropped, silently discarding parameters like tailKb on an
            // onboarding request).
            var remaining = context.Request.Query
                .Where(pair => !string.Equals(pair.Key, QueryName, StringComparison.OrdinalIgnoreCase))
                .SelectMany(pair => pair.Value, (pair, value) => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(value ?? "")}")
                .ToList();
            var path = context.Request.Path.HasValue ? context.Request.Path.Value! : "/";
            context.Response.Redirect(remaining.Count > 0 ? $"{path}?{string.Join('&', remaining)}" : path);
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
