using ProsimCompanion.Speech.FoSide;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>
/// Issue #221, the restart part — EGLL 2026-11-14 (session-20261114-043241): the automatic
/// FMGC cross-check ran at 04:24:23Z; the app was restarted at the gate at 04:32:42.0Z on the
/// same plan; the check ran again at 04:32:46.56Z (before the snapshot came back) and at
/// 04:32:48.52Z (the restored plan "began the leg" at 04:32:48.39Z and cleared the latch), and
/// both read "ADIRS one — no attitude" on values ProSim had not delivered yet.
/// </summary>
public sealed class CrossCheckAutoLatchTests
{
    private const string Plan = "req:193487122";

    private static readonly DateTimeOffset FirstCheck = new(2026, 11, 14, 4, 24, 23, TimeSpan.Zero);
    private static readonly DateTimeOffset Restarted = new(2026, 11, 14, 4, 32, 42, 2, TimeSpan.Zero);
    private static readonly DateTimeOffset FirstRepeat = new(2026, 11, 14, 4, 32, 46, 562, TimeSpan.Zero);
    private static readonly DateTimeOffset LegBegan = new(2026, 11, 14, 4, 32, 48, 393, TimeSpan.Zero);
    private static readonly DateTimeOffset SecondRepeat = new(2026, 11, 14, 4, 32, 48, 517, TimeSpan.Zero);
    private static readonly DateTimeOffset SnapshotRestored = new(2026, 11, 14, 4, 32, 48, 545, TimeSpan.Zero);

    [Fact]
    public void RestartAtTheGate_OnTheSamePlan_TheCheckIsNotRepeated()
    {
        // Before the restart: the check runs once and the snapshot keeps it.
        var before = new CrossCheckAutoLatch();
        before.Started(FirstCheck.AddMinutes(-20));
        Assert.Equal(CrossCheckAutoVerdict.Run, before.Judge(FirstCheck, due: true, valuesDelivered: true, Plan));
        var saved = before.Capture();
        Assert.Equal(new CrossCheckAutoSnapshot(true, Plan), saved);

        // After it: the same ticks and edges, at the times of the recording.
        var after = new CrossCheckAutoLatch();
        after.Started(Restarted);
        Assert.Equal(CrossCheckAutoVerdict.HeldForRestore, after.Judge(FirstRepeat, due: true, valuesDelivered: false, planId: null));
        Assert.False(after.LegBegan(Plan));                        // 04:32:48.39Z — nothing to clear yet
        Assert.Equal(CrossCheckAutoVerdict.HeldForRestore, after.Judge(SecondRepeat, due: true, valuesDelivered: false, Plan));
        Assert.True(after.Restore(saved));                         // 04:32:48.5Z — "already done for this plan"

        for (var at = SnapshotRestored; at < SnapshotRestored.AddMinutes(5); at = at.AddSeconds(1))
        {
            Assert.Equal(CrossCheckAutoVerdict.NotDue, after.Judge(at, due: !after.Done, valuesDelivered: true, Plan));
        }
    }

    [Fact]
    public void TheLegBeginningOnThePlanAlreadyChecked_AfterTheRestore_KeepsTheLatch()
    {
        // The other order of the two restore steps: the snapshot first, the leg edge after it.
        var latch = new CrossCheckAutoLatch();
        latch.Started(Restarted);
        latch.Restore(new CrossCheckAutoSnapshot(true, Plan));

        Assert.False(latch.LegBegan(Plan));

        Assert.True(latch.Done);
        Assert.Equal(CrossCheckAutoVerdict.NotDue, latch.Judge(SnapshotRestored, due: false, valuesDelivered: true, Plan));
    }

    [Fact]
    public void ALegOnADifferentPlan_IsANewDeparture()
    {
        var latch = new CrossCheckAutoLatch();
        latch.Started(Restarted);
        latch.Restore(new CrossCheckAutoSnapshot(true, Plan));

        Assert.True(latch.LegBegan("req:193500001"));

        Assert.False(latch.Done);
        Assert.Null(latch.Capture());
        Assert.Equal(CrossCheckAutoVerdict.Run, latch.Judge(SnapshotRestored.AddMinutes(40), due: true, valuesDelivered: true, "req:193500001"));
    }

