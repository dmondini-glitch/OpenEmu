using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using OpenEmu.App.Services;
using OpenEmu.Core.Cheats;
using OpenEmu.Core.Emulation;
using OpenEmu.Core.Library;
using OpenEmu.Core.Libretro;
using OpenEmu.Core.Localization;

namespace OpenEmu.App.Views;

/// <summary>Save-state manager (load / rename / delete with thumbnails).</summary>
public sealed class SaveStatesWindow : Window
{
    private readonly EmulationSession _session;
    private readonly StackPanel _list = new() { Spacing = 6 };

    public SaveStatesWindow(EmulationSession session)
    {
        _session = session;
        Title = L.T("states.title"); Width = 560; Height = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var close = new Button { Content = L.T("common.close"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        close.Click += (_, _) => Close();
        var dock = new DockPanel();
        DockPanel.SetDock(close, Dock.Bottom);
        dock.Children.Add(close);
        dock.Children.Add(new ScrollViewer { Content = _list, Padding = new Thickness(12) });
        Content = dock;
        Refresh();
    }

    private void Refresh()
    {
        _list.Children.Clear();
        var states = _session.ListStates();
        if (states.Count == 0) { _list.Children.Add(new TextBlock { Text = L.T("states.empty"), Classes = { "muted" } }); return; }
        foreach (var st in states)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*,Auto"), Height = 84 };
            var img = new Image { Width = 112, Height = 76, Stretch = Stretch.Uniform };
            if (st.ScreenshotPath != null) { try { img.Source = new Bitmap(st.ScreenshotPath); } catch { } }
            row.Children.Add(new Border { Child = img, Background = Brushes.Black, CornerRadius = new CornerRadius(4) });
            var info = new StackPanel { Margin = new Thickness(10, 0), VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = st.Name, FontWeight = FontWeight.SemiBold });
            info.Children.Add(new TextBlock { Text = $"{st.CreatedAt.ToLocalTime():g} · {st.CoreId}", Classes = { "muted" }, FontSize = 12 });
            Grid.SetColumn(info, 1); row.Children.Add(info);
            var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            var load = new Button { Content = L.T("states.load") }; load.Click += async (_, _) => { await _session.LoadState(st); Close(); };
            var ren = new Button { Content = L.T("states.rename") }; ren.Click += async (_, _) => { var n = await Dialogs.Prompt(this, L.T("states.rename"), L.T("common.name"), st.Name); if (!string.IsNullOrWhiteSpace(n)) { _session.States.Rename(st, n); Refresh(); } };
            var del = new Button { Content = L.T("states.delete") }; del.Click += async (_, _) => { if (await Dialogs.Confirm(this, L.T("states.delete"), st.Name)) { _session.States.Delete(st); Refresh(); } };
            btns.Children.Add(load); btns.Children.Add(ren); btns.Children.Add(del);
            Grid.SetColumn(btns, 2); row.Children.Add(btns);
            _list.Children.Add(new Border { Child = row, Classes = { "card" }, Padding = new Thickness(6) });
        }
    }
}

/// <summary>Per-game cheat list stored in the library and applied live to the core.</summary>
public sealed class CheatsWindow : Window
{
    private readonly Game _game;
    private readonly EmulationSession? _session;
    private readonly StackPanel _list = new() { Spacing = 6 };
    private readonly GameLibrary _lib = App.Services.Library;

    public CheatsWindow(Game game, EmulationSession? session)
    {
        _game = game; _session = session;
        Title = $"{L.T("cheats.title")} — {game.Title}"; Width = 560; Height = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var add = new Button { Content = L.T("cheats.add") };
        add.Click += async (_, _) => await AddCheat();
        var close = new Button { Content = L.T("common.close") };
        close.Click += (_, _) => Close();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        bar.Children.Add(add); bar.Children.Add(close);
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom); dock.Children.Add(bar);
        var hint = new TextBlock { Text = L.T("cheats.hint"), Classes = { "muted" }, Margin = new Thickness(12, 10, 12, 0), TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(hint, Dock.Top); dock.Children.Add(hint);
        dock.Children.Add(new ScrollViewer { Content = _list, Padding = new Thickness(12) });
        Content = dock;
        Refresh();
    }

