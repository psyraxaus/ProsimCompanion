using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Fuel;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.VoiceReference;
using ProsimCompanion.Core.Weather;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Monitoring;

/// <summary>
/// The captain's fuel decision at dispatch and the holding-time answer in flight (issue #183).
/// After an OFP import on the ground — before refuelling — the advisor reads the destination's
/// TAF (or METAR) at the ETA and the alternate's; when it recommends holding fuel the FO asks
/// "… recommend 20 minutes holding, 700 kilos. Take it?" (a captain's question over
/// <see cref="FuelDecisionStore"/>); yes raises the INIT block-fuel override by the figure,
/// rounded up to 100 kg, through the existing override path, so the refuel target follows.
/// Voice: "fuel decision" (the advice now), "extra fuel {n} minutes" / "extra fuel {n} hundred"
/// (apply directly), "how long can we hold" (<see cref="HoldingTimeCalculator"/>). Read-only
/// apart from the override, which is the pilot's own INIT entry.
/// </summary>
public sealed class FuelDecisionService : Core.Hosting.IStartupModule, IVoiceFeature, IDisposable
{
    private static readonly string[] DecisionPhrases = ["fuel decision", "fuel advice", "what about extra fuel", "do we need extra fuel"];
    private static readonly string[] HoldingPhrases = ["how long can we hold", "holding time", "how much holding do we have", "how long can we hold for"];
    private const string ExtraLeadIn = "extra fuel";

    private readonly IOptionsMonitor<FuelOptions> _options;
    private readonly OfpStore _ofp;
    private readonly IWxProvider _weather;
    private readonly ArrivalMinimaStore _minima;
    private readonly FuelDecisionStore _store;
    private readonly HoldingTimeStore _holding;
    private readonly IEfbInitOverrides _overrides;
    private readonly IFlightPhaseSource _flight;
    private readonly FlightProgressStore _progress;
    private readonly IProsimDataRefs _dataRefs;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<FuelDecisionService> _logger;
    private readonly ISimClock? _simClock;
    private readonly IDataRefSubscription<double> _fuelTotal;
    private readonly IDataRefSubscription<double> _flow1;
    private readonly IDataRefSubscription<double> _flow2;
    private readonly IDataRefSubscription<double> _plannedFuel;
    private readonly object _gate = new();
    private DateTimeOffset? _advisedForOfp;

    public FuelDecisionService(
        IOptionsMonitor<FuelOptions> options,
        OfpStore ofp,
        IWxProvider weather,
        ArrivalMinimaStore minima,
        FuelDecisionStore store,
        HoldingTimeStore holding,
        IEfbInitOverrides overrides,
        IFlightPhaseSource flight,
        FlightProgressStore progress,
        IProsimDataRefs dataRefs,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<FuelDecisionService> logger,
        ISimClock? simClock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(ofp);
        ArgumentNullException.ThrowIfNull(weather);
        ArgumentNullException.ThrowIfNull(minima);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(holding);
        ArgumentNullException.ThrowIfNull(overrides);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _ofp = ofp;
        _weather = weather;
        _minima = minima;
        _store = store;
        _holding = holding;
        _overrides = overrides;
        _flight = flight;
        _progress = progress;
        _dataRefs = dataRefs;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
        _simClock = simClock;
        _fuelTotal = dataRefs.Subscribe(ProsimDataRefNames.FuelTotal);
        _flow1 = dataRefs.Subscribe(ProsimDataRefNames.Engine1FuelFlowKgh);
        _flow2 = dataRefs.Subscribe(ProsimDataRefNames.Engine2FuelFlowKgh);
        _plannedFuel = dataRefs.Subscribe(ProsimDataRefNames.EfbPlannedFuel);
    }

