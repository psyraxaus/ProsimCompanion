namespace ProsimCompanion.Core.Checklists;

/// <summary>
/// Pure checklist state machine implementing the visual engine's three semantics:
/// <list type="bullet">
/// <item><b>Gating</b> — items complete strictly in order; the active line is always the first
/// item that is neither Done nor Skipped. An auto (dataref-verified) item completes itself the
/// moment it is active and its condition holds; a manual item waits for a tick.</item>
/// <item><b>Retreat</b> — a completed auto item whose condition regresses before the checklist
/// completes un-checks (back to Pending) and pulls the active line back to it. Items completed
/// after it keep their state (only the regressed line reopens).</item>
/// <item><b>Freeze</b> — per-item <c>freeze: true</c> opts out of retreat; manual items always
/// freeze; a COMPLETED checklist freezes wholesale (post-completion regressions are the next
/// checklist's business, not a reopened old one).</item>
/// </list>
/// Not thread-safe — the owning service serializes access.
/// </summary>
public sealed class ChecklistRunner
{
    private readonly ChecklistDefinition _definition;
    private readonly ChecklistItemStatus[] _statuses;
    private readonly bool[] _conditionSatisfied;
    private readonly bool[] _voiceFrozen;

    public ChecklistRunner(ChecklistDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _definition = definition;
        _statuses = new ChecklistItemStatus[definition.Items.Count];
        _conditionSatisfied = new bool[definition.Items.Count];
        _voiceFrozen = new bool[definition.Items.Count];
    }

    public string Name => _definition.Checklist;

    public bool IsComplete { get; private set; }

    /// <summary>Index of the active line; item count when complete.</summary>
    public int ActiveIndex { get; private set; }

    /// <summary>Re-evaluates conditions against live values. Returns true when anything
    /// user-visible changed.</summary>
    public bool Evaluate(Func<string, double> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        var changed = false;

        // Live condition states drive the UI hint even where no status changes.
        for (var i = 0; i < _definition.Items.Count; i++)
        {
            var item = _definition.Items[i];
            var satisfied = item.IsAuto && ConditionEvaluator.Evaluate(item.Verify!, read);
            if (satisfied != _conditionSatisfied[i])
            {
                _conditionSatisfied[i] = satisfied;
                changed = true;
            }
        }

        if (IsComplete)
        {
            return changed; // frozen wholesale
        }

        // Retreat pass: a done, unfrozen auto item whose condition regressed reopens.
        for (var i = 0; i < _definition.Items.Count; i++)
        {
            var item = _definition.Items[i];
            if (_statuses[i] == ChecklistItemStatus.Done && item.IsAuto && !item.Freeze
                && !_voiceFrozen[i] && !_conditionSatisfied[i])
            {
                _statuses[i] = ChecklistItemStatus.Pending;
                changed = true;
            }
        }

        // Gating pass: advance the active line, cascading through satisfied auto items.
        while (true)
        {
            var active = FirstOpenIndex();
            if (active >= _definition.Items.Count)
            {
                IsComplete = true;
                ActiveIndex = _definition.Items.Count;
                return true;
            }

            if (ActiveIndex != active)
            {
                ActiveIndex = active;
                changed = true;
            }

            var item = _definition.Items[active];
            if (item.IsDisplayOnly)
            {
                // Separator/note rows (Prosim2GSX set files) are display furniture: complete
                // them the instant they become active so they never gate the real items.
                _statuses[active] = ChecklistItemStatus.Done;
                changed = true;
                continue;
            }

            if (item.IsAuto && _conditionSatisfied[active])
            {
                _statuses[active] = ChecklistItemStatus.Done;
                changed = true;
                continue; // cascade: the next line may already be satisfied too
            }

            if (_statuses[active] != ChecklistItemStatus.Active)
            {
                _statuses[active] = ChecklistItemStatus.Active;
                changed = true;
            }
            return changed;
        }
    }

    /// <summary>Manually ticks the active line. Auto items cannot be hand-ticked — satisfy the
    /// condition or skip (the predecessor's rule, kept so a checklist can't lie).</summary>
    public bool Check(int index)
    {
        if (IsComplete || index != ActiveIndex || index >= _definition.Items.Count
            || _definition.Items[index].IsAuto)
        {
            return false;
        }
        _statuses[index] = ChecklistItemStatus.Done;
        return true;
    }

    /// <summary>Ticks the active line even when it is auto — the settings-gated manual
    /// override (Prosim2GSX's AllowManualChecklistOverride parity). The line freezes with the
    /// same latch as <see cref="VoiceComplete"/> so the retreat pass cannot immediately
    /// un-check what the crew deliberately overrode. Still active-line gated: the override
    /// relaxes WHO may tick, not the in-order discipline.</summary>
    public bool ForceCheck(int index)
    {
        if (IsComplete || index != ActiveIndex || index >= _definition.Items.Count)
        {
            return false;
        }
        _statuses[index] = ChecklistItemStatus.Done;
        _voiceFrozen[index] = true;
        return true;
    }

    /// <summary>Skips the active line (auto or manual).</summary>
    public bool Skip(int index)
    {
        if (IsComplete || index != ActiveIndex || index >= _definition.Items.Count)
        {
            return false;
        }
        _statuses[index] = ChecklistItemStatus.Skipped;
        return true;
    }

    /// <summary>Completes a line on the voice First Officer's authority. Unlike
    /// <see cref="Check"/> this may complete auto items — the FO already verified them by
    /// readback or dataref — and is by-index rather than active-line-gated, because the spoken
    /// run advances strictly linearly while the visual gating may sit behind on an unsatisfied
    /// auto item. The line voice-freezes so the retreat pass never un-checks something the crew
    /// already read out loud.</summary>
    public bool VoiceComplete(int index)
    {
        if (IsComplete || index >= _definition.Items.Count
            || _statuses[index] is ChecklistItemStatus.Done or ChecklistItemStatus.Skipped)
        {
            return false;
        }
        _statuses[index] = ChecklistItemStatus.Done;
        _voiceFrozen[index] = true;
        return true;
    }

    /// <summary>Skips a line on the voice First Officer's authority (by-index, see
    /// <see cref="VoiceComplete"/>).</summary>
    public bool VoiceSkip(int index)
    {
        if (IsComplete || index >= _definition.Items.Count
            || _statuses[index] is ChecklistItemStatus.Done or ChecklistItemStatus.Skipped)
        {
            return false;
        }
        _statuses[index] = ChecklistItemStatus.Skipped;
        _voiceFrozen[index] = true;
        return true;
    }

    public void Restart()
    {
        Array.Fill(_statuses, ChecklistItemStatus.Pending);
        Array.Fill(_voiceFrozen, false);
        IsComplete = false;
        ActiveIndex = 0;
    }

    public ChecklistView Snapshot()
        => new(
            Name,
            IsComplete,
            ActiveIndex,
            [.. _definition.Items.Select((item, i) => new ChecklistItemView(
                item.Say,
                item.ExpectedResponse ?? "",
                _statuses[i],
                item.IsAuto,
                _conditionSatisfied[i],
                item.Kind))]);

    private int FirstOpenIndex()
    {
        for (var i = 0; i < _statuses.Length; i++)
        {
            if (_statuses[i] is not (ChecklistItemStatus.Done or ChecklistItemStatus.Skipped))
            {
                return i;
            }
        }
        return _statuses.Length;
    }
}
