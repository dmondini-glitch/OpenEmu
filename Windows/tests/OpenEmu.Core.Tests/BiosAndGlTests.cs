using OpenEmu.Core.Bios;
using OpenEmu.Core.Cores;
using OpenEmu.Core.Systems;
using OpenEmu.Core.Video;
using Xunit;

namespace OpenEmu.Core.Tests;

public class BiosAndGlTests
{
    [Fact]
    public void OpenBiosIsBundledAndInstalledAsPlayStationBios()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openemu-bios-" + Guid.NewGuid().ToString("N"));
        var written = FreeSystemFiles.InstallOpenBios(dir);
        Assert.Equal(3, written.Count);
        foreach (var f in FreeSystemFiles.OpenBiosTargets)
        {
            var p = Path.Combine(dir, f);
            Assert.True(File.Exists(p));
            Assert.Equal(512 * 1024, new FileInfo(p).Length);
            Assert.True(FreeSystemFiles.IsOpenBios(p));
        }
        Assert.True(File.Exists(Path.Combine(dir, "openbios-LICENSE.txt")));
        Assert.Empty(FreeSystemFiles.InstallOpenBios(dir)); // idempotent
        var bios = new BiosManager(dir);
        var psx = SystemCatalog.Find("psx")!;
        var beetle = CoreManifest.Find("mednafen_psx_hw")!;
        Assert.Empty(bios.Blocking(psx, beetle));
        var st = bios.Status(new[] { psx });
        Assert.Contains(st, s => s.Firmware.Path.Equals("scph5501.bin", StringComparison.OrdinalIgnoreCase) && s.Present && s.IsOpenBios);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void HleCoresAreNeverBlockedByMissingBios()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openemu-bios-empty-" + Guid.NewGuid().ToString("N"));
        var bios = new BiosManager(dir);
        Assert.Empty(bios.Blocking(SystemCatalog.Find("psx")!, CoreManifest.Find("pcsx_rearmed")!));
        Assert.Empty(bios.Blocking(SystemCatalog.Find("dc")!, CoreManifest.Find("flycast")!));
        Assert.Empty(bios.Blocking(SystemCatalog.Find("ps2")!, CoreManifest.Find("play")!));
        Assert.NotEmpty(bios.Blocking(SystemCatalog.Find("psx")!, CoreManifest.Find("mednafen_psx_hw")!)); // until OpenBIOS is installed
    }

    [Fact]
    public void OpenSourceFirmwareDefaultsAreAppliedWhenProprietaryFilesAreMissing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openemu-bios-opts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Assert.Equal("enabled", FreeSystemFiles.DefaultCoreOptions("kronos", dir)["kronos_force_hle_bios"]);
        Assert.Equal("enabled", FreeSystemFiles.DefaultCoreOptions("yabause", dir)["yabause_force_hle_bios"]);
        Assert.Equal("builtin", FreeSystemFiles.DefaultCoreOptions("melondsds", dir)["melonds_sysfile_mode"]);
        Assert.Equal("AltirraOS", FreeSystemFiles.DefaultCoreOptions("atari800", dir)["atari800_os_xl"]);
        Directory.CreateDirectory(Path.Combine(dir, "kronos")); File.WriteAllBytes(Path.Combine(dir, "kronos", "saturn_bios.bin"), new byte[16]);
        Assert.False(FreeSystemFiles.DefaultCoreOptions("kronos", dir).ContainsKey("kronos_force_hle_bios"));
        Assert.Empty(FreeSystemFiles.DefaultCoreOptions("fceumm", dir));
        // systems default to cores that boot without proprietary BIOS
        Assert.Equal("play", SystemCatalog.Find("ps2")!.DefaultCore);
        Assert.Equal("kronos", SystemCatalog.Find("saturn")!.DefaultCore);
        Assert.True(FreeSystemFiles.CanRunWithoutBios(SystemCatalog.Find("dc")!.DefaultCore));
        Assert.True(FreeSystemFiles.CanRunWithoutBios(SystemCatalog.Find("nds")!.DefaultCore));
        Assert.True(FreeSystemFiles.CanRunWithoutBios(SystemCatalog.Find("atari8bit")!.DefaultCore));
        foreach (var (sysId, _, available) in FreeSystemFiles.Coverage) Assert.NotNull(SystemCatalog.Find(sysId));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void GlDestRectKeepsAspectAndIntegralScale()
    {
        var (x0, y0, x1, y1) = GlBlitter.DestRect(1920, 1080, 320, 240, 4f / 3f, true, false);
        Assert.Equal(1080, y1 - y0); Assert.Equal(1440, x1 - x0); Assert.Equal(240, x0);
        (x0, y0, x1, y1) = GlBlitter.DestRect(1920, 1080, 320, 240, 4f / 3f, true, true);
        Assert.Equal(960, y1 - y0); // 4× integral
        (x0, y0, x1, y1) = GlBlitter.DestRect(800, 600, 320, 240, 0, false, false);
        Assert.Equal((0, 0, 800, 600), (x0, y0, x1, y1));
    }
}
