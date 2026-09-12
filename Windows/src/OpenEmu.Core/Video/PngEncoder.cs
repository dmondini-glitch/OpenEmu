using System.Buffers.Binary;
using System.IO.Compression;

namespace OpenEmu.Core.Video;

/// <summary>Minimal dependency-free PNG encoder (RGB8) for screenshots and save-state thumbnails.</summary>
public static class PngEncoder
{
    public static byte[] Encode(ReadOnlySpan<int> bgra, int width, int height)
    {
        var raw = new byte[(width * 3 + 1) * height];
        var o = 0;
        for (var y = 0; y < height; y++)
        {
            raw[o++] = 0; // filter: none
            for (var x = 0; x < width; x++)
            {
                var p = bgra[y * width + x];
                raw[o++] = (byte)(p >> 16); raw[o++] = (byte)(p >> 8); raw[o++] = (byte)p;
            }
        }
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; ihdr[9] = 2; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        Chunk(ms, "IHDR", ihdr);
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Fastest, true)) zs.Write(raw);
            Chunk(ms, "IDAT", z.ToArray());
        }
        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t); s.Write(data);
        var crc = Crc32.Compute(t); crc = Crc32.Compute(data, crc);
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        s.Write(c);
    }
}

public static class Crc32
{
    private static readonly uint[] Table = Build();
    private static uint[] Build()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++) { var c = n; for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; t[n] = c; }
        return t;
    }
    public static uint Compute(ReadOnlySpan<byte> data, uint seed = 0)
    {
        var c = seed ^ 0xFFFFFFFF;
        foreach (var b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
    public static uint Compute(Stream s)
    {
        var buf = new byte[1 << 16]; uint c = 0; int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0) c = Compute(buf.AsSpan(0, n), c);
        return c;
    }
}
