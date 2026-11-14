namespace ProsimCompanion.Speech.FoSide;

/// <summary>What the auto trigger decided this tick.</summary>
public enum CrossCheckAutoVerdict
{
    /// <summary>Nothing to do: already done for this plan, or the trigger's own rule is not met.</summary>
    NotDue,

    /// <summary>Due, but the app has just started and the flight-state snapshot may still say
    /// "already done" — wait for it (or for the settle to pass).</summary>
    HeldForRestore,

    /// <summary>Due, but ProSim has not yet delivered every value the readout speaks.</summary>
    HeldForValues,

    /// <summary>Run it now — the latch is set.</summary>
    Run,

    /// <summary>Run it now although some values never arrived (<see cref="CrossCheckAutoLatch.ValuesWait"/>
    /// passed): a ProSim build without one of the datarefs must not lose the check for good.</summary>
    RunWithoutAllValues,
}

/// <summary>What the flight-state snapshot keeps of the auto cross-check (issue #221).</summary>
/// <param name="Done">The automatic cross-check has run for this departure.</param>
/// <param name="PlanId">The plan it ran for (<c>OfpPlanIdentity.PlanId</c>), or null when no
/// plan was loaded.</param>
public sealed record CrossCheckAutoSnapshot(bool Done, string? PlanId);

/// <summary>
/// The once-per-departure latch of the FMGC cross-check's auto trigger, pure (issue #221, the
/// restart part). EGLL 2026-11-14: the check ran at 04:24:23Z; the app was restarted at the
/// gate at 04:32:42Z on the same plan and the check ran again at 04:32:46Z and at 04:32:48Z,
/// both times reading "ADIRS one — no attitude" on three aligned ADIRS. Three causes, three
/// rules:
/// <list type="bullet">
/// <item>the latch lived in memory only — it now rides in the flight-state snapshot
/// (<see cref="Capture"/> / <see cref="Restore"/>) with the plan it belongs to;</item>
/// <item>the first run came 4 s after the start, 2 s before the snapshot was restored, and the
/// second when the restored plan "began the leg" and cleared the latch — the trigger now waits
/// <see cref="RestoreSettle"/> for the snapshot, and a leg that begins on the plan the check
/// already ran for is not a new trigger (<see cref="LegBegan"/>);</item>
/// <item>the ADIRS datarefs had delivered nothing yet and read their fallbacks (the early-read
/// family of issue #240) — the trigger now waits until every value it speaks has arrived, at
/// most <see cref="ValuesWait"/>.</item>
/// </list>
/// Not thread-safe: the service calls it under its own lock.
/// </summary>
public sealed class CrossCheckAutoLatch
{
    /// <summary>How long after the start the snapshot's verdict is waited for — the same 15 s
    /// the notification latches wait (<c>NotificationSignalSource.RestoreSettle</c>); the
    /// restores of 2026-11-14 came 6.5 s (at the gate) and 13 s (in the climb) after the start.</summary>
    public static readonly TimeSpan RestoreSettle = TimeSpan.FromSeconds(15);

    /// <summary>How long a due check waits for values ProSim has not delivered.</summary>
    public static readonly TimeSpan ValuesWait = TimeSpan.FromSeconds(30);

    private DateTimeOffset? _startedAtUtc;
    private DateTimeOffset? _waitingForValuesSinceUtc;
    private bool _restoreApplied;

    /// <summary>The automatic check has run for this departure.</summary>
    public bool Done { get; private set; }

    /// <summary>The plan it ran for; null when none was loaded (or nothing ran).</summary>
    public string? DonePlanId { get; private set; }

    /// <summary>The module started: the restore settle counts from here.</summary>
    public void Started(DateTimeOffset nowUtc) => _startedAtUtc = nowUtc;

    /// <summary>One tick of the auto trigger. <paramref name="due"/> is the trigger's own rule
    /// (at the gate, armed, init settled — <c>FmgcCrossCheckService.AutoDue</c> with this
    /// latch's <see cref="Done"/>); <paramref name="valuesDelivered"/> says every dataref the
    /// readout speaks has a value from ProSim; <paramref name="planId"/> is the loaded plan.</summary>
    public CrossCheckAutoVerdict Judge(DateTimeOffset nowUtc, bool due, bool valuesDelivered, string? planId)
    {
        if (Done || !due)
        {
            _waitingForValuesSinceUtc = null;
            return CrossCheckAutoVerdict.NotDue;
        }

        if (!_restoreApplied && _startedAtUtc is { } started && nowUtc - started < RestoreSettle)
        {
            return CrossCheckAutoVerdict.HeldForRestore;
        }

        var complete = true;
        if (!valuesDelivered)
        {
            _waitingForValuesSinceUtc ??= nowUtc;
            if (nowUtc - _waitingForValuesSinceUtc < ValuesWait)
            {
                return CrossCheckAutoVerdict.HeldForValues;
            }

            complete = false;
        }

        _waitingForValuesSinceUtc = null;
        Done = true;
        DonePlanId = planId;
        return complete ? CrossCheckAutoVerdict.Run : CrossCheckAutoVerdict.RunWithoutAllValues;
    }

    /// <summary>A reset of the per-leg state (session reset, cycle reset, arrival completed).
    /// Returns true when the latch was set.</summary>
    public bool Reset()
    {
        var was = Done;
        Done = false;
        DonePlanId = null;
        _waitingForValuesSinceUtc = null;
        return was;
    }

    /// <summary>A leg began on <paramref name="planId"/>. A different plan than the one the
    /// check ran for is a new departure — the latch clears (true). The same plan is the app
    /// meeting its own plan again after a restart ("first plan of the session"): the check
    /// stays done (false).</summary>
    public bool LegBegan(string? planId)
    {
        if (Done && !string.IsNullOrWhiteSpace(planId) && string.Equals(planId, DonePlanId, StringComparison.Ordinal))
        {
            return false;
        }

        return Reset();
    }

    /// <summary>The state worth keeping, or null when the check has not run.</summary>
    public CrossCheckAutoSnapshot? Capture() => Done ? new CrossCheckAutoSnapshot(true, DonePlanId) : null;

    /// <summary>Takes the snapshot's state back and ends the restore settle. Returns true when
    /// the latch is now set from the snapshot.</summary>
    public bool Restore(CrossCheckAutoSnapshot? saved)
    {
        _restoreApplied = true;
        if (saved is not { Done: true } || Done)
        {
            return false;
        }

        Done = true;
        DonePlanId = saved.PlanId;
        _waitingForValuesSinceUtc = null;
        return true;
    }
}
