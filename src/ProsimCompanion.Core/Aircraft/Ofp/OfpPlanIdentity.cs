namespace ProsimCompanion.Core.Aircraft.Ofp;

/// <summary>
/// The one rule for "is this the same plan?" (owner rule 2026-11-01, "a new plan is a new
/// leg"; ADR-0019 §2): the SimBrief request id when the plan has one, else the route. The
/// flight number is deliberately NOT part of it — a re-dispatch of the same request with a
/// corrected callsign is the same leg. Shared by the GSX departure-cycle reset, the leg
/// boundary and the snapshot identity so no two consumers can disagree about what a new
/// plan is.
/// </summary>
public static class OfpPlanIdentity
{
    /// <summary>The plan's identity, or null when the plan carries neither a request id nor
    /// a usable route.</summary>
    public static string? PlanId(OfpData? ofp)
    {
        if (ofp is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(ofp.RequestId))
        {
            return "req:" + ofp.RequestId.Trim();
        }

        return string.IsNullOrWhiteSpace(ofp.OriginIcao) || string.IsNullOrWhiteSpace(ofp.DestinationIcao)
            ? null
            : $"route:{ofp.OriginIcao.Trim().ToUpperInvariant()}-{ofp.DestinationIcao.Trim().ToUpperInvariant()}";
    }

    /// <summary>Brings an id written before this identity existed — a bare SimBrief request id,
    /// as the flight-state snapshot stored it — to the same spelling, so one compare serves
    /// both (review 2026-11-05: the snapshot compared raw request ids, a third spelling of
    /// "same plan"). Null for blank.</summary>
    public static string? Normalize(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var trimmed = id.Trim();
        return trimmed.StartsWith("req:", StringComparison.Ordinal) || trimmed.StartsWith("route:", StringComparison.Ordinal)
            ? trimmed
            : "req:" + trimmed;
    }
}
