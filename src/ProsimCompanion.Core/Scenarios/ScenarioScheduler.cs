using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Core.Scenarios;

/// <summary>What the planner needs besides the catalogue and the options.</summary>
/// <param name="RtoEnabled"><c>abnormals.rtoEnabled</c> — the RTO-cause takeoff scenario needs it.</param>
/// <param name="EfatoEnabled"><c>abnormals.efatoEnabled</c> — the EFATO takeoff scenario needs it.</param>
/// <param name="OpenWearTitles">Open tech-log items' titles: a scenario whose <c>wearTitle</c> is open weighs double.</param>
/// <param name="EnrouteMinutes">The planned enroute time, to cap a cruise fire time; null = the default cap.</param>
public sealed record ScenarioPlanInputs(
    bool RtoEnabled,
    bool EfatoEnabled,
    IReadOnlyCollection<string> OpenWearTitles,
    double? EnrouteMinutes);

/// <summary>One evaluation tick's inputs for an existing plan.</summary>
/// <param name="Live">The flight-live gate.</param>
/// <param name="PhaseSinceUtc">When the current phase was committed.</param>
/// <param name="AltitudeAglFt">Height above ground; null while unknown.</param>
/// <param name="AbnormalOpen">An ECAM procedure is latched as detected.</param>
/// <param name="DialogueActive">A drill or an ECAM dialogue runs.</param>
/// <param name="NoGoDefectOpen">The tech log holds an open category-A (no-go) item.</param>
/// <param name="Sterile">The sterile rule's verdict.</param>
public sealed record ScenarioTick(
    FlightPhase Phase,
    bool Live,
    DateTimeOffset NowUtc,
    DateTimeOffset? PhaseSinceUtc,
    double? AltitudeAglFt,
    bool AbnormalOpen,
    bool DialogueActive,
    bool NoGoDefectOpen,
    bool Sterile);

/// <summary>What the tick decided: arm now, or hold with the reason (for the log, once).</summary>
public sealed record ScenarioDecision(bool Arm, string? HoldReason)
{
    public static ScenarioDecision ArmNow { get; } = new(true, null);

    public static ScenarioDecision Hold(string reason) => new(false, reason);

    public static ScenarioDecision Wait { get; } = new(false, null);
}

/// <summary>
/// The pure scenario scheduler (issue #197): at the first live Preflight of a flight cycle
/// <see cref="Plan"/> rolls the per-flight chance and picks one allowed entry by weight with a
/// phase and a moment inside it; every tick <see cref="Evaluate"/> says whether the planned
/// failure may be armed now — the phase is current, the moment has come, and the safety
/// floor holds. The tech-log history and the roster are inputs to the weight and the wording
/// only; the dice decide what fails. The shell owns the clock, the stores, the injector.
/// </summary>
public static class ScenarioScheduler
{
    /// <summary>A cruise fire time is drawn inside this many minutes when the plan has no enroute time.</summary>
    public const double DefaultCruiseWindowMinutes = 60;

    /// <summary>The entries the options allow right now, with their effective weights.</summary>
    public static IReadOnlyList<(ScenarioEntry Entry, double Weight)> Allowed(
        IReadOnlyList<ScenarioEntry> catalogue, ScenarioOptions options, ScenarioPlanInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(inputs);
        var result = new List<(ScenarioEntry, double)>();
        foreach (var entry in catalogue)
        {
            if (!entry.Verified || entry.Weight <= 0 || entry.Phases.Count == 0)
            {
                continue;
            }

            if (entry.Severity == ScenarioSeverity.Major && !options.AllowMajor)
            {
                continue;
            }

            if (entry.Takeoff)
            {
                if (!options.TakeoffScenarios)
                {
                    continue;
                }

                var isEfato = entry.AbnormalId.Contains("after-v1", StringComparison.OrdinalIgnoreCase)
                    || entry.Id.Contains("efato", StringComparison.OrdinalIgnoreCase);
                if (isEfato ? !inputs.EfatoEnabled : !inputs.RtoEnabled)
                {
                    continue;
                }
            }

            var weight = entry.Weight;
            if (entry.WearTitle is { } wear && inputs.OpenWearTitles.Any(t => string.Equals(t, wear, StringComparison.OrdinalIgnoreCase)))
            {
                weight *= 2; // "GEN 1 was flagged last week" — history weighs, never selects
            }

            result.Add((entry, weight));
        }

        return result;
    }

