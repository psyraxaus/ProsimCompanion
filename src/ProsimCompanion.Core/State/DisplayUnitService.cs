using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Core.State;

/// <summary>
/// Resolves the weight display unit for every UI surface (Prosim2GSX's DisplayUnitSource
/// model): source "app" uses the configured default unit; source "aircraft" follows ProSim's
/// <c>system.config.Units.Weight</c> ("LBS" ⇒ pounds, anything else ⇒ kilograms — the
/// predecessor's empirically-established rule), falling back to the default while the value
/// is absent. Storage and every wire protocol stay kilograms — this service converts at the
/// display boundary only.
/// </summary>
public sealed class DisplayUnitService : IDisposable
{
    /// <summary>Exact avoirdupois factor the predecessor used.</summary>
    public const double LbPerKg = 2.20462262;

    private readonly IOptionsMonitor<WebUiOptions> _options;
    private readonly IDataRefSubscription _aircraftUnit;
    private readonly IDisposable? _optionsSubscription;

    /// <summary>Raised when the effective unit may have changed, on arbitrary threads —
    /// Blazor consumers marshal with InvokeAsync.</summary>
    public event EventHandler? Changed;

    public DisplayUnitService(IProsimDataRefs dataRefs, IOptionsMonitor<WebUiOptions> options)
    {
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _aircraftUnit = dataRefs.Subscribe(ProsimDataRefNames.ConfigWeightUnit, DataRefTier.Infrequent);
        _aircraftUnit.ValueChanged += OnSourceChanged;
        _optionsSubscription = options.OnChange(_ => Changed?.Invoke(this, EventArgs.Empty));
    }

    public void Dispose()
    {
        _aircraftUnit.ValueChanged -= OnSourceChanged;
        _aircraftUnit.Dispose();
        _optionsSubscription?.Dispose();
    }

    /// <summary>True when the effective display unit is pounds.</summary>
    public bool IsPounds
    {
        get
        {
            var options = _options.CurrentValue;
            if (string.Equals(options.UnitSource, "aircraft", StringComparison.OrdinalIgnoreCase))
            {
                var aircraft = _aircraftUnit.GetValue<string?>(null);
                if (!string.IsNullOrWhiteSpace(aircraft))
                {
                    return aircraft.Contains("LB", StringComparison.OrdinalIgnoreCase);
                }
            }
            return string.Equals(options.Unit, "lb", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>"kg" or "lb" — the label pages print beside converted figures.</summary>
    public string UnitLabel => IsPounds ? "lb" : "kg";

    /// <summary>Converts a stored kilogram figure into the display unit.</summary>
    public double FromKg(double kg) => IsPounds ? kg * LbPerKg : kg;

    /// <summary>Converts a user-entered display-unit figure back to kilograms for the wire.</summary>
    public double ToKg(double displayValue) => IsPounds ? displayValue / LbPerKg : displayValue;

    /// <summary>Formats a kilogram figure in the display unit with the unit label, e.g.
    /// "12,345 kg" / "27,215 lb".</summary>
    public string Format(double kg, string numberFormat = "N0")
        => FromKg(kg).ToString(numberFormat, System.Globalization.CultureInfo.InvariantCulture) + " " + UnitLabel;

    private void OnSourceChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
}
