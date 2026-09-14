using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OpenEmu.Core.Config;
using OpenEmu.Core.Libretro;

namespace OpenEmu.Core.Video;

/// <summary>
/// Real desktop OpenGL (WGL) context on Windows for hardware-rendered libretro cores. Avalonia renders through
/// ANGLE (OpenGL ES over Direct3D), which cannot host desktop-GL cores such as mupen64plus-next, Beetle PSX HW,
/// Flycast or PPSSPP — so the game view owns a native child HWND with its own WGL context instead.
/// The context is made current on the emulation thread; the core runs and presents from there.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class Win32GlContext : IHwRenderThreadHost, IDisposable
{
    // ---- Win32
    private const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_DISABLED = 0x08000000, WS_CLIPCHILDREN = 0x02000000, WS_CLIPSIBLINGS = 0x04000000, WS_OVERLAPPED = 0, WS_POPUP = 0x80000000;
    private const uint CS_OWNDC = 0x0020;
    private const int PFD_TYPE_RGBA = 0, PFD_MAIN_PLANE = 0;
    private const uint PFD_DOUBLEBUFFER = 1, PFD_DRAW_TO_WINDOW = 4, PFD_SUPPORT_OPENGL = 0x20;
    private const int WGL_CONTEXT_MAJOR_VERSION_ARB = 0x2091, WGL_CONTEXT_MINOR_VERSION_ARB = 0x2092, WGL_CONTEXT_FLAGS_ARB = 0x2094, WGL_CONTEXT_PROFILE_MASK_ARB = 0x9126,
        WGL_CONTEXT_CORE_PROFILE_BIT_ARB = 1, WGL_CONTEXT_COMPATIBILITY_PROFILE_BIT_ARB = 2, WGL_CONTEXT_DEBUG_BIT_ARB = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct PIXELFORMATDESCRIPTOR
    {
        public ushort nSize, nVersion; public uint dwFlags; public byte iPixelType, cColorBits, cRedBits, cRedShift, cGreenBits, cGreenShift, cBlueBits, cBlueShift, cAlphaBits, cAlphaShift,
            cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits, cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved;
        public uint dwLayerMask, dwVisibleMask, dwDamageMask;
    }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public IntPtr lpszMenuName; public IntPtr lpszClassName; public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadLibraryW(string name);
    [DllImport("gdi32.dll")] private static extern int ChoosePixelFormat(IntPtr hdc, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32.dll")] private static extern bool SetPixelFormat(IntPtr hdc, int format, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32.dll")] private static extern bool SwapBuffers(IntPtr hdc);
    [DllImport("opengl32.dll")] private static extern IntPtr wglCreateContext(IntPtr hdc);
    [DllImport("opengl32.dll")] private static extern bool wglDeleteContext(IntPtr hglrc);
    [DllImport("opengl32.dll")] private static extern bool wglMakeCurrent(IntPtr hdc, IntPtr hglrc);
    [DllImport("opengl32.dll")] private static extern IntPtr wglGetCurrentContext();
    [DllImport("opengl32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr wglGetProcAddress(string name);

    private static readonly IntPtr s_wndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc;
    private static bool s_classRegistered;
    private static IntPtr s_opengl32;

    [UnmanagedCallersOnly]
    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam) => DefWindowProcW(hwnd, msg, wParam, lParam);

    private IntPtr _hdc, _hglrc;
    private GlBlitter? _gl;
    private LibretroCore? _core;
    private bool _prepared;
    public IntPtr Hwnd { get; private set; }
    public bool OwnsTopLevelWindow { get; }
    public VideoSettings Video { get; set; } = new();
    public string? Description { get; private set; }
    public event Action<string>? Log;

    /// <summary>Creates the GL child window. Pass IntPtr.Zero for a hidden top-level window (headless tests).</summary>
    public Win32GlContext(IntPtr parentHwnd, int width = 640, int height = 480)
    {
        var hInstance = GetModuleHandleW(null);
        if (!s_classRegistered)
        {
            var cls = new WNDCLASSEXW { cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(), style = CS_OWNDC, lpfnWndProc = s_wndProc, hInstance = hInstance, lpszClassName = Marshal.StringToHGlobalUni("OpenEmuGLWindow") };
            if (RegisterClassExW(ref cls) == 0 && Marshal.GetLastWin32Error() != 1410) throw new InvalidOperationException("RegisterClassEx failed: " + Marshal.GetLastWin32Error());
            s_classRegistered = true;
        }
        OwnsTopLevelWindow = parentHwnd == IntPtr.Zero;
        // WS_DISABLED: the child never takes keyboard focus or mouse input, Avalonia keeps handling them.
        var style = OwnsTopLevelWindow ? WS_POPUP | WS_CLIPCHILDREN | WS_CLIPSIBLINGS : WS_CHILD | WS_VISIBLE | WS_DISABLED | WS_CLIPCHILDREN | WS_CLIPSIBLINGS;
        Hwnd = CreateWindowExW(0, "OpenEmuGLWindow", "OpenEmu GL", style, 0, 0, Math.Max(1, width), Math.Max(1, height), parentHwnd, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (Hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
        _hdc = GetDC(Hwnd);
        var pfd = new PIXELFORMATDESCRIPTOR
        {
            nSize = (ushort)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(), nVersion = 1, dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER,
            iPixelType = PFD_TYPE_RGBA, cColorBits = 32, cDepthBits = 24, cStencilBits = 8, iLayerType = PFD_MAIN_PLANE,
        };
        var fmt = ChoosePixelFormat(_hdc, ref pfd);
        if (fmt == 0 || !SetPixelFormat(_hdc, fmt, ref pfd)) throw new InvalidOperationException("No OpenGL pixel format available (opengl32 driver missing?)");
        if (s_opengl32 == IntPtr.Zero) s_opengl32 = LoadLibraryW("opengl32.dll");
    }

    public (int W, int H) ClientSize { get { GetClientRect(Hwnd, out var r); return (Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top)); } }
    public void Resize(int w, int h) => SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, Math.Max(1, w), Math.Max(1, h), 0x0004 | 0x0002 | 0x0010); // SWP_NOZORDER | SWP_NOMOVE | SWP_NOACTIVATE

    // ---- IHwRenderHost
    public nuint CurrentFramebuffer => (nuint)(_gl?.Fbo ?? 0);
    public IntPtr GetProcAddress(string symbol)
    {
        var p = wglGetProcAddress(symbol);
        var v = (long)p;
        if (v == 0 || v == 1 || v == 2 || v == 3 || v == -1) p = s_opengl32 != IntPtr.Zero ? GetProcAddress(s_opengl32, symbol) : IntPtr.Zero;
        return p;
    }

    // ---- IHwRenderThreadHost (all on the emulation thread)
    public void Prepare(LibretroCore core)
    {
        _core = core;
        var hw = core.HwRender;
        var wantCore = hw.context_type == Retro.HwContextOpenGlCore;
        int major = (int)hw.version_major, minor = (int)hw.version_minor;
        if (major == 0) { if (wantCore) { major = 3; minor = 3; } else { major = 2; minor = 1; } }
        // Legacy context first (needed to query wglCreateContextAttribsARB), then the requested version/profile.
        var legacy = wglCreateContext(_hdc);
        if (legacy == IntPtr.Zero) throw new InvalidOperationException("wglCreateContext failed: " + Marshal.GetLastWin32Error());
        if (!wglMakeCurrent(_hdc, legacy)) throw new InvalidOperationException("wglMakeCurrent failed");
        var createAttribs = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int*, IntPtr>)wglGetProcAddress("wglCreateContextAttribsARB");
        _hglrc = IntPtr.Zero;
        if (createAttribs != null)
        {
            var attribs = stackalloc int[9];
            attribs[0] = WGL_CONTEXT_MAJOR_VERSION_ARB; attribs[1] = major; attribs[2] = WGL_CONTEXT_MINOR_VERSION_ARB; attribs[3] = minor;
            attribs[4] = WGL_CONTEXT_PROFILE_MASK_ARB; attribs[5] = wantCore ? WGL_CONTEXT_CORE_PROFILE_BIT_ARB : WGL_CONTEXT_COMPATIBILITY_PROFILE_BIT_ARB;
            attribs[6] = WGL_CONTEXT_FLAGS_ARB; attribs[7] = hw.debug_context ? WGL_CONTEXT_DEBUG_BIT_ARB : 0; attribs[8] = 0;
            _hglrc = createAttribs(_hdc, IntPtr.Zero, attribs);
            if (_hglrc == IntPtr.Zero && wantCore)
            {
                // some drivers refuse exact versions: ask for "at least 3.3 core"
                attribs[1] = 3; attribs[3] = 3; _hglrc = createAttribs(_hdc, IntPtr.Zero, attribs);
            }
        }
        if (_hglrc != IntPtr.Zero) { wglMakeCurrent(IntPtr.Zero, IntPtr.Zero); wglDeleteContext(legacy); wglMakeCurrent(_hdc, _hglrc); }
        else _hglrc = legacy;
        var swapInterval = (delegate* unmanaged[Stdcall]<int, int>)wglGetProcAddress("wglSwapIntervalEXT");
        if (swapInterval != null) swapInterval(Video.VSync ? 1 : 0);
        _gl = new GlBlitter(GetProcAddress);
        Description = $"{_gl.Renderer} / GL {_gl.Version} (requested {major}.{minor} {(wantCore ? "core" : "compat")})";
        Log?.Invoke("OpenGL: " + Description);
        if (!_gl.IsUsable) throw new InvalidOperationException("OpenGL driver lacks required functions: " + string.Join(", ", _gl.MissingFunctions));
        var g = core.AvInfo.geometry;
        if (!_gl.EnsureFbo((int)Math.Max(g.max_width, g.base_width), (int)Math.Max(g.max_height, g.base_height), hw.depth || true, hw.stencil))
            Log?.Invoke("OpenGL: framebuffer incomplete");
        _prepared = true;
    }

    public void Present(FrameBuffer frame, uint baseWidth, uint baseHeight, float aspect)
    {
        if (!_prepared || _gl == null) return;
        var (vw, vh) = ClientSize;
        int fw = (int)baseWidth, fh = (int)baseHeight;
        if (frame.IsHardwareFrame && frame.Width > 0) { fw = frame.Width; fh = frame.Height; }
        var g = _core!.AvInfo.geometry;
        if (fw > _gl.Width || fh > _gl.Height) _gl.EnsureFbo(Math.Max(fw, (int)g.max_width), Math.Max(fh, (int)g.max_height), true, _core.HwRender.stencil);
        var (x0, y0, x1, y1) = GlBlitter.DestRect(vw, vh, fw, fh, aspect, Video.KeepAspectRatio, Video.IntegralScaling);
        _gl.Present(0, vw, vh, fw, fh, x0, y0, x1, y1, _core.HwRender.bottom_left_origin, Video.Filter == VideoFilter.Linear);
        SwapBuffers(_hdc);
    }

    public void Capture(FrameBuffer frame, uint baseWidth, uint baseHeight)
    {
        if (!_prepared || _gl == null) return;
        int fw = (int)baseWidth, fh = (int)baseHeight;
        if (frame.IsHardwareFrame && frame.Width > 0) { fw = frame.Width; fh = frame.Height; }
        _gl.Finish();
        var px = _gl.ReadPixels(fw, fh, _core!.HwRender.bottom_left_origin);
        frame.SetPixels(px, Math.Min(fw, _gl.Width), Math.Min(fh, _gl.Height));
    }

    public void Teardown()
    {
        try { _gl?.DeleteFbo(); } catch { }
        if (_hglrc != IntPtr.Zero) { wglMakeCurrent(IntPtr.Zero, IntPtr.Zero); wglDeleteContext(_hglrc); _hglrc = IntPtr.Zero; }
        _prepared = false;
    }

    public void Dispose()
    {
        Teardown();
        if (_hdc != IntPtr.Zero) { ReleaseDC(Hwnd, _hdc); _hdc = IntPtr.Zero; }
        if (Hwnd != IntPtr.Zero) { DestroyWindow(Hwnd); Hwnd = IntPtr.Zero; }
    }
}
