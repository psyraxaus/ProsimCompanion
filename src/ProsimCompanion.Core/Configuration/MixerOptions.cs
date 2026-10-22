namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Settings for the remote mixer client (<c>mixer</c>, 2026-10-10): a VoicemeeterBridge agent
/// on another PC exposes that PC's Voicemeeter over a WebSocket (protocol v1,
/// <c>claude/mixer-protocol.md</c>). Off by default; while off the client opens no socket and
/// the mapping layer subscribes nothing, so the feature costs nothing at startup. The local
/// VoiceMeeter backend of the audio pillar is a different thing: it drives the DLL on THIS PC.
/// Since #178 (2026-10-22) the section holds a list of agents — a two-pilot rig runs one
/// VoicemeeterBridge per pilot PC — and every mapping names the agent it drives.
/// </summary>
public sealed class MixerOptions : IOptionSection
{
    public static string SectionName => "mixer";

    /// <summary>Master switch. Turning it on in the web UI starts the clients without a restart.</summary>
    [OptionField(FieldKind.Bool, "audioSettings.mixer.enable")]
    public bool Enabled { get; set; }

    /// <summary>LEGACY single agent (pre-#178): host name or IP of the PC running
    /// VoicemeeterBridge. Still honoured while <see cref="Agents"/> is empty — see
    /// <see cref="EffectiveAgents"/>; the Audio page moves it into the list on its first
    /// visit (wake-on-LAN precedent). Remove with the release after 0.8.</summary>
    [OptionField(FieldKind.Text, "audioSettings.mixer.host", TextKind = TextFieldKind.Host, Placeholder = "192.168.1.20", Normalize = FieldNormalization.Trim)]
    public string Host { get; set; } = "";

    /// <summary>LEGACY single agent: listening port (the agent's default is 5088).</summary>
    [OptionField(FieldKind.Number, "audioSettings.mixer.port", Min = "1", Max = "65535")]
    public int Port { get; set; } = 5088;

    /// <summary>LEGACY single agent: shared secret. DPAPI-protected on disk
    /// (<see cref="SecretProtector.SecretPaths"/>, <c>mixer:token</c>) and never logged.</summary>
    [OptionField(FieldKind.Text, "audioSettings.mixer.token", Password = true, Normalize = FieldNormalization.Trim)]
    public string Token { get; set; } = "";

    /// <summary>The VoicemeeterBridge agents, one per PC whose Voicemeeter the cockpit
    /// drives (#178). Empty = fall back to the legacy single-agent fields.</summary>
    public List<MixerAgentOptions> Agents { get; set; } = [];

    /// <summary>First reconnect delay after a drop or a refused connection (every agent).</summary>
    [OptionField(FieldKind.Number, "audioSettings.mixer.reconnectDelay", Suffix = "ms", Min = "500", Advanced = true)]
    public int ReconnectDelayMs { get; set; } = 3000;

    /// <summary>Ceiling of the doubling reconnect backoff (an agent that is off for the
    /// whole flight is retried at this cadence, not every three seconds).</summary>
    [OptionField(FieldKind.Number, "audioSettings.mixer.reconnectMax", Suffix = "ms", Min = "1000", Advanced = true)]
    public int ReconnectMaxDelayMs { get; set; } = 30_000;

    /// <summary>How long a <c>set</c> waits for its <c>result</c> before reporting a timeout.</summary>
    [OptionField(FieldKind.Number, "audioSettings.mixer.writeTimeout", Suffix = "ms", Min = "250", Advanced = true)]
    public int SetTimeoutMs { get; set; } = 3000;

    /// <summary>LEGACY single agent: strip indices (0-based) the mixer status panel shows.
    /// Moved into <see cref="MixerAgentOptions.PanelStrips"/> with the host.</summary>
    public List<int> PanelStrips { get; set; } = [];

    /// <summary>LEGACY single agent: bus indices (0-based) the mixer status panel shows.</summary>
    public List<int> PanelBuses { get; set; } = [];

    /// <summary>Gain sent with the knob fully down. −60 dB is as good as silent.</summary>
    [OptionField(FieldKind.Number, "audioSettings.mixer.gainMin", Suffix = "dB", Step = "0.5", Advanced = true)]
    public double GainMinDb { get; set; } = -60;

    /// <summary>Gain sent with the knob fully up. +12 dB mirrors the local VoiceMeeter backend
    /// (past unity; 0 dB sits near 83 % of the knob). Set 0 for a strip that must never go
    /// past unity.</summary>
    [OptionField(FieldKind.Number, "audioSettings.mixer.gainMax", Suffix = "dB", Step = "0.5", Advanced = true)]
    public double GainMaxDb { get; set; } = 12;

    /// <summary>Audio-panel channel → strip/bus bindings, one row per channel. Empty by default.</summary>
    public List<MixerMapping> Mappings { get; set; } = [];

    /// <summary>True while the pre-#178 fields still carry a connection the list does not.</summary>
    public bool HasLegacyAgent => Agents.Count == 0 && !string.IsNullOrWhiteSpace(Host);

