using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Deice;

public sealed record DeiceHoldoverSnapshot(
    bool Active,
    bool Expired,
    string FluidLabel,
    HotPrecip Precip,
    double OatC,
    int RemainingLowSeconds,
    int RemainingHighSeconds,
    string Status);

/// <summary>
/// The deice holdover-time card: armed by the GSX deice-complete edge (via
/// <see cref="GroundOpsSignals"/>), counts the representative HOT window down each second.
/// Read-only by design — it never writes an icing dataref; it is a HOT card, not an icing
/// model. The crew supplies precipitation and OAT exactly as with a paper card.
/// </summary>
public sealed class DeiceHoldoverService : IDisposable
{
    private readonly GroundOpsSignals _signals;
    private readonly IOptionsMonitor<GsxOptions> _gsxOptions;
    private readonly ILogger<DeiceHoldoverService> _logger;
    private readonly Timer _timer;
    private readonly object _lock = new();

    private bool _active;
    private bool _expired;
    private DateTimeOffset _startedUtc;
    private int _fluidType = 4;
    private int _concentration = 100;
    private HotPrecip _precip = HotPrecip.None;
    private double _oatC;

    public event EventHandler? Changed;

    public DeiceHoldoverService(
        GroundOpsSignals signals,
        IOptionsMonitor<GsxOptions> gsxOptions,
        ILogger<DeiceHoldoverService> logger)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(gsxOptions);
        ArgumentNullException.ThrowIfNull(logger);
        _signals = signals;
        _gsxOptions = gsxOptions;
        _logger = logger;

        _signals.DeiceCompleted += OnDeiceCompleted;
        _signals.FlightCycleReset += OnCycleReset;
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void Dispose()
    {
        _signals.DeiceCompleted -= OnDeiceCompleted;
        _signals.FlightCycleReset -= OnCycleReset;
        _timer.Dispose();
    }

    /// <summary>Crew inputs — survive across flights by design (weather doesn't reset with the
    /// turnaround; the countdown does).</summary>
    public void SetPrecip(HotPrecip precip)
    {
        lock (_lock)
        {
            _precip = precip;
            _expired = false;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetOat(double oatC)
    {
        lock (_lock)
        {
            _oatC = oatC;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public DeiceHoldoverSnapshot Snapshot()
    {
        lock (_lock)
        {
            var (low, high, status) = Compute();
            return new DeiceHoldoverSnapshot(_active, _expired, FluidLabel(), _precip, _oatC, low, high, status);
        }
    }

    private void OnDeiceCompleted(int gsxFluidType)
    {
        var options = _gsxOptions.CurrentValue;
        lock (_lock)
        {
            // GSX exposes the applied fluid TYPE but not the concentration; concentration comes
            // from the configured auto-answer (what we drove into the menu).
            _fluidType = gsxFluidType is >= 1 and <= 4 ? gsxFluidType : ParseFluidType(options.DeIceFluidType);
            _concentration = int.TryParse(options.DeIceConcentration, out var concentration) ? concentration : 100;
            _active = true;
            _expired = false;
            _startedUtc = DateTimeOffset.UtcNow;
        }
        _logger.LogInformation("Deice complete — holdover card armed ({Fluid})", FluidLabel());
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnCycleReset()
    {
        lock (_lock)
        {
            if (!_active && !_expired)
            {
                return;
            }
            _active = false;
            _expired = false;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Tick()
    {
        bool notify;
        lock (_lock)
        {
            if (!_active || _expired)
            {
                return;
            }
            var (_, high, _) = Compute();
            if (high <= 0 && HotMatrix.Lookup(_fluidType, _concentration, _oatC, _precip) is not null)
            {
                _active = false;
                _expired = true;
            }
            notify = true; // countdown display advances every second while active
        }
        if (notify)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private (int Low, int High, string Status) Compute()
    {
        var fluid = FluidLabel();
        if (!_active && !_expired)
        {
            return (0, 0, "Awaiting GSX deicing…");
        }
        if (_expired)
        {
            return (0, 0, $"{fluid} · {_precip} · HOLDOVER EXPIRED — re-treatment required");
        }

        var window = HotMatrix.Lookup(_fluidType, _concentration, _oatC, _precip);
        if (window is null)
        {
            return (0, 0, _precip == HotPrecip.None
                ? $"{fluid} applied · no active precipitation — no holdover"
                : $"{fluid} · {_precip} · below fluid LOUT — no holdover");
        }

        var elapsed = (DateTimeOffset.UtcNow - _startedUtc).TotalSeconds;
        var low = Math.Max(0, (int)Math.Round(window.Value.LowMinutes * 60 - elapsed));
        var high = Math.Max(0, (int)Math.Round(window.Value.HighMinutes * 60 - elapsed));
        return (low, high, $"{fluid} · {_precip} · {low / 60}:{low % 60:D2}–{high / 60}:{high % 60:D2} remaining");
    }

    private string FluidLabel()
    {
        var roman = _fluidType switch { 1 => "I", 2 => "II", 3 => "III", 4 => "IV", _ => "?" };
        return $"Type {roman} {_concentration}%";
    }

    private static int ParseFluidType(string configured) => configured.Trim().ToUpperInvariant() switch
    {
        "TYPE I" => 1,
        "TYPE II" => 2,
        "TYPE III" => 3,
        _ => 4,
    };
}
