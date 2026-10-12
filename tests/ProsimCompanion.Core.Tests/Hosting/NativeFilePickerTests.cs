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
    public void HostAddresses_always_include_loopback()
    {
        var addresses = LocalClient.HostAddresses();
        Assert.Contains(IPAddress.Loopback, addresses);
        Assert.Same(addresses, LocalClient.HostAddresses()); // cached snapshot
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
    public void Result_shapes()
    {
        Assert.Null(NativePickResult.Cancelled.Path);
        Assert.Null(NativePickResult.Cancelled.Problem);
        Assert.Equal("C:\\x", NativePickResult.Picked("C:\\x").Path);
        Assert.Equal("busy", NativePickResult.Failed("busy").Problem);
    }
}
