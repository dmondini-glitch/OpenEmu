using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using OpenEmu.Core.Config;
using OpenEmu.Core.Cores;
using OpenEmu.Core.Emulation;
using OpenEmu.Core.Libretro;
using OpenEmu.Core.Saves;
using OpenEmu.Core.Video;

namespace OpenEmu.Core.Remote;

/// <summary>
/// Runs a libretro core in a separate "core host" process (like OpenEmu's XPC game helpers). Video arrives through
/// shared memory, keyboard/pointer input goes out through shared memory, everything else over a named pipe.
/// Used on Windows ARM64 (x64/x86 cores under Windows' emulation) and optionally for process isolation elsewhere.
/// </summary>
public sealed class RemoteSession : IEmulator
{
    private readonly string _hostPath;
    private readonly string? _bindingsJson;
    private readonly string _id = Guid.NewGuid().ToString("N")[..12];
    private Process? _proc;
    private NamedPipeServerStream? _pipe;
    private StreamWriter? _writer;
    private SharedRegion? _frameRegion, _inputRegion;
    private readonly Dictionary<long, TaskCompletionSource<Response>> _pending = new();
    private long _nextId;
    private readonly object _writeLock = new();
    private Thread? _frameThread;
    private volatile bool _stopping;
    private int[] _scratch = Array.Empty<int>();
    private float _volume = 1f;
    private bool _muted;
    private bool _fastForward;
    private double _fps;

    public SessionOptions Options { get; }
    public FrameBuffer Frame { get; } = new();
    public SessionState State { get; private set; } = SessionState.Created;
    public EmulatorInfo? Info { get; private set; }
    public IReadOnlyList<CoreOptionSnapshot> CoreOptions { get; private set; } = new List<CoreOptionSnapshot>();
    public Exception? Error { get; private set; }
    public bool IsPaused => State == SessionState.Paused;
    public bool FastForward { get => _fastForward; set { _fastForward = value; _ = Send(Protocol.FastForward, new { value }); } }
    public int CurrentSlot { get; set; } = 1;
    public double MeasuredFps => _fps;
    public uint DiskImageIndex { get; private set; }
    public float Volume { get => _volume; set { _volume = value; _ = Send(Protocol.Volume, new { value }); } }
    public bool Muted { get => _muted; set { _muted = value; _ = Send(Protocol.Muted, new { value }); } }
    public SaveStateManager States { get; }
    public string HostPath => _hostPath;
    public int? HostProcessId => _proc?.Id;

    public event Action? FrameRendered;
    public event Action<SessionState>? StateChanged;
    public event Action<CoreMessage>? Message;
    public event Action<int, string>? Log;
    public event Action<string>? Notification;

    public RemoteSession(SessionOptions options, string hostPath, string? bindingsJson = null)
    {
        Options = options; _hostPath = hostPath; _bindingsJson = bindingsJson;
        States = new SaveStateManager(options.StatesDir);
    }

