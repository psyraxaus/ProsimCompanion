using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ProsimCompanion.Speech.Tts;

/// <summary>
/// Monthly character counter for paid TTS, persisted per provider as a <c>{"yyyy-MM": chars}</c>
/// map keyed on UTC month. Google keeps the historical <c>{cacheRoot}/usage.json</c> (file
/// format and name carried from Prosim2FO for migration friendliness); every other provider
/// gets its own <c>usage.{provider}.json</c> so the counters never cross-contaminate.
/// Incremented only on real API calls, never cache hits. Unlike the predecessor (which
/// tracked but never enforced), <see cref="IsOverBudget"/> is consulted by the paid providers
/// so the chain falls through to offline voices instead of billing. Every failure is
/// swallowed — a broken counter must not break speech.
/// </summary>
public sealed class TtsUsageTracker
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly ILogger<TtsUsageTracker> _logger;
    private readonly object _gate = new();

    public TtsUsageTracker(ILogger<TtsUsageTracker> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>Characters consumed in the current UTC month by <paramref name="provider"/>
    /// (defaults to google so the pre-existing call sites read the historical file).</summary>
    public int CharactersThisMonth(string cacheRoot, string provider = "google")
    {
        lock (_gate)
        {
            return Read(cacheRoot, provider).GetValueOrDefault(MonthKey());
        }
    }

    /// <summary>True when a further <paramref name="upcomingChars"/>-character call would meet
    /// or exceed the budget (0 = enforcement disabled).</summary>
    public bool IsOverBudget(string cacheRoot, int budget, int upcomingChars, string provider = "google")
        => budget > 0 && CharactersThisMonth(cacheRoot, provider) + upcomingChars > budget;

    /// <summary>Adds a synthesis call's character count to this month's tally for
    /// <paramref name="provider"/>.</summary>
    public void Increment(string cacheRoot, int chars, string provider = "google")
    {
        lock (_gate)
        {
            try
            {
                var usage = Read(cacheRoot, provider);
                var key = MonthKey();
                usage[key] = usage.GetValueOrDefault(key) + chars;

                Directory.CreateDirectory(cacheRoot);
                File.WriteAllText(
                    Path.Combine(cacheRoot, FileNameFor(provider)),
                    JsonSerializer.Serialize(usage, SerializerOptions));

                _logger.LogInformation(
                    "{Provider} TTS usage {Month}: {Chars:N0} characters (this call: {Call})",
                    provider, key, usage[key], chars);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "TTS usage tracking failed");
            }
        }
    }

    /// <summary><c>usage.json</c> for google (unchanged since Prosim2FO — an existing counter
    /// keeps counting after an upgrade), <c>usage.{provider}.json</c> for everyone else.</summary>
    internal static string FileNameFor(string provider)
        => string.Equals(provider, "google", StringComparison.OrdinalIgnoreCase)
            ? "usage.json"
            : $"usage.{provider.ToLowerInvariant()}.json";

    private Dictionary<string, int> Read(string cacheRoot, string provider)
    {
        try
        {
            var path = Path.Combine(cacheRoot, FileNameFor(provider));
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path)) ?? [];
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TTS usage read failed");
        }

        return [];
    }

    private static string MonthKey()
        => DateTime.UtcNow.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
}
