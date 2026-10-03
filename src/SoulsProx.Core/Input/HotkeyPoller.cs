using System.Runtime.InteropServices;

namespace SoulsProx.Input;

public enum InputKind
{
    /// <summary>Keyboard key or mouse button, by Windows virtual-key code.</summary>
    Key,
    /// <summary>XInput controller button (bit mask), or a trigger (see <see cref="InputBinding"/>).</summary>
    Gamepad,
}

/// <summary>A push-to-talk / radio binding.</summary>
public sealed record InputBinding(InputKind Kind, int Code)
{
    public const int LeftTrigger = 0x10000;
    public const int RightTrigger = 0x20000;

    private static readonly Dictionary<int, string> PadNames = new()
    {
        [0x0001] = "D-pad Up", [0x0002] = "D-pad Down", [0x0004] = "D-pad Left", [0x0008] = "D-pad Right",
        [0x0010] = "Start", [0x0020] = "Back", [0x0040] = "Left Stick Click", [0x0080] = "Right Stick Click",
        [0x0100] = "LB", [0x0200] = "RB", [0x1000] = "A", [0x2000] = "B", [0x4000] = "X", [0x8000] = "Y",
        [LeftTrigger] = "LT", [RightTrigger] = "RT",
    };

    private static readonly Dictionary<int, string> MouseNames = new()
    {
        [0x04] = "Middle Mouse", [0x05] = "Mouse 4", [0x06] = "Mouse 5",
    };

    /// <param name="keyName">Turns a virtual-key code into a name (the UI uses WinForms' Keys enum).</param>
    public string Describe(Func<int, string> keyName) => Kind switch
    {
        InputKind.Gamepad => "Controller " + PadNames.GetValueOrDefault(Code, $"0x{Code:X}"),
        _ => MouseNames.GetValueOrDefault(Code) ?? keyName(Code),
    };
}

/// <summary>
/// Polls the keyboard, mouse buttons and controllers ~100 times a second. Works while the game has focus,
/// because it reads the global key state instead of window messages.
/// </summary>
public sealed class HotkeyPoller : IDisposable
{
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _pttDown, _radioDown;
    private readonly bool[] _padConnected = new bool[4];
    private long _nextPadScan;

    public HotkeyPoller()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "SoulsProx hotkeys" };
        _thread.Start();
    }

    public InputBinding? PushToTalk { get; set; }
    public InputBinding? Radio { get; set; }

    public bool PushToTalkDown => _pttDown;
    public bool RadioDown => _radioDown;

    private void Run()
    {
        while (!_cts.Token.WaitHandle.WaitOne(10))
        {
            RefreshPads();
            _pttDown = IsDown(PushToTalk);
            _radioDown = IsDown(Radio);
        }
    }

    /// <summary>Checking an empty controller slot is slow, so only look for newly plugged-in pads every 2 seconds.</summary>
    private void RefreshPads()
    {
        long now = Environment.TickCount64;
        if (now < _nextPadScan) return;
        _nextPadScan = now + 2000;
        for (uint i = 0; i < 4; i++) _padConnected[i] = XInputGetState(i, out _) == 0;
    }

    public bool IsDown(InputBinding? binding)
    {
        if (binding is null) return false;
        if (binding.Kind == InputKind.Key) return (GetAsyncKeyState(binding.Code) & 0x8000) != 0;
        for (uint i = 0; i < 4; i++)
        {
            if (!_padConnected[i] || XInputGetState(i, out var state) != 0) continue;
            if (PadPressed(state.Gamepad, binding.Code)) return true;
        }
        return false;
    }

    /// <summary>Returns the first key, mouse button or controller button currently held, for "press a key to bind".</summary>
    public InputBinding? DetectPressed()
    {
        // Skip left/right click so clicking the "bind" button doesn't immediately bind it.
        for (int vk = 0x04; vk <= 0xFE; vk++)
        {
            if (vk is 0x10 or 0x11 or 0x12) continue; // generic Shift/Ctrl/Alt; the left/right variants are reported instead
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) return new InputBinding(InputKind.Key, vk);
        }
        for (uint i = 0; i < 4; i++)
        {
            if (XInputGetState(i, out var state) != 0) continue;
            var pad = state.Gamepad;
            if (pad.LeftTrigger > 128) return new InputBinding(InputKind.Gamepad, InputBinding.LeftTrigger);
            if (pad.RightTrigger > 128) return new InputBinding(InputKind.Gamepad, InputBinding.RightTrigger);
            for (int bit = 0; bit < 16; bit++)
                if ((pad.Buttons & (1 << bit)) != 0) return new InputBinding(InputKind.Gamepad, 1 << bit);
        }
        return null;
    }

    private static bool PadPressed(XInputGamepad pad, int code) => code switch
    {
        InputBinding.LeftTrigger => pad.LeftTrigger > 128,
        InputBinding.RightTrigger => pad.RightTrigger > 128,
        _ => (pad.Buttons & code) != 0,
    };

    public void Dispose()
    {
        _cts.Cancel();
        _thread.Join(TimeSpan.FromSeconds(1));
        _cts.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint userIndex, out XInputState state);
}
