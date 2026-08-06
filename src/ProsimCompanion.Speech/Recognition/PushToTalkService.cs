using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Speech.Recognition;

/// <summary>
/// Push-to-talk input: a global low-level keyboard hook (installed on a dedicated
/// message-pump thread — never swallows keys, so PTT still reaches the sim) plus a winmm
/// joystick poller (16 devices × 32 buttons at 25 ms; legacy API, HOTAS with more buttons can
/// come later via RawInput). Raises edge events only. Bindings are re-read live from options.
/// </summary>
public sealed class PushToTalkService : IDisposable
{
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
            if (message is WmKeydown or WmSyskeydown)
            {
                if (_keysDown.Add(vk)) // de-dupe key auto-repeat
                {
                    Recompute();
                }
            }
            else if (message is WmKeyup or WmSyskeyup)
            {
                if (_keysDown.Remove(vk))
                {
                    Recompute();
                }
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
