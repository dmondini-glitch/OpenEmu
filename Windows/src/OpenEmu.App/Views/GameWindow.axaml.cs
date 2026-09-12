using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OpenEmu.App.Services;
using OpenEmu.Core.Config;
using OpenEmu.Core.Emulation;
using OpenEmu.Core.Input;
using OpenEmu.Core.Library;
using OpenEmu.Core.Libretro;
using OpenEmu.Core.Remote;
using OpenEmu.Core.Localization;
using OpenEmu.Core.Saves;
using OpenEmu.Core.Systems;

namespace OpenEmu.App.Views;

public partial class GameWindow : Window
{
    private readonly Game _game;
    private readonly SessionOptions _opts;
    private readonly AppServices _s = App.Services;
    private IEmulator? _session;
    private GameView? _swView;
    private GlGameView? _glView;
    private readonly DispatcherTimer _hudTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _fpsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private WindowState _preFullscreen = WindowState.Normal;
    private bool _closing;
    private readonly HashSet<int> _heldHotkeys = new();

    public IEmulator? Session => _session;

    public GameWindow(Game game, SessionOptions opts)
    {
        _game = game; _opts = opts;
        InitializeComponent();
        Title = $"{game.Title} — {opts.System.Name} ({opts.Core.Title})";
        var scale = Math.Clamp(_s.Settings.Video.WindowScale, 1, 8);
        Width = 256 * scale * 1.333 + 40; Height = 240 * scale + 60;
        Opened += async (_, _) => await StartAsync();
        Closing += OnClosing;
        KeyDown += OnKeyDown; KeyUp += OnKeyUp;
        PointerMoved += (_, _) => ShowHud();
        Deactivated += (_, _) => { _session?.ClearKeys(); if (_s.Settings.Gameplay.BackgroundPause && _session is { State: SessionState.Running }) _session.Pause(); };
        Activated += (_, _) => { if (_s.Settings.Gameplay.BackgroundPause && _session is { State: SessionState.Paused } && !_userPaused) _session.Resume(); };
        _hudTimer.Tick += (_, _) => { Hud.IsVisible = false; _hudTimer.Stop(); };
        _toastTimer.Tick += (_, _) => { Toast.IsVisible = false; _toastTimer.Stop(); };
        _fpsTimer.Tick += (_, _) => { if (_session != null) FpsText.Text = $"{_session.MeasuredFps:0.0} fps"; };
        WireButtons();
        VolumeSlider.Value = _s.Settings.Audio.Volume;
        FpsText.IsVisible = _s.Settings.Video.ShowFps;
        if (_s.Settings.Video.StartFullscreen) ToggleFullscreen();
    }

    private bool _userPaused;

