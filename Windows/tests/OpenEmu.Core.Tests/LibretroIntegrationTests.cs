using OpenEmu.Core.Audio;
using OpenEmu.Core.Cores;
using OpenEmu.Core.Emulation;
using OpenEmu.Core.Input;
using OpenEmu.Core.Systems;
using Xunit;

namespace OpenEmu.Core.Tests;

/// <summary>
/// Loads a real libretro core (the tiny "2048" core, which needs no game content) through the plugin loader
/// and runs it for a few frames. Skipped when the core is not available and cannot be downloaded.
/// </summary>
public class LibretroIntegrationTests
{
    private static async Task<string?> GetCoreAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openemu-test-cores");
        var mgr = new CoreManager(dir, dir);
        var p = mgr.FindLibrary("2048");
        if (p != null) return p;
        if (Environment.GetEnvironmentVariable("OPENEMU_OFFLINE") == "1") return null;
        try { return await mgr.InstallAsync("2048"); } catch { return null; }
    }

    [Fact]
    public async Task LoadsCoreRunsFramesAndSavesState()
    {
        var lib = await GetCoreAsync();
        if (lib == null) return; // offline: skip
        var def = CoreManifest.Find("2048")!;
        var sys = SystemCatalog.Find("nes")!; // any system; 2048 ignores it
        var tmp = Path.Combine(Path.GetTempPath(), "openemu-test-" + Guid.NewGuid().ToString("N"));
        var opts = new SessionOptions { System = sys, Core = def, CoreLibraryPath = lib, RomPath = null, GameKey = "2048", BiosDir = tmp, SavesDir = tmp, StatesDir = tmp };
        var audio = new NullAudioSink();
        var pads = new VirtualGamepadProvider();
        using var session = new EmulationSession(opts, audio, new InputManager(pads));
        session.FastForward = true;
        await session.StartAsync();
        Assert.Equal(SessionState.Running, session.State);
        Assert.NotNull(session.Core);
        Assert.Equal("2048", session.Core!.LibraryName);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (session.FrameCount < 30 && sw.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
        Assert.True(session.FrameCount >= 30, $"only {session.FrameCount} frames");
        Assert.True(session.Frame.Width > 0 && session.Frame.Height > 0);
        Assert.False(session.Frame.IsBlank());
        var png = session.Screenshot();
        Assert.True(png.Length > 100);
        var state = await session.SaveState("t");
        Assert.NotNull(state);
        Assert.True(await session.LoadState(state!));
        await session.Reset();
        session.Pause();
        await Task.Delay(50); // let the frame in flight finish
        var f = session.FrameCount; await Task.Delay(150); Assert.Equal(f, session.FrameCount);
        session.Resume();
        session.Stop();
        Assert.Equal(SessionState.Stopped, session.State);
        try { Directory.Delete(tmp, true); } catch { }
    }
}
