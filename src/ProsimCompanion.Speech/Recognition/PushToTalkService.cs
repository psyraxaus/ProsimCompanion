using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Speech.Recognition;

/// <summary>
/// Push-to-talk input (Prosim2FO process): a global low-level keyboard hook (installed on a
/// dedicated message-pump thread — never swallows keys, so PTT still reaches the sim) plus a
/// winmm poller that tracks the pressed buttons of EVERY connected joystick as a
/// (device, button) set. The FO PTT and ATC-mute functions each carry one
/// <see cref="PttBindingOptions"/> — a keyboard key OR a joystick button — matched against
/// those sets; the legacy flat key/device/button fields still apply while a binding is unset,
/// so pre-binding configs migrate silently. Joystick devices are matched by product name
/// first (winmm ids shuffle on re-plug), numeric id as fallback; more than 32 buttons per
/// device can come later via RawInput. Raises edge events only. The hook callback only
/// updates key state and queues the edge evaluation to the thread pool — Windows silently
/// removes low-level hooks whose callbacks exceed LowLevelHooksTimeout, so recognition
/// start/stop must never run on the hook thread. Doubles as <see cref="IPttInputCapture"/>
/// for the settings card's Set-button capture and pressed-state lamps.
/// </summary>
public sealed class PushToTalkService : IDisposable, IPttInputCapture
{
    private const int MaxJoysticks = 16;

    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmSyskeydown = 0x0104;
    private const int WmKeyup = 0x0101;
    private const int WmSyskeyup = 0x0105;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out NativeMessage lpMsg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public long Pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JoyInfoEx
    {
        public int Size;
        public int Flags;
        public int Xpos, Ypos, Zpos, Rpos, Upos, Vpos;
        public int Buttons;
        public int ButtonNumber;
        public int Pov;
        public int Reserved1, Reserved2;
    }

    [DllImport("winmm.dll")]
    private static extern int joyGetPosEx(int uJoyID, ref JoyInfoEx pji);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct JoyCaps
    {
        public ushort Mid, Pid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string ProductName;
        public uint XMin, XMax, YMin, YMax, ZMin, ZMax;
        public uint NumButtons;
        public uint PeriodMin, PeriodMax;
        public uint RMin, RMax, UMin, UMax, VMin, VMax;
        public uint Caps, MaxAxes, NumAxes, MaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string RegKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string OemVxD;
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int joyGetDevCapsW(IntPtr uJoyID, ref JoyCaps pjc, int cbjc);

    private static readonly Dictionary<string, int> KeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["space"] = 0x20, ["tab"] = 0x09, ["enter"] = 0x0D, ["pause"] = 0x13,
        ["capslock"] = 0x14, ["escape"] = 0x1B, ["insert"] = 0x2D, ["delete"] = 0x2E,
        ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22,
        ["leftshift"] = 0xA0, ["rightshift"] = 0xA1, ["lshift"] = 0xA0, ["rshift"] = 0xA1,
        ["leftctrl"] = 0xA2, ["rightctrl"] = 0xA3, ["lcontrolkey"] = 0xA2, ["rcontrolkey"] = 0xA3,
        ["leftalt"] = 0xA4, ["rightalt"] = 0xA5, ["scrolllock"] = 0x91, ["numlock"] = 0x90,
    };

    /// <summary>One canonical name per VK for captured keys (KeyNames also carries aliases).
    /// Every output round-trips through <see cref="ParseKey"/> — unknown VKs fall back to the
    /// decimal code, which the parser also accepts.</summary>
    private static readonly Dictionary<int, string> CanonicalKeyNames = new()
    {
        [0x20] = "space", [0x09] = "tab", [0x0D] = "enter", [0x13] = "pause",
        [0x14] = "capslock", [0x1B] = "escape", [0x2D] = "insert", [0x2E] = "delete",
        [0x24] = "home", [0x23] = "end", [0x21] = "pageup", [0x22] = "pagedown",
        [0xA0] = "leftshift", [0xA1] = "rightshift", [0xA2] = "leftctrl", [0xA3] = "rightctrl",
        [0xA4] = "leftalt", [0xA5] = "rightalt", [0x91] = "scrolllock", [0x90] = "numlock",
    };

