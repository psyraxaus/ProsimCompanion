using ProsimCompanion.Core.Radios;
using Xunit;

namespace ProsimCompanion.Core.Tests.Radios;

/// <summary>The push-and-start clearance (owner request 2026-11-07): ATC's live shapes from the
/// EGLL flights of 2026-10-30 and 2026-11-07.</summary>
public sealed class PushClearanceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 23, 55, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Push and start approved. Face South.", true)]
    [InlineData("Speedbird-eight-six-one, Push and start approved. Face North-West.", true)]
    [InlineData("Pushback approved, face east.", true)]
    [InlineData("Push and start approved, expect runway 27R.", true)]
    [InlineData("Start up approved, push at your discretion.", true)]
    [InlineData("Cleared to push, face north.", true)]
    [InlineData("Push and start not approved, hold position.", false)]
    [InlineData("Hold position, expect a five minute delay.", false)]
    [InlineData("Taxi to holding point runway 27L via A, Link 53.", false)]
    [InlineData("All aircraft be advised, information Uniform is now current. QNH 1023.", false)]
    [InlineData("", false)]
    public void Approval_IsReadFromATCsWords(string text, bool approved)
        => Assert.Equal(approved, PushClearanceParser.IsApproval(text));

    [Theory]
    [InlineData("Hold position, expect a five minute delay.", true)]
    [InlineData("Stand by for push.", true)]
    [InlineData("Push and start not approved, hold position.", true)]
    [InlineData("Push and start approved. Face South.", false)]
    [InlineData("Taxi to holding point runway 27L via A.", false)]
    public void Hold_IsAnAnswerThatDoesNotApprove(string text, bool held)
        => Assert.Equal(held, PushClearanceParser.IsHold(text));

    [Theory]
    [InlineData("Push and start approved. Face South.", 180.0)]
    [InlineData("Push and start approved. Face North-West.", 315.0)]
    [InlineData("Pushback approved, facing north east.", 45.0)]
    [InlineData("Push and start approved, expect runway 27R.", null)]
    public void Facing_IsTheDirectionATCNamed(string text, double? expected)
        => Assert.Equal(expected, PushClearanceParser.Facing(text));

    [Fact]
    public void Tracker_RequestThenApproval_CarriesTheDirection()
    {
        var tracker = new PushClearanceTracker();
        Assert.Equal(PushClearanceState.None, tracker.Current.State);

        var requested = tracker.NoteRequested(T0);
        Assert.Equal(PushClearanceState.Requested, requested.State);

        Assert.Null(tracker.OnAtcLine("All aircraft, information Uniform is now current.", T0.AddSeconds(2)));
        Assert.Equal(PushClearanceState.Requested, tracker.Current.State);

        var approved = tracker.OnAtcLine("Push and start approved. Face South.", T0.AddSeconds(5));
        Assert.NotNull(approved);
        Assert.Equal(PushClearanceState.Approved, approved.State);
        Assert.Equal(180, approved.FacingDeg);
        Assert.Equal("Push and start approved. Face South.", approved.AtcText);
    }

    [Fact]
    public void Tracker_AHoldIsWaitedThrough_UntilTheApproval()
    {
        var tracker = new PushClearanceTracker();
        tracker.NoteRequested(T0);

        var held = tracker.OnAtcLine("Hold position, expect a five minute delay.", T0.AddSeconds(4));
        Assert.Equal(PushClearanceState.Held, held!.State);

        var approved = tracker.OnAtcLine("Push and start approved, face east.", T0.AddMinutes(5));
        Assert.Equal(PushClearanceState.Approved, approved!.State);
        Assert.Equal(90, approved.FacingDeg);
    }

    [Fact]
    public void Tracker_AnApprovalWithoutARequest_Counts_AndASecondRequestChangesNothing()
    {
        // The captain asked by voice before the beacon sequence got there.
        var tracker = new PushClearanceTracker();
        var approved = tracker.OnAtcLine("Push and start approved. Face North-West.", T0);
        Assert.Equal(PushClearanceState.Approved, approved!.State);

        var after = tracker.NoteRequested(T0.AddSeconds(10));
        Assert.Equal(PushClearanceState.Approved, after.State);
        Assert.Equal(315, after.FacingDeg);
    }

    [Fact]
    public void Tracker_AHoldWithNoRequest_IsIgnored_AndResetClears()
    {
        var tracker = new PushClearanceTracker();
        Assert.Null(tracker.OnAtcLine("Stand by.", T0));

        tracker.NoteRequested(T0);
        tracker.OnAtcLine("Push and start approved.", T0.AddSeconds(3));
        tracker.Reset();

        Assert.Equal(PushClearance.None, tracker.Current);
    }
}
