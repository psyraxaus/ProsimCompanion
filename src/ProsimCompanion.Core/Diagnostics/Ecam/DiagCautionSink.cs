namespace ProsimCompanion.Core.Diagnostics.Ecam;

/// <summary>
/// Where collectors raise what the STATUS page lists: cautions (with their action lines),
/// features that are inoperative because of a caution, and features running degraded. One
/// sink per tick, shared by every collector; the service de-duplicates by system + title and
/// carries the first-raised time over from the previous tick so a caution's age is real.
/// </summary>
public sealed class DiagCautionSink
{
    private readonly List<DiagCaution> _cautions = [];
    private readonly List<string> _inop = [];
    private readonly List<string> _degraded = [];
    private readonly DateTimeOffset _now;

    public DiagCautionSink(DateTimeOffset now)
    {
        _now = now;
    }

    /// <summary>The cautions raised this tick, in the order raised.</summary>
    public IReadOnlyList<DiagCaution> Cautions => _cautions;

    /// <summary>Feature names blocked by a caution ("LLM BRIEFING").</summary>
    public IReadOnlyList<string> InopSystems => _inop;

    /// <summary>Feature names running with reduced function ("SIMVAR FEED").</summary>
    public IReadOnlyList<string> DegradedSystems => _degraded;

    /// <summary>Raises a caution (amber) or warning (red) under a system heading, with the
    /// action lines shown beneath it.</summary>
    public DiagCautionSink Raise(string system, string title, DiagState severity, params DiagAction[] actions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(system);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(actions);
        if (_cautions.Any(c => c.System == system && c.Title == title))
        {
            return this;
        }

        _cautions.Add(new DiagCaution(system, title, severity, actions, _now));
        return this;
    }

    /// <summary>An informational green note (a model trial that ends on a date, say).</summary>
    public DiagCautionSink Note(string system, string title, params DiagAction[] actions)
        => Raise(system, title, DiagState.Normal, actions);

    /// <summary>Lists a feature under INOP SYS (once).</summary>
    public DiagCautionSink Inop(string featureName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureName);
        if (!_inop.Contains(featureName, StringComparer.Ordinal))
        {
            _inop.Add(featureName);
        }

        return this;
    }

    /// <summary>Lists a feature under DEGRADED (once).</summary>
    public DiagCautionSink Degraded(string featureName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureName);
        if (!_degraded.Contains(featureName, StringComparer.Ordinal))
        {
            _degraded.Add(featureName);
        }

        return this;
    }

    /// <summary>Alias for <see cref="Degraded(string)"/> for callers that read better as a verb.</summary>
    public DiagCautionSink Degrade(string featureName) => Degraded(featureName);
}
