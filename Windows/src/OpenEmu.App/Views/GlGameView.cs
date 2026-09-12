using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using OpenEmu.Core.Config;
using OpenEmu.Core.Emulation;
using OpenEmu.Core.Libretro;

namespace OpenEmu.App.Views;

/// <summary>
/// Hosts hardware-rendered (OpenGL) libretro cores: owns the FBO the core draws into, steps the core once per
/// vsync on the UI/GL thread and blits the result to Avalonia's framebuffer.
/// </summary>
public sealed unsafe class GlGameView : OpenGlControlBase, IHwRenderHost
{
    private const int GL_FRAMEBUFFER = 0x8D40, GL_READ_FRAMEBUFFER = 0x8CA8, GL_DRAW_FRAMEBUFFER = 0x8CA9, GL_COLOR_ATTACHMENT0 = 0x8CE0,
        GL_DEPTH_ATTACHMENT = 0x8D00, GL_DEPTH_STENCIL_ATTACHMENT = 0x821A, GL_TEXTURE_2D = 0x0DE1, GL_RGBA = 0x1908, GL_RGBA8 = 0x8058,
        GL_UNSIGNED_BYTE = 0x1401, GL_TEXTURE_MIN_FILTER = 0x2801, GL_TEXTURE_MAG_FILTER = 0x2800, GL_LINEAR = 0x2601, GL_NEAREST = 0x2600,
        GL_RENDERBUFFER = 0x8D41, GL_DEPTH24_STENCIL8 = 0x88F0, GL_DEPTH_COMPONENT24 = 0x81A6, GL_FRAMEBUFFER_COMPLETE = 0x8CD5,
        GL_COLOR_BUFFER_BIT = 0x4000, GL_DEPTH_BUFFER_BIT = 0x100, GL_STENCIL_BUFFER_BIT = 0x400, GL_BGRA = 0x80E1, GL_SCISSOR_TEST = 0x0C11;

    private GlInterface? _gl;
    private delegate* unmanaged<int, int, int, int, int, int, int, int, int, int, void> _blitFramebuffer;
    private delegate* unmanaged<int, int, void> _bindFramebuffer;
    private delegate* unmanaged<int, int*, void> _genFramebuffers, _genTextures, _genRenderbuffers, _deleteFramebuffers, _deleteTextures, _deleteRenderbuffers;
    private delegate* unmanaged<int, int, void> _bindTexture, _bindRenderbuffer;
    private delegate* unmanaged<int, int, int, int, int, int, int, int, void*, void> _texImage2D;
    private delegate* unmanaged<int, int, int, void> _texParameteri;
    private delegate* unmanaged<int, int, int, int, int, void> _framebufferTexture2D;
    private delegate* unmanaged<int, int, int, int, void> _renderbufferStorage, _framebufferRenderbuffer;
    private delegate* unmanaged<int, int> _checkFramebufferStatus;
    private delegate* unmanaged<int, int, int, int, void> _viewport;
    private delegate* unmanaged<int, void> _disable;
    private delegate* unmanaged<float, float, float, float, void> _clearColor;
    private delegate* unmanaged<int, void> _clear;
    private delegate* unmanaged<int, int, int, int, int, int, void*, void> _readPixels;

    private int _fbo, _tex, _depth, _fboW, _fboH;
    private bool _contextReady;
    public EmulationSession? Session { get; set; }
    public VideoSettings Video { get; set; } = new();
    public event Action<string>? Error;

    public nuint CurrentFramebuffer => (nuint)_fbo;
    public IntPtr GetProcAddress(string symbol) => _gl?.GetProcAddress(symbol) ?? IntPtr.Zero;

    private T* Fn<T>(string name) where T : unmanaged => (T*)_gl!.GetProcAddress(name);

