using OpenEmu.Core.Bios;
using OpenEmu.Core.Config;
using OpenEmu.Core.Cores;
using OpenEmu.Core.Homebrew;
using OpenEmu.Core.Input;
using OpenEmu.Core.Library;
using OpenEmu.Core.Localization;

namespace OpenEmu.App.Services;

/// <summary>Composition root: long-lived services shared by all windows.</summary>
public sealed class AppServices
{
    public AppSettings Settings { get; private set; } = null!;
    public GameLibrary Library { get; private set; } = null!;
    public CoreManager Cores { get; private set; } = null!;
    public BiosManager Bios { get; private set; } = null!;
    public OpenVgdb Vgdb { get; private set; } = null!;
    public GameImporter Importer { get; private set; } = null!;
    public HomebrewCatalog Homebrew { get; private set; } = null!;
    public HttpClient Http { get; } = new() { Timeout = TimeSpan.FromMinutes(5) };
    public InputManager Input { get; private set; } = null!;
    public Logger Log { get; } = new();
    public GameLauncher Launcher { get; private set; } = null!;

    public static AppServices Create()
    {
        var s = new AppServices();
        s.Settings = SettingsStore.Load();
        if (!string.IsNullOrEmpty(s.Settings.LibraryRoot)) Paths.LibraryRoot = s.Settings.LibraryRoot;
        Paths.EnsureAll();
        L.SetLanguage(s.Settings.Language);
        s.Http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenEmu-Windows/0.1");
        s.Library = new GameLibrary();
        s.Cores = new CoreManager(http: s.Http);
        s.Bios = new BiosManager();
        s.Vgdb = new OpenVgdb();
        s.Importer = new GameImporter(s.Library, s.Vgdb, s.Http) { CopyToLibrary = s.Settings.CopyRomsToLibrary };
        s.Homebrew = new HomebrewCatalog(s.Http);
        s.Input = new InputManager { UserBindings = s.Settings.Bindings };
        s.Launcher = new GameLauncher(s);
        s.Log.Info($"OpenEmu for Windows started. Library: {Paths.LibraryRoot}; cores: {s.Cores.UserCoresDir} (+bundled {s.Cores.BundledCoresDir})");
        return s;
    }

    public void SaveSettings()
    {
        Settings.Bindings = Input.UserBindings;
        Importer.CopyToLibrary = Settings.CopyRomsToLibrary;
        SettingsStore.Save(Settings);
    }

    public void Shutdown()
    {
        try { SaveSettings(); } catch { }
        try { Library.Dispose(); } catch { }
        try { Vgdb.Dispose(); } catch { }
        Log.Dispose();
    }
}

/// <summary>Very small rolling log file in the config folder.</summary>
public sealed class Logger : IDisposable
{
    private readonly StreamWriter? _w;
    public Logger()
    {
        try
        {
            Directory.CreateDirectory(Paths.LogsDir);
            _w = new StreamWriter(Path.Combine(Paths.LogsDir, "openemu.log"), append: true) { AutoFlush = true };
        }
        catch { }
    }
    public void Info(string msg) => Write("INFO", msg);
    public void Warn(string msg) => Write("WARN", msg);
    public void Error(string msg) => Write("ERROR", msg);
    private void Write(string lvl, string msg) { lock (this) { try { _w?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{lvl}] {msg}"); } catch { } } }
    public void Dispose() => _w?.Dispose();
}