    /// <summary>Locates the core host executable for the given architecture ("x64", "x86", "arm64").</summary>
    public static string? FindHost(string arch)
    {
        var env = Environment.GetEnvironmentVariable("OPENEMU_COREHOST");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        var exe = OperatingSystem.IsWindows() ? "OpenEmu.CoreHost.exe" : "OpenEmu.CoreHost";
        foreach (var candidate in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "host", arch, exe),
            Path.Combine(AppContext.BaseDirectory, "host", arch, "OpenEmu.CoreHost.dll"),
            Path.Combine(AppContext.BaseDirectory, exe),
            Path.Combine(AppContext.BaseDirectory, "OpenEmu.CoreHost.dll"),
        })
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    public async Task StartAsync()
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "openemu-host");
            _frameRegion = new SharedRegion(Path.Combine(dir, $"frame-{_id}.bin"), SharedFrame.RegionSize, true);
            _inputRegion = new SharedRegion(Path.Combine(dir, $"input-{_id}.bin"), SharedInput.RegionSize, true);
            var pipeName = $"openemu-{_id}";
            _pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var psi = _hostPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? new ProcessStartInfo("dotnet") { ArgumentList = { _hostPath } }
                : new ProcessStartInfo(_hostPath);
            foreach (var a in new[] { "--pipe", pipeName, "--frame", _frameRegion.Path, "--input", _inputRegion.Path }) psi.ArgumentList.Add(a);
            psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardError = true;
            _proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start core host");
            _proc.EnableRaisingEvents = true;
            _proc.Exited += (_, _) => OnHostExited();
            _ = Task.Run(async () => { try { string? line; while ((line = await _proc.StandardError.ReadLineAsync()) != null) Log?.Invoke(Retro.LogWarn, "[host] " + line); } catch { } });
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                await _pipe.WaitForConnectionAsync(cts.Token);
            _writer = new StreamWriter(_pipe) { AutoFlush = true };
            _ = Task.Run(ReadLoop);
            var o = Options;
            var args = new StartArgs
            {
                SystemId = o.System.Id, CoreId = o.Core.Id, CoreLibraryPath = o.CoreLibraryPath, RomPath = o.RomPath, GameKey = o.GameKey, BiosDir = o.BiosDir, SavesDir = o.SavesDir,
                StatesDir = o.StatesDir, CoreOptions = o.CoreOptions, Language = o.Language, BindingsJson = _bindingsJson, Volume = _volume, Muted = _muted,
            };
            var resp = await Send(Protocol.Start, args, TimeSpan.FromSeconds(60));
            var result = resp.Result?.Deserialize<StartResult>(Protocol.Json) ?? throw new InvalidOperationException("Bad start result");
            Info = result.Info; CoreOptions = result.Options;
            SetState(SessionState.Running);
            _frameThread = new Thread(FrameLoop) { IsBackground = true, Name = "remote-frames" };
            _frameThread.Start();
        }
        catch (Exception ex)
        {
            Error = ex; SetState(SessionState.Failed);
            try { _proc?.Kill(); } catch { }
            throw;
        }
    }

    private unsafe void FrameLoop()
    {
        var lastSeq = 0;
        var p = _frameRegion!.Pointer;
        while (!_stopping)
        {
            if (SharedFrame.TryRead(p, ref lastSeq, ref _scratch, out var w, out var h, out var aspect, out var fps))
            {
                fixed (int* src = _scratch) Frame.Update((IntPtr)src, (uint)w, (uint)h, (nuint)(w * 4), Retro.PixelFormatXrgb8888);
                Frame.AspectRatio = aspect; _fps = fps;
                FrameRendered?.Invoke();
            }
            else Thread.Sleep(1);
        }
    }

    private async Task ReadLoop()
    {
        try
        {
            using var reader = new StreamReader(_pipe!);
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (line.Length == 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("evt", out var evt)) HandleEvent(evt.GetString()!, doc.RootElement.TryGetProperty("data", out var d) ? d.Clone() : (JsonElement?)null);
                    else
                    {
                        var resp = doc.RootElement.Deserialize<Response>(Protocol.Json)!;
                        TaskCompletionSource<Response>? tcs;
                        lock (_pending) _pending.Remove(resp.Id, out tcs);
                        tcs?.TrySetResult(resp);
                    }
                }
                catch (Exception ex) { Log?.Invoke(Retro.LogWarn, "bad host message: " + ex.Message); }
            }
        }
        catch { }
        FailPending("Core host disconnected");
    }

    private void HandleEvent(string evt, JsonElement? data)
    {
        if (evt != Protocol.EvtFps) Log?.Invoke(Retro.LogDebug, $"← {evt} {data}");
        switch (evt)
        {
            case Protocol.EvtState: SetState((SessionState)(data?.GetInt32() ?? 0)); break;
            case Protocol.EvtMessage: Message?.Invoke(new CoreMessage(data?.GetProperty("text").GetString() ?? "", (uint)(data?.GetProperty("frames").GetInt32() ?? 60))); break;
            case Protocol.EvtLog: Log?.Invoke(data?.GetProperty("level").GetInt32() ?? 1, data?.GetProperty("text").GetString() ?? ""); break;
            case Protocol.EvtNotification: Notification?.Invoke(data?.GetString() ?? ""); break;
            case Protocol.EvtFps: _fps = data?.GetDouble() ?? 0; break;
            case Protocol.EvtDisc: DiskImageIndex = (uint)(data?.GetInt32() ?? 0); break;
        }
    }

    private void SetState(SessionState s) { if (State == s) return; State = s; StateChanged?.Invoke(s); }

    private void OnHostExited()
    {
        Log?.Invoke(Retro.LogDebug, $"host exited (code {_proc?.ExitCode}, state {State}, stopping {_stopping})");
        FailPending("Core host exited");
        if (State is SessionState.Running or SessionState.Paused) { Error ??= new InvalidOperationException($"Core host exited with code {_proc?.ExitCode}"); SetState(_stopping ? SessionState.Stopped : SessionState.Failed); }
        else if (State != SessionState.Failed) SetState(SessionState.Stopped);
    }

    private void FailPending(string reason)
    {
        List<TaskCompletionSource<Response>> all;
        lock (_pending) { all = _pending.Values.ToList(); _pending.Clear(); }
        foreach (var t in all) t.TrySetResult(new Response { Ok = false, Error = reason });
    }

    private async Task<Response> Send(string cmd, object? args = null, TimeSpan? timeout = null)
    {
        if (_writer == null) return new Response { Ok = false, Error = "not connected" };
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[id] = tcs;
        var json = JsonSerializer.Serialize(new { id, cmd, args }, Protocol.Json);
        Log?.Invoke(Retro.LogDebug, "→ " + (json.Length > 300 ? json[..300] + "…" : json));
        try { lock (_writeLock) _writer.WriteLine(json); }
        catch (Exception ex) { lock (_pending) _pending.Remove(id); return new Response { Ok = false, Error = ex.Message }; }
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout ?? TimeSpan.FromSeconds(15)));
        if (done != tcs.Task) { lock (_pending) _pending.Remove(id); return new Response { Ok = false, Error = $"{cmd}: timeout" }; }
        var r = await tcs.Task;
        if (!r.Ok && r.Error != null) Log?.Invoke(Retro.LogWarn, $"host {cmd}: {r.Error}");
        return r;
    }

    // ------------------------------------------------------------------ IEmulator
    public void Pause() => _ = Send(Protocol.Pause);
    public void Resume() => _ = Send(Protocol.Resume);
    public void Stop()
    {
        if (_stopping) return;
        _stopping = true;
        try { Send(Protocol.Stop).Wait(TimeSpan.FromSeconds(10)); } catch { }
        try { if (_proc != null && !_proc.WaitForExit(8000)) _proc.Kill(); } catch { }
        if (State != SessionState.Failed) SetState(SessionState.Stopped);
    }
    public Task Reset() => Send(Protocol.Reset);

    public async Task<SaveStateInfo?> SaveState(string name, int? slot = null)
    {
        var r = await Send(Protocol.SaveState, new { name, slot }, TimeSpan.FromSeconds(60));
        return r.Ok && r.Result.HasValue && r.Result.Value.ValueKind == JsonValueKind.Object ? r.Result.Value.Deserialize<SaveStateInfo>(Protocol.Json) : null;
    }
    public async Task<bool> LoadState(SaveStateInfo state) { var r = await Send(Protocol.LoadState, new { path = state.Path }, TimeSpan.FromSeconds(60)); return r.Ok && r.Result?.GetBoolean() == true; }
    public Task<SaveStateInfo?> QuickSave() => SaveState($"Quick Save {CurrentSlot}", CurrentSlot);
    public async Task<bool> QuickLoad()
    {
        var s = States.FindSlot(Options.System.Id, Options.GameKey, CurrentSlot);
        if (s == null) { Notification?.Invoke($"Slot {CurrentSlot} is empty"); return false; }
        return await LoadState(s);
    }
    public Task<SaveStateInfo?> AutoSave() => SaveState(SaveStateManager.AutoSaveName);
    public SaveStateInfo? FindAutoSave() => States.FindAutoSave(Options.System.Id, Options.GameKey);
    public IReadOnlyList<SaveStateInfo> ListStates() => States.List(Options.System.Id, Options.GameKey);
    public byte[] Screenshot() => Frame.ToPng();
    public string SaveScreenshot(string screenshotsDir, string title)
    {
        Directory.CreateDirectory(screenshotsDir);
        var path = Path.Combine(screenshotsDir, $"{Paths.SafeFileName(title)} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png");
        File.WriteAllBytes(path, Screenshot());
        Notification?.Invoke("Screenshot saved");
        return path;
    }
    public async Task SetOption(string key, string value)
    {
        await Send(Protocol.SetOption, new { key, value });
        var r = await Send(Protocol.GetOptions);
        if (r.Ok && r.Result.HasValue) CoreOptions = r.Result.Value.Deserialize<List<CoreOptionSnapshot>>(Protocol.Json) ?? CoreOptions;
    }
    public Task ApplyCheats(IEnumerable<(bool Enabled, string Code)> cheats) => Send(Protocol.Cheats, new { cheats = cheats.Select(c => new { enabled = c.Enabled, code = c.Code }).ToList() });
    public Task SetDiskImage(uint index) => Send(Protocol.Disc, new { index });
    public Task SendKeyboardEvent(bool down, uint retroKey, uint character, ushort modifiers) => Send(Protocol.Key, new { down, retroKey, character, modifiers });
    public unsafe void SetKey(int hidUsage, bool down) { var r = _inputRegion; if (r != null && r.Pointer != null) SharedInput.SetKey(r.Pointer, hidUsage, down); }
    public unsafe void ClearKeys() { var r = _inputRegion; if (r != null && r.Pointer != null) SharedInput.ClearKeys(r.Pointer); }
    public unsafe void SetPointer(int x, int y, bool pressed) { var r = _inputRegion; if (r != null && r.Pointer != null) SharedInput.SetPointer(r.Pointer, x, y, pressed); }

    public void Dispose()
    {
        Stop();
        _stopping = true;
        try { _writer?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        var fr = _frameRegion; var ir = _inputRegion;
        _frameRegion = null; _inputRegion = null;
        fr?.Dispose(); ir?.Dispose();
        foreach (var f in new[] { fr?.Path, ir?.Path }) if (f != null) { try { File.Delete(f); } catch { } }
    }
}