    private void WireButtons()
    {
        StopBtn.Click += (_, _) => Close();
        PauseBtn.Click += (_, _) => TogglePause();
        ResetBtn.Click += async (_, _) => { if (_session != null) await _session.Reset(); };
        FfBtn.IsCheckedChanged += (_, _) => SetFastForward(FfBtn.IsChecked == true);
        SaveBtn.Click += (_, _) => ShowSaveMenu();
        LoadBtn.Click += (_, _) => ShowLoadMenu();
        ShotBtn.Click += (_, _) => Screenshot();
        CheatsBtn.Click += async (_, _) => await ShowCheats();
        OptionsBtn.Click += async (_, _) => await ShowCoreOptions();
        ControlsBtn.Click += (_, _) => new PreferencesWindow(PreferencesWindow.Tab.Controls, _opts.System.Id).Show();
        DiscBtn.Click += (_, _) => ShowDiscMenu();
        MuteBtn.Click += (_, _) => { if (_session == null) return; _session.Muted = !_session.Muted; _s.Settings.Audio.Muted = _session.Muted; MuteBtn.Content = _session.Muted ? "🔇" : "🔊"; };
        VolumeSlider.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty && _session != null) { _session.Volume = (float)VolumeSlider.Value; _s.Settings.Audio.Volume = (float)VolumeSlider.Value; } };
        FullBtn.Click += (_, _) => ToggleFullscreen();
    }

    private async Task StartAsync()
    {
        try
        {
            EmulationSession? local = null;
            if (_s.Launcher.UseCoreHost(_opts.Core))
            {
                var host = RemoteSession.FindHost(Core.Cores.CoreManifest.HostArchitecture(Core.Cores.CoreManifest.PlatformKey))
                           ?? throw new FileNotFoundException($"Core host for {Core.Cores.CoreManifest.PlatformKey} not found");
                _session = new RemoteSession(_opts, host, System.Text.Json.JsonSerializer.Serialize(_s.Input.UserBindings, AppSettings.JsonOptions));
            }
            else
            {
                local = new EmulationSession(_opts, input: _s.Input);
                local.HwThreadInvoker = a => { if (Dispatcher.UIThread.CheckAccess()) a(); else Dispatcher.UIThread.Post(a); };
                if (_opts.Core.HwRender)
                {
                    _glView = new GlGameView { Session = local, Video = _s.Settings.Video };
                    _glView.Error += e => _s.Log.Error("GL: " + e);
                    local.HwRenderHost = _glView;
                }
                _session = local;
            }
            _session.Notification += msg => Dispatcher.UIThread.Post(() => ShowToast(msg));
            _session.Message += m => { if (_s.Settings.Gameplay.ShowCoreMessages) Dispatcher.UIThread.Post(() => ShowToast(m.Text)); };
            var debug = Environment.GetEnvironmentVariable("OPENEMU_DEBUG") == "1";
            _session.Log += (lvl, msg) => { if (lvl >= Retro.LogWarn) _s.Log.Warn($"[{_opts.Core.Id}] {msg}"); else if (debug) _s.Log.Info($"[{_opts.Core.Id}] {msg}"); };
            _session.StateChanged += st => Dispatcher.UIThread.Post(() => OnSessionState(st));
            await _session.StartAsync();
            var info = _session.Info!;
            if (local != null && local.RequiresHwRender)
            {
                _glView ??= new GlGameView { Session = local, Video = _s.Settings.Video };
                ViewHost.Content = _glView;
                _glView.RequestNextFrameRendering();
            }
            else
            {
                _swView = new GameView { Frame = _session.Frame, Video = _s.Settings.Video, Rotation = info.Rotation };
                _session.FrameRendered += () => _swView.RequestRedraw();
                ViewHost.Content = _swView;
            }
            _session.Volume = _s.Settings.Audio.Volume;
            _session.Muted = _s.Settings.Audio.Muted;
            MuteBtn.Content = _session.Muted ? "🔇" : "🔊";
            DiscBtn.IsVisible = info.HasDiskControl && info.DiskImageCount > 1;
            if (_s.Settings.Video.ShowFps) _fpsTimer.Start();
            await _session.ApplyCheats(_s.Library.CheatsFor(_game.Id).Where(c => c.Enabled).Select(c => (true, c.Code)));
            if (_s.Settings.Gameplay.LoadAutoSaveOnStart && _session.FindAutoSave() is { } auto) await _session.LoadState(auto);
            if (info.IsRemote) Title += "  ·  core host";
            ShowHud();
            Focus();
        }
        catch (Exception ex)
        {
            _s.Log.Error($"Launch failed: {ex}");
            await Dialogs.Message(this, L.T("common.error"), L.T("play.failed", _game.Title) + "\n\n" + ex.Message);
            Close();
        }
    }

    private void OnSessionState(SessionState st)
    {
        if (st is SessionState.Failed or SessionState.Stopped) _s.Log.Info($"Session {st} for {_game.Title} ({_opts.Core.Id}){(_session?.Error != null ? ": " + _session.Error.Message : "")}");
        PausedOverlay.IsVisible = st == SessionState.Paused;
        PauseBtn.Content = st == SessionState.Paused ? "▶" : "⏸";
        if (st == SessionState.Failed && !_closing)
        {
            _ = Dialogs.Message(this, L.T("common.error"), L.T("play.failed", _game.Title) + "\n\n" + _session?.Error?.Message).ContinueWith(_ => Dispatcher.UIThread.Post(Close));
        }
        else if (st == SessionState.Stopped && !_closing) Close();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closing) return;
        _closing = true;
        e.Cancel = true;
        try
        {
            if (_session != null && _session.State is SessionState.Running or SessionState.Paused)
            {
                if (_s.Settings.Gameplay.AutoSaveStateOnQuit) { try { CaptureIfGl(); await _session.AutoSave(); } catch { } }
                _session.Stop();
            }
            _s.Library.RecordPlay(_game.Id, DateTime.UtcNow - _startedAt);
            _s.SaveSettings();
        }
        finally
        {
            _session?.ClearKeys();
            _session?.Dispose();
            _s.Input.Keyboard.Clear();
            Closing -= OnClosing;
            Close();
        }
    }

    // ------------------------------------------------------------------ input
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var hid = KeyMap.ToHid(e.PhysicalKey);
        if (e.Key == Key.Escape && WindowState == WindowState.FullScreen) { ToggleFullscreen(); e.Handled = true; return; }
        if (hid != 0 && HandleHotkey(hid, true)) { e.Handled = true; return; }
        if (hid != 0) { _session?.SetKey(hid, true); SendRetroKey(hid, true, e); e.Handled = true; }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        var hid = KeyMap.ToHid(e.PhysicalKey);
        if (hid != 0 && _heldHotkeys.Remove(hid)) { HandleHotkey(hid, false); e.Handled = true; return; }
        if (hid != 0) { _session?.SetKey(hid, false); SendRetroKey(hid, false, e); e.Handled = true; }
    }

    private void SendRetroKey(int hid, bool down, KeyEventArgs e)
    {
        if (_session?.Info is not { HasKeyboardCallback: true }) return;
        var mods = (ushort)(((e.KeyModifiers & KeyModifiers.Shift) != 0 ? 1 : 0) | ((e.KeyModifiers & KeyModifiers.Control) != 0 ? 2 : 0) | ((e.KeyModifiers & KeyModifiers.Alt) != 0 ? 4 : 0));
        var rk = HidKeys.ToRetroKey(hid);
        if (rk != 0) _ = _session.SendKeyboardEvent(down, rk, rk < 128 ? rk : 0, mods);
    }

    private bool HandleHotkey(int hid, bool down)
    {
        var hk = _s.Settings.Hotkeys;
        if (_session == null) return false;
        if (hid == hk.FastForward) { if (down) { _heldHotkeys.Add(hid); SetFastForward(true); } else SetFastForward(false); return true; }
        if (!down) return false;
        if (hid == hk.Pause) { TogglePause(); return true; }
        if (hid == hk.QuickSave) { _ = QuickSave(); return true; }
        if (hid == hk.QuickLoad) { _ = _session.QuickLoad(); return true; }
        if (hid == hk.Screenshot) { Screenshot(); return true; }
        if (hid == hk.Fullscreen) { ToggleFullscreen(); return true; }
        if (hid == hk.Reset) { _ = _session.Reset(); return true; }
        if (hid == hk.NextSlot) { _session.CurrentSlot = _session.CurrentSlot % 9 + 1; ShowToast(L.T("notify.slot", _session.CurrentSlot)); return true; }
        if (hid == hk.PreviousSlot) { _session.CurrentSlot = _session.CurrentSlot == 1 ? 9 : _session.CurrentSlot - 1; ShowToast(L.T("notify.slot", _session.CurrentSlot)); return true; }
        if (hid == hk.Mute) { MuteBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return true; }
        return false;
    }

    // ------------------------------------------------------------------ actions
    private void TogglePause()
    {
        if (_session == null) return;
        if (_session.IsPaused) { _userPaused = false; _session.Resume(); ShowToast(L.T("notify.resumed")); }
        else { _userPaused = true; _session.Pause(); }
    }

    private void SetFastForward(bool on)
    {
        if (_session == null) return;
        _session.FastForward = on;
        if (FfBtn.IsChecked != on) FfBtn.IsChecked = on;
        ShowToast(on ? L.T("notify.ffOn") : L.T("notify.ffOff"));
    }

    private void CaptureIfGl() { if (_glView != null && Dispatcher.UIThread.CheckAccess()) { try { _glView.CaptureFrame(); } catch { } } }

    private async Task QuickSave() { CaptureIfGl(); if (_session != null) await _session.QuickSave(); }

    private void Screenshot()
    {
        if (_session == null) return;
        CaptureIfGl();
        try { _session.SaveScreenshot(Paths.ScreenshotsDir, _game.Title); } catch (Exception ex) { ShowToast(ex.Message); }
    }

    private void ShowSaveMenu()
    {
        if (_session == null) return;
        var menu = new MenuFlyout();
        var quick = new MenuItem { Header = L.T("play.quickSave", _session.CurrentSlot) };
        quick.Click += async (_, _) => await QuickSave();
        menu.Items.Add(quick);
        var named = new MenuItem { Header = L.T("play.newState") + "…" };
        named.Click += async (_, _) =>
        {
            var wasPaused = _session.IsPaused; if (!wasPaused) _session.Pause();
            var name = await Dialogs.Prompt(this, L.T("play.newState"), L.T("play.stateName"), $"{_game.Title} {DateTime.Now:g}");
            if (!string.IsNullOrWhiteSpace(name)) { CaptureIfGl(); await _session.SaveState(name); }
            if (!wasPaused) _session.Resume();
        };
        menu.Items.Add(named);
        menu.Items.Add(new Separator());
        for (var i = 1; i <= 9; i++)
        {
            var slot = i;
            var mi = new MenuItem { Header = L.T("play.slot", slot) + (slot == _session.CurrentSlot ? " ✓" : "") };
            mi.Click += (_, _) => { _session.CurrentSlot = slot; ShowToast(L.T("notify.slot", slot)); };
            menu.Items.Add(mi);
        }
        menu.ShowAt(SaveBtn);
    }

    private void ShowLoadMenu()
    {
        if (_session == null) return;
        var menu = new MenuFlyout();
        var quick = new MenuItem { Header = L.T("play.quickLoad", _session.CurrentSlot) };
        quick.Click += async (_, _) => await _session.QuickLoad();
        menu.Items.Add(quick);
        var states = _session.ListStates();
        if (states.Count > 0) menu.Items.Add(new Separator());
        foreach (var st in states)
        {
            var mi = new MenuItem { Header = $"{st.Name}  ·  {st.CreatedAt.ToLocalTime():g}" };
            mi.Click += async (_, _) => await _session.LoadState(st);
            menu.Items.Add(mi);
        }
        if (states.Count > 0)
        {
            menu.Items.Add(new Separator());
            var manage = new MenuItem { Header = L.T("states.title") + "…" };
            manage.Click += async (_, _) => await new SaveStatesWindow(_session).ShowDialog(this);
            menu.Items.Add(manage);
        }
        menu.ShowAt(LoadBtn);
    }

    private void ShowDiscMenu()
    {
        var info = _session?.Info; if (info == null) return;
        var menu = new MenuFlyout();
        for (uint i = 0; i < info.DiskImageCount; i++)
        {
            var idx = i;
            var mi = new MenuItem { Header = (i < info.DiskLabels.Count ? info.DiskLabels[(int)i] : $"{L.T("play.disc")} {i + 1}") + (i == _session!.DiskImageIndex ? " ✓" : "") };
            mi.Click += async (_, _) => await _session!.SetDiskImage(idx);
            menu.Items.Add(mi);
        }
        menu.ShowAt(DiscBtn);
    }

    private async Task ShowCheats()
    {
        if (_session == null) return;
        var wasPaused = _session.IsPaused; if (!wasPaused) _session.Pause();
        await new CheatsWindow(_game, _session).ShowDialog(this);
        if (!wasPaused) _session.Resume();
    }

    private async Task ShowCoreOptions()
    {
        if (_session?.Info == null) return;
        var wasPaused = _session.IsPaused; if (!wasPaused) _session.Pause();
        await new CoreOptionsWindow(_session, _opts.Core.Id).ShowDialog(this);
        if (!wasPaused) _session.Resume();
    }

    private void ToggleFullscreen()
    {
        if (WindowState == WindowState.FullScreen) WindowState = _preFullscreen;
        else { _preFullscreen = WindowState; WindowState = WindowState.FullScreen; }
    }

    private void ShowHud() { Hud.IsVisible = true; _hudTimer.Stop(); _hudTimer.Start(); }
    public void ShowToast(string text) { ToastText.Text = text; Toast.IsVisible = true; _toastTimer.Stop(); _toastTimer.Start(); }
}
