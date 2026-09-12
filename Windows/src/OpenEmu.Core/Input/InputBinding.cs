using System.Text.Json.Serialization;

namespace OpenEmu.Core.Input;

public enum BindingKind { Keyboard, GamepadButton, GamepadAxis }

/// <summary>A physical input bound to a system control. Keyboard codes are USB HID usage ids (same as OpenEmu's Keyboard-Mappings.plist).</summary>
public sealed record InputBinding
{
    public BindingKind Kind { get; init; }
    /// <summary>HID usage (keyboard), GamepadButtons flag value (button) or GamepadAxis (axis).</summary>
    public int Code { get; init; }
    /// <summary>Gamepad index (0-3); -1 = "the player's gamepad".</summary>
    public int Device { get; init; } = -1;
    /// <summary>Axis direction: +1 / -1 (axes only).</summary>
    public int Direction { get; init; }

    public static InputBinding Key(int hidUsage) => new() { Kind = BindingKind.Keyboard, Code = hidUsage };
    public static InputBinding Button(GamepadButtons b, int device = -1) => new() { Kind = BindingKind.GamepadButton, Code = (int)b, Device = device };
    public static InputBinding Axis(GamepadAxis a, int direction, int device = -1) => new() { Kind = BindingKind.GamepadAxis, Code = (int)a, Direction = direction, Device = device };

    [JsonIgnore]
    public string DisplayName => Kind switch
    {
        BindingKind.Keyboard => HidKeys.Name(Code),
        BindingKind.GamepadButton => ((GamepadButtons)Code).ToString(),
        BindingKind.GamepadAxis => $"{(GamepadAxis)Code}{(Direction < 0 ? "-" : "+")}",
        _ => "?",
    };
}

/// <summary>Parsed RetroPad target of a system control ("A", "UP", "LX-", ...).</summary>
public readonly record struct RetroTarget(bool IsAnalog, uint Id, uint AnalogIndex, uint AnalogAxis, int Sign)
{
    public static RetroTarget Parse(string s)
    {
        switch (s)
        {
            case "B": return Button(0); case "Y": return Button(1); case "SELECT": return Button(2); case "START": return Button(3);
            case "UP": return Button(4); case "DOWN": return Button(5); case "LEFT": return Button(6); case "RIGHT": return Button(7);
            case "A": return Button(8); case "X": return Button(9); case "L": return Button(10); case "R": return Button(11);
            case "L2": return Button(12); case "R2": return Button(13); case "L3": return Button(14); case "R3": return Button(15);
        }
        if (s.Length == 3 && (s[0] == 'L' || s[0] == 'R') && (s[1] == 'X' || s[1] == 'Y') && (s[2] == '+' || s[2] == '-'))
            return new RetroTarget(true, 0, s[0] == 'L' ? 0u : 1u, s[1] == 'X' ? 0u : 1u, s[2] == '+' ? 1 : -1);
        throw new FormatException($"Unknown retro target '{s}'");
    }
    private static RetroTarget Button(uint id) => new(false, id, 0, 0, 0);
}

/// <summary>USB HID keyboard usage ids (page 0x07) and display names.</summary>
public static class HidKeys
{
    public const int A = 4, B = 5, C = 6, D = 7, E = 8, F = 9, G = 10, H = 11, I = 12, J = 13, K = 14, L = 15, M = 16, N = 17, O = 18, P = 19, Q = 20, R = 21, S = 22, T = 23, U = 24, V = 25, W = 26, X = 27, Y = 28, Z = 29;
    public const int D1 = 30, D2 = 31, D3 = 32, D4 = 33, D5 = 34, D6 = 35, D7 = 36, D8 = 37, D9 = 38, D0 = 39;
    public const int Enter = 40, Escape = 41, Backspace = 42, Tab = 43, Space = 44, Minus = 45, Equals = 46, LeftBracket = 47, RightBracket = 48, Backslash = 49, Semicolon = 51, Quote = 52, Grave = 53, Comma = 54, Period = 55, Slash = 56, CapsLock = 57;
    public const int F1 = 58, F2 = 59, F3 = 60, F4 = 61, F5 = 62, F6 = 63, F7 = 64, F8 = 65, F9 = 66, F10 = 67, F11 = 68, F12 = 69;
    public const int PrintScreen = 70, ScrollLock = 71, Pause = 72, Insert = 73, Home = 74, PageUp = 75, Delete = 76, End = 77, PageDown = 78, Right = 79, Left = 80, Down = 81, Up = 82;
    public const int NumLock = 83, KpDivide = 84, KpMultiply = 85, KpMinus = 86, KpPlus = 87, KpEnter = 88, Kp1 = 89, Kp2 = 90, Kp3 = 91, Kp4 = 92, Kp5 = 93, Kp6 = 94, Kp7 = 95, Kp8 = 96, Kp9 = 97, Kp0 = 98, KpPeriod = 99;
    public const int LeftControl = 224, LeftShift = 225, LeftAlt = 226, LeftGui = 227, RightControl = 228, RightShift = 229, RightAlt = 230, RightGui = 231;

