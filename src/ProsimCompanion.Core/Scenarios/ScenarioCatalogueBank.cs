using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Scenarios;

/// <summary>
/// The scenario catalogue on file (issue #197): <c>scenarios.json</c> in the USER config tree
/// (ADR-0007), on the <see cref="UserConfigFileBank{T}"/> contract — re-read on the next access
/// after its last-write time moved, an unreadable file keeps the last good catalogue and
/// surfaces on the Config page. The abnormal ids are checked by the Speech service against the
/// loaded abnormal definitions (this project cannot see them); the failure names are checked
/// by the gate against ProSim's live list.
/// </summary>
public sealed class ScenarioCatalogueBank : UserConfigFileBank<IReadOnlyList<ScenarioEntry>>
{
    public const string FileName = "scenarios.json";

    private readonly ILogger<ScenarioCatalogueBank> _logger;

    public ScenarioCatalogueBank(ILogger<ScenarioCatalogueBank> logger, ConfigProblemStore? problems = null, string? configDirectory = null)
        : base(Path.Combine(string.IsNullOrWhiteSpace(configDirectory) ? UserConfigPaths.Root : configDirectory, FileName), ConfigAreas.Scenarios, logger, problems, [])
    {
        _logger = logger;
    }

    /// <summary>The catalogue as loaded (reloaded on file change).</summary>
    public IReadOnlyList<ScenarioEntry> Entries => Current;

    protected override (IReadOnlyList<ScenarioEntry> Value, IReadOnlyList<string> Problems) Parse(string json)
    {
        var result = ScenarioCatalogueFile.Parse(json);
        return (result.Scenarios, result.Problems);
    }

    protected override void OnAbsent()
        => _logger.LogInformation("Scenario catalogue {Path} is absent — nothing to plan", FilePath);

    protected override void OnLoaded(IReadOnlyList<ScenarioEntry> value)
        => _logger.LogInformation("scenarios.json loaded ({Count} scenarios, {Verified} verified)", value.Count, value.Count(e => e.Verified));
}
