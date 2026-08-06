using ProsimCompanion.Audio.Acp;
using ProsimCompanion.Core.Configuration;
using Xunit;

namespace ProsimCompanion.Core.Tests.Audio;

public sealed class AcpDataRefCatalogTests
{
    public static IEnumerable<object[]> AllKeys()
    {
        foreach (var acp in Enum.GetValues<AcpSide>())
        {
            foreach (var channel in Enum.GetValues<AudioChannel>())
            {
                yield return [acp, channel];
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllKeys))]
    public void EveryKey_ResolvesToDistinctNonEmptyRefs(AcpSide acp, AudioChannel channel)
    {
        var volume = AcpDataRefCatalog.VolumeRef(acp, channel);
        var latch = AcpDataRefCatalog.LatchRef(acp, channel);

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

        Assert.Equal(all.Count, all.Select(k => AcpDataRefCatalog.VolumeRef(k.Item1, k.Item2)).Distinct().Count());
        Assert.Equal(all.Count, all.Select(k => AcpDataRefCatalog.LatchRef(k.Item1, k.Item2)).Distinct().Count());
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
        Assert.Equal(expected, AcpDataRefCatalog.VolumeRef(acp, channel));
    }

    [Theory]
    [InlineData(AcpSide.Captain, AudioChannel.Vhf1, "system.switches.S_ASP_VHF_1_REC_LATCH")]
    [InlineData(AcpSide.FirstOfficer, AudioChannel.Intercom, "system.switches.S_ASP2_INT_REC_LATCH")]
    [InlineData(AcpSide.Observer, AudioChannel.Hf2, "system.switches.S_ASP3_HF_2_REC_LATCH")]
    public void LatchRef_MatchesTheWireNames(AcpSide acp, AudioChannel channel, string expected)
    {
        Assert.Equal(expected, AcpDataRefCatalog.LatchRef(acp, channel));
    }
}
