using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;

namespace ProsimCompanion.Speech.Crew;

/// <summary>
/// Where the captain's ACP transmit selector points (issue #72). Values 0–8 mirror
/// <c>S_ASP_SEND_CHANNEL</c> exactly so the raw dataref casts straight in;
/// <see cref="Unknown"/> is the degrade state — no value received yet, connection stale,
/// or an out-of-range push (older ProSim without the dataref) — and every consumer treats
/// it as "gate unavailable, behave as before".
/// </summary>
public enum AcpTransmitTarget
{
    Unknown = -1,
    None = 0,
    Vhf1 = 1,
    Vhf2 = 2,
    Vhf3 = 3,
    Hf1 = 4,
    Hf2 = 5,
    Intercom = 6,
    Cabin = 7,
    Pa = 8,
}

/// <summary>
/// Read-only view of the captain ACP transmit state for the crew dialogue gates.
/// Narrow on purpose: consumers decide what a selection MEANS; this seam only reports it.
/// </summary>
public interface IAcpTransmitMonitor
{
    /// <summary>Current transmit selection; <see cref="AcpTransmitTarget.Unknown"/> when the
    /// dataref has never arrived or the ProSim connection is stale.</summary>
    AcpTransmitTarget Current { get; }

    /// <summary>True while the momentary ACP INT transmit key is pushed.</summary>
    bool IntKeyPushed { get; }

    /// <summary>Raised when <see cref="Current"/> or <see cref="IntKeyPushed"/> changes, on
    /// the SDK's (arbitrary) thread — consumers marshal to their own context.</summary>
    event EventHandler? Changed;
}

/// <summary>
/// Subscribes the captain ACP transmit datarefs once (read model rule: cached values, never
/// polled round-trips) and exposes them as <see cref="IAcpTransmitMonitor"/>. Captain-side
/// only by design — the human pilot hails from the left seat, and the FO/observer panels
/// belong to the virtual crew. Deliberately NOT <c>S_ASP_INTRAD</c>: that rocker is the GSX
/// "force next service" smart button (see <see cref="ProsimDataRefNames.AcpSendChannel"/>).
/// </summary>
public sealed class AcpTransmitMonitor : IAcpTransmitMonitor, IDisposable
{
    private readonly IDataRefSubscription _sendChannel;
    private readonly IDataRefSubscription _intSend;
    private readonly ILogger<AcpTransmitMonitor> _logger;
    private readonly object _gate = new();
    private (AcpTransmitTarget Target, bool IntKey) _last = (AcpTransmitTarget.Unknown, false);

    public AcpTransmitMonitor(IProsimDataRefs dataRefs, ILogger<AcpTransmitMonitor> logger)
    {
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        // Frequent tier: the INT key is momentary and the selector drives live hangup
        // decisions — 250 ms matches the INTRAD smart button's cadence.
        _sendChannel = dataRefs.Subscribe(ProsimDataRefNames.AcpSendChannel, DataRefTier.Frequent);
        _intSend = dataRefs.Subscribe(ProsimDataRefNames.AcpIntSend, DataRefTier.Frequent);
        _sendChannel.ValueChanged += OnValueChanged;
        _intSend.ValueChanged += OnValueChanged;
    }

    public event EventHandler? Changed;

    public AcpTransmitTarget Current
    {
        get
        {
            if (_sendChannel.RawValue is null || _sendChannel.IsStale)
            {
                return AcpTransmitTarget.Unknown;
            }

            var value = _sendChannel.GetValue(-1);
            return value is >= 0 and <= 8 ? (AcpTransmitTarget)value : AcpTransmitTarget.Unknown;
        }
    }

    public bool IntKeyPushed
        => _intSend.RawValue is not null && !_intSend.IsStale && _intSend.GetValue(0) == 1;

    public void Dispose()
    {
        _sendChannel.ValueChanged -= OnValueChanged;
        _intSend.ValueChanged -= OnValueChanged;
        _sendChannel.Dispose();
        _intSend.Dispose();
    }

    private void OnValueChanged(object? sender, EventArgs e)
    {
        // Dedupe so consumers only wake on a real state change, not every push.
        var snapshot = (Current, IntKeyPushed);
        lock (_gate)
        {
            if (snapshot == _last)
            {
                return;
            }

            _last = snapshot;
        }

        _logger.LogDebug(
            "ACP transmit changed: {Target}, INT key {IntKey}", snapshot.Item1, snapshot.Item2);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
