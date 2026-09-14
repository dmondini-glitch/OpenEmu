using System.Diagnostics;
using OpenEmu.Core.Audio;
using OpenEmu.Core.Config;
using OpenEmu.Core.Cores;
using OpenEmu.Core.Input;
using OpenEmu.Core.Libretro;
using OpenEmu.Core.Saves;
using OpenEmu.Core.Systems;
using OpenEmu.Core.Video;

namespace OpenEmu.Core.Emulation;

public enum SessionState { Created, Running, Paused, Stopped, Failed }

/// <summary>Options for starting a game.</summary>
public sealed class SessionOptions
{
    public required SystemDefinition System { get; init; }
    public required CoreDefinition Core { get; init; }
    public required string CoreLibraryPath { get; init; }
    public string? RomPath { get; init; }
    /// <summary>Stable key used for save/state folders (usually the game's hash or title).</summary>
    public required string GameKey { get; init; }
    public string BiosDir { get; init; } = Paths.BiosDir;
    public string SavesDir { get; init; } = Paths.SavesDir;
    public string StatesDir { get; init; } = Paths.StatesDir;
    public Dictionary<string, string> CoreOptions { get; init; } = new();
    public uint Language { get; init; } = Retro.LanguageEnglish;
    public bool ThrottleToVsync { get; init; } = true;
}

/// <summary>
/// Runs one libretro core on a dedicated thread with frame pacing, audio, input, battery saves, states and cheats.
/// All core calls are marshalled onto the emulation thread via <see cref="Invoke"/>.
/// </summary>
public sealed class EmulationSession : IEmulator
{
    private readonly SessionOptions _opt;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _resume = new(true);
    private readonly Queue<(Action Action, TaskCompletionSource Tcs)> _queue = new();
    private readonly object _queueLock = new();
    private volatile bool _stop;
    private volatile bool _paused;
    private volatile bool _fastForward;
    private LibretroCore? _core;
    private readonly BatterySaveManager _battery;
    private readonly SaveStateManager _states;
    private byte[]? _lastSram;
    private long _frames;
    private readonly Stopwatch _clock = new();
    private double _fpsMeasured;
    private readonly List<(bool Enabled, string Code)> _cheats = new();
    private IHwRenderThreadHost? _threadHost;
    /// <summary>True when the emulation thread owns the GL context (Win32/WGL path).</summary>
    public bool HwRenderOnEmulationThread => _threadHost != null;

    public FrameBuffer Frame { get; } = new();
    public IAudioSink Audio { get; }
    public InputManager Input { get; }
    public SessionState State { get; private set; } = SessionState.Created;
    public Exception? Error { get; private set; }
    public LibretroCore? Core => _core;
    public SessionOptions Options => _opt;
    public long FrameCount => Interlocked.Read(ref _frames);
    public double MeasuredFps => _fpsMeasured;
    public bool IsPaused => _paused;
    public bool FastForward { get => _fastForward; set { _fastForward = value; if (_core != null) _core.FastForwarding = value; } }
    public double Fps => _core?.AvInfo.timing.fps is > 0 and var f ? f : 60.0;
    public double SampleRate => _core?.AvInfo.timing.sample_rate is > 0 and var s ? s : 48000.0;
    public int CurrentSlot { get; set; } = 1;
    public IHwRenderHost? HwRenderHost { get; set; }
    public EmulatorInfo? Info { get; private set; }
    public IReadOnlyList<CoreOptionSnapshot> CoreOptions => _core?.Options.Values.Select(CoreOptionSnapshot.From).ToList() ?? new List<CoreOptionSnapshot>();
    public uint DiskImageIndex => _core?.DiskImageIndex ?? 0;
    public float Volume { get => Audio.Volume; set => Audio.Volume = value; }
    public bool Muted { get => Audio.Muted; set => Audio.Muted = value; }
    public void SetKey(int hidUsage, bool down) => Input.Keyboard.Set(hidUsage, down);
    public void ClearKeys() => Input.Keyboard.Clear();
    public Task SendKeyboardEvent(bool down, uint retroKey, uint character, ushort modifiers) => Invoke(() => _core?.SendKeyboardEvent(down, retroKey, character, modifiers));
    /// <summary>Set when the core needs a GL context; the UI must then drive frames via <see cref="RunFrameOnCallerThread"/>.</summary>
    public bool RequiresHwRender { get; private set; }

    public event Action? FrameRendered;
    public event Action<SessionState>? StateChanged;
    public event Action<CoreMessage>? Message;
    public event Action<int, string>? Log;
    public event Action<string>? Notification;

    public EmulationSession(SessionOptions options, IAudioSink? audio = null, InputManager? input = null)
    {
        _opt = options;
        Audio = audio ?? AudioSinkFactory.Create();
        Input = input ?? new InputManager();
        _battery = new BatterySaveManager(options.SavesDir);
        _states = new SaveStateManager(options.StatesDir);
        _thread = new Thread(ThreadMain) { Name = "libretro:" + options.Core.Id, IsBackground = true };
    }

