using ProsimCompanion.Core.Aircraft.Ofp;

namespace ProsimCompanion.Gsx.Automation;

/// <summary>
/// An immutable view of the departure pump's cycle-scoped latches, taken once per pump so the
/// effect planner is a pure function of (reading, outcome, latches).
/// </summary>
/// <param name="ForceNext">Single-shot force-next pending (INT/RAD / web button).</param>
/// <param name="TankeringPrelimRaised">The refuel-active edge was raised in place of a
/// tankering-skipped refuel this cycle (#117) — once per turnaround, reset at arrival.</param>
/// <param name="PlanAvailableSinceUtc">UTC instant the flight plan first read as available
/// this cycle; null while none stands (#118 settle window).</param>
/// <param name="TurnaroundReadySinceUtc">voiceThenAuto settle stamp: when the turnaround
/// auto-start conditions first held (carried between evaluations by the pure core).</param>
/// <param name="TurnaroundQuestionAsked">The FO's "commence ground services?" question was
/// raised this cycle (issue #169, ask-first).</param>
/// <param name="TurnaroundDeclined">The captain declined (or nobody answered) — stay on the
/// voice gate.</param>
/// <param name="ArrivalOfpKnown">An arrival baseline exists (an in-session arrival or a
/// resync-recovered turnaround).</param>
/// <param name="ArrivalOfp">The plan that was current when the aircraft arrived (issue #169):
/// the next leg's OFP is "loaded" once the store holds a different one. Null with
/// <paramref name="ArrivalOfpKnown"/> true after a resync-recovered turnaround — the store is
/// in-memory, so whatever it holds was imported after the restart and counts as new.</param>
/// <param name="LastImportAttemptUtc">When the automation last fired the SimBrief import
/// (60 s cooldown); <see cref="DateTimeOffset.MinValue"/> = never.</param>
/// <param name="TriggerAttempts">Dispatch attempts per service this cycle (the "(attempt N)"
/// suffix on re-offered triggers).</param>
internal sealed record DepartureLatchSnapshot(
    bool ForceNext,
    bool TankeringPrelimRaised,
    DateTimeOffset? PlanAvailableSinceUtc,
    DateTimeOffset? TurnaroundReadySinceUtc,
    bool TurnaroundQuestionAsked,
    bool TurnaroundDeclined,
    bool ArrivalOfpKnown,
    OfpData? ArrivalOfp,
    DateTimeOffset LastImportAttemptUtc,
    IReadOnlyDictionary<string, int> TriggerAttempts)
{
    /// <summary>A fresh process: nothing latched, no arrival seen.</summary>
    internal static DepartureLatchSnapshot Initial { get; } = new(
        ForceNext: false,
        TankeringPrelimRaised: false,
        PlanAvailableSinceUtc: null,
        TurnaroundReadySinceUtc: null,
        TurnaroundQuestionAsked: false,
        TurnaroundDeclined: false,
        ArrivalOfpKnown: false,
        ArrivalOfp: null,
        LastImportAttemptUtc: DateTimeOffset.MinValue,
        TriggerAttempts: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// The departure pump's cycle-scoped latch storage — the one mutable thing the host keeps
/// beside the lock. Every write is a single named mutation so the planner's latch effects
/// map one-to-one onto them and a force-next raised while a pump is running is never
/// overwritten by that pump's write-back (the field-by-field discipline the shell always had).
/// Thread-safe; read as a whole through <see cref="Snapshot"/>.
/// </summary>
public sealed class DepartureCycleLatches
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _triggerAttempts = new(StringComparer.OrdinalIgnoreCase);
    private bool _forceNext;
    private bool _tankeringPrelimRaised;
    private DateTimeOffset? _planAvailableSinceUtc;
    private DateTimeOffset? _turnaroundReadySinceUtc;
    private bool _turnaroundQuestionAsked;
    private bool _turnaroundDeclined;
    private bool _arrivalOfpKnown;
    private OfpData? _arrivalOfp;
    private DateTimeOffset _lastImportAttemptUtc = DateTimeOffset.MinValue;

    /// <summary>A consistent copy of every latch.</summary>
    internal DepartureLatchSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new DepartureLatchSnapshot(
                _forceNext,
                _tankeringPrelimRaised,
                _planAvailableSinceUtc,
                _turnaroundReadySinceUtc,
                _turnaroundQuestionAsked,
                _turnaroundDeclined,
                _arrivalOfpKnown,
                _arrivalOfp,
                _lastImportAttemptUtc,
                new Dictionary<string, int>(_triggerAttempts, StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>Single-shot force-next: raised by the smart button / web, consumed by the
    /// evaluation that sees it.</summary>
    public void SetForceNext(bool value)
    {
        lock (_gate)
        {
            _forceNext = value;
        }
    }

    public void MarkTankeringPrelimRaised()
    {
        lock (_gate)
        {
            _tankeringPrelimRaised = true;
        }
    }

    public void SetPlanAvailableSince(DateTimeOffset? value)
    {
        lock (_gate)
        {
            _planAvailableSinceUtc = value;
        }
    }

    public void SetTurnaroundReadySince(DateTimeOffset? value)
    {
        lock (_gate)
        {
            _turnaroundReadySinceUtc = value;
        }
    }

    public void MarkTurnaroundQuestionAsked()
    {
        lock (_gate)
        {
            _turnaroundQuestionAsked = true;
        }
    }

    public void MarkTurnaroundDeclined()
    {
        lock (_gate)
        {
            _turnaroundDeclined = true;
        }
    }

    /// <summary>The plan the aircraft arrived with becomes the baseline for "next leg's OFP
    /// loaded" (issue #169). Null = resync-recovered turnaround: anything the in-memory store
    /// holds or receives after the restart counts as the next leg's plan.</summary>
    public void SetArrivalBaseline(OfpData? arrivalOfp)
    {
        lock (_gate)
        {
            _arrivalOfp = arrivalOfp;
            _arrivalOfpKnown = true;
        }
    }

    public void SetLastImportAttempt(DateTimeOffset value)
    {
        lock (_gate)
        {
            _lastImportAttemptUtc = value;
        }
    }

    /// <summary>Stores the attempt number the planner computed for this dispatch.</summary>
    public void SetTriggerAttempt(string serviceId, int attempt)
    {
        lock (_gate)
        {
            _triggerAttempts[serviceId] = attempt;
        }
    }

    /// <summary>Arrival boundary: fresh cycle latches, the arrived-with plan as the turnaround
    /// baseline. Everything except the arrival baseline reads as a fresh process again.</summary>
    public void ResetForArrival(OfpData? arrivalOfp)
    {
        lock (_gate)
        {
            _tankeringPrelimRaised = false;
            _planAvailableSinceUtc = null;
            _forceNext = false;
            // Turnaround auto-start bookkeeping (issue #169): the plan we flew in on is
            // the baseline — the next leg's OFP is whatever replaces it.
            _arrivalOfp = arrivalOfp;
            _arrivalOfpKnown = true;
            _turnaroundReadySinceUtc = null;
            _turnaroundQuestionAsked = false;
            _turnaroundDeclined = false;
            _triggerAttempts.Clear();
        }
    }
}
