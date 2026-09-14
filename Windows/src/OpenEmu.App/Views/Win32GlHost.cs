using Avalonia.Controls;
using Avalonia.Platform;
using OpenEmu.Core.Config;
using OpenEmu.Core.Video;

namespace OpenEmu.App.Views;

/// <summary>Hosts the native WGL child window used by hardware-rendered cores on Windows.</summary>
public sealed class Win32GlHost : NativeControlHost
{
    private readonly TaskCompletionSource<Win32GlContext> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Win32GlContext? Context { get; private set; }
    public VideoSettings Video { get; set; } = new();
    public Task<Win32GlContext> Ready => _ready.Task;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsWindows()) return base.CreateNativeControlCore(parent);
        try
        {
            Context = new Win32GlContext(parent.Handle, (int)Math.Max(1, Bounds.Width), (int)Math.Max(1, Bounds.Height)) { Video = Video };
            _ready.TrySetResult(Context);
            return new PlatformHandle(Context.Hwnd, "HWND");
        }
        catch (Exception ex) { _ready.TrySetException(ex); throw; }
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (!OperatingSystem.IsWindows()) { base.DestroyNativeControlCore(control); return; }
        // The emulation thread tears the GL context down; only the window is destroyed here.
        var ctx = Context; Context = null;
        ctx?.Dispose();
    }
}
