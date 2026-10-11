using ProsimCompanion.Core.Configuration;
using Xunit;

namespace ProsimCompanion.Core.Tests.Configuration;

/// <summary>Owner rule 2026-08-14 at the property level for the Comms hand-over card (issue
/// #171): every <see cref="CommsHandoverOptions"/> property is bound on the SayIntentions
/// settings page, and the master is off by default.</summary>
public sealed class CommsHandoverOptionsGuardTests
{
    [Fact]
    public void MasterIsOffByDefault_EveryStepOnByDefault()
    {
        var options = new CommsHandoverOptions();

        Assert.False(options.Enabled);
        Assert.True(options.GateToCopilot);
        Assert.True(options.HoldingPointToCaptain);
        Assert.Equal(0.3, options.HoldingPointDistanceNm);
        Assert.True(options.ClimbToCopilot);
        Assert.True(options.ApproachToCaptain);
        Assert.True(options.TaxiInToCopilot);
        Assert.True(options.VoiceOverride);
        Assert.False(new SayIntentionsOptions().CommsHandover.Enabled);
    }

    [Fact]
    public void EveryProperty_HasAControlOnTheSpeechSettingsPage()
    {
        var razor = File.ReadAllText(Path.Combine(RepoRoot(), "src", "ProsimCompanion.Web", "Pages", "SpeechSettings.razor"));
        var unbound = typeof(CommsHandoverOptions).GetProperties()
            .Select(p => p.Name)
            .Where(name => !razor.Contains($"CommsHandover.{name}", StringComparison.Ordinal))
            .ToList();

        Assert.True(unbound.Count == 0, "Comms hand-over options without a web control: " + string.Join(", ", unbound));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProsimCompanion.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("ProsimCompanion.slnx not found above " + AppContext.BaseDirectory);
    }
}
