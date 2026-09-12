using OpenEmu.Core.Config;
using OpenEmu.Core.Input;
using OpenEmu.Core.Libretro;
using OpenEmu.Core.Localization;
using Xunit;

namespace OpenEmu.Core.Tests;

public class SettingsAndLocalizationTests
{
    [Fact]
    public void SettingsRoundTripIncludingBindings()
    {
        var path = Path.Combine(Path.GetTempPath(), "openemu-settings-" + Guid.NewGuid().ToString("N") + ".json");
        var s = new AppSettings { Language = "pt-BR" };
        s.DefaultCores["openemu.system.nes"] = "nestopia";
        s.CoreOptions["fceumm"] = new() { ["fceumm_palette"] = "asqrealc" };
        s.Bindings.Set("openemu.system.nes", 0, "OENESButtonA", new List<InputBinding> { InputBinding.Key(HidKeys.Z), InputBinding.Button(GamepadButtons.B) });
        SettingsStore.Save(s, path);
        var l = SettingsStore.Load(path);
        Assert.Equal("pt-BR", l.Language);
        Assert.Equal("nestopia", l.DefaultCores["openemu.system.nes"]);
        Assert.Equal("asqrealc", l.CoreOptions["fceumm"]["fceumm_palette"]);
        var b = l.Bindings.Get("openemu.system.nes", 0, "OENESButtonA")!;
        Assert.Equal(2, b.Count);
        Assert.Equal(BindingKind.GamepadButton, b[1].Kind);
        File.Delete(path);
    }

    [Fact]
    public void LocalizationFallsBackToEnglish()
    {
        L.SetLanguage("pt-BR");
        Assert.Equal("Jogar", L.T("game.play"));
        Assert.Equal("missing.key", L.T("missing.key"));
        L.SetLanguage("en");
        Assert.Equal("Play", L.T("game.play"));
        Assert.Equal("3 games", L.T("library.games", 3));
    }

    [Fact]
    public void PrintfLiteFormatsIntegersAndStrings()
    {
        Assert.Equal("? and ?", PrintfLite.Strip("%s and %d"));
        Assert.Equal("100%", PrintfLite.Strip("100%%"));
        Assert.Equal("v=42 x=2a", PrintfLite.Format("v=%d x=%x", (IntPtr)42, (IntPtr)42));
    }
}
