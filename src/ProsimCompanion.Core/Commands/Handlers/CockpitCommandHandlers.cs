using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Commands.Handlers;

/// <summary>Request DTO for <c>cockpit.press</c>.</summary>
public sealed record CockpitPressRequest
{
    /// <summary>The row id from Settings → Cockpit Switches.</summary>
    public string? Id { get; init; }

    /// <summary>True for a long press (a cycling selector steps backwards).</summary>
    public bool Long { get; init; }
}

/// <summary>Request DTO for <c>cockpit.set</c>.</summary>
public sealed record CockpitSetRequest
{
    /// <summary>The row id from Settings → Cockpit Switches — never a dataref name.</summary>
    public string? Id { get; init; }

    /// <summary>A catalogue value of the switch (0 = Normal, 1 = Pushed, …).</summary>
    public int? Value { get; init; }
}

/// <summary>
/// <c>cockpit.*</c> command handlers over the <see cref="ICockpitSwitchControl"/> seam
/// (2026-10-24): the Stream Deck cockpit-switch key presses ProSim switches through these.
/// The seam is nullable because the ProSim pillar is optional.
/// </summary>
public static class CockpitCommandHandlers
{
    /// <summary>Registers <c>cockpit.press</c> and <c>cockpit.set</c>. <paramref name="control"/>
    /// may be null (ProSim pillar off): the commands still exist and answer <c>unavailable</c>,
    /// so a Stream Deck key gets a reason instead of a 404.</summary>
    public static void Register(CommandRegistry registry, ICockpitSwitchControl? control)
    {
        ArgumentNullException.ThrowIfNull(registry);

        registry.Register<CockpitPressRequest, CommandResult>(
            "cockpit.press",
            (request, cancellationToken) => PressAsync(control, request, cancellationToken));

        registry.Register<CockpitSetRequest, CommandResult>(
            "cockpit.set",
            (request, cancellationToken) => SetAsync(control, request, cancellationToken));
    }

    /// <summary>The trimmed row id. Public and pure so the rule is unit-tested.</summary>
    /// <exception cref="CommandValidationException">Missing id.</exception>
    public static string ValidateId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new CommandValidationException("id is required, e.g. { \"id\": \"oh_hyd_lmv_blue\" } — a row on Settings → Cockpit Switches.");
        }

        return id.Trim();
    }

    /// <summary>One key press on a row (validation first, so a bad request is a 400 even with
    /// the pillar off).</summary>
    public static Task<CommandResult> PressAsync(ICockpitSwitchControl? control, CockpitPressRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = ValidateId(request.Id);
        return control is null
            ? Task.FromResult(CommandResult.Unavailable("The ProSim pillar is not running — cockpit switches live there."))
            : control.PressAsync(id, request.Long, cancellationToken);
    }

    /// <summary>Writes an explicit catalogue value of a row's switch (a knob position).</summary>
    public static Task<CommandResult> SetAsync(ICockpitSwitchControl? control, CockpitSetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = ValidateId(request.Id);
        if (request.Value is null)
        {
            throw new CommandValidationException("value is required, e.g. { \"id\": \"oh_adirs_1\", \"value\": 1 }.");
        }

        return control is null
            ? Task.FromResult(CommandResult.Unavailable("The ProSim pillar is not running — cockpit switches live there."))
            : control.SetAsync(id, request.Value.Value, cancellationToken);
    }
}
