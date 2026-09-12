using System.Text.Json;
using OpenEmu.Core.Config;

namespace OpenEmu.Core.Saves;

public sealed class SaveStateInfo
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public string? ScreenshotPath { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? CoreId { get; init; }
    public int? Slot { get; init; }
    public bool IsAutoSave => Name == SaveStateManager.AutoSaveName;
    public bool IsQuickSave => Slot.HasValue;
}

/// <summary>Save states live in "Save States/&lt;system&gt;/&lt;game key&gt;/*.state" with a PNG thumbnail and JSON metadata next to them.</summary>
public sealed class SaveStateManager
{
    public const string AutoSaveName = "Auto Save State";
    public string StatesDir { get; }
    public SaveStateManager(string? statesDir = null) => StatesDir = statesDir ?? Paths.StatesDir;

    public string GameDirectory(string systemId, string gameKey) => Path.Combine(StatesDir, Paths.SafeFileName(systemId), Paths.SafeFileName(gameKey));

    public IReadOnlyList<SaveStateInfo> List(string systemId, string gameKey)
    {
        var dir = GameDirectory(systemId, gameKey);
        if (!Directory.Exists(dir)) return Array.Empty<SaveStateInfo>();
        var list = new List<SaveStateInfo>();
        foreach (var f in Directory.EnumerateFiles(dir, "*.state"))
        {
            var meta = f + ".json";
            Meta? m = null;
            try { if (File.Exists(meta)) m = JsonSerializer.Deserialize<Meta>(File.ReadAllText(meta)); } catch { }
            var png = System.IO.Path.ChangeExtension(f, ".png");
            list.Add(new SaveStateInfo
            {
                Name = m?.Name ?? System.IO.Path.GetFileNameWithoutExtension(f), Path = f, ScreenshotPath = File.Exists(png) ? png : null,
                CreatedAt = m?.CreatedAt ?? File.GetLastWriteTimeUtc(f), CoreId = m?.CoreId, Slot = m?.Slot,
            });
        }
        return list.OrderByDescending(s => s.CreatedAt).ToList();
    }

    public SaveStateInfo Write(string systemId, string gameKey, string name, byte[] state, byte[]? screenshotPng, string coreId, int? slot = null)
    {
        var dir = GameDirectory(systemId, gameKey);
        Directory.CreateDirectory(dir);
        var fileBase = slot.HasValue ? $"Quick Save {slot}" : name == AutoSaveName ? "auto" : $"{Paths.SafeFileName(name)} {DateTime.Now:yyyy-MM-dd HH-mm-ss}";
        var path = Path.Combine(dir, fileBase + ".state");
        File.WriteAllBytes(path + ".tmp", state);
        File.Move(path + ".tmp", path, true);
        string? png = null;
        if (screenshotPng != null) { png = Path.ChangeExtension(path, ".png"); File.WriteAllBytes(png, screenshotPng); }
        var meta = new Meta { Name = name, CreatedAt = DateTime.UtcNow, CoreId = coreId, Slot = slot };
        File.WriteAllText(path + ".json", JsonSerializer.Serialize(meta));
        return new SaveStateInfo { Name = name, Path = path, ScreenshotPath = png, CreatedAt = meta.CreatedAt, CoreId = coreId, Slot = slot };
    }

    public SaveStateInfo? FindSlot(string systemId, string gameKey, int slot) => List(systemId, gameKey).FirstOrDefault(s => s.Slot == slot);
    public SaveStateInfo? FindAutoSave(string systemId, string gameKey) => List(systemId, gameKey).FirstOrDefault(s => s.IsAutoSave);

    public void Delete(SaveStateInfo s)
    {
        foreach (var f in new[] { s.Path, s.Path + ".json", s.ScreenshotPath })
            if (f != null && File.Exists(f)) File.Delete(f);
    }

    public void Rename(SaveStateInfo s, string newName)
    {
        var meta = new Meta { Name = newName, CreatedAt = s.CreatedAt, CoreId = s.CoreId, Slot = s.Slot };
        File.WriteAllText(s.Path + ".json", JsonSerializer.Serialize(meta));
    }

    private sealed class Meta { public string Name { get; set; } = ""; public DateTime CreatedAt { get; set; } public string? CoreId { get; set; } public int? Slot { get; set; } }
}

/// <summary>Battery-backed saves (SRAM / RTC) persisted as "Battery Saves/&lt;system&gt;/&lt;game key&gt;.srm".</summary>
public sealed class BatterySaveManager
{
    public string SavesDir { get; }
    public BatterySaveManager(string? dir = null) => SavesDir = dir ?? Paths.SavesDir;

    public string PathFor(string systemId, string gameKey, string ext = ".srm") => Path.Combine(SavesDir, Paths.SafeFileName(systemId), Paths.SafeFileName(gameKey) + ext);

    public byte[]? Load(string systemId, string gameKey, string ext = ".srm")
    {
        var p = PathFor(systemId, gameKey, ext);
        return File.Exists(p) ? File.ReadAllBytes(p) : null;
    }

    public void Save(string systemId, string gameKey, ReadOnlySpan<byte> data, string ext = ".srm")
    {
        var p = PathFor(systemId, gameKey, ext);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        if (File.Exists(p))
        {
            var existing = File.ReadAllBytes(p);
            if (existing.AsSpan().SequenceEqual(data)) return; // unchanged
        }
        File.WriteAllBytes(p + ".tmp", data.ToArray());
        File.Move(p + ".tmp", p, true);
    }
}
