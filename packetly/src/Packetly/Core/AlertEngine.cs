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
/// Raises events into the events table: new apps seen on the network and
/// per-quota threshold crossings (75 % / 90 % / 100 %). Each (type, app,
/// period) fires once — deduped against persisted events, so restarts never
/// re-alert for the same period.
/// </summary>
internal sealed class AlertEngine
{
    private static readonly (double Ratio, int Type)[] Thresholds = { (0.75, Db.EvQuota75), (0.9, Db.EvQuota90), (1.0, Db.EvQuota100) };
    private readonly Db _db;

    public AlertEngine(Db db) => _db = db;

    public void RunCycle(long nowUtc)
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

    public void OnAppCreated(long appId, string path)
    {
        var data = JsonSerializer.Serialize(new { path, name = Path.GetFileName(path) });
        _db.AddEvent(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Db.EvNewApp, appId, data);
    }
}
