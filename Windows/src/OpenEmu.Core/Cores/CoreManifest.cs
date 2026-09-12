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

    /// <summary>Platform key used in the manifest download table.</summary>
    public static string PlatformKey
    {
        get
        {
            var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;
            if (OperatingSystem.IsWindows()) return "windows-x64";
            if (OperatingSystem.IsMacOS()) return arch == System.Runtime.InteropServices.Architecture.Arm64 ? "osx-arm64" : "osx-x64";
            return "linux-x64";
        }
    }

    public static string LibraryExtension => OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so";
    public static string LibraryFileName(string coreId) => coreId + "_libretro" + LibraryExtension;
}
