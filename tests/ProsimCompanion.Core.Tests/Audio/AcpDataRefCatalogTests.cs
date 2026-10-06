using ProsimCompanion.Audio.Acp;
using ProsimCompanion.Core.Configuration;
using Xunit;

namespace ProsimCompanion.Core.Tests.Audio;

public sealed class AcpDataRefCatalogTests
{
    /// <summary>The 24 ACP knob keys — the loudspeaker dial is not an ACP knob (no latch,
    /// no observer dial) and has its own tests below.</summary>
    public static IEnumerable<object[]> AllKeys()
    {
        foreach (var acp in Enum.GetValues<AcpSide>())
        {
            foreach (var channel in Enum.GetValues<AudioChannel>())
            {
                if (channel.HasRecLatch())
                {
                    yield return [acp, channel];
                }
            }
        }
    }

    [Theory]
    [InlineData(AcpSide.Captain, "system.analog.A_MIP_LOUDSPEAKER_CAPT")]
    [InlineData(AcpSide.FirstOfficer, "system.analog.A_MIP_LOUDSPEAKER_FO")]
    public void LoudspeakerDial_MatchesTheWireNames(AcpSide acp, string expected)
    {
        Assert.True(AudioChannel.Loudspeaker.ExistsOn(acp));
        Assert.Equal(expected, AcpDataRefCatalog.VolumeRef(acp, AudioChannel.Loudspeaker).Name);
    }

    [Fact]
    public void LoudspeakerDial_HasNoLatch_AndNoObserverDial()
    {
        Assert.False(AudioChannel.Loudspeaker.HasRecLatch());
        Assert.False(AudioChannel.Loudspeaker.ExistsOn(AcpSide.Observer));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AcpDataRefCatalog.LatchRef(AcpSide.Captain, AudioChannel.Loudspeaker));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AcpDataRefCatalog.VolumeRef(AcpSide.Observer, AudioChannel.Loudspeaker));
    }

    [Fact]
    public void EveryAcpChannel_ExistsOnEveryPanel()
    {
        Assert.All(AllKeys(), key => Assert.True(((AudioChannel)key[1]).ExistsOn((AcpSide)key[0])));
    }

    [Theory]
    [MemberData(nameof(AllKeys))]
    public void EveryKey_ResolvesToDistinctNonEmptyRefs(AcpSide acp, AudioChannel channel)
    {
        var volume = AcpDataRefCatalog.VolumeRef(acp, channel).Name;
        var latch = AcpDataRefCatalog.LatchRef(acp, channel).Name;

        Assert.False(string.IsNullOrWhiteSpace(volume));
        Assert.False(string.IsNullOrWhiteSpace(latch));
        Assert.StartsWith("system.analog.A_ASP", volume, StringComparison.Ordinal);
        Assert.StartsWith("system.switches.S_ASP", latch, StringComparison.Ordinal);
        Assert.EndsWith("_VOLUME", volume, StringComparison.Ordinal);
        Assert.EndsWith("_REC_LATCH", latch, StringComparison.Ordinal);
    }

    [Fact]
    public void NoTwoKeys_ShareARef()
    {
        var all = AllKeys().Select(k => ((AcpSide)k[0], (AudioChannel)k[1])).ToList();

        Assert.Equal(all.Count, all.Select(k => AcpDataRefCatalog.VolumeRef(k.Item1, k.Item2).Name).Distinct().Count());
        Assert.Equal(all.Count, all.Select(k => AcpDataRefCatalog.LatchRef(k.Item1, k.Item2).Name).Distinct().Count());
    }

    [Theory]
    // Spot checks against the wire names ported verbatim from ProsimConstants:
    // ACP1 has no numeral in its prefix; INT/CAB use the panel's short names.
    [InlineData(AcpSide.Captain, AudioChannel.Vhf1, "system.analog.A_ASP_VHF_1_VOLUME")]
    [InlineData(AcpSide.Captain, AudioChannel.Intercom, "system.analog.A_ASP_INT_VOLUME")]
    [InlineData(AcpSide.FirstOfficer, AudioChannel.Cabin, "system.analog.A_ASP2_CAB_VOLUME")]
    [InlineData(AcpSide.Observer, AudioChannel.Pa, "system.analog.A_ASP3_PA_VOLUME")]
    public void VolumeRef_MatchesTheWireNames(AcpSide acp, AudioChannel channel, string expected)
    {
        Assert.Equal(expected, AcpDataRefCatalog.VolumeRef(acp, channel).Name);
    }

    [Theory]
    [InlineData(AcpSide.Captain, AudioChannel.Vhf1, "system.switches.S_ASP_VHF_1_REC_LATCH")]
    [InlineData(AcpSide.FirstOfficer, AudioChannel.Intercom, "system.switches.S_ASP2_INT_REC_LATCH")]
    [InlineData(AcpSide.Observer, AudioChannel.Hf2, "system.switches.S_ASP3_HF_2_REC_LATCH")]
    public void LatchRef_MatchesTheWireNames(AcpSide acp, AudioChannel channel, string expected)
    {
        Assert.Equal(expected, AcpDataRefCatalog.LatchRef(acp, channel).Name);
    }
}
