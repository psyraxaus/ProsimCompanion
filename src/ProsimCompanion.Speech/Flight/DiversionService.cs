using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Acars;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Airports;
using ProsimCompanion.Core.Commands;
using ProsimCompanion.Core.Commands.Handlers;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.VoiceReference;
using ProsimCompanion.Core.Weather;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Company;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Flight;

/// <summary>
/// The diversion workflow (issue #184). "Divert to the alternate" / "divert to echo golf lima
/// lima" / "we are diverting" (or <c>flight.divert</c>, or the Diversion card) declares it on
/// the Core <see cref="DiversionStore"/>, which overlays the OFP's destination — the one seam
/// every downstream consumer already reads. This service then does what the FO does: tells the
/// captain where we are going, offers the arrival briefing (once), offers the RAD NAV retune
/// through the existing MCDU tuner's phrase when actuation is allowed, offers the ATC request
/// when SayIntentions is on, clears a pilot's standing arrival-gate request, sends the company
/// the ACARS "DIVERSION {icao} REASON {reason}" and voices the company's deterministic reply.
/// Read-and-speak only: the only aircraft write anywhere in this flow is the pilot's own "tune
/// the ILS", which keeps its announce → cancel window → write + verify gate.
/// </summary>
public sealed class DiversionService : Core.Hosting.IStartupModule, IVoiceFeature, IDiversionControl, IDisposable
{
    private static readonly string[] DivertLeadIns = ["divert to", "diverting to", "we are diverting to", "we're diverting to"];
    private static readonly string[] DivertPhrases = ["we are diverting", "we're diverting", "divert to the alternate", "divert to alternate", "divert"];
    private static readonly string[] CancelPhrases = ["cancel the diversion", "cancel diversion", "continue to destination", "diversion cancelled"];
    private static readonly string[] AlternateWords = ["alternate", "the alternate", "our alternate"];
    private static readonly TimeSpan CompanyReplyDelay = TimeSpan.FromSeconds(25);

    private readonly IOptionsMonitor<FlightOptions> _options;
    private readonly IOptionsMonitor<McduOptions> _mcdu;
    private readonly IOptionsMonitor<SayIntentionsOptions> _sayIntentions;
    private readonly DiversionStore _store;
    private readonly OfpStore _ofp;
    private readonly IFlightPhaseSource _flight;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<DiversionService> _logger;
    private readonly Core.Speech.ISpokenText _spokenText;
    private readonly IAirportNames? _airportNames;
    private readonly ICompanyChannel? _company;
    private readonly IAcarsUplink? _acars;
    private readonly IGsxGateControl? _gate;
    private readonly ISimClock? _simClock;

    public DiversionService(
        IOptionsMonitor<FlightOptions> options,
        IOptionsMonitor<McduOptions> mcdu,
        IOptionsMonitor<SayIntentionsOptions> sayIntentions,
        DiversionStore store,
        OfpStore ofp,
        IFlightPhaseSource flight,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<DiversionService> logger,
        Core.Speech.ISpokenText spokenText,
        IAirportNames? airportNames = null,
        ICompanyChannel? company = null,
        IAcarsUplink? acars = null,
        IGsxGateControl? gate = null,
        ISimClock? simClock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(mcdu);
        ArgumentNullException.ThrowIfNull(sayIntentions);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(ofp);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(spokenText);
        _options = options;
        _mcdu = mcdu;
        _sayIntentions = sayIntentions;
        _store = store;
        _ofp = ofp;
        _flight = flight;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
        _spokenText = spokenText;
        _airportNames = airportNames;
        _company = company;
        _acars = acars;
        _gate = gate;
        _simClock = simClock;
    }

    /// <summary>Roster declaration: between the MCDU arrival changer and the briefings.</summary>
    public static VoiceFeatureDeclaration Declared { get; } = new()
    {
        Precedence = 145,
        Gate = VoiceFeatureGate.ByOption("flight.diversionEnabled", "voiceReference.off.diversion"),
        Description = new DescribedVoiceReference("diversion", "voiceReference.group.diversion.title", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer,
        [
            new(["divert to the alternate", "divert to", "we are diverting"], "voiceReference.group.diversion.divert",
                "Diverting to Naples. Arrival briefing for Naples when you're ready.", [VoiceBadges.Value], "{alternate} / {ICAO spelled phonetically} [for weather | technical | medical | fuel]"),
            new(["cancel the diversion", "continue to destination"], "voiceReference.group.diversion.cancel", "Diversion cancelled — back to Rome Fiumicino."),
        ]),
    };

