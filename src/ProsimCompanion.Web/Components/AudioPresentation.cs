using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Web.Components;

/// <summary>Display mapping for audio domain values (cockpit-style labels, pill tones).
/// The channel and ACP labels are the abbreviations printed on the audio control panel
/// (VHF1, INT, CPT, F/O …), so they stay as they are in every language; the mapping-state
/// pill word is prose and comes back as a language-pack text id for the Razor to resolve
/// (ADR-0014 — a static class cannot inject the per-circuit localizer).</summary>
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
        AudioChannel.Loudspeaker => "LOUDSPEAKER",
        _ => channel.ToString().ToUpperInvariant(),
    };

    public static string AcpLabel(AcpSide acp) => acp switch
    {
        AcpSide.Captain => "CPT",
        AcpSide.FirstOfficer => "F/O",
        AcpSide.Observer => "OBS",
        _ => acp.ToString(),
    };

    /// <summary>The pill tone and the text id of the pill word (under
    /// <c>audioSettings.status.mappingState</c>) for a mapping's live state.</summary>
    public static (string Tone, string LabelTextId) MappingState(AudioMappingState state) => state switch
    {
        AudioMappingState.Bound => ("tone-ok", "audioSettings.status.mappingState.bound"),
        AudioMappingState.Searching => ("tone-active", "audioSettings.status.mappingState.searching"),
        AudioMappingState.Elevated => ("tone-warn", "audioSettings.status.mappingState.elevated"),
        _ => ("tone-neutral", "audioSettings.status.mappingState.notRunning"),
    };
}
