using System.Globalization;
using System.Text.Json;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Web.Settings;

/// <summary>
/// The Audio Control page's own rules (candidate C3): list pruning on save, the per-ACP mapping
/// dictionary's name normalisation, the one-based strip/bus text bridges of the remote
/// mixer panel, and — since #178/#179 — the agent list's tidy-up, the legacy single-agent
/// lift and the shared-target check both mapping editors show. Plain C# so xunit reaches
/// it; a per-property trim is declared on the property.
/// </summary>
public static class AudioPageRules
{
    /// <summary>Blank mapping rows never persist; process/device names save trimmed; the ACP
    /// list is de-duplicated. On the mixer side a channel the panel does not have (observer
    /// loudspeaker) cannot be reached from the editor; it only survives a hand edit — drop it
    /// rather than subscribe a missing ref — the panel strip/bus lists save sorted, once each,
    /// and the agent list gets <see cref="TidyAgents"/>.</summary>
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
        TidyAgents(mixer);
    }

    /// <summary>
    /// Agent names save trimmed; a blank one becomes "Mixer PC n"; a duplicate gets a " (2)"
    /// suffix — the name is the mapping rows' only link to the agent, so two agents must never
    /// share one (rows that named the duplicate stay with the first agent of that name).
    /// Panel lists sort, once each. A mapping whose agent names no block is kept
    /// as it is: the Live column says <c>no_agent</c>, and silently re-pointing a row to another
    /// PC would move a knob to the wrong headset. Mapping names are trimmed to match.
    /// </summary>
    public static void TidyAgents(MixerOptions mixer)
    {
        ArgumentNullException.ThrowIfNull(mixer);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < mixer.Agents.Count; i++)
        {
            var agent = mixer.Agents[i];
            var name = agent.Name.Trim();
            if (name.Length == 0)
            {
                name = $"Mixer PC {i + 1}";
            }

            var candidate = name;
            for (var n = 2; !seen.Add(candidate); n++)
            {
                candidate = $"{name} ({n})";
            }

            // No RenameAgent here: a trim changes nothing a row can see (names compare
            // trimmed), a blank-fill names an agent rows address as "" (= the first), and a
            // duplicate's suffix must leave the rows with the first agent of that name.
            agent.Name = candidate;

            agent.PanelStrips = agent.PanelStrips.Distinct().OrderBy(x => x).ToList();
            agent.PanelBuses = agent.PanelBuses.Distinct().OrderBy(x => x).ToList();
        }

        foreach (var mapping in mixer.Mappings)
        {
            mapping.Agent = mapping.Agent.Trim();
        }
    }

    /// <summary>
    /// Lifts the pre-#178 single agent (<c>host</c>/<c>port</c>/<c>token</c> + the section-level
    /// panel lists) into <c>agents[0]</c> and clears the legacy fields, so the page shows one
    /// block and the next Save writes the new shape (wake-on-LAN precedent: the page moves the
    /// old value on its first visit). Rows keep an empty agent name, which means "the first
    /// agent". Returns true when something moved — the page then marks the draft dirty.
    /// </summary>
    public static bool MigrateLegacyAgent(MixerOptions mixer)
    {
        ArgumentNullException.ThrowIfNull(mixer);
        if (!mixer.HasLegacyAgent)
        {
            return false;
        }

        mixer.Agents.Add(new MixerAgentOptions
        {
            Name = MixerAgentOptions.LegacyName,
            Enabled = true,
            Host = mixer.Host.Trim(),
            Port = mixer.Port,
            Token = mixer.Token,
            PanelStrips = mixer.PanelStrips.Distinct().OrderBy(i => i).ToList(),
            PanelBuses = mixer.PanelBuses.Distinct().OrderBy(i => i).ToList(),
        });
        mixer.Host = "";
        mixer.Port = 5088;
        mixer.Token = "";
        mixer.PanelStrips = [];
        mixer.PanelBuses = [];
        return true;
    }

    /// <summary>Re-points every mapping row that named <paramref name="oldName"/> (case-
    /// insensitively) to <paramref name="newName"/>. Rows with an empty name mean "the first
    /// agent" and are left alone. Returns how many rows moved.</summary>
    public static int RenameAgent(MixerOptions mixer, string oldName, string newName)
    {
        ArgumentNullException.ThrowIfNull(mixer);
        ArgumentNullException.ThrowIfNull(oldName);
        ArgumentNullException.ThrowIfNull(newName);

        if (string.IsNullOrWhiteSpace(oldName))
        {
            return 0;
        }

        var moved = 0;
        foreach (var mapping in mixer.Mappings)
        {
            if (!string.IsNullOrWhiteSpace(mapping.Agent) && MixerAgentOptions.SameName(mapping.Agent, oldName))
            {
                mapping.Agent = newName;
                moved++;
            }
        }

        return moved;
    }

    /// <summary>One mapping row as the shared-target check sees it: which PC (empty for the
    /// local VoiceMeeter backend), which strip/bus, and the words that name the row to the
    /// pilot ("Captain VHF1" — the page supplies them localised).</summary>
    public sealed record TargetRow(string Agent, bool IsBus, int Index, string Owner);

    /// <summary>A strip or bus two or more rows drive, with the rows that drive it.</summary>
    public sealed record SharedTarget(string Agent, bool IsBus, int Index, IReadOnlyList<string> Owners);

    /// <summary>
    /// The targets that more than one row drives (#179): two knobs on one strip means the last
    /// knob to move wins, and two headsets on one bus cannot have two volumes. Agent names
    /// compare as <see cref="MixerAgentOptions.SameName"/>; the local backend passes "" for
    /// every row. Order follows the first row that named each target; owners keep row order.
    /// </summary>
    public static IReadOnlyList<SharedTarget> SharedTargets(IEnumerable<TargetRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var groups = new List<(string Agent, bool IsBus, int Index, List<string> Owners)>();
        foreach (var row in rows)
        {
            var agent = (row.Agent ?? "").Trim();
            var slot = groups.FindIndex(g => g.IsBus == row.IsBus && g.Index == row.Index && MixerAgentOptions.SameName(g.Agent, agent));
            if (slot < 0)
            {
                groups.Add((agent, row.IsBus, row.Index, [row.Owner]));
            }
            else
            {
                groups[slot].Owners.Add(row.Owner);
            }
        }

        return groups
            .Where(g => g.Owners.Count > 1)
            .Select(g => new SharedTarget(g.Agent, g.IsBus, g.Index, g.Owners))
            .ToList();
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
