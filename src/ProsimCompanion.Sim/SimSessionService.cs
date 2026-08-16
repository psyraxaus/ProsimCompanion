using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Sim;

/// <summary>
/// Samples the raw session signals (system events via <see cref="SimSessionSignals"/>, the
/// CAMERA STATE SimVar, and on MSFS 2024 the avatar SimVar) on a fixed tick, runs the pure
/// <see cref="SimSessionEvaluator"/>, and publishes the result to the
/// <see cref="SimSessionStore"/> for session-gated automation and the web UI. Degrades with
/// the rest of the Sim pillar: without a SimConnect connection the published phase stays
/// <see cref="SimSessionPhase.Unknown"/>.
/// </summary>
public sealed class SimSessionService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(500);

    private readonly ISimVars _simVars;
    private readonly SimSessionSignals _signals;
    private readonly SimSessionStore _store;
    private readonly ILogger<SimSessionService> _logger;
    private readonly SimSessionEvaluator _evaluator = new();
    private SimSessionPhase _loggedPhase = SimSessionPhase.Unknown;

    public SimSessionService(
        ISimVars simVars,
        SimSessionSignals signals,
        SimSessionStore store,
        ILogger<SimSessionService> logger)
    {
        ArgumentNullException.ThrowIfNull(simVars);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _simVars = simVars;
        _signals = signals;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var camera = _simVars.Subscribe(ProsimDataRefNames.SimVars.CameraState);
        IDataRefSubscription<bool>? avatar = null;

        try
        {
            using var timer = new PeriodicTimer(TickInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var (connected, simRunning, paused, simVersion, isMsfs2024) = _signals.Read();

                // IS AVATAR exists only in MSFS 2024 — registering it on 2020 would log a
                // SimConnect error every session, so the subscription waits for the version.
                if (isMsfs2024)
                {
                    avatar ??= _simVars.Subscribe(ProsimDataRefNames.SimVars.IsAvatar);
                }

                var inputs = new SimSessionInputs(
                    connected,
                    simRunning,
                    paused,
                    CameraState: Fresh(camera) ? camera.Value : null,
                    IsAvatar: avatar is not null && Fresh(avatar) ? avatar.Value : null);

                var phase = _evaluator.ProcessTick(inputs);
                if (phase != _loggedPhase)
                {
                    _logger.LogInformation(
                        "MSFS session {Old} -> {New} (camera {Camera}, sim running {Running}, paused {Paused})",
                        _loggedPhase,
                        phase,
                        inputs.CameraState?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?",
                        simRunning,
                        paused);
                    _loggedPhase = phase;
                }

                _store.Publish(new SimSessionSnapshot(phase, simRunning, paused, inputs.CameraState, simVersion));
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown.
        }
        finally
        {
            avatar?.Dispose();
        }
    }

    /// <summary>A value that survives a disconnect is not evidence about the current session —
    /// stale reads map to "unknown" and the evaluator holds.</summary>
    private static bool Fresh(IDataRefSubscription subscription)
        => subscription.RawValue is not null && !subscription.IsStale;
}
