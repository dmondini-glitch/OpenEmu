namespace OpenEmu.Core.Video;

/// <summary>
/// Minimal OpenGL helper (functions resolved through a proc-address resolver) that owns the FBO a libretro
/// hardware-rendered core draws into and blits it to the default framebuffer. Works on desktop GL 3.x+ and GLES3.
/// </summary>
public sealed unsafe class GlBlitter
{
    public const int GL_FRAMEBUFFER = 0x8D40, GL_READ_FRAMEBUFFER = 0x8CA8, GL_DRAW_FRAMEBUFFER = 0x8CA9, GL_COLOR_ATTACHMENT0 = 0x8CE0,
        GL_DEPTH_ATTACHMENT = 0x8D00, GL_DEPTH_STENCIL_ATTACHMENT = 0x821A, GL_TEXTURE_2D = 0x0DE1, GL_RGBA = 0x1908, GL_RGBA8 = 0x8058,
        GL_UNSIGNED_BYTE = 0x1401, GL_TEXTURE_MIN_FILTER = 0x2801, GL_TEXTURE_MAG_FILTER = 0x2800, GL_LINEAR = 0x2601, GL_NEAREST = 0x2600,
        GL_RENDERBUFFER = 0x8D41, GL_DEPTH24_STENCIL8 = 0x88F0, GL_DEPTH_COMPONENT24 = 0x81A6, GL_FRAMEBUFFER_COMPLETE = 0x8CD5,
        GL_COLOR_BUFFER_BIT = 0x4000, GL_DEPTH_BUFFER_BIT = 0x100, GL_STENCIL_BUFFER_BIT = 0x400, GL_BGRA = 0x80E1, GL_SCISSOR_TEST = 0x0C11,
        GL_VERSION = 0x1F02, GL_RENDERER = 0x1F01, GL_NO_ERROR = 0;

    private readonly Func<string, IntPtr> _resolve;
    private delegate* unmanaged[Stdcall]<int, int, int, int, int, int, int, int, int, int, void> _blitFramebuffer;
    private delegate* unmanaged[Stdcall]<int, int, void> _bindFramebuffer, _bindTexture, _bindRenderbuffer;
    private delegate* unmanaged[Stdcall]<int, int*, void> _genFramebuffers, _genTextures, _genRenderbuffers, _deleteFramebuffers, _deleteTextures, _deleteRenderbuffers;
    private delegate* unmanaged[Stdcall]<int, int, int, int, int, int, int, int, void*, void> _texImage2D;
    private delegate* unmanaged[Stdcall]<int, int, int, void> _texParameteri;
    private delegate* unmanaged[Stdcall]<int, int, int, int, int, void> _framebufferTexture2D;
    private delegate* unmanaged[Stdcall]<int, int, int, int, void> _renderbufferStorage, _framebufferRenderbuffer, _viewport;
    private delegate* unmanaged[Stdcall]<int, int> _checkFramebufferStatus;
    private delegate* unmanaged[Stdcall]<int, void> _disable, _clear;
    private delegate* unmanaged[Stdcall]<float, float, float, float, void> _clearColor;
    private delegate* unmanaged[Stdcall]<int, int, int, int, int, int, void*, void> _readPixels;
    private delegate* unmanaged[Stdcall]<int, byte*> _getString;
    private delegate* unmanaged[Stdcall]<int> _getError;
    private delegate* unmanaged[Stdcall]<void> _finish;

