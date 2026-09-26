using ProsimCompanion.Core.Checklists;

namespace ProsimCompanion.Speech.VoiceReference;

/// <summary>The checklist definitions the voice FO matches start phrases against — a seam
/// so the Voice Reference composer is testable without the file-backed checklist service.</summary>
public interface IChecklistDefinitionSource
{
    /// <summary>The named set's definitions (empty for an unknown set).</summary>
    IReadOnlyList<ChecklistDefinition> Definitions(string set);
}

/// <summary>Production adapter over <see cref="ChecklistService"/>.</summary>
internal sealed class ChecklistDefinitionSource : IChecklistDefinitionSource
{
    private readonly ChecklistService _checklists;

    public ChecklistDefinitionSource(ChecklistService checklists)
    {
        ArgumentNullException.ThrowIfNull(checklists);
        _checklists = checklists;
    }

    public IReadOnlyList<ChecklistDefinition> Definitions(string set) => _checklists.Definitions(set);
}
