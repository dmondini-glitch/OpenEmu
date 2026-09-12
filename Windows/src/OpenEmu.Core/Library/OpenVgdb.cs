using System.IO.Compression;
using Microsoft.Data.Sqlite;
using OpenEmu.Core.Config;

namespace OpenEmu.Core.Library;

public sealed record VgdbRelease(string Title, string? CoverUrl, string? Description, string? Developer, string? Publisher, string? Genre, string? ReleaseDate, string? Region, string? SystemOeId);

/// <summary>OpenVGDB lookup (the same database OpenEmu uses for titles and cover art), matched by MD5/CRC.</summary>
public sealed class OpenVgdb : IDisposable
{
    public const string DownloadUrl = "https://github.com/OpenVGDB/OpenVGDB/releases/latest/download/openvgdb.zip";
    private SqliteConnection? _db;
    public string DatabasePath { get; }
    public bool IsAvailable => File.Exists(DatabasePath);

    public OpenVgdb(string? path = null) => DatabasePath = path ?? Paths.OpenVgdbFile;

    public static async Task DownloadAsync(string targetPath, HttpClient? http = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        http ??= new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var zip = targetPath + ".zip";
        using (var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zip);
            var buf = new byte[1 << 16]; long done = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0) { await dst.WriteAsync(buf.AsMemory(0, n), ct); done += n; if (total > 0) progress?.Report((double)done / total); }
        }
        using (var z = ZipFile.OpenRead(zip))
        {
            var entry = z.Entries.First(e => e.Name.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase));
            entry.ExtractToFile(targetPath + ".new", true);
        }
        File.Delete(zip);
        File.Move(targetPath + ".new", targetPath, true);
    }

    private SqliteConnection Db()
    {
        if (_db == null)
        {
            _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
            _db.Open();
        }
        return _db;
    }

    public VgdbRelease? Lookup(string? md5, string? crc32 = null, string? systemOeId = null)
    {
        if (!IsAvailable) return null;
        const string sql = """
            SELECT r.releaseTitleName, r.releaseCoverFront, r.releaseDescription, r.releaseDeveloper, r.releasePublisher, r.releaseGenre, r.releaseDate, rg.regionName, s.systemOEID
            FROM ROMs ro JOIN RELEASES r ON r.romID = ro.romID LEFT JOIN REGIONS rg ON rg.regionID = ro.regionID LEFT JOIN SYSTEMS s ON s.systemID = ro.systemID
            WHERE {0} ORDER BY r.releaseID LIMIT 1
            """;
        foreach (var (where, val) in new[] { ("ro.romHashMD5 = $v", md5?.ToUpperInvariant()), ("ro.romHashCRC = $v", crc32?.ToUpperInvariant()) })
        {
            if (string.IsNullOrEmpty(val)) continue;
            var w = where + (systemOeId != null ? " AND s.systemOEID = $s" : "");
            using var cmd = Db().CreateCommand();
            cmd.CommandText = string.Format(sql, w);
            cmd.Parameters.AddWithValue("$v", val);
            if (systemOeId != null) cmd.Parameters.AddWithValue("$s", systemOeId);
            using var r = cmd.ExecuteReader();
            if (r.Read())
                return new VgdbRelease(r.GetString(0), N(r, 1), N(r, 2), N(r, 3), N(r, 4), N(r, 5), N(r, 6), N(r, 7), N(r, 8));
        }
        return null;
    }

    private static string? N(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetValue(i)?.ToString();

    public void Dispose() => _db?.Dispose();
}
