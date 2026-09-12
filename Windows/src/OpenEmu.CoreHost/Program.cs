// OpenEmu core host: runs one libretro core out-of-process (video via shared memory, control via named pipe).
// On Windows ARM64 this executable is the x64/x86 build running under Windows' emulation layer.
using System.IO.Pipes;
using System.Text.Json;
using OpenEmu.Core.Audio;
using OpenEmu.Core.Cores;
using OpenEmu.Core.Emulation;
using OpenEmu.Core.Input;
using OpenEmu.Core.Libretro;
using OpenEmu.Core.Remote;
using OpenEmu.Core.Systems;

string? Opt(string name) { var i = Array.IndexOf(args, "--" + name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
var pipeName = Opt("pipe") ?? throw new ArgumentException("--pipe required");
var framePath = Opt("frame") ?? throw new ArgumentException("--frame required");
var inputPath = Opt("input") ?? throw new ArgumentException("--input required");

using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
await pipe.ConnectAsync(20000);
var writer = new StreamWriter(pipe) { AutoFlush = true };
var reader = new StreamReader(pipe);
var writeLock = new object();
void SendLine(object o) { var json = JsonSerializer.Serialize(o, Protocol.Json); lock (writeLock) { try { writer.WriteLine(json); } catch { } } }
void Emit(string evt, object? data) => SendLine(new { evt, data });

unsafe
{
    using var frameRegion = new SharedRegion(framePath, SharedFrame.RegionSize, false);
    using var inputRegion = new SharedRegion(inputPath, SharedInput.RegionSize, false);
    SharedFrame.Init(frameRegion.Pointer);
    EmulationSession? session = null;
    var stopRequested = false;
    var lastFps = DateTime.UtcNow;

    object? Handle(Request req)
    {
        var a = req.Args;
        switch (req.Cmd)
        {
            case Protocol.Ping: return "pong";
            case Protocol.Start:
            {
                var sa = a!.Value.Deserialize<StartArgs>(Protocol.Json)!;
                var system = SystemCatalog.Find(sa.SystemId) ?? throw new ArgumentException("unknown system " + sa.SystemId);
                var core = CoreManifest.Find(sa.CoreId) ?? new CoreDefinition { Id = sa.CoreId };
                var opts = new SessionOptions { System = system, Core = core, CoreLibraryPath = sa.CoreLibraryPath, RomPath = sa.RomPath, GameKey = sa.GameKey, BiosDir = sa.BiosDir, SavesDir = sa.SavesDir, StatesDir = sa.StatesDir, CoreOptions = sa.CoreOptions, Language = sa.Language };
                var input = new InputManager();
                if (!string.IsNullOrEmpty(sa.BindingsJson)) { try { input.UserBindings = JsonSerializer.Deserialize<BindingSet>(sa.BindingsJson, OpenEmu.Core.Config.AppSettings.JsonOptions) ?? new(); } catch { } }
                var hostInput = new HostInput(input, inputRegion.Pointer);
                session = new EmulationSession(opts, AudioSinkFactory.Create(), input);
                session.Core?.ToString();
                session.StateChanged += st => Emit(Protocol.EvtState, (int)st);
                session.Message += m => Emit(Protocol.EvtMessage, new { text = m.Text, frames = (int)m.Frames });
                session.Log += (lvl, msg) => { if (lvl >= Retro.LogInfo) Emit(Protocol.EvtLog, new { level = lvl, text = msg }); };
                session.Notification += n => Emit(Protocol.EvtNotification, n);
                session.FrameRendered += () =>
                {
                    var f = session!.Frame;
                    lock (f.SyncRoot) SharedFrame.Write(frameRegion.Pointer, f.Pixels, f.Width, f.Height, f.AspectRatio, session.FrameCount, session.MeasuredFps);
                    if ((DateTime.UtcNow - lastFps).TotalSeconds >= 1) { lastFps = DateTime.UtcNow; Emit(Protocol.EvtFps, session.MeasuredFps); }
                };
                session.StartAsync().GetAwaiter().GetResult();
                // route input through the shared keyboard region
                session.Core!.InputSource = hostInput;
                session.Volume = sa.Volume; session.Muted = sa.Muted;
                return new StartResult { Info = session.Info with { IsRemote = true }, Options = session.CoreOptions.ToList() };
            }
            case Protocol.Pause: session?.Pause(); return true;
            case Protocol.Resume: session?.Resume(); return true;
            case Protocol.Reset: session?.Reset().Wait(); return true;
            case Protocol.Stop: stopRequested = true; session?.Stop(); return true;
            case Protocol.FastForward: if (session != null) session.FastForward = a!.Value.GetProperty("value").GetBoolean(); return true;
            case Protocol.Volume: if (session != null) session.Volume = a!.Value.GetProperty("value").GetSingle(); return true;
            case Protocol.Muted: if (session != null) session.Muted = a!.Value.GetProperty("value").GetBoolean(); return true;
            case Protocol.SetOption: session?.SetOption(a!.Value.GetProperty("key").GetString()!, a.Value.GetProperty("value").GetString()!).Wait(); return true;
            case Protocol.GetOptions: return session?.CoreOptions.ToList() ?? new List<CoreOptionSnapshot>();
            case Protocol.Cheats:
            {
                var list = a!.Value.GetProperty("cheats").EnumerateArray().Select(c => (c.GetProperty("enabled").GetBoolean(), c.GetProperty("code").GetString()!)).ToList();
                session?.ApplyCheats(list).Wait(); return true;
            }
            case Protocol.Disc: { var idx = a!.Value.GetProperty("index").GetUInt32(); session?.SetDiskImage(idx).Wait(); Emit(Protocol.EvtDisc, (int)idx); return true; }
            case Protocol.Key:
                session?.SendKeyboardEvent(a!.Value.GetProperty("down").GetBoolean(), a.Value.GetProperty("retroKey").GetUInt32(), a.Value.GetProperty("character").GetUInt32(), a.Value.GetProperty("modifiers").GetUInt16()).Wait();
                return true;
            case Protocol.SaveState:
            {
                var name = a!.Value.GetProperty("name").GetString()!;
                int? slot = a.Value.TryGetProperty("slot", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : null;
                return session?.SaveState(name, slot).GetAwaiter().GetResult();
            }
            case Protocol.LoadState:
            {
                var path = a!.Value.GetProperty("path").GetString()!;
                return session != null && session.LoadState(new OpenEmu.Core.Saves.SaveStateInfo { Name = "", Path = path }).GetAwaiter().GetResult();
            }
            default: throw new ArgumentException("unknown command " + req.Cmd);
        }
    }

    string? line;
    while (!stopRequested && (line = reader.ReadLine()) != null)
    {
        if (line.Length == 0) continue;
        Request? req = null;
        try
        {
            req = JsonSerializer.Deserialize<Request>(line, Protocol.Json)!;
            var result = Handle(req);
            SendLine(new Response { Id = req.Id, Ok = true, Result = JsonSerializer.SerializeToElement(result, Protocol.Json) });
        }
        catch (Exception ex)
        {
            SendLine(new Response { Id = req?.Id ?? 0, Ok = false, Error = ex.InnerException?.Message ?? ex.Message });
            Console.Error.WriteLine(ex);
        }
    }
    try { session?.Stop(); session?.Dispose(); } catch { }
}
return 0;

/// <summary>Input source that merges the UI's shared keyboard/pointer state into the host's own InputManager (gamepads are read locally).</summary>
unsafe sealed class HostInput : IInputSource
{
    private readonly InputManager _inner; private readonly byte* _shm;
    public HostInput(InputManager inner, byte* shm) { _inner = inner; _shm = shm; }
    public void Poll()
    {
        for (var i = 0; i < 256; i++) _inner.Keyboard.Set(i, SharedInput.IsDown(_shm, i));
        var (x, y, pressed) = SharedInput.GetPointer(_shm);
        _inner.Pointer.X = x; _inner.Pointer.Y = y; _inner.Pointer.Pressed = pressed;
        _inner.Poll();
    }
    public short GetState(uint port, uint device, uint index, uint id) => _inner.GetState(port, device, index, id);
}
