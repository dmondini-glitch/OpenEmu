using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenEmu.Core.Systems;

public sealed class ControlDefinition
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
}

/// <summary>A console/computer "system plugin" (mirrors OpenEmu's OESystemPlugin bundles).</summary>
public sealed class SystemDefinition
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("extensions")] public List<string> Extensions { get; set; } = new();
    [JsonPropertyName("type")] public string Type { get; set; } = "Console";
    [JsonPropertyName("controlGroups")] public List<List<ControlDefinition>> ControlGroups { get; set; } = new();
    [JsonPropertyName("axes")] public List<List<string>> Axes { get; set; } = new();
    [JsonPropertyName("hats")] public List<List<string>> Hats { get; set; } = new();
    [JsonPropertyName("keyboard")] public Dictionary<string, int> Keyboard { get; set; } = new();
    [JsonPropertyName("libretroMap")] public Dictionary<string, string> LibretroMap { get; set; } = new();
    [JsonPropertyName("cores")] public List<string> Cores { get; set; } = new();

    [JsonIgnore] public string ShortId => Id.StartsWith("openemu.system.") ? Id["openemu.system.".Length..] : Id;
    [JsonIgnore] public string DefaultCore => Cores.Count > 0 ? Cores[0] : "";
    [JsonIgnore] public bool IsComputer => Type == "Computer";
    [JsonIgnore] public IEnumerable<ControlDefinition> Controls => ControlGroups.SelectMany(g => g);
    [JsonIgnore] public bool UsesDiscImages => Extensions.Contains("cue") || Extensions.Contains("iso") || Extensions.Contains("chd") || Extensions.Contains("gdi");
    public override string ToString() => Name;
}

public sealed class SystemsFile
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("systems")] public List<SystemDefinition> Systems { get; set; } = new();
}

public static class SystemCatalog
{
    private static readonly Lazy<List<SystemDefinition>> s_all = new(Load);
    public static IReadOnlyList<SystemDefinition> All => s_all.Value;

    private static List<SystemDefinition> Load()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("systems.json") ?? throw new InvalidOperationException("systems.json missing");
        var f = JsonSerializer.Deserialize<SystemsFile>(s) ?? throw new InvalidOperationException("systems.json invalid");
        return f.Systems.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static SystemDefinition? Find(string id) => All.FirstOrDefault(s => s.Id == id || s.ShortId == id);

    public static IReadOnlyList<SystemDefinition> ByExtension(string extension)
    {
        var ext = extension.TrimStart('.').ToLowerInvariant();
        return All.Where(s => s.Extensions.Contains(ext)).ToList();
    }

    public static IReadOnlySet<string> AllExtensions => All.SelectMany(s => s.Extensions).ToHashSet();
}
