using Microsoft.Extensions.Logging;

namespace ProsimCompanion.Speech.SayIntentions;

/// <summary>The two SAPI writes the FO's radio work needs, shared by the services that
/// transmit: sayAs (the text ATC hears, 255-char cap, silent on the SayIntentions side — live
/// check 2026-10-25) and setVar (the SIAI L:vars). Failures log and return; nothing throws.</summary>
internal static class SapiClient
{
    public const string ApiBase = "https://apipri.sayintentions.ai/sapi";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task<bool> SayAsAsync(string apiKey, string message, ILogger logger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(logger);

        var truncated = message.Length > 255 ? message[..255] : message;
        try
        {
            using var response = await Http.GetAsync(
                $"{ApiBase}/sayAs?api_key={Uri.EscapeDataString(apiKey)}&channel=COM1&message={Uri.EscapeDataString(truncated)}",
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("sayAs failed: {Status}", (int)response.StatusCode);
                return false;
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "sayAs failed");
            return false;
        }
    }

    public static async Task<bool> SetVarAsync(string apiKey, string name, string value, ILogger logger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            using var response = await Http.GetAsync(
                $"{ApiBase}/setVar?api_key={Uri.EscapeDataString(apiKey)}&var={Uri.EscapeDataString(name)}&value={Uri.EscapeDataString(value)}&category=L",
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("setVar {Name} failed: {Status}", name, (int)response.StatusCode);
                return false;
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "setVar {Name} failed", name);
            return false;
        }
    }
}
