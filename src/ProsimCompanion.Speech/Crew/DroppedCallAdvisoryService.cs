using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;

namespace ProsimCompanion.Speech.Crew;

/// <summary>
/// Speaks a dropped GSX service call (issue #76): the trigger slot retried and GSX still never
/// picked the call up, so the pilot must act (usually a GSX menu is standing — issue #44).
/// Each <see cref="GsxDroppedCallView.Timestamp"/> is spoken once; the Flight Status row
/// keeps showing it until a later call confirms.
/// </summary>
public sealed class DroppedCallAdvisoryService : Core.Hosting.IStartupModule, IDisposable
{
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<DroppedCallAdvisoryService> _logger;
    private readonly object _gate = new();
    private DateTimeOffset? _lastSpoken;
    private IDisposable? _subscription;

    public DroppedCallAdvisoryService(
        GsxDiagnosticsStore diagnostics,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<DroppedCallAdvisoryService> logger)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _diagnostics = diagnostics;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
    }

    public void Start() => _subscription = _diagnostics.Observe(OnDiagnosticsChanged);

    public void Dispose() => _subscription?.Dispose();

    /// <summary>The spoken line. Names the service in plain words and the standing GSX menu
    /// when there is one — that menu is what the pilot has to clear.</summary>
    public static string ComposeAdvisory(string serviceId, string? openMenu)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);

        var service = SpeakableService(serviceId);
        return string.IsNullOrWhiteSpace(openMenu)
            ? $"Captain, ground did not pick up the {service} call. Check the GSX menu and call it again."
            : $"Captain, ground did not pick up the {service} call — the GSX menu \"{openMenu.Trim()}\" is still open. Clear it and call again.";
    }

    /// <summary>"OperateJetways" → "operate jetways"; "GPU" stays "GPU".</summary>
    internal static string SpeakableService(string serviceId)
    {
        if (serviceId.All(char.IsUpper))
        {
            return serviceId;
        }

        var words = new System.Text.StringBuilder();
        foreach (var ch in serviceId)
        {
            if (char.IsUpper(ch) && words.Length > 0)
            {
                words.Append(' ');
            }

            words.Append(char.ToLowerInvariant(ch));
        }

        return words.ToString();
    }

    private void OnDiagnosticsChanged(GsxDiagnosticsSnapshot snapshot)
    {
        try
        {
            var dropped = snapshot.DroppedCall;
            if (dropped is null)
            {
                return;
            }

            lock (_gate)
            {
                if (_lastSpoken == dropped.Timestamp)
                {
                    return;
                }

                _lastSpoken = dropped.Timestamp;
            }

            var text = ComposeAdvisory(dropped.ServiceId, dropped.OpenMenu);
            _eventLog.Record("fo.dropped-call-advisory", new { service = dropped.ServiceId, openMenu = dropped.OpenMenu, text });
            _ = SpeakAsync(text);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Dropped-call advisory handling failed");
        }
    }

    private async Task SpeakAsync(string text)
    {
        try
        {
            await _arbiter.EnqueueAsync(new SpeechRequest(
                text, SpeechPriority.High, Ttl: TimeSpan.FromSeconds(30),
                Tag: "fo.dropped-call")).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Dropped-call advisory speech failed");
        }
    }
}
