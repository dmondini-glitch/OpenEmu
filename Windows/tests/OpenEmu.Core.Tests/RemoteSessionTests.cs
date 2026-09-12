using OpenEmu.Core.Cores;
using OpenEmu.Core.Emulation;
using OpenEmu.Core.Remote;
using OpenEmu.Core.Systems;
using Xunit;

namespace OpenEmu.Core.Tests;

/// <summary>Runs the 2048 core through the out-of-process core host (the path used on Windows ARM64).</summary>
public class RemoteSessionTests
{
    private static string? FindHostDll()
    {
        var env = Environment.GetEnvironmentVariable("OPENEMU_COREHOST");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        // tests/OpenEmu.Core.Tests/bin/<cfg>/net9.0 → src/OpenEmu.CoreHost/bin/<cfg>/net9.0/OpenEmu.CoreHost.dll
        var dir = AppContext.BaseDirectory;
        var cfg = dir.Contains("Release") ? "Release" : "Debug";
        var root = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", ".."));
        var p = Path.Combine(root, "src", "OpenEmu.CoreHost", "bin", cfg, "net9.0", "OpenEmu.CoreHost.dll");
        return File.Exists(p) ? p : null;
    }

    [Fact]
    public async Task FramesAndCommandsFlowThroughTheCoreHost()
    {
        var host = FindHostDll();
        if (host == null) return; // host not built: skip
        var dir = Path.Combine(Path.GetTempPath(), "openemu-test-cores");
        var mgr = new CoreManager(dir, dir);
        var lib = mgr.FindLibrary("2048");
        if (lib == null) { if (Environment.GetEnvironmentVariable("OPENEMU_OFFLINE") == "1") return; try { lib = await mgr.InstallAsync("2048"); } catch { return; } }
        var tmp = Path.Combine(Path.GetTempPath(), "openemu-remote-" + Guid.NewGuid().ToString("N"));
        var opts = new SessionOptions { System = SystemCatalog.Find("nes")!, Core = CoreManifest.Find("2048")!, CoreLibraryPath = lib, GameKey = "2048", BiosDir = tmp, SavesDir = tmp, StatesDir = tmp };
        using var session = new RemoteSession(opts, host);
        var states = new List<SessionState>();
        session.StateChanged += s => states.Add(s);
        await session.StartAsync();
        Assert.Equal(SessionState.Running, session.State);
        Assert.NotNull(session.Info);
        Assert.True(session.Info!.IsRemote);
        Assert.Equal("2048", session.Info.CoreName);
        Assert.NotEmpty(session.CoreOptions);
        var frames = 0; session.FrameRendered += () => frames++;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (frames < 20 && sw.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(20);
        Assert.True(frames >= 20, $"only {frames} frames via shared memory");
        Assert.True(session.Frame.Width > 0 && !session.Frame.IsBlank());
        // keyboard goes through shared memory; RetroPad start = Enter on the default NES map
        session.SetKey(Input.HidKeys.Enter, true); await Task.Delay(100); session.SetKey(Input.HidKeys.Enter, false);
        var st = await session.SaveState("remote");
        Assert.NotNull(st);
        Assert.True(File.Exists(st!.Path));
        Assert.True(await session.LoadState(st));
        await session.Reset();
        session.Pause();
        sw.Restart(); while (session.State != SessionState.Paused && sw.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
        Assert.Equal(SessionState.Paused, session.State);
        session.Resume();
        session.Stop();
        Assert.Equal(SessionState.Stopped, session.State);
        try { Directory.Delete(tmp, true); } catch { }
    }

    [Fact]
    public void PlatformKeysAreConsistent()
    {
        Assert.Equal("windows-x86", CoreManifest.PlatformKeyFor(System.Runtime.InteropServices.Architecture.X86).Replace("osx", "windows").Replace("linux", "windows"));
        Assert.Equal("x64", CoreManifest.HostArchitecture("windows-x64"));
        Assert.Equal("arm64", CoreManifest.HostArchitecture("windows-arm64"));
        // every system has a 32-bit core except GameCube (Dolphin dropped x86)
        foreach (var s in SystemCatalog.All)
        {
            var hasX86 = s.Cores.Any(c => CoreManifest.Find(c)!.Download.ContainsKey("windows-x86"));
            if (s.Id == "openemu.system.gc") Assert.False(hasX86); else Assert.True(hasX86, s.Id + " has no windows-x86 core");
        }
    }
}
