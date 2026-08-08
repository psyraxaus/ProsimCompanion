using ProsimCompanion.Audio.Backends.VoiceMeeter;
using Xunit;

namespace ProsimCompanion.Core.Tests.Audio;

public sealed class VoiceMeeterLocatorTests
{
    private const string Configured = @"D:\custom\VoicemeeterRemote64.dll";
    private const string RegistryDll = @"C:\Program Files (x86)\VB\Voicemeeter\VoicemeeterRemote64.dll";

    [Fact]
    public void ConfiguredPathThatExists_WinsOverEverything()
    {
        var result = VoiceMeeterLocator.Resolve(
            Configured,
            fileExists: _ => true,
            readUninstallString: () => @"C:\Program Files (x86)\VB\Voicemeeter\uninstall.exe");

        Assert.Equal(Configured, result);
    }

    [Fact]
    public void MissingConfiguredPath_FallsBackToRegistryInstallDir()
    {
        var result = VoiceMeeterLocator.Resolve(
            Configured,
            fileExists: path => path == RegistryDll,
            readUninstallString: () => @"C:\Program Files (x86)\VB\Voicemeeter\uninstall.exe");

        Assert.Equal(RegistryDll, result);
    }

    [Fact]
    public void QuotedUninstallString_IsParsed()
    {
        var result = VoiceMeeterLocator.Resolve(
            "",
            fileExists: path => path == RegistryDll,
            readUninstallString: () => "\"C:\\Program Files (x86)\\VB\\Voicemeeter\\uninstall.exe\"");

        Assert.Equal(RegistryDll, result);
    }

    [Fact]
    public void NoRegistryKey_FallsBackToDefaultInstallFolders()
    {
        // The default-folder candidates are built from the real ProgramFiles special folders,
        // so match on the invariant tail rather than a hard-coded root.
        var result = VoiceMeeterLocator.Resolve(
            "",
            fileExists: path => path.EndsWith(
                @"VB\Voicemeeter\VoicemeeterRemote64.dll", StringComparison.OrdinalIgnoreCase),
            readUninstallString: () => null);

        Assert.EndsWith(@"VB\Voicemeeter\VoicemeeterRemote64.dll", result);
    }

    [Fact]
    public void NothingFoundAnywhere_ReturnsEmpty()
    {
        var result = VoiceMeeterLocator.Resolve(
            "",
            fileExists: _ => false,
            readUninstallString: () => @"C:\Program Files (x86)\VB\Voicemeeter\uninstall.exe");

        Assert.Equal("", result);
    }
}