    VoiceFeatureDeclaration IVoiceFeature.Declaration => Declared;

    public bool Enabled => _options.CurrentValue.DiversionEnabled;

    public IEnumerable<string> Phrases => [.. DivertPhrases, .. CancelPhrases, .. DivertLeadIns];

    public bool ValueParse => true;

    public void Start()
    {
    }

    public void Dispose()
    {
    }

    public bool TryHandle(string utterance)
    {
        var text = CommandMatcher.Normalize(utterance);
        if (CancelPhrases.Any(p => CommandMatcher.Normalize(p) == text))
        {
            _ = CancelAsync("voice");
            return true;
        }

        if (!ParseDivert(text, out var icao, out var reason))
        {
            return false;
        }

        _ = DivertAsync(icao, reason, "voice");
        return true;
    }

    /// <summary>"divert to the alternate for weather" → (null, Weather); "divert to echo golf
    /// lima lima" → ("EGLL", Other); "we are diverting" → (null, Other); false when the words
    /// are not a diversion. Pure, for tests. A lead-in with letters that do not make four
    /// phonetic words is still a diversion with no airport (the FO asks for the alternate).</summary>
    public static bool ParseDivert(string normalized, out string? icao, out DiversionReason reason)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        icao = null;
        reason = DiversionReason.Other;
        var text = normalized.Trim();
        if (!text.StartsWith("divert", StringComparison.Ordinal) && !text.StartsWith("diverting", StringComparison.Ordinal)
            && !text.StartsWith("we are diverting", StringComparison.Ordinal) && !text.StartsWith("we re diverting", StringComparison.Ordinal))
        {
            return false;
        }

        var reasonAt = text.IndexOf(" for ", StringComparison.Ordinal);
        var reasonWords = reasonAt >= 0 ? text[(reasonAt + 5)..] : "";
        var head = reasonAt >= 0 ? text[..reasonAt] : text;
        if (reasonWords.Length > 0)
        {
            reason = FlightCommandHandlers.ParseReason(reasonWords);
        }

        var toAt = head.IndexOf(" to ", StringComparison.Ordinal);
        if (toAt < 0)
        {
            return true; // "we are diverting" — the alternate
        }

        var target = head[(toAt + 4)..].Trim();
        if (AlternateWords.Contains(target, StringComparer.Ordinal))
        {
            return true;
        }