    // ------------------------------------------------------------------ lifecycle
    public void Start()
    {
        if (State != SessionState.Created) throw new InvalidOperationException();
        _thread.Start();
    }

    /// <summary>Starts the core synchronously and reports whether the game loaded (throws on failure).</summary>
    public async Task StartAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _startedTcs = tcs;
        Start();
        await tcs.Task;
    }
    private TaskCompletionSource? _startedTcs;

    public void Pause() { _paused = true; _resume.Reset(); SetState(SessionState.Paused); }
    public void Resume() { _paused = false; _resume.Set(); Audio.Clear(); SetState(SessionState.Running); }
    public void TogglePause() { if (_paused) Resume(); else Pause(); }

    public void Stop()
    {
        if (_stop) return;
        _stop = true;
        _resume.Set();
        if (Thread.CurrentThread != _thread && _thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(10));
    }

    /// <summary>For hardware-rendered cores: marshals core calls onto the GL/UI thread (set by the game window).</summary>
    public Action<Action>? HwThreadInvoker { get; set; }

    public Task Invoke(Action action)
    {
        if (Thread.CurrentThread == _thread) { action(); return Task.CompletedTask; }
        if (RequiresHwRender && HwThreadInvoker != null)
        {
            var htcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            HwThreadInvoker(() => { try { action(); htcs.TrySetResult(); } catch (Exception ex) { htcs.TrySetException(ex); } });
            return htcs.Task;
        }
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_queueLock) _queue.Enqueue((action, tcs));
        _resume.Set();
        return tcs.Task;
    }

    public Task<T> Invoke<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Invoke(() => { try { tcs.SetResult(func()); } catch (Exception ex) { tcs.SetException(ex); } });
        return tcs.Task;
    }

    private void SetState(SessionState s) { State = s; StateChanged?.Invoke(s); }

    // ------------------------------------------------------------------ thread
    private void ThreadMain()
    {
        try
        {
            InitCore();
            _startedTcs?.TrySetResult();
            SetState(SessionState.Running);
            Loop();
        }
        catch (Exception ex)
        {
            Error = ex;
            Log?.Invoke(Retro.LogError, ex.ToString());
            _startedTcs?.TrySetException(ex);
            SetState(SessionState.Failed);
        }
        finally
        {
            try { FlushBattery(); } catch { }
            if (_threadHost != null)
            {
                try { _core?.FireHwContextDestroy(); } catch { }
                try { _core?.UnloadGame(); _core?.Deinit(); } catch { }
                try { _threadHost.Teardown(); } catch { }
                _threadHost = null;
            }
            try { _core?.Dispose(); } catch { }
            _core = null;
            try { Audio.Dispose(); } catch { }
            if (State != SessionState.Failed) SetState(SessionState.Stopped);
        }
    }

    private void InitCore()
    {
        var core = new LibretroCore(_opt.CoreLibraryPath)
        {
            SystemDirectory = _opt.BiosDir,
            SaveDirectory = Path.Combine(_opt.SavesDir, Paths.SafeFileName(_opt.System.Id)),
            CoreAssetsDirectory = _opt.BiosDir,
            Language = _opt.Language,
            InputSource = Input,
            HwRenderHost = HwRenderHost,
        };
        Directory.CreateDirectory(core.SaveDirectory);
        Directory.CreateDirectory(core.SystemDirectory);
        foreach (var kv in _opt.CoreOptions) core.OptionOverrides[kv.Key] = kv.Value;
        core.Log += (lvl, msg) => Log?.Invoke(lvl, msg);
        core.Message += m => Message?.Invoke(m);
        core.VideoFrame += (data, w, h, pitch, fmt) => { Frame.Update(data, w, h, pitch, fmt); Frame.AspectRatio = core.AvInfo.geometry.aspect_ratio; };
        core.VideoFrameDuplicated += () => { };
        core.AudioSamples += OnAudio;
        core.Rumble += (port, effect, strength) => Input.Rumble(port, effect, strength);
        core.AvInfoChanged += () => Audio.Configure((int)Math.Round(core.AvInfo.timing.sample_rate));
        _core = core;
        Input.SetSystem(_opt.System);
        Input.KeyboardPassthrough = _opt.System.IsComputer && core.HasKeyboardCallback;
        core.Init();
        if (!core.LoadGame(_opt.RomPath))
            throw new InvalidOperationException($"{core.LibraryName} could not load {_opt.RomPath}");
        RequiresHwRender = core.UsesHwRender;
        if (core.UsesHwRender && HwRenderHost is IHwRenderThreadHost th)
        {
            th.Prepare(core);          // creates the GL context + FBO on this (emulation) thread
            core.FireHwContextReset(); // the core may now create its GL resources
            _threadHost = th;
        }
        var labels = new List<string>();
        if (core.HasDiskControl) for (uint i = 0; i < core.DiskImageCount; i++) labels.Add(core.GetDiskImageLabel(i) ?? $"Disc {i + 1}");
        Info = new EmulatorInfo(core.LibraryName, core.LibraryVersion, core.AvInfo.timing.fps, core.AvInfo.timing.sample_rate, core.AvInfo.geometry.base_width, core.AvInfo.geometry.base_height,
            core.AvInfo.geometry.aspect_ratio, core.Rotation, core.HasDiskControl, core.DiskImageCount, labels, core.HasKeyboardCallback, core.UsesHwRender, false);
        Audio.Configure((int)Math.Round(core.AvInfo.timing.sample_rate));
        Audio.Volume = 1f;
        // restore battery save
        var sram = _battery.Load(_opt.System.Id, _opt.GameKey);
        if (sram != null && core.WriteMemory(Retro.MemorySaveRam, sram)) _lastSram = sram;
        var rtc = _battery.Load(_opt.System.Id, _opt.GameKey, ".rtc");
        if (rtc != null) core.WriteMemory(Retro.MemoryRtc, rtc);
        for (uint p = 0; p < 4; p++) TrySetPort(core, p);
        _clock.Start();
    }

    private static void TrySetPort(LibretroCore core, uint port)
    {
        try
        {
            if (core.ControllerInfo.Count > (int)port)
            {
                var types = core.ControllerInfo[(int)port];
                var analog = types.FirstOrDefault(t => (t.Id & 0xFF) == Retro.DeviceAnalog);
                core.SetControllerPortDevice(port, analog?.Id ?? Retro.DeviceJoypad);
            }
            else core.SetControllerPortDevice(port, Retro.DeviceJoypad);
        }
        catch { }
    }

    private void OnAudio(ReadOnlySpan<short> samples)
    {
        if (_fastForward && (_frames & 3) != 0) return; // drop most audio while fast-forwarding
        Audio.Write(samples);
    }

    private void Loop()
    {
        var core = _core!;
        var next = Stopwatch.GetTimestamp();
        var fpsWindowStart = next; long fpsWindowFrames = 0;
        while (!_stop)
        {
            DrainQueue();
            if (_paused)
            {
                _resume.Wait(100);
                DrainQueue();
                next = Stopwatch.GetTimestamp();
                continue;
            }
            if (RequiresHwRender && HwRenderHost != null && _threadHost == null)
            {
                // GL cores hosted by the UI (Avalonia OpenGlControlBase, non-Windows) are stepped by the UI thread; this thread only serves the queue.
                _resume.Wait(20); _resume.Reset(); continue;
            }
            RunOneFrame(core);
            if (core.ShutdownRequested) { Notification?.Invoke("Core requested shutdown"); break; }
            var period = (long)(Stopwatch.Frequency / Fps);
            if (_fastForward) { next = Stopwatch.GetTimestamp(); }
            else
            {
                next += period;
                var now = Stopwatch.GetTimestamp();
                if (next < now - period * 4) next = now; // fell too far behind: don't try to catch up
                while ((now = Stopwatch.GetTimestamp()) < next)
                {
                    var remainingMs = (next - now) * 1000.0 / Stopwatch.Frequency;
                    if (remainingMs > 2) Thread.Sleep(1); else Thread.SpinWait(50);
                }
            }
            fpsWindowFrames++;
            var elapsed = Stopwatch.GetTimestamp() - fpsWindowStart;
            if (elapsed > Stopwatch.Frequency)
            {
                _fpsMeasured = fpsWindowFrames * (double)Stopwatch.Frequency / elapsed;
                fpsWindowFrames = 0; fpsWindowStart = Stopwatch.GetTimestamp();
                FlushBattery();
            }
        }
    }

    private void RunOneFrame(LibretroCore core)
    {
        core.Run();
        Interlocked.Increment(ref _frames);
        if (_threadHost != null)
        {
            var g = core.AvInfo.geometry;
            try { _threadHost.Present(Frame, g.base_width, g.base_height, g.aspect_ratio); }
            catch (Exception ex) { Log?.Invoke(Retro.LogError, "present failed: " + ex.Message); }
        }
        FrameRendered?.Invoke();
    }

    /// <summary>For GL cores hosted on the emulation thread: reads the current GL frame into <see cref="Frame"/> (call via Invoke).</summary>
    public void CaptureHwFrame()
    {
        var core = _core;
        if (core == null || _threadHost == null) return;
        var g = core.AvInfo.geometry;
        try { _threadHost.Capture(Frame, g.base_width, g.base_height); } catch (Exception ex) { Log?.Invoke(Retro.LogWarn, "capture failed: " + ex.Message); }
    }

    /// <summary>For hardware-rendered cores: run one frame on the calling (GL) thread.</summary>
    public void RunFrameOnCallerThread()
    {
        if (_core == null || _paused || _stop || State != SessionState.Running) return;
        RunOneFrame(_core);
        if ((_frames % 120) == 0) { try { FlushBattery(); } catch { } }
        if (_core.ShutdownRequested) { Notification?.Invoke("Core requested shutdown"); Stop(); }
    }

    private void DrainQueue()
    {
        while (true)
        {
            (Action Action, TaskCompletionSource Tcs) item;
            lock (_queueLock) { if (_queue.Count == 0) return; item = _queue.Dequeue(); }
            try { item.Action(); item.Tcs.TrySetResult(); } catch (Exception ex) { item.Tcs.TrySetException(ex); }
        }
    }

    // ------------------------------------------------------------------ features
    public void FlushBattery()
    {
        var core = _core; if (core == null) return;
        var sram = core.ReadMemory(Retro.MemorySaveRam);
        if (sram != null && (_lastSram == null || !sram.AsSpan().SequenceEqual(_lastSram)))
        {
            _battery.Save(_opt.System.Id, _opt.GameKey, sram);
            _lastSram = sram;
        }
        var rtc = core.ReadMemory(Retro.MemoryRtc);
        if (rtc != null) _battery.Save(_opt.System.Id, _opt.GameKey, rtc, ".rtc");
    }

    public Task Reset() => Invoke(() => _core?.Reset());

    public Task<SaveStateInfo?> SaveState(string name, int? slot = null) => Invoke<SaveStateInfo?>(() =>
    {
        var core = _core; if (core == null) return null;
        var data = core.Serialize();
        if (data == null) { Notification?.Invoke("This core does not support save states"); return null; }
        CaptureHwFrame();
        var png = Frame.Width > 0 && !Frame.IsHardwareFrame ? Frame.ToPng() : null;
        var info = _states.Write(_opt.System.Id, _opt.GameKey, name, data, png, _opt.Core.Id, slot);
        Notification?.Invoke(slot.HasValue ? $"Saved to slot {slot}" : $"Saved \"{name}\"");
        return info;
    });

    public Task<bool> LoadState(SaveStateInfo state) => Invoke(() =>
    {
        var core = _core; if (core == null) return false;
        var data = File.ReadAllBytes(state.Path);
        var ok = core.Unserialize(data);
        Notification?.Invoke(ok ? $"Loaded \"{state.Name}\"" : "Failed to load save state");
        Audio.Clear();
        return ok;
    });

    public Task<SaveStateInfo?> QuickSave() => SaveState($"Quick Save {CurrentSlot}", CurrentSlot);
    public async Task<bool> QuickLoad()
    {
        var s = _states.FindSlot(_opt.System.Id, _opt.GameKey, CurrentSlot);
        if (s == null) { Notification?.Invoke($"Slot {CurrentSlot} is empty"); return false; }
        return await LoadState(s);
    }
    public Task<SaveStateInfo?> AutoSave() => SaveState(SaveStateManager.AutoSaveName);
    public SaveStateInfo? FindAutoSave() => _states.FindAutoSave(_opt.System.Id, _opt.GameKey);
    public IReadOnlyList<SaveStateInfo> ListStates() => _states.List(_opt.System.Id, _opt.GameKey);
    public SaveStateManager States => _states;

    public byte[] Screenshot()
    {
        if (_threadHost != null) { try { Invoke(CaptureHwFrame).Wait(2000); } catch { } }
        return Frame.ToPng();
    }

    public string SaveScreenshot(string screenshotsDir, string title)
    {
        Directory.CreateDirectory(screenshotsDir);
        var path = Path.Combine(screenshotsDir, $"{Paths.SafeFileName(title)} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png");
        File.WriteAllBytes(path, Screenshot());
        Notification?.Invoke("Screenshot saved");
        return path;
    }

    public Task SetOption(string key, string value) => Invoke(() => _core?.SetOption(key, value));

    public Task ApplyCheats(IEnumerable<(bool Enabled, string Code)> cheats) => Invoke(() =>
    {
        var core = _core; if (core == null) return;
        _cheats.Clear(); _cheats.AddRange(cheats);
        core.CheatReset();
        uint i = 0;
        foreach (var (enabled, code) in _cheats) core.CheatSet(i++, enabled, code);
    });

    public Task SetDiskImage(uint index) => Invoke(() =>
    {
        var core = _core; if (core == null || !core.HasDiskControl) return;
        core.SetDiskEjected(true); core.SetDiskImageIndex(index); core.SetDiskEjected(false);
        Notification?.Invoke($"Disc {index + 1} inserted");
    });

    public void Dispose() { Stop(); _resume.Dispose(); }
}
