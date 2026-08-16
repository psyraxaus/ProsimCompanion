using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Arbiter;

namespace ProsimCompanion.Speech.Crew;

/// <summary>
/// Resolves the accent-localized ground-crew voice per TTS provider (issue #53). The airport
/// is the FMS origin while on the ground pre-departure and the destination from TaxiIn on —
/// the crew outside the window is local, whichever leg you are on. Resolution is a per-call
/// selector handed to the TTS router: Google gets "{locale}-Chirp3-HD-{persona}", local
/// providers get a mapped US/UK voice for English locales, and null everywhere else — which
/// falls back to the configured role voice, so a disabled/unmapped/offline case is exactly
/// today's behaviour.
/// </summary>
public sealed class AccentVoiceResolver : IDisposable
{
    private readonly IOptionsMonitor<AccentOptions> _options;
    private readonly IFlightPhaseSource _flight;
    private readonly IDataRefSubscription _fmsOrigin;
    private readonly IDataRefSubscription _fmsDestination;
    private readonly ILogger<AccentVoiceResolver> _logger;
    private string? _loggedLocale;

    public AccentVoiceResolver(
        IProsimDataRefs dataRefs,
        IFlightPhaseSource flight,
        IOptionsMonitor<AccentOptions> options,
        ILogger<AccentVoiceResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _flight = flight;
        _options = options;
        _logger = logger;
        _fmsOrigin = dataRefs.Subscribe(ProsimDataRefNames.FmsOrigin, DataRefTier.Infrequent);
        _fmsDestination = dataRefs.Subscribe(ProsimDataRefNames.FmsDestination, DataRefTier.Infrequent);
    }

    public void Dispose()
    {
        _fmsOrigin.Dispose();
        _fmsDestination.Dispose();
    }

    /// <summary>Per-provider voice selector for this utterance, or null when accents do not
    /// apply (role, disabled, no mapped airport) — the caller then uses the configured role
    /// voice unchanged.</summary>
    public Func<string, string?>? ProviderVoiceSelector(SpeechRole role)
    {
        var options = _options.CurrentValue;
        if (role != SpeechRole.GroundCrew || !options.Enabled)
        {
            return null;
        }

        var locale = AirportAccentMap.LocaleFor(CurrentAirport(), options.Overrides);
        if (locale is null)
        {
            return null;
        }

        if (_loggedLocale != locale)
        {
            _loggedLocale = locale;
            _logger.LogInformation("Ground-crew accent locale resolved to {Locale}", locale);
        }

        var persona = string.IsNullOrWhiteSpace(options.GooglePersona) ? "Charon" : options.GooglePersona.Trim();
        return providerName => providerName switch
        {
            // Google carries the accents: 51 Chirp 3 HD locales.
            "google" => $"{locale}-Chirp3-HD-{persona}",

            // Kokoro speaks only US/UK English usably — map the British-flavoured English
            // locales to the British male; everything else keeps the configured voice.
            "kokoro" when locale is "en-GB" or "en-AU" or "en-IN" => "bm_george",

            _ => null, // fall back to the configured role voice
        };
    }

    /// <summary>Origin on the ground before departure; destination once arriving (TaxiIn,
    /// Shutdown — and the flight phases, where a stray ground call would still be "ahead").</summary>
    private string? CurrentAirport()
    {
        var origin = ValidIcaoOrNull(_fmsOrigin.GetValue<string?>(null));
        var destination = ValidIcaoOrNull(_fmsDestination.GetValue<string?>(null));

        return _flight.CurrentPhase switch
        {
            FlightPhase.ColdAndDark or FlightPhase.Preflight or FlightPhase.PushbackAndStart
                or FlightPhase.TaxiOut or FlightPhase.TakeoffRoll => origin ?? destination,
            _ => destination ?? origin,
        };
    }

    /// <summary>The FMS refs read "----" before a plan and have been observed returning the
    /// literal "Null" (predecessor archaeology) — same rule as the GSX flight-plan monitor.</summary>
    private static string? ValidIcaoOrNull(string? value)
        => value is { Length: 4 }
            && value != "----"
            && !value.Equals("Null", StringComparison.OrdinalIgnoreCase)
            ? value
            : null;
}
