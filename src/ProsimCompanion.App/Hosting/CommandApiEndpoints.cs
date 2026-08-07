using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Commands;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.App.Hosting;

/// <summary>
/// Minimal-API surface over the <see cref="CommandRegistry"/>:
/// <c>GET /api/commands</c> (registered names) and <c>POST /api/command/{name}</c> (execute,
/// optional JSON body). Outcomes map to status codes via <see cref="CommandOutcomeHttp"/> —
/// every response body carries outcome + reason, never a bare ack.
/// <para>
/// Security: gated by <see cref="CommandApiOptions"/> (off by default — this is a write
/// surface) and, unlike <see cref="LanTokenMiddleware"/> where loopback always passes, these
/// routes require the web access token even from loopback unless
/// <see cref="CommandApiOptions.RequireTokenOnLoopback"/> is turned off. The check is scoped
/// here rather than in the middleware so the page-serving lockout guarantee is untouched.
/// The token itself is never logged.
/// </para>
/// <para>
/// Degrade-not-fail: the registry is resolved per request; when the composition root has not
/// registered/populated it (see WIRING-COMMANDS.md) the routes answer 503 instead of failing
/// endpoint mapping at startup.
/// </para>
/// </summary>
public static class CommandApiEndpoints
{
    /// <summary>camelCase + string enums — the contract documented in
    /// docs/integrations/command-api.md, matched by the Stream Deck plugin.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static void MapCommandApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/commands", ListCommands);
        endpoints.MapPost("/api/command/{name}", ExecuteCommandAsync);
    }

    private static IResult ListCommands(
        HttpContext context,
        IOptionsMonitor<CommandApiOptions> apiOptions,
        IOptionsMonitor<WebUiOptions> webUiOptions)
    {
        var gate = Gate(context, apiOptions.CurrentValue, webUiOptions.CurrentValue, out var registry);
        if (gate is not null)
        {
            return gate;
        }

        return Results.Json(new { commands = registry!.Names }, Json);
    }

    private static async Task<IResult> ExecuteCommandAsync(
        HttpContext context,
        string name,
        IOptionsMonitor<CommandApiOptions> apiOptions,
        IOptionsMonitor<WebUiOptions> webUiOptions,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var gate = Gate(context, apiOptions.CurrentValue, webUiOptions.CurrentValue, out var registry);
        if (gate is not null)
        {
            return gate;
        }

        if (!registry!.TryGetRequestType(name, out var requestType))
        {
            return Results.Json(
                new { reason = $"Command '{name}' is not registered." }, Json, statusCode: StatusCodes.Status404NotFound);
        }

        // Empty body → empty request DTO; per-command validation decides which fields were required.
        object? request;
        try
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
            var json = string.IsNullOrWhiteSpace(body) ? "{}" : body;
            request = JsonSerializer.Deserialize(json, requestType, Json)
                ?? JsonSerializer.Deserialize("{}", requestType, Json);
        }
        catch (JsonException ex)
        {
            return Results.Json(
                new { reason = $"Request body is not valid JSON for '{name}': {ex.Message}" },
                Json,
                statusCode: StatusCodes.Status400BadRequest);
        }

        var logger = loggerFactory.CreateLogger(typeof(CommandApiEndpoints));
        try
        {
            var result = await registry.ExecuteUntypedAsync(name, request, context.RequestAborted)
                .ConfigureAwait(false);

            if (result is CommandResult commandResult)
            {
                logger.LogInformation(
                    "Command {Command} → {Outcome}: {Reason}",
                    name,
                    commandResult.Outcome,
                    commandResult.Reason);

                // Serialise the runtime type: richer derived results keep their extra fields.
                return Results.Json(
                    result, Json, statusCode: CommandOutcomeHttp.StatusCodeFor(commandResult.Outcome));
            }

            // No handler currently returns a non-CommandResult, but the registry allows it.
            return Results.Json(result, Json);
        }
        catch (CommandValidationException ex)
        {
            return Results.Json(
                new { reason = ex.Message }, Json, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (CommandNotFoundException)
        {
            return Results.Json(
                new { reason = $"Command '{name}' is not registered." }, Json, statusCode: StatusCodes.Status404NotFound);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw; // Client went away — nothing useful to answer.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Command {Command} threw", name);
            return Results.Json(
                new { reason = ex.Message }, Json, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>Shared route gate. Returns the refusal result, or null (with the registry) when
    /// the request may proceed. Order matters: a disabled API answers 404 before any auth
    /// probing, so scanners cannot even confirm the surface exists.</summary>
    private static IResult? Gate(
        HttpContext context,
        CommandApiOptions apiOptions,
        WebUiOptions webUiOptions,
        out CommandRegistry? registry)
    {
        registry = null;

        if (!apiOptions.Enabled)
        {
            return Results.NotFound();
        }

        if (!IsAuthorized(context, apiOptions, webUiOptions))
        {
            return Results.Json(
                new { reason = "Access token required (Authorization: Bearer <token>, or the web UI cookie)." },
                Json,
                statusCode: StatusCodes.Status401Unauthorized);
        }

        registry = context.RequestServices.GetService<CommandRegistry>();
        if (registry is null)
        {
            return Results.Json(
                new CommandResult(
                    CommandOutcome.Unavailable,
                    "The command registry is not wired up in this build — see WIRING-COMMANDS.md."),
                Json,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return null;
    }

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

        // A write surface with no token configured stays closed; EnsureAccessToken generates
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
