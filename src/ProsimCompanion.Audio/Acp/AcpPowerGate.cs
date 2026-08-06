using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Audio.Acp;

/// <summary>Electrical inputs to the per-ACP power gate. AudioSwitching is
/// S_AUDIO_SWITCHING: 0 = CAPT (captain swapped to ACP3), 1 = NORM, 2 = F/O.</summary>
public readonly record struct AcpPowerInputs(bool AcEss, bool DcEss, bool Dc1, int AudioSwitching)
{
    /// <summary>NORM switching and no bus power — the state before any dataref arrives.
    /// AudioSwitching defaults to NORM so an unreadable switch never spuriously gates out
    /// the captain or first officer.</summary>
    public static AcpPowerInputs Unknown { get; } = new(false, false, false, AudioSwitching: 1);
}

/// <summary>
/// Per-ACP electrical gating: knob values from an unpowered panel are electrical noise and
/// must be ignored (hold last written state, never write). Applied uniformly to both backends
/// — the predecessor gated CoreAudio only on DC ESS at startup, which let writes continue
/// through a power loss.
/// </summary>
public static class AcpPowerGate
{
    public static bool IsPowered(AcpSide acp, in AcpPowerInputs inputs) => acp switch
    {
        // 0 = CAPT: the captain's headset is swapped onto ACP3, so ACP1 is out of the loop.
        AcpSide.Captain => (inputs.AcEss || inputs.DcEss) && inputs.AudioSwitching != 0,
        // 2 = F/O: same swap for the first officer.
        AcpSide.FirstOfficer => (inputs.AcEss || inputs.DcEss) && inputs.AudioSwitching != 2,
        AcpSide.Observer => inputs.Dc1,
        _ => false,
    };
}
