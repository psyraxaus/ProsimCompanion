using System.Linq.Expressions;
using ProsimCompanion.Core.Configuration;
using Xunit;

namespace ProsimCompanion.Core.Tests.Configuration.Fields;

/// <summary>The resolver from a page's selector expression to the property's description and
/// its read / write / default / enabled operations (C3).</summary>
public sealed class OptionFieldBindingTests
{
    private static OptionFieldBinding Bind<T>(Expression<Func<T, object?>> selector) => OptionFieldBinding.For(selector);

    [Fact]
    public void For_ResolvesTheDescription_OfATopLevelProperty()
    {
        var binding = Bind<SpeechOptions>(o => o.Volume);

        Assert.Equal(FieldKind.Number, binding.Description.Kind);
        Assert.Equal("speechSettings.general.playback.volume", binding.Description.TextId);
        Assert.Equal(typeof(int), binding.ValueType);
        Assert.Equal("%", binding.Description.Spec.Suffix);
    }

    [Fact]
    public void For_SeesThroughTheBoxingConversion_AndWalksANestedChain()
    {
        var binding = Bind<SpeechOptions>(o => o.Humanize.Tempo);
        var speech = new SpeechOptions { Humanize = { Tempo = 1.5, Enabled = false } };

        Assert.Equal("speechSettings.general.pacing.tempo", binding.Description.TextId);
        Assert.Equal(1.5, binding.Get(speech));
        Assert.Same(speech.Humanize, binding.Owner(speech));
        Assert.Equal(2, binding.OwnerChain(speech).Count);
        Assert.False(binding.IsEnabled(speech)); // DependsOn = "Enabled" on the Humanize block
    }

    [Fact]
    public void For_IsCached_PerRootTypeAndChain()
        => Assert.Same(Bind<SpeechOptions>(o => o.Volume), Bind<SpeechOptions>(o => o.Volume));

    [Fact]
    public void For_RejectsASelectorThatIsNotAPropertyChain()
    {
        Assert.Throws<ArgumentException>(() => Bind<SpeechOptions>(o => o.Volume + 1));
        Assert.Throws<ArgumentException>(() => Bind<SpeechOptions>(o => o.PttBinding.IsSet ? 1 : 0));
    }

    [Fact]
    public void For_ThrowsForAPropertyWithoutADescription()
        => Assert.Throws<InvalidOperationException>(() => Bind<SpeechOptions>(o => o.PttKey));

    [Fact]
    public void Set_CoercesDropdownText_ToEnumIntAndDouble()
    {
        var briefing = new BriefingOptions();
        var gate = new ApproachGate();
        var cabin = new CabinOptions();

        Bind<BriefingOptions>(o => o.LlmApi).Set(briefing, "ollama");
        Bind<ApproachGate>(g => g.RequireFlapHandleAtLeast).Set(gate, "4");
        Bind<CabinOptions>(o => o.CabinSecureSecondsPerPax).Set(cabin, "2.5");

        Assert.Equal(LlmApiKind.Ollama, briefing.LlmApi);
        Assert.Equal(4, gate.RequireFlapHandleAtLeast);
        Assert.Equal(2.5, cabin.CabinSecureSecondsPerPax);
    }

    [Fact]
    public void Set_WithAnUnknownEnumName_FallsBackToThePropertysOwnDefault()
    {
        // The hand-written page bridges fell back to OpenAi / FasterWhisper / Auto / Allow —
        // each the property's initialiser, not the enum's first member (Allow is the second).
        var speech = new SpeechOptions { SterileNormalPolicy = SterileNormalPolicy.Suppress, AsrApi = AsrApiKind.WhisperCpp };
        var briefing = new BriefingOptions { LlmApi = LlmApiKind.Ollama, ProcedureSource = ProcedureSourceMode.FlightJson };

        Bind<SpeechOptions>(o => o.SterileNormalPolicy).Set(speech, "loud");
        Bind<SpeechOptions>(o => o.AsrApi).Set(speech, "vosk");
        Bind<BriefingOptions>(o => o.LlmApi).Set(briefing, "gpt");
        Bind<BriefingOptions>(o => o.ProcedureSource).Set(briefing, "guess");

        Assert.Equal(SterileNormalPolicy.Allow, speech.SterileNormalPolicy);
        Assert.Equal(AsrApiKind.FasterWhisper, speech.AsrApi);
        Assert.Equal(LlmApiKind.OpenAi, briefing.LlmApi);
        Assert.Equal(ProcedureSourceMode.Auto, briefing.ProcedureSource);
    }

