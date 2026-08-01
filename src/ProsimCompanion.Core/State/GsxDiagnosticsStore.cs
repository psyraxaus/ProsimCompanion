namespace ProsimCompanion.Core.State;

public sealed record GsxServiceView(
    string Id,
    string DisplayName,
    string? SemanticState,
    string MappedState,
    bool CanTrigger,
    bool Waiting,
    string? ProgressText);

public sealed record GsxCommandView(
    DateTimeOffset Timestamp,
    string Verb,
    string Args,
    bool Ok,
    string Code);

public sealed record GsxDiagnosticsSnapshot(
    string Readiness,
    IReadOnlyList<string> Capabilities,
    string? AirportIcao,
    string? GateContextKey,
    string? StartupSid,
    bool MenuShown,
    string? MenuTitle,
    IReadOnlyList<string> MenuEntries,
    IReadOnlyList<GsxServiceView> Services,
    IReadOnlyList<GsxCommandView> RecentCommands)
{
    public static GsxDiagnosticsSnapshot Empty { get; } =
        new("Disconnected", [], null, null, null, false, null, [], [], []);
}

/// <summary>
/// Live GSX diagnostics for the web UI — the browser-side twin of the wire trace, built for
/// evaluating sim smoke tests at a glance. The GSX layer pushes updates; readers poll
/// <see cref="Snapshot"/>. Kept in Core so the Web project (which references only Core) can
/// render it.
/// </summary>
public sealed class GsxDiagnosticsStore
{
    public const int RecentCommandLimit = 25;

    private readonly object _gate = new();
    private readonly Queue<GsxCommandView> _commands = new();
    private GsxDiagnosticsSnapshot _current = GsxDiagnosticsSnapshot.Empty;

    /// <summary>Point-in-time diagnostics view (recent commands newest-first).</summary>
    public GsxDiagnosticsSnapshot Snapshot()
    {
        lock (_gate)
        {
            return _current with { RecentCommands = [.. _commands.Reverse()] };
        }
    }

    /// <summary>Replaces the connection/mirror-derived portion of the view.</summary>
    public void Update(GsxDiagnosticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            _current = snapshot;
        }
    }

    /// <summary>Appends a command outcome to the bounded recent-commands ring.</summary>
    public void RecordCommand(GsxCommandView command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_gate)
        {
            _commands.Enqueue(command);
            while (_commands.Count > RecentCommandLimit)
            {
                _ = _commands.Dequeue();
            }
        }
    }
}
