using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Radios;

/// <summary>Where the frequency ATC expects came from.</summary>
public enum RadioAssignmentSource
{
    /// <summary>ATC's last "Contact … on …" hand-off in the SayIntentions comms history.</summary>
    Handoff,

    /// <summary>SayIntentions' <c>correct_frequency</c> — the station it names before the
    /// flight's first hand-off (Ground, from the moment the flight is filed).</summary>
    CorrectFrequency,
}

/// <summary>One ATC station at an airport, from SayIntentions' getWX comms list.</summary>
/// <param name="Type">SayIntentions' station type ("CLEARANCE", "GROUND", "TOWER", "ATIS", …).</param>
/// <param name="Callsign">The spoken station name when SayIntentions gives one, else empty.</param>
/// <param name="Khz">Frequency in integer kHz — the unit of ProSim's COM analogs.</param>
public sealed record AtcStation(string Type, string Callsign, int Khz);

/// <summary>The station list of one airport and when it was fetched.</summary>
public sealed record AirportStations(string Icao, IReadOnlyList<AtcStation> Stations, DateTimeOffset FetchedAtUtc);

/// <summary>The frequency SayIntentions ATC wants COM1 on.</summary>
/// <param name="Station">The station as ATC said it ("Tallinn Control", "GROUND").</param>
/// <param name="Khz">Integer kHz.</param>
/// <param name="SinceUtc">When this assignment was first seen — kept across polls that repeat it.</param>
public sealed record RadioAssignment(string Station, int Khz, RadioAssignmentSource Source, DateTimeOffset SinceUtc);

/// <summary>
/// Everything the Flight Status Radios tile and the Flight Monitor board render (owner
/// request 2026-10-25: "I have to bring the SayIntentions window forward to see which
/// frequency ATC is on"). COM1 comes from ProSim's push read model; the assignment, the
/// station lists and the last ATC line from the SayIntentions frequency monitor in the
/// Speech pillar. Kept in Core so the Web project can render it without a pillar reference.
/// </summary>
/// <param name="Com1ActiveKhz">COM1 active in kHz, 0 until ProSim pushes it.</param>
/// <param name="Com1StandbyKhz">COM1 standby in kHz, 0 until ProSim pushes it.</param>
/// <param name="Com1Live">False while the COM1 datarefs are absent or stale (ProSim gone).</param>
/// <param name="Assigned">What ATC expects, or null while nothing is known.</param>
/// <param name="Airports">Station lists fetched so far (at most the two OFP airports).</param>
/// <param name="LastAtcLine">The last ATC → pilot COM1 line SayIntentions logged, or empty.</param>
/// <param name="LastAtcAtUtc">When that line was noticed by the poll (not ATC's own stamp).</param>
/// <param name="SayIntentionsOn">The SayIntentions pillar is on and has an API key.</param>
public sealed record RadioSnapshot(
    int Com1ActiveKhz,
    int Com1StandbyKhz,
    bool Com1Live,
    RadioAssignment? Assigned,
    IReadOnlyList<AirportStations> Airports,
    string LastAtcLine,
    DateTimeOffset? LastAtcAtUtc,
    bool SayIntentionsOn)
{
    public static RadioSnapshot Empty { get; } = new(0, 0, false, null, [], "", null, false);

    /// <summary>The station list for <paramref name="icao"/>, or null when none was fetched.</summary>
    public AirportStations? StationsFor(string? icao)
    {
        if (string.IsNullOrWhiteSpace(icao))
        {
            return null;
        }

        foreach (var airport in Airports)
        {
            if (string.Equals(airport.Icao, icao.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return airport;
            }
        }

        return null;
    }

    /// <summary>True when COM1 active is the assigned frequency; null when either is unknown.</summary>
    public bool? OnAssignedFrequency
        => Assigned is null || !Com1Live || Com1ActiveKhz <= 0 ? null : Com1ActiveKhz == Assigned.Khz;
}

/// <summary>Live radio state for the web surfaces; written by Core's COM1 panel service and
/// the Speech pillar's SayIntentions frequency monitor, read by the pages.</summary>
public sealed class RadioStore : SnapshotStore<RadioSnapshot>
{
    public RadioStore()
        : base(RadioSnapshot.Empty)
    {
    }
}

/// <summary>Frequency text helpers shared by the writers and the pages.</summary>
public static class RadioFrequency
{
    /// <summary>VHF COM band in kHz (8.33 kHz spacing goes to 136.990).</summary>
    public const int BandLowKhz = 118_000;
    public const int BandHighKhz = 136_990;

    public static bool InBand(int khz) => khz is >= BandLowKhz and <= BandHighKhz;

    /// <summary>"118.125" / "119.700" — always three decimals so columns line up.</summary>
    public static string Format(int khz)
        => khz <= 0 ? "---.---" : (khz / 1000.0).ToString("F3", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"119.7" → 119700, "118.12" → 118125 (the spoken short form of a 25 kHz channel).
    /// Null for text that is not a frequency or is outside the COM band.</summary>
    public static int? ToKhz(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (!decimal.TryParse(trimmed, System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture, out var mhz)
            || mhz is < 100m or > 200m)
        {
            return null;
        }

        var khz = (int)Math.Round(mhz * 1000m);
        var dot = trimmed.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0 && trimmed.Length - dot - 1 == 2 && trimmed[^1] is '2' or '7')
        {
            khz += 5;
        }

        return InBand(khz) ? khz : null;
    }
}
