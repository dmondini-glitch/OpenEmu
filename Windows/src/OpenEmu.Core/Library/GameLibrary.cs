using Microsoft.Data.Sqlite;
using OpenEmu.Core.Config;

namespace OpenEmu.Core.Library;

public sealed class Game
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string SystemId { get; set; } = "";
    public string RomPath { get; set; } = "";
    public string? Md5 { get; set; }
    public string? Crc32 { get; set; }
    public string? Sha1 { get; set; }
    public long Size { get; set; }
    public string? CoverPath { get; set; }
    public string? CoverUrl { get; set; }
    public int Rating { get; set; }
    public int PlayCount { get; set; }
    public long PlayTimeSeconds { get; set; }
    public DateTime? LastPlayed { get; set; }
    public DateTime AddedAt { get; set; }
    public string? CoreOverride { get; set; }
    public string? Description { get; set; }
    public string? Developer { get; set; }
    public string? Publisher { get; set; }
    public string? Genre { get; set; }
    public string? ReleaseDate { get; set; }
    public string? Region { get; set; }
    public bool Missing { get; set; }
    public string? Notes { get; set; }
    public string GameKey => string.IsNullOrEmpty(Md5) ? Paths.SafeFileName(Path.GetFileNameWithoutExtension(RomPath)) : Md5!;
}

public sealed class Collection
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>"manual" or a smart kind: "all", "recent", "favorites", "unplayed".</summary>
    public string Kind { get; set; } = "manual";
    public int SortOrder { get; set; }
}

/// <summary>SQLite-backed game library (games, collections, cheats, play stats).</summary>
public sealed class GameLibrary : IDisposable
{
    private readonly SqliteConnection _db;
    public string DatabasePath { get; }
    public event Action? Changed;

    public GameLibrary(string? databasePath = null)
    {
        DatabasePath = databasePath ?? Paths.DatabaseFile;
        if (DatabasePath != ":memory:") Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;");
        Migrate();
    }

