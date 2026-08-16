namespace ProsimCompanion.Core.Configuration;

/// <summary>Visual checklist behaviour (settings section "checklists").</summary>
public sealed class ChecklistOptions : IOptionSection
{
    public static string SectionName => "checklists";

    /// <summary>Lets the crew hand-tick dataref-verified (auto) checklist lines on the web
    /// page — Prosim2GSX parity (its AllowManualChecklistOverride). Default false because the
    /// strict rule is the point of auto items: a checklist that can be ticked without the
    /// cockpit actually being set can lie. The override exists for hardware cockpits where a
    /// dataref occasionally disagrees with a physically-correct switch, and the crew needs to
    /// move on. Overridden lines freeze so the engine never un-ticks the crew's decision.</summary>
    public bool AllowManualOverride { get; set; }
}
