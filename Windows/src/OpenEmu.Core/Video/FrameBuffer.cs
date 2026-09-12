using System.Runtime.InteropServices;
using OpenEmu.Core.Libretro;

namespace OpenEmu.Core.Video;

/// <summary>Latest video frame converted to BGRA32 (the pixel layout Avalonia's Bgra8888 bitmaps expect).</summary>
public sealed class FrameBuffer
{
    private readonly object _lock = new();
    private int[] _pixels = Array.Empty<int>();
    public int Width { get; private set; }
    public int Height { get; private set; }
    public long Version { get; private set; }
    public float AspectRatio { get; set; }
    public bool IsHardwareFrame { get; private set; }

    public object SyncRoot => _lock;
    /// <summary>Pixels in BGRA (0xAARRGGBB as int) row-major, Width*Height. Read under SyncRoot.</summary>
    public int[] Pixels => _pixels;

    public unsafe void Update(IntPtr data, uint width, uint height, nuint pitch, int pixelFormat)
    {
        if (data == Retro.HwFrameBufferValid) { lock (_lock) { Width = (int)width; Height = (int)height; IsHardwareFrame = true; Version++; } return; }
        lock (_lock)
        {
            IsHardwareFrame = false;
            var w = (int)width; var h = (int)height;
            if (_pixels.Length < w * h) _pixels = new int[w * h];
            Width = w; Height = h;
            var src = (byte*)data;
            fixed (int* dst = _pixels)
            {
                switch (pixelFormat)
                {
                    case Retro.PixelFormatXrgb8888:
                        for (var y = 0; y < h; y++)
                        {
                            var row = (uint*)(src + y * (long)pitch); var drow = dst + y * w;
                            for (var x = 0; x < w; x++) drow[x] = (int)(row[x] | 0xFF000000u);
                        }
                        break;
                    case Retro.PixelFormatRgb565:
                        for (var y = 0; y < h; y++)
                        {
                            var row = (ushort*)(src + y * (long)pitch); var drow = dst + y * w;
                            for (var x = 0; x < w; x++)
                            {
                                var p = row[x];
                                var r = (p >> 11) & 0x1F; var g = (p >> 5) & 0x3F; var b = p & 0x1F;
                                r = (r << 3) | (r >> 2); g = (g << 2) | (g >> 4); b = (b << 3) | (b >> 2);
                                drow[x] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
                            }
                        }
                        break;
                    default: // 0RGB1555
                        for (var y = 0; y < h; y++)
                        {
                            var row = (ushort*)(src + y * (long)pitch); var drow = dst + y * w;
                            for (var x = 0; x < w; x++)
                            {
                                var p = row[x];
                                var r = (p >> 10) & 0x1F; var g = (p >> 5) & 0x1F; var b = p & 0x1F;
                                r = (r << 3) | (r >> 2); g = (g << 3) | (g >> 2); b = (b << 3) | (b >> 2);
                                drow[x] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
                            }
                        }
                        break;
                }
            }
            Version++;
        }
    }

    /// <summary>Replace contents with a BGRA frame produced elsewhere (e.g. read back from the GL FBO).</summary>
    public void SetPixels(int[] bgra, int width, int height)
    {
        lock (_lock)
        {
            if (_pixels.Length < width * height) _pixels = new int[width * height];
            Array.Copy(bgra, _pixels, width * height);
            Width = width; Height = height; IsHardwareFrame = false; Version++;
        }
    }

    public int[] Snapshot(out int width, out int height)
    {
        lock (_lock)
        {
            width = Width; height = Height;
            var copy = new int[Width * Height];
            Array.Copy(_pixels, copy, copy.Length);
            return copy;
        }
    }

    public bool IsBlank()
    {
        lock (_lock)
        {
            var n = Width * Height; if (n == 0) return true;
            var first = _pixels[0];
            for (var i = 1; i < n; i++) if (_pixels[i] != first) return false;
            return true;
        }
    }

    public byte[] ToPng() { var px = Snapshot(out var w, out var h); return PngEncoder.Encode(px, w, h); }
}
