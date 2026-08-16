using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Speech.Callouts;

namespace ProsimCompanion.Speech.Tts;

/// <summary>
/// Warms the TTS disk cache with every phrase the FO can speak from static config — checklist
/// reads and SOP callout/advisory texts — so time-critical calls ("V one") play from cache
/// instead of paying first-synthesis latency mid-takeoff (pattern proven in Prosim2FO). The
/// speaker-role voices (purser cabin reports, the company loadsheet lead-in) are warmed too,
/// with the actual configured wording (see <see cref="CollectRolePhrases"/>).
///
/// Phrases are normalized with <see cref="AviationSpeech.Normalize"/> exactly as the render
/// path does, so the warmed cache keys match at runtime. Synthesis goes DIRECTLY to the first
/// configured caching provider (Kokoro, else Google when not local-only) — never through the
/// router, so a briefly-down Kokoro can't silently warm the whole library through paid Google.
/// Fully background; a failing provider aborts the run after a few consecutive misses.
/// </summary>
public sealed class TtsPrewarmService : Core.Hosting.IStartupModule, IDisposable
{
    private const int ConsecutiveFailureAbort = 3;
    private static readonly TimeSpan ChecklistChangeDebounce = TimeSpan.FromSeconds(2);

    private readonly IOptionsMonitor<SpeechOptions> _speech;
    private readonly IOptionsMonitor<SopOptions> _sop;
    private readonly IOptionsMonitor<CabinOptions> _cabin;
    private readonly IOptionsMonitor<VoicesOptions> _voices;
    private readonly IReadOnlyList<ITtsProvider> _providers;
    private readonly ChecklistService _checklists;
    private readonly ILogger<TtsPrewarmService> _logger;
    private readonly object _gate = new();

    private CancellationTokenSource? _run;
    private Timer? _debounce;
    private string _lastWarmedFingerprint = "";
    private bool _disposed;

    public TtsPrewarmService(
        IOptionsMonitor<SpeechOptions> speech,
        IOptionsMonitor<SopOptions> sop,
        IOptionsMonitor<CabinOptions> cabin,
        IOptionsMonitor<VoicesOptions> voices,
        IEnumerable<ITtsProvider> providers,
        ChecklistService checklists,
        ILogger<TtsPrewarmService> logger)
    {
        ArgumentNullException.ThrowIfNull(speech);
        ArgumentNullException.ThrowIfNull(sop);
        ArgumentNullException.ThrowIfNull(cabin);
        ArgumentNullException.ThrowIfNull(voices);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(checklists);
        ArgumentNullException.ThrowIfNull(logger);

        _speech = speech;
        _sop = sop;
        _cabin = cabin;
        _voices = voices;
        _providers = [.. providers];
        _checklists = checklists;
        _logger = logger;
    }

    public void Start()
    {
        // ChecklistService.Changed also fires on user interaction (select/check) — debounce
        // and fingerprint below keep that from re-warming anything.
        _checklists.Changed += OnChecklistsChanged;
        StartWarm();
    }

    public void Dispose()
    {
        _checklists.Changed -= OnChecklistsChanged;
        lock (_gate)
        {
            // The flag stops a debounce callback (or Changed event) already in flight from
            // arming a fresh warm after shutdown — StartWarm checks it under this gate.
            _disposed = true;
            _debounce?.Dispose();
            _run?.Cancel();
        }
    }

