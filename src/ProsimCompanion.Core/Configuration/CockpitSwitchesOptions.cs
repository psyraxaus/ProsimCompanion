namespace ProsimCompanion.Core.Configuration;

/// <summary>How a cockpit-switch key is drawn on the Stream Deck.</summary>
public enum CockpitSwitchLook
{
    /// <summary>Airbus Korry push button with two lamps (upper FAULT/AVAIL, lower ON/OFF).</summary>
    Korry2,

    /// <summary>Korry push button with one lamp.</summary>
    Korry1,

    /// <summary>Two-position toggle switch (lever up / down).</summary>
    Toggle,

    /// <summary>Rotary selector; the face shows the position labels around the knob.</summary>
    Knob,

    /// <summary>Guarded switch: a red guard over a toggle.</summary>
    Guarded,
}

/// <summary>What one key press does to the switch's dataref.</summary>
public enum CockpitPressMode
{
    /// <summary>Two-state switch: write the other value (first ↔ second catalogue value).</summary>
    Toggle,

    /// <summary>Momentary push button: press 1 → hold → 0 through the serialised press worker.</summary>
    Momentary,

    /// <summary>Multi-position selector: short press = next value, long press = previous, wrapping.</summary>
    Cycle,
}

/// <summary>
/// Settings for the cockpit-switch relay (<c>cockpitSwitches</c>, 2026-10-24, owner option A):
/// Stream Deck keys on any PC that press ProSim cockpit switches the pilot has no hardware for,
/// drawn as Korry buttons, toggles and knobs. Each row names one <c>system.switches.S_*</c>
/// dataref from ProSim's own switch catalogue; the app writes it directly over the SDK — no
/// FSUIPC, no offsets, no Lua in between (the predecessor path for the owner's three leak
/// measurement valves).
/// <para>
/// Write safety: the code-level allow-list (<c>ProsimWriteGate</c>) admits a switch only while
/// it is a row here AND exists in the catalogue (shipped CSV or the live ProSim list), and only
/// in the <c>system.switches.S_</c> family — never a circuit breaker, never a free-typed name
/// over the network. Decision record: ADR-0015.
/// </para>
/// </summary>
public sealed class CockpitSwitchesOptions : IOptionSection
{
    public static string SectionName => "cockpitSwitches";

    /// <summary>Master switch. On by default — nothing happens until a row exists, and every
    /// press still needs the command API (off by default) to reach the app.</summary>
    [OptionField(FieldKind.Bool, "cockpit.enable")]
    public bool Enabled { get; set; } = true;

    /// <summary>The switches offered to the Stream Deck keys. Edited on Settings → Cockpit Switches.</summary>
    public List<CockpitSwitchRow> Rows { get; set; } = [];
}

/// <summary>One cockpit switch a key may press and show.</summary>
public sealed class CockpitSwitchRow
{
    /// <summary>Stable id a key refers to (<c>cockpit.press {"id":"oh_hyd_lmv_blue"}</c>);
    /// letters, digits and underscores. Filled from the dataref on pick, editable.</summary>
    [OptionField(FieldKind.Text, "cockpit.row.id", Normalize = FieldNormalization.Trim)]
    public string Id { get; set; } = "";

    /// <summary>The title on the key face ("LEAK MEAS B").</summary>
    [OptionField(FieldKind.Text, "cockpit.row.name", Normalize = FieldNormalization.Trim)]
    public string Name { get; set; } = "";

    /// <summary>The <c>system.switches.S_*</c> dataref, picked from the catalogue on the page.</summary>
    public string DataRef { get; set; } = "";

    [OptionField(FieldKind.Select, "cockpit.row.look", Choices = "cockpitLook")]
    public CockpitSwitchLook Look { get; set; } = CockpitSwitchLook.Korry2;

    [OptionField(FieldKind.Select, "cockpit.row.press", Choices = "cockpitPress")]
    public CockpitPressMode PressMode { get; set; } = CockpitPressMode.Toggle;

    /// <summary>Upper lamp legend (Korry2) — "FAULT", "AVAIL".</summary>
    [OptionField(FieldKind.Text, "cockpit.row.upperLegend", Normalize = FieldNormalization.Trim)]
    public string UpperLegend { get; set; } = "FAULT";

    /// <summary>Lower lamp legend (Korry2 / Korry1) — "OFF", "ON".</summary>
    [OptionField(FieldKind.Text, "cockpit.row.lowerLegend", Normalize = FieldNormalization.Trim)]
    public string LowerLegend { get; set; } = "OFF";

    /// <summary>Optional <c>system.indicators.I_*</c> dataref that lights the upper lamp
    /// (non-zero = lit). Empty = the upper lamp never lights.</summary>
    [OptionField(FieldKind.Text, "cockpit.row.upperIndicator", Normalize = FieldNormalization.Trim, Placeholder = "system.indicators.I_…")]
    public string UpperIndicator { get; set; } = "";

    /// <summary>Optional indicator dataref for the lower lamp. Empty = lit while the switch
    /// value is not zero (pushed / on).</summary>
    [OptionField(FieldKind.Text, "cockpit.row.lowerIndicator", Normalize = FieldNormalization.Trim, Placeholder = "system.indicators.I_…")]
    public string LowerIndicator { get; set; } = "";

    /// <summary>Show a legend as dim grey text while its lamp is dark (the default, the way a
    /// real Korry's engraved legend stays faintly readable) — or hide it completely so a dark
    /// lamp is truly extinguished and the word appears only when lit (owner's leak valves:
    /// nothing when Normal, ON when pushed).</summary>
    [OptionField(FieldKind.Bool, "cockpit.row.showUnlitLegend")]
    public bool ShowUnlitLegend { get; set; } = true;

    /// <summary>Upper lamp colour, hex. Amber by default (FAULT).</summary>
    public string UpperColor { get; set; } = "#ffb000";

    /// <summary>Lower lamp colour, hex. White by default; blue for a selected ON.</summary>
    public string LowerColor { get; set; } = "#f4f6f8";

    /// <summary>Button bezel colour, hex; empty = the plain grey bezel. The leak measurement
    /// valves use blue / green / yellow here for the hydraulic system.</summary>
    public string BezelColor { get; set; } = "";
}
