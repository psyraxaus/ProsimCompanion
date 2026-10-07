using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Network;
using Xunit;

namespace ProsimCompanion.Core.Tests.Network;

/// <summary>The Wake-on-LAN targets list (2026-10-08): the send-all outcome line, and the
/// legacy single-PC block still counted until the Setup page migrates it. The UDP send
/// itself goes to a broadcast address on the loopback-only test machine — harmless.</summary>
public sealed class WakeOnLanStartupTests
{
    [Fact]
    public void SendAll_NothingConfigured_PointsAtSetup()
    {
        var module = Build(new WakeTargetsOptions(), new BriefingOptions());

        Assert.Contains("Settings → Setup", module.SendAll(), StringComparison.Ordinal);
    }

    [Fact]
    public void SendAll_CountsEnabledTargetsOnly_AndNamesBadRows()
    {
        var targets = new WakeTargetsOptions
        {
            Targets =
            [
                new WakeTarget { Name = "LLM box", MacAddress = "AA:BB:CC:DD:EE:FF", BroadcastAddress = "127.0.0.1" },
                new WakeTarget { Name = "Off row", Enabled = false, MacAddress = "11:22:33:44:55:66", BroadcastAddress = "127.0.0.1" },
                new WakeTarget { Name = "Typo", MacAddress = "not-a-mac", BroadcastAddress = "127.0.0.1" },
            ],
        };
        var module = Build(targets, new BriefingOptions());

        var line = module.SendAll();

        Assert.StartsWith("Magic packet sent to 1 PC", line, StringComparison.Ordinal);
        Assert.Contains("Typo:", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Off row", line, StringComparison.Ordinal);
    }

    [Fact]
    public void SendAll_LegacyLlmBlock_StillCountsWhileEnabled()
    {
        var briefing = new BriefingOptions
        {
            LlmWakeOnLan = new WakeOnLanOptions { Enabled = true, MacAddress = "AA:BB:CC:DD:EE:FF", BroadcastAddress = "127.0.0.1" },
        };
        var module = Build(new WakeTargetsOptions(), briefing);

        Assert.StartsWith("Magic packet sent to 1 PC", module.SendAll(), StringComparison.Ordinal);
    }

    [Fact]
    public void Send_OneTarget_ReturnsTheOutcomeLine()
    {
        var module = Build(new WakeTargetsOptions(), new BriefingOptions());

        var line = module.Send(new WakeTarget { MacAddress = "AA:BB:CC:DD:EE:FF", BroadcastAddress = "127.0.0.1" });

        Assert.Contains("AA:BB:CC:DD:EE:FF", line, StringComparison.Ordinal);
    }

    private static WakeOnLanStartup Build(WakeTargetsOptions targets, BriefingOptions briefing)
    {
        var targetsMonitor = new Mock<IOptionsMonitor<WakeTargetsOptions>>();
        targetsMonitor.SetupGet(m => m.CurrentValue).Returns(targets);
        var briefingMonitor = new Mock<IOptionsMonitor<BriefingOptions>>();
        briefingMonitor.SetupGet(m => m.CurrentValue).Returns(briefing);
        return new WakeOnLanStartup(targetsMonitor.Object, briefingMonitor.Object, NullLogger<WakeOnLanStartup>.Instance);
    }
}
