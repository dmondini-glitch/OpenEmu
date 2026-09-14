using System.IO.Compression;
using System.Reflection;
using OpenEmu.Core.Config;

namespace OpenEmu.Core.Bios;

/// <summary>
/// Firmware and system files that are legal to redistribute, used so consoles boot without the proprietary BIOS:
///  • OpenBIOS (PCSX-Redux, MIT) installed as scph5500/5501/5502.bin for PlayStation cores (Beetle PSX, SwanStation, PCSX-ReARMed);
///  • C-BIOS machine definitions for MSX (blueMSX system pack, BSD);
///  • PPSSPP assets and Dolphin "Sys" files, required by those cores and freely distributed by libretro.
/// Proprietary BIOS images (Sony, Sega, Nintendo, NEC, 3DO, Coleco, Magnavox, Mattel, Atari…) are NOT downloaded:
/// they are copyrighted and must be dumped from hardware the user owns.
/// </summary>
public static class FreeSystemFiles
{
    public sealed record Pack(string Id, string Title, string Url, string TargetSubdir, string[] Systems, string License);

    public static readonly IReadOnlyList<Pack> Packs = new[]
    {
        new Pack("bluemsx", "MSX machines with C-BIOS (blueMSX)", "https://buildbot.libretro.com/assets/system/blueMSX.zip", "", new[] { "openemu.system.msx", "openemu.system.colecovision" }, "BSD / C-BIOS"),
        new Pack("ppsspp", "PPSSPP assets (fonts, shaders)", "https://buildbot.libretro.com/assets/system/PPSSPP.zip", "", new[] { "openemu.system.psp" }, "GPLv2"),
        new Pack("dolphin", "Dolphin Sys files", "https://buildbot.libretro.com/assets/system/Dolphin.zip", "", new[] { "openemu.system.gc" }, "GPLv2"),
    };

    public static readonly string[] OpenBiosTargets = { "scph5500.bin", "scph5501.bin", "scph5502.bin" };

    /// <summary>Cores that boot without the official BIOS (HLE / built-in replacement), so a missing BIOS must not block launching.</summary>
    public static readonly HashSet<string> CoresWithHleBios = new(StringComparer.Ordinal)
    {
        "pcsx_rearmed", "play", "flycast", "yabause", "kronos", "melondsds", "melonds", "mgba", "pokemini", "vecx", "virtualjaguar", "a5200", "prosystem", "stella", "stella2014",
        "gearsystem", "genesis_plus_gx", "picodrive", "bluemsx", "fbneo", "mame2003_plus",
    };

    public static bool CanRunWithoutBios(string coreId) => CoresWithHleBios.Contains(coreId);

    /// <summary>Copies the bundled OpenBIOS as the PlayStation BIOS file names when they are missing. Returns the files written.</summary>
    public static IReadOnlyList<string> InstallOpenBios(string? biosDir = null, bool overwrite = false)
    {
        biosDir ??= Paths.BiosDir;
        Directory.CreateDirectory(biosDir);
        using var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("bios/openbios.bin") ?? throw new InvalidOperationException("openbios.bin missing");
        var bytes = new byte[src.Length]; src.ReadExactly(bytes);
        var written = new List<string>();
        foreach (var name in OpenBiosTargets)
        {
            var p = Path.Combine(biosDir, name);
            if (File.Exists(p) && !overwrite) continue;
            File.WriteAllBytes(p, bytes); written.Add(p);
        }
        var lic = Path.Combine(biosDir, "openbios-LICENSE.txt");
        if (!File.Exists(lic))
        {
            using var ls = Assembly.GetExecutingAssembly().GetManifestResourceStream("bios/openbios-LICENSE.txt");
            if (ls != null) { using var f = File.Create(lic); ls.CopyTo(f); }
        }
        return written;
    }

    public static bool IsOpenBios(string path)
    {
        try
        {
            using var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("bios/openbios.bin")!;
            var a = new byte[src.Length]; src.ReadExactly(a);
            var b = File.ReadAllBytes(path);
            return a.AsSpan().SequenceEqual(b);
        }
        catch { return false; }
    }

    public static bool IsPackInstalled(Pack pack, string? biosDir = null)
    {
        biosDir ??= Paths.BiosDir;
        return pack.Id switch
        {
            "bluemsx" => Directory.Exists(Path.Combine(biosDir, "Machines")),
            "ppsspp" => Directory.Exists(Path.Combine(biosDir, "PPSSPP")),
            "dolphin" => Directory.Exists(Path.Combine(biosDir, "dolphin-emu")),
            _ => false,
        };
    }

    public static async Task InstallPackAsync(Pack pack, HttpClient http, string? biosDir = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        biosDir ??= Paths.BiosDir;
        Directory.CreateDirectory(biosDir);
        var zip = Path.Combine(biosDir, pack.Id + ".zip.part");
        using (var resp = await http.GetAsync(pack.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zip);
            var buf = new byte[1 << 16]; long done = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0) { await dst.WriteAsync(buf.AsMemory(0, n), ct); done += n; if (total > 0) progress?.Report((double)done / total); }
        }
        var target = string.IsNullOrEmpty(pack.TargetSubdir) ? biosDir : Path.Combine(biosDir, pack.TargetSubdir);
        Directory.CreateDirectory(target);
        ZipFile.ExtractToDirectory(zip, target, true);
        File.Delete(zip);
        progress?.Report(1);
    }

    public static async Task<Dictionary<string, Exception?>> InstallAllAsync(HttpClient http, string? biosDir = null, IProgress<(string Pack, double Progress)>? progress = null, CancellationToken ct = default)
    {
        var results = new Dictionary<string, Exception?>();
        try { InstallOpenBios(biosDir); results["openbios"] = null; } catch (Exception ex) { results["openbios"] = ex; }
        foreach (var pack in Packs)
        {
            try { await InstallPackAsync(pack, http, biosDir, new Progress<double>(p => progress?.Report((pack.Id, p))), ct); results[pack.Id] = null; }
            catch (Exception ex) { results[pack.Id] = ex; }
        }
        return results;
    }
}
