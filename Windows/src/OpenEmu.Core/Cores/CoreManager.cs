using System.IO.Compression;
using OpenEmu.Core.Config;
using OpenEmu.Core.Systems;

namespace OpenEmu.Core.Cores;

public sealed record InstalledCore(CoreDefinition Definition, string Path, DateTime InstalledAt, string? Version);

/// <summary>
/// Installs, updates and locates libretro cores. Cores are looked up in the user cores folder first,
/// then in the "cores" folder shipped next to the application (the bundled "all drivers" set).
/// </summary>
public sealed class CoreManager
{
    private readonly HttpClient _http;
    public string UserCoresDir { get; }
    public string BundledCoresDir { get; }
    public event Action? Changed;

    public CoreManager(string? userCoresDir = null, string? bundledCoresDir = null, HttpClient? http = null)
    {
        UserCoresDir = userCoresDir ?? Paths.CoresDir;
        BundledCoresDir = bundledCoresDir ?? Paths.BundledCoresDir;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenEmu-Windows/0.1");
    }

    public string? FindLibrary(string coreId)
    {
        var name = CoreManifest.LibraryFileName(coreId);
        var user = Path.Combine(UserCoresDir, name);
        if (File.Exists(user)) return user;
        var bundled = Path.Combine(BundledCoresDir, name);
        return File.Exists(bundled) ? bundled : null;
    }

    public bool IsInstalled(string coreId) => FindLibrary(coreId) != null;

    public IReadOnlyList<InstalledCore> Installed()
    {
        var list = new List<InstalledCore>();
        foreach (var def in CoreManifest.All)
        {
            var p = FindLibrary(def.Id);
            if (p == null) continue;
            list.Add(new InstalledCore(def, p, File.GetLastWriteTimeUtc(p), ReadVersionStamp(def.Id)));
        }
        return list;
    }

    /// <summary>Cores able to run a system, ordered by preference (user default first, then manifest order).</summary>
    public IReadOnlyList<CoreDefinition> CoresForSystem(SystemDefinition system, AppSettings? settings = null)
    {
        var ids = new List<string>(system.Cores);
        if (settings != null && settings.DefaultCores.TryGetValue(system.Id, out var pref) && ids.Remove(pref)) ids.Insert(0, pref);
        return ids.Select(CoreManifest.Find).Where(c => c != null).Cast<CoreDefinition>().ToList();
    }

    public CoreDefinition? PreferredCore(SystemDefinition system, AppSettings? settings = null)
    {
        var cores = CoresForSystem(system, settings);
        return cores.FirstOrDefault(c => IsInstalled(c.Id)) ?? cores.FirstOrDefault();
    }

    public async Task<string> InstallAsync(string coreId, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var def = CoreManifest.Find(coreId) ?? throw new ArgumentException($"Unknown core {coreId}");
        if (!def.Download.TryGetValue(CoreManifest.PlatformKey, out var url)) throw new PlatformNotSupportedException($"No download for {coreId} on {CoreManifest.PlatformKey}");
        Directory.CreateDirectory(UserCoresDir);
        var zipPath = Path.Combine(UserCoresDir, coreId + ".zip.part");
        using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zipPath);
            var buf = new byte[1 << 16]; long done = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        var target = Path.Combine(UserCoresDir, CoreManifest.LibraryFileName(coreId));
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var entry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(CoreManifest.LibraryExtension, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException("Core archive does not contain a library");
            entry.ExtractToFile(target + ".new", true);
        }
        File.Delete(zipPath);
        File.Move(target + ".new", target, true);
        File.WriteAllText(target + ".version", DateTime.UtcNow.ToString("O"));
        progress?.Report(1);
        Changed?.Invoke();
        return target;
    }

    public async Task<Dictionary<string, Exception?>> InstallAllAsync(IEnumerable<string> coreIds, IProgress<(string Core, double Progress)>? progress = null, CancellationToken ct = default)
    {
        var results = new Dictionary<string, Exception?>();
        foreach (var id in coreIds)
        {
            ct.ThrowIfCancellationRequested();
            try { await InstallAsync(id, new Progress<double>(p => progress?.Report((id, p))), ct); results[id] = null; }
            catch (Exception ex) { results[id] = ex; }
        }
        return results;
    }

    public bool Uninstall(string coreId)
    {
        var p = Path.Combine(UserCoresDir, CoreManifest.LibraryFileName(coreId));
        if (!File.Exists(p)) return false;
        File.Delete(p);
        if (File.Exists(p + ".version")) File.Delete(p + ".version");
        Changed?.Invoke();
        return true;
    }

    /// <summary>Checks the buildbot index for newer builds (by Last-Modified) than the locally installed ones.</summary>
    public async Task<IReadOnlyList<CoreDefinition>> CheckUpdatesAsync(CancellationToken ct = default)
    {
        var updates = new List<CoreDefinition>();
        foreach (var inst in Installed())
        {
            if (!inst.Definition.Download.TryGetValue(CoreManifest.PlatformKey, out var url)) continue;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, url);
                using var resp = await _http.SendAsync(req, ct);
                var remote = resp.Content.Headers.LastModified?.UtcDateTime;
                if (remote.HasValue && remote.Value > inst.InstalledAt.AddHours(1)) updates.Add(inst.Definition);
            }
            catch { }
        }
        return updates;
    }

    private string? ReadVersionStamp(string coreId)
    {
        var p = FindLibrary(coreId);
        if (p == null) return null;
        var v = p + ".version";
        return File.Exists(v) ? File.ReadAllText(v).Trim() : null;
    }

    public static string CoreIdFromLibrary(string path)
    {
        var n = Path.GetFileNameWithoutExtension(path);
        return n.EndsWith("_libretro") ? n[..^"_libretro".Length] : n;
    }
}
