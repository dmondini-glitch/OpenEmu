using System.Text.Json;
using System.Text.Json.Serialization;
using OpenEmu.Core.Input;

namespace OpenEmu.Core.Config;

public enum VideoFilter { Nearest, Linear }
public enum LibraryViewMode { Grid, List }

public sealed class VideoSettings
{
    public VideoFilter Filter { get; set; } = VideoFilter.Nearest;
    public bool IntegralScaling { get; set; } = false;
    public bool KeepAspectRatio { get; set; } = true;
    public bool VSync { get; set; } = true;
    public int WindowScale { get; set; } = 3;
    public bool StartFullscreen { get; set; } = false;
    public bool ShowFps { get; set; } = false;
}

public sealed class AudioSettings
{
    public float Volume { get; set; } = 1.0f;
    public bool Muted { get; set; } = false;
}

public sealed class GameplaySettings
{
    public bool AutoSaveStateOnQuit { get; set; } = true;
    public bool LoadAutoSaveOnStart { get; set; } = true;
    public bool ShowCoreMessages { get; set; } = true;
    public bool BackgroundPause { get; set; } = true;
    public int FastForwardMultiplier { get; set; } = 4;
    public int QuickSaveSlot { get; set; } = 1;
}

public sealed class Hotkeys
{
    // HID usages; defaults follow OpenEmu's keyboard shortcuts spirit
    public int Pause { get; set; } = HidKeys.P;
    public int FastForward { get; set; } = HidKeys.Tab;
    public int QuickSave { get; set; } = HidKeys.F5;
    public int QuickLoad { get; set; } = HidKeys.F8;
    public int Screenshot { get; set; } = HidKeys.F12;
    public int Fullscreen { get; set; } = HidKeys.F11;
    public int Reset { get; set; } = HidKeys.F2;
    public int NextSlot { get; set; } = HidKeys.F7;
    public int PreviousSlot { get; set; } = HidKeys.F6;
    public int Mute { get; set; } = HidKeys.M;
}

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public string? LibraryRoot { get; set; }
    public bool CopyRomsToLibrary { get; set; } = true;
    public string Language { get; set; } = "en";
    public LibraryViewMode ViewMode { get; set; } = LibraryViewMode.Grid;
    public double GridItemSize { get; set; } = 160;
    public VideoSettings Video { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public GameplaySettings Gameplay { get; set; } = new();
    public Hotkeys Hotkeys { get; set; } = new();
    /// <summary>System id → preferred core id.</summary>
    public Dictionary<string, string> DefaultCores { get; set; } = new();
    /// <summary>Core id → option key → value.</summary>
    public Dictionary<string, Dictionary<string, string>> CoreOptions { get; set; } = new();
    public BindingSet Bindings { get; set; } = new();
    /// <summary>Systems the user hid from the sidebar.</summary>
    public HashSet<string> HiddenSystems { get; set; } = new();
    public DateTime? LastCoreUpdateCheck { get; set; }
    public bool WelcomeShown { get; set; }

    [JsonIgnore] public static JsonSerializerOptions JsonOptions { get; } = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() }, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
}

public static class SettingsStore
{
    private static readonly object s_lock = new();

    public static AppSettings Load(string? path = null)
    {
        path ??= Paths.SettingsFile;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), AppSettings.JsonOptions) ?? new AppSettings();
        }
        catch { /* corrupt settings → defaults (a backup is kept below) */ try { File.Copy(path, path + ".bak", true); } catch { } }
        return new AppSettings();
    }

    public static void Save(AppSettings settings, string? path = null)
    {
        path ??= Paths.SettingsFile;
        lock (s_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, AppSettings.JsonOptions));
            File.Move(tmp, path, true);
        }
    }
}
