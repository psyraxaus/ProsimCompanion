using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Geo;

namespace ProsimCompanion.Speech.Immersion;

/// <summary>One scheduler tick's inputs — sampled by the shell, judged by the core.</summary>
/// <param name="Live">The flight-live gate (issue #114): ProSim pushes plausible data with no MSFS session.</param>
/// <param name="VoicePaused">The pilot's "ear off" latch — the FO keeps quiet too.</param>
/// <param name="Sterile">The sterile-cockpit rule's verdict for this phase/altitude (never in the cruise, but explicit).</param>
/// <param name="Quiet">"Quiet please" has been said this session (the arbiter would drop a Low item anyway; no lookup is spent).</param>
public sealed record RegionFactTick(
    FlightPhase Phase,
    bool Live,
    DateTimeOffset NowUtc,
    bool VoicePaused = false,
    bool Sterile = false,
    bool Quiet = false);

/// <summary>Why a due tick did not end in a fact (the session event's outcome word).</summary>
public enum RegionFactSkip
{
    /// <summary>No position to look up.</summary>
    NoPosition,

    /// <summary>The atlas has neither a country nor a sea there.</summary>
    Unresolved,

    /// <summary>This region already had its fact this flight.</summary>
    Repeated,
}

/// <summary>
/// The pure scheduler of the cruise region facts (issue #122): when the next fact is due
/// (a jittered gap drawn between the configured minimum and maximum), the per-flight cap,
/// and the no-repeat rule per region. Re-arms with the cabin's once-per-flight latches
/// (cold-and-dark, or a fresh Preflight after shutdown). The shell owns the clock, the
/// atlas, the model and the arbiter.
/// </summary>
public sealed class RegionFactCore
{
    /// <summary>A due tick with nothing to say (over water, a repeat) looks again this much
    /// later — shorter than a full gap, so a coastline crossing is not missed by half an hour.</summary>
    public static readonly TimeSpan Retry = TimeSpan.FromMinutes(5);

    private readonly HashSet<string> _spokenRegions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _told = [];
    private DateTimeOffset? _cruiseSince;
    private DateTimeOffset? _nextDueUtc;
    private FlightPhase _lastPhase = FlightPhase.Unknown;

    /// <summary>Facts spoken this flight.</summary>
    public int CountThisFlight { get; private set; }

    /// <summary>When the next fact may be offered; null until the cruise begins.</summary>
    public DateTimeOffset? NextDueUtc => _nextDueUtc;

    /// <summary>Regions already spoken about this flight (for the event payload).</summary>
    public IReadOnlyCollection<string> SpokenRegions => _spokenRegions;

    /// <summary>The facts spoken this flight, in order — the model is shown them so it does
    /// not circle back to one of them.</summary>
    public IReadOnlyList<string> Told => _told;

