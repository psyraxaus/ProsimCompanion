using System.Globalization;
using ProsimCompanion.Core.Debrief;

namespace ProsimCompanion.Core.Day;

/// <summary>Result of applying a completed leg's extracted facts: the filled leg, plus the
/// planned/actual pair when the destination deviated from a planned rotation (null/null
/// otherwise).</summary>
public sealed record LegFactsOutcome(DayLeg Leg, string? DeviationPlanned, string? DeviationActual);

/// <summary>
/// The pure day-mode state machine: every transition of the duty day
/// (<c>OnLeg → Turnaround → OnLeg → … → Ended</c>) as plain methods over <see cref="DayState"/>,
/// with the clock passed in and no I/O, timers, locking or speech — the companion service in
/// the Speech pillar owns those. This is the "extract a ProcessTick-style core" convention:
/// the whole multi-leg flow is testable by calling methods in sequence.
/// </summary>
public sealed class CompanyDayEngine
{
    /// <summary>The tracked day; null before the first start (or when no state was persisted).</summary>
    public DayState? Day { get; private set; }

    /// <summary>Adopts previously persisted state (an open day resumes across restarts).</summary>
    public void Resume(DayState? day) => Day = day;

    /// <summary>Starts a new day. False when a day is already open. With a rotation the day is
    /// <see cref="DayMode.Planned"/> and its legs are pre-filled; otherwise a single
    /// progressive leg is opened.</summary>
    public bool StartDay(RotationFile? rotation, int postFlightAllowanceMinutes, DateTimeOffset nowUtc)
    {
        if (Day is { IsOpen: true })
        {
            return false;
        }

        var nowIso = Iso(nowUtc);
        var day = new DayState
        {
            DayId = !string.IsNullOrWhiteSpace(rotation?.DayId)
                ? rotation!.DayId!
                : "day-" + nowUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture),
            StartedUtc = nowIso,
            State = DayPhase.OnLeg,
            Mode = rotation is not null ? DayMode.Planned : DayMode.Progressive,
            ReportTimeUtc = rotation?.ReportTimeUtc,
            PostFlightAllowanceMin = Math.Max(0, postFlightAllowanceMinutes),
            CurrentLegIndex = 1,
        };
        day.DutyStartUtc = day.ReportTimeUtc ?? nowIso;

        if (rotation is not null)
        {
            for (var i = 0; i < rotation.Legs.Count; i++)
            {
                var planned = rotation.Legs[i];
                day.Legs.Add(new DayLeg
                {
                    Index = i + 1,
                    From = planned.From,
                    To = planned.To,
                    FlightNo = planned.FlightNo,
                    ScheduledOffUtc = planned.ScheduledOffUtc,
                    ScheduledOnUtc = planned.ScheduledOnUtc,
                });
            }
        }

        if (day.Legs.Count == 0)
        {
            day.Legs.Add(new DayLeg { Index = 1 });
        }