    private void Migrate()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS games (
              id INTEGER PRIMARY KEY, title TEXT NOT NULL, system_id TEXT NOT NULL, rom_path TEXT NOT NULL UNIQUE,
              md5 TEXT, crc32 TEXT, sha1 TEXT, size INTEGER NOT NULL DEFAULT 0, cover_path TEXT, cover_url TEXT,
              rating INTEGER NOT NULL DEFAULT 0, play_count INTEGER NOT NULL DEFAULT 0, play_time INTEGER NOT NULL DEFAULT 0,
              last_played TEXT, added_at TEXT NOT NULL, core_override TEXT, description TEXT, developer TEXT, publisher TEXT,
              genre TEXT, release_date TEXT, region TEXT, missing INTEGER NOT NULL DEFAULT 0, notes TEXT);
            CREATE INDEX IF NOT EXISTS ix_games_system ON games(system_id);
            CREATE INDEX IF NOT EXISTS ix_games_md5 ON games(md5);
            CREATE TABLE IF NOT EXISTS collections (id INTEGER PRIMARY KEY, name TEXT NOT NULL, kind TEXT NOT NULL DEFAULT 'manual', sort_order INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS collection_games (collection_id INTEGER NOT NULL REFERENCES collections(id) ON DELETE CASCADE, game_id INTEGER NOT NULL REFERENCES games(id) ON DELETE CASCADE, PRIMARY KEY(collection_id, game_id));
            CREATE TABLE IF NOT EXISTS cheats (id INTEGER PRIMARY KEY, game_id INTEGER NOT NULL REFERENCES games(id) ON DELETE CASCADE, description TEXT NOT NULL, code TEXT NOT NULL, enabled INTEGER NOT NULL DEFAULT 0, type TEXT);
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);
            """);
        Exec("INSERT OR IGNORE INTO meta(key,value) VALUES('schema','1')");
    }

    private void Exec(string sql, params (string, object?)[] args)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private SqliteCommand Cmd(string sql, params (string, object?)[] args)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return cmd;
    }

    // ------------------------------------------------------------------ games
    private static Game Read(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0), Title = r.GetString(1), SystemId = r.GetString(2), RomPath = r.GetString(3),
        Md5 = r.IsDBNull(4) ? null : r.GetString(4), Crc32 = r.IsDBNull(5) ? null : r.GetString(5), Sha1 = r.IsDBNull(6) ? null : r.GetString(6),
        Size = r.GetInt64(7), CoverPath = r.IsDBNull(8) ? null : r.GetString(8), CoverUrl = r.IsDBNull(9) ? null : r.GetString(9),
        Rating = r.GetInt32(10), PlayCount = r.GetInt32(11), PlayTimeSeconds = r.GetInt64(12),
        LastPlayed = r.IsDBNull(13) ? null : DateTime.Parse(r.GetString(13), null, System.Globalization.DateTimeStyles.RoundtripKind),
        AddedAt = DateTime.Parse(r.GetString(14), null, System.Globalization.DateTimeStyles.RoundtripKind),
        CoreOverride = r.IsDBNull(15) ? null : r.GetString(15), Description = r.IsDBNull(16) ? null : r.GetString(16),
        Developer = r.IsDBNull(17) ? null : r.GetString(17), Publisher = r.IsDBNull(18) ? null : r.GetString(18), Genre = r.IsDBNull(19) ? null : r.GetString(19),
        ReleaseDate = r.IsDBNull(20) ? null : r.GetString(20), Region = r.IsDBNull(21) ? null : r.GetString(21), Missing = r.GetInt32(22) != 0, Notes = r.IsDBNull(23) ? null : r.GetString(23),
    };

    private const string Cols = "id,title,system_id,rom_path,md5,crc32,sha1,size,cover_path,cover_url,rating,play_count,play_time,last_played,added_at,core_override,description,developer,publisher,genre,release_date,region,missing,notes";

    public Game Add(Game g)
    {
        if (g.AddedAt == default) g.AddedAt = DateTime.UtcNow;
        using var cmd = Cmd($"INSERT INTO games(title,system_id,rom_path,md5,crc32,sha1,size,cover_path,cover_url,rating,play_count,play_time,last_played,added_at,core_override,description,developer,publisher,genre,release_date,region,missing,notes) VALUES($t,$s,$p,$m,$c,$h,$z,$cp,$cu,$r,$pc,$pt,$lp,$a,$co,$d,$dev,$pub,$g,$rd,$rg,$mi,$n); SELECT last_insert_rowid();",
            ("$t", g.Title), ("$s", g.SystemId), ("$p", g.RomPath), ("$m", g.Md5), ("$c", g.Crc32), ("$h", g.Sha1), ("$z", g.Size), ("$cp", g.CoverPath), ("$cu", g.CoverUrl),
            ("$r", g.Rating), ("$pc", g.PlayCount), ("$pt", g.PlayTimeSeconds), ("$lp", g.LastPlayed?.ToString("O")), ("$a", g.AddedAt.ToString("O")), ("$co", g.CoreOverride),
            ("$d", g.Description), ("$dev", g.Developer), ("$pub", g.Publisher), ("$g", g.Genre), ("$rd", g.ReleaseDate), ("$rg", g.Region), ("$mi", g.Missing ? 1 : 0), ("$n", g.Notes));
        g.Id = (long)cmd.ExecuteScalar()!;
        Changed?.Invoke();
        return g;
    }

    public void Update(Game g)
    {
        Exec("UPDATE games SET title=$t,system_id=$s,rom_path=$p,md5=$m,crc32=$c,sha1=$h,size=$z,cover_path=$cp,cover_url=$cu,rating=$r,play_count=$pc,play_time=$pt,last_played=$lp,core_override=$co,description=$d,developer=$dev,publisher=$pub,genre=$g,release_date=$rd,region=$rg,missing=$mi,notes=$n WHERE id=$id",
            ("$t", g.Title), ("$s", g.SystemId), ("$p", g.RomPath), ("$m", g.Md5), ("$c", g.Crc32), ("$h", g.Sha1), ("$z", g.Size), ("$cp", g.CoverPath), ("$cu", g.CoverUrl),
            ("$r", g.Rating), ("$pc", g.PlayCount), ("$pt", g.PlayTimeSeconds), ("$lp", g.LastPlayed?.ToString("O")), ("$co", g.CoreOverride),
            ("$d", g.Description), ("$dev", g.Developer), ("$pub", g.Publisher), ("$g", g.Genre), ("$rd", g.ReleaseDate), ("$rg", g.Region), ("$mi", g.Missing ? 1 : 0), ("$n", g.Notes), ("$id", g.Id));
        Changed?.Invoke();
    }

    public void Remove(long id) { Exec("DELETE FROM games WHERE id=$id", ("$id", id)); Changed?.Invoke(); }

    public Game? Get(long id) { using var cmd = Cmd($"SELECT {Cols} FROM games WHERE id=$id", ("$id", id)); using var r = cmd.ExecuteReader(); return r.Read() ? Read(r) : null; }
    public Game? FindByPath(string path) { using var cmd = Cmd($"SELECT {Cols} FROM games WHERE rom_path=$p", ("$p", path)); using var r = cmd.ExecuteReader(); return r.Read() ? Read(r) : null; }
    public Game? FindByMd5(string md5, string? systemId = null)
    {
        using var cmd = systemId == null ? Cmd($"SELECT {Cols} FROM games WHERE md5=$m", ("$m", md5)) : Cmd($"SELECT {Cols} FROM games WHERE md5=$m AND system_id=$s", ("$m", md5), ("$s", systemId));
        using var r = cmd.ExecuteReader(); return r.Read() ? Read(r) : null;
    }

    public List<Game> All(string? systemId = null, string? search = null)
    {
        var sql = $"SELECT {Cols} FROM games WHERE 1=1";
        var args = new List<(string, object?)>();
        if (systemId != null) { sql += " AND system_id=$s"; args.Add(("$s", systemId)); }
        if (!string.IsNullOrWhiteSpace(search)) { sql += " AND title LIKE $q"; args.Add(("$q", "%" + search.Trim() + "%")); }
        sql += " ORDER BY title COLLATE NOCASE";
        using var cmd = Cmd(sql, args.ToArray());
        using var r = cmd.ExecuteReader();
        var list = new List<Game>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    public List<Game> Recent(int limit = 50)
    {
        using var cmd = Cmd($"SELECT {Cols} FROM games ORDER BY added_at DESC LIMIT $l", ("$l", limit));
        using var r = cmd.ExecuteReader(); var list = new List<Game>(); while (r.Read()) list.Add(Read(r)); return list;
    }

    public List<Game> RecentlyPlayed(int limit = 50)
    {
        using var cmd = Cmd($"SELECT {Cols} FROM games WHERE last_played IS NOT NULL ORDER BY last_played DESC LIMIT $l", ("$l", limit));
        using var r = cmd.ExecuteReader(); var list = new List<Game>(); while (r.Read()) list.Add(Read(r)); return list;
    }

    public Dictionary<string, int> CountBySystem()
    {
        using var cmd = Cmd("SELECT system_id, COUNT(*) FROM games GROUP BY system_id");
        using var r = cmd.ExecuteReader(); var d = new Dictionary<string, int>(); while (r.Read()) d[r.GetString(0)] = r.GetInt32(1); return d;
    }

    public int Count() { using var cmd = Cmd("SELECT COUNT(*) FROM games"); return Convert.ToInt32(cmd.ExecuteScalar()); }

    public void RecordPlay(long id, TimeSpan duration)
    {
        Exec("UPDATE games SET play_count=play_count+1, play_time=play_time+$d, last_played=$lp WHERE id=$id", ("$d", (long)duration.TotalSeconds), ("$lp", DateTime.UtcNow.ToString("O")), ("$id", id));
        Changed?.Invoke();
    }

    /// <summary>Marks games whose ROM file no longer exists.</summary>
    public int VerifyFiles()
    {
        var n = 0;
        foreach (var g in All())
        {
            var missing = !File.Exists(g.RomPath);
            if (missing != g.Missing) { Exec("UPDATE games SET missing=$m WHERE id=$id", ("$m", missing ? 1 : 0), ("$id", g.Id)); n++; }
        }
        if (n > 0) Changed?.Invoke();
        return n;
    }

    // ------------------------------------------------------------------ collections
    public List<Collection> Collections()
    {
        using var cmd = Cmd("SELECT id,name,kind,sort_order FROM collections ORDER BY sort_order, name COLLATE NOCASE");
        using var r = cmd.ExecuteReader(); var list = new List<Collection>();
        while (r.Read()) list.Add(new Collection { Id = r.GetInt64(0), Name = r.GetString(1), Kind = r.GetString(2), SortOrder = r.GetInt32(3) });
        return list;
    }

    public Collection AddCollection(string name, string kind = "manual")
    {
        using var cmd = Cmd("INSERT INTO collections(name,kind) VALUES($n,$k); SELECT last_insert_rowid();", ("$n", name), ("$k", kind));
        var c = new Collection { Id = (long)cmd.ExecuteScalar()!, Name = name, Kind = kind };
        Changed?.Invoke(); return c;
    }

    public void RenameCollection(long id, string name) { Exec("UPDATE collections SET name=$n WHERE id=$id", ("$n", name), ("$id", id)); Changed?.Invoke(); }
    public void RemoveCollection(long id) { Exec("DELETE FROM collections WHERE id=$id", ("$id", id)); Changed?.Invoke(); }
    public void AddToCollection(long collectionId, long gameId) { Exec("INSERT OR IGNORE INTO collection_games VALUES($c,$g)", ("$c", collectionId), ("$g", gameId)); Changed?.Invoke(); }
    public void RemoveFromCollection(long collectionId, long gameId) { Exec("DELETE FROM collection_games WHERE collection_id=$c AND game_id=$g", ("$c", collectionId), ("$g", gameId)); Changed?.Invoke(); }

    public List<Game> GamesInCollection(long collectionId)
    {
        using var cmd = Cmd($"SELECT {string.Join(",", Cols.Split(',').Select(c => "g." + c))} FROM games g JOIN collection_games cg ON cg.game_id=g.id WHERE cg.collection_id=$c ORDER BY g.title COLLATE NOCASE", ("$c", collectionId));
        using var r = cmd.ExecuteReader(); var list = new List<Game>(); while (r.Read()) list.Add(Read(r)); return list;
    }

    // ------------------------------------------------------------------ cheats
    public List<Cheats.Cheat> CheatsFor(long gameId)
    {
        using var cmd = Cmd("SELECT id,game_id,description,code,enabled,type FROM cheats WHERE game_id=$g ORDER BY id", ("$g", gameId));
        using var r = cmd.ExecuteReader(); var list = new List<Cheats.Cheat>();
        while (r.Read()) list.Add(new Cheats.Cheat { Id = r.GetInt64(0), GameId = r.GetInt64(1), Description = r.GetString(2), Code = r.GetString(3), Enabled = r.GetInt32(4) != 0, Type = r.IsDBNull(5) ? null : r.GetString(5) });
        return list;
    }

    public Cheats.Cheat AddCheat(Cheats.Cheat c)
    {
        using var cmd = Cmd("INSERT INTO cheats(game_id,description,code,enabled,type) VALUES($g,$d,$c,$e,$t); SELECT last_insert_rowid();", ("$g", c.GameId), ("$d", c.Description), ("$c", c.Code), ("$e", c.Enabled ? 1 : 0), ("$t", c.Type));
        c.Id = (long)cmd.ExecuteScalar()!; return c;
    }

    public void UpdateCheat(Cheats.Cheat c) => Exec("UPDATE cheats SET description=$d,code=$c,enabled=$e,type=$t WHERE id=$id", ("$d", c.Description), ("$c", c.Code), ("$e", c.Enabled ? 1 : 0), ("$t", c.Type), ("$id", c.Id));
    public void RemoveCheat(long id) => Exec("DELETE FROM cheats WHERE id=$id", ("$id", id));

    public void Dispose() => _db.Dispose();
}
