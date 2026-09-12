using Avalonia.Input;
using OpenEmu.Core.Input;

namespace OpenEmu.App.Services;

/// <summary>Avalonia physical keys → USB HID usage ids (what OpenEmu's keyboard maps use).</summary>
public static class KeyMap
{
    public static int ToHid(PhysicalKey key)
    {
        if (key >= PhysicalKey.A && key <= PhysicalKey.Z) return HidKeys.A + (key - PhysicalKey.A);
        if (key >= PhysicalKey.Digit1 && key <= PhysicalKey.Digit9) return HidKeys.D1 + (key - PhysicalKey.Digit1);
        if (key >= PhysicalKey.F1 && key <= PhysicalKey.F12) return HidKeys.F1 + (key - PhysicalKey.F1);
        if (key >= PhysicalKey.NumPad1 && key <= PhysicalKey.NumPad9) return HidKeys.Kp1 + (key - PhysicalKey.NumPad1);
        return key switch
        {
            PhysicalKey.Digit0 => HidKeys.D0, PhysicalKey.Enter => HidKeys.Enter, PhysicalKey.Escape => HidKeys.Escape, PhysicalKey.Backspace => HidKeys.Backspace,
            PhysicalKey.Tab => HidKeys.Tab, PhysicalKey.Space => HidKeys.Space, PhysicalKey.Minus => HidKeys.Minus, PhysicalKey.Equal => HidKeys.Equals,
            PhysicalKey.BracketLeft => HidKeys.LeftBracket, PhysicalKey.BracketRight => HidKeys.RightBracket, PhysicalKey.Backslash => HidKeys.Backslash,
            PhysicalKey.Semicolon => HidKeys.Semicolon, PhysicalKey.Quote => HidKeys.Quote, PhysicalKey.Backquote => HidKeys.Grave, PhysicalKey.Comma => HidKeys.Comma,
            PhysicalKey.Period => HidKeys.Period, PhysicalKey.Slash => HidKeys.Slash, PhysicalKey.CapsLock => HidKeys.CapsLock, PhysicalKey.PrintScreen => HidKeys.PrintScreen,
            PhysicalKey.ScrollLock => HidKeys.ScrollLock, PhysicalKey.Pause => HidKeys.Pause, PhysicalKey.Insert => HidKeys.Insert, PhysicalKey.Home => HidKeys.Home,
            PhysicalKey.PageUp => HidKeys.PageUp, PhysicalKey.Delete => HidKeys.Delete, PhysicalKey.End => HidKeys.End, PhysicalKey.PageDown => HidKeys.PageDown,
            PhysicalKey.ArrowRight => HidKeys.Right, PhysicalKey.ArrowLeft => HidKeys.Left, PhysicalKey.ArrowDown => HidKeys.Down, PhysicalKey.ArrowUp => HidKeys.Up,
            PhysicalKey.NumLock => HidKeys.NumLock, PhysicalKey.NumPadDivide => HidKeys.KpDivide, PhysicalKey.NumPadMultiply => HidKeys.KpMultiply,
            PhysicalKey.NumPadSubtract => HidKeys.KpMinus, PhysicalKey.NumPadAdd => HidKeys.KpPlus, PhysicalKey.NumPadEnter => HidKeys.KpEnter,
            PhysicalKey.NumPad0 => HidKeys.Kp0, PhysicalKey.NumPadDecimal => HidKeys.KpPeriod,
            PhysicalKey.ControlLeft => HidKeys.LeftControl, PhysicalKey.ShiftLeft => HidKeys.LeftShift, PhysicalKey.AltLeft => HidKeys.LeftAlt, PhysicalKey.MetaLeft => HidKeys.LeftGui,
            PhysicalKey.ControlRight => HidKeys.RightControl, PhysicalKey.ShiftRight => HidKeys.RightShift, PhysicalKey.AltRight => HidKeys.RightAlt, PhysicalKey.MetaRight => HidKeys.RightGui,
            _ => 0,
        };
    }
}
