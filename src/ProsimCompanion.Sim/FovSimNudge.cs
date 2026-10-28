using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Display;

namespace ProsimCompanion.Sim;

/// <summary>
/// <see cref="IFovSimNudge"/> over SimConnect (Display &amp; FOV page, 2026-10-28;
/// design: <c>claude/fov-tool-design.md</c>). The two names are on
/// <see cref="SimWriteGate"/>'s exact list. Each is subscribed through its typed descriptor
/// before the first write, which is what fixes its SimConnect unit (<c>percent</c> and
/// <c>Bool</c>) — a bare write would register the name as a plain number.
/// </summary>
public sealed class FovSimNudge : IFovSimNudge
{
    private readonly ISimVars _simVars;
    private readonly ILogger<FovSimNudge> _logger;
    private readonly object _gate = new();
    private IDataRefSubscription<double>? _zoom;
    private IDataRefSubscription<bool>? _reset;

    public FovSimNudge(ISimVars simVars, ILogger<FovSimNudge> logger)
    {
        ArgumentNullException.ThrowIfNull(simVars);
        ArgumentNullException.ThrowIfNull(logger);
        _simVars = simVars;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> NudgeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureSubscribed();
            await _simVars.WriteAsync(ProsimDataRefNames.SimVars.CockpitCameraZoom.Name, 50, cancellationToken).ConfigureAwait(false);
            await _simVars.WriteAsync(ProsimDataRefNames.SimVars.CameraActionCockpitViewReset.Name, 1, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("FOV nudge sent: cockpit zoom 50 % and cockpit view reset");
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or TimeoutException)
        {
            // Not connected, or the sim refused the write: the file is already written, the
            // page says the change takes effect on the next aircraft load.
            _logger.LogDebug(ex, "FOV nudge not delivered; the new zoom applies on the next aircraft load");
            return false;
        }
    }

    private void EnsureSubscribed()
    {
        lock (_gate)
        {
            _zoom ??= _simVars.Subscribe(ProsimDataRefNames.SimVars.CockpitCameraZoom);
            _reset ??= _simVars.Subscribe(ProsimDataRefNames.SimVars.CameraActionCockpitViewReset);
        }
    }
}
