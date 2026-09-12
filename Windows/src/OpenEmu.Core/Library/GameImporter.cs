using System.IO.Compression;
using OpenEmu.Core.Config;
using OpenEmu.Core.Systems;

namespace OpenEmu.Core.Library;

public sealed record ImportResult(string SourcePath, Game? Game, string? Error, IReadOnlyList<SystemDefinition>? AmbiguousSystems = null)
{
    public bool Success => Game != null;
    public bool NeedsSystemChoice => AmbiguousSystems is { Count: > 1 };
}

/// <summary>
/// Imports ROM files/folders/archives into the library: detects the system, hashes, looks up OpenVGDB metadata,
/// optionally copies the file into the library and downloads cover art.
/// </summary>
public sealed class GameImporter
{
    private readonly GameLibrary _library;
    private readonly OpenVgdb? _vgdb;
    private readonly HttpClient _http;
    public bool CopyToLibrary { get; set; } = true;
    public string RomsDir { get; set; } = Paths.RomsDir;
    public string CoversDir { get; set; } = Paths.CoversDir;
    public bool DownloadCovers { get; set; } = true;
    public event Action<string>? Progress;

    private static readonly HashSet<string> ArchiveExts = new(StringComparer.OrdinalIgnoreCase) { "zip" };
    private static readonly HashSet<string> IgnoredExts = new(StringComparer.OrdinalIgnoreCase) { "txt", "nfo", "png", "jpg", "jpeg", "srm", "state", "sav", "cfg", "json", "xml", "ini", "dat", "html", "pdf", "dll", "exe", "db", "md", "bak" };

