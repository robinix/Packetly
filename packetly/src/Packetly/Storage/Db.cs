using System.Net;
using Microsoft.Data.Sqlite;
using Packetly.Core;

namespace Packetly.Storage;

/// <summary>
/// SQLite store in WAL mode. The connection is shared across the writer,
/// aggregator, resolver and API threads — every public operation is
/// serialized through a single gate (the bundled engine is fast enough that
/// this never becomes the bottleneck at personal-workload scale).
/// </summary>
internal sealed class Db : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();

    public Db(string path)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _conn = new SqliteConnection($"Data Source={full}");
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL");
        Exec("PRAGMA synchronous=NORMAL");
        Exec("PRAGMA busy_timeout=5000");
        Exec("PRAGMA page_size=4096");
        InitSchema();
    }

    /// <summary>Raised when FindOrInsertApp inserts a brand-new app (id, path).</summary>
    public event Action<long, string>? AppCreated;

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void InitSchema()
    {
        lock (_gate) Exec("""
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);

            CREATE TABLE IF NOT EXISTS apps(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                path TEXT UNIQUE NOT NULL,
                name TEXT,
                publisher TEXT,
                icon BLOB,
                first_seen INTEGER NOT NULL);

            CREATE TABLE IF NOT EXISTS hosts(
                ip BLOB PRIMARY KEY,
                hostname TEXT,
                region TEXT,
                first_seen INTEGER NOT NULL,
                last_seen INTEGER NOT NULL);

            CREATE TABLE IF NOT EXISTS traffic_1s(ts INTEGER NOT NULL, app_id INTEGER NOT NULL, host BLOB NOT NULL, port INTEGER NOT NULL, proto INTEGER NOT NULL, inb INTEGER NOT NULL, outb INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS traffic_60s(ts INTEGER NOT NULL, app_id INTEGER NOT NULL, host BLOB NOT NULL, port INTEGER NOT NULL, proto INTEGER NOT NULL, inb INTEGER NOT NULL, outb INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS traffic_3600s(ts INTEGER NOT NULL, app_id INTEGER NOT NULL, host BLOB NOT NULL, port INTEGER NOT NULL, proto INTEGER NOT NULL, inb INTEGER NOT NULL, outb INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS traffic_86400s(ts INTEGER NOT NULL, app_id INTEGER NOT NULL, host BLOB NOT NULL, port INTEGER NOT NULL, proto INTEGER NOT NULL, inb INTEGER NOT NULL, outb INTEGER NOT NULL);

            CREATE INDEX IF NOT EXISTS idx_1s_ts ON traffic_1s(ts);
            CREATE INDEX IF NOT EXISTS idx_1s_app ON traffic_1s(app_id);
            CREATE INDEX IF NOT EXISTS idx_60s_ts ON traffic_60s(ts);
            CREATE INDEX IF NOT EXISTS idx_60s_app ON traffic_60s(app_id);
            CREATE INDEX IF NOT EXISTS idx_3600s_ts ON traffic_3600s(ts);
            CREATE INDEX IF NOT EXISTS idx_3600s_app ON traffic_3600s(app_id);
            CREATE INDEX IF NOT EXISTS idx_86400s_ts ON traffic_86400s(ts);
            CREATE INDEX IF NOT EXISTS idx_86400s_app ON traffic_86400s(app_id);

            -- rollups are keyed writes: re-running a window replaces, never duplicates
            CREATE UNIQUE INDEX IF NOT EXISTS uq_60s ON traffic_60s(ts, app_id, host, port, proto);
            CREATE UNIQUE INDEX IF NOT EXISTS uq_3600s ON traffic_3600s(ts, app_id, host, port, proto);
            CREATE UNIQUE INDEX IF NOT EXISTS uq_86400s ON traffic_86400s(ts, app_id, host, port, proto);

            CREATE TABLE IF NOT EXISTS quotas(app_id INTEGER, period TEXT NOT NULL, limit_bytes INTEGER NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS uq_quotas ON quotas(COALESCE(app_id, -1), period);

            CREATE TABLE IF NOT EXISTS events(id INTEGER PRIMARY KEY AUTOINCREMENT, ts INTEGER NOT NULL, type INTEGER NOT NULL, app_id INTEGER, data TEXT);
            """);
        MigrateEventsTable();
        MigrateAppsMetadataColumns();
    }

    /// <summary>Add version / file_size / file_mtime columns to apps for the
    /// Application Info Monitor (v1.8.0+) without breaking existing DBs.</summary>
    private void MigrateAppsMetadataColumns()
    {
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(apps)";
            using var r = cmd.ExecuteReader();
            while (r.Read()) cols.Add(r.GetString(1));
        }
        if (!cols.Contains("version")) Exec("ALTER TABLE apps ADD COLUMN version TEXT");
        if (!cols.Contains("file_size")) Exec("ALTER TABLE apps ADD COLUMN file_size INTEGER");
        if (!cols.Contains("file_mtime")) Exec("ALTER TABLE apps ADD COLUMN file_mtime INTEGER");
    }

    /// <summary>Databases created before phase 4 have an events table without
    /// the id primary key; alert history is disposable, so recreate it.</summary>
    private void MigrateEventsTable()
    {
        bool hasId = false;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(events)";
            using var r = cmd.ExecuteReader();
            while (r.Read()) if (r.GetString(1) == "id") hasId = true;
        }
        if (hasId) return;
        Exec("DROP TABLE events");
        Exec("CREATE TABLE events(id INTEGER PRIMARY KEY AUTOINCREMENT, ts INTEGER NOT NULL, type INTEGER NOT NULL, app_id INTEGER, data TEXT)");
    }

    // ---- meta / watermarks ----

    private long ReadMetaLongUnlocked(string key, long fallback)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        var v = cmd.ExecuteScalar() as string;
        return v != null && long.TryParse(v, out var n) ? n : fallback;
    }

    public long GetMetaLong(string key, long fallback)
    {
        lock (_gate) return ReadMetaLongUnlocked(key, fallback);
    }

    /// <summary>
    /// Builds a read-only subquery over the requested rollup tier PLUS any
    /// unrolled live tail from finer tiers beyond the aggregator watermark.
    /// Guarantees zero 1–90s (or hourly/daily) blind spot between traffic_1s
    /// and traffic_60s / traffic_3600s / traffic_86400s across graphs, totals,
    /// and selection popups. Caller must hold _gate and bind $f (and $t when
    /// hasUpperBound is true).
    /// </summary>
    private string TrafficSourceSql(string table, bool hasUpperBound)
    {
        var upper = hasUpperBound ? " AND ts < $t" : "";
        if (table == "traffic_1s")
            return $"(SELECT ts, app_id, host, port, proto, inb, outb FROM traffic_1s WHERE ts >= $f{upper})";

        long wm60 = ReadMetaLongUnlocked("rollup_60s_wm", 0);
        if (table == "traffic_60s")
        {
            return $"""
                (SELECT ts, app_id, host, port, proto, inb, outb FROM traffic_60s WHERE ts >= $f AND ts < {wm60}{upper}
                 UNION ALL
                 SELECT (ts / 60) * 60 AS ts, app_id, host, port, proto, inb, outb FROM traffic_1s WHERE ts >= $f AND ts >= {wm60}{upper})
                """;
        }

        long wm3600 = ReadMetaLongUnlocked("rollup_3600s_wm", 0);
        if (table == "traffic_3600s")
        {
            long w60 = Math.Max(wm3600, wm60);
            return $"""
                (SELECT ts, app_id, host, port, proto, inb, outb FROM traffic_3600s WHERE ts >= $f AND ts < {wm3600}{upper}
                 UNION ALL
                 SELECT (ts / 3600) * 3600 AS ts, app_id, host, port, proto, inb, outb FROM traffic_60s WHERE ts >= $f AND ts >= {wm3600} AND ts < {w60}{upper}
                 UNION ALL
                 SELECT (ts / 3600) * 3600 AS ts, app_id, host, port, proto, inb, outb FROM traffic_1s WHERE ts >= $f AND ts >= {w60}{upper})
                """;
        }

        if (table == "traffic_86400s")
        {
            long wm86400 = ReadMetaLongUnlocked("rollup_86400s_wm", 0);
            long w3600 = Math.Max(wm86400, wm3600);
            long w60 = Math.Max(w3600, wm60);
            return $"""
                (SELECT ts, app_id, host, port, proto, inb, outb FROM traffic_86400s WHERE ts >= $f AND ts < {wm86400}{upper}
                 UNION ALL
                 SELECT (ts / 86400) * 86400 AS ts, app_id, host, port, proto, inb, outb FROM traffic_3600s WHERE ts >= $f AND ts >= {wm86400} AND ts < {w3600}{upper}
                 UNION ALL
                 SELECT (ts / 86400) * 86400 AS ts, app_id, host, port, proto, inb, outb FROM traffic_60s WHERE ts >= $f AND ts >= {w3600} AND ts < {w60}{upper}
                 UNION ALL
                 SELECT (ts / 86400) * 86400 AS ts, app_id, host, port, proto, inb, outb FROM traffic_1s WHERE ts >= $f AND ts >= {w60}{upper})
                """;
        }

        throw new ArgumentException($"invalid traffic table '{table}'", nameof(table));
    }

    public void SetMetaLong(string key, long value)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO meta(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value.ToString());
            cmd.ExecuteNonQuery();
        }
    }

    // ---- apps ----

    public int FindOrInsertApp(string path, string name, long now)
    {
        lock (_gate)
        {
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id FROM apps WHERE path = $p";
                cmd.Parameters.AddWithValue("$p", path);
                var found = cmd.ExecuteScalar();
                if (found is not null) return Convert.ToInt32(found);
            }

            long id;
            using (var tx = _conn.BeginTransaction())
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO apps (path, name, first_seen) VALUES ($p, $n, $t); SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$p", path);
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$t", now);
                id = (long)(cmd.ExecuteScalar() ?? 0L);
                tx.Commit();
            }
            AppCreated?.Invoke(id, path);
            return (int)id;
        }
    }

    public string? GetAppName(int id)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM apps WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetAppIcon(int appId, byte[] png)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE apps SET icon = $i WHERE id = $id";
            cmd.Parameters.AddWithValue("$i", png);
            cmd.Parameters.AddWithValue("$id", appId);
            cmd.ExecuteNonQuery();
        }
    }

    public byte[]? GetAppIcon(int appId)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT icon FROM apps WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", appId);
            var v = cmd.ExecuteScalar();
            return v is byte[] b && b.Length > 0 ? b : null;
        }
    }

    /// <summary>Apps whose icon has not been extracted yet and whose path points at an exe.</summary>
    public List<(int Id, string Path)> AppsWithoutIcon(int limit)
    {
        var list = new List<(int, string)>();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id, path FROM apps WHERE icon IS NULL AND path LIKE '%.exe' ORDER BY id DESC LIMIT $l";
            cmd.Parameters.AddWithValue("$l", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetInt32(0), r.GetString(1)));
        }
        return list;
    }

    /// <summary>All exe-backed apps for one-time icon quality migration.</summary>
    public List<(int Id, string Path)> AllExeApps(int limit)
    {
        var list = new List<(int, string)>();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id, path FROM apps WHERE path LIKE '%.exe' ORDER BY id DESC LIMIT $l";
            cmd.Parameters.AddWithValue("$l", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetInt32(0), r.GetString(1)));
        }
        return list;
    }

    public (string? Name, string? Path, string? Publisher)? GetAppById(int id)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT name, path, publisher FROM apps WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
        }
    }

    /// <summary>Exe-backed apps with persisted version/file metadata for change monitoring.</summary>
    public List<(int Id, string Path, string? Name, string? Publisher, string? Version, long? FileSize, long? FileMtime)> GetMonitoredApps(int limit)
    {
        var list = new List<(int, string, string?, string?, string?, long?, long?)>();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, path, name, publisher, version, file_size, file_mtime
                FROM apps
                WHERE path LIKE '%.exe'
                ORDER BY id DESC LIMIT $l
                """;
            cmd.Parameters.AddWithValue("$l", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add((
                    r.GetInt32(0),
                    r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetInt64(5),
                    r.IsDBNull(6) ? null : r.GetInt64(6)
                ));
            }
        }
        return list;
    }

    public void UpdateAppMetadata(long appId, string? version, string? publisher, long? fileSize, long? fileMtime)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE apps
                SET version = COALESCE($ver, version),
                    publisher = COALESCE($pub, publisher),
                    file_size = COALESCE($sz, file_size),
                    file_mtime = COALESCE($mt, file_mtime)
                WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$ver", (object?)version ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pub", (object?)publisher ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sz", (object?)fileSize ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$mt", (object?)fileMtime ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", appId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Per-host traffic for one app in the window (top by total).</summary>
    public List<(string Ip, string? Hostname, string? Region, long In, long Out)> AppHostsBetween(string table, int appId, long from, long to, int limit)
    {
        var list = new List<(string, string?, string?, long, long)>();
        lock (_gate)
        {
            var src = TrafficSourceSql(table, true);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT hex(t.host) AS iphex, h.hostname, h.region, SUM(t.inb), SUM(t.outb)
                FROM {src} t LEFT JOIN hosts h ON h.ip = t.host
                WHERE t.app_id = $id
                GROUP BY t.host ORDER BY SUM(t.inb + t.outb) DESC, t.host ASC LIMIT $l
                """;
            cmd.Parameters.AddWithValue("$f", from);
            cmd.Parameters.AddWithValue("$t", to);
            cmd.Parameters.AddWithValue("$id", appId);
            cmd.Parameters.AddWithValue("$l", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt64(3), r.GetInt64(4)));
        }
        return list;
    }

    public (long In, long Out) AppBytesBetween(string table, int appId, long from, long to)
    {
        lock (_gate)
        {
            var src = TrafficSourceSql(table, true);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT SUM(inb), SUM(outb) FROM {src} WHERE app_id = $id";
            cmd.Parameters.AddWithValue("$f", from);
            cmd.Parameters.AddWithValue("$t", to);
            cmd.Parameters.AddWithValue("$id", appId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return (0, 0);
            return (r.IsDBNull(0) ? 0 : r.GetInt64(0), r.IsDBNull(1) ? 0 : r.GetInt64(1));
        }
    }

    public (long In, long Out) TotalBytesBetween(string table, long from, long to)
    {
        lock (_gate)
        {
            var src = TrafficSourceSql(table, true);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT SUM(inb), SUM(outb) FROM {src}";
            cmd.Parameters.AddWithValue("$f", from);
            cmd.Parameters.AddWithValue("$t", to);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return (0, 0);
            return (r.IsDBNull(0) ? 0 : r.GetInt64(0), r.IsDBNull(1) ? 0 : r.GetInt64(1));
        }
    }

    public int AppAlertCount(int appId)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM events WHERE app_id = $id";
            cmd.Parameters.AddWithValue("$id", appId);
            using var r = cmd.ExecuteReader();
            return r.Read() ? r.GetInt32(0) : 0;
        }
    }

    /// <summary>Per-app totals + distinct host counts in the window (for the detail table).</summary>
    public List<(int Id, string Name, int HostCount, long In, long Out)> AppsHostsBetween(string table, long from, long to, int limit)
    {
        var list = new List<(int, string, int, long, long)>();
        lock (_gate)
        {
            var src = TrafficSourceSql(table, true);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT a.id, COALESCE(a.name, a.path), COUNT(DISTINCT t.host), SUM(t.inb), SUM(t.outb)
                FROM {src} t JOIN apps a ON a.id = t.app_id
                GROUP BY a.id ORDER BY SUM(t.inb + t.outb) DESC, a.id ASC LIMIT $l
                """;
            cmd.Parameters.AddWithValue("$f", from);
            cmd.Parameters.AddWithValue("$t", to);
            cmd.Parameters.AddWithValue("$l", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt64(3), r.GetInt64(4)));
        }
        return list;
    }

    public Dictionary<int, string> AppNames()
    {
        var map = new Dictionary<int, string>();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id, COALESCE(name, path) FROM apps";
            using var r = cmd.ExecuteReader();
            while (r.Read()) map[r.GetInt32(0)] = r.GetString(1);
        }
        return map;
    }

    // ---- hosts ----

    public void ClaimHost(byte[] ip, long now)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO hosts (ip, first_seen, last_seen) VALUES ($ip, $t, $t) ON CONFLICT(ip) DO UPDATE SET last_seen = $t";
            cmd.Parameters.AddWithValue("$ip", ip);
            cmd.Parameters.AddWithValue("$t", now);
            cmd.ExecuteNonQuery();
        }
    }

    public string? GetHostname(byte[] ip)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT hostname FROM hosts WHERE ip = $ip";
            cmd.Parameters.AddWithValue("$ip", ip);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetHostname(byte[] ip, string? hostname, long now)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE hosts SET hostname = COALESCE($h, hostname), last_seen = $t WHERE ip = $ip";
            cmd.Parameters.AddWithValue("$ip", ip);
            cmd.Parameters.AddWithValue("$h", (object?)hostname ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$t", now);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>IPs that have no hostname yet and were seen within the last 7 days.</summary>
    public List<byte[]> HostsNeedingResolve(long now, int limit)
    {
        var list = new List<byte[]>();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT ip FROM hosts WHERE hostname IS NULL AND last_seen >= $stale ORDER BY last_seen DESC LIMIT $l";
            cmd.Parameters.AddWithValue("$stale", now - 7L * 86400);
            cmd.Parameters.AddWithValue("$l", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((byte[])r.GetValue(0));
        }
        return list;
    }

    // ---- writes ----

    public int InsertBuckets(string table, long ts, IEnumerable<(int AppId, byte[] Host, int Port, int Proto, long In, long Out)> rows)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"INSERT INTO {table} (ts, app_id, host, port, proto, inb, outb) VALUES ($ts, $app, $host, $port, $proto, $in, $out)";
                AddRowParams(cmd);
                int n = 0;
                foreach (var r in rows)
                {
                    SetRowParams(cmd, ts, r.AppId, r.Host, r.Port, r.Proto, r.In, r.Out);
                    n += cmd.ExecuteNonQuery();
                }
                tx.Commit();
                return n;
            }
        }
    }

    private static void AddRowParams(SqliteCommand cmd)
    {
        foreach (var name in new[] { "$ts", "$app", "$host", "$port", "$proto", "$in", "$out" })
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            if (name == "$host") p.SqliteType = SqliteType.Blob;
            cmd.Parameters.Add(p);
        }
    }

    private static void SetRowParams(SqliteCommand cmd, long ts, int appId, byte[] host, int port, int proto, long inb, long outb)
    {
        cmd.Parameters["$ts"].Value = ts;
        cmd.Parameters["$app"].Value = appId;
        cmd.Parameters["$host"].Value = host;
        cmd.Parameters["$port"].Value = port;
        cmd.Parameters["$proto"].Value = proto;
        cmd.Parameters["$in"].Value = inb;
        cmd.Parameters["$out"].Value = outb;
    }

    /// <summary>Aggregates src into dst over [from, to) in fixed buckets. Idempotent via the dst unique index.</summary>
    public int Rollup(string src, string dst, long from, long to, long bucketSeconds)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"""
                INSERT OR REPLACE INTO {dst} (ts, app_id, host, port, proto, inb, outb)
                SELECT (ts / {bucketSeconds}) * {bucketSeconds}, app_id, host, port, proto, SUM(inb), SUM(outb)
                FROM {src} WHERE ts >= $f AND ts < $t
                GROUP BY 1, 2, 3, 4, 5
                """;
            cmd.Parameters.AddWithValue("$f", from);
            cmd.Parameters.AddWithValue("$t", to);
            return cmd.ExecuteNonQuery();
        }
    }

    public int Prune(string table, long beforeTs)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM {table} WHERE ts < $t";
            cmd.Parameters.AddWithValue("$t", beforeTs);
            return cmd.ExecuteNonQuery();
        }
    }

    // ---- queries ----

    public List<(long Ts, long In, long Out)> SeriesTotals(string table, long from, long to, int? appId = null)
    {
        var list = new List<(long, long, long)>();
        lock (_gate)
        {
            var src = TrafficSourceSql(table, true);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT ts, SUM(inb), SUM(outb) FROM {src}" + (appId != null ? " WHERE app_id = $app" : "") + " GROUP BY ts ORDER BY ts";
            cmd.Parameters.AddWithValue("$f", from);
            cmd.Parameters.AddWithValue("$t", to);
            if (appId != null) cmd.Parameters.AddWithValue("$app", appId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetInt64(0), r.GetInt64(1), r.GetInt64(2)));
        }
        return list;
    }

    public List<(long Ts, int AppId, long In, long Out)> SeriesByApp(string table, long from, long to, int? appId = null)
    {
        var list = new List<(long, int, long, long)>();
        lock (_gate)
        {
            var src = TrafficSourceSql(table, true);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT ts, app_id, SUM(inb), SUM(outb) FROM {src}" + (appId != null ? " WHERE app_id = $app" : "") + " GROUP BY ts, app_id ORDER BY ts";
            cmd.Parameters.AddWithValue("$f", from);
            cmd.Parameters.AddWithValue("$t", to);
            if (appId != null) cmd.Parameters.AddWithValue("$app", appId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetInt64(0), r.GetInt32(1), r.GetInt64(2), r.GetInt64(3)));
        }
        return list;
    }

    public List<(string Name, long In, long Out)> TopApps(string table, long from, long to, int limit)
    {
        var list = new List<(string, long, long)>();
        lock (_gate)
        {
            var src = TrafficSourceSql(table, true);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT COALESCE(a.name, a.path), SUM(t.inb), SUM(t.outb)
                FROM {src} t JOIN apps a ON a.id = t.app_id
                GROUP BY t.app_id ORDER BY SUM(t.inb + t.outb) DESC, t.app_id ASC LIMIT $l
                """;
            cmd.Parameters.AddWithValue("$f", from);
            cmd.Parameters.AddWithValue("$t", to);
            cmd.Parameters.AddWithValue("$l", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetString(0), r.GetInt64(1), r.GetInt64(2)));
        }
        return list;
    }

    public List<(int Id, string Name, string Path, long In, long Out)> AppsInWindow(string table, long from, long to, int limit)
    {
        var list = new List<(int, string, string, long, long)>();
        lock (_gate)
        {
            var src = TrafficSourceSql(table, true);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT a.id, COALESCE(a.name, a.path), a.path, SUM(t.inb), SUM(t.outb)
                FROM {src} t JOIN apps a ON a.id = t.app_id
                GROUP BY a.id ORDER BY SUM(t.inb + t.outb) DESC, a.id ASC LIMIT $l
                """;
            cmd.Parameters.AddWithValue("$f", from);
            cmd.Parameters.AddWithValue("$t", to);
            cmd.Parameters.AddWithValue("$l", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4)));
        }
        return list;
    }

    public List<(string Ip, string? Hostname, string? Region, long In, long Out)> TopHosts(string table, long from, long to, int limit)
    {
        var list = new List<(string, string?, string?, long, long)>();
        lock (_gate)
        {
            var src = TrafficSourceSql(table, true);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT hex(t.host) AS iphex, h.hostname, h.region, SUM(t.inb), SUM(t.outb)
                FROM {src} t LEFT JOIN hosts h ON h.ip = t.host
                GROUP BY t.host ORDER BY SUM(t.inb + t.outb) DESC, t.host ASC LIMIT $l
                """;
            cmd.Parameters.AddWithValue("$f", from);
            cmd.Parameters.AddWithValue("$t", to);
            cmd.Parameters.AddWithValue("$l", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt64(3), r.GetInt64(4)));
        }
        return list;
    }

    public List<byte[]> DistinctHosts(long fromTs)
    {
        var list = new List<byte[]>();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT host FROM traffic_1s WHERE ts >= $s";
            cmd.Parameters.AddWithValue("$s", fromTs);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((byte[])r.GetValue(0));
        }
        return list;
    }

    public List<byte[]> HostsForGeoIp(int limit)
    {
        var list = new List<byte[]>();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT ip FROM hosts WHERE region IS NULL ORDER BY last_seen DESC LIMIT $l";
            cmd.Parameters.AddWithValue("$l", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((byte[])r.GetValue(0));
        }
        return list;
    }

    public void SetRegion(byte[] ip, string region, long now)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE hosts SET region = $r, last_seen = $t WHERE ip = $ip";
            cmd.Parameters.AddWithValue("$r", region);
            cmd.Parameters.AddWithValue("$t", now);
            cmd.Parameters.AddWithValue("$ip", ip);
            cmd.ExecuteNonQuery();
        }
    }

    public long RowCount(string table)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }
    }

    // ---- quotas ----

    public List<(long? AppId, string Period, long LimitBytes)> GetQuotas()
    {
        var list = new List<(long?, string, long)>();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT app_id, period, limit_bytes FROM quotas";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.IsDBNull(0) ? null : r.GetInt64(0), r.GetString(1), r.GetInt64(2)));
        }
        return list;
    }

    public void SetQuota(long? appId, string period, long limitBytes)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO quotas (app_id, period, limit_bytes) VALUES ($a, $p, $l)
                ON CONFLICT(COALESCE(app_id, -1), period) DO UPDATE SET limit_bytes = $l
                """;
            cmd.Parameters.AddWithValue("$a", (object?)appId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$p", period);
            cmd.Parameters.AddWithValue("$l", limitBytes);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteQuota(long? appId, string period)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM quotas WHERE COALESCE(app_id, -1) = COALESCE($a, -1) AND period = $p";
            cmd.Parameters.AddWithValue("$a", (object?)appId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$p", period);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Per-app totals (in+out) from the 60 s tier + unrolled live tail since the given second.</summary>
    public Dictionary<int, long> UsageByAppSince(long startTs)
    {
        var map = new Dictionary<int, long>();
        lock (_gate)
        {
            var src = TrafficSourceSql("traffic_60s", false);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT app_id, SUM(inb + outb) FROM {src} GROUP BY app_id";
            cmd.Parameters.AddWithValue("$f", startTs);
            using var r = cmd.ExecuteReader();
            while (r.Read()) map[r.GetInt32(0)] = r.GetInt64(1);
        }
        return map;
    }

    public Dictionary<int, long> UsageByAppBetween(long startTs, long endTs)
    {
        var map = new Dictionary<int, long>();
        lock (_gate)
        {
            var src = TrafficSourceSql("traffic_60s", true);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT app_id, SUM(inb + outb) FROM {src} GROUP BY app_id";
            cmd.Parameters.AddWithValue("$f", startTs);
            cmd.Parameters.AddWithValue("$t", endTs);
            using var r = cmd.ExecuteReader();
            while (r.Read()) map[r.GetInt32(0)] = r.GetInt64(1);
        }
        return map;
    }

    /// <summary>Per-app (in, out) byte breakdown for a given rollup tier + live tail and time window.</summary>
    public Dictionary<int, (long In, long Out)> UsageSplitByApp(string table, long startTs, long? endTs = null)
    {
        var map = new Dictionary<int, (long In, long Out)>();
        lock (_gate)
        {
            var src = TrafficSourceSql(table, endTs != null);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT app_id, SUM(inb), SUM(outb) FROM {src} GROUP BY app_id";
            cmd.Parameters.AddWithValue("$f", startTs);
            if (endTs != null) cmd.Parameters.AddWithValue("$t", endTs.Value);
            using var r = cmd.ExecuteReader();
            while (r.Read()) map[r.GetInt32(0)] = (r.GetInt64(1), r.GetInt64(2));
        }
        return map;
    }

    // ---- grouped series (traffic types, countries, lan/wan) ----

    /// <summary>Raw per-(ts, port, proto) groups; caller classifies into traffic types.</summary>
    public List<(long Ts, int Port, int Proto, long In, long Out)> SeriesByPort(string table, long from, long to)
    {
        var list = new List<(long, int, int, long, long)>();
        lock (_gate)
        {
            var src = TrafficSourceSql(table, true);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT ts, port, proto, SUM(inb), SUM(outb) FROM {src} GROUP BY ts, port, proto ORDER BY ts";
            cmd.Parameters.AddWithValue("$f", from);
            cmd.Parameters.AddWithValue("$t", to);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetInt64(0), r.GetInt32(1), r.GetInt32(2), r.GetInt64(3), r.GetInt64(4)));
        }
        return list;
    }

    public List<(long Ts, string Region, long In, long Out)> SeriesByRegion(string table, long from, long to)
    {
        var list = new List<(long, string, long, long)>();
        lock (_gate)
        {
            var src = TrafficSourceSql(table, true);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT t.ts, COALESCE(h.region, ''), SUM(t.inb), SUM(t.outb)
                FROM {src} t LEFT JOIN hosts h ON h.ip = t.host
                GROUP BY t.ts, h.region ORDER BY t.ts
                """;
            cmd.Parameters.AddWithValue("$f", from);
            cmd.Parameters.AddWithValue("$t", to);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3)));
        }
        return list;
    }

    /// <summary>Totals split LAN (private subnets) vs WAN. IPv6 hosts count as WAN.</summary>
    public (long LanIn, long LanOut, long WanIn, long WanOut) LanWanTotals(string table, long from, long to)
    {
        long li = 0, lo = 0, wi = 0, wo = 0;
        lock (_gate)
        {
            var src = TrafficSourceSql(table, true);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT CASE WHEN substr(host, 1, 1) = x'0A'
                          OR substr(host, 1, 3) = x'C0A8'
                          OR (substr(host, 1, 1) = x'AC' AND substr(host, 2, 1) >= x'10' AND substr(host, 2, 1) <= x'1F')
                          OR host = x'7F000001' THEN 1 ELSE 0 END AS is_lan,
                       SUM(inb), SUM(outb)
                FROM {src} GROUP BY is_lan
                """;
            cmd.Parameters.AddWithValue("$f", from);
            cmd.Parameters.AddWithValue("$t", to);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.GetInt64(0) == 1) { li = r.GetInt64(1); lo = r.GetInt64(2); }
                else { wi = r.GetInt64(1); wo = r.GetInt64(2); }
            }
        }
        return (li, lo, wi, wo);
    }

    // ---- events ----

    public const int EvNewApp = 1;
    public const int EvQuota75 = 2;
    public const int EvQuota90 = 3;
    public const int EvQuota100 = 4;
    public const int EvAppVersionChanged = 5;
    public const int EvAppExeChanged = 6;
    public const int EvAppPublisherChanged = 7;

    public long AddEvent(long ts, int type, long? appId, string data)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO events (ts, type, app_id, data) VALUES ($t, $ty, $a, $d); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$t", ts);
            cmd.Parameters.AddWithValue("$ty", type);
            cmd.Parameters.AddWithValue("$a", (object?)appId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$d", data);
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }
    }

    public bool EventExistsSince(int type, long? appId, long sinceTs)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM events WHERE type = $ty AND COALESCE(app_id, -1) = COALESCE($a, -1) AND ts >= $s";
            cmd.Parameters.AddWithValue("$ty", type);
            cmd.Parameters.AddWithValue("$a", (object?)appId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$s", sinceTs);
            return (long)(cmd.ExecuteScalar() ?? 0L) > 0;
        }
    }

    public List<(long Id, long Ts, int Type, long? AppId, string? AppName, string Data)> GetEvents(int limit)
    {
        var list = new List<(long, long, int, long?, string?, string)>();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT e.id, e.ts, e.type, e.app_id, COALESCE(a.name, a.path), e.data
                FROM events e LEFT JOIN apps a ON a.id = e.app_id
                ORDER BY e.id DESC LIMIT $l
                """;
            cmd.Parameters.AddWithValue("$l", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add((r.GetInt64(0), r.GetInt64(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetInt64(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5)));
        }
        return list;
    }

    public List<(long Id, long Ts, int Type, long? AppId, string? AppName, string Data)> GetEventsForApp(int appId, int limit)
    {
        var list = new List<(long, long, int, long?, string?, string)>();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT e.id, e.ts, e.type, e.app_id, COALESCE(a.name, a.path), e.data
                FROM events e LEFT JOIN apps a ON a.id = e.app_id
                WHERE e.app_id = $id
                ORDER BY e.id DESC LIMIT $l
                """;
            cmd.Parameters.AddWithValue("$id", appId);
            cmd.Parameters.AddWithValue("$l", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add((r.GetInt64(0), r.GetInt64(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetInt64(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5)));
        }
        return list;
    }

    public long MaxEventId()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(MAX(id), 0) FROM events";
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }
    }

    /// <summary>Count of unseen Important alerts (excludes low-severity unknown/unidentified/name logs).</summary>
    public long UnseenEventCount()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*)
                FROM events e LEFT JOIN apps a ON a.id = e.app_id
                WHERE e.id > $seen
                  AND NOT (e.type = 1 AND (
                      COALESCE(a.path, '') LIKE 'unknown:%'
                      OR COALESCE(a.path, '') LIKE 'unidentified:%'
                      OR COALESCE(a.path, '') LIKE 'name:%'
                  ))
                """;
            cmd.Parameters.AddWithValue("$seen", ReadMetaLongUnlocked("events_seen_id", 0));
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }
    }

    /// <summary>
    /// Consolidates legacy "unknown:pidNNNN" app rows and IP-keyed
    /// "unidentified:&lt;ip&gt;" rows (whose IPs now have resolved hostnames) into
    /// clean domain-grouped "unidentified:&lt;domain&gt;" rows, and strips stale
    /// "name:" prefixes from app display names. Safe and idempotent.
    /// Returns the list of removed app paths so AppRegistry can drop them.
    /// </summary>
    public List<string> ConsolidateUnidentifiedApps()
    {
        var removed = new List<string>();
        lock (_gate)
        {
            // Clean legacy "name:svchost.exe" display names
            using (var fixNames = _conn.CreateCommand())
            {
                fixNames.CommandText = "UPDATE apps SET name = substr(name, 6) WHERE name LIKE 'name:%'";
                fixNames.ExecuteNonQuery();
            }

            // Load candidate unknown:* and unidentified:* rows
            var candidates = new List<(int Id, string Path, string? Name, long FirstSeen)>();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id, path, name, first_seen FROM apps WHERE path LIKE 'unknown:%' OR path LIKE 'unidentified:%'";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    candidates.Add((r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt64(3)));
            }
            if (candidates.Count == 0) return removed;

            using var tx = _conn.BeginTransaction();
            try
            {
                foreach (var (oldId, oldPath, oldName, firstSeen) in candidates)
                {
                    string targetGroup;
                    if (oldPath.StartsWith("unidentified:", StringComparison.OrdinalIgnoreCase))
                    {
                        var curKey = oldPath["unidentified:".Length..];
                        var desiredName = HostGrouping.DisplayName(curKey);
                        if (!string.Equals(oldName, desiredName, StringComparison.Ordinal))
                        {
                            using var upd = _conn.CreateCommand();
                            upd.Transaction = tx;
                            upd.CommandText = "UPDATE apps SET name = $n WHERE id = $id";
                            upd.Parameters.AddWithValue("$n", desiredName);
                            upd.Parameters.AddWithValue("$id", oldId);
                            upd.ExecuteNonQuery();
                        }
                        if (!HostGrouping.IsIpGroupKey(curKey)) continue;
                        if (!IPAddress.TryParse(curKey, out var parsedIp)) continue;
                        var resolvedHost = LookupHostnameUnlocked(tx, parsedIp.GetAddressBytes());
                        targetGroup = HostGrouping.GroupForHost(resolvedHost, curKey);
                        if (string.Equals(targetGroup, curKey, StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    else
                    {
                        var primary = FindPrimaryHostUnlocked(tx, oldId);
                        targetGroup = primary != null
                            ? HostGrouping.GroupForHost(primary.Value.Hostname, primary.Value.Ip)
                            : "general";
                    }

                    var newPath = $"unidentified:{targetGroup}";
                    var newName = HostGrouping.DisplayName(targetGroup);
                    if (string.Equals(newPath, oldPath, StringComparison.OrdinalIgnoreCase)) continue;

                    int targetId = FindOrInsertAppSilentUnlocked(tx, newPath, newName, firstSeen);
                    if (targetId == oldId) continue;

                    MergeAppIntoUnlocked(tx, oldId, targetId);
                    removed.Add(oldPath);
                }
                tx.Commit();
            }
            catch
            {
                try { tx.Rollback(); } catch { }
                throw;
            }
        }
        return removed;
    }

    private string? LookupHostnameUnlocked(SqliteTransaction tx, byte[] ip)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT hostname FROM hosts WHERE ip = $ip";
        cmd.Parameters.AddWithValue("$ip", ip);
        return cmd.ExecuteScalar() as string;
    }

    private (string Ip, string? Hostname)? FindPrimaryHostUnlocked(SqliteTransaction tx, int appId)
    {
        foreach (var tbl in new[] { "traffic_60s", "traffic_1s", "traffic_3600s", "traffic_86400s" })
        {
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                SELECT t.host, h.hostname
                FROM {tbl} t LEFT JOIN hosts h ON h.ip = t.host
                WHERE t.app_id = $id
                GROUP BY t.host
                ORDER BY SUM(t.inb + t.outb) DESC
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$id", appId);
            using var r = cmd.ExecuteReader();
            if (r.Read() && r.GetValue(0) is byte[] ipBytes && (ipBytes.Length == 4 || ipBytes.Length == 16))
            {
                var ipStr = new IPAddress(ipBytes).ToString();
                var hn = r.IsDBNull(1) ? null : r.GetString(1);
                return (ipStr, hn);
            }
        }
        return null;
    }

    private int FindOrInsertAppSilentUnlocked(SqliteTransaction tx, string path, string name, long firstSeen)
    {
        using (var find = _conn.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText = "SELECT id FROM apps WHERE path = $p";
            find.Parameters.AddWithValue("$p", path);
            var existing = find.ExecuteScalar();
            if (existing is not null) return Convert.ToInt32(existing);
        }
        using var ins = _conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = "INSERT INTO apps (path, name, first_seen) VALUES ($p, $n, $t); SELECT last_insert_rowid();";
        ins.Parameters.AddWithValue("$p", path);
        ins.Parameters.AddWithValue("$n", name);
        ins.Parameters.AddWithValue("$t", firstSeen);
        return Convert.ToInt32(ins.ExecuteScalar() ?? 0L);
    }

    private void MergeAppIntoUnlocked(SqliteTransaction tx, int srcId, int dstId)
    {
        // traffic_1s has no unique index -> direct update
        using (var c1 = _conn.CreateCommand())
        {
            c1.Transaction = tx;
            c1.CommandText = "UPDATE traffic_1s SET app_id = $dst WHERE app_id = $src";
            c1.Parameters.AddWithValue("$dst", dstId);
            c1.Parameters.AddWithValue("$src", srcId);
            c1.ExecuteNonQuery();
        }

        // Rollup tables have unique index (ts, app_id, host, port, proto) -> upsert sum & delete source
        foreach (var tbl in new[] { "traffic_60s", "traffic_3600s", "traffic_86400s" })
        {
            using var cm = _conn.CreateCommand();
            cm.Transaction = tx;
            cm.CommandText = $"""
                INSERT INTO {tbl} (ts, app_id, host, port, proto, inb, outb)
                SELECT ts, $dst, host, port, proto, SUM(inb), SUM(outb)
                FROM {tbl} WHERE app_id = $src
                GROUP BY ts, host, port, proto
                ON CONFLICT(ts, app_id, host, port, proto) DO UPDATE SET
                    inb = inb + excluded.inb,
                    outb = outb + excluded.outb;
                DELETE FROM {tbl} WHERE app_id = $src;
                """;
            cm.Parameters.AddWithValue("$dst", dstId);
            cm.Parameters.AddWithValue("$src", srcId);
            cm.ExecuteNonQuery();
        }

        // Deduplicate EvNewApp spam when merging multiple unknown:pid rows into one domain group
        bool dstHasNewApp;
        using (var chk = _conn.CreateCommand())
        {
            chk.Transaction = tx;
            chk.CommandText = "SELECT COUNT(*) FROM events WHERE app_id = $dst AND type = 1";
            chk.Parameters.AddWithValue("$dst", dstId);
            dstHasNewApp = (long)(chk.ExecuteScalar() ?? 0L) > 0;
        }
        if (dstHasNewApp)
        {
            using var delEv = _conn.CreateCommand();
            delEv.Transaction = tx;
            delEv.CommandText = "DELETE FROM events WHERE app_id = $src AND type = 1";
            delEv.Parameters.AddWithValue("$src", srcId);
            delEv.ExecuteNonQuery();
        }
        using (var updEv = _conn.CreateCommand())
        {
            updEv.Transaction = tx;
            updEv.CommandText = "UPDATE events SET app_id = $dst WHERE app_id = $src";
            updEv.Parameters.AddWithValue("$dst", dstId);
            updEv.Parameters.AddWithValue("$src", srcId);
            updEv.ExecuteNonQuery();
        }

        // Quotas: keep target's quota if one already exists for the same period
        using (var delQ = _conn.CreateCommand())
        {
            delQ.Transaction = tx;
            delQ.CommandText = "DELETE FROM quotas WHERE app_id = $src AND EXISTS (SELECT 1 FROM quotas q2 WHERE q2.app_id = $dst AND q2.period = quotas.period)";
            delQ.Parameters.AddWithValue("$dst", dstId);
            delQ.Parameters.AddWithValue("$src", srcId);
            delQ.ExecuteNonQuery();
        }
        using (var updQ = _conn.CreateCommand())
        {
            updQ.Transaction = tx;
            updQ.CommandText = "UPDATE quotas SET app_id = $dst WHERE app_id = $src";
            updQ.Parameters.AddWithValue("$dst", dstId);
            updQ.Parameters.AddWithValue("$src", srcId);
            updQ.ExecuteNonQuery();
        }

        using var delApp = _conn.CreateCommand();
        delApp.Transaction = tx;
        delApp.CommandText = "DELETE FROM apps WHERE id = $src";
        delApp.Parameters.AddWithValue("$src", srcId);
        delApp.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
