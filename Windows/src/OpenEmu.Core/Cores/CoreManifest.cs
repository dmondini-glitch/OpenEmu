using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenEmu.Core.Cores;

public sealed class FirmwareEntry
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("desc")] public string? Description { get; set; }
    [JsonPropertyName("optional")] public bool Optional { get; set; }
}

/// <summary>Metadata for one libretro core ("driver plugin"), generated from libretro-core-info.</summary>
public sealed class CoreDefinition
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("coreName")] public string? CoreName { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("license")] public string? License { get; set; }
    [JsonPropertyName("authors")] public string? Authors { get; set; }
    [JsonPropertyName("systemName")] public string? SystemName { get; set; }
    [JsonPropertyName("manufacturer")] public string? Manufacturer { get; set; }
    [JsonPropertyName("extensions")] public List<string> Extensions { get; set; } = new();
    [JsonPropertyName("hwRender")] public bool HwRender { get; set; }
    [JsonPropertyName("needsFullPath")] public bool NeedsFullPath { get; set; }
    [JsonPropertyName("supportsNoGame")] public bool SupportsNoGame { get; set; }
    [JsonPropertyName("saveStates")] public bool SaveStates { get; set; }
    [JsonPropertyName("cheats")] public bool Cheats { get; set; }
    [JsonPropertyName("diskControl")] public bool DiskControl { get; set; }
    [JsonPropertyName("database")] public List<string> Database { get; set; } = new();
    [JsonPropertyName("firmware")] public List<FirmwareEntry> Firmware { get; set; } = new();
    [JsonPropertyName("download")] public Dictionary<string, string> Download { get; set; } = new();

    [JsonIgnore] public string Title => CoreName ?? DisplayName ?? Id;
    [JsonIgnore] public IEnumerable<FirmwareEntry> RequiredFirmware => Firmware.Where(f => !f.Optional);
    public override string ToString() => Title;
}

public sealed class CoresFile
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("cores")] public List<CoreDefinition> Cores { get; set; } = new();
}

public static class CoreManifest
{
    private static readonly Lazy<List<CoreDefinition>> s_all = new(Load);
    public static IReadOnlyList<CoreDefinition> All => s_all.Value;

    private static List<CoreDefinition> Load()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("cores.json") ?? throw new InvalidOperationException("cores.json missing");
        return (JsonSerializer.Deserialize<CoresFile>(s) ?? throw new InvalidOperationException("cores.json invalid")).Cores;
    }

    public static CoreDefinition? Find(string id) => All.FirstOrDefault(c => c.Id == id);

    /// <summary>Platform key of the running process ("windows-x64", "windows-x86", "windows-arm64", "osx-arm64", ...).</summary>
    public static string NativePlatformKey => PlatformKeyFor(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);

    public static string PlatformKeyFor(System.Runtime.InteropServices.Architecture arch)
    {
        var a = arch switch { System.Runtime.InteropServices.Architecture.X64 => "x64", System.Runtime.InteropServices.Architecture.X86 => "x86", System.Runtime.InteropServices.Architecture.Arm64 => "arm64", _ => arch.ToString().ToLowerInvariant() };
        if (OperatingSystem.IsWindows()) return "windows-" + a;
        if (OperatingSystem.IsMacOS()) return "osx-" + a;
        return "linux-" + a;
    }

    /// <summary>
    /// Platform whose cores this process should use. Native when cores exist for it; on Windows ARM64 (no native libretro
    /// builds) the x64 cores are used through the out-of-process core host running under Windows' x64 emulation.
    /// Override with the OPENEMU_CORE_PLATFORM environment variable (e.g. "windows-x86" on Windows 10 ARM).
    /// </summary>
    public static string PlatformKey
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("OPENEMU_CORE_PLATFORM");
            if (!string.IsNullOrEmpty(env)) return env;
            var native = NativePlatformKey;
            if (All.Any(c => c.Download.ContainsKey(native))) return native;
            return native switch { "windows-arm64" => "windows-x64", "osx-arm64" => "osx-x64", _ => native };
        }
    }

    /// <summary>True when cores for <see cref="PlatformKey"/> cannot be loaded into this process and need the core host.</summary>
    public static bool RequiresCoreHost => PlatformKey != NativePlatformKey;

    /// <summary>Process architecture the core host must have to load cores of the given platform key.</summary>
    public static string HostArchitecture(string platformKey) => platformKey[(platformKey.IndexOf('-') + 1)..];

    public static string LibraryExtension => OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so";
    public static string LibraryFileName(string coreId) => coreId + "_libretro" + LibraryExtension;
}
