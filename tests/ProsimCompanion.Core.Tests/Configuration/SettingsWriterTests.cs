using ProsimCompanion.Core.Configuration;
using Xunit;

namespace ProsimCompanion.Core.Tests.Configuration;

/// <summary>
/// The typed settings write path (#84): keys derive from property expressions and drafts, so
/// these pin the derived names, the camelCase enum values, the diff semantics that protect
/// co-owned keys (gsx.fuelFobSaved), and draft dirty tracking.
/// </summary>
public sealed class SettingsWriterTests : IDisposable
{
    private readonly string _directory;
    private readonly JsonSettingsFile _file;
    private readonly SettingsWriter _writer;

    public SettingsWriterTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ProsimCompanionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _file = new JsonSettingsFile(Path.Combine(_directory, "settings.json"));
        _writer = new SettingsWriter(_file);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Set_DerivesSectionAndCamelCaseKey_FromTheExpression()
    {
        _writer.Set<GsxOptions, string>(o => o.PushbackPreference, "tailLeft");

        Assert.Equal("tailLeft", (string?)_file.Read()["gsx"]?["pushbackPreference"]);
    }

    [Fact]
    public void Set_NestedPropertyChain_BecomesNestedJsonKeys()
    {
        _writer.Set<SpeechOptions, string>(o => o.PttBinding.Key, "F12");

        Assert.Equal("F12", (string?)_file.Read()["speech"]?["pttBinding"]?["key"]);
    }

    [Fact]
    public void Set_EnumValue_WritesTheCamelCaseName()
    {
        _writer.Set<AudioOptions, AudioBackend>(o => o.Backend, AudioBackend.VoiceMeeter);

        Assert.Equal("voiceMeeter", (string?)_file.Read()["audio"]?["backend"]);
    }

    [Fact]
    public void Set_PreservesUnrelatedContent()
    {
        File.WriteAllText(_file.Path, """{ "gsx": { "enabled": false }, "custom": 7 }""");

        _writer.Set<GsxOptions, string>(o => o.PushbackPreference, "straight");

        var root = _file.Read();
        Assert.Equal(false, (bool?)root["gsx"]?["enabled"]);
        Assert.Equal(7, (int?)root["custom"]);
    }

    [Fact]
    public void Set_NonPropertyChainSelector_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => _writer.Set<GsxOptions, string>(o => o.PushbackPreference.ToUpperInvariant(), "x"));
    }

    [Fact]
    public void Save_WritesOnlyTheKeysTheUserChanged()
    {
        var live = new GsxOptions();
        var draft = new OptionsDraft<GsxOptions>(() => live);
        draft.Value.PushbackPreference = "tailRight";

        var saved = _writer.Save(draft);

        Assert.True(saved);
        var gsx = _file.Read()["gsx"];
        Assert.Equal("tailRight", (string?)gsx?["pushbackPreference"]);
        // Untouched properties are not written at all — nothing to clobber later.
        Assert.Null(gsx?["enabled"]);
    }

    [Fact]
    public void Save_NeverClobbersKeysCoOwnedByServices()
    {
        var live = new GsxOptions();
        var draft = new OptionsDraft<GsxOptions>(() => live);
        // The arrival service saves the FOB while the settings page is open (issue #84's
        // co-ownership hazard) — a page save must not write the draft's stale copy back.
        File.WriteAllText(_file.Path, """{ "gsx": { "fuelFobSaved": { "A322 PROSIM": 4321 } } }""");
        draft.Value.Enabled = false;

        _writer.Save(draft);

        var gsx = _file.Read()["gsx"];
        Assert.Equal(false, (bool?)gsx?["enabled"]);
        Assert.Equal(4321, (int?)gsx?["fuelFobSaved"]?["A322 PROSIM"]);
    }

    [Fact]
    public void Save_CleanDrafts_AreANoOp()
    {
        var live = new GsxOptions();
        var draft = new OptionsDraft<GsxOptions>(() => live);

        var saved = _writer.Save(draft);

        Assert.False(saved);
        Assert.False(File.Exists(_file.Path));
    }

    [Fact]
    public void Save_ValueClearedToNull_WritesAnExplicitNull()
    {
        var live = new ProsimOptions { ApiKey = "secret" };
        var draft = new OptionsDraft<ProsimOptions>(() => live);
        draft.Value.ApiKey = null;

        _writer.Save(draft);

        var prosim = Assert.IsType<System.Text.Json.Nodes.JsonObject>(_file.Read()["prosim"]);
        Assert.True(prosim.ContainsKey("apiKey"));
        Assert.Null(prosim["apiKey"]);
    }

    [Fact]
    public void Save_MultipleDrafts_PersistInOneWrite_AndRebaseline()
    {
        var gsxDraft = new OptionsDraft<GsxOptions>(() => new GsxOptions());
        var cabinDraft = new OptionsDraft<CabinOptions>(() => new CabinOptions());
        gsxDraft.Value.ArrivalGate = "B12";
        cabinDraft.Value.DingOnStartup = !cabinDraft.Value.DingOnStartup;

        var saved = _writer.Save(gsxDraft, cabinDraft);

        Assert.True(saved);
        Assert.Equal("B12", (string?)_file.Read()["gsx"]?["arrivalGate"]);
        Assert.NotNull(_file.Read()["cabin"]?["dingOnStartup"]);
        Assert.False(gsxDraft.IsDirty);
        Assert.False(cabinDraft.IsDirty);
    }

    [Fact]
    public void Draft_TracksDirtyState_AndReloadDiscardsEdits()
    {
        var live = new GsxOptions { ArrivalGate = "D5" };
        var draft = new OptionsDraft<GsxOptions>(() => live);

        Assert.False(draft.IsDirty);
        draft.Value.ArrivalGate = "D27";
        Assert.True(draft.IsDirty);

        draft.Reload();

        Assert.False(draft.IsDirty);
        Assert.Equal("D5", draft.Value.ArrivalGate);
    }

    [Fact]
    public void Draft_IsAClone_EditingItNeverTouchesTheLiveOptions()
    {
        var live = new GsxOptions();
        var draft = new OptionsDraft<GsxOptions>(() => live);

        draft.Value.DepartureServices.Clear();
        draft.Value.Enabled = false;

        Assert.True(live.Enabled);
        Assert.NotEmpty(live.DepartureServices);
    }
}
