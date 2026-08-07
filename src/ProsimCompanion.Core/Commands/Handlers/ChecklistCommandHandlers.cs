using ProsimCompanion.Core.Checklists;

namespace ProsimCompanion.Core.Commands.Handlers;

/// <summary>Request DTO for <c>checklists.select</c>.</summary>
public sealed record ChecklistSelectRequest
{
    /// <summary>Checklist name as listed in the catalog (case-insensitive).</summary>
    public string? Name { get; init; }
}

/// <summary>Request DTO for <c>checklists.check</c> / <c>checklists.skip</c>. The index is
/// nullable so "field missing" (validation error) is distinguishable from index 0.</summary>
public sealed record ChecklistItemRequest
{
    /// <summary>Zero-based item index; must be the active line (items complete strictly in order).</summary>
    public int? Index { get; init; }
}

/// <summary>
/// <c>checklists.*</c> command handlers over <see cref="ChecklistService"/>. The decision logic
/// is split into pure <c>Evaluate*</c> functions over a <see cref="ChecklistView"/> snapshot so
/// the index/gating rules are unit-testable without the file-watching, timer-driven service.
/// Evaluate-then-mutate is safe here: the service serialises mutations internally, and a stale
/// verdict merely produces a no-op (the runner re-validates the active line itself).
/// </summary>
public static class ChecklistCommandHandlers
{
    public static void Register(CommandRegistry registry, ChecklistService? checklists)
    {
        ArgumentNullException.ThrowIfNull(registry);

        registry.Register<ChecklistSelectRequest, CommandResult>(
            "checklists.select",
            (request, _) => Task.FromResult(Select(checklists, request)));

        registry.Register<ChecklistItemRequest, CommandResult>(
            "checklists.check",
            (request, _) => Task.FromResult(Check(checklists, request)));

        registry.Register<ChecklistItemRequest, CommandResult>(
            "checklists.skip",
            (request, _) => Task.FromResult(Skip(checklists, request)));

        registry.Register<EmptyCommandRequest, CommandResult>(
            "checklists.restart",
            (_, _) => Task.FromResult(Restart(checklists)));

        registry.Register<EmptyCommandRequest, CommandResult>(
            "checklists.deselect",
            (_, _) => Task.FromResult(Deselect(checklists)));

        registry.Register<EmptyCommandRequest, CommandResult>(
            "checklists.advanceNext",
            (_, _) => Task.FromResult(AdvanceNext(checklists)));
    }

    // ── Pure decision helpers (public: unit-tested, and reusable by later UI surfaces) ──────

    /// <summary>
    /// Decides what <c>checklists.check</c> should do against a snapshot. Success means "go
    /// ahead and tick this line"; auto (dataref-verified) items are refused because a checklist
    /// must not lie — satisfy the condition in the cockpit or skip the line.
    /// </summary>
    /// <exception cref="CommandValidationException">Index missing or out of bounds.</exception>
    public static CommandResult EvaluateCheck(ChecklistView? view, int? index)
    {
        if (index is null)
        {
            throw new CommandValidationException("index is required, e.g. { \"index\": 0 }.");
        }

        if (view is null)
        {
            return CommandResult.PreconditionFailed("No checklist is selected.");
        }

        if (index < 0 || index >= view.Items.Count)
        {
            throw new CommandValidationException(
                $"index {index} is out of bounds — '{view.Name}' has {view.Items.Count} items (0..{view.Items.Count - 1}).");
        }

        if (view.IsComplete)
        {
            return CommandResult.AlreadySatisfied($"Checklist '{view.Name}' is already complete.");
        }

        if (index != view.ActiveIndex)
        {
            return CommandResult.PreconditionFailed(
                $"Item {index} is not the active line (active is {view.ActiveIndex}) — items complete strictly in order.");
        }

        if (view.Items[index.Value].IsAuto)
        {
            return CommandResult.PreconditionFailed(
                "The active line is dataref-verified and cannot be hand-ticked — satisfy the condition or skip it.");
        }

        return CommandResult.Ok($"Item {index} ('{view.Items[index.Value].Label}') checked.");
    }