    private readonly IOptionsMonitor<SpeechOptions> _options;
    private readonly ILogger<PushToTalkService> _logger;
    private readonly HookProc _hookProc; // held so the GC never collects the callback
    private readonly HashSet<int> _keysDown = [];
    private readonly HashSet<(int Dev, int Btn)> _buttonsDown = [];
    private readonly Dictionary<int, int> _deviceMasks = []; // per winmm id: previous buttons
    private readonly Dictionary<(string Name, int? Id), int> _resolvedIds = [];
    private readonly object _recomputeGate = new(); // serializes edge detection (pool + timer threads)

    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _hook;
    private Timer? _joystickTimer;
    private bool _ownPressed;
    private bool _atcPressed;

    public PushToTalkService(IOptionsMonitor<SpeechOptions> options, ILogger<PushToTalkService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;
        _hookProc = HookCallback;
    }

    /// <summary>Edge events: true on press, false on release.</summary>
    public event Action<bool>? OwnPttChanged;

    public event Action<bool>? AtcPttChanged;

    /// <summary>Any pressed-state edge — the settings card's lamps re-render on this.</summary>
    public event EventHandler? PressedChanged;

    public bool OwnPttPressed => _ownPressed;

    public bool AtcPttPressed => _atcPressed;

    bool IPttInputCapture.AtcMutePressed => _atcPressed;

    public void Start()
    {
        _hookThread = new Thread(HookThreadMain) { IsBackground = true, Name = "ptt-hook" };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
        _joystickTimer = new Timer(_ => PollJoysticks(), null, 1000, 25);
    }