    public int Fbo { get; private set; }
    public int Texture { get; private set; }
    public int Depth { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public List<string> MissingFunctions { get; } = new();

    public GlBlitter(Func<string, IntPtr> resolve)
    {
        _resolve = resolve;
        IntPtr P(string n) { var p = resolve(n); if (p == IntPtr.Zero) MissingFunctions.Add(n); return p; }
        _blitFramebuffer = (delegate* unmanaged[Stdcall]<int, int, int, int, int, int, int, int, int, int, void>)P("glBlitFramebuffer");
        _bindFramebuffer = (delegate* unmanaged[Stdcall]<int, int, void>)P("glBindFramebuffer");
        _bindTexture = (delegate* unmanaged[Stdcall]<int, int, void>)P("glBindTexture");
        _bindRenderbuffer = (delegate* unmanaged[Stdcall]<int, int, void>)P("glBindRenderbuffer");
        _genFramebuffers = (delegate* unmanaged[Stdcall]<int, int*, void>)P("glGenFramebuffers");
        _genTextures = (delegate* unmanaged[Stdcall]<int, int*, void>)P("glGenTextures");
        _genRenderbuffers = (delegate* unmanaged[Stdcall]<int, int*, void>)P("glGenRenderbuffers");
        _deleteFramebuffers = (delegate* unmanaged[Stdcall]<int, int*, void>)P("glDeleteFramebuffers");
        _deleteTextures = (delegate* unmanaged[Stdcall]<int, int*, void>)P("glDeleteTextures");
        _deleteRenderbuffers = (delegate* unmanaged[Stdcall]<int, int*, void>)P("glDeleteRenderbuffers");
        _texImage2D = (delegate* unmanaged[Stdcall]<int, int, int, int, int, int, int, int, void*, void>)P("glTexImage2D");
        _texParameteri = (delegate* unmanaged[Stdcall]<int, int, int, void>)P("glTexParameteri");
        _framebufferTexture2D = (delegate* unmanaged[Stdcall]<int, int, int, int, int, void>)P("glFramebufferTexture2D");
        _renderbufferStorage = (delegate* unmanaged[Stdcall]<int, int, int, int, void>)P("glRenderbufferStorage");
        _framebufferRenderbuffer = (delegate* unmanaged[Stdcall]<int, int, int, int, void>)P("glFramebufferRenderbuffer");
        _viewport = (delegate* unmanaged[Stdcall]<int, int, int, int, void>)P("glViewport");
        _checkFramebufferStatus = (delegate* unmanaged[Stdcall]<int, int>)P("glCheckFramebufferStatus");
        _disable = (delegate* unmanaged[Stdcall]<int, void>)P("glDisable");
        _clear = (delegate* unmanaged[Stdcall]<int, void>)P("glClear");
        _clearColor = (delegate* unmanaged[Stdcall]<float, float, float, float, void>)P("glClearColor");
        _readPixels = (delegate* unmanaged[Stdcall]<int, int, int, int, int, int, void*, void>)P("glReadPixels");
        _getString = (delegate* unmanaged[Stdcall]<int, byte*>)P("glGetString");
        _getError = (delegate* unmanaged[Stdcall]<int>)P("glGetError");
        _finish = (delegate* unmanaged[Stdcall]<void>)P("glFinish");
    }

    public bool IsUsable => MissingFunctions.Count == 0;

    public string Version => _getString == null ? "" : System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)_getString(GL_VERSION)) ?? "";
    public string Renderer => _getString == null ? "" : System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)_getString(GL_RENDERER)) ?? "";
    public int Error => _getError == null ? 0 : _getError();

    /// <summary>Creates (or recreates) the offscreen FBO the core renders into.</summary>
    public bool EnsureFbo(int width, int height, bool depth, bool stencil)
    {
        width = Math.Max(1, width); height = Math.Max(1, height);
        if (Fbo != 0 && Width == width && Height == height) return true;
        DeleteFbo();
        int fbo, tex, rb = 0;
        _genFramebuffers(1, &fbo); _genTextures(1, &tex);
        _bindTexture(GL_TEXTURE_2D, tex);
        _texImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, width, height, 0, GL_RGBA, GL_UNSIGNED_BYTE, null);
        _texParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        _texParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        _bindFramebuffer(GL_FRAMEBUFFER, fbo);
        _framebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, tex, 0);
        if (depth || stencil)
        {
            _genRenderbuffers(1, &rb);
            _bindRenderbuffer(GL_RENDERBUFFER, rb);
            _renderbufferStorage(GL_RENDERBUFFER, stencil ? GL_DEPTH24_STENCIL8 : GL_DEPTH_COMPONENT24, width, height);
            _framebufferRenderbuffer(GL_FRAMEBUFFER, stencil ? GL_DEPTH_STENCIL_ATTACHMENT : GL_DEPTH_ATTACHMENT, GL_RENDERBUFFER, rb);
        }
        var status = _checkFramebufferStatus(GL_FRAMEBUFFER);
        _clearColor(0, 0, 0, 1); _clear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT | GL_STENCIL_BUFFER_BIT);
        _bindFramebuffer(GL_FRAMEBUFFER, 0);
        Fbo = fbo; Texture = tex; Depth = rb; Width = width; Height = height;
        return status == GL_FRAMEBUFFER_COMPLETE;
    }

    public void DeleteFbo()
    {
        if (Fbo == 0) return;
        int fbo = Fbo, tex = Texture, rb = Depth;
        _deleteFramebuffers(1, &fbo); _deleteTextures(1, &tex); if (rb != 0) _deleteRenderbuffers(1, &rb);
        Fbo = Texture = Depth = 0; Width = Height = 0;
    }

    /// <summary>Blits the core's frame (fw×fh, bottom-left of the FBO) to the destination framebuffer at destRect (GL coordinates, origin bottom-left).</summary>
    public void Present(int destFramebuffer, int viewportW, int viewportH, int fw, int fh, int dx0, int dy0, int dx1, int dy1, bool bottomLeftOrigin, bool linear)
    {
        fw = Math.Clamp(fw, 1, Width); fh = Math.Clamp(fh, 1, Height);
        _disable(GL_SCISSOR_TEST);
        _bindFramebuffer(GL_DRAW_FRAMEBUFFER, destFramebuffer);
        _viewport(0, 0, viewportW, viewportH);
        _clearColor(0, 0, 0, 1); _clear(GL_COLOR_BUFFER_BIT);
        _bindFramebuffer(GL_READ_FRAMEBUFFER, Fbo);
        var filter = linear ? GL_LINEAR : GL_NEAREST;
        if (bottomLeftOrigin) _blitFramebuffer(0, 0, fw, fh, dx0, dy0, dx1, dy1, GL_COLOR_BUFFER_BIT, filter);
        else _blitFramebuffer(0, fh, fw, 0, dx0, dy0, dx1, dy1, GL_COLOR_BUFFER_BIT, filter);
        _bindFramebuffer(GL_FRAMEBUFFER, destFramebuffer);
    }

    /// <summary>Reads the core's frame back as top-down BGRA.</summary>
    public int[] ReadPixels(int fw, int fh, bool bottomLeftOrigin)
    {
        fw = Math.Clamp(fw, 1, Width); fh = Math.Clamp(fh, 1, Height);
        var px = new int[fw * fh];
        _bindFramebuffer(GL_READ_FRAMEBUFFER, Fbo);
        fixed (int* p = px) _readPixels(0, 0, fw, fh, GL_BGRA, GL_UNSIGNED_BYTE, p);
        var outp = new int[fw * fh];
        for (var y = 0; y < fh; y++) Array.Copy(px, (bottomLeftOrigin ? fh - 1 - y : y) * fw, outp, y * fw, fw);
        for (var i = 0; i < outp.Length; i++) outp[i] |= unchecked((int)0xFF000000);
        return outp;
    }

    public void Finish() { if (_finish != null) _finish(); }

    /// <summary>Computes the destination rectangle (GL coordinates, origin bottom-left) with OpenEmu-style scaling.</summary>
    public static (int X0, int Y0, int X1, int Y1) DestRect(int viewportW, int viewportH, int fw, int fh, float aspect, bool keepAspect, bool integral)
    {
        if (viewportW <= 0 || viewportH <= 0) return (0, 0, Math.Max(1, viewportW), Math.Max(1, viewportH));
        var srcAspect = aspect > 0 ? (double)aspect : (double)fw / fh;
        double dw, dh;
        if (!keepAspect) { dw = viewportW; dh = viewportH; }
        else if (integral)
        {
            var scale = Math.Max(1, (int)Math.Floor(Math.Min(viewportH / (double)fh, viewportW / (fh * srcAspect))));
            dh = fh * scale; dw = Math.Round(dh * srcAspect);
        }
        else if ((double)viewportW / viewportH > srcAspect) { dh = viewportH; dw = Math.Round(dh * srcAspect); }
        else { dw = viewportW; dh = Math.Round(dw / srcAspect); }
        var x0 = (int)((viewportW - dw) / 2); var y0 = (int)((viewportH - dh) / 2);
        return (x0, y0, x0 + (int)dw, y0 + (int)dh);
    }
}
