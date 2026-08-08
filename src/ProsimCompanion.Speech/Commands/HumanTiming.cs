using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Speech.Commands;

/// <summary>
/// Human-like hold/gap timings for automated button sequences (Prosim2FO parity). Pure
/// functions over <see cref="HumanizeOptions"/> and the configured times; a configured delay
/// is a functional minimum (MCDU page-change time) and is never undercut — humanization only
/// ever adds time. Without it, the virtual pilot machine-guns MCDU keys at wire speed, which
/// is exactly the unrealism live testing flagged.
/// </summary>
public static class HumanTiming
{
    /// <summary>True for CDU keys that change the displayed page (PERF, F-PLN, arrows, …) —
    /// a human pauses to scan the new page before the next press. LSK and CLEAR presses stay
    /// in-flow. Accepts a full dataref or a bare key suffix.</summary>
    public static bool IsPageKey(string datarefOrSuffix)
    {
        ArgumentNullException.ThrowIfNull(datarefOrSuffix);
        var name = datarefOrSuffix.Contains("_KEY_", StringComparison.OrdinalIgnoreCase)
            ? datarefOrSuffix[(datarefOrSuffix.IndexOf("_KEY_", StringComparison.OrdinalIgnoreCase) + 5)..]
            : datarefOrSuffix;
        return datarefOrSuffix.Contains("CDU", StringComparison.OrdinalIgnoreCase)
            ? !name.StartsWith("LSK", StringComparison.OrdinalIgnoreCase)
                && !name.StartsWith("CLEAR", StringComparison.OrdinalIgnoreCase)
            : false;
    }

    /// <summary>How long to hold this press: the configured hold jittered by ±HoldJitter.</summary>
    public static int Hold(HumanizeOptions options, int baseHoldMs, Random rng)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rng);
        if (!options.Enabled)
        {
            return Math.Max(1, baseHoldMs);
        }

        var factor = 1 + (rng.NextDouble() * 2 - 1) * Math.Clamp(options.HoldJitter, 0, 0.9);
        return Math.Max(40, (int)(baseHoldMs * factor));
    }

    /// <summary>Gap before the next key: the configured delay stretched by up to GapJitter,
    /// scaled by Tempo, plus an occasional think/scan pause (three times as likely after a
    /// page-changing key). Never less than the configured delay.</summary>
    public static int Gap(HumanizeOptions options, int baseDelayMs, bool afterPageKey, Random rng)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rng);
        if (!options.Enabled)
        {
            return baseDelayMs;
        }

        var tempo = Math.Clamp(options.Tempo, 0.25, 4.0);
        var gap = baseDelayMs * (1 + rng.NextDouble() * Math.Max(0, options.GapJitter)) * tempo;

        var chance = Math.Clamp(
            afterPageKey ? options.ThinkPauseChance * 3 : options.ThinkPauseChance, 0, 1);
        if (rng.NextDouble() < chance)
        {
            gap += (options.ThinkPauseMinMs
                + rng.NextDouble() * Math.Max(0, options.ThinkPauseMaxMs - options.ThinkPauseMinMs)) * tempo;
        }

        return (int)Math.Max(baseDelayMs, gap);
    }
}
