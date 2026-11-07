using System.Text.RegularExpressions;
using ProsimCompanion.Core.Airports.Parking;

namespace ProsimCompanion.Core.Radios;

/// <summary>Where this departure's push-and-start clearance stands.</summary>
public enum PushClearanceState
{
    /// <summary>Nothing asked, nothing heard this cycle.</summary>
    None,

    /// <summary>The request went out on COM1 (by the beacon sequence or the captain's voice);
    /// ATC has not approved yet.</summary>
    Requested,

    /// <summary>ATC approved the push (and the start); <see cref="PushClearance.FacingDeg"/>
    /// carries the direction when ATC named one.</summary>
    Approved,

    /// <summary>ATC answered without approving ("hold position, expect a five minute delay") —
    /// the sequence keeps waiting for the approval that follows.</summary>
    Held,
}

/// <summary>The clearance as last heard: its state, ATC's words, the direction ATC named
/// ("Face South" → 180) and when it changed.</summary>
public sealed record PushClearance(PushClearanceState State, string? AtcText, double? FacingDeg, DateTimeOffset AtUtc)
{
    public static PushClearance None { get; } = new(PushClearanceState.None, null, null, DateTimeOffset.MinValue);
}

/// <summary>
/// The radio the beacon-orchestrated pushback asks for its push-and-start clearance (owner
/// request 2026-11-07: "before calling GSX ground we need to call ATC"). Implemented over
/// SayIntentions in the Speech project; the GSX sequence takes it as an optional
/// collaborator and calls pushback without it exactly as before.
/// </summary>
public interface IPushClearanceRadio
{
    /// <summary>True when a request can go out now: the ATC pillar is on, a flight is filed
    /// and an API key is at hand.</summary>
    bool Available { get; }

    /// <summary>The clearance as it stands for this departure cycle.</summary>
    PushClearance Current { get; }

    /// <summary>Transmits "request push and start" on COM1 in the FO's voice. False when it
    /// could not be sent (the caller then carries on without a clearance).</summary>
    Task<bool> RequestPushAndStartAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads ATC's answer to a push-and-start request. Live shapes (SayIntentions, EGLL
/// 2026-10-30 / 2026-11-07): "Push and start approved. Face South.", "Push and start approved.
/// Face North-West.", "Pushback approved, face east", FAA "Push and start approved, expect
/// runway 27R". A hold: "Hold position, expect a five minute delay", "Stand by for push".
/// Pure, so the shape is testable without a radio.
/// </summary>
public static partial class PushClearanceParser
{
    [GeneratedRegex(@"\b(push(?:\s*back)?(?:\s+and\s+start)?|start\s+and\s+push|start\s*up)\b[^.]*?\bapproved\b|\bapproved\b[^.]*?\bpush|\bcleared\s+(?:to|for)\s+push",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ApprovalPattern();

    [GeneratedRegex(@"\b(hold\s+position|stand\s*by|expect\s+(?:a\s+)?\w*\s*(?:minute|min)|unable|negative|not\s+approved|delay)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HoldPattern();

    [GeneratedRegex(@"\bfac(?:e|ing)\s+(?<dir>north[\s-]?east|north[\s-]?west|south[\s-]?east|south[\s-]?west|north|south|east|west)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FacingPattern();

    /// <summary>True when the line approves the push ("not approved" is a hold).</summary>
    public static bool IsApproval(string? text)
        => !string.IsNullOrWhiteSpace(text)
            && ApprovalPattern().IsMatch(text)
            && !text.Contains("not approved", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the line answers the request without approving it.</summary>
    public static bool IsHold(string? text)
        => !string.IsNullOrWhiteSpace(text) && !IsApproval(text) && HoldPattern().IsMatch(text);

    /// <summary>The direction ATC said to face, as a bearing (eight points), or null when the
    /// line names none — "Face South." → 180, "face North-West" → 315.</summary>
    public static double? Facing(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = FacingPattern().Match(text);
        return match.Success ? Compass.Parse(match.Groups["dir"].Value) : null;
    }
}

/// <summary>
/// The pure state of this cycle's push clearance, fed with the request and every ATC line:
/// a request opens it (<see cref="PushClearanceState.Requested"/>), an approval closes it
/// whether or not a request was heard first (the captain may have asked by voice before the
/// beacon sequence got there), a hold is noted and waited through, and anything else is
/// ignored. The host resets it with the flight cycle.
/// </summary>
public sealed class PushClearanceTracker
{
    public PushClearance Current { get; private set; } = PushClearance.None;

    /// <summary>The request went out.</summary>
    public PushClearance NoteRequested(DateTimeOffset nowUtc)
    {
        if (Current.State is PushClearanceState.Approved)
        {
            return Current; // already approved — a second request changes nothing
        }

        Current = new PushClearance(PushClearanceState.Requested, Current.AtcText, null, nowUtc);
        return Current;
    }

    /// <summary>One ATC → pilot line; returns the new state when the line changed it, else null.</summary>
    public PushClearance? OnAtcLine(string? text, DateTimeOffset nowUtc)
    {
        if (PushClearanceParser.IsApproval(text))
        {
            Current = new PushClearance(PushClearanceState.Approved, text!.Trim(), PushClearanceParser.Facing(text), nowUtc);
            return Current;
        }

        if (Current.State is PushClearanceState.Requested or PushClearanceState.Held && PushClearanceParser.IsHold(text))
        {
            Current = new PushClearance(PushClearanceState.Held, text!.Trim(), null, nowUtc);
            return Current;
        }

        return null;
    }

    public void Reset() => Current = PushClearance.None;
}
