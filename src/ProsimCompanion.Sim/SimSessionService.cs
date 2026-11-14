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
    private readonly Core.EventLog.JsonlEventLog? _eventLog;
    private readonly SimSessionEvaluator _evaluator = new();
    private SimSessionPhase _loggedPhase = SimSessionPhase.Unknown;
    private bool _loggedHolding;

    public SimSessionService(
        ISimVars simVars,
        SimSessionSignals signals,
        SimSessionStore store,
        ILogger<SimSessionService> logger,
        Core.EventLog.JsonlEventLog? eventLog = null)
    {
        ArgumentNullException.ThrowIfNull(simVars);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _simVars = simVars;
        _signals = signals;
        _store = store;
        _logger = logger;
        _eventLog = eventLog;
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
                var (connected, simRunning, paused, simVersion, isMsfs2024, simQuit) = _signals.Read();

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
                    IsAvatar: avatar is not null && Fresh(avatar) ? avatar.Value : null,
                    SimQuit: simQuit);

                var phase = _evaluator.ProcessTick(inputs);
                LogLinkHold(_evaluator.HoldingAcrossLinkLoss, phase);
                if (_evaluator.HoldingAcrossLinkLoss)
                {
                    // The snapshot from before the drop stands: the signals read "not running,
                    // paused, no camera" while the link is down, and none of that was observed.
                    continue;
                }

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

    /// <summary>Says once when a live session is held across a lost SimConnect link, and once
    /// how the hold ended (issue #246). The wording is a probe signature — keep it.</summary>
    private void LogLinkHold(bool holding, SimSessionPhase phase)
    {
        if (holding == _loggedHolding)
        {
            return;
        }

        _loggedHolding = holding;
        if (holding)
        {
            _logger.LogWarning(
                "SimConnect link lost in a flight session — session {Phase} held for up to {Seconds} s",
                phase, SimSessionEvaluator.LinkLossHoldTicks * TickInterval.TotalSeconds);
            _eventLog?.Record("sim-link-hold", new { state = "held", phase = phase.ToString() });
        }
        else
        {
            var kept = phase is SimSessionPhase.InSession or SimSessionPhase.Walkaround;
            _logger.LogInformation(
                "SimConnect link hold ended: session {Outcome} ({Phase})",
                kept ? "kept" : "ended", phase);
            _eventLog?.Record("sim-link-hold", new { state = kept ? "kept" : "ended", phase = phase.ToString() });
        }
    }

    /// <summary>A value that survives a disconnect is not evidence about the current session —
    /// stale reads map to "unknown" and the evaluator holds.</summary>
    private static bool Fresh(IDataRefSubscription subscription)
        => subscription.RawValue is not null && !subscription.IsStale;
}
