using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Playback;

namespace ProsimCompanion.Speech.Cabin;

/// <summary>
/// The Prosim2GSX "cabin ding" cues: an optional chime at application start and one when the
/// final loadsheet is transmitted. Plays through the synthesized cabin chime directly (not the
/// speech arbiter) — a ding is an ambience cue, not an utterance, and must not queue behind or
/// suppress FO speech.
/// </summary>
public sealed class CabinDingService : IHostedService, IDisposable
{
    private readonly LoadsheetStore _loadsheets;
    private readonly ISpeechPlayback _playback;
    private readonly IOptionsMonitor<CabinOptions> _options;
    private readonly ILogger<CabinDingService> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private LoadsheetSlotStatus _lastFinalStatus = LoadsheetSlotStatus.None;
    private IDisposable? _subscription;

    public CabinDingService(
        LoadsheetStore loadsheets,
        ISpeechPlayback playback,
        IOptionsMonitor<CabinOptions> options,
        ILogger<CabinDingService> logger)
    {
        ArgumentNullException.ThrowIfNull(loadsheets);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _loadsheets = loadsheets;
        _playback = playback;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lastFinalStatus = _loadsheets.Snapshot().Final.Status;
        _subscription = _loadsheets.Observe(OnLoadsheetsChanged);

        if (_options.CurrentValue.DingOnStartup)
        {
            Play("startup");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _shutdown.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose() => _shutdown.Dispose();

    private void OnLoadsheetsChanged(LoadsheetSnapshot snapshot)
    {
        // Edge-detect the final going to Sent — resends re-enter Sent from Generating and
        // ding again, which matches the predecessor (every transmitted final chimed).
        var status = snapshot.Final.Status;
        var wasSent = _lastFinalStatus == LoadsheetSlotStatus.Sent;
        _lastFinalStatus = status;

        if (status == LoadsheetSlotStatus.Sent && !wasSent && _options.CurrentValue.DingOnFinal)
        {
            Play("final loadsheet");
        }
    }

    private void Play(string reason)
        => _ = Task.Run(async () =>
        {
            try
            {
                await _playback.PlayChimeAsync("cabin", _shutdown.Token).ConfigureAwait(false);
                _logger.LogDebug("Cabin ding played ({Reason})", reason);
            }
            catch (OperationCanceledException)
            {
                // shutdown — fine
            }
        });
}
