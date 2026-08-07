namespace ProsimCompanion.Core.State;

/// <summary>
/// One airport's SayIntentions getWX result: full ATIS broadcast text, raw METAR/TAF and the
/// active runway, plus the explicit wind fields SI supplies alongside the METAR. Absent
/// fields are empty strings / null (SI's own convention) so the web page can render blanks
/// without null checks.
/// </summary>
public sealed record AirportWeather(
    string Airport,
    string Atis,
    string Metar,
    string Taf,
    string ActiveRunway,
    int? WindDirection,
    int? WindSpeed);

/// <summary>Everything the web Weather page renders. <see cref="CpdlcStation"/> is the CPDLC
/// logon code for whatever station SayIntentions currently considers active — empty is the
/// only "absent" value.</summary>
public sealed record WeatherSnapshot(
    AirportWeather? Departure,
    AirportWeather? Arrival,
    string CpdlcStation,
    string Status,
    DateTimeOffset? FetchedAtUtc,
    bool IsRefreshing)
{
    public static WeatherSnapshot Empty { get; } = new(null, null, "", "", null, false);

    /// <summary>The stored entry for <paramref name="icao"/> (departure or arrival), if any —
    /// how the composite weather provider backfills ATIS/active-runway without a network call.</summary>
    public AirportWeather? ForIcao(string? icao)
    {
        if (string.IsNullOrWhiteSpace(icao))
        {
            return null;
        }

        if (string.Equals(Departure?.Airport, icao, StringComparison.OrdinalIgnoreCase))
        {
            return Departure;
        }

        return string.Equals(Arrival?.Airport, icao, StringComparison.OrdinalIgnoreCase) ? Arrival : null;
    }
}

/// <summary>
/// Live SayIntentions weather + CPDLC state. Kept in Core so the Web project (which
/// references only Core) can render it and the composite weather provider can read it back.
/// Written by the SayIntentions weather service in ProsimCompanion.Speech.
/// </summary>
public sealed class WeatherStore
{
    private readonly object _gate = new();
    private WeatherSnapshot _snapshot = WeatherSnapshot.Empty;

    /// <summary>Raised after any update, on the writer's thread — consumers marshal to their
    /// own context (InvokeAsync in Blazor components).</summary>
    public event EventHandler? Changed;

    public WeatherSnapshot Snapshot()
    {
        lock (_gate)
        {
            return _snapshot;
        }
    }

    /// <summary>Replaces the snapshot via a pure transform of the current one.</summary>
    public void Update(Func<WeatherSnapshot, WeatherSnapshot> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        lock (_gate)
        {
            _snapshot = mutate(_snapshot);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Commands the web Weather page can issue (implemented by the SayIntentions weather
/// service in ProsimCompanion.Speech; registered only when that pillar is).</summary>
public interface IWeatherControl
{
    /// <summary>User-initiated refresh of weather + CPDLC. Serves the cache when it is still
    /// fresh and debounces rapid repeats — the outcome (data or reason) lands in the
    /// <see cref="WeatherStore"/>, never as an exception.</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);
}
