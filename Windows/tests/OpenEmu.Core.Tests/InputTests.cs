using OpenEmu.Core.Input;
using OpenEmu.Core.Libretro;
using OpenEmu.Core.Systems;
using Xunit;

namespace OpenEmu.Core.Tests;

public class InputTests
{
    [Fact]
    public void KeyboardDefaultsDriveRetroPad()
    {
        var pads = new VirtualGamepadProvider();
        var im = new InputManager(pads);
        im.SetSystem(SystemCatalog.Find("nes"));
        Assert.Equal(0, im.GetState(0, Retro.DeviceJoypad, 0, Retro.JoypadA));
        im.Keyboard.Set(HidKeys.A, true); // OpenEmu default: A key → NES A
        im.Poll();
        Assert.Equal(1, im.GetState(0, Retro.DeviceJoypad, 0, Retro.JoypadA));
        Assert.Equal(0, im.GetState(0, Retro.DeviceJoypad, 0, Retro.JoypadB));
        im.Keyboard.Set(HidKeys.Up, true);
        Assert.Equal(1, im.GetState(0, Retro.DeviceJoypad, 0, Retro.JoypadUp));
        // player 2 has no keyboard defaults
        Assert.Equal(0, im.GetState(1, Retro.DeviceJoypad, 0, Retro.JoypadA));
    }

    [Fact]
    public void GamepadDefaultsFollowRetroPadLayoutPerPlayer()
    {
        var pads = new VirtualGamepadProvider();
        var im = new InputManager(pads);
        im.SetSystem(SystemCatalog.Find("snes"));
        pads.Set(1, new GamepadState { Connected = true, Buttons = GamepadButtons.A | GamepadButtons.DPadLeft, LeftX = -30000 });
        im.Poll();
        Assert.Equal(1, im.GetState(1, Retro.DeviceJoypad, 0, Retro.JoypadB)); // Xbox A = RetroPad B
        Assert.Equal(1, im.GetState(1, Retro.DeviceJoypad, 0, Retro.JoypadLeft));
        Assert.Equal(0, im.GetState(0, Retro.DeviceJoypad, 0, Retro.JoypadB));
    }

    [Fact]
    public void AnalogSticksAndGenesisLayout()
    {
        var pads = new VirtualGamepadProvider();
        var im = new InputManager(pads);
        im.SetSystem(SystemCatalog.Find("n64"));
        pads.Set(0, new GamepadState { Connected = true, LeftX = 20000, LeftY = 30000, Buttons = GamepadButtons.B });
        im.Poll();
        Assert.True(im.GetState(0, Retro.DeviceAnalog, Retro.AnalogIndexLeft, Retro.AnalogIdX) > 15000);
        Assert.True(im.GetState(0, Retro.DeviceAnalog, Retro.AnalogIndexLeft, Retro.AnalogIdY) < -15000); // XInput up → libretro negative
        Assert.Equal(1, im.GetState(0, Retro.DeviceJoypad, 0, Retro.JoypadA)); // Xbox B = RetroPad A = N64 A

        im.SetSystem(SystemCatalog.Find("sg"));
        pads.Set(0, new GamepadState { Connected = true, Buttons = GamepadButtons.X });
        im.Poll();
        Assert.Equal(1, im.GetState(0, Retro.DeviceJoypad, 0, Retro.JoypadY)); // Genesis A → RetroPad Y ← Xbox X
    }

    [Fact]
    public void UserBindingsOverrideDefaults()
    {
        var im = new InputManager(new VirtualGamepadProvider());
        im.UserBindings.Set("openemu.system.nes", 0, "OENESButtonA", new List<InputBinding> { InputBinding.Key(HidKeys.Z) });
        im.SetSystem(SystemCatalog.Find("nes"));
        im.Keyboard.Set(HidKeys.A, true);
        Assert.Equal(0, im.GetState(0, Retro.DeviceJoypad, 0, Retro.JoypadA));
        im.Keyboard.Set(HidKeys.Z, true);
        Assert.Equal(1, im.GetState(0, Retro.DeviceJoypad, 0, Retro.JoypadA));
        Assert.Equal("Z", im.BindingsFor("openemu.system.nes", 0, "OENESButtonA")[0].DisplayName);
    }

    [Fact]
    public void BitmaskAndKeyboardDevice()
    {
        var im = new InputManager(new VirtualGamepadProvider());
        im.SetSystem(SystemCatalog.Find("c64"));
        im.Keyboard.Set(HidKeys.Q, true);
        Assert.Equal(1, im.GetState(0, Retro.DeviceKeyboard, 0, 'q'));
        Assert.Equal(0, im.GetState(0, Retro.DeviceKeyboard, 0, 'w'));
        Assert.Equal(282u, HidKeys.ToRetroKey(HidKeys.F1));
        Assert.Equal("Left Shift", HidKeys.Name(HidKeys.LeftShift));
    }
}
