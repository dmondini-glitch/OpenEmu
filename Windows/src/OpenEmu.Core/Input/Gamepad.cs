using System.Runtime.InteropServices;

namespace OpenEmu.Core.Input;

[Flags]
public enum GamepadButtons : uint
{
    None = 0, DPadUp = 0x0001, DPadDown = 0x0002, DPadLeft = 0x0004, DPadRight = 0x0008, Start = 0x0010, Back = 0x0020,
    LeftThumb = 0x0040, RightThumb = 0x0080, LeftShoulder = 0x0100, RightShoulder = 0x0200, Guide = 0x0400,
    A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000,
}

public enum GamepadAxis { LeftX = 0, LeftY = 1, RightX = 2, RightY = 3, LeftTrigger = 4, RightTrigger = 5 }

public struct GamepadState
{
    public bool Connected;
    public GamepadButtons Buttons;
    public short LeftX, LeftY, RightX, RightY;
    public byte LeftTrigger, RightTrigger;

    public readonly bool IsPressed(GamepadButtons b) => (Buttons & b) != 0;
    public readonly int Axis(GamepadAxis a) => a switch
    {
        GamepadAxis.LeftX => LeftX, GamepadAxis.LeftY => LeftY, GamepadAxis.RightX => RightX, GamepadAxis.RightY => RightY,
        GamepadAxis.LeftTrigger => LeftTrigger * 32767 / 255, GamepadAxis.RightTrigger => RightTrigger * 32767 / 255, _ => 0,
    };
}

public interface IGamepadProvider
{
    int MaxDevices { get; }
    GamepadState GetState(int index);
    void SetRumble(int index, ushort lowFrequency, ushort highFrequency);
    string DeviceName(int index);
}

public sealed class NullGamepadProvider : IGamepadProvider
{
    public int MaxDevices => 0;
    public GamepadState GetState(int index) => default;
    public void SetRumble(int index, ushort lowFrequency, ushort highFrequency) { }
    public string DeviceName(int index) => "";
}

/// <summary>In-memory provider used by tests and by UI-driven virtual controllers.</summary>
public sealed class VirtualGamepadProvider : IGamepadProvider
{
    private readonly GamepadState[] _states = new GamepadState[4];
    public int MaxDevices => 4;
    public GamepadState GetState(int index) => index >= 0 && index < 4 ? _states[index] : default;
    public void Set(int index, GamepadState s) => _states[index] = s;
    public void SetRumble(int index, ushort lowFrequency, ushort highFrequency) { }
    public string DeviceName(int index) => $"Virtual {index + 1}";
}

/// <summary>Xbox / XInput controllers on Windows (xinput1_4 with fallback to xinput9_1_0).</summary>
public sealed class XInputProvider : IGamepadProvider
{
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD { public ushort wButtons; public byte bLeftTrigger, bRightTrigger; public short sThumbLX, sThumbLY, sThumbRX, sThumbRY; }
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE { public uint dwPacketNumber; public XINPUT_GAMEPAD Gamepad; }
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_VIBRATION { public ushort wLeftMotorSpeed, wRightMotorSpeed; }

    private unsafe delegate* unmanaged<uint, XINPUT_STATE*, uint> _getState;
    private unsafe delegate* unmanaged<uint, XINPUT_VIBRATION*, uint> _setState;
    private readonly bool[] _connected = new bool[4];
    private readonly DateTime[] _nextProbe = new DateTime[4];

    public static bool IsAvailable => OperatingSystem.IsWindows();
    public int MaxDevices => 4;

    public unsafe XInputProvider()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        IntPtr lib = IntPtr.Zero;
        foreach (var name in new[] { "xinput1_4.dll", "xinput1_3.dll", "xinput9_1_0.dll" })
            if (NativeLibrary.TryLoad(name, out lib)) break;
        if (lib == IntPtr.Zero) throw new DllNotFoundException("XInput not available");
        _getState = (delegate* unmanaged<uint, XINPUT_STATE*, uint>)NativeLibrary.GetExport(lib, "XInputGetState");
        _setState = (delegate* unmanaged<uint, XINPUT_VIBRATION*, uint>)NativeLibrary.GetExport(lib, "XInputSetState");
    }

    public unsafe GamepadState GetState(int index)
    {
        if (index < 0 || index > 3) return default;
        // Disconnected pads are probed at most every 2s (XInputGetState is slow for absent devices).
        if (!_connected[index] && DateTime.UtcNow < _nextProbe[index]) return default;
        XINPUT_STATE st;
        var r = _getState((uint)index, &st);
        if (r != 0) { _connected[index] = false; _nextProbe[index] = DateTime.UtcNow.AddSeconds(2); return default; }
        _connected[index] = true;
        var g = st.Gamepad;
        return new GamepadState
        {
            Connected = true, Buttons = (GamepadButtons)g.wButtons,
            LeftX = g.sThumbLX, LeftY = g.sThumbLY, RightX = g.sThumbRX, RightY = g.sThumbRY,
            LeftTrigger = g.bLeftTrigger, RightTrigger = g.bRightTrigger,
        };
    }

    public unsafe void SetRumble(int index, ushort lowFrequency, ushort highFrequency)
    {
        if (index < 0 || index > 3 || !_connected[index]) return;
        var v = new XINPUT_VIBRATION { wLeftMotorSpeed = lowFrequency, wRightMotorSpeed = highFrequency };
        _setState((uint)index, &v);
    }

    public string DeviceName(int index) => _connected[index] ? $"XInput Controller {index + 1}" : "";
}
