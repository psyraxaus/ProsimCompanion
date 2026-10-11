using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Deice;

/// <param name="HasHoldover">A holdover window exists for the fluid, OAT and precipitation
/// the crew entered (issue #151): with no active precipitation, or below the fluid's LOUT,
/// there is no holdover at all — the remaining figures then read 0 but nothing is
/// "expiring". The expiring notification fired at arm time on 2026-10-10 (20:15:46Z, the
/// same second as the card) because it read that 0 as "0 minutes left".</param>
/// <param name="FluidSource">Where the fluid figures came from (issue #166): "GSX" (the
/// <c>L:FSDT_GSX_DEICING_TYPE</c> LVAR), "menu answer" (what the app picked on GSX's fluid /
/// concentration menus) or "settings (unconfirmed)" (nothing confirmed what GSX sprayed).</param>
public sealed record DeiceHoldoverSnapshot(
    bool Active,
    bool Expired,
    string FluidLabel,
    HotPrecip Precip,
    double OatC,
    int RemainingLowSeconds,
    int RemainingHighSeconds,
    string Status,
    bool HasHoldover = false,
    string FluidSource = "");

/// <summary>
/// The deice holdover-time card: armed by the GSX deice-complete edge (via
/// <see cref="GroundOpsSignals"/>), counts the representative HOT window down each second.
/// Read-only by design — it never writes an icing dataref; it is a HOT card, not an icing
/// model. The crew supplies precipitation and OAT exactly as with a paper card.
/// <para>The fluid (issue #166, EFHK 2026-10-10): the card armed as "Type IV 75%" from the
/// settings while GSX had sprayed Type I — the fluid menus had gone unanswered and the LVAR
/// read 0 throughout. The arming now takes the LVAR when it is non-zero, else the menu entry
/// the app answered, else the settings with a warning; the card says which.</para>
/// </summary>
public sealed class DeiceHoldoverService : IDisposable
{
    private readonly GroundOpsSignals _signals;
    private readonly DeiceActivityStore _activity;
    private readonly IOptionsMonitor<GsxOptions> _gsxOptions;
    private readonly ILogger<DeiceHoldoverService> _logger;
    private readonly Timer _timer;
    private readonly object _lock = new();

    private bool _active;
    private bool _expired;
    private DateTimeOffset _startedUtc;
    private int _fluidType = 4;
    private int _concentration = 100;
    private string _fluidSource = "";
    private HotPrecip _precip = HotPrecip.None;
    private double _oatC;

    public event EventHandler? Changed;

    public DeiceHoldoverService(
        GroundOpsSignals signals,
        DeiceActivityStore activity,
        IOptionsMonitor<GsxOptions> gsxOptions,
        ILogger<DeiceHoldoverService> logger)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(gsxOptions);
        ArgumentNullException.ThrowIfNull(logger);
        _signals = signals;
        _activity = activity;
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
            var hasHoldover = (_active || _expired) && HotMatrix.Lookup(_fluidType, _concentration, _oatC, _precip) is not null;
            return new DeiceHoldoverSnapshot(_active, _expired, FluidLabel(), _precip, _oatC, low, high, status, hasHoldover, _fluidSource);
        }
    }

    /// <summary>The fluid the card arms with, in order of trust: GSX's own LVAR (type only —
    /// GSX never exposes the concentration), the menu entries the app answered, the settings.
    /// Pure, for tests.</summary>
    /// <param name="gsxFluidType"><c>L:FSDT_GSX_DEICING_TYPE</c> at the completed edge (0 = not reported).</param>
    /// <param name="menuFluidType">The fluid-type menu pick this cycle, if the app answered it.</param>
    /// <param name="menuConcentration">The concentration menu pick this cycle, if the app answered it.</param>
    /// <param name="configuredFluidType"><c>gsx.deIceFluidType</c> ("Type IV").</param>
    /// <param name="configuredConcentration"><c>gsx.deIceConcentration</c> ("75").</param>
    public static (int FluidType, int Concentration, string Source, bool Unconfirmed) ResolveFluid(
        int gsxFluidType,
        int? menuFluidType,
        int? menuConcentration,
        string configuredFluidType,
        string configuredConcentration)
    {
        ArgumentNullException.ThrowIfNull(configuredFluidType);
        ArgumentNullException.ThrowIfNull(configuredConcentration);

        var configuredConc = int.TryParse(configuredConcentration, out var c) ? c : 100;
        var concentration = menuConcentration is >= 1 and <= 100 ? menuConcentration.Value : configuredConc;

        if (gsxFluidType is >= 1 and <= 4)
        {
            // Type I is a heated single-table fluid: whatever the menu or settings said about
            // concentration is immaterial, but the label keeps the figure that was answered.
            return (gsxFluidType, concentration, "GSX", false);
        }

        if (menuFluidType is >= 1 and <= 4)
        {
            return (menuFluidType.Value, concentration, "menu answer", false);
        }

        return (ParseFluidType(configuredFluidType), concentration, "settings (unconfirmed)", true);
    }

    private void OnDeiceCompleted(int gsxFluidType)
    {
        var options = _gsxOptions.CurrentValue;
        var activity = _activity.Snapshot();
        var (fluidType, concentration, source, unconfirmed) = ResolveFluid(
            gsxFluidType, activity.MenuFluidType, activity.MenuConcentration, options.DeIceFluidType, options.DeIceConcentration);

        lock (_lock)
        {
            _fluidType = fluidType;
            _concentration = concentration;
            _fluidSource = source;
            _active = true;
            _expired = false;
            _startedUtc = DateTimeOffset.UtcNow;
        }

        if (unconfirmed)
        {
            _logger.LogWarning(
                "Deice complete — holdover card armed ({Fluid}) from the settings: fluid type unconfirmed (L:FSDT_GSX_DEICING_TYPE read {Lvar}, the fluid menus were not answered by the app)",
                FluidLabel(), gsxFluidType);
        }
        else
        {
            _logger.LogInformation("Deice complete — holdover card armed ({Fluid}, from {Source})", FluidLabel(), source);
        }

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

    /// <summary>"Type I" … "Type IV" → 1 … 4; anything else reads as Type IV (the shipped default).</summary>
    public static int ParseFluidType(string configured)
    {
        ArgumentNullException.ThrowIfNull(configured);
        return configured.Trim().ToUpperInvariant() switch
        {
            "TYPE I" => 1,
            "TYPE II" => 2,
            "TYPE III" => 3,
            _ => 4,
        };
    }
}
