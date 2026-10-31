using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Scenarios;

/// <summary>
/// The scenario catalogue on file (issue #197): <c>scenarios.json</c> in the USER config tree
/// (ADR-0007), re-read on the next access after its last-write time moved. An unreadable file
/// keeps the last good catalogue and surfaces on the Config page. The abnormal ids are checked
/// by the Speech service against the loaded abnormal definitions (this project cannot see
/// them); the failure names are checked by the gate against ProSim's live list.
/// </summary>
public sealed class ScenarioCatalogueBank
{
    public const string FileName = "scenarios.json";

    private readonly string _path;
    private readonly ILogger<ScenarioCatalogueBank> _logger;
    private readonly ConfigProblemStore? _problems;
    private readonly object _gate = new();
    private IReadOnlyList<ScenarioEntry> _entries = [];
    private DateTime _loadedWriteTimeUtc = DateTime.MinValue;

    public ScenarioCatalogueBank(ILogger<ScenarioCatalogueBank> logger, ConfigProblemStore? problems = null, string? configDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _problems = problems;
        _path = Path.Combine(string.IsNullOrWhiteSpace(configDirectory) ? UserConfigPaths.Root : configDirectory, FileName);
    }

    public string FilePath => _path;

    public IReadOnlyList<ScenarioEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                ReloadIfChanged();
                return _entries;
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
            _entries = [];
            _problems?.ClearArea(ConfigAreas.Scenarios);
            _logger.LogInformation("Scenario catalogue {Path} is absent — nothing to plan", _path);
            return;
        }

        try
        {
            var result = ScenarioCatalogueFile.Parse(File.ReadAllText(_path));
            _entries = result.Scenarios;
            if (result.Problems.Count > 0)
            {
                _problems?.Report(ConfigAreas.Scenarios, _path, string.Join("; ", result.Problems));
                _logger.LogWarning("scenarios.json: {Problems}", string.Join("; ", result.Problems));
            }
            else
            {
                _problems?.ClearArea(ConfigAreas.Scenarios);
            }

            _logger.LogInformation("scenarios.json loaded ({Count} scenarios, {Verified} verified)", _entries.Count, _entries.Count(e => e.Verified));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            _logger.LogWarning(ex, "scenarios.json unreadable — keeping the last good catalogue ({Count})", _entries.Count);
            _problems?.Report(ConfigAreas.Scenarios, _path, ex.Message);
        }
    }
}