    /// <summary>Collects every warmable phrase, normalized and deduplicated. Static so tests
    /// exercise it without providers. Phrases containing '{' are live-token templates and
    /// cannot be pre-warmed.</summary>
    public static IReadOnlyList<string> CollectPhrases(
        SopOptions sop, IReadOnlyList<ChecklistDefinition> checklists)
    {
        ArgumentNullException.ThrowIfNull(sop);
        ArgumentNullException.ThrowIfNull(checklists);

        var phrases = new List<string>();

        foreach (var checklist in checklists)
        {
            phrases.Add($"{checklist.Checklist} checklist.");
            phrases.Add($"{checklist.Checklist} checklist complete.");
            foreach (var item in checklist.Items)
            {
                phrases.Add(item.Say);
                phrases.Add(item.ExpectedResponse ?? "");
            }
        }

        phrases.AddRange(
        [
            sop.ThrustSet.Text, sop.HundredKnots.Text, sop.V1.Text, sop.Rotate.Text,
            sop.V2.Text, sop.PositiveClimb.Text, sop.OneThousand.Text, sop.FiveHundred.Text,
            sop.HundredAbove.Text, sop.Minimums.Text, sop.Spoilers.Text, sop.ReverseGreen.Text,
            sop.DecelSpeed.Text, sop.OneThousandToGo.Text,
            sop.PlacardAdvisory.ApproachingText, sop.PlacardAdvisory.ExceededText,
            sop.PlacardAdvisory.GearExceededText,
            sop.Stabilized.StableText, sop.Stabilized.UnstableText,
            sop.FlowMonitor.LandingLightsAboveCeiling.Text, sop.FlowMonitor.LandingLightsBelowCeiling.Text,
            sop.FlowMonitor.FlapsNotRetracted.Text, sop.FlowMonitor.GearStillDown.Text,
            sop.FlowMonitor.ParkingBrakeWithThrust.Text, sop.FlowMonitor.SeatbeltSignsOff.Text,
            sop.FlowMonitor.BeaconOffEngineRunning.Text, sop.FlowMonitor.SpoilersNotArmed.Text,
            sop.FlowMonitor.TransponderNotSet.Text,
            sop.Weather.IcingConditions.Text, sop.Weather.AntiIceLeftOn.Text,
        ]);
        phrases.AddRange(sop.AltitudeCallouts.Select(callout => callout.Text));

        return [.. phrases
            .Where(p => !string.IsNullOrWhiteSpace(p) && !p.Contains('{', StringComparison.Ordinal))
            .Select(AviationSpeech.Normalize)
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Warmable role-voiced phrases as (voice id, normalized phrase) pairs: the purser role
    /// warms the ACTUAL configured cabin report wording (not a canned copy — the predecessor
    /// warmed phrases its cabin service never spoke) and the company role warms the fixed
    /// "Loadsheet." lead-in (the rest of a loadsheet is live numbers and cannot be warmed).
    /// A role is skipped when its voice is blank (it would render in the FO voice — already
    /// warmed) or equal to <paramref name="foVoice"/> (same cache namespace — already warmed).
    /// Static so tests exercise it without providers.
    /// </summary>
    public static IReadOnlyList<(string Voice, string Phrase)> CollectRolePhrases(
        CabinOptions cabin, VoicesOptions voices, string foVoice)
    {
        ArgumentNullException.ThrowIfNull(cabin);
        ArgumentNullException.ThrowIfNull(voices);

        var pairs = new List<(string Voice, string Phrase)>();

        void AddRole(string voice, IEnumerable<string> phrases)
        {
            if (string.IsNullOrWhiteSpace(voice) || string.Equals(voice, foVoice, StringComparison.Ordinal))
            {
                return;
            }

            pairs.AddRange(phrases
                .Where(p => !string.IsNullOrWhiteSpace(p) && !p.Contains('{', StringComparison.Ordinal))
                .Select(AviationSpeech.Normalize)
                .Distinct(StringComparer.Ordinal)
                .Select(p => (voice, p)));
        }

        AddRole(voices.Purser, [cabin.CabinSecureText, cabin.CabinReadyText, cabin.BoardingDelayText]);
        AddRole(voices.Company, ["Loadsheet."]);
        return pairs;
    }

    private void OnChecklistsChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _debounce?.Dispose();
            _debounce = new Timer(_ => StartWarm(), null, ChecklistChangeDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void StartWarm()
    {
        CancellationTokenSource run;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _run?.Cancel();
            _run = new CancellationTokenSource();
            run = _run;
        }

        _ = Task.Run(() => WarmAsync(run.Token));
    }

    private async Task WarmAsync(CancellationToken cancellationToken)
    {
        try
        {
            var provider = PickCachingProvider();
            if (provider is null)
            {
                return; // No caching provider configured — WinRT/SAPI are fast anyway.
            }

            var phrases = CollectPhrases(_sop.CurrentValue, _checklists.Definitions());

            // Role voices are warmed on the same provider with an override — the provider's
            // per-voice cache namespacing keeps them isolated from the FO phrases above.
            var foVoice = provider.Name == "google"
                ? _speech.CurrentValue.GoogleVoice
                : _speech.CurrentValue.KokoroVoice;
            var work = phrases.Select(p => (Voice: (string?)null, Phrase: p))
                .Concat(CollectRolePhrases(_cabin.CurrentValue, _voices.CurrentValue, foVoice)
                    .Select(rp => (Voice: (string?)rp.Voice, Phrase: rp.Phrase)))
                .ToList();

            var fingerprint = provider.Name + "\n"
                + string.Join("\n", work.Select(w => (w.Voice is null ? "" : w.Voice + "|") + w.Phrase));
            if (fingerprint == _lastWarmedFingerprint)
            {
                return; // Nothing changed since the last successful warm.
            }

            var cached = 0;
            var failed = 0;
            var consecutiveFailures = 0;
            foreach (var (voice, phrase) in work)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await provider.SynthesizeAsync(phrase, cancellationToken, voice).ConfigureAwait(false);
                    cached++;
                    consecutiveFailures = 0;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Pre-warm failed for a phrase via {Provider}", provider.Name);
                    failed++;
                    if (++consecutiveFailures >= ConsecutiveFailureAbort)
                    {
                        _logger.LogWarning(
                            "TTS pre-warm aborted — {Provider} failed {Count} phrases in a row",
                            provider.Name, consecutiveFailures);
                        return;
                    }
                }
            }

            // A partially-failed run must stay retryable: recording the fingerprint would
            // freeze the cold phrases until the config or provider next changes.
            if (failed == 0)
            {
                _lastWarmedFingerprint = fingerprint;
            }

            _logger.LogInformation("TTS pre-warm complete: {Cached}/{Total} via {Provider} ({Failed} failed)",
                cached, work.Count, provider.Name, failed);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer warm or shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TTS pre-warm crashed");
        }
    }

    /// <summary>Kokoro when configured; else Google (never in local-only mode). Null when no
    /// caching provider is available.</summary>
    private ITtsProvider? PickCachingProvider()
    {
        var localOnly = _speech.CurrentValue.LocalOnly;
        var kokoro = _providers.FirstOrDefault(p => p.Name == "kokoro");
        if (kokoro is { IsConfigured: true })
        {
            return kokoro;
        }

        if (localOnly)
        {
            return null;
        }

        var google = _providers.FirstOrDefault(p => p.Name == "google");
        return google is { IsConfigured: true } ? google : null;
    }
}