    /// <summary>Decides what <c>checklists.skip</c> should do; unlike check, auto items may be
    /// skipped (that is the escape hatch for a condition that will not come true).</summary>
    /// <exception cref="CommandValidationException">Index missing or out of bounds.</exception>
    public static CommandResult EvaluateSkip(ChecklistView? view, int? index)
    {
        if (index is null)
        {
            throw new CommandValidationException("index is required, e.g. { \"index\": 0 }.");
        }

        if (view is null)
        {
            return CommandResult.PreconditionFailed("No checklist is selected.");
        }

        if (index < 0 || index >= view.Items.Count)
        {
            throw new CommandValidationException(
                $"index {index} is out of bounds — '{view.Name}' has {view.Items.Count} items (0..{view.Items.Count - 1}).");
        }

        if (view.IsComplete)
        {
            return CommandResult.AlreadySatisfied($"Checklist '{view.Name}' is already complete.");
        }

        if (index != view.ActiveIndex)
        {
            return CommandResult.PreconditionFailed(
                $"Item {index} is not the active line (active is {view.ActiveIndex}) — items complete strictly in order.");
        }

        return CommandResult.Ok($"Item {index} ('{view.Items[index.Value].Label}') skipped.");
    }

    /// <summary>Decides what <c>checklists.advanceNext</c> (check the active line, whatever its
    /// index — the one-button Stream Deck flow) should do.</summary>
    public static CommandResult EvaluateAdvanceNext(ChecklistView? view)
    {
        if (view is null)
        {
            return CommandResult.PreconditionFailed("No checklist is selected.");
        }

        if (view.IsComplete)
        {
            return CommandResult.AlreadySatisfied($"Checklist '{view.Name}' is already complete.");
        }

        return EvaluateCheck(view, view.ActiveIndex);
    }

    // ── Handlers ────────────────────────────────────────────────────────────────────────────

    private static CommandResult Select(ChecklistService? checklists, ChecklistSelectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (checklists is null)
        {
            return CommandResult.Unavailable("The checklist service is not running.");
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new CommandValidationException("name is required, e.g. { \"name\": \"Before Start\" }.");
        }

        var name = request.Name.Trim();
        var known = checklists.Catalog().Any(entry =>
            string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
        if (!known)
        {
            throw new CommandValidationException(
                $"Unknown checklist '{name}' — GET /api/commands lists commands; the checklist catalog is on the /checklists page.");
        }

        checklists.Select(name);
        return CommandResult.Ok($"Checklist '{name}' selected.");
    }

    private static CommandResult Check(ChecklistService? checklists, ChecklistItemRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (checklists is null)
        {
            return CommandResult.Unavailable("The checklist service is not running.");
        }

        var verdict = EvaluateCheck(checklists.ActiveView(), request.Index);
        if (verdict.Outcome == CommandOutcome.Success)
        {
            checklists.Check(request.Index!.Value);
        }

        return verdict;
    }

    private static CommandResult Skip(ChecklistService? checklists, ChecklistItemRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (checklists is null)
        {
            return CommandResult.Unavailable("The checklist service is not running.");
        }

        var verdict = EvaluateSkip(checklists.ActiveView(), request.Index);
        if (verdict.Outcome == CommandOutcome.Success)
        {
            checklists.Skip(request.Index!.Value);
        }

        return verdict;
    }

    private static CommandResult Restart(ChecklistService? checklists)
    {
        if (checklists is null)
        {
            return CommandResult.Unavailable("The checklist service is not running.");
        }

        var view = checklists.ActiveView();
        if (view is null)
        {
            return CommandResult.PreconditionFailed("No checklist is selected.");
        }

        checklists.Restart();
        return CommandResult.Ok($"Checklist '{view.Name}' restarted.");
    }

    private static CommandResult Deselect(ChecklistService? checklists)
    {
        if (checklists is null)
        {
            return CommandResult.Unavailable("The checklist service is not running.");
        }

        if (checklists.ActiveView() is null)
        {
            return CommandResult.AlreadySatisfied("No checklist is selected.");
        }

        checklists.Deselect();
        return CommandResult.Ok("Checklist deselected.");
    }

    private static CommandResult AdvanceNext(ChecklistService? checklists)
    {
        if (checklists is null)
        {
            return CommandResult.Unavailable("The checklist service is not running.");
        }

        var view = checklists.ActiveView();
        var verdict = EvaluateAdvanceNext(view);
        if (verdict.Outcome == CommandOutcome.Success)
        {
            checklists.Check(view!.ActiveIndex);
        }

        return verdict;
    }
}
