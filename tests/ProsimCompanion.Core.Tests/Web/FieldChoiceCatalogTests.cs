using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using ProsimCompanion.Core.Tests.Localization;
using ProsimCompanion.Web.Components;
using Xunit;

namespace ProsimCompanion.Core.Tests.Web;

/// <summary>The static dropdown lists behind the field descriptions (C3).</summary>
public sealed class FieldChoiceCatalogTests
{
    private static IStringLocalizer English()
    {
        var services = new ServiceCollection().AddTestLocalizer();
        return services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IStringLocalizer>();
    }

    [Fact]
    public void EveryStaticList_Resolves_WithTranslatedLabels_AndDistinctValues()
    {
        var localizer = English();
        var problems = new List<string>();
        foreach (var name in FieldChoiceCatalog.Names)
        {
            var rows = FieldChoiceCatalog.Resolve(name, localizer);
            Assert.NotNull(rows);
            if (rows!.Count == 0)
            {
                problems.Add($"{name}: empty");
            }

            if (rows.Select(r => r.Value).Distinct(StringComparer.Ordinal).Count() != rows.Count)
            {
                problems.Add($"{name}: duplicate values");
            }

            foreach (var (value, label) in rows.Where(r => r.Label.StartsWith("speechSettings.", StringComparison.Ordinal)
                                                             || r.Label.StartsWith("gsxSettings.", StringComparison.Ordinal)
                                                             || r.Label.StartsWith("audioSettings.", StringComparison.Ordinal)
                                                             || r.Label.StartsWith("appSettings.", StringComparison.Ordinal)))
            {
                problems.Add($"{name}: '{value}' shows its text id '{label}'");
            }
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    [Fact]
    public void RuntimeLists_AreKnown_ButNotResolvable()
    {
        Assert.True(FieldChoiceCatalog.IsKnown("outputDevices"));
        Assert.Null(FieldChoiceCatalog.Resolve("outputDevices", English()));
        Assert.False(FieldChoiceCatalog.IsKnown("noSuchList"));
    }

    [Fact]
    public void TheEnumLists_SpeakTheCamelCaseNames_TheDraftStores()
    {
        var localizer = English();

        Assert.Equal(["openAi", "ollama"], FieldChoiceCatalog.Resolve("llmApi", localizer)!.Select(r => r.Value));
        Assert.Equal(["fasterWhisper", "whisperCpp"], FieldChoiceCatalog.Resolve("asrApi", localizer)!.Select(r => r.Value));
        Assert.Equal(["allow", "defer", "suppress"], FieldChoiceCatalog.Resolve("sterileNormalPolicy", localizer)!.Select(r => r.Value));
        Assert.Equal(["coreAudio", "voiceMeeter"], FieldChoiceCatalog.Resolve("audioBackend", localizer)!.Select(r => r.Value));
        Assert.Equal(["left", "right"], FieldChoiceCatalog.Resolve("pilotSeat", localizer)!.Select(r => r.Value));
    }

    [Fact]
    public void TheFlapLeverList_UsesThePlacardNames()
        => Assert.Equal(["UP", "1", "1+F", "2", "3", "FULL"], FieldChoiceCatalog.Resolve("flapLever", English())!.Select(r => r.Label));
}