    [Fact]
    public void SelectText_SpeaksCamelCaseEnumNames_AndInvariantNumbers()
    {
        var briefing = new BriefingOptions { LlmApi = LlmApiKind.OpenAi };
        var gate = new ApproachGate { RequireFlapHandleAtLeast = 3 };

        Assert.Equal("openAi", Bind<BriefingOptions>(o => o.LlmApi).SelectText(briefing));
        Assert.Equal("3", Bind<ApproachGate>(g => g.RequireFlapHandleAtLeast).SelectText(gate));
    }

    [Fact]
    public void Range_ReadsAndWritesBothBounds()
    {
        var gsx = new GsxOptions();
        var binding = Bind<GsxOptions>(o => o.ChockDelayMinSec);

        Assert.Equal(FieldKind.Range, binding.Description.Kind);
        Assert.Equal(20, binding.GetRangeEnd(gsx));
        binding.SetRangeEnd(gsx, "25");
        binding.Set(gsx, 12);
        Assert.Equal(25, gsx.ChockDelayMaxSec);
        Assert.Equal(12, gsx.ChockDelayMinSec);
        Assert.Null(Bind<GsxOptions>(o => o.ArrivalStableSeconds).GetRangeEnd(gsx));
        Assert.Throws<InvalidOperationException>(() => Bind<GsxOptions>(o => o.ArrivalStableSeconds).SetRangeEnd(gsx, 1));
    }

    [Fact]
    public void DefaultOf_ReadsAFreshInstance_NeverADuplicatedValue()
    {
        Assert.Equal(100, OptionFieldBinding.DefaultOf(typeof(SpeechOptions).GetProperty(nameof(SpeechOptions.Volume))!));
        Assert.Equal(SterileNormalPolicy.Allow, OptionFieldBinding.DefaultOf(typeof(SpeechOptions).GetProperty(nameof(SpeechOptions.SterileNormalPolicy))!));
    }

    [Fact]
    public void IsEnabled_FollowsTheDeclaredRule_IncludingTheOwnersOwner()
    {
        var sayIntentions = new SayIntentionsOptions { Enabled = true, CommsHandover = { Enabled = false } };
        var gate = Bind<SayIntentionsOptions>(o => o.CommsHandover.GateToCopilot);
        var legacy = Bind<SayIntentionsOptions>(o => o.DepartureGatingEnabled);

        Assert.False(gate.IsEnabled(sayIntentions));
        Assert.True(legacy.IsEnabled(sayIntentions));

        sayIntentions.CommsHandover.Enabled = true;
        Assert.True(gate.IsEnabled(sayIntentions));
        Assert.False(legacy.IsEnabled(sayIntentions));

        sayIntentions.Enabled = false;
        Assert.False(gate.IsEnabled(sayIntentions));
    }

    [Fact]
    public void Description_ValidatesKindAgainstType_AndRangePairs()
    {
        var upper = OptionFieldCatalog.Describe(typeof(GsxOptions).GetProperty(nameof(GsxOptions.ChockDelayMaxSec))!);

        Assert.NotNull(upper);
        Assert.Equal(FieldKind.RangeUpper, upper!.Kind);
        Assert.Equal(nameof(GsxOptions.ChockDelayMinSec), upper.Spec.RangeStart);
        Assert.Null(OptionFieldCatalog.Describe(typeof(SpeechOptions).GetProperty(nameof(SpeechOptions.PttKey))!));
        Assert.Contains(OptionFieldCatalog.Describe(typeof(SpeechOptions)), d => d.Property.Name == nameof(SpeechOptions.Volume));
    }
}
