using System.Xml.Linq;
using OpenEmu.Core.Config;

namespace OpenEmu.Core.Homebrew;

public sealed record HomebrewGame(string Name, string SystemId, string? Developer, string? Website, string FileUrl, string? Md5, string? Description, string? CoverUrl, IReadOnlyList<string> Screenshots, DateTime? Released, DateTime? Added);

/// <summary>The curated free homebrew feed maintained by the OpenEmu team (same feed the macOS app shows).</summary>
public sealed class HomebrewCatalog
{
    public const string FeedUrl = "https://raw.githubusercontent.com/OpenEmu/OpenEmu-Update/master/games.xml";
    private readonly HttpClient _http;
    public string CacheFile { get; }

    public HomebrewCatalog(HttpClient? http = null, string? cacheFile = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        CacheFile = cacheFile ?? Path.Combine(Paths.ConfigRoot, "homebrew.xml");
    }

    public async Task<IReadOnlyList<HomebrewGame>> LoadAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        string xml;
        var fresh = File.Exists(CacheFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(CacheFile) < TimeSpan.FromDays(1);
        if (!forceRefresh && fresh) xml = await File.ReadAllTextAsync(CacheFile, ct);
        else
        {
            try
            {
                xml = await _http.GetStringAsync(FeedUrl, ct);
                Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
                await File.WriteAllTextAsync(CacheFile, xml, ct);
            }
            catch when (File.Exists(CacheFile)) { xml = await File.ReadAllTextAsync(CacheFile, ct); }
        }
        return Parse(xml);
    }

    public static IReadOnlyList<HomebrewGame> Parse(string xml)
    {
        var doc = XDocument.Parse(xml);
        var list = new List<HomebrewGame>();
        foreach (var g in doc.Root!.Elements("game"))
        {
            string? A(string n) => g.Attribute(n)?.Value;
            var images = g.Element("images")?.Elements("image").ToList() ?? new();
            var cover = images.FirstOrDefault(i => i.Attribute("type")?.Value == "cover")?.Attribute("src")?.Value;
            var shots = images.Where(i => i.Attribute("type")?.Value != "cover").Select(i => i.Attribute("src")?.Value).Where(s => s != null).Cast<string>().ToList();
            DateTime? Ts(string? v) => long.TryParse(v, out var t) ? DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime : null;
            var file = A("file");
            if (A("name") == null || A("system") == null || file == null) continue;
            list.Add(new HomebrewGame(A("name")!, A("system")!, A("developer"), A("website"), file, A("md5"), g.Element("description")?.Value.Trim(), cover, shots, Ts(A("released")), Ts(A("added"))));
        }
        return list;
    }

    /// <summary>Downloads the ROM into the Homebrew folder and returns its local path.</summary>
    public async Task<string> DownloadAsync(HomebrewGame game, string? targetDir = null, CancellationToken ct = default)
    {
        targetDir ??= Paths.HomebrewDir;
        Directory.CreateDirectory(targetDir);
        var name = Uri.UnescapeDataString(Path.GetFileName(new Uri(game.FileUrl).AbsolutePath));
        var dest = Path.Combine(targetDir, name);
        if (!File.Exists(dest))
        {
            var bytes = await _http.GetByteArrayAsync(game.FileUrl, ct);
            await File.WriteAllBytesAsync(dest + ".part", bytes, ct);
            File.Move(dest + ".part", dest, true);
        }
        return dest;
    }
}