    /// <summary>Rolls the chance and picks the flight's scenario; null for a quiet flight.
    /// <paramref name="roll"/> is the dice (0–1): the first roll is the chance, the second the
    /// weighted pick, the third the phase, the fourth the moment inside it.</summary>
    public static ScenarioPlan? Plan(
        IReadOnlyList<ScenarioEntry> catalogue, ScenarioOptions options, ScenarioPlanInputs inputs,
        DateTimeOffset nowUtc, string? sessionId, Func<double> roll)
    {
        ArgumentNullException.ThrowIfNull(roll);
        if (!options.Enabled || roll() >= Math.Clamp(options.PerFlightChance, 0, 1))
        {
            return null;
        }

        var allowed = Allowed(catalogue, options, inputs);
        if (allowed.Count == 0)
        {
            return null;
        }

        var total = allowed.Sum(a => a.Weight);
        var point = Math.Clamp(roll(), 0, 0.999999) * total;
        var picked = allowed[^1].Entry;
        foreach (var (entry, weight) in allowed)
        {
            point -= weight;
            if (point < 0)
            {
                picked = entry;
                break;
            }
        }

        var phase = picked.Phases[Math.Min(picked.Phases.Count - 1, (int)(Math.Clamp(roll(), 0, 0.999999) * picked.Phases.Count))];
        var fraction = 0.2 + 0.6 * Math.Clamp(roll(), 0, 1);
        var windowSeconds = phase switch
        {
            FlightPhase.Cruise => Math.Max(5, (inputs.EnrouteMinutes ?? DefaultCruiseWindowMinutes) - 10) * 60,
            FlightPhase.Climb or FlightPhase.Descent => 12 * 60,
            FlightPhase.TaxiIn or FlightPhase.TaxiOut => 3 * 60,
            _ => 60,
        };

        return new ScenarioPlan
        {
            ScenarioId = picked.Id,
            Title = picked.Title,
            AbnormalId = picked.AbnormalId,
            FailureName = picked.FailureName,
            Phase = phase,
            FireAfterSeconds = picked.Takeoff ? 0 : (int)Math.Round(fraction * windowSeconds),
            Takeoff = picked.Takeoff,
            ClearAfterSeconds = picked.ClearAfterSeconds,
            TechLogAfter = picked.TechLogAfter,
            Decision = picked.Decision,
            PlannedAtUtc = nowUtc,
            State = ScenarioPlanState.Planned,
            SessionId = sessionId,
        };
    }

    /// <summary>Whether the planned failure may be armed now. A takeoff scenario arms on the
    /// ground before the roll (the SDK's own after-event condition does the timing); every
    /// other one arms in its phase, after its moment, above the floor, with nothing open.</summary>
    public static ScenarioDecision Evaluate(ScenarioPlan plan, ScenarioTick tick, ScenarioOptions options)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(tick);
        ArgumentNullException.ThrowIfNull(options);

        if (plan.State != ScenarioPlanState.Planned)
        {
            return ScenarioDecision.Wait;
        }

        if (!options.Enabled)
        {
            return ScenarioDecision.Hold("scenario mode is off");
        }

        if (!tick.Live)
        {
            return ScenarioDecision.Wait;
        }

        if (plan.Takeoff)
        {
            if (tick.Phase is not (FlightPhase.TaxiOut or FlightPhase.Departure or FlightPhase.PushbackAndStart))
            {
                return ScenarioDecision.Wait;
            }
        }
        else
        {
            if (tick.Phase != plan.Phase)
            {
                return ScenarioDecision.Wait;
            }

            if (tick.PhaseSinceUtc is not { } since || (tick.NowUtc - since).TotalSeconds < plan.FireAfterSeconds)
            {
                return ScenarioDecision.Wait;
            }

            var airborne = tick.Phase is FlightPhase.InitialClimb or FlightPhase.Climb or FlightPhase.Cruise or FlightPhase.Descent or FlightPhase.Approach;
            if (airborne && (tick.AltitudeAglFt is not { } agl || agl < options.MinAltitudeFt))
            {
                return ScenarioDecision.Hold($"below the {options.MinAltitudeFt} ft floor");
            }
        }

        if (tick.AbnormalOpen)
        {
            return ScenarioDecision.Hold("an abnormal is open");
        }

        if (tick.DialogueActive)
        {
            return ScenarioDecision.Hold("a drill or ECAM dialogue is running");
        }

        if (tick.NoGoDefectOpen)
        {
            return ScenarioDecision.Hold("the tech log holds a no-go item");
        }

        if (tick.Sterile)
        {
            return ScenarioDecision.Hold("sterile cockpit");
        }

        return ScenarioDecision.ArmNow;
    }

    /// <summary>The FO's decision line from the entry's template, with the airports filled in.</summary>
    public static string DecisionLine(string template, string? alternate, string? destination)
        => (string.IsNullOrWhiteSpace(template) ? "We have the ECAM actions done — I'd suggest we continue; your call, captain." : template)
            .Replace("{alternate}", string.IsNullOrWhiteSpace(alternate) ? "the alternate" : alternate, StringComparison.Ordinal)
            .Replace("{destination}", string.IsNullOrWhiteSpace(destination) ? "the destination" : destination, StringComparison.Ordinal);
}
