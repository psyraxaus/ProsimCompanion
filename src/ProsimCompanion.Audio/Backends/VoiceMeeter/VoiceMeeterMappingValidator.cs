using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Audio.Backends.VoiceMeeter;

/// <summary>
/// Validates the active VoiceMeeter mapping set before binding. Two invariants: a channel may
/// appear only once per ACP (which knob wins?), and a strip/bus target may be driven by only
/// one ACP (racing writes). VHF1→Strip 1 on ACP1 plus VHF1→Strip 5 on ACP2 is legal.
/// </summary>
public static class VoiceMeeterMappingValidator
{
    /// <summary>Returns null when valid, else a human-readable reason. On a conflict the
    /// binder falls back to Captain-only without touching the config — fix and save.</summary>
    public static string? Validate(
        IReadOnlyList<(AcpSide Acp, IReadOnlyList<VoiceMeeterTargetMapping> Mappings)> activeSets)
    {
        ArgumentNullException.ThrowIfNull(activeSets);

        var targets = new Dictionary<(int Index, bool IsBus), AcpSide>();
        foreach (var (acp, mappings) in activeSets)
        {
            var channels = new HashSet<AudioChannel>();
            foreach (var mapping in mappings)
            {
                if (!channels.Add(mapping.Channel))
                {
                    return $"{acp}: channel {mapping.Channel} is mapped more than once";
                }

                var key = (mapping.StripIndex, mapping.IsBus);
                if (targets.TryGetValue(key, out var owner))
                {
                    var target = $"{(mapping.IsBus ? "Bus" : "Strip")} {mapping.StripIndex + 1}";
                    return $"{target} is driven by both {owner} and {acp}";
                }

                targets[key] = acp;
            }
        }

        return null;
    }
}
