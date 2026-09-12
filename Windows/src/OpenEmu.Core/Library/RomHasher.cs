using System.Security.Cryptography;
using OpenEmu.Core.Video;

namespace OpenEmu.Core.Library;

public sealed record RomHashes(string Md5, string Sha1, string Crc32, long Size, int HeaderSkipped);

/// <summary>
/// Hashes ROMs the way OpenEmu/OpenVGDB expect: copier/format headers are skipped for NES (iNES 16 B),
/// SNES (512 B copier header), Atari Lynx (64 B) and Atari 7800 (128 B A78 header).
/// </summary>
public static class RomHasher
{
    public static int HeaderSize(string systemId, string path, long size)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        switch (systemId)
        {
            case "openemu.system.nes":
            case "openemu.system.fds":
                if (ext == "nes" && StartsWith(path, "NES\x1A")) return 16;
                if (ext == "fds" && StartsWith(path, "FDS\x1A")) return 16;
                return 0;
            case "openemu.system.snes":
                return size % 1024 == 512 ? 512 : 0;
            case "openemu.system.lynx":
                return StartsWith(path, "LYNX") ? 64 : 0;
            case "openemu.system.7800":
                return ext == "a78" && size % 1024 == 128 ? 128 : 0;
            default: return 0;
        }
    }

    private static bool StartsWith(string path, string magic)
    {
        try
        {
            using var f = File.OpenRead(path);
            var buf = new byte[magic.Length];
            return f.Read(buf, 0, buf.Length) == buf.Length && buf.SequenceEqual(magic.Select(c => (byte)c));
        }
        catch { return false; }
    }

    public static RomHashes Compute(string path, string? systemId = null)
    {
        using var f = File.OpenRead(path);
        var skip = systemId == null ? 0 : HeaderSize(systemId, path, f.Length);
        f.Position = skip;
        return Compute(f, skip);
    }

    public static RomHashes Compute(Stream s, int headerSkipped = 0)
    {
        using var md5 = MD5.Create();
        using var sha1 = SHA1.Create();
        var buf = new byte[1 << 16]; int n; uint crc = 0; long size = 0;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            md5.TransformBlock(buf, 0, n, null, 0);
            sha1.TransformBlock(buf, 0, n, null, 0);
            crc = Crc32.Compute(buf.AsSpan(0, n), crc);
            size += n;
        }
        md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        sha1.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return new RomHashes(Convert.ToHexString(md5.Hash!), Convert.ToHexString(sha1.Hash!), crc.ToString("X8"), size, headerSkipped);
    }
}
