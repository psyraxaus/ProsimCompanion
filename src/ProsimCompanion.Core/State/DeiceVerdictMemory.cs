using ProsimCompanion.Core.Aircraft.Ofp;

namespace ProsimCompanion.Core.State;

/// <summary>
/// The settled de-icing answer of one departure, as the flight-state snapshot keeps it across
/// an app restart (issue #166, EGLL 2026-11-14: "no de-icing" by voice at 04:20:45Z, the app
/// restarted at the stand at 04:32:42Z on the same plan, and at 04:40:51Z GSX's own offer was
/// answered "Yes" on "policy verdict Pending" — the trucks came against the captain's no).
/// Only an ANSWER is kept: the captain's yes or no, the timeout's no, the policy's own
/// <c>auto</c> request. A verdict without an answer (not required, no data, a question still
/// open) is re-reached by the policy when the step's turn comes again.
/// </summary>
/// <param name="PlanId">The plan the answer was given for (<see cref="OfpPlanIdentity"/>);
/// null when no plan was loaded then. An answer never crosses to another plan.</param>
/// <param name="Accepted">True = de-icing is requested this departure; false = declined.</param>
/// <param name="Source">Who answered: "voice", "web", "called", "timeout", "policy".</param>
/// <param name="Reason">The decline reason as the decision log recorded it; null on yes.</param>
/// <param name="AnsweredAtUtc">When the answer was given.</param>
/// <param name="Verdict">The policy verdict that stood with the answer.</param>
/// <param name="VerdictReason">The policy's sentence for that verdict.</param>
/// <param name="OatC">The outside air temperature the verdict used.</param>
/// <param name="Precip">The departure METAR's precipitation word.</param>
/// <param name="Icao">The departure airport the weather was read for.</param>
/// <param name="StepNext">The DeIce step's turn had come on the departure sequence.</param>
public sealed record DeiceVerdictMemory(
    string? PlanId,
    bool Accepted,
    string Source,
    string? Reason,
    DateTimeOffset AnsweredAtUtc,
    DeicePolicyVerdict Verdict,
    string? VerdictReason,
    double? OatC,
    string? Precip,
    string? Icao,
    bool StepNext)
{
    /// <summary>What to keep of a snapshot: null while the cycle has no answer.</summary>
    /// <param name="planId">The plan the answer was given for.</param>
    public static DeiceVerdictMemory? Of(DeiceRequestSnapshot snapshot, string? planId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Asking.Answer is not { } answer)
        {
            return null;
        }

        return new DeiceVerdictMemory(
            planId,
            answer.Accepted,
            answer.Source,
            answer.Reason,
            answer.AtUtc,
            snapshot.Verdict,
            snapshot.Reason,
            snapshot.OatC,
            snapshot.Precip,
            snapshot.Icao,
            snapshot.StepNext);
    }

    /// <summary>True when the kept answer and the plan now loaded are the same departure: the
    /// same plan id, or one side without a plan (nothing contradicts it — the snapshot file's
    /// own identity check already passed). A different plan is another leg: the answer is
    /// dropped, exactly as the new-plan cycle reset drops it in a running session.</summary>
    public bool IsForPlan(string? currentPlanId)
    {
        var saved = OfpPlanIdentity.Normalize(PlanId);
        var current = OfpPlanIdentity.Normalize(currentPlanId);
        return saved is null || current is null || string.Equals(saved, current, StringComparison.Ordinal);
    }

    /// <summary>The snapshot with this answer put back. A session that already has an answer
    /// keeps its own (the captain spoke again before the restore ran); the question part
    /// comes back closed — an answered question is not asked again.</summary>
    public DeiceRequestSnapshot ApplyTo(DeiceRequestSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (current.Asking.Answer is not null || string.IsNullOrWhiteSpace(Source))
        {
            return current;
        }

        var reason = Accepted ? null : (string.IsNullOrWhiteSpace(Reason) ? $"declined ({Source})" : Reason);
        return current with
        {
            Verdict = Verdict,
            Reason = string.IsNullOrWhiteSpace(VerdictReason) ? current.Reason : VerdictReason,
            OatC = OatC,
            Precip = string.IsNullOrWhiteSpace(Precip) ? current.Precip : Precip,
            Icao = Icao,
            DecidedAtUtc = AnsweredAtUtc,
            StepNext = StepNext || current.StepNext,
            Asking = new CaptainsQuestionState(null, new CaptainsAnswer(Accepted, null, Source, reason, AnsweredAtUtc, null)),
        };
    }
}