    public GameImporter(GameLibrary library, OpenVgdb? vgdb = null, HttpClient? http = null)
    {
        _library = library; _vgdb = vgdb;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    public IEnumerable<string> ExpandPaths(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) yield return f;
            }
            else if (File.Exists(p)) yield return p;
        }
    }

    public async Task<List<ImportResult>> ImportAsync(IEnumerable<string> paths, SystemDefinition? forceSystem = null, CancellationToken ct = default)
    {
        var results = new List<ImportResult>();
        var seenTrackFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = ExpandPaths(paths).ToList();
        // Disc images: the cue/ccd/m3u sheet is the game; its bin/img/iso tracks are not imported separately.
        foreach (var f in files.Where(f => Path.GetExtension(f).ToLowerInvariant() is ".cue" or ".ccd" or ".m3u"))
            foreach (var t in ReferencedFiles(f)) seenTrackFiles.Add(t);
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(f).TrimStart('.').ToLowerInvariant();
            if (IgnoredExts.Contains(ext) || seenTrackFiles.Contains(f)) continue;
            if (ext is "bin" or "img" or "iso" && seenTrackFiles.Contains(f)) continue;
            Progress?.Invoke(Path.GetFileName(f));
            try { results.Add(await ImportOneAsync(f, forceSystem, ct)); }
            catch (Exception ex) { results.Add(new ImportResult(f, null, ex.Message)); }
        }
        return results;
    }

    private static IEnumerable<string> ReferencedFiles(string sheet)
    {
        var dir = Path.GetDirectoryName(sheet) ?? "";
        foreach (var line in File.ReadLines(sheet))
        {
            var t = line.Trim();
            if (t.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase))
            {
                var rest = t[5..].Trim();
                var name = rest.StartsWith('"') ? rest[1..rest.IndexOf('"', 1)] : rest.Split(' ')[0];
                yield return Path.GetFullPath(Path.Combine(dir, name));
            }
            else if (sheet.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase) && t.Length > 0 && !t.StartsWith('#')) yield return Path.GetFullPath(Path.Combine(dir, t));
            else if (sheet.EndsWith(".ccd", StringComparison.OrdinalIgnoreCase)) { yield return Path.ChangeExtension(sheet, ".img"); yield return Path.ChangeExtension(sheet, ".sub"); }
        }
    }

    public async Task<ImportResult> ImportOneAsync(string file, SystemDefinition? forceSystem = null, CancellationToken ct = default)
    {
        var existing = _library.FindByPath(file);
        if (existing != null) return new ImportResult(file, existing, "Already in library");

        var ext = Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
        var romFile = file;
        string? extractedTemp = null;
        SystemDefinition? system = forceSystem;

        // Archives: arcade sets stay zipped; otherwise extract the single ROM inside.
        if (ArchiveExts.Contains(ext))
        {
            var arcade = SystemCatalog.Find("openemu.system.arcade");
            if (forceSystem?.Id == arcade?.Id) system = arcade;
            else
            {
                var inner = ExtractSingleRom(file, out extractedTemp);
                if (inner == null)
                {
                    // Could be an arcade ROM set (many files without a known extension)
                    if (arcade != null && forceSystem == null) system = arcade;
                    else return new ImportResult(file, null, "Archive does not contain a recognized ROM");
                }
                else romFile = inner;
            }
        }

        if (system == null)
        {
            var candidates = SystemDetector.Candidates(romFile);
            if (candidates.Count == 0) { Cleanup(extractedTemp); return new ImportResult(file, null, $"Unknown file type .{Path.GetExtension(romFile).TrimStart('.')}"); }
            if (candidates.Count > 1) { Cleanup(extractedTemp); return new ImportResult(file, null, "Ambiguous system", candidates); }
            system = candidates[0];
        }

        var hashes = system.Id == "openemu.system.arcade" || Path.GetExtension(romFile).ToLowerInvariant() is ".cue" or ".ccd" or ".m3u" or ".gdi"
            ? HashDiscOrSet(romFile)
            : RomHasher.Compute(romFile, system.Id);

        var dup = hashes.Md5.Length > 0 ? _library.FindByMd5(hashes.Md5, system.Id) : null;
        if (dup != null) { Cleanup(extractedTemp); return new ImportResult(file, dup, "Duplicate of an existing game"); }

        var title = CleanTitle(Path.GetFileNameWithoutExtension(system.Id == "openemu.system.arcade" ? file : romFile));
        var game = new Game { Title = title, SystemId = system.Id, RomPath = romFile, Md5 = hashes.Md5, Crc32 = hashes.Crc32, Sha1 = hashes.Sha1, Size = hashes.Size, AddedAt = DateTime.UtcNow };

        var release = _vgdb?.Lookup(hashes.Md5, hashes.Crc32);
        if (release != null)
        {
            game.Title = release.Title; game.CoverUrl = release.CoverUrl; game.Description = release.Description; game.Developer = release.Developer;
            game.Publisher = release.Publisher; game.Genre = release.Genre; game.ReleaseDate = release.ReleaseDate; game.Region = release.Region;
            if (release.SystemOeId != null && SystemCatalog.Find(release.SystemOeId) is { } better && better.Id != system.Id && !system.UsesDiscImages) { system = better; game.SystemId = better.Id; }
        }

        if (CopyToLibrary || extractedTemp != null)
        {
            var destDir = Path.Combine(RomsDir, Paths.SafeFileName(system.Name));
            Directory.CreateDirectory(destDir);
            if (Path.GetExtension(romFile).ToLowerInvariant() is ".cue" or ".ccd" or ".m3u" or ".gdi")
            {
                // copy the whole disc set (sheet + referenced tracks)
                foreach (var t in ReferencedFiles(romFile).Append(romFile).Distinct(StringComparer.OrdinalIgnoreCase))
                    if (File.Exists(t)) File.Copy(t, Path.Combine(destDir, Path.GetFileName(t)), true);
                game.RomPath = Path.Combine(destDir, Path.GetFileName(romFile));
            }
            else
            {
                var dest = UniquePath(Path.Combine(destDir, Path.GetFileName(romFile)));
                if (extractedTemp != null) File.Move(romFile, dest, true); else File.Copy(romFile, dest, true);
                game.RomPath = dest;
            }
        }
        Cleanup(extractedTemp);

        if (DownloadCovers && game.CoverUrl != null)
        {
            try { game.CoverPath = await DownloadCoverAsync(game.CoverUrl, game, ct); } catch { }
        }
        _library.Add(game);
        return new ImportResult(file, game, null);
    }

    public async Task<string?> DownloadCoverAsync(string url, Game game, CancellationToken ct = default)
    {
        Directory.CreateDirectory(CoversDir);
        var ext = Path.GetExtension(new Uri(url).AbsolutePath);
        if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".jpg";
        var dest = Path.Combine(CoversDir, (game.Md5 ?? Paths.SafeFileName(game.Title)) + ext);
        if (File.Exists(dest)) return dest;
        var bytes = await _http.GetByteArrayAsync(url, ct);
        await File.WriteAllBytesAsync(dest, bytes, ct);
        return dest;
    }

    private static RomHashes HashDiscOrSet(string path)
    {
        // Disc sheets and arcade sets: hash the sheet/zip itself (stable identity, cheap).
        using var f = File.OpenRead(path);
        return RomHasher.Compute(f);
    }

    private static string? ExtractSingleRom(string zipPath, out string? tempDir)
    {
        tempDir = null;
        using var zip = ZipFile.OpenRead(zipPath);
        var known = SystemCatalog.AllExtensions;
        var roms = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name) && known.Contains(Path.GetExtension(e.Name).TrimStart('.').ToLowerInvariant()) && Path.GetExtension(e.Name).ToLowerInvariant() != ".zip").ToList();
        if (roms.Count != 1) return null;
        tempDir = Path.Combine(Path.GetTempPath(), "openemu-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var dest = Path.Combine(tempDir, roms[0].Name);
        roms[0].ExtractToFile(dest, true);
        return dest;
    }

    private static void Cleanup(string? tempDir) { if (tempDir != null && Directory.Exists(tempDir)) { try { Directory.Delete(tempDir, true); } catch { } } }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!; var name = Path.GetFileNameWithoutExtension(path); var ext = Path.GetExtension(path);
        for (var i = 2; ; i++) { var p = Path.Combine(dir, $"{name} ({i}){ext}"); if (!File.Exists(p)) return p; }
    }

    /// <summary>"Super Mario World (USA) [!].sfc" → "Super Mario World".</summary>
    public static string CleanTitle(string name)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(name, @"\s*[\(\[][^\)\]]*[\)\]]", "");
        t = t.Replace('_', ' ').Trim();
        return t.Length == 0 ? name : t;
    }
}
