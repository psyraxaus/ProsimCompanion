using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Core.Persona;

/// <summary>
/// Persistence for <c>fo-roster-state.json</c> (issue #194) so the drawn First Officer
/// survives an app restart mid-day — the <see cref="JsonStateFile{T}"/> contract: atomic
/// temp-then-move writes, an empty file is "no draw", a corrupt file is moved aside as
/// <c>.corrupt-&lt;timestamp&gt;.bak</c> and the roster starts clean, never a throw. Lives
/// beside <c>daystate.json</c> under the app's data root, NOT in <c>settings.json</c>: a
/// settings page saves its whole section, and a draw made after the page loaded would be
/// written back stale by the next Save.
/// </summary>
public sealed class FoRosterStateFile : JsonStateFile<FoRosterState>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;

    /// <param name="path">The file; null = <c>%LOCALAPPDATA%\ProsimCompanion\fo-roster-state.json</c>.</param>
    public FoRosterStateFile(ILogger<FoRosterStateFile> logger, string? path = null)
        : base(logger, "FO roster state", JsonOptions)
    {
        _path = string.IsNullOrWhiteSpace(path) ? Path.Combine(UserDataPaths.Root, "fo-roster-state.json") : path;
    }

    protected override string ResolvePath() => _path;

    /// <summary>The persisted state, or an empty one when the file is absent, empty or corrupt.</summary>
    public FoRosterState Load() => LoadOrNull() ?? new FoRosterState();

    /// <summary>Writes the state atomically. Never throws — a failed save costs restart
    /// recovery, not the running day.</summary>
    public void Save(FoRosterState state) => Write(state);
}
