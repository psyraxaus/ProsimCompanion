using System.Globalization;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Commands.Handlers;

/// <summary>Request DTO for <c>input.vjoyPress</c>.</summary>
public sealed record InputVJoyPressRequest
{
    /// <summary>The vJoy button, 1-based (vJoy numbers them 1–128).</summary>
    public int? Button { get; init; }

    /// <summary>Hold time in ms; null = the configured <c>inputRelay.holdMs</c>.</summary>
    public int? HoldMs { get; init; }
}

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
/// </summary>
public static class InputCommandHandlers
{
    /// <summary>vJoy's hard limit per device.</summary>
    public const int MaxButton = 128;

    /// <summary>Below ~20 ms ChasePlane/MSFS may not see the press at all; above two seconds
    /// a stuck-looking button is a mistake, not a view change.</summary>
    public const int MinHoldMs = 20;
    public const int MaxHoldMs = 2000;

    public static void Register(
        CommandRegistry registry,
        IInputRelay? relay,
        IOptionsMonitor<InputRelayOptions>? options)
    {
        ArgumentNullException.ThrowIfNull(registry);

        registry.Register<InputVJoyPressRequest, CommandResult>(
            "input.vjoyPress",
            (request, cancellationToken) => PressAsync(relay, request, cancellationToken));

        registry.Register<InputPressViewRequest, CommandResult>(
            "input.pressView",
            (request, cancellationToken) => PressViewAsync(relay, options?.CurrentValue, request, cancellationToken));
    }

    /// <summary>Validates the button number. Public and pure so the rule is unit-tested.</summary>
    /// <exception cref="CommandValidationException">Missing or out-of-range button.</exception>
    public static int ValidateButton(int? button)
    {
        if (button is null)
        {
            throw new CommandValidationException("button is required, e.g. { \"button\": 5 }.");
        }

        if (button is < 1 or > MaxButton)
        {
            throw new CommandValidationException(
                FormattableString.Invariant($"button must be between 1 and {MaxButton} (vJoy numbers buttons from 1)."));
        }

        return button.Value;
    }

    /// <summary>Validates an explicit hold time; null passes through (configured default).</summary>
    /// <exception cref="CommandValidationException">Out-of-range hold.</exception>
    public static int? ValidateHold(int? holdMs)
    {
        if (holdMs is null)
        {
            return null;
        }

        if (holdMs is < MinHoldMs or > MaxHoldMs)
        {
            throw new CommandValidationException(
                FormattableString.Invariant($"holdMs must be between {MinHoldMs} and {MaxHoldMs}."));
        }

        return holdMs;
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

    public static Task<CommandResult> PressAsync(IInputRelay? relay, InputVJoyPressRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var button = ValidateButton(request.Button);
        var hold = ValidateHold(request.HoldMs);
        if (relay is null)
        {
            return Task.FromResult(CommandResult.Unavailable("The Sim pillar is not running — the vJoy relay lives there."));
        }

        return relay.PressAsync(button, hold, "button " + button.ToString(CultureInfo.InvariantCulture), cancellationToken);
    }

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

        if (view.Button is < 1 or > MaxButton)
        {
            return Task.FromResult(CommandResult.PreconditionFailed(
                FormattableString.Invariant($"View '{view.Name}' maps to button {view.Button}, outside 1–{MaxButton} — fix the row.")));
        }

        if (relay is null)
        {
            return Task.FromResult(CommandResult.Unavailable("The Sim pillar is not running — the vJoy relay lives there."));
        }

        return relay.PressAsync(view.Button, null, view.Name.Trim(), cancellationToken);
    }
}