    /// <summary>Roster declaration: beside the fuel check, ahead of the gross error check.</summary>
    public static VoiceFeatureDeclaration Declared { get; } = new()
    {
        Precedence = 15,
        Gate = VoiceFeatureGate.AlwaysOn,
        Description = new DescribedVoiceReference("fuelDecision", "voiceReference.group.fuelDecision.title", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer,
        [
            new(["fuel decision", "fuel advice", "what about extra fuel", "do we need extra fuel"],
                "voiceReference.group.fuelDecision.decision",
                "Destination TAF has thunderstorms around our arrival — recommend 20 minutes holding, 700 kilos."),
            new(["extra fuel"], "voiceReference.group.fuelDecision.extra",
                "Block fuel raised to 9.1 tonnes — 700 kilos extra.", [VoiceBadges.Value], "{n} minutes / {n} hundred"),
            new(["how long can we hold", "holding time", "how much holding do we have", "how long can we hold for"],
                "voiceReference.group.fuelDecision.holding", "We can hold for 25 minutes before we have to divert."),
        ]),
    };

    VoiceFeatureDeclaration IVoiceFeature.Declaration => Declared;

    /// <summary>Always on as a query; the automatic dispatch question follows <see cref="FuelOptions.DecisionAdvice"/>.</summary>
    public bool Enabled => true;

    public IEnumerable<string> Phrases => [.. DecisionPhrases, .. HoldingPhrases, ExtraLeadIn];

    public bool ValueParse => true;

    public void Start()
    {
        _ofp.Changed += OnOfpChanged;
        _store.Answered += OnAnswered;
    }

    public void Dispose()
    {
        _ofp.Changed -= OnOfpChanged;
        _store.Answered -= OnAnswered;
        _fuelTotal.Dispose();
        _flow1.Dispose();
        _flow2.Dispose();
        _plannedFuel.Dispose();
    }

    public bool TryHandle(string utterance)
    {
        var text = CommandMatcher.Normalize(utterance);
        if (DecisionPhrases.Any(p => CommandMatcher.Normalize(p) == text))
        {
            _ = AnswerDecisionAsync();
            return true;
        }

        if (HoldingPhrases.Any(p => CommandMatcher.Normalize(p) == text))
        {
            _ = AnswerHoldingAsync();
            return true;
        }

        if (text.StartsWith(ExtraLeadIn, StringComparison.Ordinal) && ParseExtra(text, _options.CurrentValue.HoldingBurnKgPerHour) is { } extraKg)
        {
            _ = ApplyExtraAsync(extraKg, "voice");
            return true;
        }

        return false;
    }

    /// <summary>"extra fuel 20 minutes" → the holding burn for 20 minutes; "extra fuel 7 hundred"
    /// / "extra fuel 700" → 700 kg; "extra fuel 1.2 tonnes" → 1200 kg. Null when no figure.
    /// Pure, for tests.</summary>
    public static double? ParseExtra(string normalizedUtterance, int holdingBurnKgPerHour)
    {
        ArgumentNullException.ThrowIfNull(normalizedUtterance);
        if (!NumberExtractor.TryExtract(normalizedUtterance, out var value) || value <= 0)
        {
            return null;
        }

        if (normalizedUtterance.Contains("minute", StringComparison.Ordinal))
        {
            return Math.Ceiling(value / 60.0 * holdingBurnKgPerHour / 100.0) * 100;
        }

        if (normalizedUtterance.Contains("tonne", StringComparison.Ordinal) || normalizedUtterance.Contains(" ton", StringComparison.Ordinal))
        {
            return Math.Ceiling(value * 10) * 100;
        }

        if (normalizedUtterance.Contains("hundred", StringComparison.Ordinal) && value < 100)
        {
            return value * 100;
        }

        return Math.Ceiling(value / 100.0) * 100;
    }

    /// <summary>The block fuel after the captain's extra: the effective figure plus the extra,
    /// rounded up to the next 100 kg (fuel-order increments). Pure, for tests.</summary>
    public static double RaisedBlockKg(double effectiveBlockKg, double extraKg)
        => Math.Ceiling((effectiveBlockKg + extraKg) / 100.0) * 100;

    private void OnOfpChanged(object? sender, EventArgs e) => _ = AdviseAfterImportAsync();

