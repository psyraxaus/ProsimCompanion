using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Weather;

namespace ProsimCompanion.Core.State;

/// <summary>The moments the FO prompts the captain's PA (issue #186).</summary>
public enum PaPromptKind
{
    /// <summary>Some minutes after the OFF edge — the welcome-aboard / cruise PA.</summary>
    AfterTakeoff,

    /// <summary>At the top-of-descent notice — the arrival PA.</summary>
    TopOfDescent,

    /// <summary>The seat-belt sign went on in cruise turbulence — the "please return to your seats" PA.</summary>
    SeatbeltsOn,
}

/// <summary>The live facts the PA script card fills in; every field optional (the card drops
/// the sentence whose fact is missing).</summary>
public sealed record PaScriptFacts(
    string? FlightNumber,
    string? DestinationName,
    string? DestinationIcao,
    int? FlightMinutes,
    int? CruiseFlightLevel,
    DateTimeOffset? EtaUtc,
    string? WeatherSkyLabel,
    int? WeatherTemperatureC,
    int? WeatherWindDirDeg,
    int? WeatherWindSpeedKt)
{
    public static PaScriptFacts Empty { get; } = new(null, null, null, null, null, null, null, null, null, null);

    /// <summary>Gathers the facts from the plan, the progress model and the destination weather
    /// card. Pure; a missing source leaves its facts null.</summary>
    public static PaScriptFacts From(OfpData? ofp, FlightProgressSnapshot? progress, WeatherCard? destinationWeather)
    {
        var wx = destinationWeather is { Status: WxProbeStatus.Found } card ? card : null;
        var eta = progress?.EtaUtc;
        int? flightMinutes = ofp?.EstimatedEnroute is { } enroute && enroute > TimeSpan.Zero ? (int)Math.Round(enroute.TotalMinutes) : null;
        return new PaScriptFacts(
            Text(ofp?.FlightNumber) ?? Text(ofp?.Callsign),
            Text(ofp?.DestinationName),
            Text(ofp?.DestinationIcao),
            flightMinutes,
            ofp is { CruiseFlightLevel: > 0 } ? ofp.CruiseFlightLevel : null,
            eta,
            wx?.SkyLabel,
            wx?.Facts.TemperatureC,
            wx?.Facts.WindDirDeg,
            wx?.Facts.WindSpeedKt);
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>What the PA card shows.</summary>
/// <param name="Kind">The standing prompt; null = none this cycle yet (or cleared).</param>
/// <param name="PromptedAtUtc">When the FO said "captain, your PA".</param>
/// <param name="Dismissed">The pilot closed the card; it can be reopened.</param>
/// <param name="Facts">The facts as gathered at the prompt.</param>
/// <param name="Done">The kinds already prompted this cycle (once each).</param>
public sealed record PaPromptSnapshot(
    PaPromptKind? Kind,
    DateTimeOffset? PromptedAtUtc,
    bool Dismissed,
    PaScriptFacts Facts,
    IReadOnlyList<PaPromptKind> Done)
{
    public static PaPromptSnapshot Empty { get; } = new(null, null, false, PaScriptFacts.Empty, []);

    /// <summary>A card is showing.</summary>
    public bool Visible => Kind is not null && !Dismissed;
}

/// <summary>
/// The captain's-PA prompt state (issue #186): the Speech side raises a prompt with the live
/// facts, the Flight Status page renders the script and lets the pilot dismiss or reopen it.
/// Each kind prompts once per flight cycle; the cycle reset clears everything.
/// </summary>
public sealed class PaPromptStore : SnapshotStore<PaPromptSnapshot>
{
    public PaPromptStore(GroundOpsSignals signals)
        : base(PaPromptSnapshot.Empty)
    {
        ArgumentNullException.ThrowIfNull(signals);
        signals.FlightCycleReset += Clear;
    }

    /// <summary>Raises the prompt; false when this kind already prompted this cycle.</summary>
    public bool Prompt(PaPromptKind kind, PaScriptFacts facts, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var raised = false;
        Update(s =>
        {
            if (s.Done.Contains(kind))
            {
                return s;
            }

            raised = true;
            return s with { Kind = kind, PromptedAtUtc = nowUtc, Dismissed = false, Facts = facts, Done = [.. s.Done, kind] };
        });
        return raised;
    }

    public void Dismiss() => Update(s => s.Kind is null || s.Dismissed ? s : s with { Dismissed = true });

    public void Reopen() => Update(s => s.Kind is null || !s.Dismissed ? s : s with { Dismissed = false });

    /// <summary>A new flight cycle: nothing prompted, nothing showing.</summary>
    public void Clear() => Update(_ => PaPromptSnapshot.Empty);
}