        var words = target.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 1 && words[0].Length == 4 && words[0].All(char.IsAsciiLetter))
        {
            icao = words[0].ToUpperInvariant();
            return true;
        }

        if (words.Length == 4)
        {
            var letters = new char[4];
            for (var i = 0; i < 4; i++)
            {
                if (!Core.Speech.NatoPhonetics.TryParseWord(words[i], out letters[i]))
                {
                    return true; // a diversion, but the airport did not parse — the FO asks
                }
            }

            icao = new string(letters).ToUpperInvariant();
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<CommandResult> DivertAsync(string? icao, DiversionReason reason, string source, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Enabled)
            {
                return CommandResult.Unavailable("The diversion workflow is off (flight.diversionEnabled).");
            }

            var planned = _ofp.Planned;
            var target = string.IsNullOrWhiteSpace(icao) ? planned?.AlternateIcao : icao.Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(target))
            {
                await _arbiter.SpeakAsync("Divert to where, captain? The plan has no alternate — say divert to and the ICAO.", SpeechPriority.High, cancellationToken).ConfigureAwait(false);
                return CommandResult.Failed("No airport: the OFP has no alternate and none was given.");
            }

            if (string.Equals(target, planned?.DestinationIcao, StringComparison.OrdinalIgnoreCase))
            {
                await _arbiter.SpeakAsync($"{Spoken(target)} is our destination already.", SpeechPriority.Normal, cancellationToken).ConfigureAwait(false);
                return CommandResult.Failed("That is the planned destination.");
            }

            var name = string.Equals(target, planned?.AlternateIcao, StringComparison.OrdinalIgnoreCase) && planned?.AlternateName is { Length: > 0 } altName
                ? AirportNameFormat.TitleCase(altName)
                : _airportNames?.SpokenName(target);
            var from = _store.Current.Active ? _store.Current.OriginalDestinationIcao : planned?.DestinationIcao;
            var now = _simClock?.UtcNowOrReal ?? DateTimeOffset.UtcNow;
            if (!_store.Declare(target, name, reason, now))
            {
                return CommandResult.Failed("The diversion could not be declared.");
            }

            _gate?.Cancel(); // a stand asked for at the old destination means nothing now
            _eventLog.Record("flight.diverted", new
            {
                from,
                to = target,
                reason = reason.ToString().ToLowerInvariant(),
                source,
                phase = _flight.CurrentPhase.ToString(),
                airborne = _flight.CurrentPhase.IsAirborne(),
            });
            _logger.LogInformation("Diversion declared: {From} → {To} ({Reason}, {Source})", from, target, reason, source);

            var spoken = Spoken(target);
            var lines = new List<string> { $"Diverting to {spoken}. Arrival briefing for {spoken} when you're ready." };
            _store.AddOffer("arrival briefing");
            if (_mcdu.CurrentValue.AllowActuation)
            {
                lines.Add("Say tune the ILS once the approach is in the box and I'll set the radios.");
                _store.AddOffer("rad nav");
            }

            if (_sayIntentions.CurrentValue.Enabled)
            {
                lines.Add("Say request diversion and I'll ask ATC.");
                _store.AddOffer("atc request");
            }

            await _arbiter.EnqueueAsync(new SpeechRequest(string.Join(" ", lines), SpeechPriority.High, Tag: "flight.diversion"), cancellationToken).ConfigureAwait(false);
            _ = TellCompanyAsync(target, reason, cancellationToken);
            return CommandResult.Ok($"Diverting to {target} ({reason.ToString().ToLowerInvariant()}).");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Diversion failed");
            return CommandResult.Failed("The diversion failed — see log.");
        }
    }

    /// <inheritdoc />
    public async Task<CommandResult> CancelAsync(string source, CancellationToken cancellationToken = default)
    {
        try
        {
            var original = _store.Current.OriginalDestinationIcao;
            if (!_store.Cancel())
            {
                await _arbiter.SpeakAsync("No diversion to cancel.", SpeechPriority.Normal, cancellationToken).ConfigureAwait(false);
                return CommandResult.Failed("No diversion is active.");
            }

            _eventLog.Record("flight.diversionCancelled", new { backTo = original, source });
            await _arbiter.EnqueueAsync(new SpeechRequest(
                original is null ? "Diversion cancelled." : $"Diversion cancelled — back to {Spoken(original)}.",
                SpeechPriority.High, Tag: "flight.diversion"), cancellationToken).ConfigureAwait(false);
            return CommandResult.Ok(original is null ? "Diversion cancelled." : $"Diversion cancelled — back to {original}.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cancelling the diversion failed");
            return CommandResult.Failed("Cancelling the diversion failed — see log.");
        }
    }

    /// <summary>The ACARS downlink text, as the company reads it.</summary>
    public static string CompanyMessage(string icao, DiversionReason reason)
        => $"DIVERSION {icao.ToUpperInvariant()} REASON {reason.ToString().ToUpperInvariant()}";

    /// <summary>The company's deterministic reply.</summary>
    public static string CompanyReply(string icao) => $"ROGER DIVERSION, HANDLING ARRANGED {icao.ToUpperInvariant()}, CALL ON BLOCKS";

    /// <summary>Sends the diversion to the company — the ACARS uplink in its own slot when the
    /// ProSim pillar is there — and voices the company's reply after a dispatcher's pause.</summary>
    private async Task TellCompanyAsync(string icao, DiversionReason reason, CancellationToken cancellationToken)
    {
        try
        {
            var message = CompanyMessage(icao, reason);
            var sent = false;
            if (_acars is not null)
            {
                sent = await _acars.SendAsync(new AcarsMessage
                {
                    Type = "DIVERSION",
                    Header = "DIVERSION",
                    Id = "05",
                    Accept = false,
                    Content = message + "\n" + CompanyReply(icao),
                }, cancellationToken).ConfigureAwait(false);
            }

            _eventLog.Record("company.diversion", new { message, reply = CompanyReply(icao), acarsSent = sent });
            await Task.Delay(CompanyReplyDelay, cancellationToken).ConfigureAwait(false);
            if (_store.Current is { Active: true, NewDestinationIcao: { } current } && string.Equals(current, icao, StringComparison.OrdinalIgnoreCase))
            {
                _company?.DeliverMessage($"Company copies the diversion to {Spoken(icao)}. Handling arranged at {Spoken(icao)}, call on blocks.");
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Diversion company message failed");
        }
    }

    private string Spoken(string icao) => _spokenText.Airport(icao);
}