    private async Task AddCheat()
    {
        var desc = await Dialogs.Prompt(this, L.T("cheats.add"), L.T("cheats.description"));
        if (string.IsNullOrWhiteSpace(desc)) return;
        var code = await Dialogs.Prompt(this, L.T("cheats.add"), L.T("cheats.code"));
        if (string.IsNullOrWhiteSpace(code) || !Cheat.LooksValid(code)) return;
        _lib.AddCheat(new Cheat { GameId = _game.Id, Description = desc, Code = Cheat.NormalizeCode(code), Enabled = true });
        Refresh(); await Apply();
    }

    private async Task Apply()
    {
        if (_session == null) return;
        await _session.ApplyCheats(_lib.CheatsFor(_game.Id).Where(c => c.Enabled).Select(c => (true, c.Code)));
    }

    private void Refresh()
    {
        _list.Children.Clear();
        foreach (var c in _lib.CheatsFor(_game.Id))
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            var cb = new CheckBox { IsChecked = c.Enabled, VerticalAlignment = VerticalAlignment.Center };
            cb.IsCheckedChanged += async (_, _) => { c.Enabled = cb.IsChecked == true; _lib.UpdateCheat(c); await Apply(); };
            row.Children.Add(cb);
            var info = new StackPanel { Margin = new Thickness(8, 0) };
            info.Children.Add(new TextBlock { Text = c.Description, FontWeight = FontWeight.SemiBold });
            info.Children.Add(new TextBlock { Text = c.Code, Classes = { "muted" }, FontFamily = new FontFamily("Consolas,Menlo,monospace"), FontSize = 12 });
            Grid.SetColumn(info, 1); row.Children.Add(info);
            var del = new Button { Content = L.T("cheats.remove") };
            del.Click += async (_, _) => { _lib.RemoveCheat(c.Id); Refresh(); await Apply(); };
            Grid.SetColumn(del, 2); row.Children.Add(del);
            _list.Children.Add(new Border { Child = row, Classes = { "card" }, Padding = new Thickness(8) });
        }
        if (_list.Children.Count == 0) _list.Children.Add(new TextBlock { Text = "—", Classes = { "muted" } });
    }
}

/// <summary>Core options ("Core Settings" in OpenEmu), persisted per core in settings.</summary>
public sealed class CoreOptionsWindow : Window
{
    public CoreOptionsWindow(EmulationSession session, string coreId)
    {
        Title = $"{L.T("play.coreOptions")} — {session.Core?.LibraryName}"; Width = 620; Height = 560; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var settings = App.Services.Settings;
        if (!settings.CoreOptions.TryGetValue(coreId, out var saved)) settings.CoreOptions[coreId] = saved = new();
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(12) };
        var options = session.Core!.Options.Values.Where(o => o.Visible).OrderBy(o => o.Category ?? "").ThenBy(o => o.Description).ToList();
        string? lastCat = null;
        foreach (var o in options)
        {
            if (o.Category != lastCat) { panel.Children.Add(new TextBlock { Text = o.Category ?? "General", FontWeight = FontWeight.Bold, Margin = new Thickness(0, 10, 0, 2) }); lastCat = o.Category; }
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,220") };
            var label = new StackPanel();
            label.Children.Add(new TextBlock { Text = o.Description, TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(o.Info)) label.Children.Add(new TextBlock { Text = o.Info, Classes = { "muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap });
            row.Children.Add(label);
            var combo = new ComboBox { ItemsSource = o.Values.Select(v => v.Label).ToList(), HorizontalAlignment = HorizontalAlignment.Stretch };
            var idx = o.Values.FindIndex(v => v.Value == o.EffectiveValue);
            combo.SelectedIndex = idx >= 0 ? idx : 0;
            combo.SelectionChanged += async (_, _) =>
            {
                if (combo.SelectedIndex < 0) return;
                var val = o.Values[combo.SelectedIndex].Value;
                saved[o.Key] = val;
                await session.SetOption(o.Key, val);
            };
            Grid.SetColumn(combo, 1); row.Children.Add(combo);
            panel.Children.Add(row);
        }
        if (options.Count == 0) panel.Children.Add(new TextBlock { Text = "—", Classes = { "muted" } });
        var close = new Button { Content = L.T("common.close"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        close.Click += (_, _) => { App.Services.SaveSettings(); Close(); };
        var dock = new DockPanel();
        DockPanel.SetDock(close, Dock.Bottom); dock.Children.Add(close);
        dock.Children.Add(new ScrollViewer { Content = panel });
        Content = dock;
    }
}
