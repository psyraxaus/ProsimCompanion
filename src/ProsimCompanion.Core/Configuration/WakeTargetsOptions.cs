namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Wake-on-LAN for every PC the app depends on (<c>wakeOnLan</c>, owner idea 2026-10-08):
/// the language-model box, the speech server, an ActiveSky PC — as many as the pilot wants.
/// Each target gets one magic packet at startup, and "Send now" on the Setup page sends one
/// on demand. Replaces the single <c>briefing.llmWakeOnLan</c> block, which is still honoured
/// at startup until the Setup page migrates it into this list (one-way, on first visit).
/// Targets are a top-level list — the binder list-append fix covers it.
/// </summary>
public sealed class WakeTargetsOptions : IOptionSection
{
    public static string SectionName => "wakeOnLan";

    /// <summary>The PCs to wake. Empty by default.</summary>
    public List<WakeTarget> Targets { get; set; } = [];
}

/// <summary>One PC to wake. Prerequisites the app cannot satisfy: WoL enabled in the target's
/// BIOS/UEFI and NIC driver, wired Ethernet on this subnet (directed broadcasts do not cross
/// routers; WoL over WiFi is unreliable).</summary>
public sealed class WakeTarget
{
    /// <summary>Your label for it ("LLM box", "Mac mini") — what the log and the page show.</summary>
    public string Name { get; set; } = "";

    /// <summary>Send the packet at startup. Off keeps the row for "Send now" only.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Target NIC MAC address; ':' or '-' separators (or none).</summary>
    public string MacAddress { get; set; } = "";

    /// <summary>"255.255.255.255" or the subnet broadcast, e.g. "192.168.1.255".</summary>
    public string BroadcastAddress { get; set; } = "255.255.255.255";

    /// <summary>UDP port for the magic packet (conventionally 9, the discard port).</summary>
    public int Port { get; set; } = 9;
}
