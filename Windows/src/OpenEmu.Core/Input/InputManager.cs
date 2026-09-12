using OpenEmu.Core.Libretro;
using OpenEmu.Core.Systems;

namespace OpenEmu.Core.Input;

/// <summary>Live keyboard state fed by the UI layer (HID usages).</summary>
public sealed class KeyboardState
{
    private readonly bool[] _down = new bool[256];
    private readonly HashSet<int> _justPressed = new();
    public void Set(int usage, bool down)
    {
        if (usage < 0 || usage > 255) return;
        lock (_justPressed) { if (down && !_down[usage]) _justPressed.Add(usage); }
        _down[usage] = down;
    }
    public bool IsDown(int usage) => usage >= 0 && usage < 256 && _down[usage];
    public void Clear() { Array.Clear(_down); lock (_justPressed) _justPressed.Clear(); }
    /// <summary>Returns and clears the set of keys pressed since last call (used by "press a key" binding capture).</summary>
    public int[] TakeJustPressed() { lock (_justPressed) { var a = _justPressed.ToArray(); _justPressed.Clear(); return a; } }
    public IEnumerable<int> Down { get { for (var i = 0; i < 256; i++) if (_down[i]) yield return i; } }
}

/// <summary>Pointer/touch state (NDS touch screen, lightguns). Coordinates in libretro range -32767..32767.</summary>
public sealed class PointerState { public volatile int X; public volatile int Y; public volatile bool Pressed; }

/// <summary>Per-system control bindings: system → player → control name → bindings.</summary>
public sealed class BindingSet
{
    public Dictionary<string, Dictionary<int, Dictionary<string, List<InputBinding>>>> Systems { get; set; } = new();

    public List<InputBinding>? Get(string systemId, int player, string control)
        => Systems.TryGetValue(systemId, out var s) && s.TryGetValue(player, out var p) && p.TryGetValue(control, out var b) ? b : null;

    public void Set(string systemId, int player, string control, List<InputBinding> bindings)
    {
        if (!Systems.TryGetValue(systemId, out var s)) Systems[systemId] = s = new();
        if (!s.TryGetValue(player, out var p)) s[player] = p = new();
        p[control] = bindings;
    }

    public void Clear(string systemId, int player, string control) => Systems.GetValueOrDefault(systemId)?.GetValueOrDefault(player)?.Remove(control);
    public void ResetSystem(string systemId) => Systems.Remove(systemId);
}

/// <summary>
/// Resolves system controls → physical inputs and answers libretro input_state queries.
/// Player 1 defaults to OpenEmu's keyboard map; every player defaults to gamepad N via the RetroPad layout.
/// </summary>
public sealed class InputManager : IInputSource
{
    public const int MaxPlayers = 4;
    public KeyboardState Keyboard { get; } = new();
    public PointerState Pointer { get; } = new();
    public IGamepadProvider Gamepads { get; set; }
    public BindingSet UserBindings { get; set; } = new();
    public bool KeyboardPassthrough { get; set; } // when true, keyboard goes to the core's keyboard device (computers), not to RetroPad

    private SystemDefinition? _system;
    private readonly GamepadState[] _pads = new GamepadState[MaxPlayers];
    // resolved per player: retro button id → bindings ; analog (index,axis,sign) → bindings
    private readonly Dictionary<uint, List<InputBinding>>[] _buttons = new Dictionary<uint, List<InputBinding>>[MaxPlayers];
    private readonly Dictionary<(uint, uint, int), List<InputBinding>>[] _analog = new Dictionary<(uint, uint, int), List<InputBinding>>[MaxPlayers];
    public event Action<uint, uint, ushort>? RumbleRequested;

    public InputManager(IGamepadProvider? gamepads = null)
    {
        Gamepads = gamepads ?? (XInputProvider.IsAvailable ? TryXInput() : new NullGamepadProvider());
        for (var i = 0; i < MaxPlayers; i++) { _buttons[i] = new(); _analog[i] = new(); }
    }

