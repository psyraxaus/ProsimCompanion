using System.Globalization;
using System.Text.Json;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Web.Settings;

/// <summary>
/// The Audio Control page's own rules (candidate C3): list pruning on save, the per-ACP mapping
/// dictionary's name normalisation, and the one-based strip/bus text bridges of the remote
/// mixer panel. Plain C# so xunit reaches it; a per-property trim is declared on the property.
/// </summary>
public static class AudioPageRules
{
    /// <summary>Blank mapping rows never persist; process/device names save trimmed; the ACP
    /// list is de-duplicated. On the mixer side a channel the panel does not have (observer
    /// loudspeaker) cannot be reached from the editor; it only survives a hand edit — drop it
    /// rather than subscribe a missing ref — and the panel strip/bus lists save sorted, once each.</summary>
    public static void BeforeSave(AudioOptions audio, MixerOptions mixer)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(mixer);

        audio.AppMappings.RemoveAll(m => string.IsNullOrWhiteSpace(m.Binary));
        foreach (var mapping in audio.AppMappings)
        {
            mapping.Binary = mapping.Binary.Trim();
            mapping.Device = mapping.Device.Trim();
        }

        audio.DeviceBlacklist.RemoveAll(string.IsNullOrWhiteSpace);
        for (var i = 0; i < audio.DeviceBlacklist.Count; i++)
        {
            audio.DeviceBlacklist[i] = audio.DeviceBlacklist[i].Trim();
        }

        audio.ActiveAcps = audio.ActiveAcps.Distinct().ToList();

        mixer.Mappings.RemoveAll(m => !m.Channel.ExistsOn(m.Acp));
        mixer.PanelStrips = mixer.PanelStrips.Distinct().OrderBy(i => i).ToList();
        mixer.PanelBuses = mixer.PanelBuses.Distinct().OrderBy(i => i).ToList();
    }

    /// <summary>The canonical camelCase name of an ACP in the mapping dictionary.</summary>
    public static string AcpName(AcpSide acp) => JsonNamingPolicy.CamelCase.ConvertName(acp.ToString());

    /// <summary>Normalises the dictionary's names to the canonical camelCase ACP names so the
    /// editor and the save path address the same buckets regardless of hand-edited casing.</summary>
    public static void NormalizeVoiceMeeterAcpNames(AudioOptions audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var normalized = new Dictionary<string, List<VoiceMeeterTargetMapping>>();
        foreach (var (name, mappings) in audio.VoiceMeeterMappings)
        {
            if (Enum.TryParse<AcpSide>(name, ignoreCase: true, out var acp))
            {
                normalized[AcpName(acp)] = mappings;
            }
        }

        audio.VoiceMeeterMappings = normalized;
    }

    /// <summary>"1, 2, 4" (as Voicemeeter numbers them) → [0, 1, 3]; junk is ignored.</summary>
    public static List<int> ParseOneBasedList(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n - 1 : -1)
            .Where(n => n >= 0 && n < 64)
            .Distinct()
            .OrderBy(n => n)
            .ToList();
    }

    /// <summary>[0, 1, 3] → "1, 2, 4", the way Voicemeeter numbers them.</summary>
    public static string OneBasedText(IEnumerable<int> indices)
    {
        ArgumentNullException.ThrowIfNull(indices);
        return string.Join(", ", indices.Select(i => (i + 1).ToString(CultureInfo.InvariantCulture)));
    }
}
