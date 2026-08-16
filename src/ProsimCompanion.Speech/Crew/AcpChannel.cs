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

/// <summary>The crew channels whose receive latches gate audible replies.</summary>
public enum AcpChannelKind
{
    /// <summary>INT — the ground crew's channel.</summary>
    Intercom,

    /// <summary>CAB — the purser's channel.</summary>
    Cabin,
}

/// <summary>Atomic view of the captain ACP transmit state — one read, one coherent pair
/// (campaign #85: the old two-property surface let a key release slip between reads).</summary>
public readonly record struct AcpTransmitState(AcpTransmitTarget Target, bool IntKeyPushed);

/// <summary>
/// The captain ACP as the crew dialogues see it (campaign #85): the transmit selector state,
/// and the receive latches for the crew channels — including the latch-or-grace wait that
/// the crew hails and the ground upcalls used to hand-roll separately (with divergent
/// staleness handling; this module's rule is decided once). Narrow on purpose: consumers
/// decide what a selection MEANS (<see cref="AcpHailGateCore"/> stays the pure policy);
/// this seam only reports it.
/// </summary>
public interface IAcpChannel
{
    /// <summary>Current transmit selection + INT key, as one coherent snapshot.</summary>
    AcpTransmitState Transmit { get; }

    /// <summary>True while any of the three ACPs has the channel's receive latch up.</summary>
    bool IsReceiving(AcpChannelKind kind);

    /// <summary>Waits for the channel's receive latch or the grace (250 ms poll — the
    /// purser's CAB pattern). Returns true when the latch was observed; false when the grace
    /// elapsed or ProSim is absent/stale (degrade immediately — a dialogue must never hang on
    /// a signal nobody is producing).</summary>
    Task<bool> AwaitReceiveAsync(AcpChannelKind kind, TimeSpan grace, CancellationToken cancellationToken);

    /// <summary>Raised when <see cref="Transmit"/> changes, on the SDK's (arbitrary) thread —
    /// consumers marshal to their own context.</summary>
    event EventHandler? Changed;
}

/// <summary>
/// Subscribes the captain ACP transmit datarefs and the six receive latches once (read model
/// rule: cached values, never polled round-trips). Captain-side only by design — the human
/// pilot hails from the left seat, and the FO/observer panels belong to the virtual crew.
/// Deliberately NOT <c>S_ASP_INTRAD</c>: that rocker is the GSX "force next service" smart
/// button (see <see cref="ProsimDataRefNames.AcpSendChannel"/>).
/// </summary>
public sealed class AcpChannel : IAcpChannel, IDisposable
{
    private static readonly DataRef<int>[] IntLatches =
        [ProsimDataRefNames.Acp1IntLatch, ProsimDataRefNames.Acp2IntLatch, ProsimDataRefNames.Acp3IntLatch];

    private static readonly DataRef<int>[] CabLatches =
        [ProsimDataRefNames.Acp1CabLatch, ProsimDataRefNames.Acp2CabLatch, ProsimDataRefNames.Acp3CabLatch];

    private readonly IDataRefSubscription<int> _sendChannel;
    private readonly IDataRefSubscription<int> _intSend;
    private readonly IDataRefSubscription<int>[] _intLatches;
    private readonly IDataRefSubscription<int>[] _cabLatches;
    private readonly ILogger<AcpChannel> _logger;
    private readonly object _gate = new();
    private AcpTransmitState _last = new(AcpTransmitTarget.Unknown, false);

    public AcpChannel(IProsimDataRefs dataRefs, ILogger<AcpChannel> logger)
    {
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        // Tiers come from the catalog (#83): the transmit refs are Frequent — the INT key is
        // momentary and the selector drives live hangup decisions, 250 ms matching the INTRAD
        // smart button's cadence.
        _sendChannel = dataRefs.Subscribe(ProsimDataRefNames.AcpSendChannel);
        _intSend = dataRefs.Subscribe(ProsimDataRefNames.AcpIntSend);
        _intLatches = [.. IntLatches.Select(latch => dataRefs.Subscribe(latch))];
        _cabLatches = [.. CabLatches.Select(latch => dataRefs.Subscribe(latch))];
        _sendChannel.ValueChanged += OnTransmitValueChanged;
        _intSend.ValueChanged += OnTransmitValueChanged;
    }

    public event EventHandler? Changed;

    /// <inheritdoc />
    public AcpTransmitState Transmit
    {
        get
        {
            AcpTransmitTarget target;
            if (_sendChannel.RawValue is null || _sendChannel.IsStale)
            {
                target = AcpTransmitTarget.Unknown;
            }
            else
            {
                var value = _sendChannel.Value;
                target = value is >= 0 and <= 8 ? (AcpTransmitTarget)value : AcpTransmitTarget.Unknown;
            }

            var intKey = _intSend.RawValue is not null && !_intSend.IsStale && _intSend.Value == 1;
            return new AcpTransmitState(target, intKey);
        }
    }

    /// <inheritdoc />
    public bool IsReceiving(AcpChannelKind kind)
        => LatchesFor(kind).Any(IsLatched);

    /// <inheritdoc />
    public async Task<bool> AwaitReceiveAsync(AcpChannelKind kind, TimeSpan grace, CancellationToken cancellationToken)
    {
        var latches = LatchesFor(kind);
        if (latches.Any(latch => latch.IsStale) || latches.All(latch => latch.RawValue is null))
        {
            return false;
        }

        var deadline = Environment.TickCount64 + (long)Math.Max(0, grace.TotalMilliseconds);
        while (Environment.TickCount64 < deadline)
        {
            if (latches.Any(IsLatched))
            {
                return true;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>The latch descriptors fall back to 1 (unmuted / fail-audible, #83) — so
    /// "no data has arrived yet" must be decided on the liveness contract (RawValue is null),
    /// never by reading the fallback, or a dead subscription would report a held latch.
    /// A stale value keeps the last real reading ("valid or hold previous decision"), exactly
    /// as the old fallback-0 read did; callers that must degrade on staleness gate on IsStale
    /// themselves (see <see cref="AwaitReceiveAsync"/>'s entry check).</summary>
    private static bool IsLatched(IDataRefSubscription<int> latch)
        => latch.RawValue is not null && latch.Value == 1;

    public void Dispose()
    {
        _sendChannel.ValueChanged -= OnTransmitValueChanged;
        _intSend.ValueChanged -= OnTransmitValueChanged;
        _sendChannel.Dispose();
        _intSend.Dispose();
        foreach (var latch in _intLatches.Concat(_cabLatches))
        {
            latch.Dispose();
        }
    }

    private IDataRefSubscription<int>[] LatchesFor(AcpChannelKind kind)
        => kind == AcpChannelKind.Intercom ? _intLatches : _cabLatches;

    private void OnTransmitValueChanged(object? sender, EventArgs e)
    {
        // Dedupe so consumers only wake on a real state change, not every push.
        var snapshot = Transmit;
        lock (_gate)
        {
            if (snapshot == _last)
            {
                return;
            }

            _last = snapshot;
        }

        _logger.LogDebug(
            "ACP transmit changed: {Target}, INT key {IntKey}", snapshot.Target, snapshot.IntKeyPushed);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
