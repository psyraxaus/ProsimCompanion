using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Geo;
using ProsimCompanion.Core.Persona;
using ProsimCompanion.Core.Speech;
using ProsimCompanion.Core.Weather;

namespace ProsimCompanion.Speech.Persona;

/// <summary>
/// What the small talk knows about the moment — who the FO is, where the aircraft is, what the
/// sky and the sun are doing, where the flight is going — gathered from the stores into one
/// <see cref="SmallTalkContext"/>. Cut out of <see cref="CruiseSmallTalkService"/> (review
/// 2026-11-05: the context's five optional dependencies lived in the scheduler shell). Every
/// lookup degrades to "unknown" on its own; nothing here throws.
/// </summary>
public sealed class SmallTalkContextSource
{
    private readonly PersonaService _persona;
    private readonly FlightProgressStore _progress;
    private readonly HeroWeatherStore _weather;
    private readonly ILogger<SmallTalkContextSource> _logger;
    private readonly OfpStore? _ofp;
    private readonly ISpokenText? _spokenText;
    private readonly IFoRosterService? _roster;
    private readonly IPlaceLookup? _places;
    private readonly ISimLocalTime? _clock;

    public SmallTalkContextSource(
        PersonaService persona,
        FlightProgressStore progress,
        HeroWeatherStore weather,
        ILogger<SmallTalkContextSource> logger,
        OfpStore? ofp = null,
        ISpokenText? spokenText = null,
        IFoRosterService? roster = null,
        IPlaceLookup? places = null,
        ISimLocalTime? clock = null)
    {
        ArgumentNullException.ThrowIfNull(persona);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(weather);
        ArgumentNullException.ThrowIfNull(logger);
        _persona = persona;
        _progress = progress;
        _weather = weather;
        _logger = logger;
        _ofp = ofp;
        _spokenText = spokenText;
        _roster = roster;
        _places = places;
        _clock = clock;
    }

    /// <summary>The context for one line, from the stores as they stand now.</summary>
    public SmallTalkContext Build()
    {
        var fo = _roster?.Current;
        var effective = _persona.Effective;
        string? destination = null;
        try
        {
            var ofp = _ofp?.Current;
            destination = !string.IsNullOrWhiteSpace(ofp?.DestinationName) ? ofp!.DestinationName
                : ofp?.DestinationIcao is { Length: 4 } icao ? _spokenText?.Airport(icao) : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Small talk: destination lookup failed");
        }

        string? town = null, townSide = null, sunSide = null;
        double? sunElevation = null;
        var morning = false;
        try
        {
            var progress = _progress.Snapshot();
            if (progress.Position is { } position)
            {
                var fix = _places?.Locate(position, progress.TrackTrueDeg);
                var near = fix?.Reference ?? fix?.Nearest;
                town = near?.Town.Name;
                townSide = near?.Side;
                if (_clock is { LocalDate: { } date, ZuluTimeOfDay: { } zulu })
                {
                    var sun = SolarPosition.At(position.LatitudeDeg, position.LongitudeDeg, date, zulu);
                    sunElevation = sun.ElevationDeg;
                    sunSide = progress.TrackTrueDeg is { } track ? SolarPosition.SideOf(sun.AzimuthDeg, track) : null;
                }
            }

            morning = CruiseSmallTalkService.IsLocalMorning(_clock?.LocalTimeOfDay);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Small talk: place / sun lookup failed");
        }

        var sky = SkyCondition.Unknown;
        try
        {
            sky = _weather.Snapshot().Local.Sky;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Small talk: weather read failed");
        }

        return new SmallTalkContext(
            FoName: fo?.Name ?? (string.IsNullOrWhiteSpace(effective.Name) ? null : effective.Name),
            FoBase: fo?.Base is { Length: 4 } b ? (_spokenText?.Airport(b) ?? b) : null,
            FoHours: fo?.SpokenHours ?? 0,
            Confidence: effective.Confidence,
            DestinationCity: destination,
            TownName: town,
            TownSide: townSide,
            SunSide: sunSide,
            SunElevationDeg: sunElevation,
            Morning: morning,
            Sky: sky);
    }
}
