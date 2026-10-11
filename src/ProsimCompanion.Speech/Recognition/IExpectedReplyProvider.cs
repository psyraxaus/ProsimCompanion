namespace ProsimCompanion.Speech.Recognition;

/// <summary>
/// What the FO is waiting to hear right now (issue #172): the candidate of a pending
/// "did you mean …?" and the open checklist item's accepted phrases / expected response. The
/// self-echo guard exempts these from its containment test — on 2026-10-10 the pilot's
/// "Flight directors off." 4 s after the FO's "did you mean flight directors off?" was dropped
/// as the FO's own voice, and "as required." after "Chocks, as required" skipped the item.
/// The router writes (it owns both dialogues); the recognizer reads at transcript time.
/// </summary>
public interface IExpectedReplyProvider
{
    /// <summary>The replies expected now, as said (not normalized). Empty when nothing is pending.</summary>
    IReadOnlyList<string> ExpectedReplies { get; }
}

/// <summary>The one registered <see cref="IExpectedReplyProvider"/>: a clarifier candidate
/// the router sets and clears around its confirm dialogue, plus a lazy source for the
/// awaiting checklist item (read on demand, so it is always the item open at that instant).
/// Thread-safe; every member tolerates a null or throwing source.</summary>
public sealed class ExpectedReplyStore : IExpectedReplyProvider
{
    private readonly object _gate = new();
    private string? _clarifierCandidate;
    private Func<IEnumerable<string>>? _awaitingSource;

    /// <summary>The "did you mean …?" candidate while that dialogue listens; null clears it.</summary>
    public void SetClarifier(string? candidate)
    {
        lock (_gate)
        {
            _clarifierCandidate = string.IsNullOrWhiteSpace(candidate) ? null : candidate;
        }
    }

    /// <summary>Registers where the open checklist item's phrases come from (the router's
    /// host). Called once at attach; a later call replaces the source.</summary>
    public void RegisterAwaitingSource(Func<IEnumerable<string>> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            _awaitingSource = source;
        }
    }

    public IReadOnlyList<string> ExpectedReplies
    {
        get
        {
            string? candidate;
            Func<IEnumerable<string>>? source;
            lock (_gate)
            {
                candidate = _clarifierCandidate;
                source = _awaitingSource;
            }

            var list = new List<string>();
            if (candidate is not null)
            {
                list.Add(candidate);
            }

            if (source is not null)
            {
                try
                {
                    list.AddRange(source().Where(p => !string.IsNullOrWhiteSpace(p)));
                }
                catch (Exception)
                {
                    // The host is mid-transition; the guard simply gets no exemptions this time.
                }
            }

            return list;
        }
    }
}
