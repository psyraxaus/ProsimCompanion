namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Cruise small talk (issue #195, <c>smallTalk</c>): now and then in the cruise the First
/// Officer says something that is not about the flight — offers a coffee, remarks on the view,
/// asks about the layover, talks about themselves. Unprompted, Low priority, sterile-cockpit
/// aware, never while anything is open. The persona's chattiness is the actual count; the
/// figure here is the ceiling. Off by default.
/// </summary>
public sealed class SmallTalkOptions : IOptionSection
{
    public static string SectionName => "smallTalk";

    /// <summary>Master switch. Off: one log line at startup and nothing else.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.smallTalk.enabled", Label = "speechSettings.common.enabled")]
    public bool Enabled { get; set; }

    /// <summary>The ceiling per flight. The persona's chattiness (0–3) is the actual count —
    /// chattiness 1 means one line even with this at 3; "quiet please" makes it 0.</summary>
    [OptionField(FieldKind.Number, "speechSettings.smallTalk.maxPerFlight", Min = "0", Max = "10", DependsOn = "Enabled")]
    public int MaxPerFlight { get; set; } = 3;

    /// <summary>Minutes of FO silence before a line may come, and the gap after the cruise
    /// begins before the first one.</summary>
    [OptionField(FieldKind.Number, "speechSettings.smallTalk.minQuietMinutes", Suffix = "min", Min = "1", Max = "120", DependsOn = "Enabled")]
    public int MinQuietMinutes { get; set; } = 8;

    /// <summary>Which kinds of line may come (the sequence per flight is drawn from the ticked
    /// ones without repeats).</summary>
    public SmallTalkKindsOptions Kinds { get; set; } = new();
}

/// <summary>The four kinds of cruise small talk, each its own tick box (issue #195).</summary>
public sealed class SmallTalkKindsOptions
{
    /// <summary>"Coffee, captain?" — a captain's yes/no; yes has the purser send something up.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.smallTalk.kinds.offer", DependsOn = "../Enabled")]
    public bool Offer { get; set; } = true;

    /// <summary>Something about what is outside: a town below, the sun going down, the cloud deck.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.smallTalk.kinds.view", DependsOn = "../Enabled")]
    public bool View { get; set; } = true;

    /// <summary>The destination: "do you know {city}?" — a free-text reply the FO follows up once.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.smallTalk.kinds.layover", DependsOn = "../Enabled")]
    public bool Layover { get; set; } = true;

    /// <summary>The FO talks about themselves from the roster record (base, hours) — needs the roster.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.smallTalk.kinds.crew", DependsOn = "../Enabled")]
    public bool Crew { get; set; } = true;
}
