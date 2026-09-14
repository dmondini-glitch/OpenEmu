using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OpenEmu.App.Services;
using OpenEmu.Core.Config;
using OpenEmu.Core.Cores;
using OpenEmu.Core.Input;
using OpenEmu.Core.Library;
using OpenEmu.Core.Localization;
using OpenEmu.Core.Systems;

namespace OpenEmu.App.Views;

public partial class PreferencesWindow : Window
{
    public enum Tab { Library = 0, Gameplay = 1, Controls = 2, Cores = 3, Bios = 4, About = 5 }

    private readonly AppServices _s = App.Services;
    private readonly DispatcherTimer _padTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private (string Control, int Player, Button Button)? _capture;
    private GamepadState[] _padBaseline = new GamepadState[4];
    private Action<int>? _hotkeyCapture;

    public PreferencesWindow(Tab tab = Tab.Library, string? systemId = null)
    {
        InitializeComponent();
        Tabs.SelectedIndex = (int)tab;
        BuildLibrary(); BuildGameplay(); BuildControls(systemId); BuildCores(); BuildBios();
        var hostPath = Core.Remote.RemoteSession.FindHost(CoreManifest.HostArchitecture(CoreManifest.PlatformKey));
        VersionText.Text = $"Version {typeof(PreferencesWindow).Assembly.GetName().Version} · .NET {Environment.Version} · app: {CoreManifest.NativePlatformKey} · cores: {CoreManifest.PlatformKey}"
            + (CoreManifest.RequiresCoreHost ? $" (core host {CoreManifest.HostArchitecture(CoreManifest.PlatformKey)}: {(hostPath != null ? "ok" : "MISSING")})" : "");
        PathsText.Text = $"Library: {Paths.LibraryRoot}\nSettings: {Paths.SettingsFile}\nCores: {_s.Cores.UserCoresDir}\nBundled cores: {_s.Cores.BundledCoresDir}\nBIOS: {Paths.BiosDir}";
        _padTimer.Tick += (_, _) => PollPads();
        KeyDown += OnKeyDown;
        Closing += (_, _) => { _padTimer.Stop(); _s.Input.Rebuild(); _s.SaveSettings(); };
    }

    // ------------------------------------------------------------------ helpers
    private static Control Row(string label, Control input, string? hint = null)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*") };
        var l = new StackPanel();
        l.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        if (hint != null) l.Children.Add(new TextBlock { Text = hint, Classes = { "muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        g.Children.Add(l); Grid.SetColumn(input, 1); g.Children.Add(input);
        return g;
    }
    private static TextBlock Header(string text) => new() { Text = text, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 10, 0, 2) };
    private static CheckBox Check(bool value, Action<bool> set) { var cb = new CheckBox { IsChecked = value }; cb.IsCheckedChanged += (_, _) => set(cb.IsChecked == true); return cb; }

    // ------------------------------------------------------------------ library
    private void BuildLibrary()
    {
        var st = _s.Settings;
        var p = LibraryPanel;
        var rootBox = new TextBox { Text = Paths.LibraryRoot, IsReadOnly = true };
        var choose = new Button { Content = L.T("prefs.library.choose") };
        choose.Click += async (_, _) =>
        {
            var f = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
            var path = f.FirstOrDefault()?.TryGetLocalPath();
            if (path != null) { st.LibraryRoot = path; rootBox.Text = path; _s.SaveSettings(); await Dialogs.Message(this, L.T("prefs.library.root"), "Restart OpenEmu to use the new library location."); }
        };
        var rootRow = new DockPanel(); DockPanel.SetDock(choose, Dock.Right); rootRow.Children.Add(choose); rootRow.Children.Add(rootBox);
        p.Children.Add(Row(L.T("prefs.library.root"), rootRow));
        p.Children.Add(Row(L.T("prefs.library.copy"), Check(st.CopyRomsToLibrary, v => st.CopyRomsToLibrary = v)));

        var lang = new ComboBox { ItemsSource = L.Available.Select(a => a.Name).ToList(), SelectedIndex = L.Available.ToList().FindIndex(a => a.Code == st.Language) };
        lang.SelectionChanged += (_, _) => { if (lang.SelectedIndex >= 0) { st.Language = L.Available[lang.SelectedIndex].Code; L.SetLanguage(st.Language); _s.SaveSettings(); } };
        p.Children.Add(Row(L.T("prefs.library.language"), lang, "Restart to apply / Reinicie para aplicar"));

        var vgdbStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Text = _s.Vgdb.IsAvailable ? L.T("prefs.library.vgdbOk") : L.T("prefs.library.vgdbMissing"), Foreground = _s.Vgdb.IsAvailable ? Brushes.LightGreen : Brushes.Orange };
        var vgdbBtn = new Button { Content = L.T("prefs.library.vgdbDownload") };
        vgdbBtn.Click += async (_, _) =>
        {
            try
            {
                await Dialogs.RunWithProgress(this, "OpenVGDB", prog => OpenVgdb.DownloadAsync(Paths.OpenVgdbFile, _s.Http, prog));
                vgdbStatus.Text = L.T("prefs.library.vgdbOk"); vgdbStatus.Foreground = Brushes.LightGreen;
            }
            catch (Exception ex) { await Dialogs.Message(this, L.T("common.error"), ex.Message); }
        };
        var vg = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 }; vg.Children.Add(vgdbBtn); vg.Children.Add(vgdbStatus);
        p.Children.Add(Row(L.T("prefs.library.vgdb"), vg));