    /// <summary>The dispatch dialogue: once per OFP, on the ground before the flight, only when
    /// the switch is on and the advisor recommends extra.</summary>
    private async Task AdviseAfterImportAsync()
    {
        try
        {
            var ofp = _ofp.Current;
            if (ofp is null || !_options.CurrentValue.DecisionAdvice)
            {
                return;
            }

            lock (_gate)
            {
                if (_advisedForOfp == ofp.FetchedAtUtc)
                {
                    return;
                }

                _advisedForOfp = ofp.FetchedAtUtc;
            }

            var state = _flight.Snapshot();
            if (state.HasBeenAirborneThisSession || !state.Phase.IsBeforeTaxiOut())
            {
                return; // an in-flight OFP (restart, turnaround plan loaded early) is not the dispatch decision
            }

            var decision = await AdviseAsync(ofp).ConfigureAwait(false);
            if (decision is null)
            {
                return;
            }

            if (decision.Recommendation == FuelRecommendation.None)
            {
                return;
            }

            if (!_store.Ask(FuelDecisionAdvisor.QuestionPrompt(decision), decision))
            {
                _logger.LogDebug("Fuel decision question not raised — one stands or the cycle is answered");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fuel decision after the OFP import failed");
        }
    }

    /// <summary>Runs the advisor for the OFP: destination and alternate outlooks at the ETA
    /// (STD + enroute time, else now + enroute, else now), the briefed minima.</summary>
    public async Task<FuelDecision?> AdviseAsync(OfpData ofp)
    {
        ArgumentNullException.ThrowIfNull(ofp);
        if (string.IsNullOrWhiteSpace(ofp.DestinationIcao))
        {
            return null;
        }

        var now = _simClock?.UtcNowOrReal ?? DateTimeOffset.UtcNow;
        var departure = ofp.ScheduledOutUtc is { } std && std > now.AddHours(-3) ? std : now;
        var eta = departure + (ofp.EstimatedEnroute ?? TimeSpan.FromHours(1.5));

        var destinationFacts = await _weather.GetAsync(ofp.DestinationIcao).ConfigureAwait(false);
        var alternateFacts = string.IsNullOrWhiteSpace(ofp.AlternateIcao) ? null : await _weather.GetAsync(ofp.AlternateIcao).ConfigureAwait(false);
        var destination = ArrivalOutlook.From(ofp.DestinationIcao, destinationFacts, eta);
        var alternate = ArrivalOutlook.From(ofp.AlternateIcao, alternateFacts, eta.AddMinutes(45));
        var decision = FuelDecisionAdvisor.Advise(destination, alternate, _minima.Current, _options.CurrentValue);
        _store.SetAdvice(decision);
        _eventLog.Record("fuel.decision", new
        {
            destination = ofp.DestinationIcao,
            alternate = ofp.AlternateIcao,
            etaUtc = eta,
            destinationOutlook = destination is null ? null : new { destination.VisibilityM, destination.CeilingFt, destination.Thunderstorm, destination.FromTaf },
            alternateOutlook = alternate is null ? null : new { alternate.VisibilityM, alternate.CeilingFt, alternate.Thunderstorm, alternate.FromTaf },
            minima = new { decision.Minima.CeilingFt, decision.Minima.VisibilityM, decision.Minima.Source },
            recommendation = decision.Recommendation.ToString(),
            holdingMinutes = decision.HoldingMinutes,
            extraKg = decision.ExtraKg,
            reasons = decision.Reasons,
            legality = decision.LegalityLine,
        });
        _logger.LogInformation("Fuel decision {Destination}: {Recommendation} {Minutes} min / {Extra:F0} kg — {Reasons}",
            ofp.DestinationIcao, decision.Recommendation, decision.HoldingMinutes, decision.ExtraKg, string.Join("; ", decision.Reasons));
        return decision;
    }

    private async Task AnswerDecisionAsync()
    {
        try
        {
            var ofp = _ofp.Current;
            if (ofp is null)
            {
                await _arbiter.SpeakAsync("No flight plan loaded for a fuel decision.", SpeechPriority.Normal).ConfigureAwait(false);
                return;
            }

            var decision = await AdviseAsync(ofp).ConfigureAwait(false);
            var text = decision is null ? "No destination weather to base a fuel decision on." : FuelDecisionAdvisor.Spoken(decision);
            await _arbiter.EnqueueAsync(new SpeechRequest(text, SpeechPriority.Normal, Tag: "fuel.decision")).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fuel decision answer failed");
        }
    }

    private void OnAnswered(object? sender, (bool Accepted, string Source) answer)
    {
        if (!answer.Accepted || _store.Advice is not { ExtraKg: > 0 } decision)
        {
            return;
        }

        _ = ApplyExtraAsync(decision.ExtraKg, answer.Source);
    }

    /// <summary>Raises the INIT block-fuel override by the extra through the existing override
    /// path (the refuel target follows it). The captain's own INIT entry, not an aircraft write
    /// of ours.</summary>
    public async Task<bool> ApplyExtraAsync(double extraKg, string source)
    {
        try
        {
            var effective = EffectiveBlockFuel.Resolve(_overrides.Snapshot(), _ofp.Current, _plannedFuel.Value);
            if (effective.Kg <= 0)
            {
                await _arbiter.SpeakAsync("No block fuel figure to raise — fetch an OFP first.", SpeechPriority.Normal).ConfigureAwait(false);
                return false;
            }

            var raised = RaisedBlockKg(effective.Kg, extraKg);
            var ok = await _overrides.SetAsync(IEfbInitOverrides.FuelRampKg, raised).ConfigureAwait(false);
            _eventLog.Record("fuel.extraApplied", new { source, extraKg, fromKg = effective.Kg, toKg = raised, ok });
            if (!ok)
            {
                await _arbiter.SpeakAsync("The block fuel override failed — see the log.", SpeechPriority.Normal).ConfigureAwait(false);
                return false;
            }

            _store.MarkApplied(extraKg, source);
            var text = $"Block fuel raised to {(raised / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)} tonnes — {Math.Round(extraKg).ToString("0", CultureInfo.InvariantCulture)} kilos extra.";
            await _arbiter.EnqueueAsync(new SpeechRequest(text, SpeechPriority.Normal, Tag: "fuel.decision")).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Applying the extra fuel failed");
            return false;
        }
    }

    private async Task AnswerHoldingAsync()
    {
        try
        {
            var estimate = ComputeHolding(_simClock?.UtcNowOrReal ?? DateTimeOffset.UtcNow);
            if (estimate is null)
            {
                await _arbiter.SpeakAsync("I can't work out the holding time — no fuel flow or no ETA yet.", SpeechPriority.Normal).ConfigureAwait(false);
                return;
            }

            _holding.Set(estimate);
            _eventLog.Record("fuel.holdingAvailableMinutes", new
            {
                trigger = "voice",
                minutes = estimate.AvailableMinutes,
                holdingFuelKg = Math.Round(estimate.HoldingFuelKg),
                overDestinationKg = Math.Round(estimate.EstimatedOverDestinationKg),
                alternateKg = estimate.AlternateKg,
                finalReserveKg = estimate.FinalReserveKg,
                burnKgPerHour = estimate.HoldingBurnKgPerHour,
                divertNow = estimate.DivertNow,
            });
            await _arbiter.EnqueueAsync(new SpeechRequest(estimate.Spoken, SpeechPriority.Normal, Tag: "fuel.holding")).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Holding time answer failed");
        }
    }

    /// <summary>The holding estimate from the live figures: FOB, the burn to the ETA at the
    /// current flow, the OFP's alternate and final reserve. Null without fuel, flow or an ETA.</summary>
    public HoldingEstimate? ComputeHolding(DateTimeOffset nowUtc)
    {
        var ofp = _ofp.Current;
        var fob = _fuelTotal.RawValue is null || _fuelTotal.IsStale ? 0 : _fuelTotal.Value;
        var flow = Math.Max(0, _flow1.Value) + Math.Max(0, _flow2.Value);
        var eta = _progress.Snapshot().EtaUtc;
        if (fob <= 0 || flow <= 0 || eta is not { } etaUtc || etaUtc <= nowUtc)
        {
            return null;
        }

        var toDestination = flow * (etaUtc - nowUtc).TotalHours;
        return HoldingTimeCalculator.Compute(fob, toDestination, ofp?.FuelAlternateKg ?? 0, ofp?.FuelFinalReserveKg ?? 0,
            _options.CurrentValue.HoldingBurnKgPerHour, nowUtc);
    }
}