    private static IGamepadProvider TryXInput() { try { return new XInputProvider(); } catch { return new NullGamepadProvider(); } }

    public SystemDefinition? System => _system;

    public void SetSystem(SystemDefinition? system)
    {
        _system = system;
        Rebuild();
    }

    /// <summary>Effective bindings for a control (user override or defaults).</summary>
    public List<InputBinding> BindingsFor(string systemId, int player, string control)
    {
        var user = UserBindings.Get(systemId, player, control);
        if (user != null) return user;
        return DefaultBindings(systemId, player, control);
    }

    public List<InputBinding> DefaultBindings(string systemId, int player, string control)
    {
        var list = new List<InputBinding>();
        var sys = SystemCatalog.Find(systemId);
        if (sys == null) return list;
        if (player == 0 && sys.Keyboard.TryGetValue(control, out var hid)) list.Add(InputBinding.Key(hid));
        if (sys.LibretroMap.TryGetValue(control, out var target))
        {
            var t = RetroTarget.Parse(target);
            var b = DefaultGamepadBinding(t);
            if (b != null) list.Add(b);
        }
        return list;
    }

    /// <summary>Standard RetroPad → Xbox layout.</summary>
    public static InputBinding? DefaultGamepadBinding(RetroTarget t)
    {
        if (t.IsAnalog)
        {
            var axis = (t.AnalogIndex, t.AnalogAxis) switch { (0u, 0u) => GamepadAxis.LeftX, (0u, 1u) => GamepadAxis.LeftY, (1u, 0u) => GamepadAxis.RightX, _ => GamepadAxis.RightY };
            return InputBinding.Axis(axis, t.Sign);
        }
        return t.Id switch
        {
            Retro.JoypadB => InputBinding.Button(GamepadButtons.A), Retro.JoypadA => InputBinding.Button(GamepadButtons.B),
            Retro.JoypadY => InputBinding.Button(GamepadButtons.X), Retro.JoypadX => InputBinding.Button(GamepadButtons.Y),
            Retro.JoypadL => InputBinding.Button(GamepadButtons.LeftShoulder), Retro.JoypadR => InputBinding.Button(GamepadButtons.RightShoulder),
            Retro.JoypadL2 => InputBinding.Axis(GamepadAxis.LeftTrigger, 1), Retro.JoypadR2 => InputBinding.Axis(GamepadAxis.RightTrigger, 1),
            Retro.JoypadL3 => InputBinding.Button(GamepadButtons.LeftThumb), Retro.JoypadR3 => InputBinding.Button(GamepadButtons.RightThumb),
            Retro.JoypadStart => InputBinding.Button(GamepadButtons.Start), Retro.JoypadSelect => InputBinding.Button(GamepadButtons.Back),
            Retro.JoypadUp => InputBinding.Button(GamepadButtons.DPadUp), Retro.JoypadDown => InputBinding.Button(GamepadButtons.DPadDown),
            Retro.JoypadLeft => InputBinding.Button(GamepadButtons.DPadLeft), Retro.JoypadRight => InputBinding.Button(GamepadButtons.DPadRight),
            _ => null,
        };
    }

    public void Rebuild()
    {
        for (var p = 0; p < MaxPlayers; p++) { _buttons[p].Clear(); _analog[p].Clear(); }
        if (_system == null) return;
        foreach (var group in _system.ControlGroups)
            foreach (var control in group)
            {
                if (!_system.LibretroMap.TryGetValue(control.Name, out var target)) continue;
                var t = RetroTarget.Parse(target);
                for (var p = 0; p < MaxPlayers; p++)
                {
                    var b = BindingsFor(_system.Id, p, control.Name);
                    if (b.Count == 0) continue;
                    if (t.IsAnalog) { var key = (t.AnalogIndex, t.AnalogAxis, t.Sign); if (!_analog[p].TryGetValue(key, out var l)) _analog[p][key] = l = new(); l.AddRange(b); }
                    else { if (!_buttons[p].TryGetValue(t.Id, out var l)) _buttons[p][t.Id] = l = new(); l.AddRange(b); }
                }
            }
    }