    public void Dispose()
    {
        _joystickTimer?.Dispose();
        if (_hookThreadId != 0)
        {
            PostThreadMessageW(_hookThreadId, 0x0012 /*WM_QUIT*/, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// <summary>Parses a configured key: known name, single character, "F1".."F24", or a
    /// decimal VK code. 0 = unbound.</summary>
    public static int ParseKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return 0;
        }

        var k = key.Trim();
        if (KeyNames.TryGetValue(k, out var vk))
        {
            return vk;
        }

        if (k.Length == 1 && char.IsAsciiLetterOrDigit(k[0]))
        {
            return char.ToUpperInvariant(k[0]);
        }

        if ((k.StartsWith('F') || k.StartsWith('f'))
            && int.TryParse(k[1..], out var f) && f is >= 1 and <= 24)
        {
            return 0x70 + f - 1;
        }

        return int.TryParse(k, out var code) && code is > 0 and < 256 ? code : 0;
    }

    /// <summary>Formats a VK code as a settings-file key name (inverse of
    /// <see cref="ParseKey"/>).</summary>
    public static string FormatKey(int vk) =>
        vk is (>= 'A' and <= 'Z') or (>= '0' and <= '9') ? ((char)vk).ToString()
        : vk is >= 0x70 and <= 0x87 ? $"F{vk - 0x70 + 1}"
        : CanonicalKeyNames.TryGetValue(vk, out var name) ? name
        : vk.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public IReadOnlyList<JoystickDeviceView> GetJoysticks()
    {
        var result = new List<JoystickDeviceView>();
        for (var id = 0; id < MaxJoysticks; id++)
        {
            if (ProductNameOf(id) is { } name)
            {
                result.Add(new JoystickDeviceView(id, name));
            }
        }

        return result;
    }

    public async Task<PttInputCaptureResult?> CaptureAsync(
        bool includeJoysticks, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Baselines: anything already down when the capture starts never binds. Released
        // inputs are dropped from the baseline each poll, so press-release-press still
        // captures within the window.
        HashSet<int> baselineKeys;
        HashSet<(int Dev, int Btn)> baselineButtons;
        lock (_recomputeGate)
        {
            lock (_keysDown)
            {
                baselineKeys = [.. _keysDown];
            }

            baselineButtons = [.. _buttonsDown];
        }

        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);

            lock (_keysDown)
            {
                baselineKeys.IntersectWith(_keysDown);
                foreach (var vk in _keysDown)
                {
                    if (!baselineKeys.Contains(vk))
                    {
                        return new PttInputCaptureResult(FormatKey(vk), null, null);
                    }
                }
            }

            if (!includeJoysticks)
            {
                continue;
            }

            lock (_recomputeGate)
            {
                baselineButtons.IntersectWith(_buttonsDown);
                foreach (var (dev, btn) in _buttonsDown)
                {
                    if (!baselineButtons.Contains((dev, btn)))
                    {
                        return new PttInputCaptureResult(null, dev, btn);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>The id a binding should poll: a non-empty product name wins (prefix-matched
    /// both ways, winmm truncates to 31 chars), the stored numeric id is the fallback, −1 is
    /// unbound. <paramref name="productNameOf"/> returns null for ids with no connected
    /// device.</summary>
    public static int ResolveJoystickId(string name, int? configuredId, Func<int, string?> productNameOf)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            for (var id = 0; id < MaxJoysticks; id++)
            {
                var product = productNameOf(id);
                if (product is not null
                    && (product.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith(product, StringComparison.OrdinalIgnoreCase)))
                {
                    return id;
                }
            }
        }

        return configuredId ?? -1;
    }

    private static string? ProductNameOf(int id)
    {
        if (ReadButtons(id) is null)
        {
            return null;
        }

        var caps = new JoyCaps();
        return joyGetDevCapsW((IntPtr)id, ref caps, Marshal.SizeOf<JoyCaps>()) == 0
            && !string.IsNullOrWhiteSpace(caps.ProductName)
            ? caps.ProductName
            : $"Joystick {id}";
    }

    private void HookThreadMain()
    {
        _hookThreadId = GetCurrentThreadId();
        _hook = SetWindowsHookExW(WhKeyboardLl, _hookProc, IntPtr.Zero, 0);
        if (_hook == IntPtr.Zero)
        {
            _logger.LogWarning("Keyboard hook install failed — keyboard PTT unavailable");
            return;
        }

        while (GetMessageW(out _, IntPtr.Zero, 0, 0) > 0)
        {
            // Low-level hook callbacks arrive via this message loop; nothing to dispatch.
        }

        UnhookWindowsHookEx(_hook);
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var vk = Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode is the first field
            var message = (int)(long)wParam;
            var changed = false;
            if (message is WmKeydown or WmSyskeydown)
            {
                lock (_keysDown)
                {
                    changed = _keysDown.Add(vk); // de-dupe key auto-repeat
                }
            }
            else if (message is WmKeyup or WmSyskeyup)
            {
                lock (_keysDown)
                {
                    changed = _keysDown.Remove(vk);
                }
            }

            if (changed)
            {
                // Off the hook thread — Recompute reaches into recognition start/stop, which
                // is far beyond the hook timeout budget.
                ThreadPool.QueueUserWorkItem(static state => ((PushToTalkService)state!).Recompute(), this);
            }
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>Maintains the (device, button) pressed set across every connected winmm
    /// device (Prosim2FO's JoystickMonitor, poll-based).</summary>
    private void PollJoysticks()
    {
        lock (_recomputeGate)
        {
            var changed = false;
            for (var id = 0; id < MaxJoysticks; id++)
            {
                var buttons = ReadButtons(id);
                if (buttons is null)
                {
                    // Device gone — drop its state so a stale held button can't latch PTT.
                    if (_deviceMasks.Remove(id, out var stale) && stale != 0)
                    {
                        _buttonsDown.RemoveWhere(x => x.Dev == id);
                        changed = true;
                    }

                    continue;
                }

                _deviceMasks.TryGetValue(id, out var previous);
                if (buttons == previous)
                {
                    continue;
                }

                _deviceMasks[id] = buttons.Value;
                var delta = buttons.Value ^ previous;
                while (delta != 0)
                {
                    var bit = BitOperations.TrailingZeroCount((uint)delta);
                    delta &= ~(1 << bit);
                    if ((buttons.Value & (1 << bit)) != 0)
                    {
                        _buttonsDown.Add((id, bit));
                    }
                    else
                    {
                        _buttonsDown.Remove((id, bit));
                    }
                }

                changed = true;
            }

            if (changed)
            {
                Recompute();
            }
        }
    }

    private void Recompute()
    {
        // Serialized: a queued keyboard edge and the joystick timer may arrive concurrently,
        // and the press/release edge pair must reach handlers in order.
        lock (_recomputeGate)
        {
            var options = _options.CurrentValue;
            var own = Matches(options.PttBinding, options.PttKey,
                options.PttJoystickDeviceName, options.PttJoystickDevice, options.PttJoystickButton);
            var atc = Matches(options.AtcMuteBinding, options.AtcMuteKey,
                options.AtcMuteJoystickDeviceName, options.AtcMuteJoystickDevice, options.AtcMuteJoystickButton);

            var pressedEdge = own != _ownPressed || atc != _atcPressed;
            if (own != _ownPressed)
            {
                _ownPressed = own;
                try
                {
                    OwnPttChanged?.Invoke(own);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "PTT handler threw");
                }
            }

            if (atc != _atcPressed)
            {
                _atcPressed = atc;
                try
                {
                    AtcPttChanged?.Invoke(atc);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "ATC PTT handler threw");
                }
            }

            if (pressedEdge)
            {
                try
                {
                    PressedChanged?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Pressed-state handler threw");
                }
            }
        }
    }

    /// <summary>Prosim2FO's Matches(): the binding wins when set; unset falls back to the
    /// legacy flat fields (key AND joystick both apply there, preserving old configs).</summary>
    private bool Matches(PttBindingOptions binding, string legacyKey,
        string legacyName, int? legacyDevice, int? legacyButton)
    {
        if (binding.IsSet)
        {
            return binding.Kind.Equals("keyboard", StringComparison.OrdinalIgnoreCase)
                ? KeyDown(binding.Key)
                : ButtonDown(binding.JoystickDeviceName, binding.JoystickDevice, binding.Button);
        }

        return KeyDown(legacyKey) || ButtonDown(legacyName, legacyDevice, legacyButton);
    }

    private bool KeyDown(string key)
    {
        var vk = ParseKey(key);
        if (vk == 0)
        {
            return false;
        }

        lock (_keysDown)
        {
            return _keysDown.Contains(vk);
        }
    }

    private bool ButtonDown(string name, int? configuredId, int? button)
    {
        if (button is not { } btn || (string.IsNullOrWhiteSpace(name) && configuredId is null))
        {
            return false;
        }

        var key = (name ?? "", configuredId);
        // Cached resolution; re-resolve when the resolved device stops answering (re-plugged
        // devices shuffle winmm ids).
        if (!_resolvedIds.TryGetValue(key, out var resolved) || !_deviceMasks.ContainsKey(resolved))
        {
            resolved = ResolveJoystickId(key.Item1, configuredId, ProductNameOf);
            _resolvedIds[key] = resolved;
        }

        return resolved >= 0 && _buttonsDown.Contains((resolved, btn));
    }

    private static int? ReadButtons(int id)
    {
        var info = new JoyInfoEx { Size = Marshal.SizeOf<JoyInfoEx>(), Flags = 0x80 /*JOY_RETURNBUTTONS*/ };
        return joyGetPosEx(id, ref info) == 0 ? info.Buttons : null;
    }
}
