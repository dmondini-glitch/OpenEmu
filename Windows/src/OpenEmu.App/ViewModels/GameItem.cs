using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenEmu.Core.Library;
using OpenEmu.Core.Localization;
using OpenEmu.Core.Systems;

namespace OpenEmu.App.ViewModels;

/// <summary>One game in the grid/list, with lazily loaded cover art and a generated placeholder.</summary>
public sealed partial class GameItem : ObservableObject
{
    public Game Game { get; }
    public SystemDefinition? System { get; }
    [ObservableProperty] private Bitmap? _cover;
    private bool _loading;

    public GameItem(Game game)
    {
        Game = game;
        System = SystemCatalog.Find(game.SystemId);
    }

    public string Title => Game.Title;
    public string SystemName => System?.Name ?? Game.SystemId;
    public string SystemShort => System?.ShortId.ToUpperInvariant() ?? "?";
    public bool HasCover => Cover != null;
    public bool NoCover => Cover == null;
    public bool Missing => Game.Missing;
    public string PlayInfo => Game.PlayCount == 0 ? L.T("library.neverPlayed") : L.T("library.playCount", Game.PlayCount);
    public string LastPlayed => Game.LastPlayed is { } d ? d.ToLocalTime().ToString("g") : "—";
    public string Added => Game.AddedAt.ToLocalTime().ToString("d");
    public string SizeText => Game.Size < 1024 * 1024 ? $"{Game.Size / 1024.0:0} KB" : $"{Game.Size / 1024.0 / 1024.0:0.#} MB";
    public IBrush PlaceholderBrush => SystemBrush(Game.SystemId);

    public void EnsureCover()
    {
        if (_loading || Cover != null || string.IsNullOrEmpty(Game.CoverPath) || !File.Exists(Game.CoverPath)) return;
        _loading = true;
        var path = Game.CoverPath;
        Task.Run(() =>
        {
            try
            {
                var bmp = CoverCache.Load(path);
                Dispatcher.UIThread.Post(() => { Cover = bmp; OnPropertyChanged(nameof(HasCover)); OnPropertyChanged(nameof(NoCover)); });
            }
            catch { }
            finally { _loading = false; }
        });
    }

    public void ReloadCover()
    {
        CoverCache.Invalidate(Game.CoverPath);
        Cover = null; OnPropertyChanged(nameof(HasCover)); OnPropertyChanged(nameof(NoCover));
        EnsureCover();
    }

    public static IBrush SystemBrush(string systemId)
    {
        // deterministic hue per system, dark gradient like OpenEmu's blank covers
        var h = 0; foreach (var c in systemId) h = h * 31 + c;
        var hue = Math.Abs(h) % 360;
        var c1 = HslToRgb(hue, 0.45, 0.32); var c2 = HslToRgb(hue, 0.5, 0.18);
        return new LinearGradientBrush
        {
            StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative), EndPoint = new Avalonia.RelativePoint(1, 1, Avalonia.RelativeUnit.Relative),
            GradientStops = { new GradientStop(c1, 0), new GradientStop(c2, 1) },
        };
    }

    private static Color HslToRgb(double h, double s, double l)
    {
        double C = (1 - Math.Abs(2 * l - 1)) * s, X = C * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - C / 2;
        (double r, double g, double b) = h switch { < 60 => (C, X, 0.0), < 120 => (X, C, 0.0), < 180 => (0.0, C, X), < 240 => (0.0, X, C), < 300 => (X, 0.0, C), _ => (C, 0.0, X) };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}

public static class CoverCache
{
    private static readonly Dictionary<string, Bitmap> s_cache = new();
    public static Bitmap Load(string path)
    {
        lock (s_cache)
        {
            if (s_cache.TryGetValue(path, out var b)) return b;
            using var fs = File.OpenRead(path);
            b = Bitmap.DecodeToWidth(fs, 320);
            s_cache[path] = b;
            return b;
        }
    }
    public static void Invalidate(string? path) { if (path == null) return; lock (s_cache) s_cache.Remove(path); }
}
