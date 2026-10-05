using ProsimCompanion.Prosim.Sdk;
using Xunit;

namespace ProsimCompanion.Core.Tests.Prosim;

/// <summary>
/// Issue #158 (ticket t-20261005-1918): the ProSimSDK.dll builds in the field have disjoint
/// <c>ProSimConnect</c> constructors — ProSim 1.74-beta.8 only <c>()</c>, the 2026-07 build only
/// <c>(string apiKey = "")</c> — and the app bound the second one directly. The stand-in types
/// below have the same constructor shapes as the two real dlls.
/// </summary>
public sealed class SdkConstructorSelectorTests
{
    // The shape enum is internal, so the theories carry it as an int.
    [Theory]
    [InlineData(typeof(OlderSdkConnect), (int)SdkConstructorShape.Parameterless)]
    [InlineData(typeof(NewerSdkConnect), (int)SdkConstructorShape.ApiKey)]
    [InlineData(typeof(BothSdkConnect), (int)(SdkConstructorShape.Parameterless | SdkConstructorShape.ApiKey))]
    [InlineData(typeof(UnknownSdkConnect), (int)SdkConstructorShape.None)]
    public void Detect_ReadsTheConstructorsTheTypeOffers(Type connectType, int expected)
        => Assert.Equal((SdkConstructorShape)expected, SdkConstructorSelector.Detect(connectType));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_OlderSdk_NoKey_UsesTheParameterlessConstructor(string? apiKey)
    {
        var connect = Assert.IsType<OlderSdkConnect>(SdkConstructorSelector.Create(typeof(OlderSdkConnect), apiKey));

        Assert.True(connect.Built);
    }

    [Fact]
    public void Create_OlderSdk_WithKey_StillConnects_TheKeyIsDropped()
    {
        // The older dll cannot take a key; refusing to connect over it would turn an unused
        // setting into an outage. The service warns instead.
        var connect = Assert.IsType<OlderSdkConnect>(SdkConstructorSelector.Create(typeof(OlderSdkConnect), "secret"));

        Assert.True(connect.Built);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Create_NewerSdk_NoKey_PassesTheConstructorsOwnDefault(string? apiKey)
    {
        // What the compiler passed for `new ProSimConnect()` against the newer dll.
        var connect = Assert.IsType<NewerSdkConnect>(SdkConstructorSelector.Create(typeof(NewerSdkConnect), apiKey));

        Assert.Equal("", connect.ApiKey);
    }

    [Fact]
    public void Create_NewerSdk_WithKey_PassesTheKey()
    {
        var connect = Assert.IsType<NewerSdkConnect>(SdkConstructorSelector.Create(typeof(NewerSdkConnect), "secret"));

        Assert.Equal("secret", connect.ApiKey);
    }

    [Fact]
    public void Create_SdkWithBoth_PicksByWhetherAKeyIsSet()
    {
        var withoutKey = Assert.IsType<BothSdkConnect>(SdkConstructorSelector.Create(typeof(BothSdkConnect), null));
        var withKey = Assert.IsType<BothSdkConnect>(SdkConstructorSelector.Create(typeof(BothSdkConnect), "secret"));

        Assert.Null(withoutKey.ApiKey);
        Assert.Equal("secret", withKey.ApiKey);
    }

    [Fact]
    public void Create_UnknownShape_ThrowsTheMismatchException()
    {
        var ex = Assert.Throws<SdkIncompatibleException>(
            () => SdkConstructorSelector.Create(typeof(UnknownSdkConnect), null));

        Assert.Contains(nameof(UnknownSdkConnect), ex.Message, StringComparison.Ordinal);
        Assert.True(SdkIncompatibleException.IsMismatch(ex));
    }

    [Fact]
    public void Create_ConstructorThatThrows_SurfacesTheSdksOwnException()
    {
        // A bad key throws from inside the SDK; reflection must not bury it in a
        // TargetInvocationException.
        Assert.Throws<UnauthorizedAccessException>(
            () => SdkConstructorSelector.Create(typeof(RejectingSdkConnect), "bad-key"));
    }

    [Fact]
    public void IsMismatch_CoversTheRuntimesBindingFailures_AndNothingElse()
    {
        // MissingMethodException is the field signature of #158.
        Assert.True(SdkIncompatibleException.IsMismatch(new MissingMethodException("ProSimSDK.ProSimConnect", ".ctor")));
        Assert.True(SdkIncompatibleException.IsMismatch(new MissingFieldException()));
        Assert.True(SdkIncompatibleException.IsMismatch(new TypeLoadException()));
        Assert.True(SdkIncompatibleException.IsMismatch(new BadImageFormatException()));

        Assert.False(SdkIncompatibleException.IsMismatch(new InvalidOperationException()));
        Assert.False(SdkIncompatibleException.IsMismatch(new FileNotFoundException()));
    }

    [Fact]
    public void Inspect_ReportsWhereTheTypeCameFrom_AndItsShape()
    {
        var info = SdkConstructorSelector.Inspect(typeof(OlderSdkConnect));

        Assert.Equal(typeof(OlderSdkConnect).Assembly.Location, info.Location);
        Assert.False(string.IsNullOrWhiteSpace(info.ProductVersion));
        Assert.Equal(SdkConstructorShape.Parameterless, info.Shape);
    }

    [Theory]
    [InlineData((int)SdkConstructorShape.Parameterless, "parameterless")]
    [InlineData((int)SdkConstructorShape.ApiKey, "API-key")]
    [InlineData((int)(SdkConstructorShape.Parameterless | SdkConstructorShape.ApiKey), "parameterless and API-key")]
    [InlineData((int)SdkConstructorShape.None, "unknown")]
    public void Describe_NamesTheShapeForTheLog(int shape, string expected)
        => Assert.Equal(expected, SdkConstructorSelector.Describe((SdkConstructorShape)shape));

    /// <summary>Shape of ProSimConnect in ProSim 1.74-beta.8 (dll 1.0.0+2f0d88c188, 2025-11-10).</summary>
    private sealed class OlderSdkConnect
    {
        public OlderSdkConnect() => Built = true;

        public bool Built { get; }
    }

    /// <summary>Shape of ProSimConnect in the 2026-07 build (dll 1.0.0+626faa2df9).</summary>
    private sealed class NewerSdkConnect
    {
        public NewerSdkConnect(string apiKey = "") => ApiKey = apiKey;

        public string ApiKey { get; }
    }

    private sealed class BothSdkConnect
    {
        public BothSdkConnect()
        {
        }

        public BothSdkConnect(string apiKey) => ApiKey = apiKey;

        public string? ApiKey { get; }
    }

    private sealed class UnknownSdkConnect
    {
        public UnknownSdkConnect(int port) => Port = port;

        public int Port { get; }
    }

    private sealed class RejectingSdkConnect
    {
        public RejectingSdkConnect(string apiKey)
            => throw new UnauthorizedAccessException($"rejected key of length {apiKey.Length}");
    }
}
