using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.App.Hosting;

/// <summary>
/// The one API credential check (bearer token or onboarded-browser cookie, fixed-time
/// compared, with a per-surface loopback policy). Extracted with the telemetry API
/// (issue #94): the command and status APIs each carried a byte-identical private copy, and
/// a third copy is where the implementations would have started drifting.
/// </summary>
internal static class ApiTokenAuth
{
    /// <summary>
    /// True when the request may proceed. <paramref name="requireTokenOnLoopback"/> is the
    /// surface's own policy: write surfaces demand the token even locally (any local process
    /// can reach loopback); read-only surfaces may mirror the page-serving middleware and
    /// let loopback pass.
    /// </summary>
    public static bool IsAuthorized(
        HttpContext context,
        bool requireTokenOnLoopback,
        WebUiOptions webUiOptions)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(webUiOptions);

        var remote = context.Connection.RemoteIpAddress;
        var isLoopback = remote is null || IPAddress.IsLoopback(remote);
        if (isLoopback && !requireTokenOnLoopback)
        {
            return true;
        }

        // A gated surface with no token configured stays closed; EnsureAccessToken generates
        // one on first start, so in practice this only trips on a hand-emptied settings file.
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

        // The browser path: a session already onboarded through LanTokenMiddleware carries the
        // cookie, so web-page buttons can call the API without special headers.
        return context.Request.Cookies.TryGetValue(LanTokenMiddleware.CookieName, out var cookie)
            && TokensEqual(cookie, token);
    }

    /// <summary>Fixed-time comparison, same as <see cref="LanTokenMiddleware"/> — an early-exit
    /// compare would leak prefix-match timing to a guessing client.</summary>
    private static bool TokensEqual(string presented, string expected)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented),
            Encoding.UTF8.GetBytes(expected));
}
