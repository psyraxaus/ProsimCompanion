using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Scenarios;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Llm;
using ProsimCompanion.Speech.Persona;
using ProsimCompanion.Speech.Questions;

namespace ProsimCompanion.Speech.Scenarios;

/// <summary>
/// The FO's decision turn after a scenario's ECAM actions are done (issue #197, ADR-0018):
/// "continue, return or divert?" as a pick-one captain's question — the model's line when it
/// is on, every number checked against the fact sheet, the catalogue entry's template as the
/// floor — and the diversion declared from the answer. Cut out of <see cref="ScenarioService"/>
/// (review 2026-11-05: six optional dependencies lived there for this alone).
/// </summary>
public sealed class ScenarioDecisionService : IDisposable
{
    private readonly IOptionsMonitor<BriefingOptions> _briefing;
    private readonly ScenarioDecisionStore _decision;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<ScenarioDecisionService> _logger;
    private readonly OfpStore? _ofp;
    private readonly DiversionStore? _diversion;
    private readonly PersonaService? _persona;
    private readonly OpenAiChatClient? _llm;
    private readonly LlmHealthStore? _llmHealth;
    private readonly FoFactSource? _facts;
    private readonly object _gate = new();
    private ScenarioPlan? _raisedFor;

    public ScenarioDecisionService(
        IOptionsMonitor<BriefingOptions> briefing,
        ScenarioDecisionStore decision,
        JsonlEventLog eventLog,
        ILogger<ScenarioDecisionService> logger,
        OfpStore? ofp = null,
        DiversionStore? diversion = null,
        PersonaService? persona = null,
        OpenAiChatClient? llm = null,
        LlmHealthStore? llmHealth = null,
        FoFactSource? facts = null)
    {
        ArgumentNullException.ThrowIfNull(briefing);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        _briefing = briefing;
        _decision = decision;
        _eventLog = eventLog;
        _logger = logger;
        _ofp = ofp;
        _diversion = diversion;
        _persona = persona;
        _llm = llm;
        _llmHealth = llmHealth;
        _facts = facts;
        _decision.Answered += OnDecisionAnswered;
    }

    /// <summary>The clock, replaceable by tests.</summary>
    internal Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    public void Dispose() => _decision.Answered -= OnDecisionAnswered;

    /// <summary>Raises the decision turn for <paramref name="plan"/>: the model's line when it
    /// is on (every number checked against the fact sheet), the entry's template as the floor,
    /// as a pick-one captain's question.</summary>
    public async Task RaiseAsync(ScenarioPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        lock (_gate)
        {
            _raisedFor = plan;
        }

        var ofp = _ofp?.Current;
        var floor = ScenarioScheduler.DecisionLine(plan.Decision, ofp?.AlternateName is { Length: > 0 } an ? an : ofp?.AlternateIcao, ofp?.DestinationName is { Length: > 0 } dn ? dn : ofp?.DestinationIcao);
        var line = floor;
        var source = "template";
        try
        {
            var llmUp = _llm is { IsConfigured: true } && _briefing.CurrentValue.LlmEnabled && _llmHealth?.Snapshot().IsUnhealthy != true;
            if (llmUp && _facts is not null)
            {
                var sheet = _facts.Build(Now());
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                budget.CancelAfter(TimeSpan.FromSeconds(15));
                var reply = await _llm!.CompleteAsync(
                    (_persona?.SystemPromptFragment(PersonaStyleCategory.Advisory) ?? "")
                    + "You are the First Officer of an Airbus A320. The ECAM actions for a failure are complete and you now give the Captain "
                    + "your recommendation in ONE or TWO short spoken sentences ending with 'your call, captain' — continue, return or divert, "
                    + "with the one reason that matters. Use only the FACTS and the SHAPE; never add a figure of your own. Plain English for text-to-speech.",
                    "FAILURE: " + plan.Title + "\nSHAPE (keep the meaning): " + floor + "\nFACTS:\n" + sheet.Text + "\n\nSay it now.",
                    budget.Token).ConfigureAwait(false);
                var candidate = (reply ?? "").Trim().Trim('"', '\'');
                if (!string.IsNullOrWhiteSpace(candidate) && NumberVerifier.Check(SpokenNumberText.ToDigits(candidate), sheet.AllowedNumbers).Ok)
                {
                    line = candidate;
                    source = "llm";
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Scenario decision: model turn failed — template");
        }

        _eventLog.Record("scenario.decision", new { scenario = plan.ScenarioId, source, line });
        if (!_decision.Ask(plan.ScenarioId, plan.Title, line))
        {
            _logger.LogInformation("Scenario decision not raised — a question already stands");
        }
    }

    private void OnDecisionAnswered(object? sender, (bool Accepted, string Source) answer)
    {
        var outcome = _decision.Outcome;
        ScenarioPlan? plan;
        lock (_gate)
        {
            plan = _raisedFor;
        }

        _eventLog.Record("scenario.decided", new { scenario = plan?.ScenarioId, outcome, source = answer.Source });
        if (plan is null || _diversion is null || outcome == ScenarioDecisionStore.Continue)
        {
            return;
        }

        try
        {
            var ofp = _ofp?.Current;
            var (icao, name) = outcome == ScenarioDecisionStore.Divert
                ? (ofp?.AlternateIcao, ofp?.AlternateName)
                : (ofp?.OriginIcao, ofp?.OriginName);
            if (string.IsNullOrWhiteSpace(icao))
            {
                _logger.LogInformation("Scenario decision {Outcome}: no airport in the plan — the diversion is the captain's own", outcome);
                return;
            }

            if (_diversion.Declare(icao, name, DiversionReason.Technical, Now()))
            {
                _logger.LogInformation("Scenario decision {Outcome}: diversion declared to {Icao}", outcome, icao);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scenario decision: the diversion could not be declared");
        }
    }
}
