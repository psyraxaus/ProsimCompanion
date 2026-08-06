using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ProsimCompanion.Speech.Tts;

/// <summary>
/// Monthly character counter for paid TTS, persisted as <c>{cacheRoot}/usage.json</c> — a
/// <c>{"yyyy-MM": chars}</c> map keyed on UTC month (file format carried from Prosim2FO for
/// migration friendliness). Incremented only on real API calls, never cache hits. Unlike the
/// predecessor (which tracked but never enforced), <see cref="IsOverBudget"/> is consulted by
/// the Google provider so the chain falls through to offline voices instead of billing.
/// Every failure is swallowed — a broken counter must not break speech.
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

    /// <summary>Characters consumed in the current UTC month.</summary>
    public int CharactersThisMonth(string cacheRoot)
    {
        lock (_gate)
        {
            return Read(cacheRoot).GetValueOrDefault(MonthKey());
        }
    }

    /// <summary>True when a further <paramref name="upcomingChars"/>-character call would meet
    /// or exceed the budget (0 = enforcement disabled).</summary>
    public bool IsOverBudget(string cacheRoot, int budget, int upcomingChars)
        => budget > 0 && CharactersThisMonth(cacheRoot) + upcomingChars > budget;

    /// <summary>Adds a synthesis call's character count to this month's tally.</summary>
    public void Increment(string cacheRoot, int chars)
    {
        lock (_gate)
        {
            try
            {
                var usage = Read(cacheRoot);
                var key = MonthKey();
                usage[key] = usage.GetValueOrDefault(key) + chars;

                Directory.CreateDirectory(cacheRoot);
                File.WriteAllText(
                    Path.Combine(cacheRoot, "usage.json"),
                    JsonSerializer.Serialize(usage, SerializerOptions));

                _logger.LogInformation(
                    "Google TTS usage {Month}: {Chars:N0} characters (this call: {Call})",
                    key, usage[key], chars);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "TTS usage tracking failed");
            }
        }
    }

    private Dictionary<string, int> Read(string cacheRoot)
    {
        try
        {
            var path = Path.Combine(cacheRoot, "usage.json");
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
