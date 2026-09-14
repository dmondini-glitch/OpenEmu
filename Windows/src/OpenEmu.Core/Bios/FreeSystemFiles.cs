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
        "pcsx_rearmed", "play", "flycast", "yabause", "kronos", "melondsds", "melonds", "mgba", "pokemini", "vecx", "virtualjaguar", "a5200", "atari800", "prosystem", "stella", "stella2014",
        "gearsystem", "genesis_plus_gx", "picodrive", "bluemsx", "fbneo", "mame2003_plus", "handy", "sameboy", "gambatte", "tgbdual",
    };

    public static bool CanRunWithoutBios(string coreId) => CoresWithHleBios.Contains(coreId);

    /// <summary>
    /// Core options that switch a core to its open-source / HLE firmware when the proprietary file is absent
    /// (applied under the user's own options at launch).
    /// </summary>
    public static Dictionary<string, string> DefaultCoreOptions(string coreId, string? biosDir = null)
    {
        biosDir ??= Paths.BiosDir;
        var o = new Dictionary<string, string>();
        bool Missing(string rel) => !File.Exists(Path.Combine(biosDir, rel.Replace('/', Path.DirectorySeparatorChar)));
        switch (coreId)
        {
            case "kronos": if (Missing("kronos/saturn_bios.bin")) o["kronos_force_hle_bios"] = "enabled"; break;
            case "yabause": if (Missing("saturn_bios.bin")) o["yabause_force_hle_bios"] = "enabled"; break;
            case "melondsds": if (Missing("bios7.bin") || Missing("firmware.bin")) o["melonds_sysfile_mode"] = "builtin"; break;
            case "flycast": if (Missing("dc/dc_boot.bin")) o["flycast_hle_bios"] = "enabled"; break;
            case "pcsx_rearmed": o["pcsx_rearmed_show_bios_bootlogo"] = "disabled"; break;
            case "atari800": if (Missing("ATARIXL.ROM")) { o["atari800_os_xl"] = "AltirraOS"; o["atari800_os_400_800"] = "AltirraOS"; } break;
        }
        return o;
    }

    /// <summary>Per-system summary of what boots without proprietary firmware (shown in Preferences → BIOS and the docs).</summary>
    public static readonly IReadOnlyList<(string SystemId, string Replacement, bool Available)> Coverage = new[]
    {
        ("openemu.system.psx", "OpenBIOS (PCSX-Redux, MIT) — bundled", true),
        ("openemu.system.ps2", "Play! HLE BIOS", true),
        ("openemu.system.saturn", "Kronos / Yabause HLE BIOS", true),
        ("openemu.system.dc", "Flycast HLE BIOS", true),
        ("openemu.system.nds", "melonDS FreeBIOS (DS mode)", true),
        ("openemu.system.gba", "mGBA HLE BIOS", true),
        ("openemu.system.gb", "SameBoy open-source boot ROMs", true),
        ("openemu.system.msx", "C-BIOS (blueMSX pack)", true),
        ("openemu.system.psp", "PPSSPP assets pack (no BIOS needed)", true),
        ("openemu.system.gc", "Dolphin Sys pack (IPL optional)", true),
        ("openemu.system.atari8bit", "AltirraOS (built into atari800)", true),
        ("openemu.system.5200", "built-in 5200 BIOS (a5200)", true),
        ("openemu.system.7800", "BIOS optional (ProSystem)", true),
        ("openemu.system.lynx", "boot ROM optional (Handy)", true),
        ("openemu.system.pokemonmini", "PokeMini FreeBIOS", true),
        ("openemu.system.vectrex", "GCE BIOS built into vecx (freely licensed)", true),
        ("openemu.system.jaguar", "built into VirtualJaguar", true),
        ("openemu.system.scd", "none — Sega CD BIOS required", false),
        ("openemu.system.pcecd", "none — syscard3.pce required", false),
        ("openemu.system.pcfx", "none — pcfx.rom required", false),
        ("openemu.system.3do", "none — Panasonic/GoldStar BIOS required", false),
        ("openemu.system.fds", "none — disksys.rom required", false),
        ("openemu.system.colecovision", "none — colecovision.rom required", false),
        ("openemu.system.odyssey2", "none — o2rom.bin required", false),
        ("openemu.system.intellivision", "none — exec.bin / grom.bin required", false),
        ("openemu.system.arcade", "Neo Geo sets need neogeo.zip (no open-source BIOS)", false),
    };

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
