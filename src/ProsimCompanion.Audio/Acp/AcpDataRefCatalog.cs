using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Audio.Acp;

/// <summary>
/// Maps (ACP, channel) to the ProSim knob/latch dataref names. ACP1 = Captain, ACP2 = First
/// Officer, ACP3 = Observer; knob analogs are 0–1024, REC latches are 0 = muted / 1 = unmuted.
/// </summary>
public static class AcpDataRefCatalog
{
    public static DataRef<double> VolumeRef(AcpSide acp, AudioChannel channel) => (acp, channel) switch
    {
        (AcpSide.Captain, AudioChannel.Vhf1) => ProsimDataRefNames.Acp1Vhf1Analog,
        (AcpSide.Captain, AudioChannel.Vhf2) => ProsimDataRefNames.Acp1Vhf2Analog,
        (AcpSide.Captain, AudioChannel.Vhf3) => ProsimDataRefNames.Acp1Vhf3Analog,
        (AcpSide.Captain, AudioChannel.Hf1) => ProsimDataRefNames.Acp1Hf1Analog,
        (AcpSide.Captain, AudioChannel.Hf2) => ProsimDataRefNames.Acp1Hf2Analog,
        (AcpSide.Captain, AudioChannel.Intercom) => ProsimDataRefNames.Acp1IntAnalog,
        (AcpSide.Captain, AudioChannel.Cabin) => ProsimDataRefNames.Acp1CabAnalog,
        (AcpSide.Captain, AudioChannel.Pa) => ProsimDataRefNames.Acp1PaAnalog,
        (AcpSide.FirstOfficer, AudioChannel.Vhf1) => ProsimDataRefNames.Acp2Vhf1Analog,
        (AcpSide.FirstOfficer, AudioChannel.Vhf2) => ProsimDataRefNames.Acp2Vhf2Analog,
        (AcpSide.FirstOfficer, AudioChannel.Vhf3) => ProsimDataRefNames.Acp2Vhf3Analog,
        (AcpSide.FirstOfficer, AudioChannel.Hf1) => ProsimDataRefNames.Acp2Hf1Analog,
        (AcpSide.FirstOfficer, AudioChannel.Hf2) => ProsimDataRefNames.Acp2Hf2Analog,
        (AcpSide.FirstOfficer, AudioChannel.Intercom) => ProsimDataRefNames.Acp2IntAnalog,
        (AcpSide.FirstOfficer, AudioChannel.Cabin) => ProsimDataRefNames.Acp2CabAnalog,
        (AcpSide.FirstOfficer, AudioChannel.Pa) => ProsimDataRefNames.Acp2PaAnalog,
        (AcpSide.Observer, AudioChannel.Vhf1) => ProsimDataRefNames.Acp3Vhf1Analog,
        (AcpSide.Observer, AudioChannel.Vhf2) => ProsimDataRefNames.Acp3Vhf2Analog,
        (AcpSide.Observer, AudioChannel.Vhf3) => ProsimDataRefNames.Acp3Vhf3Analog,
        (AcpSide.Observer, AudioChannel.Hf1) => ProsimDataRefNames.Acp3Hf1Analog,
        (AcpSide.Observer, AudioChannel.Hf2) => ProsimDataRefNames.Acp3Hf2Analog,
        (AcpSide.Observer, AudioChannel.Intercom) => ProsimDataRefNames.Acp3IntAnalog,
        (AcpSide.Observer, AudioChannel.Cabin) => ProsimDataRefNames.Acp3CabAnalog,
        (AcpSide.Observer, AudioChannel.Pa) => ProsimDataRefNames.Acp3PaAnalog,
        _ => throw new ArgumentOutOfRangeException(nameof(channel), $"{acp}/{channel}"),
    };

    public static DataRef<int> LatchRef(AcpSide acp, AudioChannel channel) => (acp, channel) switch
    {
        (AcpSide.Captain, AudioChannel.Vhf1) => ProsimDataRefNames.Acp1Vhf1Latch,
        (AcpSide.Captain, AudioChannel.Vhf2) => ProsimDataRefNames.Acp1Vhf2Latch,
        (AcpSide.Captain, AudioChannel.Vhf3) => ProsimDataRefNames.Acp1Vhf3Latch,
        (AcpSide.Captain, AudioChannel.Hf1) => ProsimDataRefNames.Acp1Hf1Latch,
        (AcpSide.Captain, AudioChannel.Hf2) => ProsimDataRefNames.Acp1Hf2Latch,
        (AcpSide.Captain, AudioChannel.Intercom) => ProsimDataRefNames.Acp1IntLatch,
        (AcpSide.Captain, AudioChannel.Cabin) => ProsimDataRefNames.Acp1CabLatch,
        (AcpSide.Captain, AudioChannel.Pa) => ProsimDataRefNames.Acp1PaLatch,
        (AcpSide.FirstOfficer, AudioChannel.Vhf1) => ProsimDataRefNames.Acp2Vhf1Latch,
        (AcpSide.FirstOfficer, AudioChannel.Vhf2) => ProsimDataRefNames.Acp2Vhf2Latch,
        (AcpSide.FirstOfficer, AudioChannel.Vhf3) => ProsimDataRefNames.Acp2Vhf3Latch,
        (AcpSide.FirstOfficer, AudioChannel.Hf1) => ProsimDataRefNames.Acp2Hf1Latch,
        (AcpSide.FirstOfficer, AudioChannel.Hf2) => ProsimDataRefNames.Acp2Hf2Latch,
        (AcpSide.FirstOfficer, AudioChannel.Intercom) => ProsimDataRefNames.Acp2IntLatch,
        (AcpSide.FirstOfficer, AudioChannel.Cabin) => ProsimDataRefNames.Acp2CabLatch,
        (AcpSide.FirstOfficer, AudioChannel.Pa) => ProsimDataRefNames.Acp2PaLatch,
        (AcpSide.Observer, AudioChannel.Vhf1) => ProsimDataRefNames.Acp3Vhf1Latch,
        (AcpSide.Observer, AudioChannel.Vhf2) => ProsimDataRefNames.Acp3Vhf2Latch,
        (AcpSide.Observer, AudioChannel.Vhf3) => ProsimDataRefNames.Acp3Vhf3Latch,
        (AcpSide.Observer, AudioChannel.Hf1) => ProsimDataRefNames.Acp3Hf1Latch,
        (AcpSide.Observer, AudioChannel.Hf2) => ProsimDataRefNames.Acp3Hf2Latch,
        (AcpSide.Observer, AudioChannel.Intercom) => ProsimDataRefNames.Acp3IntLatch,
        (AcpSide.Observer, AudioChannel.Cabin) => ProsimDataRefNames.Acp3CabLatch,
        (AcpSide.Observer, AudioChannel.Pa) => ProsimDataRefNames.Acp3PaLatch,
        _ => throw new ArgumentOutOfRangeException(nameof(channel), $"{acp}/{channel}"),
    };
}