        Day = day;
        return true;
    }

    /// <summary>Stamps the current leg's actual off-blocks on the first
    /// PushbackAndStart/TaxiOut edge. False when there is nothing to stamp (no open day, or
    /// already stamped).</summary>
    public bool StampOffBlocks(DateTimeOffset nowUtc)
    {
        if (Day is not { IsOpen: true } day || day.Current is not { ActualOffUtc: null } leg)
        {
            return false;
        }

        leg.ActualOffUtc = Iso(nowUtc);
        return true;
    }

    /// <summary>Shutdown while on a leg: stamps actual on-blocks and the leg's session id,
    /// then enters the turnaround. Returns the completed leg's index, or null when not on a
    /// leg. The leg's facts (block, route, approach…) are filled later by
    /// <see cref="ApplyLegFacts"/>, once the session-finalization step has the session log.</summary>
    public int? CompleteLeg(string? sessionId, DateTimeOffset nowUtc)
    {
        if (Day is not { IsOpen: true, State: DayPhase.OnLeg } day)
        {
            return null;
        }

        if (day.Current is { } leg)
        {
            leg.ActualOnUtc = Iso(nowUtc);
            leg.SessionId = sessionId;
        }

        day.State = DayPhase.Turnaround;
        return day.CurrentLegIndex;
    }

    /// <summary>Preflight while in a turnaround: advances to the next leg (appending one when
    /// the rotation ran out, chaining From = previous To) and returns it; null when the day is
    /// not in a turnaround. The caller rotates the event-log session BEFORE calling this.</summary>
    public DayLeg? StartNextLeg()
    {
        if (Day is not { IsOpen: true, State: DayPhase.Turnaround } day)
        {
            return null;
        }

        var next = day.CurrentLegIndex + 1;
        day.CurrentLegIndex = next;
        if (day.Legs.All(l => l.Index != next))
        {
            day.Legs.Add(new DayLeg
            {
                Index = next,
                From = day.Legs.FirstOrDefault(l => l.Index == next - 1)?.To,
            });
        }

        day.State = DayPhase.OnLeg;
        return day.Current;
    }

    /// <summary>
    /// Fills a completed leg's facts from its session log extraction, keyed by session id so a
    /// late or replayed finalization can never write into the wrong leg. Route follows reality
    /// (extracted origin/destination win); in planned mode a destination mismatch records the
    /// deviation once and then follows reality too. Stabilized uses the ONE logbook-aligned
    /// rule: any unstable gate → false, else any stable gate → true, none judged → null.
    /// </summary>
    public LegFactsOutcome? ApplyLegFacts(string sessionId, DebriefFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var leg = Day?.Legs.FirstOrDefault(
            l => string.Equals(l.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
        if (leg is null)
        {
            return null;
        }

        leg.BlockMinutes = facts.BlockMinutes;
        leg.FlightMinutes = facts.FlightMinutes;
        leg.Landed = facts.TouchdownGroundSpeedKt is not null || facts.FlightMinutes is not null;
        leg.Abnormals = facts.Abnormals.Count;
        leg.MemoryDrills = facts.MemoryDrills;
        leg.DefectsRaised = facts.DefectsRaised;
        leg.DefectsRectified = facts.DefectsRectified;
        leg.Stabilized = OverallStabilized(facts);

        if (!string.IsNullOrWhiteSpace(facts.Origin))
        {
            leg.From = facts.Origin;
        }

        string? deviationPlanned = null;
        string? deviationActual = null;
        if (!string.IsNullOrWhiteSpace(facts.Destination))
        {
            var actual = facts.Destination!;
            if (Day!.Mode == DayMode.Planned
                && !string.IsNullOrEmpty(leg.To)
                && leg.Deviation is null
                && !string.Equals(leg.To, actual, StringComparison.OrdinalIgnoreCase))
            {
                deviationPlanned = leg.To;
                deviationActual = actual;
                leg.Deviation = $"planned {leg.To}, flew {actual}";
            }

            leg.To = actual; // follow reality
        }

        return new LegFactsOutcome(leg, deviationPlanned, deviationActual);
    }

    /// <summary>Closes the day and returns it (for the summary/logbook fold); null when no day
    /// is open.</summary>
    public DayState? EndDay()
    {
        if (Day is not { IsOpen: true } day)
        {
            return null;
        }

        day.State = DayPhase.Ended;
        return day;
    }

    /// <summary>The derived view for the given moment; null when no day exists.</summary>
    public DayView? BuildView(DateTimeOffset nowUtc)
        => Day is null ? null : DayMath.BuildView(Day, nowUtc);

    /// <summary>The one line the debrief appends for day context: "Leg 2 of 4 complete." in a
    /// planned multi-leg day, "Leg 2 complete." otherwise; null when no day is open.</summary>
    public string? DebriefContextLine
        => Day is { IsOpen: true } day
            ? day.Mode == DayMode.Planned && day.Legs.Count > 1
                ? $"Leg {day.CurrentLegIndex} of {day.Legs.Count} complete."
                : $"Leg {day.CurrentLegIndex} complete."
            : null;

    /// <summary>The full snapshot the <see cref="DayStatusStore"/> publishes to the web page.</summary>
    public DaySnapshot BuildSnapshot(DateTimeOffset nowUtc)
    {
        if (Day is not { } day)
        {
            return DaySnapshot.Empty;
        }

        var legs = day.Legs
            .OrderBy(l => l.Index)
            .Select(l => new DayLegView(
                l.Index, l.From, l.To, l.FlightNo, l.ScheduledOffUtc, l.ScheduledOnUtc,
                l.ActualOffUtc, l.ActualOnUtc, l.BlockMinutes, l.Landed, l.Stabilized, l.Deviation))
            .ToList();
        return new DaySnapshot(DayMath.BuildView(day, nowUtc), legs, DebriefContextLine);
    }

    private static string Iso(DateTimeOffset t) => t.ToString("O", CultureInfo.InvariantCulture);

    private static bool? OverallStabilized(DebriefFacts facts)
    {
        if (facts.Gates.Any(g => string.Equals(g.Result, "unstable", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (facts.Gates.Any(g => string.Equals(g.Result, "stable", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return null;
    }
}
