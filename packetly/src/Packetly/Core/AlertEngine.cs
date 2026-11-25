using System.Diagnostics;
using System.Text.Json;
using Packetly.Storage;

namespace Packetly.Core;

/// <summary>
/// Period boundaries in local time: daily = local midnight, weekly = Monday
/// 00:00 local, monthly = 1st 00:00 local. Returns the UTC epoch second of
/// the boundary.
/// </summary>
public static class Periods
{
    public static readonly string[] Valid = { "daily", "weekly", "monthly" };

    public static long StartUtc(string period, DateTimeOffset? atUtc = null) => period switch
    {
        "daily" => StartUtc(atUtc, d => d.Date),
        "weekly" => StartUtc(atUtc, d => d.Date.AddDays(-(((int)d.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7))),
        "monthly" => StartUtc(atUtc, d => new DateTime(d.Year, d.Month, 1)),
        _ => throw new ArgumentException($"unknown period '{period}'", nameof(period)),
    };

    private static long StartUtc(DateTimeOffset? atUtc, Func<DateTimeOffset, DateTimeOffset> floor)
    {
        var local = TimeZoneInfo.ConvertTime(atUtc ?? DateTimeOffset.UtcNow, TimeZoneInfo.Local);
        return floor(local).ToUnixTimeSeconds();
    }
}

/// <summary>
/// Raises events into the events table:
///   - First network activity for newly discovered apps
///   - Quota threshold crossings (75 % / 90 % / 100 %) deduped per period
///   - Application info monitor: version, executable binary, and publisher changes
/// </summary>
internal sealed class AlertEngine
{
    private static readonly (double Ratio, int Type)[] Thresholds = { (0.75, Db.EvQuota75), (0.9, Db.EvQuota90), (1.0, Db.EvQuota100) };
    private readonly Db _db;
    private long _lastAppScanUtc;

    public AlertEngine(Db db) => _db = db;

    public void RunCycle(long nowUtc)
    {
        CheckQuotas(nowUtc);
        if (nowUtc - _lastAppScanUtc >= 120)
        {
            _lastAppScanUtc = nowUtc;
            CheckAppChanges(nowUtc);
        }
    }

    private void CheckQuotas(long nowUtc)
    {
        foreach (var (appId, period, limit) in _db.GetQuotas())
        {
            var start = Periods.StartUtc(period);
            if (limit <= 0) continue;

            long used;
            if (appId == null)
            {
                used = 0;
                foreach (var v in _db.UsageByAppSince(start).Values) used += v;
            }
            else
            {
                _db.UsageByAppSince(start).TryGetValue((int)appId, out used);
            }

            foreach (var (ratio, type) in Thresholds)
            {
                if (used < ratio * limit) continue;
                if (_db.EventExistsSince(type, appId, start)) continue;
                var data = JsonSerializer.Serialize(new { period, threshold = (int)(ratio * 100), used, limit });
                _db.AddEvent(nowUtc, type, appId, data);
            }
        }
    }

    private void CheckAppChanges(long nowUtc)
    {
        foreach (var (id, path, name, oldPub, oldVer, oldSize, oldMtime) in _db.GetMonitoredApps(500))
        {
            if (string.IsNullOrEmpty(path)) continue;
            var meta = ReadExeMeta(path);
            if (meta == null) continue;
            var (curVer, curPub, curSize, curMtime) = meta.Value;

            // First-time baseline seed for existing rows after upgrade: store quietly without spamming alerts.
            if (oldSize == null && oldMtime == null && string.IsNullOrEmpty(oldVer))
            {
                _db.UpdateAppMetadata(id, curVer, curPub, curSize, curMtime);
                continue;
            }

            var displayName = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(path) : name;
            bool versionChanged = !string.IsNullOrEmpty(oldVer)
                && !string.IsNullOrEmpty(curVer)
                && !string.Equals(oldVer, curVer, StringComparison.OrdinalIgnoreCase);
            bool binaryChanged = (oldSize.HasValue && oldSize.Value != curSize)
                || (oldMtime.HasValue && oldMtime.Value != curMtime);
            bool publisherChanged = !string.IsNullOrEmpty(oldPub)
                && !string.IsNullOrEmpty(curPub)
                && !string.Equals(oldPub, curPub, StringComparison.OrdinalIgnoreCase);

            if (versionChanged)
            {
                var data = JsonSerializer.Serialize(new
                {
                    path,
                    name = displayName,
                    oldVersion = oldVer,
                    newVersion = curVer,
                    publisher = curPub ?? oldPub,
                });
                _db.AddEvent(nowUtc, Db.EvAppVersionChanged, id, data);
            }
            else if (binaryChanged)
            {
                var data = JsonSerializer.Serialize(new
                {
                    path,
                    name = displayName,
                    version = curVer ?? oldVer,
                    oldSize,
                    newSize = curSize,
                    publisher = curPub ?? oldPub,
                });
                _db.AddEvent(nowUtc, Db.EvAppExeChanged, id, data);
            }

            if (publisherChanged)
            {
                var data = JsonSerializer.Serialize(new
                {
                    path,
                    name = displayName,
                    oldPublisher = oldPub,
                    newPublisher = curPub,
                });
                _db.AddEvent(nowUtc, Db.EvAppPublisherChanged, id, data);
            }

            if (versionChanged || binaryChanged || publisherChanged
                || (string.IsNullOrEmpty(oldVer) && !string.IsNullOrEmpty(curVer))
                || (string.IsNullOrEmpty(oldPub) && !string.IsNullOrEmpty(curPub)))
            {
                _db.UpdateAppMetadata(id, curVer ?? oldVer, curPub ?? oldPub, curSize, curMtime);
            }
        }
    }

    public void OnAppCreated(long appId, string path)
    {
        string? version = null;
        string? publisher = null;
        if (!string.IsNullOrEmpty(path) && !path.StartsWith("unknown:") && !path.StartsWith("name:"))
        {
            var meta = ReadExeMeta(path);
            if (meta != null)
            {
                version = meta.Value.Version;
                publisher = meta.Value.Publisher;
                _db.UpdateAppMetadata(appId, version, publisher, meta.Value.FileSize, meta.Value.FileMtime);
            }
        }
        var data = JsonSerializer.Serialize(new
        {
            path,
            name = Path.GetFileName(path),
            version,
            publisher,
        });
        _db.AddEvent(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Db.EvNewApp, appId, data);
    }

    private static (string? Version, string? Publisher, long FileSize, long FileMtime)? ReadExeMeta(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var fi = new FileInfo(path);
            long size = fi.Length;
            long mtime = new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeSeconds();
            string? ver = null;
            string? pub = null;
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                ver = CleanMeta(vi.ProductVersion) ?? CleanMeta(vi.FileVersion);
                pub = CleanMeta(vi.CompanyName);
            }
            catch { /* unreadable version resource */ }
            return (ver, pub, size, mtime);
        }
        catch
        {
            return null;
        }
    }

    private static string? CleanMeta(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        // Strip trailing commit hashes or whitespace noise sometimes appended to ProductVersion
        var sp = s.IndexOf(' ');
        if (sp > 0) s = s[..sp].Trim();
        return s.Length > 0 ? s : null;
    }
}
