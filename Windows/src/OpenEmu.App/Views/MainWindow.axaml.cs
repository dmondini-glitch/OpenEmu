using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OpenEmu.App.Services;
using OpenEmu.App.ViewModels;
using OpenEmu.Core.Config;
using OpenEmu.Core.Library;
using OpenEmu.Core.Localization;
using OpenEmu.Core.Systems;

namespace OpenEmu.App.Views;

public partial class MainWindow : Window
{
    private readonly AppServices _s = App.Services;
    private readonly ObservableCollection<GameItem> _items = new();
    private readonly List<SidebarEntry> _systemEntries = new();
    private readonly List<SidebarEntry> _collectionEntries = new();
    private SidebarEntry? _selected;
    private bool _syncingSidebar;
    private HomebrewView? _homebrew;

    private sealed record SidebarEntry(string Kind, string Id, string Label, object? Data) { public override string ToString() => Label; }

    public MainWindow()
    {
        InitializeComponent();
        SizeSlider.Value = _s.Settings.GridItemSize;
        GamesGrid.Tag = _s.Settings.GridItemSize;
        GamesGrid.ItemsSource = _items; GamesList.ItemsSource = _items;
        SystemsList.SelectionChanged += (_, _) => OnSidebar(SystemsList);
        CollectionsList.SelectionChanged += (_, _) => OnSidebar(CollectionsList);
        HomebrewList.SelectionChanged += (_, _) => OnSidebar(HomebrewList);
        SearchBox.TextChanged += (_, _) => Refresh();
        SizeSlider.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) { GamesGrid.Tag = SizeSlider.Value; _s.Settings.GridItemSize = SizeSlider.Value; } };
        GridBtn.Click += (_, _) => SetView(LibraryViewMode.Grid);
        ListBtn.Click += (_, _) => SetView(LibraryViewMode.List);
        ImportBtn.Click += async (_, _) => await PickAndImport();
        PrefsBtn.Click += (_, _) => new PreferencesWindow().Show();
        CoresBtn.Click += (_, _) => new PreferencesWindow(PreferencesWindow.Tab.Cores).Show();
        AddCollectionBtn.Click += async (_, _) => { var n = await Dialogs.Prompt(this, L.T("sidebar.newCollection"), L.T("common.name")); if (!string.IsNullOrWhiteSpace(n)) { _s.Library.AddCollection(n); BuildSidebar(); } };
        foreach (var lb in new[] { GamesGrid, GamesList })
        {
            lb.DoubleTapped += async (_, _) => { if (lb.SelectedItem is GameItem gi) await _s.Launcher.LaunchAsync(gi.Game, this); };
            lb.KeyDown += async (_, e) => { if (e.Key == Key.Enter && lb.SelectedItem is GameItem gi) await _s.Launcher.LaunchAsync(gi.Game, this); };
            lb.ContextRequested += (_, e) => { if (lb.SelectedItem is GameItem gi) { BuildContextMenu(gi, lb.SelectedItems?.OfType<GameItem>().ToList() ?? new()).ShowAt(lb, true); e.Handled = true; } };
        }
        GamesGrid.SelectionChanged += (_, _) => { foreach (var gi in GamesGrid.SelectedItems?.OfType<GameItem>() ?? Enumerable.Empty<GameItem>()) gi.EnsureCover(); };
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => { e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None; DropOverlay.IsVisible = e.DragEffects != DragDropEffects.None; });
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropOverlay.IsVisible = false);
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            DropOverlay.IsVisible = false;
            var paths = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>().ToList();
            if (paths is { Count: > 0 }) await ImportPaths(paths);
        });
        _s.Library.Changed += () => Dispatcher.UIThread.Post(() => { BuildSidebar(); Refresh(); });
        SetView(_s.Settings.ViewMode);
        BuildSidebar();
        Opened += async (_, _) => await FirstRun();
        Closing += (_, _) => { _s.Settings.GridItemSize = SizeSlider.Value; _s.SaveSettings(); };
    }

    private async Task FirstRun()
    {
        _s.Library.VerifyFiles();
        foreach (var arg in App.LaunchArgs.Where(File.Exists))
        {
            var game = _s.Library.FindByPath(arg);
            if (game == null) { var r = await _s.Importer.ImportOneAsync(arg); game = r.Game; if (r.NeedsSystemChoice) { var pick = await Dialogs.Choose(this, L.T("common.system"), L.T("library.ambiguous", Path.GetFileName(arg)), r.AmbiguousSystems!, x => x.Name); if (pick != null) game = (await _s.Importer.ImportOneAsync(arg, pick)).Game; } }
            if (game != null) await _s.Launcher.LaunchAsync(game, this);
        }
        if (_s.Settings.WelcomeShown) return;
        _s.Settings.WelcomeShown = true;
        var installed = _s.Cores.Installed().Count;
        var needed = SystemCatalog.All.SelectMany(s => s.Cores).Distinct().Count();
        if (installed < needed / 2)
        {
            if (await Dialogs.Confirm(this, L.T("welcome.title"), L.T("welcome.body") + $"\n\n({installed}/{needed} cores)", L.T("welcome.installCores"), L.T("welcome.skip")))
                new PreferencesWindow(PreferencesWindow.Tab.Cores).Show();
        }
        _s.SaveSettings();
    }

    // ------------------------------------------------------------------ sidebar
    private void BuildSidebar()
    {
        _syncingSidebar = true;
        var counts = _s.Library.CountBySystem();
        _systemEntries.Clear();
        foreach (var sys in SystemCatalog.All)
        {
            if (_s.Settings.HiddenSystems.Contains(sys.Id)) continue;
            var n = counts.GetValueOrDefault(sys.Id);
            if (n == 0 && counts.Count > 0) continue; // like OpenEmu: only consoles with games (all when library empty)
            _systemEntries.Add(new SidebarEntry("system", sys.Id, n > 0 ? $"{sys.Name}  ({n})" : sys.Name, sys));
        }
        _collectionEntries.Clear();
        _collectionEntries.Add(new SidebarEntry("all", "all", L.T("sidebar.allGames"), null));
        _collectionEntries.Add(new SidebarEntry("recent", "recent", L.T("sidebar.recentlyAdded"), null));
        _collectionEntries.Add(new SidebarEntry("played", "played", L.T("sidebar.recentlyPlayed"), null));
        foreach (var c in _s.Library.Collections()) _collectionEntries.Add(new SidebarEntry("collection", c.Id.ToString(), c.Name, c));
        SystemsList.ItemsSource = _systemEntries.ToList();
        CollectionsList.ItemsSource = _collectionEntries.ToList();
        HomebrewList.ItemsSource = new[] { new SidebarEntry("homebrew", "homebrew", L.T("homebrew.title"), null) };
        _syncingSidebar = false;
        if (_selected == null) { CollectionsList.SelectedIndex = 0; }
        else Reselect();
        CollectionsList.ContextRequested += (_, e) =>
        {
            if (CollectionsList.SelectedItem is SidebarEntry { Kind: "collection", Data: Collection c })
            {
                var m = new MenuFlyout();
                var ren = new MenuItem { Header = L.T("sidebar.renameCollection") }; ren.Click += async (_, _) => { var n = await Dialogs.Prompt(this, L.T("sidebar.renameCollection"), L.T("common.name"), c.Name); if (!string.IsNullOrWhiteSpace(n)) _s.Library.RenameCollection(c.Id, n); };
                var del = new MenuItem { Header = L.T("sidebar.deleteCollection") }; del.Click += async (_, _) => { if (await Dialogs.Confirm(this, L.T("sidebar.deleteCollection"), c.Name)) { _selected = null; _s.Library.RemoveCollection(c.Id); } };
                m.Items.Add(ren); m.Items.Add(del); m.ShowAt(CollectionsList, true); e.Handled = true;
            }
        };
    }

    private void Reselect()
    {
        _syncingSidebar = true;
        SystemsList.SelectedItem = _systemEntries.FirstOrDefault(x => _selected != null && x.Kind == _selected.Kind && x.Id == _selected.Id);
        CollectionsList.SelectedItem = _collectionEntries.FirstOrDefault(x => _selected != null && x.Kind == _selected.Kind && x.Id == _selected.Id);
        _syncingSidebar = false;
        if (SystemsList.SelectedItem == null && CollectionsList.SelectedItem == null && _selected?.Kind != "homebrew") { _selected = null; CollectionsList.SelectedIndex = 0; }
    }

    private void OnSidebar(ListBox source)
    {
        if (_syncingSidebar || source.SelectedItem is not SidebarEntry entry) return;
        _syncingSidebar = true;
        foreach (var lb in new[] { SystemsList, CollectionsList, HomebrewList }) if (lb != source) lb.SelectedItem = null;
        _syncingSidebar = false;
        _selected = entry;
        Refresh();
    }

    // ------------------------------------------------------------------ content
    private void SetView(LibraryViewMode mode)
    {
        _s.Settings.ViewMode = mode;
        GridBtn.IsChecked = mode == LibraryViewMode.Grid; ListBtn.IsChecked = mode == LibraryViewMode.List;
        GridScroll.IsVisible = mode == LibraryViewMode.Grid && !HomebrewHost.IsVisible; ListScroll.IsVisible = mode == LibraryViewMode.List && !HomebrewHost.IsVisible;
    }

    private void Refresh()
    {
        var entry = _selected;
        if (entry?.Kind == "homebrew")
        {
            _homebrew ??= new HomebrewView(this);
            HomebrewHost.Content = _homebrew; HomebrewHost.IsVisible = true; GridScroll.IsVisible = ListScroll.IsVisible = false; BlankSlate.IsVisible = false;
            HeaderText.Text = L.T("homebrew.title"); CountText.Text = "";
            _ = _homebrew.LoadAsync();
            return;
        }
        HomebrewHost.IsVisible = false; SetView(_s.Settings.ViewMode);
        var search = SearchBox.Text;
        List<Game> games = entry?.Kind switch
        {
            "system" => _s.Library.All(entry.Id, search),
            "recent" => _s.Library.Recent(),
            "played" => _s.Library.RecentlyPlayed(),
            "collection" => _s.Library.GamesInCollection(long.Parse(entry.Id)),
            _ => _s.Library.All(null, search),
        };
        if (!string.IsNullOrWhiteSpace(search) && entry?.Kind is "recent" or "played" or "collection") games = games.Where(g => g.Title.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        _items.Clear();
        foreach (var g in games) _items.Add(new GameItem(g));
        HeaderText.Text = entry?.Label.Split("  (")[0] ?? L.T("sidebar.allGames");
        CountText.Text = games.Count == 1 ? L.T("library.game") : L.T("library.games", games.Count);
        var total = _s.Library.Count();
        BlankSlate.IsVisible = total == 0;
        StatusText.Text = total == 1 ? L.T("library.game") : L.T("library.games", total);
        // covers: load lazily but eagerly for the first screenful
        foreach (var gi in _items.Take(60)) gi.EnsureCover();
        Task.Run(() => { foreach (var gi in _items.Skip(60).ToList()) gi.EnsureCover(); });
    }

    private MenuFlyout BuildContextMenu(GameItem gi, List<GameItem> selection)
    {
        var m = new MenuFlyout();
        void Add(string header, Func<Task> action) { var mi = new MenuItem { Header = header }; mi.Click += async (_, _) => await action(); m.Items.Add(mi); }
        Add(L.T("game.play"), () => _s.Launcher.LaunchAsync(gi.Game, this));
        var sys = gi.System;
        if (sys != null)
        {
            var sub = new MenuItem { Header = L.T("game.playWith") };
            foreach (var core in _s.Cores.CoresForSystem(sys, _s.Settings))
            {
                var c = core;
                var mi = new MenuItem { Header = $"{c.Title}{(_s.Cores.IsInstalled(c.Id) ? "" : "  (" + L.T("prefs.cores.notInstalled") + ")")}" };
                mi.Click += async (_, _) => await _s.Launcher.LaunchAsync(gi.Game, this, c.Id);
                sub.Items.Add(mi);
            }
            m.Items.Add(sub);
        }
        m.Items.Add(new Separator());
        Add(L.T("game.rename"), async () => { var n = await Dialogs.Prompt(this, L.T("game.rename"), L.T("common.title"), gi.Game.Title); if (!string.IsNullOrWhiteSpace(n)) { gi.Game.Title = n; _s.Library.Update(gi.Game); } });
        Add(L.T("game.setCover"), async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false, FileTypeFilter = new[] { FilePickerFileTypes.ImageAll } });
            var p = files.FirstOrDefault()?.TryGetLocalPath();
            if (p == null) return;
            Directory.CreateDirectory(Paths.CoversDir);
            var dest = Path.Combine(Paths.CoversDir, (gi.Game.Md5 ?? Guid.NewGuid().ToString("N")) + Path.GetExtension(p));
            File.Copy(p, dest, true); gi.Game.CoverPath = dest; _s.Library.Update(gi.Game); gi.ReloadCover();
        });
        if (gi.Game.CoverUrl != null) Add(L.T("game.downloadCover"), async () => { try { gi.Game.CoverPath = await _s.Importer.DownloadCoverAsync(gi.Game.CoverUrl!, gi.Game); _s.Library.Update(gi.Game); } catch (Exception ex) { await Dialogs.Message(this, L.T("common.error"), ex.Message); } });
        var cols = _s.Library.Collections();
        if (cols.Count > 0)
        {
            var sub = new MenuItem { Header = L.T("game.addToCollection") };
            foreach (var c in cols) { var cc = c; var mi = new MenuItem { Header = c.Name }; mi.Click += (_, _) => { foreach (var s in selection) _s.Library.AddToCollection(cc.Id, s.Game.Id); }; sub.Items.Add(mi); }
            m.Items.Add(sub);
            if (_selected?.Kind == "collection") Add(L.T("game.removeFromCollection"), () => { foreach (var s in selection) _s.Library.RemoveFromCollection(long.Parse(_selected.Id), s.Game.Id); return Task.CompletedTask; });
        }
        Add(L.T("game.cheats"), () => new CheatsWindow(gi.Game, null).ShowDialog(this));
        Add(L.T("game.showInExplorer"), () => { RevealInExplorer(gi.Game.RomPath); return Task.CompletedTask; });
        Add(L.T("game.info"), () => Dialogs.Message(this, gi.Game.Title, string.Join("\n", new[] {
            $"{L.T("common.system")}: {gi.SystemName}", $"{L.T("game.core")}: {gi.Game.CoreOverride ?? "default"}", $"MD5: {gi.Game.Md5}", $"CRC32: {gi.Game.Crc32}", $"{L.T("common.size")}: {gi.SizeText}",
            $"{L.T("common.added")}: {gi.Added}", gi.PlayInfo, gi.Game.Developer != null ? $"Developer: {gi.Game.Developer}" : null, gi.Game.Genre != null ? $"Genre: {gi.Game.Genre}" : null, gi.Game.ReleaseDate != null ? $"Released: {gi.Game.ReleaseDate}" : null,
            gi.Game.Description != null ? "\n" + gi.Game.Description : null, "\n" + gi.Game.RomPath }.Where(x => x != null))));
        m.Items.Add(new Separator());
        Add(L.T("game.delete"), async () =>
        {
            if (!await Dialogs.Confirm(this, L.T("game.delete"), selection.Count == 1 ? gi.Game.Title : L.T("library.games", selection.Count))) return;
            var deleteFiles = await Dialogs.Confirm(this, L.T("game.delete"), L.T("game.deleteFiles"));
            foreach (var s in selection) { _s.Library.Remove(s.Game.Id); if (deleteFiles && File.Exists(s.Game.RomPath) && s.Game.RomPath.StartsWith(Paths.RomsDir)) { try { File.Delete(s.Game.RomPath); } catch { } } }
        });
        return m;
    }

    private static void RevealInExplorer(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) System.Diagnostics.Process.Start("open", $"-R \"{path}\"");
            else System.Diagnostics.Process.Start("xdg-open", Path.GetDirectoryName(path)!);
        }
        catch { }
    }

    // ------------------------------------------------------------------ import
    private async Task PickAndImport()
    {
        var exts = SystemCatalog.AllExtensions.Select(e => "*." + e).Append("*.zip").Distinct().ToList();
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true, Title = L.T("toolbar.import"),
            FileTypeFilter = new[] { new FilePickerFileType("ROMs") { Patterns = exts }, FilePickerFileTypes.All },
        });
        var paths = files.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>().ToList();
        if (paths.Count > 0) await ImportPaths(paths);
    }

    public async Task ImportPaths(IEnumerable<string> paths, SystemDefinition? force = null)
    {
        ImportBtn.IsEnabled = false;
        _s.Importer.CopyToLibrary = _s.Settings.CopyRomsToLibrary;
        var handler = new Action<string>(f => Dispatcher.UIThread.Post(() => StatusText.Text = $"{L.T("toolbar.import")} {f}"));
        _s.Importer.Progress += handler;
        try
        {
            var results = await _s.Importer.ImportAsync(paths, force);
            _s.Importer.Progress -= handler;
            var ok = results.Count(r => r.Success && r.Error == null);
            // resolve ambiguous systems one by one
            foreach (var amb in results.Where(r => r.NeedsSystemChoice).ToList())
            {
                var pick = await Dialogs.Choose(this, L.T("common.system"), L.T("library.ambiguous", Path.GetFileName(amb.SourcePath)), amb.AmbiguousSystems!, s => s.Name);
                if (pick == null) continue;
                var r = await _s.Importer.ImportOneAsync(amb.SourcePath, pick);
                if (r.Success) ok++;
            }
            var skipped = results.Count - ok;
            StatusText.Text = L.T("library.importDone", ok, skipped);
            var errors = results.Where(r => !r.Success && !r.NeedsSystemChoice && r.Error != "Already in library").Take(8).Select(r => $"• {Path.GetFileName(r.SourcePath)}: {r.Error}").ToList();
            if (errors.Count > 0) await Dialogs.Message(this, L.T("toolbar.import"), string.Join("\n", errors));
        }
        finally { _s.Importer.Progress -= handler; ImportBtn.IsEnabled = true; BuildSidebar(); Refresh(); }
    }
}
