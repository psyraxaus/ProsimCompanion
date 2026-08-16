namespace ProsimCompanion.Core.State;

/// <summary>Well-known <see cref="ConfigProblem.Area"/> values — one per user-editable
/// content family under <c>%LOCALAPPDATA%\ProsimCompanion\config</c> (ADR-0007).</summary>
public static class ConfigAreas
{
    public const string Checklists = "checklists";
    public const string Abnormals = "abnormals";
    public const string Commands = "commands";
    public const string Phrases = "phrases";
    public const string AtcRequests = "atc-requests";
    public const string Themes = "themes";
    public const string AircraftStates = "aircraft-states";
}

/// <summary>One user-content file that failed to parse/load. <see cref="Message"/> is the
/// human-readable diagnosis (JSON parser messages carry line numbers — keep them).</summary>
public sealed record ConfigProblem(
    string Area,
    string SourceFile,
    string Message,
    DateTimeOffset TimestampUtc);

/// <summary>
/// Observable store of user-content load failures (issue #74): the 2026-08-15 flight ran
/// without the pilot's edited checklist because the parse warning lived only in the log file.
/// Loaders report failures beside their existing log calls; the web UI renders them as a
/// banner. Entries are keyed by (area, file) so a re-failing file updates in place, and a
/// loader clears its whole area on a successful (re)load so fixed files vanish from the
/// banner. <see cref="Changed"/> fires on the writer's thread — consumers marshal.
/// </summary>
public sealed class ConfigProblemStore
{
    private readonly object _gate = new();
    private readonly List<ConfigProblem> _problems = [];

    /// <summary>Raised after any change, on the caller's thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Copy of the current problems, oldest first.</summary>
    public IReadOnlyList<ConfigProblem> Snapshot()
    {
        lock (_gate)
        {
            return [.. _problems];
        }
    }

    /// <summary>Records (or refreshes) a problem for <paramref name="sourceFile"/>. Upserts by
    /// (area, file) — a file that keeps failing across hot-reloads holds one entry, not a
    /// growing pile.</summary>
    public void Report(string area, string sourceFile, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(area);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFile);
        ArgumentNullException.ThrowIfNull(message);

        lock (_gate)
        {
            _problems.RemoveAll(problem =>
                string.Equals(problem.Area, area, StringComparison.OrdinalIgnoreCase)
                && string.Equals(problem.SourceFile, sourceFile, StringComparison.OrdinalIgnoreCase));
            _problems.Add(new ConfigProblem(area, sourceFile, message, DateTimeOffset.UtcNow));
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops every problem in <paramref name="area"/> — called at the start of each
    /// (re)load so problems the user has fixed disappear. Fires <see cref="Changed"/> only
    /// when something was actually removed.</summary>
    public void ClearArea(string area)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(area);

        bool removed;
        lock (_gate)
        {
            removed = _problems.RemoveAll(problem =>
                string.Equals(problem.Area, area, StringComparison.OrdinalIgnoreCase)) > 0;
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
