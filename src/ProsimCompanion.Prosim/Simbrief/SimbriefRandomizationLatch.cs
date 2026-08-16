using System.Globalization;

namespace ProsimCompanion.Prosim.Simbrief;

/// <summary>One OFP's randomized load figures, latched after the first import so re-imports of
/// the same plan are idempotent (issue #64: six imports during one boarding re-rolled the pax
/// count 89→91→88→85→91→89 while the prelim loadsheet had already cut at 89, and GSX armed a
/// different figure each time). <see cref="BookedMap"/> is the post-randomization seat map;
/// <see cref="PaxCount"/>/<see cref="CargoKg"/> are the figures actually written to ProSim.</summary>
public sealed record SimbriefRandomizationLatch(string OfpKey, bool[] BookedMap, int PaxCount, double CargoKg);

/// <summary>
/// Pure latch policy for SimBrief pax/cargo randomization — extracted so the "same OFP or new
/// OFP?" decision is unit-testable without HTTP or dataref plumbing.
/// </summary>
public static class SimbriefRandomizationPolicy
{
    /// <summary>
    /// Stable identity of an OFP for randomization latching: SimBrief's <c>request_id</c>
    /// changes on every (re)generation, so it is the primary key; an OFP without one (never
    /// observed live, but the field is external input) falls back to flight number + scheduled
    /// departure date. Empty when neither exists — such an OFP is never latched.
    /// </summary>
    public static string OfpKey(string? requestId, string? flightNumber, DateTimeOffset? scheduledOutUtc)
    {
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            return requestId.Trim();
        }

        if (string.IsNullOrWhiteSpace(flightNumber) || scheduledOutUtc is not { } schedOut)
        {
            return "";
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{flightNumber.Trim().ToUpperInvariant()}|{schedOut:yyyy-MM-dd}");
    }

    /// <summary>
    /// Whether a latched randomization should be reused for this import. Reused only for the
    /// SAME OFP identity and never on a forced (explicit user) re-import — a new OFP or a
    /// user-triggered fetch re-randomizes by design; everything else (automation retriggers,
    /// dataref flaps) must reproduce the figures the loadsheet already committed to.
    /// </summary>
    public static bool ShouldReuse(SimbriefRandomizationLatch? latched, string ofpKey, bool force)
        => !force
            && latched is not null
            && ofpKey.Length > 0
            && string.Equals(latched.OfpKey, ofpKey, StringComparison.Ordinal);
}
