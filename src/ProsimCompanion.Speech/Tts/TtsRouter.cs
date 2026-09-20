using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Speech.Tts;

/// <summary>
/// Walks the provider chain in configured order and returns the first successful synthesis.
/// A provider that throws goes into a cooldown window and is skipped until it elapses — one
/// slow dead LAN host must not add its timeout to every utterance. Provider health is
/// published to the <see cref="SpeechStatusStore"/> for the /speech page.
/// </summary>
public sealed class TtsRouter
{
    /// <summary>How long a failed provider is skipped before it gets another chance.</summary>
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(60);

    private readonly IReadOnlyList<ITtsProvider> _providers;
    private readonly IOptionsMonitor<SpeechOptions> _options;
    private readonly SpeechStatusStore _store;
    private readonly ILogger<TtsRouter> _logger;
    private readonly Dictionary<string, DateTimeOffset> _cooldownUntil = [];
    private readonly HashSet<string> _succeeded = [];
    private readonly object _gate = new();

    private readonly ConnectionStatusStore? _connections;

    /// <param name="connections">Footer/status dot for the network voice
    /// (<see cref="Subsystems.Tts"/>); optional so tests need no store.</param>
    public TtsRouter(
        IEnumerable<ITtsProvider> providers,
        IOptionsMonitor<SpeechOptions> options,
        SpeechStatusStore store,
        ILogger<TtsRouter> logger,
        ConnectionStatusStore? connections = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _providers = [.. providers];
        _options = options;
        _store = store;
        _logger = logger;
        _connections = connections;
        PublishHealth();
    }

    /// <summary>Asks every configured network provider that can answer a health check
    /// (Kokoro) whether it is up, now. A provider that answers has its failure cooldown
    /// cleared so the next utterance uses it at once instead of waiting out the 60 s — the
    /// "Reconnect voice services" button (2026-09-20: Kokoro refused connections while the
    /// voice box finished an update). One readable sentence per provider.</summary>
    public async Task<string> ProbeNetworkProvidersAsync(CancellationToken cancellationToken)
    {
        var localOnly = _options.CurrentValue.LocalOnly;
        var lines = new List<string>();
        foreach (var provider in _providers)
        {
            if (provider is not IProbeableTtsProvider probeable || !provider.IsConfigured)
            {
                continue;
            }

            if (localOnly)
            {
                lines.Add($"Voice ({provider.Name}): skipped — local-only mode.");
                continue;
            }

            var ok = await probeable.ProbeAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (ok)
                {
                    // A health answer is as good as a synthesis for the chain's purposes:
                    // the provider is reachable, so stop skipping it.
                    _cooldownUntil.Remove(provider.Name);
                    _succeeded.Add(provider.Name);
                }
            }

            lines.Add(ok
                ? $"Voice ({provider.Name}): answers — cooldown cleared."
                : $"Voice ({provider.Name}): not reachable — the next voice in the chain keeps covering.");
        }

