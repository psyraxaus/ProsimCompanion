using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Core.Sessions;

/// <summary>Immutable identity of the session being finalized.</summary>
/// <param name="SessionPath">Full path of the session's JSONL event log.</param>
/// <param name="SessionId">The log's file name without extension (e.g. "session-20260808-101500") —
/// the idempotency key every store uses.</param>
public sealed record SessionFinalizationContext(string SessionPath, string SessionId);

/// <summary>
/// One end-of-flight bookkeeping action (debrief, logbook fold, tech-log sector fold…).
/// Steps run strictly in ascending <see cref="Order"/> so later steps can rely on earlier
/// ones having read the log first; a throwing step is logged and skipped, never fatal.
/// </summary>
public interface ISessionFinalizationStep
{
    /// <summary>Short name for logs and the session event.</summary>
    string Name { get; }

    /// <summary>Ascending execution order. Convention: debrief 10, logbook 20, tech log 30 —
    /// the debrief reads the log before the folds mutate their stores, so its "first landing
    /// into X" comparison can never race its own flight's fold.</summary>
    int Order { get; }

    Task RunAsync(SessionFinalizationContext context, CancellationToken cancellationToken);
}

/// <summary>
/// The single shutdown-edge coordinator for end-of-flight bookkeeping. Prosim2FO ran the
/// debrief, logbook fold and tech-log fold off three independent 750/1200/1500 ms delays and
/// relied on those constants staying ordered; this replaces that with one flush delay followed
/// by an explicitly ordered run, once per flight (re-armed when a new flight starts). Runs
/// off-thread so it never blocks the phase engine, and never throws.
/// </summary>
public sealed class SessionFinalizer : IDisposable
{
    /// <summary>Default pause before reading the session log, letting the event-log writer
    /// flush the shutdown-edge records the steps are about to read.</summary>
    public static readonly TimeSpan DefaultFlushDelay = TimeSpan.FromMilliseconds(1500);

    private readonly IFlightPhaseSource _flight;
    private readonly JsonlEventLog _eventLog;
    private readonly List<ISessionFinalizationStep> _steps;
    private readonly ILogger<SessionFinalizer> _logger;
    private readonly TimeSpan _flushDelay;
    private readonly object _gate = new();
    private bool _started;
    private bool _doneThisFlight;

    public SessionFinalizer(
        IFlightPhaseSource flight,
        JsonlEventLog eventLog,
        IEnumerable<ISessionFinalizationStep> steps,
        ILogger<SessionFinalizer> logger,
        TimeSpan? flushDelay = null)
    {
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(logger);

        _flight = flight;
        _eventLog = eventLog;
        _steps = steps.OrderBy(s => s.Order).ToList();
        _logger = logger;
        _flushDelay = flushDelay ?? DefaultFlushDelay;
    }

    /// <summary>The in-flight (or last) finalization run — awaitable by tests so they can
    /// assert on step effects without polling.</summary>
    public Task? LastRun { get; private set; }

    public void Start()
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        _flight.PhaseChanged += OnPhaseChanged;
        _logger.LogInformation("Session finalizer started ({Count} step(s): {Steps})",
            _steps.Count, string.Join(", ", _steps.Select(s => s.Name)));
    }

    public void Dispose() => _flight.PhaseChanged -= OnPhaseChanged;

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
    {
        // A fresh flight re-arms finalization; either edge works (Preflight covers a
        // turnaround that never fully powers down, TakeoffRoll covers a phase-engine
        // resync that skipped Preflight).
        if (e.Current is FlightPhase.TakeoffRoll or FlightPhase.Preflight)
        {
            lock (_gate)
            {
                _doneThisFlight = false;
            }
        }

        if (e.Current != FlightPhase.Shutdown)
        {
            return;
        }

        lock (_gate)
        {
            if (_doneThisFlight)
            {
                return;
            }

            _doneThisFlight = true;
            LastRun = Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        try
        {
            if (_flushDelay > TimeSpan.Zero)
            {
                await Task.Delay(_flushDelay).ConfigureAwait(false);
            }

            var path = _eventLog.Path;
            var context = new SessionFinalizationContext(path, System.IO.Path.GetFileNameWithoutExtension(path));

            foreach (var step in _steps)
            {
                try
                {
                    await step.RunAsync(context, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Session finalization step {Step} failed", step.Name);
                }
            }

            _eventLog.Record("session.finalized", new { steps = _steps.Select(s => s.Name).ToArray() });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session finalization failed");
        }
    }
}
