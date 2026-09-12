// Headless command-line front-end: useful for CI, scripting and verifying cores without the UI.
using OpenEmu.Core.Audio;
using OpenEmu.Core.Bios;
using OpenEmu.Core.Config;
using OpenEmu.Core.Cores;
using OpenEmu.Core.Emulation;
using OpenEmu.Core.Input;
using OpenEmu.Core.Library;
using OpenEmu.Core.Systems;

var args0 = args.Length == 0 ? new[] { "help" } : args;
string? Opt(string name) { var i = Array.IndexOf(args0, "--" + name); return i >= 0 && i + 1 < args0.Length ? args0[i + 1] : null; }
bool Flag(string name) => Array.IndexOf(args0, "--" + name) >= 0;

try
{
    switch (args0[0])
    {
        case "systems":
            foreach (var s in SystemCatalog.All) Console.WriteLine($"{s.ShortId,-14} {s.Name,-26} ext={string.Join(",", s.Extensions),-40} cores={string.Join(",", s.Cores)}");
            return 0;
        case "cores":
        {
            var mgr = new CoreManager();
            foreach (var c in CoreManifest.All)
                Console.WriteLine($"{c.Id,-28} {(mgr.IsInstalled(c.Id) ? "installed" : "-"),-10} {c.Title,-22} hw={(c.HwRender ? "y" : "n")} fw={c.Firmware.Count} ext={string.Join(",", c.Extensions)}");
            return 0;
        }
        case "install":
        {
            var mgr = new CoreManager();
            var ids = args0.Skip(1).Where(a => !a.StartsWith("--")).ToList();
            if (Flag("all")) ids = SystemCatalog.All.SelectMany(s => s.Cores).Distinct().ToList();
            var res = await mgr.InstallAllAsync(ids, new Progress<(string, double)>(p => { if (p.Item2 >= 1) Console.WriteLine($"  {p.Item1} ok"); }));
            foreach (var kv in res.Where(k => k.Value != null)) Console.Error.WriteLine($"  {kv.Key} FAILED: {kv.Value!.Message}");
            return res.Values.All(v => v == null) ? 0 : 1;
        }
        case "platform":
            Console.WriteLine($"native={CoreManifest.NativePlatformKey} cores={CoreManifest.PlatformKey} requiresHost={CoreManifest.RequiresCoreHost} host={OpenEmu.Core.Remote.RemoteSession.FindHost(CoreManifest.HostArchitecture(CoreManifest.PlatformKey)) ?? "(not found)"} coresDir={new CoreManager().UserCoresDir} bundled={new CoreManager().BundledCoresDir}");
            return 0;
        case "bios":
            foreach (var b in new BiosManager().Status())
                Console.WriteLine($"{(b.Present ? "OK " : "-- ")}{b.System.Name,-24} {b.Firmware.Path,-30} {(b.Firmware.Optional ? "(optional) " : "")}{b.Firmware.Description}");
            return 0;
        case "import":
        {
            using var lib = new GameLibrary();
            var imp = new GameImporter(lib, new OpenVgdb()) { CopyToLibrary = !Flag("no-copy") };
            var results = await imp.ImportAsync(args0.Skip(1).Where(a => !a.StartsWith("--")));
            foreach (var r in results) Console.WriteLine(r.Success ? $"+ {r.Game!.Title} [{SystemCatalog.Find(r.Game.SystemId)?.Name}]" : $"! {Path.GetFileName(r.SourcePath)}: {r.Error}");
            return 0;
        }
        case "library":
        {
            using var lib = new GameLibrary();
            foreach (var g in lib.All()) Console.WriteLine($"{g.Id,4} {SystemCatalog.Find(g.SystemId)?.Name,-24} {g.Title,-40} {g.Md5}");
            return 0;
        }
        case "run":
        {
            var coreId = Opt("core") ?? throw new ArgumentException("--core required");
            var rom = Opt("rom");
            var frames = int.Parse(Opt("frames") ?? "300");
            var mgr = new CoreManager();
            var lib = Opt("core-path") ?? mgr.FindLibrary(coreId) ?? (Flag("install") ? await mgr.InstallAsync(coreId) : throw new FileNotFoundException($"core {coreId} not installed (use --install)"));
            var def = CoreManifest.Find(coreId) ?? new CoreDefinition { Id = coreId };
            var sys = (Opt("system") is { } sid ? SystemCatalog.Find(sid) : null) ?? (rom != null ? SystemDetector.Detect(rom) : null) ?? SystemCatalog.All.FirstOrDefault(s => s.Cores.Contains(coreId)) ?? SystemCatalog.All[0];
            var opts = new SessionOptions { System = sys, Core = def, CoreLibraryPath = lib, RomPath = rom, GameKey = rom != null ? Path.GetFileNameWithoutExtension(rom) : coreId };
            var audio = new NullAudioSink();
            IEmulator session;
            if (Flag("host") || CoreManifest.RequiresCoreHost)
            {
                var hostPath = Opt("host-path") ?? OpenEmu.Core.Remote.RemoteSession.FindHost(CoreManifest.HostArchitecture(CoreManifest.PlatformKey)) ?? throw new FileNotFoundException("core host not found (set OPENEMU_COREHOST)");
                session = new OpenEmu.Core.Remote.RemoteSession(opts, hostPath);
                Console.WriteLine($"using core host {hostPath}");
            }
            else session = new EmulationSession(opts, audio, new InputManager(new NullGamepadProvider()));
            session.Log += (lvl, msg) => { if (Flag("verbose") || lvl >= 2) Console.Error.WriteLine($"[{coreId}] {msg}"); };
            session.FastForward = Flag("fast");
            await session.StartAsync();
            var info = session.Info!;
            Console.WriteLine($"{info.CoreName} {info.CoreVersion}: {info.BaseWidth}x{info.BaseHeight} @ {info.Fps:0.##} fps, {info.SampleRate} Hz, hw={info.HwRender}, remote={info.IsRemote}, options={session.CoreOptions.Count}");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long count = 0; session.FrameRendered += () => Interlocked.Increment(ref count);
            while (Interlocked.Read(ref count) < frames && session.State == SessionState.Running && sw.Elapsed < TimeSpan.FromSeconds(120)) await Task.Delay(5);
            Console.WriteLine($"ran {count} frames in {sw.Elapsed.TotalSeconds:0.00}s; audio samples={audio.SamplesWritten}; frame {session.Frame.Width}x{session.Frame.Height} blank={session.Frame.IsBlank()}");
            if (Opt("screenshot") is { } shot) { File.WriteAllBytes(shot, session.Screenshot()); Console.WriteLine($"screenshot → {shot}"); }
            if (Flag("state"))
            {
                var st = await session.SaveState("cli test");
                Console.WriteLine(st != null ? $"state saved: {st.Path} ({new FileInfo(st.Path).Length} bytes)" : "state: unsupported");
                if (st != null) Console.WriteLine($"state loaded: {await session.LoadState(st)}");
            }
            session.Stop();
            return 0;
        }
        default:
            Console.WriteLine("""
                openemu-cli — headless OpenEmu for Windows
                  systems                         list supported systems
                  cores                           list cores and install status
                  install <id...> | --all         download cores from the libretro buildbot
                  bios                            BIOS/firmware status
                  import <files|folders> [--no-copy]
                  library                         list games
                  run --core <id> [--rom file] [--frames N] [--screenshot out.png] [--state] [--install] [--fast] [--verbose]
                                  [--host [--host-path exe]]   run the core in the out-of-process core host
                  platform                        show native platform, core platform and core-host status
                """);
            return 0;
    }
}
catch (Exception ex) { Console.Error.WriteLine("error: " + ex.Message); if (Flag("verbose")) Console.Error.WriteLine(ex); return 2; }
