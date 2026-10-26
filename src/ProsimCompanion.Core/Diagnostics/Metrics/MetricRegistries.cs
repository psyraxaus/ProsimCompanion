using System.Collections.Concurrent;

namespace ProsimCompanion.Core.Diagnostics.Metrics;

/// <summary>The latency series the VOICE page prints. Feature projects record under these
/// names; the collector reads them back by name, so adding a series is one constant.</summary>
public static class LatencyNames
{
    /// <summary>The ASR HTTP call, request sent to transcript parsed.</summary>
    public const string Asr = "asr";

    /// <summary>LLM stream: request sent to first text delta.</summary>
    public const string LlmFirstToken = "llm_first_token";

    /// <summary>LLM stream: request sent to stream end.</summary>
    public const string LlmTotal = "llm_total";

    /// <summary>One successful TTS provider synthesis.</summary>
    public const string Tts = "tts";

    /// <summary>Utterance end (pilot stopped talking) to the first FO audio out.</summary>
    public const string EndToEnd = "e2e";

    /// <summary>Not a latency: the LLM's generation rate in tokens per second, one sample per
    /// completed stream, kept in the same registry so the VOICE page has one place to read.</summary>
    public const string LlmTokensPerSecond = "llm_tok_s";
}

/// <summary>The rate series the SIM and REMOTE pages print.</summary>
public static class RateNames
{
    /// <summary>ProSim dataref updates dispatched to subscribers.</summary>
    public const string Datarefs = "datarefs";

    /// <summary>SimConnect simvar updates dispatched to subscribers.</summary>
    public const string Simvars = "simvars";

    /// <summary>HTTP requests under /api/.</summary>
    public const string Api = "api";
}

/// <summary>Latency trackers by name, created on first use. One singleton, so a feature project
/// records with <c>registry[LatencyNames.Asr].Record(ms)</c> and needs no interface per metric.</summary>
public sealed class LatencyRegistry
{
    private readonly ConcurrentDictionary<string, LatencyTracker> _trackers = new(StringComparer.Ordinal);

    /// <summary>The tracker for <paramref name="name"/>, created when first asked for.</summary>
    public LatencyTracker this[string name]
    {
        get
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            return _trackers.GetOrAdd(name, static _ => new LatencyTracker());
        }
    }

    /// <summary>The names recorded so far.</summary>
    public IReadOnlyCollection<string> Names => [.. _trackers.Keys];
}

/// <summary>Rate counters by name, created on first use.</summary>
public sealed class RateRegistry
{
    private readonly ConcurrentDictionary<string, RateCounter> _counters = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset>? _clock;

    public RateRegistry(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock;
    }

    /// <summary>The counter for <paramref name="name"/>, created when first asked for.</summary>
    public RateCounter this[string name]
    {
        get
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            return _counters.GetOrAdd(name, _ => new RateCounter(_clock));
        }
    }
}
