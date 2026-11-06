using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Core.Day;

/// <summary>
/// Persistence for <c>daystate.json</c> so an open day survives an app restart mid-rotation —
/// the <see cref="JsonStateFile{T}"/> contract (options-overridable path; the predecessor
/// hard-coded <c>%LOCALAPPDATA%</c>; atomic temp-then-move writes; a corrupt file moved aside
/// as <c>.corrupt-&lt;timestamp&gt;.bak</c> — the unified naming, the predecessor used a one-off
/// <c>.bad-*</c> scheme here — and rebuilt, never a throw), plus two policies of its own: an
/// EMPTY file is "no day", not corruption, and every write is logged with its size.
/// </summary>
public sealed class DayStateFile : JsonStateFile<DayState>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IOptionsMonitor<DayOptions> _options;
    private readonly ILogger<DayStateFile> _logger;

    public DayStateFile(IOptionsMonitor<DayOptions> options, ILogger<DayStateFile> logger)
        : base(logger, "Day state", JsonOptions)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _logger = logger;
    }

    /// <summary>Loads the persisted day state; null when absent. A corrupt file is backed up
    /// beside itself and null is returned — the day simply starts clean. An EMPTY file is
    /// "no day", not corruption: on the owner's sim PC every start from 2026-10-03 to
    /// 2026-10-31 found a 0-byte daystate.json (nine corrupt-*.bak files, all empty, each
    /// stamped about a minute after the previous run's last log line), so the day never
    /// survived a restart and the log carried a stack trace for it. The empty file is removed
    /// and the fact logged plainly; which writer empties it is still open (issue filed
    /// 2026-10-31) — Save logs every write with its size to find out.</summary>
    public DayState? Load() => LoadOrNull();

    /// <summary>Writes the day state atomically (temp-then-move). Never throws — a failed save
    /// costs restart recovery, not the running day. Writes are serialized and each uses its
    /// own temp name, so two callers (the phase handler and the leg-facts step both save) can
    /// never truncate each other's temp file; nothing but a complete document is ever moved
    /// into place.</summary>
    public void Save(DayState day) => Write(day);

    protected override string ResolvePath()
    {
        var configured = _options.CurrentValue.Path;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProsimCompanion",
            "daystate.json");
    }

    protected override void OnEmpty(string path)
    {
        _logger.LogInformation("Day state file {Path} is empty — starting without a day (no backup taken)", path);
        TryDelete(path);
    }

    protected override bool AcceptJson(string path, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            _logger.LogWarning("Day state serialized to nothing — not written to {Path}", path);
            return false;
        }

        return true;
    }

    protected override void OnSaved(string path, string json, DayState value)
        => _logger.LogInformation("Day state saved ({Bytes} bytes, day {DayId}, {State})", json.Length, value.DayId, value.State);
}
