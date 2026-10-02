using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Speech;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.TechLog;
using ProsimCompanion.Core.Weather;

namespace ProsimCompanion.Speech.Questions;

/// <summary>
/// Gathers the live inputs for <see cref="FoFactSheetBuilder"/> (issue #149): every Core store
/// the FO can be asked about, plus the three aircraft figures no store holds (fuel on board,
/// zero fuel weight, gross weight) from dataref subscriptions registered once.
/// </summary>
public sealed class FoFactSource : ILiveAircraftFacts, IDisposable
{
    private readonly IFlightPhaseSource _flight;
    private readonly FlightProgressStore _progress;
    private readonly FlightTimesStore _times;
    private readonly OfpStore _ofp;
    private readonly LoadsheetStore _loadsheet;
    private readonly HeroWeatherStore _weather;
    private readonly ArrivalMinimaStore _minima;
    private readonly ITechLogService? _techLog;
    private readonly IDataRefSubscription<double> _fuel;
    private readonly IDataRefSubscription<double> _zfw;
    private readonly IDataRefSubscription<double> _gross;

    public FoFactSource(
        IFlightPhaseSource flight,
        FlightProgressStore progress,
        FlightTimesStore times,
        OfpStore ofp,
        LoadsheetStore loadsheet,
        HeroWeatherStore weather,
        ArrivalMinimaStore minima,
        IProsimDataRefs dataRefs,
        ITechLogService? techLog = null)
    {
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(times);
        ArgumentNullException.ThrowIfNull(ofp);
        ArgumentNullException.ThrowIfNull(loadsheet);
        ArgumentNullException.ThrowIfNull(weather);
        ArgumentNullException.ThrowIfNull(minima);
        ArgumentNullException.ThrowIfNull(dataRefs);

        _flight = flight;
        _progress = progress;
        _times = times;
        _ofp = ofp;
        _loadsheet = loadsheet;
        _weather = weather;
        _minima = minima;
        _techLog = techLog;
        _fuel = dataRefs.Subscribe(ProsimDataRefNames.FuelTotal);
        _zfw = dataRefs.Subscribe(ProsimDataRefNames.WeightZfw);
        _gross = dataRefs.Subscribe(ProsimDataRefNames.WeightGross);
    }

    public double? FuelOnBoardKg => Live(_fuel);

    public double? ZeroFuelWeightKg => Live(_zfw);

    public double? GrossWeightKg => Live(_gross);

    /// <summary>The sheet as of now. Never throws: a store that fails to read is left out.</summary>
    public FoFactSheet Build(DateTimeOffset nowUtc)
        => FoFactSheetBuilder.Build(new FoFactInputs(
            Safe(() => _flight.Snapshot()),
            Safe(() => _progress.Snapshot()),
            Safe(() => _times.Snapshot()),
            Safe(() => _ofp.Current),
            Safe(() => _loadsheet.Snapshot()),
            this,
            Safe(() => _weather.Snapshot()),
            Safe(() => _minima.Current),
            _techLog is { IsEnabled: true } log ? Safe(() => log.OpenDefects) : null,
            nowUtc));

    public void Dispose()
    {
        _fuel.Dispose();
        _zfw.Dispose();
        _gross.Dispose();
    }

    private static double? Live(IDataRefSubscription<double> sub)
        => sub.RawValue is null || sub.IsStale ? null : sub.Value;

    private static T? Safe<T>(Func<T?> read)
        where T : class
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
