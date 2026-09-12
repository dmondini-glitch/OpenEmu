using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace OpenEmu.Core.Remote;

/// <summary>
/// File-backed memory-mapped regions shared between the UI and the core host (works on Windows, macOS and Linux).
/// Frame region: seqlock header + BGRA pixels. Input region: keyboard bitset + pointer written by the UI.
/// </summary>
public sealed unsafe class SharedRegion : IDisposable
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private byte* _ptr;
    public string Path { get; }
    public long Size { get; }
    public byte* Pointer => _ptr;

    public SharedRegion(string path, long size, bool create)
    {
        Path = path; Size = size;
        if (create)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            using (var f = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite)) f.SetLength(size);
        }
        _mmf = MemoryMappedFile.CreateFromFile(new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite), null, size, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false);
        _view = _mmf.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _ptr);
    }

    public void Dispose()
    {
        if (_ptr != null) { _view.SafeMemoryMappedViewHandle.ReleasePointer(); _ptr = null; }
        _view.Dispose(); _mmf.Dispose();
    }
}

/// <summary>Layout of the shared frame region.</summary>
public static unsafe class SharedFrame
{
    public const int MaxWidth = 2048, MaxHeight = 2048, HeaderSize = 64;
    public const long RegionSize = HeaderSize + (long)MaxWidth * MaxHeight * 4;
    // header offsets
    private const int OffMagic = 0, OffSeq = 4, OffWidth = 8, OffHeight = 12, OffAspect = 16, OffFrames = 24, OffFps = 32;
    public const int Magic = 0x4F454D46; // "OEMF"

    public static void Init(byte* p) { *(int*)(p + OffMagic) = Magic; *(int*)(p + OffSeq) = 0; }

    /// <summary>Writer side (host): publish a BGRA frame with a seqlock (odd = writing).</summary>
    public static void Write(byte* p, int[] pixels, int width, int height, float aspect, long frameCount, double fps)
    {
        width = Math.Min(width, MaxWidth); height = Math.Min(height, MaxHeight);
        var seq = Volatile.Read(ref *(int*)(p + OffSeq));
        Volatile.Write(ref *(int*)(p + OffSeq), seq + 1);
        Interlocked.MemoryBarrier();
        *(int*)(p + OffWidth) = width; *(int*)(p + OffHeight) = height; *(float*)(p + OffAspect) = aspect; *(long*)(p + OffFrames) = frameCount; *(double*)(p + OffFps) = fps;
        fixed (int* src = pixels)
        {
            if (pixels.Length >= width * height) Buffer.MemoryCopy(src, p + HeaderSize, RegionSize - HeaderSize, (long)width * height * 4);
        }
        Interlocked.MemoryBarrier();
        Volatile.Write(ref *(int*)(p + OffSeq), seq + 2);
    }

    /// <summary>Reader side (UI): returns false when no new stable frame is available. Copies into <paramref name="dest"/> (grown as needed).</summary>
    public static bool TryRead(byte* p, ref int lastSeq, ref int[] dest, out int width, out int height, out float aspect, out double fps)
    {
        width = height = 0; aspect = 0; fps = 0;
        if (*(int*)(p + OffMagic) != Magic) return false;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var seq = Volatile.Read(ref *(int*)(p + OffSeq));
            if (seq == lastSeq || (seq & 1) == 1) { if (seq == lastSeq) return false; Thread.SpinWait(200); continue; }
            Interlocked.MemoryBarrier();
            width = *(int*)(p + OffWidth); height = *(int*)(p + OffHeight); aspect = *(float*)(p + OffAspect); fps = *(double*)(p + OffFps);
            if (width <= 0 || height <= 0 || width > MaxWidth || height > MaxHeight) return false;
            if (dest.Length < width * height) dest = new int[width * height];
            fixed (int* d = dest) Buffer.MemoryCopy(p + HeaderSize, d, (long)dest.Length * 4, (long)width * height * 4);
            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref *(int*)(p + OffSeq)) == seq) { lastSeq = seq; return true; }
        }
        return false;
    }
}

/// <summary>Layout of the shared input region (UI → host): 256-key HID bitset + pointer state.</summary>
public static unsafe class SharedInput
{
    public const long RegionSize = 256;
    private const int OffKeys = 0, OffPointerX = 32, OffPointerY = 36, OffPointerPressed = 40, OffSeq = 44;

    public static void SetKey(byte* p, int hid, bool down)
    {
        if (hid < 0 || hid > 255) return;
        var b = p + OffKeys + hid / 8; var mask = (byte)(1 << (hid % 8));
        if (down) *b |= mask; else *b &= (byte)~mask;
        Interlocked.Increment(ref *(int*)(p + OffSeq));
    }
    public static void ClearKeys(byte* p) { for (var i = 0; i < 32; i++) p[OffKeys + i] = 0; Interlocked.Increment(ref *(int*)(p + OffSeq)); }
    public static bool IsDown(byte* p, int hid) => hid >= 0 && hid <= 255 && (p[OffKeys + hid / 8] & (1 << (hid % 8))) != 0;
    public static void SetPointer(byte* p, int x, int y, bool pressed) { *(int*)(p + OffPointerX) = x; *(int*)(p + OffPointerY) = y; *(int*)(p + OffPointerPressed) = pressed ? 1 : 0; }
    public static (int X, int Y, bool Pressed) GetPointer(byte* p) => (*(int*)(p + OffPointerX), *(int*)(p + OffPointerY), *(int*)(p + OffPointerPressed) != 0);
}
