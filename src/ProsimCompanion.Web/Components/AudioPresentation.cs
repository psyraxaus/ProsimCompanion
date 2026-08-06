using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Web.Components;

/// <summary>Display mapping for audio domain values (cockpit-style labels, pill tones).</summary>
public static class AudioPresentation
{
    public static string ChannelLabel(AudioChannel channel) => channel switch
    {
        AudioChannel.Vhf1 => "VHF1",
        AudioChannel.Vhf2 => "VHF2",
        AudioChannel.Vhf3 => "VHF3",
        AudioChannel.Hf1 => "HF1",
        AudioChannel.Hf2 => "HF2",
        AudioChannel.Intercom => "INT",
        AudioChannel.Cabin => "CAB",
        AudioChannel.Pa => "PA",
        _ => channel.ToString().ToUpperInvariant(),
    };

    public static string AcpLabel(AcpSide acp) => acp switch
    {
        AcpSide.Captain => "CPT",
        AcpSide.FirstOfficer => "F/O",
        AcpSide.Observer => "OBS",
        _ => acp.ToString(),
    };

    public static (string Tone, string Label) MappingState(AudioMappingState state) => state switch
    {
        AudioMappingState.Bound => ("tone-ok", "Bound"),
        AudioMappingState.Searching => ("tone-active", "Searching"),
        AudioMappingState.Elevated => ("tone-warn", "Elevated"),
        _ => ("tone-neutral", "Not running"),
    };
}
