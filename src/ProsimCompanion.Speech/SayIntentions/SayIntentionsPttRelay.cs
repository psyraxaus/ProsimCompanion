using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Hosting;

namespace ProsimCompanion.Speech.SayIntentions;

/// <summary>Press/release edges of a 0/1 switch, plus whether SayIntentions reported a
/// transmission while it was held — pure, so the relay's decisions are testable.</summary>
public sealed class PttEdgeTracker
{
    public enum Edge
    {
        None,
        Pressed,
        Released,
    }

    public bool Pressed { get; private set; }

    /// <summary>True once <c>L:SIAI_RADIO_PTT</c> read 1 during the current/last press — the
    /// proof that the LVAR actually keyed the client.</summary>
    public bool SawTransmit { get; private set; }

    public Edge Observe(int switchValue)
    {
        var pushed = switchValue != 0;
        if (pushed == Pressed)
        {
            return Edge.None;
        }

        Pressed = pushed;
        if (pushed)
        {
            SawTransmit = false;
            return Edge.Pressed;
        }

        return Edge.Released;
    }

    public void NoteRadioPtt(double radioPtt)
    {
        if (Pressed && radioPtt >= 0.5)
        {
            SawTransmit = true;
        }
    }
}

/// <summary>
/// Keys SayIntentions from a ProSim push-to-talk switch (owner idea 2026-10-10: the sidestick
/// PTT lives on the second PC and reaches ProSim as a dataref). One typed subscription to the
/// chosen switch; each edge writes the SI control LVAR (1 pushed, 0 released) through
/// SimConnect — the client's "Map PTT inside the sim with an LVAR". The <c>L:SIAI_RADIO_PTT</c>
/// echo is watched so the session log says whether SI really transmitted. No joystick
/// polling anywhere on this path. Off (source <c>none</c>, or SayIntentions off) binds
/// nothing. MSFS down: the write fails, one warning per episode, the next edge retries.
/// </summary>
public sealed class SayIntentionsPttRelay : IStartupModule, IDisposable
{
    private readonly IOptionsMonitor<SayIntentionsOptions> _options;
    private readonly IProsimDataRefs _prosim;
    private readonly ISimVars? _simVars;
    private readonly JsonlEventLog? _eventLog;
    private readonly ILogger<SayIntentionsPttRelay> _logger;
    private readonly object _gate = new();
    private readonly PttEdgeTracker _tracker = new();

    private IDisposable? _optionsChange;
    private IDataRefSubscription<int>? _switch;
    private IDataRefSubscription<double>? _radioPtt;
    private string _lvar = "";
    private string _source = "none";
    private bool _writeFailureLogged;

    public SayIntentionsPttRelay(
        IOptionsMonitor<SayIntentionsOptions> options,
        IProsimDataRefs prosim,
        ILogger<SayIntentionsPttRelay> logger,
        ISimVars? simVars = null,
        JsonlEventLog? eventLog = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _prosim = prosim;
        _logger = logger;
        _simVars = simVars;
        _eventLog = eventLog;
    }

    /// <summary>The ProSim switch behind a <see cref="SayIntentionsOptions.PttSource"/> key;
    /// null for <c>none</c> or an unknown key.</summary>
    public static DataRef<int>? SourceRef(string? source) => Recognition.ProsimPttSwitches.SourceRef(source);

    public void Start()
    {
        Bind();
        _optionsChange = _options.OnChange(_ => Bind());
    }

    private void Bind()
    {
        lock (_gate)
        {
            UnbindLocked();

            var options = _options.CurrentValue;
            var source = SourceRef(options.PttSource);
            if (!options.Enabled || source is null)
            {
                if (options.Enabled && !string.Equals(options.PttSource, "none", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("SayIntentions PTT relay: unknown source {Source} — not bound", options.PttSource);
                }

                return;
            }

            _lvar = options.PttLvar.Trim();
            _source = options.PttSource;
            _switch = _prosim.Subscribe(source.Value);
            _switch.ValueChanged += OnSwitchChanged;
            if (_simVars is not null)
            {
                _radioPtt = _simVars.Subscribe(SayIntentionsLvarNames.RadioPtt);
                _radioPtt.ValueChanged += OnRadioPttChanged;
            }

            _logger.LogInformation("SayIntentions PTT relay bound: {Dataref} → {Lvar}", source.Value.Name, _lvar);
        }
    }

    private void UnbindLocked()
    {
        if (_switch is not null)
        {
            _switch.ValueChanged -= OnSwitchChanged;
            _switch.Dispose();
            _switch = null;
        }

        if (_radioPtt is not null)
        {
            _radioPtt.ValueChanged -= OnRadioPttChanged;
            _radioPtt.Dispose();
            _radioPtt = null;
        }

        // Never leave SayIntentions keyed by a binding that is going away.
        if (_tracker.Pressed && _tracker.Observe(0) == PttEdgeTracker.Edge.Released)
        {
            _ = WriteAsync(pressed: false);
        }
    }

    private void OnSwitchChanged(object? sender, EventArgs e)
    {
        PttEdgeTracker.Edge edge;
        lock (_gate)
        {
            if (_switch is null || _switch.RawValue is null)
            {
                return;
            }

            edge = _tracker.Observe(_switch.Value);
        }

        if (edge != PttEdgeTracker.Edge.None)
        {
            _ = WriteAsync(edge == PttEdgeTracker.Edge.Pressed);
        }
    }

    private void OnRadioPttChanged(object? sender, EventArgs e)
    {
        var radioPtt = _radioPtt;
        if (radioPtt is not null)
        {
            _tracker.NoteRadioPtt(radioPtt.Value);
        }
    }

    private async Task WriteAsync(bool pressed)
    {
        var lvar = _lvar;
        var value = pressed ? 1 : 0;
        if (_simVars is null)
        {
            WarnOnce("no SimConnect layer");
            return;
        }

        try
        {
            await _simVars.WriteAsync(lvar, value).ConfigureAwait(false);
            if (_writeFailureLogged)
            {
                _writeFailureLogged = false;
                _logger.LogInformation("SayIntentions PTT relay: writes reach the sim again");
            }

            _logger.LogDebug("SayIntentions PTT {Lvar} = {Value} ({Source})", lvar, value, _source);
            _eventLog?.Record("sayintentions.ptt", new
            {
                pressed,
                lvar,
                source = _source,
                // On release: did SI report a transmission while the button was held?
                transmitted = pressed ? (bool?)null : _tracker.SawTransmit,
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            WarnOnce(ex.Message);
        }
    }

    private void WarnOnce(string reason)
    {
        if (!_writeFailureLogged)
        {
            _writeFailureLogged = true;
            _logger.LogWarning("SayIntentions PTT not relayed to {Lvar}: {Reason} — retrying on the next press", _lvar, reason);
        }
    }

    public void Dispose()
    {
        _optionsChange?.Dispose();
        lock (_gate)
        {
            UnbindLocked();
        }
    }
}
