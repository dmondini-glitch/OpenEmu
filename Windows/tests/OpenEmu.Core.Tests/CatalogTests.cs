using OpenEmu.Core.Cores;
using OpenEmu.Core.Input;
using OpenEmu.Core.Systems;
using Xunit;

namespace OpenEmu.Core.Tests;

public class CatalogTests
{
    [Fact]
    public void All42OpenEmuSystemsArePresent()
    {
        Assert.Equal(42, SystemCatalog.All.Count);
        foreach (var id in new[] { "nes", "snes", "n64", "gb", "gba", "nds", "psx", "ps2", "psp", "saturn", "dc", "gc", "arcade", "c64", "msx", "vectrex", "ws", "vb", "3do", "jaguar" })
            Assert.NotNull(SystemCatalog.Find(id));
    }

    [Fact]
    public void EverySystemHasAtLeastOneCoreWithWindowsDownload()
    {
        foreach (var s in SystemCatalog.All)
        {
            Assert.NotEmpty(s.Cores);
            foreach (var c in s.Cores)
            {
                var def = CoreManifest.Find(c);
                Assert.True(def != null, $"{s.Id}: core {c} missing from manifest");
                Assert.True(def!.Download.ContainsKey("windows-x64"), $"{c}: no windows-x64 download");
            }
        }
    }

    [Fact]
    public void EveryControlMapsToAValidRetroPadTarget()
    {
        foreach (var s in SystemCatalog.All)
            foreach (var c in s.Controls)
            {
                Assert.True(s.LibretroMap.ContainsKey(c.Name), $"{s.Id}: {c.Name} has no RetroPad mapping");
                var t = RetroTarget.Parse(s.LibretroMap[c.Name]);
                Assert.True(t.IsAnalog || t.Id <= 15);
            }
    }

    [Fact]
    public void Player1KeyboardDefaultsExistForEverySystem()
    {
        foreach (var s in SystemCatalog.All)
        {
            Assert.NotEmpty(s.Keyboard);
            // every keyboard entry refers to a real control
            foreach (var k in s.Keyboard.Keys) Assert.Contains(s.Controls, c => c.Name == k);
        }
    }

    [Fact]
    public void ExtensionsLookupWorks()
    {
        Assert.Single(SystemCatalog.ByExtension("nes"));
        Assert.Single(SystemCatalog.ByExtension(".sfc"));
        Assert.True(SystemCatalog.ByExtension("bin").Count > 3);
        Assert.Contains("openemu.system.snes", SystemCatalog.ByExtension("smc").Select(s => s.Id));
    }

    [Fact]
    public void CoreManifestHasFirmwareInfoForBiosSystems()
    {
        var psx = CoreManifest.Find("mednafen_psx_hw")!;
        Assert.Contains(psx.Firmware, f => f.Path.Contains("scph5501", StringComparison.OrdinalIgnoreCase));
        Assert.True(psx.HwRender);
        Assert.False(CoreManifest.Find("fceumm")!.HwRender);
        Assert.Equal(OperatingSystem.IsWindows() ? "x_libretro.dll" : OperatingSystem.IsMacOS() ? "x_libretro.dylib" : "x_libretro.so", CoreManifest.LibraryFileName("x"));
    }
}
