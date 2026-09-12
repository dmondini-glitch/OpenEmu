namespace OpenEmu.Core.Config;

/// <summary>Filesystem layout (mirrors ~/Library/Application Support/OpenEmu on macOS).</summary>
public static class Paths
{
    private static string? s_rootOverride;

    public static string ConfigRoot => Environment.GetEnvironmentVariable("OPENEMU_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenEmu");

    /// <summary>Game library root (ROMs, saves, states, covers, database). Defaults to Documents\OpenEmu Library on Windows.</summary>
    public static string LibraryRoot
    {
        get => s_rootOverride ?? Environment.GetEnvironmentVariable("OPENEMU_LIBRARY")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OpenEmu Library");
        set => s_rootOverride = value;
    }

    public static string SettingsFile => Path.Combine(ConfigRoot, "settings.json");
    public static string LogsDir => Path.Combine(ConfigRoot, "Logs");
    public static string CoresDir => Environment.GetEnvironmentVariable("OPENEMU_CORES") ?? Path.Combine(ConfigRoot, "Cores");
    public static string BundledCoresDir => Path.Combine(AppContext.BaseDirectory, "cores");
    public static string DatabaseFile => Path.Combine(LibraryRoot, "Library.sqlite");
    public static string RomsDir => Path.Combine(LibraryRoot, "roms");
    public static string SavesDir => Path.Combine(LibraryRoot, "Battery Saves");
    public static string StatesDir => Path.Combine(LibraryRoot, "Save States");
    public static string ScreenshotsDir => Path.Combine(LibraryRoot, "Screenshots");
    public static string CoversDir => Path.Combine(LibraryRoot, "Artwork");
    public static string BiosDir => Path.Combine(LibraryRoot, "BIOS");
    public static string OpenVgdbFile => Path.Combine(ConfigRoot, "openvgdb.sqlite");
    public static string HomebrewDir => Path.Combine(LibraryRoot, "Homebrew");

    public static void EnsureAll()
    {
        foreach (var d in new[] { ConfigRoot, LogsDir, CoresDir, LibraryRoot, RomsDir, SavesDir, StatesDir, ScreenshotsDir, CoversDir, BiosDir, HomebrewDir })
            Directory.CreateDirectory(d);
    }

    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim();
        return s.Length == 0 ? "_" : s;
    }
}
