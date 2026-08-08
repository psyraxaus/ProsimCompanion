using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Speech.Recognition;

/// <summary>
/// Push-to-talk input: a global low-level keyboard hook (installed on a dedicated
/// message-pump thread — never swallows keys, so PTT still reaches the sim) plus a winmm
/// joystick poller (the configured device id 0–15 / button 0–31 at 25 ms; legacy API, HOTAS
/// with more buttons can come later via RawInput). Raises edge events only. Bindings are
/// re-read live from options. The hook callback only updates key state and queues the edge
/// evaluation to the thread pool — Windows silently removes low-level hooks whose callbacks
/// exceed LowLevelHooksTimeout, so recognition start/stop must never run on the hook thread.
/// Doubles as <see cref="IPttInputCapture"/> for the settings page's press-to-detect binding
/// (it already owns the key state and the joystick API).
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

    private readonly IOptionsMonitor<SpeechOptions> _options;
    private readonly ILogger<PushToTalkService> _logger;
    private readonly HookProc _hookProc; // held so the GC never collects the callback
    private readonly HashSet<int> _keysDown = [];
    private readonly object _recomputeGate = new(); // serializes edge detection (pool + timer threads)

    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _hook;
    private Timer? _joystickTimer;
    private int _previousButtons;
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

    public bool OwnPttPressed => _ownPressed;

    public bool AtcPttPressed => _atcPressed;

    public void Start()
    {
        _hookThread = new Thread(HookThreadMain) { IsBackground = true, Name = "ptt-hook" };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
        _joystickTimer = new Timer(_ => PollJoystick(), null, 1000, 25);
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
            if (ReadButtons(id) is null)
            {
                continue; // not connected
            }

            var caps = new JoyCaps();
            var name = joyGetDevCapsW((IntPtr)id, ref caps, Marshal.SizeOf<JoyCaps>()) == 0
                && !string.IsNullOrWhiteSpace(caps.ProductName)
                ? caps.ProductName
                : $"Joystick {id}";
            result.Add(new JoystickDeviceView(id, name));
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
        lock (_keysDown)
        {
            baselineKeys = [.. _keysDown];
        }

        var baselineButtons = new int[MaxJoysticks];
        if (includeJoysticks)
        {
            for (var id = 0; id < MaxJoysticks; id++)
            {
                baselineButtons[id] = ReadButtons(id) ?? 0;
            }
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

            for (var id = 0; id < MaxJoysticks; id++)
            {
                if (ReadButtons(id) is not { } buttons)
                {
                    continue;
                }

                baselineButtons[id] &= buttons;
                var fresh = buttons & ~baselineButtons[id];
                if (fresh != 0)
                {
                    return new PttInputCaptureResult(null, id, BitOperations.TrailingZeroCount((uint)fresh));
                }
            }
        }

        return null;
    }

    /// <summary>Button bitmask for a winmm device, or null when the id has no connected
    /// device.</summary>
    private static int? ReadButtons(int id)
    {
        var info = new JoyInfoEx { Size = Marshal.SizeOf<JoyInfoEx>(), Flags = 0x80 /*JOY_RETURNBUTTONS*/ };
        return joyGetPosEx(id, ref info) == 0 ? info.Buttons : null;
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

    private void PollJoystick()
    {
        var options = _options.CurrentValue;
        if (options.PttJoystickDevice is not { } device || options.PttJoystickButton is not { } button)
        {
            return;
        }

        var info = new JoyInfoEx { Size = Marshal.SizeOf<JoyInfoEx>(), Flags = 0x80 /*JOY_RETURNBUTTONS*/ };
        var buttons = joyGetPosEx(device, ref info) == 0 ? info.Buttons : 0;
        if (buttons == _previousButtons)
        {
            return;
        }

        _previousButtons = buttons;
        Recompute(joystickButtons: buttons, joystickButton: button);
    }

    private void Recompute(int joystickButtons = -1, int joystickButton = -1)
    {
        // Serialized: a queued keyboard edge and the joystick timer may arrive concurrently,
        // and the press/release edge pair must reach handlers in order.
        lock (_recomputeGate)
        {
            var options = _options.CurrentValue;
            var ownKey = ParseKey(options.PttKey);
            var atcKey = ParseKey(options.AtcMuteKey);

            bool own;
            bool atc;
            lock (_keysDown)
            {
                own = ownKey != 0 && _keysDown.Contains(ownKey);
                atc = atcKey != 0 && _keysDown.Contains(atcKey);
            }

            if (joystickButtons >= 0 && joystickButton >= 0)
            {
                own |= (joystickButtons & (1 << joystickButton)) != 0;
            }
            else if (options.PttJoystickDevice is not null && options.PttJoystickButton is { } jb)
            {
                own |= (_previousButtons & (1 << jb)) != 0;
            }

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
        }
    }
}
