using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Crew;

/// <summary>
/// The walkaround findings on file (issue #196): <c>walkaround-findings.json</c> in the USER
/// config tree (ADR-0007), on the <see cref="UserConfigFileBank{T}"/> contract — re-read on the
/// next access after its last-write time moved, an unreadable file keeps the last good bank and
/// surfaces on the Config page, an absent file means no findings, never a throw.
/// </summary>
public sealed class WalkaroundFindingBank : UserConfigFileBank<IReadOnlyList<WalkaroundFinding>>
{
    public const string FileName = "walkaround-findings.json";

    private readonly ILogger<WalkaroundFindingBank> _logger;

    /// <param name="configDirectory">Where the file lives; null = the user config root.</param>
    public WalkaroundFindingBank(ILogger<WalkaroundFindingBank> logger, ConfigProblemStore? problems = null, string? configDirectory = null)
        : base(Path.Combine(string.IsNullOrWhiteSpace(configDirectory) ? UserConfigPaths.Root : configDirectory, FileName), ConfigAreas.Walkaround, logger, problems, [])
    {
        _logger = logger;
    }

    /// <summary>The findings as loaded (reloaded on file change).</summary>
    public IReadOnlyList<WalkaroundFinding> Findings => Current;

    protected override (IReadOnlyList<WalkaroundFinding> Value, IReadOnlyList<string> Problems) Parse(string json)
    {
        var result = WalkaroundFindingsFile.Parse(json);
        return (result.Findings, result.Problems);
    }

    protected override void OnAbsent()
        => _logger.LogInformation("Walkaround findings file {Path} is absent — the walkaround reports no findings", FilePath);

    protected override void OnLoaded(IReadOnlyList<WalkaroundFinding> value)
        => _logger.LogInformation("walkaround-findings.json loaded ({Count} findings)", value.Count);
}
