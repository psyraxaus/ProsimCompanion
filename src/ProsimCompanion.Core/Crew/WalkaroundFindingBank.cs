using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Crew;

/// <summary>
/// The walkaround findings on file (issue #196): <c>walkaround-findings.json</c> in the USER
/// config tree (ADR-0007), re-read on the next access after its last-write time moved — the
/// <c>PhraseBank</c> pattern. An unreadable file keeps the last good bank and surfaces on the
/// Config page; an absent file means no findings, never a throw.
/// </summary>
public sealed class WalkaroundFindingBank
{
    public const string FileName = "walkaround-findings.json";

    private readonly string _path;
    private readonly ILogger<WalkaroundFindingBank> _logger;
    private readonly ConfigProblemStore? _problems;
    private readonly object _gate = new();
    private IReadOnlyList<WalkaroundFinding> _findings = [];
    private DateTime _loadedWriteTimeUtc = DateTime.MinValue;

    /// <param name="configDirectory">Where the file lives; null = the user config root.</param>
    public WalkaroundFindingBank(ILogger<WalkaroundFindingBank> logger, ConfigProblemStore? problems = null, string? configDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _problems = problems;
        _path = Path.Combine(string.IsNullOrWhiteSpace(configDirectory) ? UserConfigPaths.Root : configDirectory, FileName);
    }

    public string FilePath => _path;

    /// <summary>The findings as loaded (reloaded on file change).</summary>
    public IReadOnlyList<WalkaroundFinding> Findings
    {
        get
        {
            lock (_gate)
            {
                ReloadIfChanged();
                return _findings;
            }
        }
    }

    private void ReloadIfChanged()
    {
        DateTime writeTime;
        try
        {
            writeTime = File.GetLastWriteTimeUtc(_path);
        }
        catch (IOException)
        {
            return;
        }

        if (writeTime == _loadedWriteTimeUtc)
        {
            return;
        }

        _loadedWriteTimeUtc = writeTime;
        if (!File.Exists(_path))
        {
            _findings = [];
            _problems?.ClearArea(ConfigAreas.Walkaround);
            _logger.LogInformation("Walkaround findings file {Path} is absent — the walkaround reports no findings", _path);
            return;
        }

        try
        {
            var result = WalkaroundFindingsFile.Parse(File.ReadAllText(_path));
            _findings = result.Findings;
            if (result.Problems.Count > 0)
            {
                _problems?.Report(ConfigAreas.Walkaround, _path, string.Join("; ", result.Problems));
                _logger.LogWarning("walkaround-findings.json: {Problems}", string.Join("; ", result.Problems));
            }
            else
            {
                _problems?.ClearArea(ConfigAreas.Walkaround);
            }

            _logger.LogInformation("walkaround-findings.json loaded ({Count} findings)", _findings.Count);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            _logger.LogWarning(ex, "walkaround-findings.json unreadable — keeping the last good bank ({Count} findings)", _findings.Count);
            _problems?.Report(ConfigAreas.Walkaround, _path, ex.Message);
        }
    }
}