        PublishHealth();
        return lines.Count == 0 ? "Voice: no network provider configured." : string.Join(' ', lines);
    }

    /// <summary>
    /// Synthesizes via the first willing provider. Returns null only when every provider is
    /// unavailable or failed — the caller logs the utterance as lost; nothing throws out.
    /// Cancellation (pre-emption) aborts without penalizing the provider in flight.
    /// <paramref name="voiceOverride"/> (speaker-role voice) is handed to every provider tried
    /// — a provider without configurable voices ignores it rather than failing the chain.
    /// <paramref name="voiceForProvider"/> (accent localization, issue #53) may supply a
    /// provider-specific voice by provider name; a null answer falls back to
    /// <paramref name="voiceOverride"/> — so a Chirp locale id is never handed to Kokoro.
    /// </summary>
    public async Task<TtsAudio?> SynthesizeAsync(
        string text,
        CancellationToken cancellationToken,
        string? voiceOverride = null,
        Func<string, string?>? voiceForProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var localOnly = _options.CurrentValue.LocalOnly;
        var now = DateTimeOffset.UtcNow;

        foreach (var provider in _providers)
        {
            if (!provider.IsConfigured || (localOnly && provider.IsNetworkProvider))
            {
                continue;
            }

            lock (_gate)
            {
                if (_cooldownUntil.TryGetValue(provider.Name, out var until) && now < until)
                {
                    continue;
                }
            }

            try
            {
                var effectiveVoice = voiceForProvider?.Invoke(provider.Name) ?? voiceOverride;
                var audio = await provider.SynthesizeAsync(text, cancellationToken, effectiveVoice).ConfigureAwait(false);
                lock (_gate)
                {
                    _cooldownUntil.Remove(provider.Name);
                    _succeeded.Add(provider.Name);
                }

                PublishHealth();
                return audio;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // Pre-emption, not a provider fault.
            }
            catch (Exception ex)
            {
                // The cooldown starts when the failure happened, not when this call began
                // (issue #113): a provider that streamed for seconds before failing must not
                // get a shorter window.
                lock (_gate)
                {
                    _cooldownUntil[provider.Name] = DateTimeOffset.UtcNow + FailureCooldown;
                }

                if (ex is TimeoutException)
                {
                    // An operational timeout is one readable sentence, not a four-level
                    // socket trace in CMTrace.
                    _logger.LogWarning(
                        "TTS provider {Provider} failed: {Reason}; cooling down {CooldownSeconds}s",
                        provider.Name, ex.Message, (int)FailureCooldown.TotalSeconds);
                }
                else
                {
                    _logger.LogWarning(ex,
                        "TTS provider {Provider} failed; cooling down {CooldownSeconds}s",
                        provider.Name, (int)FailureCooldown.TotalSeconds);
                }

                PublishHealth();
            }
        }

        _logger.LogWarning("No TTS provider could synthesize the utterance ({Length} chars)", text.Length);
        return null;
    }

    /// <summary>Current provider chain as the /speech page shows it.</summary>
    public IReadOnlyList<TtsProviderView> DescribeProviders()
    {
        var localOnly = _options.CurrentValue.LocalOnly;
        var now = DateTimeOffset.UtcNow;
        var views = new List<TtsProviderView>(_providers.Count);
        foreach (var provider in _providers)
        {
            TtsProviderHealth health;
            var detail = "";
            if (!provider.IsConfigured)
            {
                health = TtsProviderHealth.Disabled;
                detail = "not configured";
            }
            else if (localOnly && provider.IsNetworkProvider)
            {
                health = TtsProviderHealth.Disabled;
                detail = "local-only mode";
            }
            else
            {
                lock (_gate)
                {
                    if (_cooldownUntil.TryGetValue(provider.Name, out var until) && now < until)
                    {
                        health = TtsProviderHealth.Failed;
                        detail = $"retry in {(int)(until - now).TotalSeconds}s";
                    }
                    else
                    {
                        health = _succeeded.Contains(provider.Name)
                            ? TtsProviderHealth.Healthy
                            : TtsProviderHealth.Unknown;
                    }
                }
            }

            views.Add(new TtsProviderView(provider.Name, health, detail));
        }

        return views;
    }

    private void PublishHealth()
    {
        var views = DescribeProviders();
        _store.Update(s => s with { Providers = views });
        _connections?.Set(Subsystems.Tts, NetworkVoiceState(views));
    }

    /// <summary>The footer's TTS dot: the state of the first network provider in the chain
    /// (Kokoro). Reality, like every other dot — a cooldown shows as disconnected even
    /// though the chain still speaks through the next provider.</summary>
    internal TtsProviderHealth? NetworkVoiceHealth(IReadOnlyList<TtsProviderView> views)
    {
        for (var i = 0; i < _providers.Count && i < views.Count; i++)
        {
            if (_providers[i].IsNetworkProvider)
            {
                return views[i].Health;
            }
        }

        return null;
    }

    private ConnectionState NetworkVoiceState(IReadOnlyList<TtsProviderView> views) => NetworkVoiceHealth(views) switch
    {
        TtsProviderHealth.Healthy => ConnectionState.Connected,
        TtsProviderHealth.Failed => ConnectionState.Disconnected,
        TtsProviderHealth.Unknown => ConnectionState.Connecting,
        _ => ConnectionState.Disabled,
    };
}
