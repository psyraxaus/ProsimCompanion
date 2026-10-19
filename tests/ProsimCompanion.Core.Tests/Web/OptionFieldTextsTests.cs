using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Web.Components;
using Xunit;

namespace ProsimCompanion.Core.Tests.Web;

/// <summary>The text-id convention of a field description (C3): derived ids only where the
/// English pack has them, explicit ids over the convention, the label falling back to the root.</summary>
public sealed class OptionFieldTextsTests
{
    private static OptionFieldDescription Describe<T>(string property)
        => OptionFieldCatalog.Describe(typeof(T).GetProperty(property)!)!;

    [Fact]
    public void Resolve_DerivesLabelHintMoreAndPlaceholder_WhenThePackHasThem()
    {
        var pack = new HashSet<string> { "speechSettings.persona.name.label", "speechSettings.persona.name.hint", "speechSettings.persona.name.placeholder" };

        var ids = OptionFieldTexts.Resolve(Describe<PersonaOptions>(nameof(PersonaOptions.Name)), pack.Contains);

        Assert.Equal("speechSettings.persona.name.label", ids.Label);
        Assert.Equal("speechSettings.persona.name.hint", ids.Hint);
        Assert.Null(ids.More);
        Assert.Equal("speechSettings.persona.name.placeholder", ids.Placeholder);
        Assert.Null(ids.Tally);
    }

    [Fact]
    public void Resolve_UsesTheRootAsLabel_WhenThereIsNoLabelSuffix()
    {
        var ids = OptionFieldTexts.Resolve(Describe<SpeechOptions>(nameof(SpeechOptions.Volume)), id => id == "speechSettings.general.playback.volume");

        Assert.Equal("speechSettings.general.playback.volume", ids.Label);
        Assert.Null(ids.Hint);
    }

    [Fact]
    public void Resolve_PrefersExplicitIds_OverTheConvention()
    {
        // SterileEnabled: the shared "Enabled" label and a hint that does not sit at {root}.hint.
        var pack = new HashSet<string> { "speechSettings.common.enabled", "speechSettings.sterile.enabledHint" };

        var ids = OptionFieldTexts.Resolve(Describe<SpeechOptions>(nameof(SpeechOptions.SterileEnabled)), pack.Contains);

        Assert.Equal("speechSettings.common.enabled", ids.Label);
        Assert.Equal("speechSettings.sterile.enabledHint", ids.Hint);
    }

    [Fact]
    public void Resolve_DerivesTheTallyName_ForARepeatedLabel()
    {
        var pack = new HashSet<string> { "speechSettings.providers.kokoro.baseUrl.hint", "speechSettings.providers.kokoro.baseUrl.tally" };

        var ids = OptionFieldTexts.Resolve(Describe<SpeechOptions>(nameof(SpeechOptions.KokoroBaseUrl)), pack.Contains);

        Assert.Equal("speechSettings.common.baseUrl", ids.Label);
        Assert.Equal("speechSettings.providers.kokoro.baseUrl.tally", ids.Tally);
    }

    [Fact]
    public void Resolve_CarriesTheVoicePickerTexts()
    {
        var ids = OptionFieldTexts.Resolve(Describe<SpeechOptions>(nameof(SpeechOptions.WinRtVoice)), _ => false);

        Assert.Equal("speechSettings.common.windowsDefaultVoice", ids.BlankLabel);
        Assert.Equal("speechSettings.common.noListPressRefresh", ids.EmptyListHint);
        Assert.Equal("speechSettings.providers.windows.winrt", ids.Label);
    }
}
