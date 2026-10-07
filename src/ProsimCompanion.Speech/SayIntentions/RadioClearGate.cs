using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;

namespace ProsimCompanion.Speech.SayIntentions;

/// <summary>
/// Waits for a quiet COM1 before the FO transmits to SayIntentions (Prosim2FO's radio-clear
/// gate, here over <see cref="ISimVars"/> instead of MobiFlight client data — 2026-10-08).
/// "Clear" = <c>L:SIAI_COM1_RECEIVING</c> and <c>L:SIAI_RADIO_PTT</c> both 0, held for a
/// short settle so the tail of ATC's sentence is not stepped on. Bounded: after
/// <see cref="MaxWait"/> the transmission goes anyway (a busy frequency must not swallow a
/// request). Without a sim connection (null <c>ISimVars</c>, or MSFS down) the gate is the
/// predecessor's fixed 400 ms settle. Subscriptions are opened on first use, so a session
/// with SayIntentions off never registers them.
/// </summary>
public sealed class RadioClearGate : IDisposable
{
    /// <summary>The no-SimVars fallback — the predecessor's own figure.</summary>
    public static readonly TimeSpan FallbackSettle = TimeSpan.FromMilliseconds(400);

    /// <summary>Quiet time required after the last activity before the FO keys up.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(400);

    /// <summary>Longest the FO waits for a clear frequency before transmitting anyway.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(8);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

    private readonly ISimVars? _simVars;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private IDataRefSubscription<double>? _receiving;
    private IDataRefSubscription<double>? _ptt;

    public RadioClearGate(ISimVars? simVars, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _simVars = simVars;
        _logger = logger;
    }

    /// <summary>True while someone is on COM1 (or our own PTT is keyed) according to the
    /// L:vars; false when clear or unreadable. Exposed for the status line and tests.</summary>
    public bool IsBusy
    {
        get
        {
            var (receiving, ptt) = Subscriptions();
            if (receiving is null || ptt is null)
            {
                return false;
            }

            // A stale value (MSFS dropped) must never hold a transmission.
            if (receiving.IsStale || ptt.IsStale)
            {
                return false;
            }

            return receiving.Value >= 0.5 || ptt.Value >= 0.5;
        }
    }

    /// <summary>Returns how long the gate held, and whether it timed out (transmitted into a
    /// still-busy frequency) — for the <c>sayintentions.request</c> event.</summary>
    public async Task<(TimeSpan Waited, bool TimedOut)> WaitForClearAsync(CancellationToken cancellationToken = default)
    {
        if (_simVars is null)
        {
            await Task.Delay(FallbackSettle, cancellationToken).ConfigureAwait(false);
            return (FallbackSettle, false);
        }

        var started = DateTimeOffset.UtcNow;
        var quietSince = IsBusy ? (DateTimeOffset?)null : started;
        while (true)
        {
            var now = DateTimeOffset.UtcNow;
            if (quietSince is not null && now - quietSince.Value >= Settle)
            {
                return (now - started, false);
            }

            if (now - started >= MaxWait)
            {
                _logger.LogInformation("Radio-clear gate: COM1 still busy after {Seconds:0.0} s — transmitting anyway", MaxWait.TotalSeconds);
                return (now - started, true);
            }

            await Task.Delay(Poll, cancellationToken).ConfigureAwait(false);
            if (IsBusy)
            {
                quietSince = null;
            }
            else
            {
                quietSince ??= DateTimeOffset.UtcNow;
            }
        }
    }

    private (IDataRefSubscription<double>? Receiving, IDataRefSubscription<double>? Ptt) Subscriptions()
    {
        if (_simVars is null)
        {
            return (null, null);
        }

        lock (_lock)
        {
            if (_receiving is null)
            {
                try
                {
                    _receiving = _simVars.Subscribe(SayIntentionsLvarNames.Com1Receiving);
                    _ptt = _simVars.Subscribe(SayIntentionsLvarNames.RadioPtt);
                }
                catch (Exception ex)
                {
                    // Degrade, not fail: no gate, the fixed settle still applies upstream.
                    _logger.LogWarning(ex, "Radio-clear gate: SIAI L:var subscription failed — transmitting without the gate");
                    _receiving?.Dispose();
                    _receiving = null;
                    _ptt = null;
                }
            }

            return (_receiving, _ptt);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _receiving?.Dispose();
            _ptt?.Dispose();
            _receiving = null;
            _ptt = null;
        }
    }
}
