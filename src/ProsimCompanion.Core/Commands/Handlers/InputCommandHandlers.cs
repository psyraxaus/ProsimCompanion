using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Commands.Handlers;

/// <summary>Request DTO for <c>input.pressView</c>.</summary>
public sealed record InputPressViewRequest
{
    /// <summary>A name from the Setup page's view rows (case-insensitive).</summary>
    public string? Name { get; init; }
}

/// <summary>
/// <c>input.*</c> command handlers over the <see cref="IInputRelay"/> seam (2026-10-24, the
/// vJoy relay for ChasePlane views from a second PC). The seam is nullable because the Sim
/// pillar is optional; the relay itself answers <c>unavailable</c> while switched off.
/// <para>
/// One command only: <c>input.pressView {name}</c>. A remote key names a view row the pilot
/// configured; it never names a raw button number (ADR-0015's "configured rows, not free
/// names", applied to the relay too — the raw <c>input.vjoyPress</c> was removed in the
/// 2026-10-25 review: the plugin never used it, and it let any token holder press any of the
/// 128 buttons).
/// </para>
/// </summary>
public static class InputCommandHandlers
{
    /// <summary>Registers <c>input.pressView</c>. <paramref name="relay"/> may be null (Sim
    /// pillar off): the command still exists and answers <c>unavailable</c> after validation.</summary>
    public static void Register(
        CommandRegistry registry,
        IInputRelay? relay,
        IOptionsMonitor<InputRelayOptions>? options)
    {
        ArgumentNullException.ThrowIfNull(registry);

        registry.Register<InputPressViewRequest, CommandResult>(
            "input.pressView",
            (request, cancellationToken) => PressViewAsync(relay, options?.CurrentValue, request, cancellationToken));
    }

    /// <summary>Finds the view row for a name (trimmed, case-insensitive). Null when there is no
    /// such row — the caller words the refusal with the names that do exist.</summary>
    public static InputViewButton? ResolveView(InputRelayOptions? options, string? name)
    {
        if (options is null || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var wanted = name.Trim();
        return options.Views.FirstOrDefault(view =>
            string.Equals(view.Name.Trim(), wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Presses the button a named view row maps. Validation first (a missing name is a
    /// 400), then the row lookup, then the pillar check — so the reason always says why.</summary>
    public static Task<CommandResult> PressViewAsync(
        IInputRelay? relay,
        InputRelayOptions? options,
        InputPressViewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new CommandValidationException("name is required, e.g. { \"name\": \"wing\" } — a row on Settings → Setup → vJoy relay.");
        }

        var view = ResolveView(options, request.Name);
        if (view is null)
        {
            var known = options?.Views.Select(v => v.Name.Trim()).Where(n => n.Length > 0).ToList() ?? [];
            return Task.FromResult(CommandResult.PreconditionFailed(
                known.Count == 0
                    ? $"No view named '{request.Name.Trim()}' — add view rows on Settings → Setup → vJoy relay."
                    : $"No view named '{request.Name.Trim()}' — known views: {string.Join(", ", known)}."));
        }

        if (view.Button is < 1 or > InputRelayOptions.MaxButton)
        {
            return Task.FromResult(CommandResult.PreconditionFailed(
                FormattableString.Invariant($"View '{view.Name}' maps to button {view.Button}, outside 1–{InputRelayOptions.MaxButton} — fix the row.")));
        }

        if (relay is null)
        {
            return Task.FromResult(CommandResult.Unavailable("The Sim pillar is not running — the vJoy relay lives there."));
        }

        return relay.PressAsync(view.Button, null, view.Name.Trim(), cancellationToken);
    }
}
