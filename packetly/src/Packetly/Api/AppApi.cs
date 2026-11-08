using System.Net;
using Packetly.Collectors;
using Packetly.Core;
using Packetly.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Packetly.Api;

/// <summary>Shared service references for the API endpoints.</summary>
internal sealed class AppServices
{
    public AppServices(Db db, TrafficWriter writer, ProcessTracker tracker, string collectorMode, bool elevated, string dbPath, AlertEngine alerts)
    {
        Db = db;
        Writer = writer;
        Tracker = tracker;
        CollectorMode = collectorMode;
        Elevated = elevated;
        DbPath = dbPath;
        Alerts = alerts;
        StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    public Db Db { get; }
    public TrafficWriter Writer { get; }
    public ProcessTracker Tracker { get; }
    public string CollectorMode { get; }
    public bool Elevated { get; }
    public string DbPath { get; }
    public AlertEngine Alerts { get; }
    public long StartedAt { get; }
}

internal sealed class SetQuotaBody
{
    public long? AppId { get; set; }
    public string Period { get; set; } = "daily";
    public long LimitBytes { get; set; }
}

internal sealed class SettingsBody
{
    public bool Geoip { get; set; }
}

internal static class AppApi
{
    public static void MapApi(WebApplication app, AppServices svc)
    {
        app.MapGet("/api/status", () =>
        {
            var now = Now();
            return new
            {
                app = "packetly",
                version = "1.0.0",
                now,
                startedAt = svc.StartedAt,
                uptimeSeconds = now - svc.StartedAt,
                collector = svc.CollectorMode,
                elevated = svc.Elevated,
                processesKnown = svc.Tracker.Count,
                eventsSeen = svc.Writer.EventsSeen,
                dbPath = svc.DbPath,
                dbBytes = File.Exists(svc.DbPath) ? new FileInfo(svc.DbPath).Length : 0,
                rows = new
                {
                    t1s = svc.Db.RowCount("traffic_1s"),
                    t60s = svc.Db.RowCount("traffic_60s"),
                    t3600s = svc.Db.RowCount("traffic_3600s"),
                    t86400s = svc.Db.RowCount("traffic_86400s"),
                },
            };
        });

        // Live per-second series straight from the in-memory ring.
        app.MapGet("/api/live", (int? seconds) =>
        {
            var (points, apps) = svc.Writer.LiveSnapshot(Math.Clamp(seconds ?? 300, 1, 300));
            return new
            {
                now = Now(),
                points = points.Select(p => new[] { p.Ts, p.In, p.Out }),
                apps = apps.Select(a => new { id = a.AppId, name = a.Name, inBytes = a.In, outBytes = a.Out }),
            };
        });

        app.MapGet("/api/traffic", (string? range, long? from, long? to, int? appId, string? breakdown, string? group) =>
        {
            var now = Now();
            var r = ResolveRange(range, from, to, now, appId);
            if (r == null) return Results.BadRequest(new { error = "range must be 5m|3h|24h|30d|unlimited|custom (custom requires from)" });

            var (f, t, table, bucket, _) = r.Value;
            group ??= "apps";

            if (group == "type")
            {
                // classify (port, proto) into named traffic types, then aggregate per (ts, type)
                var portRows = svc.Db.SeriesByPort(table, f, t);
                var names = new Dictionary<int, string>
                {
                    [0] = "HTTPS", [1] = "HTTP", [2] = "SSH", [3] = "RDP", [4] = "Other TCP",
                    [5] = "DNS", [6] = "NTP", [7] = "QUIC", [8] = "SSDP", [9] = "mDNS",
                    [10] = "Other UDP", [11] = "Other",
                };
                var acc = new Dictionary<(long Ts, int Type), (long In, long Out)>();
                foreach (var pr in portRows)
                {
                    var typeId = pr.Proto switch
                    {
                        6 => pr.Port switch { 443 => 0, 80 => 1, 22 => 2, 3389 => 3, _ => 4 },
                        17 => pr.Port switch { 53 => 5, 123 => 6, 443 => 7, 1900 => 8, 5353 => 9, _ => 10 },
                        _ => 11,
                    };
                    var k = (pr.Ts, typeId);
                    var (i, o) = acc.TryGetValue(k, out var v) ? v : (0L, 0L);
                    acc[k] = (i + pr.In, o + pr.Out);
                }
                var typeRows = acc.OrderBy(kv => kv.Key.Ts)
                    .Select(kv => new[] { kv.Key.Ts, (object)kv.Key.Item2, kv.Value.Item1, kv.Value.Item2 });
                return Results.Json(new
                {
                    range = range ?? "5m", tier = table, bucketSeconds = bucket, from = f, to = t,
                    data = new { rows = typeRows, keyNames = names },
                });
            }

            if (group == "country")
            {
                var regionRows = svc.Db.SeriesByRegion(table, f, t);
                var ids = new Dictionary<string, int>();
                var keyNames = new Dictionary<int, string>();
                var rows = new List<object[]>();
                foreach (var rr in regionRows)
                {
                    var key = string.IsNullOrEmpty(rr.Region) ? "??" : rr.Region;
                    if (!ids.TryGetValue(key, out var id))
                    {
                        id = ids.Count;
                        ids[key] = id;
                        keyNames[id] = key == "??" ? "Unknown" : key;
                    }
                    rows.Add(new object[] { rr.Ts, id, rr.In, rr.Out });
                }
                return Results.Json(new
                {
                    range = range ?? "5m", tier = table, bucketSeconds = bucket, from = f, to = t,
                    data = new { rows, keyNames },
                });
            }

            if (group == "lan")
            {
                var totals = svc.Db.LanWanTotals(table, f, t);
                return Results.Json(new
                {
                    range = range ?? "5m", tier = table, bucketSeconds = bucket, from = f, to = t,
                    lanIn = totals.LanIn, lanOut = totals.LanOut,
                    wanIn = totals.WanIn, wanOut = totals.WanOut,
                });
            }

            var byApp = string.Equals(breakdown, "apps", StringComparison.OrdinalIgnoreCase);
            object payload;
            if (byApp)
            {
                var rows = svc.Db.SeriesByApp(table, f, t, appId);
                var names = svc.Db.AppsInWindow(table, f, t, 5000).ToDictionary(a => a.Id, a => a.Name);
                payload = new
                {
                    rows = rows.Select(x => new[] { x.Ts, x.AppId, x.In, x.Out }),
                    appNames = names,
                };
            }
            else
            {
                var rows = svc.Db.SeriesTotals(table, f, t, appId);
                payload = new
                {
                    totals = rows.Select(x => new[] { x.Ts, x.In, x.Out }),
                };
            }
            return Results.Json(new
            {
                range = range ?? "custom",
                tier = table,
                bucketSeconds = bucket,
                from = f,
                to = t,
                appId,
                data = payload,
            });
        });

        app.MapGet("/api/apps", (string? range, long? from, long? to, int? limit) =>
        {
            var now = Now();
            var r = ResolveRange(range ?? "5m", from, to, now, null);
            if (r == null) return Results.BadRequest(new { error = "invalid range" });
            var (f, t, table, _, _) = r.Value;
            var rows = svc.Db.AppsInWindow(table, f, t, Math.Clamp(limit ?? 200, 1, 2000));
            return Results.Json(new
            {
                from = f,
                to = t,
                tier = table,
                apps = rows.Select(a => new { id = a.Id, name = a.Name, path = a.Path, inBytes = a.In, outBytes = a.Out }),
            });
        });

        app.MapGet("/api/hosts", (string? range, long? from, long? to, int? limit) =>
        {
            var now = Now();
            var r = ResolveRange(range ?? "5m", from, to, now, null);
            if (r == null) return Results.BadRequest(new { error = "invalid range" });
            var (f, t, table, _, _) = r.Value;
            var rows = svc.Db.TopHosts(table, f, t, Math.Clamp(limit ?? 50, 1, 1000));
            return Results.Json(new
            {
                from = f,
                to = t,
                tier = table,
                hosts = rows.Select(h => new { ip = HexToIp(h.Ip), hostname = h.Hostname, region = h.Region, inBytes = h.In, outBytes = h.Out }),
            });
        });

        // ---- usage, quotas, events ----

        app.MapGet("/api/usage", (string? period, long? from, long? to) =>
        {
            var names = svc.Db.AppNames();
            var now = Now();
            long start;
            long end = to ?? now;
            Dictionary<int, long> usage;
            string p;
            if (from != null)
            {
                // arbitrary window (5m/3h/24h/7d/30d/all/custom from the UI)
                start = from.Value;
                p = "range";
                usage = svc.Db.UsageByAppBetween(start, end);
            }
            else
            {
                p = period switch { "today" => "daily", "week" => "weekly", "month" => "monthly", "daily" => "daily", "weekly" => "weekly", "monthly" => "monthly", _ => "daily" };
                start = Periods.StartUtc(p);
                usage = svc.Db.UsageByAppSince(start);
            }
            return Results.Json(new
            {
                period = p,
                start,
                now,
                total = usage.Values.Sum(),
                perApp = usage.OrderByDescending(kv => kv.Value)
                    .Select(kv => new { appId = kv.Key, name = names.GetValueOrDefault(kv.Key, $"app {kv.Key}"), bytes = kv.Value }),
            });
        });

        app.MapGet("/api/quotas", () =>
        {
            var names = svc.Db.AppNames();
            var now = Now();
            var usageByApp = svc.Db.UsageByAppSince(0);
            var quotas = svc.Db.GetQuotas().Select(q =>
            {
                var start = Periods.StartUtc(q.Period);
                var usage = svc.Db.UsageByAppSince(start);
                var used = q.AppId == null
                    ? usage.Values.Sum()
                    : usage.TryGetValue((int)q.AppId, out var u) ? u : 0;
                return new
                {
                    appId = q.AppId,
                    appName = q.AppId == null ? "All apps" : names.GetValueOrDefault((int)q.AppId, $"app {q.AppId}"),
                    period = q.Period,
                    limitBytes = q.LimitBytes,
                    usedBytes = used,
                    ratio = q.LimitBytes > 0 ? (double)used / q.LimitBytes : 0,
                    periodStart = start,
                };
            });
            return Results.Json(new { now, quotas });
        });

        app.MapPost("/api/quotas", (SetQuotaBody b) =>
        {
            if (!Periods.Valid.Contains(b.Period)) return Results.BadRequest(new { error = "period must be daily, weekly or monthly" });
            if (b.LimitBytes <= 0) return Results.BadRequest(new { error = "limitBytes must be positive" });
            svc.Db.SetQuota(b.AppId, b.Period, b.LimitBytes);
            return Results.Json(new { ok = true });
        });

        app.MapDelete("/api/quotas", (long? appId, string? period) =>
        {
            if (string.IsNullOrEmpty(period) || !Periods.Valid.Contains(period)) return Results.BadRequest(new { error = "period must be daily, weekly or monthly" });
            svc.Db.DeleteQuota(appId, period);
            return Results.Json(new { ok = true });
        });

        app.MapGet("/api/events", (int? limit) =>
        {
            var rows = svc.Db.GetEvents(Math.Clamp(limit ?? 100, 1, 500));
            return Results.Json(new
            {
                now = Now(),
                unseen = svc.Db.UnseenEventCount(),
                events = rows.Select(e => new { id = e.Id, ts = e.Ts, type = e.Type, appId = e.AppId, appName = e.AppName, data = e.Data }),
            });
        });

        app.MapPost("/api/events/seen", () =>
        {
            svc.Db.SetMetaLong("events_seen_id", svc.Db.MaxEventId());
            return Results.Json(new { ok = true });
        });

        // ---- phase 5: app icons, geoip setting ----

        app.MapGet("/api/icon", (HttpContext ctx) =>
        {
            if (!int.TryParse(ctx.Request.Query["app_id"], out var appId)) return Results.BadRequest();
            var png = svc.Db.GetAppIcon(appId);
            if (png == null) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "public,max-age=86400";
            return Results.File(png, "image/png");
        });

        app.MapGet("/api/settings", () => Results.Json(new
        {
            geoip = svc.Db.GetMetaLong("geoip_enabled", 0) == 1,
        }));

        app.MapPost("/api/settings", (SettingsBody b) =>
        {
            svc.Db.SetMetaLong("geoip_enabled", b.Geoip ? 1 : 0);
            return Results.Json(new { ok = true });
        });
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static long ParseTime(string? s, long fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        if (long.TryParse(s, out var unix)) return unix;
        if (DateTimeOffset.TryParse(s, out var dto)) return dto.ToUnixTimeSeconds();
        return fallback;
    }

    private static (long From, long To, string Table, long Bucket, bool UseRing)? ResolveRange(string? range, long? from, long? to, long now, int? appId)
    {
        var t = ParseTime(to == null ? null : to.ToString(), now);
        long f;
        switch (range)
        {
            case "5m":
                // traffic_1s covers 48 h, so the live window comes from the DB
                // too — this keeps breakdown=apps uniform across all ranges.
                return (now - 300, now, "traffic_1s", 1, false);
            case "3h":
                return (now - 3L * 3600, t, "traffic_1s", 1, false);
            case "24h":
                return (now - 86400, t, "traffic_60s", 60, false);
            case "7d":
                return (now - 7L * 86400, t, "traffic_60s", 60, false);
            case "30d":
                return (now - 30L * 86400, t, "traffic_3600s", 3600, false);
            case "unlimited":
                return (0, t, "traffic_86400s", 86400, false);
            case "custom" when from != null:
                f = ParseTime(from.ToString(), 0);
                var span = Math.Max(1, t - f);
                var (table, bucket) = span switch
                {
                    <= 3L * 3600 => ("traffic_1s", 1),
                    <= 3L * 86400 => ("traffic_60s", 60),
                    <= 90L * 86400 => ("traffic_3600s", 3600),
                    _ => ("traffic_86400s", 86400),
                };
                return (f, t, table, bucket, false);
            default:
                return null;
        }
    }

    private static string HexToIp(string hex)
    {
        try
        {
            var bytes = Convert.FromHexString(hex);
            return new IPAddress(bytes).ToString();
        }
        catch
        {
            return hex;
        }
    }
}
