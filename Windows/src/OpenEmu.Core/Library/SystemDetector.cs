using System.Text;
using OpenEmu.Core.Systems;

namespace OpenEmu.Core.Library;

/// <summary>Guesses the system for a ROM/disc image by extension, then by file signature for ambiguous extensions.</summary>
public static class SystemDetector
{
    public static IReadOnlyList<SystemDefinition> Candidates(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        var byExt = SystemCatalog.ByExtension(ext);
        if (byExt.Count == 1) return byExt;
        var sniffed = Sniff(path, ext, byExt);
        return sniffed != null ? new[] { sniffed } : byExt;
    }

    public static SystemDefinition? Detect(string path)
    {
        var c = Candidates(path);
        return c.Count == 1 ? c[0] : null;
    }

    private static SystemDefinition? Sniff(string path, string ext, IReadOnlyList<SystemDefinition> candidates)
    {
        // A positive signature match beats the extension list (e.g. a Master System ROM saved as .bin).
        SystemDefinition? Pick(string id) => SystemCatalog.Find(id);
        try
        {
            if (ext is "cue" or "ccd" or "m3u")
            {
                var bin = FirstTrackFile(path);
                if (bin != null && File.Exists(bin)) return SniffDisc(bin, Pick);
                return null;
            }
            if (ext is "iso" or "chd") return SniffDisc(path, Pick);
            using var f = File.OpenRead(path);
            var head = new byte[0x8000];
            var n = f.Read(head, 0, head.Length);
            if (n < 16) return null;
            var s = head.AsSpan(0, n);
            if (Ascii(s, 0, 4) == "NES\x1A") return Pick("openemu.system.nes");
            if (Ascii(s, 0, 4) == "FDS\x1A") return Pick("openemu.system.fds");
            if (Ascii(s, 0x100, 8) == "SEGA 32X") return Pick("openemu.system.32x");
            if (Ascii(s, 0x100, 4) == "SEGA")
            {
                var name = Ascii(s, 0x100, 16);
                if (name.Contains("32X")) return Pick("openemu.system.32x");
                return Pick("openemu.system.sg");
            }
            if (n > 0x7FF8 && Ascii(s, 0x7FF0, 8) == "TMR SEGA") return Pick("openemu.system.sms") ?? Pick("openemu.system.gg");
            if (n > 0x1FF8 && Ascii(s, 0x1FF0, 8) == "TMR SEGA") return Pick("openemu.system.sms");
            if (Ascii(s, 1, 3) == "ATR" || (s[0] == 0x96 && s[1] == 0x02)) return Pick("openemu.system.atari8bit");
            if (Ascii(s, 0, 4) == "LYNX") return Pick("openemu.system.lynx");
            if (s[0] == 0x80 && s[1] == 0x37 && s[2] == 0x12 && s[3] == 0x40) return Pick("openemu.system.n64");
            if (Ascii(s, 0, 2) == "AB" && ext == "rom" && candidates.Any(c => c.Id == "openemu.system.msx")) return Pick("openemu.system.msx");
            if (ext == "bin")
            {
                // Vectrex cartridges start with "g GCE"
                if (Ascii(s, 0, 5) == "g GCE") return Pick("openemu.system.vectrex");
                // Atari 2600 sizes are 2K/4K/8K/16K/32K; ColecoVision starts with AA55/55AA
                if ((s[0] == 0xAA && s[1] == 0x55) || (s[0] == 0x55 && s[1] == 0xAA)) return Pick("openemu.system.colecovision");
                if (f.Length is 2048 or 4096 or 8192 or 16384 or 32768 && Pick("openemu.system.2600") is { } a26) return a26;
            }
            if (ext == "rom" && f.Length <= 16384 && Pick("openemu.system.colecovision") is { } col && (s[0] == 0xAA || s[0] == 0x55)) return col;
            return null;
        }
        catch { return null; }
    }

    private static SystemDefinition? SniffDisc(string image, Func<string, SystemDefinition?> pick)
    {
        using var f = File.OpenRead(image);
        var buf = new byte[Math.Min(f.Length, 0x10000)];
        var n = f.Read(buf, 0, buf.Length);
        var text = Encoding.ASCII.GetString(buf, 0, n);
        if (text.Contains("SEGADISCSYSTEM") || text.Contains("SEGABOOTDISC")) return pick("openemu.system.scd");
        if (text.Contains("SEGA SEGASATURN")) return pick("openemu.system.saturn");
        if (text.Contains("SEGA SEGAKATANA")) return pick("openemu.system.dc");
        if (text.Contains("PC Engine CD-ROM SYSTEM")) return pick("openemu.system.pcecd");
        if (text.Contains("PC-FX:Hu_CD-ROM")) return pick("openemu.system.pcfx");
        if (text.Contains("PLAYSTATION") || text.Contains("Sony Computer Entertainment")) return text.Contains("BOOT2") ? pick("openemu.system.ps2") ?? pick("openemu.system.psx") : pick("openemu.system.psx") ?? pick("openemu.system.ps2");
        if (text.Contains("iamaduckiamaduck") || text.Contains("CD-ROM") && text.Contains("3DO")) return pick("openemu.system.3do");
        if (text.Contains("PSP GAME")) return pick("openemu.system.psp");
        return null;
    }

    /// <summary>Path of the first FILE referenced by a .cue/.ccd/.m3u (relative to the sheet).</summary>
    public static string? FirstTrackFile(string sheet)
    {
        var dir = Path.GetDirectoryName(sheet) ?? "";
        var ext = Path.GetExtension(sheet).ToLowerInvariant();
        foreach (var line in File.ReadLines(sheet))
        {
            var t = line.Trim();
            if (ext == ".m3u") { if (t.Length > 0 && !t.StartsWith('#')) { var p = Path.Combine(dir, t); return p.EndsWith(".cue", StringComparison.OrdinalIgnoreCase) ? FirstTrackFile(p) : p; } continue; }
            if (ext == ".ccd") { var img = Path.ChangeExtension(sheet, ".img"); return File.Exists(img) ? img : Path.ChangeExtension(sheet, ".bin"); }
            if (t.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase))
            {
                var rest = t[5..].Trim();
                var name = rest.StartsWith('"') ? rest[1..rest.IndexOf('"', 1)] : rest.Split(' ')[0];
                return Path.Combine(dir, name);
            }
        }
        return null;
    }

    private static string Ascii(ReadOnlySpan<byte> s, int offset, int len)
        => offset + len <= s.Length ? Encoding.ASCII.GetString(s.Slice(offset, len)) : "";
}
