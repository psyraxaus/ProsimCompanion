using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Persona;

namespace ProsimCompanion.Speech.FoSide;

/// <summary>
/// "Captain, the FMS still holds the previous flight plan." (ADR-0019 §3, issue #215) — said
/// once, when a new OFP lands while the leg boundary is still in <b>Turnaround</b> (no FMS
/// clear since deboarding) and the box still shows the flown pair. Never when the FMS was
/// cleared first (Awaiting plan, whether by the IOS Reset FMS or ProSim's reset-after-landing
/// option), never when the FMS already reads the new plan's route, never on the first plan of
/// the session. One reminder per leg by construction: the leg-began edge fires once per plan.
/// Read-and-speak only.
/// </summary>
public sealed class FmsPlanReminderService : Core.Hosting.IStartupModule, IDisposable
{
    public const string Line = "Captain, the FMS still holds the previous flight plan.";
    public const string Tag = "fo.fmsReminder";
    public const string EventName = "fo.fms-reminder";

    private readonly ILegBoundaryEvents _legBoundary;
    private readonly FmsPlanWatcher _fms;
    private readonly OfpStore _ofp;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<FmsPlanReminderService> _logger;
    private readonly StyledSpeechService? _styled;
    private readonly CancellationTokenSource _shutdown = new();

    public FmsPlanReminderService(
        ILegBoundaryEvents legBoundary,
        FmsPlanWatcher fms,
        OfpStore ofp,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<FmsPlanReminderService> logger,
        StyledSpeechService? styled = null)
    {
        ArgumentNullException.ThrowIfNull(legBoundary);
        ArgumentNullException.ThrowIfNull(fms);
        ArgumentNullException.ThrowIfNull(ofp);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        _legBoundary = legBoundary;
        _fms = fms;
        _ofp = ofp;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
        _styled = styled;
    }

    public void Start() => _legBoundary.LegBegan += OnLegBegan;

    public void Dispose()
    {
        _legBoundary.LegBegan -= OnLegBegan;
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    /// <summary>The rule, pure: the plan arrived in Turnaround (the FMS was never cleared this
    /// turnaround), the FMS holds a valid pair, and that pair is not the new plan's.</summary>
    public static bool ShouldRemind(LegBoundaryState from, string? fmsOrigin, string? fmsDestination, string? newOrigin, string? newDestination)
    {
        if (from != LegBoundaryState.Turnaround)
        {
            return false;
        }

        if (!FmsPlanRules.IsPlanPresent(fmsOrigin, fmsDestination))
        {
            return false; // the box is empty (or not read yet): nothing stale to point at
        }

        var fmsIsNewPlan = string.Equals(fmsOrigin?.Trim(), newOrigin?.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(fmsDestination?.Trim(), newDestination?.Trim(), StringComparison.OrdinalIgnoreCase);
        return !fmsIsNewPlan;
    }

    private void OnLegBegan(object? sender, LegBeganEventArgs e)
    {
        try
        {
            var ofp = _ofp.Current;
            var fmsOrigin = _fms.Origin;
            var fmsDestination = _fms.Destination;
            if (!ShouldRemind(e.From, fmsOrigin, fmsDestination, ofp?.OriginIcao, ofp?.DestinationIcao))
            {
                return;
            }

            _eventLog.Record(EventName, new
            {
                planId = e.PlanId,
                previousPlanId = e.PreviousPlanId,
                fmsOrigin,
                fmsDestination,
                newOrigin = ofp?.OriginIcao,
                newDestination = ofp?.DestinationIcao,
            });
            _logger.LogInformation(
                "New plan {PlanId} landed while the FMS still holds {FmsOrigin}-{FmsDestination} (plan {NewOrigin}-{NewDestination}) — reminding once",
                e.PlanId, fmsOrigin, fmsDestination, ofp?.OriginIcao, ofp?.DestinationIcao);
            _ = SpeakAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FMS plan reminder failed");
        }
    }

    private async Task SpeakAsync()
    {
        try
        {
            var text = Line;
            if (_styled is not null)
            {
                var styled = await _styled.StyleAsync(Line, PersonaStyleCategory.Advisory, "fo:fmsReminder", _shutdown.Token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(styled))
                {
                    text = styled;
                }
            }

            await _arbiter.EnqueueAsync(new SpeechRequest(text, SpeechPriority.Normal, Ttl: TimeSpan.FromMinutes(2), Tag: Tag), _shutdown.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FMS plan reminder could not be spoken");
        }
    }
}