    protected override void OnOpenGlInit(GlInterface gl)
    {
        _gl = gl;
        IntPtr P(string n) { var p = gl.GetProcAddress(n); if (p == IntPtr.Zero) Error?.Invoke($"GL: {n} missing"); return p; }
        _blitFramebuffer = (delegate* unmanaged<int, int, int, int, int, int, int, int, int, int, void>)P("glBlitFramebuffer");
        _bindFramebuffer = (delegate* unmanaged<int, int, void>)P("glBindFramebuffer");
        _genFramebuffers = (delegate* unmanaged<int, int*, void>)P("glGenFramebuffers");
        _genTextures = (delegate* unmanaged<int, int*, void>)P("glGenTextures");
        _genRenderbuffers = (delegate* unmanaged<int, int*, void>)P("glGenRenderbuffers");
        _deleteFramebuffers = (delegate* unmanaged<int, int*, void>)P("glDeleteFramebuffers");
        _deleteTextures = (delegate* unmanaged<int, int*, void>)P("glDeleteTextures");
        _deleteRenderbuffers = (delegate* unmanaged<int, int*, void>)P("glDeleteRenderbuffers");
        _bindTexture = (delegate* unmanaged<int, int, void>)P("glBindTexture");
        _bindRenderbuffer = (delegate* unmanaged<int, int, void>)P("glBindRenderbuffer");
        _texImage2D = (delegate* unmanaged<int, int, int, int, int, int, int, int, void*, void>)P("glTexImage2D");
        _texParameteri = (delegate* unmanaged<int, int, int, void>)P("glTexParameteri");
        _framebufferTexture2D = (delegate* unmanaged<int, int, int, int, int, void>)P("glFramebufferTexture2D");
        _renderbufferStorage = (delegate* unmanaged<int, int, int, int, void>)P("glRenderbufferStorage");
        _framebufferRenderbuffer = (delegate* unmanaged<int, int, int, int, void>)P("glFramebufferRenderbuffer");
        _checkFramebufferStatus = (delegate* unmanaged<int, int>)P("glCheckFramebufferStatus");
        _viewport = (delegate* unmanaged<int, int, int, int, void>)P("glViewport");
        _disable = (delegate* unmanaged<int, void>)P("glDisable");
        _clearColor = (delegate* unmanaged<float, float, float, float, void>)P("glClearColor");
        _clear = (delegate* unmanaged<int, void>)P("glClear");
        _readPixels = (delegate* unmanaged<int, int, int, int, int, int, void*, void>)P("glReadPixels");
        _contextReady = true;
        EnsureFbo();
        if (Session?.Core != null && Session.RequiresHwRender) Session.Core.FireHwContextReset();
    }

