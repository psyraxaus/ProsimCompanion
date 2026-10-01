using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Speech.Tts;

/// <summary>The Core-facing read-only view over <see cref="TtsUsageTracker"/>: resolves the
/// cache root from the live options so the settings page never needs the speech pillar.</summary>
public sealed class TtsUsageReadout : ITtsUsageReadout
{
    private readonly IOptionsMonitor<SpeechOptions> _options;
    private readonly TtsUsageTracker _usage;

    public TtsUsageReadout(IOptionsMonitor<SpeechOptions> options, TtsUsageTracker usage)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(usage);
        _options = options;
        _usage = usage;
    }

    public int CharactersThisMonth(string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        return _usage.CharactersThisMonth(SpeechPaths.CacheRoot(_options.CurrentValue), provider);
    }
}
