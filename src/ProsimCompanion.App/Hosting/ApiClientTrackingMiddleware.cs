using Microsoft.AspNetCore.Http;
using ProsimCompanion.Core.Diagnostics;
using ProsimCompanion.Core.Diagnostics.Metrics;

namespace ProsimCompanion.App.Hosting;

/// <summary>
/// Presence for the HTTP API's clients (the diagnostics REMOTE page, ADR-0016): every request
/// under <c>/api/</c> counts one hit on the API rate and stamps the caller's address and user
/// agent in the <see cref="ApiClientRegistry"/>. Runs after the LAN token middleware so an
/// unauthenticated probe never registers as a client. Never alters the response.
/// </summary>
public sealed class ApiClientTrackingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ApiClientRegistry _registry;
    private readonly RateCounter _rate;

    public ApiClientTrackingMiddleware(RequestDelegate next, ApiClientRegistry registry, RateRegistry rates)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(rates);
        _next = next;
        _registry = registry;
        _rate = rates[RateNames.Api];
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            _rate.Hit();
            _registry.Seen(
                context.Connection.RemoteIpAddress?.ToString() ?? "?",
                context.Request.Headers.UserAgent.ToString(),
                DateTimeOffset.UtcNow);
        }

        return _next(context);
    }
}
