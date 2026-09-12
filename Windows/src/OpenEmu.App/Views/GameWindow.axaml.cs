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
using OpenEmu.Core.Localization;
using OpenEmu.Core.Saves;
using OpenEmu.Core.Systems;

namespace OpenEmu.App.Views;

public partial class GameWindow : Window
{
    private readonly Game _game;
    private readonly SessionOptions _opts;
    private readonly AppServices _s = App.Services;
    private EmulationSession? _session;
    private GameView? _swView;
    private GlGameView? _glView;
    private readonly DispatcherTimer _hudTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _fpsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private WindowState _preFullscreen = WindowState.Normal;
    private bool _closing;
    private readonly HashSet<int> _heldHotkeys = new();

    public EmulationSession? Session => _session;

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
        Deactivated += (_, _) => { _s.Input.Keyboard.Clear(); if (_s.Settings.Gameplay.BackgroundPause && _session is { State: SessionState.Running }) _session.Pause(); };
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
        MuteBtn.Click += (_, _) => { if (_session == null) return; _session.Audio.Muted = !_session.Audio.Muted; _s.Settings.Audio.Muted = _session.Audio.Muted; MuteBtn.Content = _session.Audio.Muted ? "🔇" : "🔊"; };
        VolumeSlider.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty && _session != null) { _session.Audio.Volume = (float)VolumeSlider.Value; _s.Settings.Audio.Volume = (float)VolumeSlider.Value; } };
        FullBtn.Click += (_, _) => ToggleFullscreen();
    }

    private async Task StartAsync()
    {
        try
        {
            _session = new EmulationSession(_opts, input: _s.Input);
            _session.Notification += msg => Dispatcher.UIThread.Post(() => ShowToast(msg));
            _session.Message += m => { if (_s.Settings.Gameplay.ShowCoreMessages) Dispatcher.UIThread.Post(() => ShowToast(m.Text)); };
            _session.Log += (lvl, msg) => { if (lvl >= Retro.LogWarn) _s.Log.Warn($"[{_opts.Core.Id}] {msg}"); };
            _session.StateChanged += st => Dispatcher.UIThread.Post(() => OnSessionState(st));
            _session.HwThreadInvoker = a => { if (Dispatcher.UIThread.CheckAccess()) a(); else Dispatcher.UIThread.Post(a); };
            if (_opts.Core.HwRender)
            {
                _glView = new GlGameView { Session = _session, Video = _s.Settings.Video };
                _glView.Error += e => _s.Log.Error("GL: " + e);
                _session.HwRenderHost = _glView;
            }
            await _session.StartAsync();
            if (_session.RequiresHwRender)
            {
                if (_glView == null) { _glView = new GlGameView { Session = _session, Video = _s.Settings.Video }; }
                ViewHost.Content = _glView;
                _glView.RequestNextFrameRendering();
            }
            else
            {
                _swView = new GameView { Frame = _session.Frame, Video = _s.Settings.Video, Rotation = _session.Core!.Rotation };
                _session.FrameRendered += () => _swView.RequestRedraw();
                ViewHost.Content = _swView;
            }
            _session.Audio.Volume = _s.Settings.Audio.Volume;
            _session.Audio.Muted = _s.Settings.Audio.Muted;
            MuteBtn.Content = _session.Audio.Muted ? "🔇" : "🔊";
            DiscBtn.IsVisible = _session.Core!.HasDiskControl && _session.Core.DiskImageCount > 1;
            if (_s.Settings.Video.ShowFps) _fpsTimer.Start();
            await _session.ApplyCheats(_s.Library.CheatsFor(_game.Id).Where(c => c.Enabled).Select(c => (true, c.Code)));
            if (_s.Settings.Gameplay.LoadAutoSaveOnStart && _session.FindAutoSave() is { } auto) await _session.LoadState(auto);
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
        if (hid != 0) { _s.Input.Keyboard.Set(hid, true); SendRetroKey(hid, true, e); e.Handled = true; }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        var hid = KeyMap.ToHid(e.PhysicalKey);
        if (hid != 0 && _heldHotkeys.Remove(hid)) { HandleHotkey(hid, false); e.Handled = true; return; }
        if (hid != 0) { _s.Input.Keyboard.Set(hid, false); SendRetroKey(hid, false, e); e.Handled = true; }
    }

    private void SendRetroKey(int hid, bool down, KeyEventArgs e)
    {
        var core = _session?.Core;
        if (core == null || !core.HasKeyboardCallback) return;
        var mods = (ushort)(((e.KeyModifiers & KeyModifiers.Shift) != 0 ? 1 : 0) | ((e.KeyModifiers & KeyModifiers.Control) != 0 ? 2 : 0) | ((e.KeyModifiers & KeyModifiers.Alt) != 0 ? 4 : 0));
        var rk = HidKeys.ToRetroKey(hid);
        if (rk != 0) _session!.Invoke(() => core.SendKeyboardEvent(down, rk, rk < 128 ? rk : 0, mods));
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
        var core = _session?.Core; if (core == null) return;
        var menu = new MenuFlyout();
        for (uint i = 0; i < core.DiskImageCount; i++)
        {
            var idx = i;
            var mi = new MenuItem { Header = (core.GetDiskImageLabel(i) ?? $"{L.T("play.disc")} {i + 1}") + (i == core.DiskImageIndex ? " ✓" : "") };
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
        if (_session?.Core == null) return;
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