    [Fact]
    public void ACheckThatRanWithNoPlanLoaded_RunsAgainWhenAPlanBeginsTheLeg()
    {
        var latch = new CrossCheckAutoLatch();
        latch.Started(Restarted);
        Assert.Equal(CrossCheckAutoVerdict.Run, latch.Judge(Restarted.AddMinutes(1), due: true, valuesDelivered: true, planId: null));

        Assert.True(latch.LegBegan(Plan));
        Assert.False(latch.Done);
    }

    [Fact]
    public void NoSnapshot_TheCheckWaitsTheSettle_ThenForTheValues_AndRunsOnce()
    {
        var latch = new CrossCheckAutoLatch();
        latch.Started(Restarted);

        Assert.Equal(CrossCheckAutoVerdict.HeldForRestore, latch.Judge(FirstRepeat, due: true, valuesDelivered: false, Plan));
        Assert.Equal(CrossCheckAutoVerdict.HeldForRestore, latch.Judge(Restarted.AddSeconds(14.9), due: true, valuesDelivered: true, Plan));

        var settled = Restarted + CrossCheckAutoLatch.RestoreSettle;
        Assert.Equal(CrossCheckAutoVerdict.HeldForValues, latch.Judge(settled, due: true, valuesDelivered: false, Plan));
        Assert.Equal(CrossCheckAutoVerdict.HeldForValues, latch.Judge(settled.AddSeconds(5), due: true, valuesDelivered: false, Plan));
        Assert.Equal(CrossCheckAutoVerdict.Run, latch.Judge(settled.AddSeconds(6), due: true, valuesDelivered: true, Plan));
        Assert.Equal(CrossCheckAutoVerdict.NotDue, latch.Judge(settled.AddSeconds(7), due: false, valuesDelivered: true, Plan));
        Assert.Equal(Plan, latch.DonePlanId);
    }

    [Fact]
    public void ValuesThatNeverArrive_DoNotLoseTheCheckForGood()
    {
        var latch = new CrossCheckAutoLatch();
        latch.Started(Restarted);
        var due = Restarted.AddMinutes(10);

        Assert.Equal(CrossCheckAutoVerdict.HeldForValues, latch.Judge(due, due: true, valuesDelivered: false, Plan));
        Assert.Equal(CrossCheckAutoVerdict.HeldForValues, latch.Judge(due.AddSeconds(29), due: true, valuesDelivered: false, Plan));
        Assert.Equal(CrossCheckAutoVerdict.RunWithoutAllValues, latch.Judge(due + CrossCheckAutoLatch.ValuesWait, due: true, valuesDelivered: false, Plan));
        Assert.True(latch.Done);
    }

    [Fact]
    public void TheWaitForValues_StartsAgain_WhenTheTriggerStopsBeingDue()
    {
        var latch = new CrossCheckAutoLatch();
        latch.Started(Restarted);
        var due = Restarted.AddMinutes(10);
        Assert.Equal(CrossCheckAutoVerdict.HeldForValues, latch.Judge(due, due: true, valuesDelivered: false, Plan));
        Assert.Equal(CrossCheckAutoVerdict.NotDue, latch.Judge(due.AddSeconds(10), due: false, valuesDelivered: false, Plan));

        Assert.Equal(CrossCheckAutoVerdict.HeldForValues, latch.Judge(due.AddSeconds(40), due: true, valuesDelivered: false, Plan));
    }

    [Fact]
    public void Reset_ClearsTheLatch_AndSaysWhetherItWasSet()
    {
        var latch = new CrossCheckAutoLatch();
        Assert.False(latch.Reset());
        Assert.Equal(CrossCheckAutoVerdict.Run, latch.Judge(FirstCheck, due: true, valuesDelivered: true, Plan));

        Assert.True(latch.Reset());

        Assert.False(latch.Done);
        Assert.Null(latch.DonePlanId);
        Assert.Null(latch.Capture());
    }

    [Fact]
    public void Restore_OfNothingDone_OnlyEndsTheSettle()
    {
        var latch = new CrossCheckAutoLatch();
        latch.Started(Restarted);

        Assert.False(latch.Restore(new CrossCheckAutoSnapshot(false, null)));

        Assert.False(latch.Done);
        Assert.Equal(CrossCheckAutoVerdict.Run, latch.Judge(FirstRepeat, due: true, valuesDelivered: true, Plan));
    }
}