    private void EnsureFbo()
    {
        var core = Session?.Core;
        var w = (int)(core?.AvInfo.geometry.max_width ?? 640); var h = (int)(core?.AvInfo.geometry.max_height ?? 480);
        w = Math.Max(w, 1); h = Math.Max(h, 1);
        if (_fbo != 0 && _fboW == w && _fboH == h) return;
        DeleteFbo();
        int fbo, tex, depth;
        _genFramebuffers(1, &fbo); _genTextures(1, &tex); _genRenderbuffers(1, &depth);
        _bindTexture(GL_TEXTURE_2D, tex);
        _texImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, w, h, 0, GL_RGBA, GL_UNSIGNED_BYTE, null);
        _texParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        _texParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        _bindFramebuffer(GL_FRAMEBUFFER, fbo);
        _framebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, tex, 0);
        var wantsStencil = core?.HwRender.stencil ?? true;
        _bindRenderbuffer(GL_RENDERBUFFER, depth);
        _renderbufferStorage(GL_RENDERBUFFER, wantsStencil ? GL_DEPTH24_STENCIL8 : GL_DEPTH_COMPONENT24, w, h);
        _framebufferRenderbuffer(GL_FRAMEBUFFER, wantsStencil ? GL_DEPTH_STENCIL_ATTACHMENT : GL_DEPTH_ATTACHMENT, GL_RENDERBUFFER, depth);
        var status = _checkFramebufferStatus(GL_FRAMEBUFFER);
        if (status != GL_FRAMEBUFFER_COMPLETE) Error?.Invoke($"GL framebuffer incomplete: 0x{status:X}");
        _clearColor(0, 0, 0, 1); _clear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
        _fbo = fbo; _tex = tex; _depth = depth; _fboW = w; _fboH = h;
    }

    private void DeleteFbo()
    {
        if (_fbo == 0) return;
        int fbo = _fbo, tex = _tex, depth = _depth;
        _deleteFramebuffers(1, &fbo); _deleteTextures(1, &tex); _deleteRenderbuffers(1, &depth);
        _fbo = _tex = _depth = 0;
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _contextReady = false;
        try { if (Session?.Core != null && Session.RequiresHwRender) Session.Core.FireHwContextDestroy(); } catch { }
        DeleteFbo();
        _gl = null;
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        var session = Session;
        if (session?.Core == null || !_contextReady) return;
        EnsureFbo();
        var core = session.Core;
        try { session.RunFrameOnCallerThread(); }
        catch (Exception ex) { Error?.Invoke(ex.Message); }
        // Blit the core's framebuffer to Avalonia's.
        var scaling = VisualRoot?.RenderScaling ?? 1.0;
        var pw = (int)(Bounds.Width * scaling); var ph = (int)(Bounds.Height * scaling);
        var fw = (int)Math.Min(core.AvInfo.geometry.base_width, (uint)_fboW); var fh = (int)Math.Min(core.AvInfo.geometry.base_height, (uint)_fboH);
        if (session.Frame.Width > 0 && session.Frame.IsHardwareFrame) { fw = Math.Min(session.Frame.Width, _fboW); fh = Math.Min(session.Frame.Height, _fboH); }
        var dest = GameView.ComputeDestRect(new Size(pw, ph), fw, fh, core.AvInfo.geometry.aspect_ratio, Video);
        _disable(GL_SCISSOR_TEST);
        _bindFramebuffer(GL_DRAW_FRAMEBUFFER, fb);
        _viewport(0, 0, pw, ph);
        _clearColor(0, 0, 0, 1); _clear(GL_COLOR_BUFFER_BIT);
        _bindFramebuffer(GL_READ_FRAMEBUFFER, _fbo);
        var bottomLeft = core.HwRender.bottom_left_origin;
        int dx0 = (int)dest.X, dy0 = (int)(ph - dest.Bottom), dx1 = (int)dest.Right, dy1 = (int)(ph - dest.Y);
        if (bottomLeft) _blitFramebuffer(0, 0, fw, fh, dx0, dy0, dx1, dy1, GL_COLOR_BUFFER_BIT, Video.Filter == VideoFilter.Nearest ? GL_NEAREST : GL_LINEAR);
        else _blitFramebuffer(0, fh, fw, 0, dx0, dy0, dx1, dy1, GL_COLOR_BUFFER_BIT, Video.Filter == VideoFilter.Nearest ? GL_NEAREST : GL_LINEAR);
        _bindFramebuffer(GL_FRAMEBUFFER, fb);
        if (session.State == SessionState.Running) Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Render);
    }

    /// <summary>Reads the current core framebuffer back into the session's FrameBuffer (for screenshots / state thumbnails). GL thread only.</summary>
    public void CaptureFrame()
    {
        var session = Session; var core = session?.Core;
        if (core == null || !_contextReady || _fbo == 0) return;
        var fw = (int)Math.Min(core.AvInfo.geometry.base_width, (uint)_fboW); var fh = (int)Math.Min(core.AvInfo.geometry.base_height, (uint)_fboH);
        var px = new int[fw * fh];
        _bindFramebuffer(GL_READ_FRAMEBUFFER, _fbo);
        fixed (int* p = px) _readPixels(0, 0, fw, fh, GL_BGRA, GL_UNSIGNED_BYTE, p);
        if (!core.HwRender.bottom_left_origin) { }
        // GL rows are bottom-up
        var flipped = new int[fw * fh];
        for (var y = 0; y < fh; y++) Array.Copy(px, (fh - 1 - y) * fw, flipped, y * fw, fw);
        for (var i = 0; i < flipped.Length; i++) flipped[i] |= unchecked((int)0xFF000000);
        session!.Frame.SetPixels(flipped, fw, fh);
    }
}