    /// <summary>Books a spoken fact's text for <see cref="Told"/>.</summary>
    public void Remember(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            _told.Add(text.Trim());
        }
    }

    public void OnPhaseChanged(FlightPhase from, FlightPhase to)
    {
        if (to == FlightPhase.ColdAndDark
            || (to == FlightPhase.Preflight && from is FlightPhase.Shutdown or FlightPhase.TaxiIn))
        {
            _spokenRegions.Clear();
            _told.Clear();
            CountThisFlight = 0;
            _cruiseSince = null;
            _nextDueUtc = null;
        }
    }

    /// <summary>The region key a place fix resolves to: the country's ISO code, else the
    /// named sea, else null (the atlas has nothing there — silence). The spoken name rides
    /// along for the prompt and the event.</summary>
    public static (string Key, string Name)? RegionOf(PlaceFix? fix)
    {
        if (fix?.Country is { } country)
        {
            // Two atlas entries carry no ISO code (Northern Cyprus, Somaliland): keyed by name.
            var key = string.IsNullOrWhiteSpace(country.Iso2) ? "country:" + country.Name : country.Iso2.ToUpperInvariant();
            return (key, PlaceFixText.CountryName(country));
        }

        if (fix?.Sea is { } sea)
        {
            return ("sea:" + sea.Name, PlaceFixText.WithArticle(sea));
        }

        return null;
    }

    /// <summary>One gap between facts: minimum plus the 0–1 <paramref name="roll"/>'s share of
    /// the spread. A maximum below the minimum reads as the minimum; nothing is ever below one minute.</summary>
    public static TimeSpan Gap(RegionFactsOptions options, double roll)
    {
        ArgumentNullException.ThrowIfNull(options);
        var min = Math.Max(1, options.MinIntervalMinutes);
        var max = Math.Max(min, options.MaxIntervalMinutes);
        return TimeSpan.FromMinutes(min + Math.Clamp(roll, 0, 1) * (max - min));
    }

    /// <summary>True when the gates are open AND the gap has run: the shell should look the
    /// region up now. The first gap starts at the cruise entry; every gate below merely holds
    /// a due fact for the next tick — it never consumes it.</summary>
    public bool IsDue(RegionFactTick tick, RegionFactsOptions options, Func<double> roll)
    {
        ArgumentNullException.ThrowIfNull(tick);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(roll);

        if (tick.Phase != _lastPhase)
        {
            _lastPhase = tick.Phase;
            if (tick.Phase == FlightPhase.Cruise && _cruiseSince is null)
            {
                // The first gap runs from the cruise entry; a step climb (Cruise → Climb →
                // Cruise) keeps the schedule it had.
                _cruiseSince = tick.NowUtc;
                _nextDueUtc = tick.NowUtc + Gap(options, roll());
            }
            else if (tick.Phase is not (FlightPhase.Climb or FlightPhase.Cruise))
            {
                _cruiseSince = null;
                _nextDueUtc = null;
            }
        }

        if (!options.Enabled || !tick.Live || tick.Phase != FlightPhase.Cruise || _cruiseSince is null
            || tick.VoicePaused || tick.Sterile || tick.Quiet
            || CountThisFlight >= Math.Max(0, options.MaxPerFlight)
            || _nextDueUtc is not { } due || tick.NowUtc < due)
        {
            return false;
        }

        return true;
    }

    /// <summary>Claims the region for a fact: false (and a short retry) when it already had
    /// one this flight; true books it, counts it and draws the next gap.</summary>
    public bool TryClaim(string regionKey, RegionFactsOptions options, DateTimeOffset nowUtc, Func<double> roll)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(regionKey);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(roll);

        if (!_spokenRegions.Add(regionKey))
        {
            _nextDueUtc = nowUtc + Retry;
            return false;
        }

        CountThisFlight++;
        _nextDueUtc = nowUtc + Gap(options, roll());
        return true;
    }

    /// <summary>A due tick that could not be resolved (no position, open water): look again
    /// after the short retry instead of a whole gap.</summary>
    public void Defer(DateTimeOffset nowUtc) => _nextDueUtc = nowUtc + Retry;

    /// <summary>A claimed fact that produced no speech (model silent, guard refused, nothing
    /// curated): the region is given back so a later tick can try it again.</summary>
    public void Release(string regionKey)
    {
        if (_spokenRegions.Remove(regionKey))
        {
            CountThisFlight = Math.Max(0, CountThisFlight - 1);
        }
    }

    /// <summary>The model tier's instructions (issue #122): one short fact about the REGION
    /// named, two sentences, the tone a crew member uses with passengers — and the same
    /// fences as the place facts (#153): nothing about this flight, no position or distance,
    /// no instruction to the Captain.</summary>
    public static string SystemPrompt(string personaFragment)
        => (personaFragment ?? "")
            + "You are the First Officer of an Airbus A320 in the cruise, making light conversation with the "
            + "Captain. Offer ONE short, interesting fact about the REGION we are flying over, as a friendly "
            + "aside — the kind of thing a well-travelled crew member mentions to passengers. Keep it to two "
            + "sentences, aviation-passenger tone. You may open with the region's name ('We're over France now'). "
            + "Use general knowledge; prefer well-known facts over precise figures. Plain English for "
            + "text-to-speech: no markdown, no lists, no emoji, no preamble. Do not give any distance, direction, "
            + "position or coordinate, and never say anything about this flight's fuel, weights, speeds, "
            + "altitudes, times, weather or route. Never tell the Captain to do anything.";

    /// <summary>The user turn: the region, the atlas's nearby names for colour, and the facts
    /// already given this flight so the model does not circle back to them.</summary>
    public static string UserPrompt(string regionName, string placeNames, IEnumerable<string> alreadyTold)
    {
        var told = string.Join(" | ", alreadyTold ?? []);
        return "REGION: " + regionName
            + "\nNEARBY: " + (string.IsNullOrWhiteSpace(placeNames) ? "(nothing named)" : placeNames)
            + (told.Length > 0 ? "\nALREADY MENTIONED THIS FLIGHT: " + told : "")
            + "\n\nOffer the fact now.";
    }
}
