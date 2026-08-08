namespace ProsimCompanion.Core.State;

/// <summary>
/// Outcome of the one-shot startup state resync (issue #30), shared as a dumb holder so the
/// GSX automation (which must not sequence before the assessment) and the loadsheet service
/// (which restores or primes its datarefs based on it) can consume the result without a
/// circular dependency on the resync service itself. Assessment is terminal: it happens once
/// per process, whether evidence was found or not.
/// </summary>
public sealed class GsxResyncState
{
    private readonly object _gate = new();
    private bool _assessed;

    /// <summary>Raised exactly once, when the assessment completes (caller's thread).</summary>
    public event Action? Assessed;

    /// <summary>True once the startup assessment has run (or timed out with nothing to do).
    /// The departure sequencer holds until this is set.</summary>
    public bool IsAssessed
    {
        get
        {
            lock (_gate)
            {
                return _assessed;
            }
        }
    }

    /// <summary>The tracking LVARs say an arrival happened this sim session — this leg is a
    /// turnaround even though the in-memory flag was lost with the previous process.</summary>
    public bool TurnaroundDetected { get; private set; }

    /// <summary>Preliminary-loadsheet edition recovered from the tracking LVARs; 0 = none.</summary>
    public int LoadsheetPrelimEdition { get; private set; }

    /// <summary>True when the tracking LVARs say the final loadsheet was already sent.</summary>
    public bool LoadsheetFinalSent { get; private set; }

    /// <summary>Boarding completion backed by actual evidence (pax counts / EFB status /
    /// tracking LVAR) — the loadsheet restore may re-arm the automatic final on this, never on
    /// the never-re-call assumption.</summary>
    public bool BoardingProven { get; private set; }

    public void MarkAssessed(
        bool turnaroundDetected,
        int loadsheetPrelimEdition,
        bool loadsheetFinalSent,
        bool boardingProven = false)
    {
        lock (_gate)
        {
            if (_assessed)
            {
                return;
            }

            _assessed = true;
            TurnaroundDetected = turnaroundDetected;
            LoadsheetPrelimEdition = loadsheetPrelimEdition;
            LoadsheetFinalSent = loadsheetFinalSent;
            BoardingProven = boardingProven;
        }

        Assessed?.Invoke();
    }
}