    // ---- IInputSource
    public void Poll()
    {
        for (var i = 0; i < MaxPlayers && i < Gamepads.MaxDevices; i++) _pads[i] = Gamepads.GetState(i);
    }

    public short GetState(uint port, uint device, uint index, uint id)
    {
        if (port >= MaxPlayers) return 0;
        var p = (int)port;
        switch (device)
        {
            case Retro.DeviceJoypad:
                return _buttons[p].TryGetValue(id, out var list) && AnyActive(list, p) ? (short)1 : (short)0;
            case Retro.DeviceAnalog:
                if (index == Retro.AnalogIndexButton) return _buttons[p].TryGetValue(id, out var bl) ? Strength(bl, p) : (short)0;
                {
                    var plus = _analog[p].TryGetValue((index, id, 1), out var pl) ? Strength(pl, p) : 0;
                    var minus = _analog[p].TryGetValue((index, id, -1), out var ml) ? Strength(ml, p) : 0;
                    return (short)Math.Clamp(plus - minus, -32767, 32767);
                }
            case Retro.DeviceKeyboard:
                if (!KeyboardPassthrough && p != 0) return 0;
                return IsRetroKeyDown(id) ? (short)1 : (short)0;
            case Retro.DevicePointer:
                if (index != 0) return 0;
                return id switch { Retro.PointerX => (short)Pointer.X, Retro.PointerY => (short)Pointer.Y, Retro.PointerPressed => (short)(Pointer.Pressed ? 1 : 0), _ => (short)0 };
            case Retro.DeviceMouse:
                return 0;
            default: return 0;
        }
    }

    private bool IsRetroKeyDown(uint retroKey)
    {
        foreach (var hid in Keyboard.Down) if (HidKeys.ToRetroKey(hid) == retroKey) return true;
        return false;
    }

    private bool AnyActive(List<InputBinding> list, int player)
    {
        foreach (var b in list) if (Strength(b, player) > 16000) return true;
        return false;
    }

    private short Strength(List<InputBinding> list, int player)
    {
        var max = 0;
        foreach (var b in list) max = Math.Max(max, Strength(b, player));
        return (short)max;
    }

    private int Strength(InputBinding b, int player)
    {
        switch (b.Kind)
        {
            case BindingKind.Keyboard:
                return !KeyboardPassthrough && Keyboard.IsDown(b.Code) ? 32767 : 0;
            case BindingKind.GamepadButton:
            {
                var pad = PadFor(b, player);
                return pad.Connected && pad.IsPressed((GamepadButtons)b.Code) ? 32767 : 0;
            }
            case BindingKind.GamepadAxis:
            {
                var pad = PadFor(b, player);
                if (!pad.Connected) return 0;
                var v = pad.Axis((GamepadAxis)b.Code);
                var axis = (GamepadAxis)b.Code;
                // XInput Y axes point up; libretro Y axes point down.
                if (axis is GamepadAxis.LeftY or GamepadAxis.RightY) v = -v;
                var s = b.Direction < 0 ? -v : v;
                if (s < 8000) return 0; // deadzone
                return Math.Min(32767, s);
            }
        }
        return 0;
    }

    private GamepadState PadFor(InputBinding b, int player)
    {
        var idx = b.Device >= 0 ? b.Device : player;
        return idx < MaxPlayers ? _pads[idx] : default;
    }

    public void Rumble(uint port, uint effect, ushort strength)
    {
        if (port < (uint)Gamepads.MaxDevices)
        {
            // effect 0 = strong (low freq), 1 = weak (high freq)
            if (effect == 0) Gamepads.SetRumble((int)port, strength, 0); else Gamepads.SetRumble((int)port, 0, strength);
        }
        RumbleRequested?.Invoke(port, effect, strength);
    }
}
