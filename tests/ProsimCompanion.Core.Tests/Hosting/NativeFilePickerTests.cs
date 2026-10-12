using System.Net;
using ProsimCompanion.Core.Hosting;
using Xunit;

namespace ProsimCompanion.Core.Tests.Hosting;

/// <summary>The pure halves of the sim-PC Browse… button (option 3, 2026-10-13): who counts
/// as a local browser, and where a dialog opens for a field's current value.</summary>
public sealed class NativeFilePickerTests
{
    private static readonly IPAddress[] Host = [IPAddress.Parse("192.168.1.5"), IPAddress.Parse("fe80::1%12"), IPAddress.Loopback];

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("192.168.1.5")]
    [InlineData("::ffff:192.168.1.5")]
    [InlineData("fe80::1")]
    public void Local_when_loopback_or_one_of_our_own_addresses(string remote)
        => Assert.True(LocalClient.IsLocal(IPAddress.Parse(remote), Host));

    [Theory]
    [InlineData("192.168.1.20")]
    [InlineData("10.0.0.1")]
    [InlineData("::ffff:192.168.1.20")]
    public void Remote_when_another_machine(string remote)
        => Assert.False(LocalClient.IsLocal(IPAddress.Parse(remote), Host));

    [Fact]
    public void No_address_is_remote()
        => Assert.False(LocalClient.IsLocal(null, Host));

    [Fact]
    public void Host_addresses_always_include_both_loopbacks_once()
    {
        var lan = IPAddress.Parse("192.168.1.5");

        var addresses = LocalClient.WithLoopback([lan, IPAddress.Loopback, lan]);

        Assert.Equal([lan, IPAddress.Loopback, IPAddress.IPv6Loopback], addresses);
        Assert.Equal([IPAddress.Loopback, IPAddress.IPv6Loopback], LocalClient.WithLoopback([]));
    }

    [Fact]
    public void Dialog_opens_in_the_value_folder_or_the_fallback()
    {
        var folder = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var file = Path.GetTempFileName();
        try
        {
            Assert.Equal(folder, PickStart.DirectoryFor(folder, "X:\\fallback"));
            Assert.Equal(folder, PickStart.DirectoryFor("\"" + folder + "\"", "X:\\fallback"));
            Assert.Equal(Path.GetDirectoryName(file), PickStart.DirectoryFor(file, "X:\\fallback"));
            // A file that is not there yet still opens in its (existing) folder.
            Assert.Equal(folder, PickStart.DirectoryFor(Path.Combine(folder, "not-yet.json"), "X:\\fallback"));
        }
        finally
        {
            File.Delete(file);
        }

        Assert.Equal("X:\\fallback", PickStart.DirectoryFor("", "X:\\fallback"));
        Assert.Equal("X:\\fallback", PickStart.DirectoryFor(null, "X:\\fallback"));
        Assert.Equal("X:\\fallback", PickStart.DirectoryFor("Q:\\no\\such\\dir\\x.json", "X:\\fallback"));
        Assert.Equal("X:\\fallback", PickStart.DirectoryFor("C:\\bad\0name", "X:\\fallback"));
    }

    [Fact]
    public void Dialog_falls_through_value_then_expected_folders_then_fallback()
    {
        var present = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var absent = Path.Combine(present, "gone-" + Guid.NewGuid().ToString("N"));

        // The value's folder wins over the expected list.
        Assert.Equal(present, PickStart.DirectoryFor(present, [absent], "X:\\"));
        // No usable value: the first expected folder that exists.
        Assert.Equal(present, PickStart.DirectoryFor("", [absent, present], "X:\\"));
        Assert.Equal(present, PickStart.DirectoryFor(absent, [absent, present], "X:\\"));
        // Nothing exists: the fallback (the system drive in production).
        Assert.Equal("X:\\", PickStart.DirectoryFor("", [absent], "X:\\"));
        Assert.Equal("X:\\", PickStart.DirectoryFor("", [], "X:\\"));
    }

    [Fact]
    public void Expected_folder_lists_are_populated()
    {
        Assert.EndsWith("\\", ExpectedFolders.SystemDrive, StringComparison.Ordinal);
        Assert.NotEmpty(ExpectedFolders.ProSimSdk());
        Assert.Single(ExpectedFolders.ProSimNavdata());
        Assert.EndsWith(Path.Combine("ProSim-AR", "Navdata"), ExpectedFolders.ProSimNavdata()[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, ExpectedFolders.VoiceMeeter().Count);
        Assert.NotEmpty(ExpectedFolders.ActiveSky());
        Assert.EndsWith("rotations", ExpectedFolders.Rotations()[0], StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("ProsimCompanion", ExpectedFolders.AppStateFolder()[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rotations_folder_resolves_blank_and_the_legacy_default_to_the_user_folder()
    {
        const string baseDir = "C:\\app";
        const string user = "C:\\user\\rotations";

        Assert.Equal(user, Core.Day.RotationLoader.ResolveFolder("", baseDir, user, _ => false));
        Assert.Equal(user, Core.Day.RotationLoader.ResolveFolder(null, baseDir, user, _ => false));
        // The pre-2026-10-13 default under the exe is honoured only while it exists.
        Assert.Equal(user, Core.Day.RotationLoader.ResolveFolder("rotations", baseDir, user, _ => false));
        Assert.Equal("C:\\app\\rotations", Core.Day.RotationLoader.ResolveFolder("rotations", baseDir, user, _ => true));
        // Any other relative path stays under the exe; a rooted path is taken as is.
        Assert.Equal("C:\\app\\plans", Core.Day.RotationLoader.ResolveFolder("plans", baseDir, user, _ => false));
        Assert.Equal("D:\\plans", Core.Day.RotationLoader.ResolveFolder("\"D:\\plans\"", baseDir, user, _ => false));
    }

    [Fact]
    public void Result_shapes()
    {
        Assert.Null(NativePickResult.Cancelled.Path);
        Assert.Null(NativePickResult.Cancelled.Problem);
        Assert.Equal("C:\\x", NativePickResult.Picked("C:\\x").Path);
        Assert.Equal("busy", NativePickResult.Failed("busy").Problem);
    }
}