    /// <summary>
    /// The agents to run: <see cref="Agents"/> when the list has entries, else one agent built
    /// from the legacy single-agent fields (name <see cref="MixerAgentOptions.LegacyName"/>),
    /// else nothing. Every consumer reads the agents through this, so a settings.json written
    /// before #178 keeps working unchanged.
    /// </summary>
    public IReadOnlyList<MixerAgentOptions> EffectiveAgents()
    {
        if (Agents.Count > 0)
        {
            return Agents;
        }

        if (string.IsNullOrWhiteSpace(Host))
        {
            return [];
        }

        return
        [
            new MixerAgentOptions
            {
                Name = MixerAgentOptions.LegacyName,
                Enabled = true,
                Host = Host,
                Port = Port,
                Token = Token,
                PanelStrips = [.. PanelStrips],
                PanelBuses = [.. PanelBuses],
            },
        ];
    }

    /// <summary>The agent a mapping names: an empty name means the first agent (every mapping
    /// written before #178 has no name), otherwise an exact, case-insensitive match on
    /// <see cref="MixerAgentOptions.Name"/>. Null when nothing matches.</summary>
    public MixerAgentOptions? FindAgent(string? name)
    {
        var agents = EffectiveAgents();
        if (string.IsNullOrWhiteSpace(name))
        {
            return agents.Count > 0 ? agents[0] : null;
        }

        return agents.FirstOrDefault(a => MixerAgentOptions.SameName(a.Name, name));
    }
}

/// <summary>
/// One VoicemeeterBridge agent (#178): the PC it runs on and the strips/buses of that PC's
/// Voicemeeter the status card shows. Named, because a mapping row must say which PC's
/// strip it drives and a name survives reordering where an index does not.
/// </summary>
public sealed class MixerAgentOptions
{
    /// <summary>Name the legacy single agent gets when it is lifted into the list.</summary>
    public const string LegacyName = "Mixer PC";

    /// <summary>Your label for the PC ("PC1", "Captain PC"). Mapping rows and the status
    /// card use it; renaming it on the page re-points the rows.</summary>
    [OptionField(FieldKind.Text, "audioSettings.mixer.agent.name", Normalize = FieldNormalization.Trim)]
    public string Name { get; set; } = "";

    /// <summary>Off keeps the block without opening a socket; its mappings read <c>no_agent</c>.</summary>
    [OptionField(FieldKind.Bool, "audioSettings.mixer.agent.enable")]
    public bool Enabled { get; set; } = true;

    /// <summary>Host name or IP of the PC running VoicemeeterBridge. Empty = not configured.</summary>
    [OptionField(FieldKind.Text, "audioSettings.mixer.agent.host", TextKind = TextFieldKind.Host, Placeholder = "192.168.1.20", Normalize = FieldNormalization.Trim)]
    public string Host { get; set; } = "";

    /// <summary>Agent listening port (the agent's default is 5088).</summary>
    [OptionField(FieldKind.Number, "audioSettings.mixer.agent.port", Min = "1", Max = "65535")]
    public int Port { get; set; } = 5088;

    /// <summary>Shared secret from the agent's tray menu ("Copy token"). DPAPI-protected on
    /// disk (<see cref="SecretProtector.SecretPaths"/>, <c>mixer:agents:*:token</c>) and
    /// never logged or wire-traced.</summary>
    [OptionField(FieldKind.Text, "audioSettings.mixer.agent.token", Password = true, Normalize = FieldNormalization.Trim)]
    public string Token { get; set; } = "";

    /// <summary>Strip indices (0-based) of this agent's Voicemeeter the status panel shows and watches.</summary>
    public List<int> PanelStrips { get; set; } = [];

    /// <summary>Bus indices (0-based) of this agent's Voicemeeter the status panel shows and watches.</summary>
    public List<int> PanelBuses { get; set; } = [];

    /// <summary>Names compare trimmed and case-insensitively — a hand-edited mapping must
    /// not miss its agent over casing.</summary>
    public static bool SameName(string? a, string? b) =>
        string.Equals(CleanName(a), CleanName(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>The name as the clients, the log and the roster carry it: trimmed, and with
    /// any line break a hand edit put in replaced by a space (a name reaches the log on
    /// every connection line — no forged lines from settings.json).</summary>
    public static string CleanName(string? name) =>
        (name ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
}

/// <summary>
/// One audio-panel channel bound to one strip or bus on one mixer PC, shaped like the local
/// <see cref="VoiceMeeterTargetMapping"/> (owner request 2026-10-10: same editor as the
/// VoiceMeeter page, a Latch tick instead of separate mute rows). The knob drives the
/// target's Gain over <see cref="MixerOptions.GainMinDb"/>…<see cref="MixerOptions.GainMaxDb"/>;
/// with <see cref="UseLatch"/> the REC push-button drives its Mute (the loudspeaker dial,
/// which has no button, mutes fully down).
/// </summary>
public sealed class MixerMapping
{
    /// <summary>Off keeps the row without subscribing its knob.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Which audio panel the channel is read from.</summary>
    public AcpSide Acp { get; set; } = AcpSide.Captain;

    public AudioChannel Channel { get; set; } = AudioChannel.Vhf1;

    /// <summary>The <see cref="MixerAgentOptions.Name"/> of the PC whose strip this row drives
    /// (#178). Empty = the first agent, so rows saved before #178 keep working.</summary>
    public string Agent { get; set; } = "";

    /// <summary>0-based strip/bus index as the Remote API counts (UI shows the mixer PC's names).</summary>
    public int StripIndex { get; set; }

    /// <summary>Target a bus instead of a strip.</summary>
    public bool IsBus { get; set; }

    /// <summary>Drive the target's Mute from the channel's REC push-button.</summary>
    public bool UseLatch { get; set; } = true;
}