        var rescan = new Button { Content = L.T("prefs.library.rescan") };
        rescan.Click += (_, _) => { var n = _s.Library.VerifyFiles(); rescan.Content = $"{L.T("prefs.library.rescan")} ({n})"; };
        p.Children.Add(Row(L.T("prefs.library.rescan"), rescan));
    }

    // ------------------------------------------------------------------ gameplay
    private void BuildGameplay()
    {
        var st = _s.Settings; var p = GameplayPanel;
        p.Children.Add(Header(L.T("prefs.gameplay.video")));
        var filter = new ComboBox { ItemsSource = new[] { L.T("prefs.gameplay.nearest"), L.T("prefs.gameplay.linear") }, SelectedIndex = (int)st.Video.Filter };
        filter.SelectionChanged += (_, _) => st.Video.Filter = (VideoFilter)filter.SelectedIndex;
        p.Children.Add(Row(L.T("prefs.gameplay.filter"), filter));
        p.Children.Add(Row(L.T("prefs.gameplay.integral"), Check(st.Video.IntegralScaling, v => st.Video.IntegralScaling = v)));
        p.Children.Add(Row(L.T("prefs.gameplay.aspect"), Check(st.Video.KeepAspectRatio, v => st.Video.KeepAspectRatio = v)));
        var scale = new ComboBox { ItemsSource = new[] { "1x", "2x", "3x", "4x", "5x", "6x" }, SelectedIndex = Math.Clamp(st.Video.WindowScale, 1, 6) - 1 };
        scale.SelectionChanged += (_, _) => st.Video.WindowScale = scale.SelectedIndex + 1;
        p.Children.Add(Row(L.T("prefs.gameplay.scale"), scale));
        p.Children.Add(Row(L.T("prefs.gameplay.fullscreen"), Check(st.Video.StartFullscreen, v => st.Video.StartFullscreen = v)));
        p.Children.Add(Row(L.T("prefs.gameplay.fps"), Check(st.Video.ShowFps, v => st.Video.ShowFps = v)));
        p.Children.Add(Header(L.T("prefs.gameplay.audio")));
        var vol = new Slider { Minimum = 0, Maximum = 1, Value = st.Audio.Volume, Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
        vol.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) st.Audio.Volume = (float)vol.Value; };
        p.Children.Add(Row(L.T("prefs.gameplay.volume"), vol));
        p.Children.Add(Header(L.T("prefs.gameplay.states")));
        p.Children.Add(Row(L.T("prefs.gameplay.autosave"), Check(st.Gameplay.AutoSaveStateOnQuit, v => st.Gameplay.AutoSaveStateOnQuit = v)));
        p.Children.Add(Row(L.T("prefs.gameplay.autoload"), Check(st.Gameplay.LoadAutoSaveOnStart, v => st.Gameplay.LoadAutoSaveOnStart = v)));
        p.Children.Add(Row(L.T("prefs.gameplay.messages"), Check(st.Gameplay.ShowCoreMessages, v => st.Gameplay.ShowCoreMessages = v)));
        p.Children.Add(Row(L.T("prefs.gameplay.pauseBackground"), Check(st.Gameplay.BackgroundPause, v => st.Gameplay.BackgroundPause = v)));
        var oop = Check(st.RunCoresOutOfProcess || CoreManifest.RequiresCoreHost, v => st.RunCoresOutOfProcess = v);
        oop.IsEnabled = !CoreManifest.RequiresCoreHost;
        p.Children.Add(Row(L.T("prefs.gameplay.outOfProcess"), oop, L.T("prefs.gameplay.outOfProcessHint", CoreManifest.PlatformKey)));
        p.Children.Add(Header(L.T("prefs.gameplay.hotkeys")));
        var hk = st.Hotkeys;
        void Hot(string label, Func<int> get, Action<int> set)
        {
            var b = new Button { Content = HidKeys.Name(get()), MinWidth = 140 };
            b.Click += (_, _) => { b.Content = L.T("prefs.controls.press"); _hotkeyCapture = code => { set(code); b.Content = HidKeys.Name(code); _hotkeyCapture = null; }; };
            p.Children.Add(Row(label, b));
        }
        Hot(L.T("play.pause"), () => hk.Pause, v => hk.Pause = v);
        Hot(L.T("play.fastForward"), () => hk.FastForward, v => hk.FastForward = v);
        Hot(L.T("play.quickSave", "n"), () => hk.QuickSave, v => hk.QuickSave = v);
        Hot(L.T("play.quickLoad", "n"), () => hk.QuickLoad, v => hk.QuickLoad = v);
        Hot(L.T("play.screenshot"), () => hk.Screenshot, v => hk.Screenshot = v);
        Hot(L.T("play.fullscreen"), () => hk.Fullscreen, v => hk.Fullscreen = v);
        Hot(L.T("play.reset"), () => hk.Reset, v => hk.Reset = v);
        Hot(L.T("play.mute"), () => hk.Mute, v => hk.Mute = v);
    }

    // ------------------------------------------------------------------ controls
    private void BuildControls(string? systemId)
    {
        var systems = SystemCatalog.All.ToList();
        ControlsSystem.ItemsSource = systems.Select(s => s.Name).ToList();
        ControlsSystem.SelectedIndex = Math.Max(0, systems.FindIndex(s => s.Id == systemId));
        ControlsPlayer.ItemsSource = Enumerable.Range(1, InputManager.MaxPlayers).Select(i => L.T("prefs.controls.player", i)).ToList();
        ControlsPlayer.SelectedIndex = 0;
        ControlsSystem.SelectionChanged += (_, _) => RefreshControls();
        ControlsPlayer.SelectionChanged += (_, _) => RefreshControls();
        ControlsReset.Click += (_, _) => { var sys = CurrentSystem(); if (sys != null) { _s.Input.UserBindings.ResetSystem(sys.Id); _s.Input.Rebuild(); RefreshControls(); } };
        Tabs.SelectionChanged += (_, _) => { if (Tabs.SelectedIndex == (int)Tab.Controls) _padTimer.Start(); else { _padTimer.Stop(); _capture = null; CaptureHint.IsVisible = false; } };
        if (Tabs.SelectedIndex == (int)Tab.Controls) _padTimer.Start();
        RefreshControls();
    }

    private SystemDefinition? CurrentSystem() => ControlsSystem.SelectedIndex >= 0 ? SystemCatalog.All[ControlsSystem.SelectedIndex] : null;

    private void RefreshControls()
    {
        ControlsPanel.Children.Clear();
        var sys = CurrentSystem(); if (sys == null) return;
        var player = Math.Max(0, ControlsPlayer.SelectedIndex);
        foreach (var group in sys.ControlGroups)
        {
            var box = new Border { Classes = { "card" }, Padding = new Thickness(10, 6), Margin = new Thickness(0, 2) };
            var grid = new StackPanel { Spacing = 2 };
            foreach (var control in group)
            {
                var bindings = _s.Input.BindingsFor(sys.Id, player, control.Name);
                var kb = bindings.FirstOrDefault(b => b.Kind == BindingKind.Keyboard);
                var pad = bindings.FirstOrDefault(b => b.Kind != BindingKind.Keyboard);
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("160,150,150,Auto") };
                row.Children.Add(new TextBlock { Text = control.Label, VerticalAlignment = VerticalAlignment.Center });
                var kbBtn = new Button { Content = kb?.DisplayName ?? "—", MinWidth = 130, Tag = "kb" };
                var padBtn = new Button { Content = pad?.DisplayName ?? "—", MinWidth = 130, Tag = "pad" };
                var c = control;
                kbBtn.Click += (_, _) => BeginCapture(c.Name, player, kbBtn);
                padBtn.Click += (_, _) => BeginCapture(c.Name, player, padBtn);
                var clear = new Button { Content = "✕" };
                ToolTip.SetTip(clear, L.T("prefs.controls.clear"));
                clear.Click += (_, _) => { _s.Input.UserBindings.Set(sys.Id, player, c.Name, new List<InputBinding>()); _s.Input.Rebuild(); RefreshControls(); };
                Grid.SetColumn(kbBtn, 1); Grid.SetColumn(padBtn, 2); Grid.SetColumn(clear, 3);
                row.Children.Add(kbBtn); row.Children.Add(padBtn); row.Children.Add(clear);
                grid.Children.Add(row);
            }
            box.Child = grid;
            ControlsPanel.Children.Add(box);
        }
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("160,150,150,Auto"), Margin = new Thickness(10, 0, 0, 4) };
        var h1 = new TextBlock { Text = L.T("prefs.controls.keyboard"), Classes = { "muted" }, FontSize = 11 }; Grid.SetColumn(h1, 1);
        var h2 = new TextBlock { Text = L.T("prefs.controls.gamepads"), Classes = { "muted" }, FontSize = 11 }; Grid.SetColumn(h2, 2);
        header.Children.Add(h1); header.Children.Add(h2);
        ControlsPanel.Children.Insert(0, header);
    }

    private void BeginCapture(string control, int player, Button button)
    {
        _capture = (control, player, button);
        button.Content = "…";
        CaptureHint.IsVisible = true;
        _s.Input.Poll();
        for (var i = 0; i < 4; i++) _padBaseline[i] = _s.Input.Gamepads.GetState(i);
        Focus();
    }

    private void CommitCapture(InputBinding binding)
    {
        if (_capture is not { } cap) return;
        var sys = CurrentSystem(); if (sys == null) return;
        var list = new List<InputBinding>(_s.Input.BindingsFor(sys.Id, cap.Player, cap.Control));
        // keyboard button replaces the keyboard binding, gamepad button replaces the gamepad binding
        list.RemoveAll(b => (b.Kind == BindingKind.Keyboard) == (binding.Kind == BindingKind.Keyboard));
        list.Add(binding);
        _s.Input.UserBindings.Set(sys.Id, cap.Player, cap.Control, list);
        _s.Input.Rebuild();
        _capture = null; CaptureHint.IsVisible = false;
        RefreshControls();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var hid = KeyMap.ToHid(e.PhysicalKey);
        if (_hotkeyCapture != null && hid != 0) { _hotkeyCapture(hid); e.Handled = true; return; }
        if (_capture is not { } cap) return;
        if (e.Key == Key.Escape) { _capture = null; CaptureHint.IsVisible = false; RefreshControls(); e.Handled = true; return; }
        if (hid == 0 || (string?)cap.Button.Tag != "kb") return;
        CommitCapture(InputBinding.Key(hid));
        e.Handled = true;
    }

    private void PollPads()
    {
        var connected = Enumerable.Range(0, _s.Input.Gamepads.MaxDevices).Select(i => _s.Input.Gamepads.GetState(i)).Count(s => s.Connected);
        GamepadStatus.Text = connected == 0 ? L.T("prefs.controls.noGamepad") : $"{connected} gamepad(s)";
        if (_capture is not { } cap || (string?)cap.Button.Tag != "pad") return;
        for (var i = 0; i < Math.Min(4, _s.Input.Gamepads.MaxDevices); i++)
        {
            var st = _s.Input.Gamepads.GetState(i);
            if (!st.Connected) continue;
            var newly = st.Buttons & ~_padBaseline[i].Buttons;
            if (newly != GamepadButtons.None)
            {
                var one = Enum.GetValues<GamepadButtons>().First(b => b != GamepadButtons.None && (newly & b) != 0);
                CommitCapture(InputBinding.Button(one)); return;
            }
            foreach (var axis in Enum.GetValues<GamepadAxis>())
            {
                var v = st.Axis(axis); var b = _padBaseline[i].Axis(axis);
                if (Math.Abs(v) > 20000 && Math.Abs(b) < 12000) { CommitCapture(InputBinding.Axis(axis, axis is GamepadAxis.LeftY or GamepadAxis.RightY ? -Math.Sign(v) : Math.Sign(v))); return; }
            }
        }
    }

    // ------------------------------------------------------------------ cores
    private void BuildCores()
    {
        InstallAllBtn.Click += async (_, _) => await InstallCores(SystemCatalog.All.SelectMany(s => s.Cores).Distinct().Where(id => !_s.Cores.IsInstalled(id)).ToList());
        CheckUpdatesBtn.Click += async (_, _) =>
        {
            CoresStatus.Text = L.T("common.loading");
            var updates = await _s.Cores.CheckUpdatesAsync();
            CoresStatus.Text = updates.Count == 0 ? L.T("prefs.cores.upToDate") : L.T("prefs.cores.updatesAvailable", updates.Count);
            if (updates.Count > 0 && await Dialogs.Confirm(this, L.T("prefs.cores.update"), string.Join(", ", updates.Select(u => u.Title)))) await InstallCores(updates.Select(u => u.Id).ToList());
        };
        RefreshCores();
    }

    private async Task InstallCores(List<string> ids)
    {
        if (ids.Count == 0) { CoresStatus.Text = L.T("prefs.cores.upToDate"); return; }
        InstallAllBtn.IsEnabled = CheckUpdatesBtn.IsEnabled = false; CoresProgress.IsVisible = true;
        var done = 0;
        var res = await _s.Cores.InstallAllAsync(ids, new Progress<(string Core, double Progress)>(p =>
        {
            CoresStatus.Text = L.T("notify.installing", p.Core) + $" ({done + 1}/{ids.Count})";
            CoresProgress.Value = (done + p.Progress) / ids.Count;
            if (p.Progress >= 1) done++;
        }));
        var failed = res.Where(r => r.Value != null).ToList();
        CoresStatus.Text = failed.Count == 0 ? L.T("common.done") : string.Join("; ", failed.Select(f => L.T("notify.installFailed", f.Key, f.Value!.Message)));
        CoresProgress.IsVisible = false; InstallAllBtn.IsEnabled = CheckUpdatesBtn.IsEnabled = true;
        RefreshCores();
    }

    private void RefreshCores()
    {
        CoresPanel.Children.Clear();
        foreach (var sys in SystemCatalog.All)
        {
            var box = new Border { Classes = { "card" }, Padding = new Thickness(10, 6), Margin = new Thickness(0, 2) };
            var st = new StackPanel { Spacing = 4 };
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,220") };
            head.Children.Add(new TextBlock { Text = sys.Name, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var lbl = new TextBlock { Text = L.T("prefs.cores.default", ""), Classes = { "muted" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }; Grid.SetColumn(lbl, 1); head.Children.Add(lbl);
            var cores = _s.Cores.CoresForSystem(sys);
            var def = new ComboBox { ItemsSource = cores.Select(c => c.Title).ToList(), HorizontalAlignment = HorizontalAlignment.Stretch };
            var pref = _s.Settings.DefaultCores.GetValueOrDefault(sys.Id);
            def.SelectedIndex = Math.Max(0, cores.ToList().FindIndex(c => c.Id == pref));
            def.SelectionChanged += (_, _) => { if (def.SelectedIndex >= 0) _s.Settings.DefaultCores[sys.Id] = cores[def.SelectedIndex].Id; };
            Grid.SetColumn(def, 2); head.Children.Add(def);
            st.Children.Add(head);
            foreach (var core in cores)
            {
                var c = core;
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,110,Auto"), Margin = new Thickness(12, 0, 0, 0) };
                var lib = _s.Cores.FindLibrary(c.Id);
                var bundled = lib != null && lib.StartsWith(_s.Cores.BundledCoresDir);
                row.Children.Add(new TextBlock { Text = $"{c.Title}  ·  {c.License}{(c.HwRender ? "  ·  OpenGL" : "")}", Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
                var status = new TextBlock { Text = lib != null ? L.T("prefs.cores.installed") + (bundled ? $" ({L.T("prefs.cores.bundled")})" : "") : L.T("prefs.cores.notInstalled"), Foreground = lib != null ? Brushes.LightGreen : Brushes.Orange, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
                Grid.SetColumn(status, 1); row.Children.Add(status);
                var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                var inst = new Button { Content = lib != null ? L.T("prefs.cores.update") : L.T("prefs.cores.install"), FontSize = 12, Padding = new Thickness(8, 2) };
                inst.Click += async (_, _) => await InstallCores(new List<string> { c.Id });
                btns.Children.Add(inst);
                if (lib != null && !bundled) { var un = new Button { Content = L.T("prefs.cores.uninstall"), FontSize = 12, Padding = new Thickness(8, 2) }; un.Click += (_, _) => { _s.Cores.Uninstall(c.Id); RefreshCores(); }; btns.Children.Add(un); }
                Grid.SetColumn(btns, 2); row.Children.Add(btns);
                st.Children.Add(row);
            }
            box.Child = st;
            CoresPanel.Children.Add(box);
        }
        var installed = _s.Cores.Installed().Count; var total = CoreManifest.All.Count(c => c.Id != "2048");
        if (string.IsNullOrEmpty(CoresStatus.Text) || CoresStatus.Text == L.T("common.done")) CoresStatus.Text = $"{installed}/{total}";
    }

    // ------------------------------------------------------------------ bios
    private void BuildBios()
    {
        BiosImportBtn.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true, Title = L.T("prefs.bios.import") });
            var n = 0;
            foreach (var f in files) { var p = f.TryGetLocalPath(); if (p != null && _s.Bios.Import(p) != null) n++; }
            BiosStatus.Text = $"{n} / {files.Count}";
            RefreshBios();
        };
        BiosFreeBtn.Click += async (_, _) =>
        {
            BiosFreeBtn.IsEnabled = false;
            try
            {
                var res = await Dialogs.RunWithProgress(this, L.T("prefs.bios.installFree"), p => Core.Bios.FreeSystemFiles.InstallAllAsync(_s.Http, progress: new Progress<(string Pack, double Progress)>(x => p.Report(x.Progress))));
                var failed = res.Where(r => r.Value != null).Select(r => $"{r.Key}: {r.Value!.Message}").ToList();
                BiosStatus.Text = failed.Count == 0 ? L.T("common.done") : string.Join("; ", failed);
            }
            catch (Exception ex) { BiosStatus.Text = ex.Message; }
            finally { BiosFreeBtn.IsEnabled = true; RefreshBios(); }
        };
        BiosFolderBtn.Click += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Paths.BiosDir) { UseShellExecute = true }); } catch { } };
        DragDrop.SetAllowDrop(BiosPanel, true);
        BiosPanel.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var n = 0;
            foreach (var f in e.Data.GetFiles() ?? Enumerable.Empty<IStorageItem>()) { var p = f.TryGetLocalPath(); if (p != null && File.Exists(p) && _s.Bios.Import(p) != null) n++; }
            BiosStatus.Text = $"{n}"; RefreshBios();
        });
        RefreshBios();
    }

    private void RefreshBios()
    {
        BiosPanel.Children.Clear();
        // open-source firmware coverage (what boots without proprietary BIOS)
        var cov = new Border { Classes = { "card" }, Padding = new Thickness(10, 6), Margin = new Thickness(0, 2, 0, 8) };
        var cst = new StackPanel { Spacing = 2 };
        cst.Children.Add(new TextBlock { Text = L.T("prefs.bios.coverage"), FontWeight = FontWeight.SemiBold });
        foreach (var (sysId, replacement, available) in Core.Bios.FreeSystemFiles.Coverage)
        {
            var sys = SystemCatalog.Find(sysId); if (sys == null) continue;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,200,*"), Margin = new Thickness(12, 0, 0, 0) };
            row.Children.Add(new TextBlock { Text = available ? "✔" : "✖", Foreground = available ? Brushes.LightGreen : Brushes.Orange, Width = 22 });
            var n = new TextBlock { Text = sys.Name, FontSize = 12 }; Grid.SetColumn(n, 1); row.Children.Add(n);
            var r = new TextBlock { Text = replacement, Classes = { "muted" }, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis }; Grid.SetColumn(r, 2); row.Children.Add(r);
            cst.Children.Add(row);
        }
        cov.Child = cst;
        BiosPanel.Children.Add(cov);
        foreach (var group in _s.Bios.Status().GroupBy(b => b.System.Name).OrderBy(g => g.Key))
        {
            var box = new Border { Classes = { "card" }, Padding = new Thickness(10, 6), Margin = new Thickness(0, 2) };
            var st = new StackPanel { Spacing = 2 };
            var required = group.Count(b => !b.Firmware.Optional); var present = group.Count(b => !b.Firmware.Optional && b.Present);
            st.Children.Add(new TextBlock { Text = group.Key + (required > 0 ? $"   ({present}/{required})" : ""), FontWeight = FontWeight.SemiBold });
            foreach (var b in group.OrderBy(b => b.Firmware.Optional).ThenBy(b => b.Firmware.Path))
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,220,*"), Margin = new Thickness(12, 0, 0, 0) };
                var soft = !b.Present && !b.Firmware.Optional && b.CoreHasHle;
                row.Children.Add(new TextBlock { Text = b.Present ? "✔" : (b.Firmware.Optional || soft ? "○" : "✖"), Foreground = b.Present ? Brushes.LightGreen : (b.Firmware.Optional || soft) ? Brushes.Gray : Brushes.Orange, Width = 22 });
                var name = new TextBlock { Text = b.Firmware.Path, FontFamily = new FontFamily("Consolas,Menlo,monospace"), FontSize = 12 }; Grid.SetColumn(name, 1); row.Children.Add(name);
                var extra = b.IsOpenBios ? "  ·  OpenBIOS" : (soft ? $"  ·  {L.T("prefs.bios.hle")}" : "");
                var desc = new TextBlock { Text = (b.Firmware.Description ?? "") + (b.Firmware.Optional ? $"  ({L.T("prefs.bios.optional")})" : "") + extra + $"  ·  {b.Core.Title}", Classes = { "muted" }, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis }; Grid.SetColumn(desc, 2); row.Children.Add(desc);
                st.Children.Add(row);
            }
            box.Child = st;
            BiosPanel.Children.Add(box);
        }
    }
}
