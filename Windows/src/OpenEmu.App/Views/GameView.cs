using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using OpenEmu.Core.Config;
using OpenEmu.Core.Video;

namespace OpenEmu.App.Views;

/// <summary>Displays a software-rendered libretro frame with OpenEmu-style scaling options.</summary>
public sealed class GameView : Control
{
    private WriteableBitmap? _bmp;
    private long _drawnVersion = -1;
    private int _redrawPending;
    public FrameBuffer? Frame { get; set; }
    public VideoSettings Video { get; set; } = new();
    public uint Rotation { get; set; }

    public GameView() { ClipToBounds = true; }

    public void RequestRedraw()
    {
        if (Interlocked.Exchange(ref _redrawPending, 1) == 1) return;
        Dispatcher.UIThread.Post(() => { _redrawPending = 0; InvalidateVisual(); }, DispatcherPriority.Render);
    }

    public static Rect ComputeDestRect(Size bounds, int w, int h, float aspect, VideoSettings video)
    {
        if (w <= 0 || h <= 0) return new Rect(0, 0, bounds.Width, bounds.Height);
        var srcAspect = aspect > 0 ? aspect : (double)w / h;
        if (!video.KeepAspectRatio) return new Rect(0, 0, bounds.Width, bounds.Height);
        double dw, dh;
        if (video.IntegralScaling)
        {
            // integer multiple of the native height, width follows the aspect ratio
            var scale = Math.Max(1, (int)Math.Floor(Math.Min(bounds.Height / h, bounds.Width / (h * srcAspect))));
            dh = h * scale; dw = Math.Round(dh * srcAspect);
        }
        else
        {
            if (bounds.Width / bounds.Height > srcAspect) { dh = bounds.Height; dw = dh * srcAspect; }
            else { dw = bounds.Width; dh = dw / srcAspect; }
        }
        return new Rect((bounds.Width - dw) / 2, (bounds.Height - dh) / 2, dw, dh);
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        var frame = Frame;
        if (frame == null) return;
        int w, h; float aspect;
        lock (frame.SyncRoot)
        {
            w = frame.Width; h = frame.Height; aspect = frame.AspectRatio;
            if (w <= 0 || h <= 0) return;
            if (_bmp == null || _bmp.PixelSize.Width != w || _bmp.PixelSize.Height != h)
            {
                _bmp?.Dispose();
                _bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                _drawnVersion = -1;
            }
            if (frame.Version != _drawnVersion)
            {
                using var fb = _bmp.Lock();
                var px = frame.Pixels;
                if (fb.RowBytes == w * 4) System.Runtime.InteropServices.Marshal.Copy(px, 0, fb.Address, w * h);
                else for (var y = 0; y < h; y++) System.Runtime.InteropServices.Marshal.Copy(px, y * w, fb.Address + y * fb.RowBytes, w);
                _drawnVersion = frame.Version;
            }
        }
        RenderOptions.SetBitmapInterpolationMode(this, Video.Filter == VideoFilter.Nearest ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
        var dest = ComputeDestRect(Bounds.Size, w, h, aspect, Video);
        if (Rotation % 4 != 0)
        {
            var angle = Rotation % 4 * 90.0; // libretro rotation is counter-clockwise
            using (ctx.PushTransform(Matrix.CreateTranslation(-dest.Center.X, -dest.Center.Y) * Matrix.CreateRotation(-angle * Math.PI / 180) * Matrix.CreateTranslation(dest.Center.X, dest.Center.Y)))
                ctx.DrawImage(_bmp, new Rect(0, 0, w, h), dest);
        }
        else ctx.DrawImage(_bmp, new Rect(0, 0, w, h), dest);
    }
}
