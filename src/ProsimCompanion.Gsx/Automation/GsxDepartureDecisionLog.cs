using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Gsx.Automation;

/// <summary>
/// Where the departure automation writes its decisions and session events. One interface
/// for the three sinks (log file, diagnostics store, session event log) plus the per-action
/// dedup, so the effect applier and the host record through one seam and a test can assert
/// the recorded sequence against a fake instead of three stores.
/// </summary>
public interface IGsxDecisionLog
{
    /// <summary>Records a decision unconditionally (log line + diagnostics + session event).</summary>
    void Record(string action, string reason);

    /// <summary>Records only when this action's reason differs from the last one recorded
    /// for the same action — a stable hold never floods the log.</summary>
    void RecordOnce(string action, string reason);

    /// <summary>Writes a session event (the JSONL record the probes evaluate).</summary>
    void RecordEvent(string type, object? payload = null);
}

/// <summary>
/// The departure automation's decision log: the per-action dedup dictionary, the CMTrace log
/// line, the diagnostics decision row and the <c>gsx-decision</c> session event. The log
/// category stays <see cref="GsxAutomationService"/> on purpose: the log lines read exactly as
/// they did when the shell wrote them itself, so flight-verification probes keep matching.
/// </summary>
public sealed class GsxDepartureDecisionLog : IGsxDecisionLog
{
    private readonly Dictionary<string, string> _lastReasonByAction = new(StringComparer.Ordinal);
    private readonly ILogger<GsxAutomationService> _logger;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly JsonlEventLog _eventLog;

    public GsxDepartureDecisionLog(
        GsxDiagnosticsStore diagnostics,
        JsonlEventLog eventLog,
        ILogger<GsxAutomationService> logger)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        _diagnostics = diagnostics;
        _eventLog = eventLog;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Record(string action, string reason)
    {
        lock (_lastReasonByAction)
        {
            _lastReasonByAction[action] = reason;
        }
        _logger.LogInformation("GSX automation: {Action} — {Reason}", action, reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, action, reason));
        _eventLog.Record("gsx-decision", new { action, reason });
    }

    /// <summary>Deduplicated per action: records only when that action's reason changes, so a
    /// stable hold never floods the log (smoke-test find: a single shared last-summary let
    /// alternating actions defeat the dedupe).</summary>
    public void RecordOnce(string action, string reason)
    {
        lock (_lastReasonByAction)
        {
            if (_lastReasonByAction.TryGetValue(action, out var last)
                && string.Equals(last, reason, StringComparison.Ordinal))
            {
                return;
            }
        }
        Record(action, reason);
    }

    /// <inheritdoc />
    public void RecordEvent(string type, object? payload = null) => _eventLog.Record(type, payload);
}
