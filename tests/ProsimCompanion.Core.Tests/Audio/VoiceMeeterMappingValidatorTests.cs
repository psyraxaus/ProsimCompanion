using ProsimCompanion.Audio.Backends.VoiceMeeter;
using ProsimCompanion.Core.Configuration;
using Xunit;

namespace ProsimCompanion.Core.Tests.Audio;

public sealed class VoiceMeeterMappingValidatorTests
{
    private static (AcpSide, IReadOnlyList<VoiceMeeterTargetMapping>) Set(
        AcpSide acp, params VoiceMeeterTargetMapping[] mappings) => (acp, mappings);

    [Fact]
    public void EmptyAndSingleMappings_AreValid()
    {
        Assert.Null(VoiceMeeterMappingValidator.Validate([]));
        Assert.Null(VoiceMeeterMappingValidator.Validate(
        [
            Set(AcpSide.Captain, new VoiceMeeterTargetMapping(AudioChannel.Vhf1, 0, isBus: false)),
        ]));
    }

    [Fact]
    public void DuplicateChannelWithinOneAcp_IsRejected()
    {
        var reason = VoiceMeeterMappingValidator.Validate(
        [
            Set(AcpSide.Captain,
                new VoiceMeeterTargetMapping(AudioChannel.Vhf1, 0, isBus: false),
                new VoiceMeeterTargetMapping(AudioChannel.Vhf1, 1, isBus: false)),
        ]);

        Assert.NotNull(reason);
        Assert.Contains("Vhf1", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SameTargetDrivenByTwoAcps_IsRejected()
    {
        var reason = VoiceMeeterMappingValidator.Validate(
        [
            Set(AcpSide.Captain, new VoiceMeeterTargetMapping(AudioChannel.Vhf1, 2, isBus: false)),
            Set(AcpSide.FirstOfficer, new VoiceMeeterTargetMapping(AudioChannel.Intercom, 2, isBus: false)),
        ]);

        Assert.NotNull(reason);
        Assert.Contains("Strip 3", reason, StringComparison.Ordinal); // UI-facing 1-based index
    }

    [Fact]
    public void SameIndexAsStripAndBus_AreDifferentTargets()
    {
        Assert.Null(VoiceMeeterMappingValidator.Validate(
        [
            Set(AcpSide.Captain, new VoiceMeeterTargetMapping(AudioChannel.Vhf1, 0, isBus: false)),
            Set(AcpSide.FirstOfficer, new VoiceMeeterTargetMapping(AudioChannel.Vhf1, 0, isBus: true)),
        ]));
    }

    [Fact]
    public void SameChannelOnDifferentAcpsWithDifferentTargets_IsLegal()
    {
        Assert.Null(VoiceMeeterMappingValidator.Validate(
        [
            Set(AcpSide.Captain, new VoiceMeeterTargetMapping(AudioChannel.Vhf1, 0, isBus: false)),
            Set(AcpSide.FirstOfficer, new VoiceMeeterTargetMapping(AudioChannel.Vhf1, 4, isBus: false)),
        ]));
    }
}
