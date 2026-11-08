using Packetly.Storage;

namespace Packetly.Core;

/// <summary>
/// Rolls 1s → 60s → 3600s → 86400s using per-tier watermarks in the meta
/// table (crash-safe: rollups are keyed writes, re-running a window replaces),
/// and prunes expired rows: 1 s kept 48 h, 60 s kept 62 days, hourly kept
/// 400 days, daily kept forever.
/// </summary>
internal sealed class Aggregator
{
    private readonly Db _db;

    public Aggregator(Db db) => _db = db;

    public void RunCycle(long nowUtc)
    {
        RollTier("traffic_1s", "traffic_60s", "rollup_60s_wm", 60, nowUtc);
        RollTier("traffic_60s", "traffic_3600s", "rollup_3600s_wm", 3600, nowUtc);
        RollTier("traffic_3600s", "traffic_86400s", "rollup_86400s_wm", 86400, nowUtc);

        _db.Prune("traffic_1s", nowUtc - 48L * 3600);
        _db.Prune("traffic_60s", nowUtc - 62L * 86400);
        _db.Prune("traffic_3600s", nowUtc - 400L * 86400);
    }

    private void RollTier(string src, string dst, string watermarkKey, long bucket, long nowUtc)
    {
        var completeTo = nowUtc - nowUtc % bucket; // only full buckets
        var wm = _db.GetMetaLong(watermarkKey, 0);
        if (completeTo <= wm) return;
        _db.Rollup(src, dst, wm, completeTo, bucket);
        _db.SetMetaLong(watermarkKey, completeTo);
    }
}
