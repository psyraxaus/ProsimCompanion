namespace ProsimCompanion.Core.Debrief;

/// <summary>Debrief length: Brief speaks the highlights, Full appends the activity counts.</summary>
public enum DebriefVerbosity
{
    Brief,
    Full,
}

/// <summary>One stabilized-approach gate outcome, extracted from the session event log.</summary>
public sealed record GateFact(string Name, double AglFt, string Result, string? FailingCriterion);

/// <summary>One abnormal handled in the session. This codebase's failure monitor is
/// detect-and-report (no ECAM action tracking yet), so only the title and whether the
/// condition cleared are recoverable from the log.</summary>
public sealed record AbnormalFact(string Title, bool Cleared);

/// <summary>
/// The deterministic fact set for the post-flight debrief and the logbook fold, extracted
/// from the session event log with no model involvement. Every value traces to a logged
/// event; anything the log doesn't contain stays null/empty and is simply omitted
/// (confirmed-or-omitted).
/// </summary>
public sealed record DebriefFacts(
    int? BlockMinutes,
    int? FlightMinutes,
    double? LiftoffIasKt,
    double? TouchdownGroundSpeedKt,
    IReadOnlyList<GateFact> Gates,
    int CalloutsFired,
    int SpeechSuppressed,
    int ChecklistsCompleted,
    IReadOnlyList<string> ChecklistNames,
    double? StartFobKg,
    double? FinalFobKg,
    double? FuelUsedKg,
    IReadOnlyList<string> Advisories,
    int Degradations,
    IReadOnlyList<AbnormalFact> Abnormals,
    string? Origin = null,
    string? Destination = null,
    string? DepartureRunway = null,
    string? ArrivalRunway = null,
    int CabinReports = 0,
    int DefectsRaised = 0,
    int DefectsRectified = 0,
    int DefectsCarried = 0,
    int RadioTunes = 0,
    int MemoryDrills = 0)
{
    public static DebriefFacts Empty { get; } = new(
        null, null, null, null, [], 0, 0, 0, [],
        null, null, null, [], 0, []);

    /// <summary>True when there's enough logged to be worth speaking or folding.</summary>
    public bool HasData =>
        BlockMinutes is not null || FlightMinutes is not null || Gates.Count > 0
        || CalloutsFired > 0 || ChecklistsCompleted > 0 || FinalFobKg is not null
        || Advisories.Count > 0 || Abnormals.Count > 0
        || DefectsRaised > 0 || DefectsRectified > 0;
}
