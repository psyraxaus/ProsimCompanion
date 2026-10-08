namespace ProsimCompanion.Core.State;

/// <summary>One utterance the FO heard that nothing acted on (issue #112).</summary>
/// <param name="HeardAtUtc">When the recognizer delivered it.</param>
/// <param name="Text">The raw transcription, as heard.</param>
/// <param name="Normalized">The grouping key: the Speech pillar's command normalization
/// (lower-case, punctuation stripped) so "Set the parking brake." and "set the parking brake"
/// count as one phrase.</param>
/// <param name="Score">The interpreter's best phrase score (0 when nothing scored).</param>
/// <param name="Context">What the FO was doing: "idle", "checklist: &lt;item&gt;", "checklist-hold"
/// or "confirm-declined".</param>
/// <param name="Phase">The flight phase at the time ("Unknown" without a phase source).</param>
/// <param name="Reason">Why it fell through: "reject", "no-handler", "not-an-answer",
/// "confirm-declined" or "engine-reject".</param>
/// <param name="Suppressed">True when the miss was absorbed silently by sterile-phase
/// suppression. Counted, but excluded from the command candidates — the pilot was making
/// SOP callouts, not commands.</param>
public sealed record UnmatchedUtterance(
    DateTimeOffset HeardAtUtc,
    string Text,
    string Normalized,
    double Score,
    string Context,
    string Phase,
    string Reason,
    bool Suppressed);

/// <summary>One command candidate: a phrase heard more than once, with where it was heard.</summary>
public sealed record UnmatchedPhraseGroup(
    string Normalized,
    string Example,
    int Count,
    DateTimeOffset LastHeardUtc,
    IReadOnlyList<string> Contexts);

/// <summary>The store's view: the recent misses (newest first, bounded) and the session totals.</summary>
public sealed record UnmatchedUtteranceSnapshot(
    IReadOnlyList<UnmatchedUtterance> Recent,
    int Total,
    int SuppressedTotal)
{
    public static UnmatchedUtteranceSnapshot Empty { get; } = new([], 0, 0);

    /// <summary>The candidates: non-suppressed misses grouped by normalized text, most
    /// frequent first, then most recent — the list the Voice page shows and the post-flight
    /// pass reads (3+ of one phrase in a flight is the actionable signal).</summary>
    public IReadOnlyList<UnmatchedPhraseGroup> Groups()
        => [.. Recent
            .Where(u => !u.Suppressed)
            .GroupBy(u => u.Normalized, StringComparer.Ordinal)
            .Select(g =>
            {
                var newest = g.OrderByDescending(u => u.HeardAtUtc).First();
                return new UnmatchedPhraseGroup(
                    g.Key,
                    newest.Text,
                    g.Count(),
                    newest.HeardAtUtc,
                    [.. g.Select(u => u.Context).Distinct(StringComparer.Ordinal)]);
            })
            .OrderByDescending(g => g.Count)
            .ThenByDescending(g => g.LastHeardUtc)];
}

/// <summary>
/// Everything the FO heard this app session that no command, feature, checklist answer or
/// question handler consumed (issue #112). Written by the Speech pillar's utterance router
/// through its tracker; read by the Voice page's "Heard but not understood" section. Bounded
/// so a chatty cockpit over a long day cannot grow it without limit — the session event log
/// keeps the full history.
/// </summary>
public sealed class UnmatchedUtteranceStore : SnapshotStore<UnmatchedUtteranceSnapshot>
{
    /// <summary>Misses kept in memory; older ones fall off the page but stay in the session log.</summary>
    public const int Capacity = 300;

    public UnmatchedUtteranceStore()
        : base(UnmatchedUtteranceSnapshot.Empty)
    {
    }

    /// <summary>Records one miss (newest first; the list is trimmed to <see cref="Capacity"/>).</summary>
    public void Record(UnmatchedUtterance utterance)
    {
        ArgumentNullException.ThrowIfNull(utterance);
        Update(s => s with
        {
            Recent = [utterance, .. s.Recent.Take(Capacity - 1)],
            Total = s.Total + 1,
            SuppressedTotal = s.SuppressedTotal + (utterance.Suppressed ? 1 : 0),
        });
    }

    /// <summary>The page's "Clear" — forgets the list and the counts.</summary>
    public void Clear() => Update(_ => UnmatchedUtteranceSnapshot.Empty);
}
