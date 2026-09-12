using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using OpenEmu.App.Services;
using OpenEmu.Core.Homebrew;
using OpenEmu.Core.Localization;
using OpenEmu.Core.Systems;

namespace OpenEmu.App.Views;

/// <summary>OpenEmu's Homebrew section: curated free games that can be downloaded straight into the library.</summary>
public sealed class HomebrewView : UserControl
{
    private readonly MainWindow _owner;
    private readonly AppServices _s = App.Services;
    private readonly WrapPanel _panel = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(10) };
    private readonly TextBlock _status = new() { Margin = new Thickness(16), Classes = { "muted" } };
    private bool _loaded;

    public HomebrewView(MainWindow owner)
    {
        _owner = owner;
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = L.T("homebrew.body"), Margin = new Thickness(16, 14, 16, 0), Classes = { "muted" } });
        stack.Children.Add(_status);
        stack.Children.Add(_panel);
        Content = new ScrollViewer { Content = stack };
    }

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _status.Text = L.T("common.loading");
        try
        {
            var games = await _s.Homebrew.LoadAsync();
            _panel.Children.Clear();
            foreach (var g in games.OrderByDescending(g => g.Added)) _panel.Children.Add(Card(g));
            _status.Text = L.T("library.games", games.Count);
            _loaded = true;
        }
        catch (Exception ex) { _status.Text = L.T("homebrew.offline") + " " + ex.Message; }
    }

    private Control Card(HomebrewGame g)
    {
        var sys = SystemCatalog.Find(g.SystemId);
        var card = new Border { Classes = { "card" }, Width = 260, Margin = new Thickness(6), Padding = new Thickness(10) };
        var st = new StackPanel { Spacing = 6 };
        var img = new Border { Height = 150, CornerRadius = new CornerRadius(4), ClipToBounds = true, Background = ViewModels.GameItem.SystemBrush(g.SystemId) };
        var image = new Image { Stretch = Stretch.Uniform };
        img.Child = image;
        if (g.CoverUrl != null) _ = LoadImage(g.CoverUrl, image);
        st.Children.Add(img);
        st.Children.Add(new TextBlock { Text = g.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        st.Children.Add(new TextBlock { Text = $"{sys?.Name ?? g.SystemId} · {g.Developer}", Classes = { "muted" }, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
        st.Children.Add(new TextBlock { Text = g.Description ?? "", Classes = { "muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis });
        var btn = new Button { Content = L.T("homebrew.download"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        btn.Click += async (_, _) =>
        {
            btn.IsEnabled = false;
            try
            {
                var path = await _s.Homebrew.DownloadAsync(g);
                var res = await _s.Importer.ImportOneAsync(path, sys);
                var game = res.Game ?? _s.Library.FindByPath(path);
                if (game != null)
                {
                    if (game.CoverPath == null && g.CoverUrl != null) { try { game.CoverPath = await _s.Importer.DownloadCoverAsync(g.CoverUrl, game); game.Developer ??= g.Developer; game.Description ??= g.Description; _s.Library.Update(game); } catch { } }
                    await _s.Launcher.LaunchAsync(game, _owner);
                }
                else await Dialogs.Message(_owner, L.T("common.error"), res.Error ?? "?");
            }
            catch (Exception ex) { await Dialogs.Message(_owner, L.T("common.error"), ex.Message); }
            finally { btn.IsEnabled = true; }
        };
        st.Children.Add(btn);
        card.Child = st;
        return card;
    }

    private async Task LoadImage(string url, Image target)
    {
        try
        {
            var cache = Path.Combine(Core.Config.Paths.ConfigRoot, "homebrew-cache", Path.GetFileName(new Uri(url).AbsolutePath).Replace("%20", " "));
            if (!File.Exists(cache))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
                var bytes = await _s.Http.GetByteArrayAsync(url);
                await File.WriteAllBytesAsync(cache, bytes);
            }
            var bmp = await Task.Run(() => { using var fs = File.OpenRead(cache); return Bitmap.DecodeToWidth(fs, 480); });
            Dispatcher.UIThread.Post(() => target.Source = bmp);
        }
        catch { }
    }
}
