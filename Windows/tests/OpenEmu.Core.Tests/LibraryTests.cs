using OpenEmu.Core.Cheats;
using OpenEmu.Core.Library;
using OpenEmu.Core.Saves;
using OpenEmu.Core.Systems;
using OpenEmu.Core.Video;
using Xunit;

namespace OpenEmu.Core.Tests;

public class LibraryTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "openemu-tests-" + Guid.NewGuid().ToString("N"));
    public LibraryTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    private string Write(string name, byte[] data) { var p = Path.Combine(_tmp, name); File.WriteAllBytes(p, data); return p; }

    private static byte[] NesRom(int prgBanks = 1)
    {
        var rom = new byte[16 + 16384 * prgBanks];
        rom[0] = (byte)'N'; rom[1] = (byte)'E'; rom[2] = (byte)'S'; rom[3] = 0x1A; rom[4] = (byte)prgBanks;
        for (var i = 16; i < rom.Length; i++) rom[i] = (byte)(i * 7);
        return rom;
    }

    [Fact]
    public void HashingSkipsInesHeaderLikeOpenEmu()
    {
        var rom = NesRom();
        var p = Write("a.nes", rom);
        var h = RomHasher.Compute(p, "openemu.system.nes");
        Assert.Equal(16, h.HeaderSkipped);
        Assert.Equal(rom.Length - 16, h.Size);
        var expected = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(rom.AsSpan(16)));
        Assert.Equal(expected, h.Md5);
        Assert.Equal(8, h.Crc32.Length);
        // SNES copier header
        var snes = new byte[512 + 32768];
        var ps = Write("b.smc", snes);
        Assert.Equal(512, RomHasher.Compute(ps, "openemu.system.snes").HeaderSkipped);
    }

    [Fact]
    public void DetectsSystemsByExtensionAndSignature()
    {
        Assert.Equal("openemu.system.nes", SystemDetector.Detect(Write("x.nes", NesRom()))!.Id);
        var md = new byte[0x200]; "SEGA MEGA DRIVE "u8.ToArray().CopyTo(md, 0x100);
        Assert.Equal("openemu.system.sg", SystemDetector.Detect(Write("y.bin", md))!.Id);
        var x32 = new byte[0x200]; "SEGA 32X        "u8.ToArray().CopyTo(x32, 0x100);
        Assert.Equal("openemu.system.32x", SystemDetector.Detect(Write("z.bin", x32))!.Id);
        var sms = new byte[0x8000]; "TMR SEGA"u8.ToArray().CopyTo(sms, 0x7FF0);
        Assert.Equal("openemu.system.sms", SystemDetector.Detect(Write("s.bin", sms))!.Id);
        var a26 = new byte[4096];
        Assert.Equal("openemu.system.2600", SystemDetector.Detect(Write("t.bin", a26))!.Id);
        // PSX disc via cue
        var iso = new byte[0x9400]; "  Sony Computer Entertainment Inc."u8.ToArray().CopyTo(iso, 0x9340);
        var bin = Write("game.bin", iso);
        var cue = Write("game.cue", System.Text.Encoding.ASCII.GetBytes("FILE \"game.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n"));
        Assert.Equal("openemu.system.psx", SystemDetector.Detect(cue)!.Id);
        Assert.Equal(bin, SystemDetector.FirstTrackFile(cue));
        Assert.True(SystemDetector.Candidates(Write("u.rom", new byte[100])).Count > 1);
    }

    [Fact]
    public async Task ImporterAddsGamesAndDetectsDuplicates()
    {
        using var lib = new GameLibrary(Path.Combine(_tmp, "lib.sqlite"));
        var imp = new GameImporter(lib) { CopyToLibrary = true, RomsDir = Path.Combine(_tmp, "roms"), DownloadCovers = false };
        var rom = Write("Super Game (USA) [!].nes", NesRom());
        var res = await imp.ImportAsync(new[] { rom });
        Assert.Single(res);
        Assert.True(res[0].Success, res[0].Error);
        Assert.Equal("Super Game", res[0].Game!.Title);
        Assert.StartsWith(Path.Combine(_tmp, "roms"), res[0].Game!.RomPath);
        Assert.True(File.Exists(res[0].Game!.RomPath));
        // duplicate content under another name
        var dup = Write("copy.nes", NesRom());
        var res2 = await imp.ImportAsync(new[] { dup });
        Assert.Equal("Duplicate of an existing game", res2[0].Error);
        Assert.Equal(1, lib.Count());
        // zip with a single rom is extracted
        var zipPath = Path.Combine(_tmp, "other.zip");
        using (var z = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            var e = z.CreateEntry("Other Game.nes");
            using var s = e.Open(); s.Write(NesRom(2));
        }
        var res3 = await imp.ImportAsync(new[] { zipPath });
        Assert.True(res3[0].Success, res3[0].Error);
        Assert.Equal("Other Game", res3[0].Game!.Title);
        Assert.Equal(2, lib.Count());
        Assert.Equal(2, lib.CountBySystem()["openemu.system.nes"]);
        // collections + cheats + stats
        var col = lib.AddCollection("Favorites");
        lib.AddToCollection(col.Id, res3[0].Game!.Id);
        Assert.Single(lib.GamesInCollection(col.Id));
        var cheat = lib.AddCheat(new Cheat { GameId = res3[0].Game!.Id, Description = "Lives", Code = Cheat.NormalizeCode("SXIOPO\nAAAA"), Enabled = true });
        Assert.Equal("SXIOPO+AAAA", lib.CheatsFor(res3[0].Game!.Id)[0].Code);
        lib.RecordPlay(res3[0].Game!.Id, TimeSpan.FromMinutes(5));
        Assert.Equal(1, lib.Get(res3[0].Game!.Id)!.PlayCount);
        Assert.Single(lib.All(search: "other"));
        Assert.Equal(res3[0].Game!.Id, lib.RecentlyPlayed()[0].Id);
    }

    [Fact]
    public void SaveStatesAndBatterySavesRoundTrip()
    {
        var states = new SaveStateManager(Path.Combine(_tmp, "states"));
        var px = new int[8 * 8]; Array.Fill(px, unchecked((int)0xFFFF0000));
        var info = states.Write("openemu.system.nes", "ABC", "Level 3", new byte[] { 1, 2, 3 }, PngEncoder.Encode(px, 8, 8), "fceumm");
        Assert.True(File.Exists(info.Path));
        Assert.True(File.Exists(info.ScreenshotPath!));
        var q = states.Write("openemu.system.nes", "ABC", "Quick Save 2", new byte[] { 9 }, null, "fceumm", 2);
        var list = states.List("openemu.system.nes", "ABC");
        Assert.Equal(2, list.Count);
        Assert.Equal(q.Path, states.FindSlot("openemu.system.nes", "ABC", 2)!.Path);
        Assert.Null(states.FindAutoSave("openemu.system.nes", "ABC"));
        states.Rename(list[0], "Renamed");
        Assert.Contains(states.List("openemu.system.nes", "ABC"), s => s.Name == "Renamed");
        states.Delete(list[0]);
        Assert.Single(states.List("openemu.system.nes", "ABC"));

        var battery = new BatterySaveManager(Path.Combine(_tmp, "saves"));
        battery.Save("openemu.system.gb", "K", new byte[] { 5, 6, 7 });
        Assert.Equal(new byte[] { 5, 6, 7 }, battery.Load("openemu.system.gb", "K"));
        Assert.Null(battery.Load("openemu.system.gb", "missing"));
    }

    [Fact]
    public void PngEncoderProducesValidHeader()
    {
        var png = PngEncoder.Encode(new int[4], 2, 2);
        Assert.Equal(0x89, png[0]); Assert.Equal((byte)'P', png[1]);
        Assert.Contains("IHDR", System.Text.Encoding.ASCII.GetString(png, 8, 8));
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));
    }

    [Fact]
    public void FrameBufferConvertsPixelFormats()
    {
        var fb = new FrameBuffer();
        unsafe
        {
            ushort* rgb565 = stackalloc ushort[2]; rgb565[0] = 0xF800; rgb565[1] = 0x07E0; // red, green
            fb.Update((IntPtr)rgb565, 2, 1, 4, OpenEmu.Core.Libretro.Retro.PixelFormatRgb565);
            Assert.Equal(unchecked((int)0xFFFF0000), fb.Pixels[0]);
            Assert.Equal(unchecked((int)0xFF00FF00), fb.Pixels[1]);
            ushort* rgb555 = stackalloc ushort[1]; rgb555[0] = 0x001F; // blue
            fb.Update((IntPtr)rgb555, 1, 1, 2, OpenEmu.Core.Libretro.Retro.PixelFormat0Rgb1555);
            Assert.Equal(unchecked((int)0xFF0000FF), fb.Pixels[0]);
            uint* xrgb = stackalloc uint[1]; xrgb[0] = 0x00123456;
            fb.Update((IntPtr)xrgb, 1, 1, 4, OpenEmu.Core.Libretro.Retro.PixelFormatXrgb8888);
            Assert.Equal(unchecked((int)0xFF123456), fb.Pixels[0]);
        }
        Assert.Equal(3, fb.Version);
    }

    [Fact]
    public void CleanTitleAndCheatNormalization()
    {
        Assert.Equal("Sonic The Hedgehog 2", GameImporter.CleanTitle("Sonic The Hedgehog 2 (World) (Rev A) [!]"));
        Assert.Equal("AAAA-BBBB+CCCC-DDDD", Cheat.NormalizeCode("AAAA-BBBB, CCCC-DDDD"));
        Assert.True(Cheat.LooksValid("SXIOPO"));
        Assert.False(Cheat.LooksValid("  "));
    }
}

public class HomebrewTests
{
    [Fact]
    public void ParsesOpenEmuFeed()
    {
        const string xml = """
            <games><game name="Demo" system="openemu.system.nes" developer="Dev" file="https://x/y/demo.nes" md5="abc" released="1421283600">
            <description> Hello </description><images><image type="cover" src="https://x/c.png"/><image type="ingame" src="https://x/1.png"/></images></game></games>
            """;
        var g = OpenEmu.Core.Homebrew.HomebrewCatalog.Parse(xml);
        Assert.Single(g);
        Assert.Equal("Demo", g[0].Name);
        Assert.Equal("https://x/c.png", g[0].CoverUrl);
        Assert.Single(g[0].Screenshots);
        Assert.Equal("Hello", g[0].Description);
        Assert.Equal(2015, g[0].Released!.Value.Year);
    }
}