    public static string Name(int usage)
    {
        if (usage >= A && usage <= Z) return ((char)('A' + usage - A)).ToString();
        if (usage >= D1 && usage <= D9) return ((char)('1' + usage - D1)).ToString();
        return usage switch
        {
            D0 => "0", Enter => "Enter", Escape => "Esc", Backspace => "Backspace", Tab => "Tab", Space => "Space", Minus => "-", Equals => "=",
            LeftBracket => "[", RightBracket => "]", Backslash => "\\", Semicolon => ";", Quote => "'", Grave => "`", Comma => ",", Period => ".", Slash => "/",
            CapsLock => "Caps Lock", PrintScreen => "Print", ScrollLock => "Scroll Lock", Pause => "Pause", Insert => "Insert", Home => "Home", PageUp => "Page Up",
            Delete => "Delete", End => "End", PageDown => "Page Down", Right => "→", Left => "←", Down => "↓", Up => "↑", NumLock => "Num Lock",
            KpDivide => "Num /", KpMultiply => "Num *", KpMinus => "Num -", KpPlus => "Num +", KpEnter => "Num Enter", KpPeriod => "Num .",
            >= F1 and <= F12 => "F" + (usage - F1 + 1), >= Kp1 and <= Kp9 => "Num " + (usage - Kp1 + 1), Kp0 => "Num 0",
            LeftControl => "Left Ctrl", LeftShift => "Left Shift", LeftAlt => "Left Alt", LeftGui => "Left Win", RightControl => "Right Ctrl", RightShift => "Right Shift", RightAlt => "Right Alt", RightGui => "Right Win",
            _ => $"Key {usage}",
        };
    }

    /// <summary>Map HID usage → libretro RETROK_* keycode (SDL1-style keysyms) for keyboard-device cores (computers).</summary>
    public static uint ToRetroKey(int usage)
    {
        if (usage >= A && usage <= Z) return (uint)('a' + usage - A);
        if (usage >= D1 && usage <= D9) return (uint)('1' + usage - D1);
        if (usage >= F1 && usage <= F12) return (uint)(282 + usage - F1);
        if (usage >= Kp1 && usage <= Kp9) return (uint)(257 + usage - Kp1);
        return usage switch
        {
            D0 => '0', Enter => 13, Escape => 27, Backspace => 8, Tab => 9, Space => 32, Minus => '-', Equals => '=', LeftBracket => '[', RightBracket => ']', Backslash => '\\',
            Semicolon => ';', Quote => '\'', Grave => '`', Comma => ',', Period => '.', Slash => '/', CapsLock => 301, Insert => 277, Home => 278, PageUp => 280, Delete => 127, End => 279, PageDown => 281,
            Right => 275, Left => 276, Down => 274, Up => 273, NumLock => 300, KpDivide => 267, KpMultiply => 268, KpMinus => 269, KpPlus => 270, KpEnter => 271, Kp0 => 256, KpPeriod => 266,
            LeftControl => 306, LeftShift => 304, LeftAlt => 308, LeftGui => 311, RightControl => 305, RightShift => 303, RightAlt => 307, RightGui => 312, PrintScreen => 316, ScrollLock => 302, Pause => 19,
            _ => 0,
        };
    }
}
