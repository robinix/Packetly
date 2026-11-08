using System.Net;
using GWLite.Collectors;
using GWLite.Storage;

namespace GWLite.Core;

/// <summary>
/// Shared pipeline: filters events, fills the ring, and flushes closed
/// seconds into SQLite with deferred pid → app resolution. Used by both the
/// smoke mode and the serve mode.
/// </summary>
internal sealed class TrafficWriter
{
    private readonly Db _db;
    private readonly TrafficRing _ring;
    private readonly ProcessTracker _tracker;
    private readonly AppRegistry _registry;
    private readonly int _selfPid = Environment.ProcessId;
    private readonly Dictionary<int, int> _ownerFixes = new();
    private readonly HashSet<int> _diagSeen = new();
    private readonly Queue<TrafficRing.Bucket> _history = new();
    private readonly object _historyLock = new();
    private const int HistorySeconds = 300;
    private long _eventsSeen;

    public TrafficWriter(Db db, TrafficRing ring, ProcessTracker tracker, AppRegistry registry)
    {
        _db = db;
        _ring = ring;
        _tracker = tracker;
        _registry = registry;
    }

    public TrafficRing Ring => _ring;
    public ProcessTracker Tracker => _tracker;
    public long EventsSeen => Interlocked.Read(ref _eventsSeen);

    public void OnEvent(FlowEvent e)
    {
        Interlocked.Increment(ref _eventsSeen);
        if (e.Pid == _selfPid) return;
        if (IPAddress.IsLoopback(e.Remote)) return;
        _ring.Add(e.Ts, e.Pid, e.Remote.ToString(), e.Port, e.Proto, e.In, e.Out);
    }

    public void FlushOnce()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _ring.AdvanceTo(now);
        foreach (var b in _ring.DrainClosed())
        {
            lock (_historyLock)
            {
                _history.Enqueue(b);
                while (_history.Count > HistorySeconds) _history.Dequeue();
            }
            FlushBucket(b.Ts, b.Flows);
        }
    }

    private void FlushBucket(long ts, IReadOnlyDictionary<TrafficRing.FlowKey, (long In, long Out)> flows)
    {
        // Resolve pids to (path, resolved?) first. Flows whose pid belongs to
        // no live process are relay copies of a transparent local proxy (the
        // interceptor's process is invisible); when the same second contains
        // a resolved flow for the same (proto, remote, port), the relay copy
        // is dropped so the payload is attributed once, to the real app.
        var entries = new List<(TrafficRing.FlowKey Key, long In, long Out, string? Path)>();
        foreach (var (k, v) in flows)
        {
            var path = _tracker.GetPath(k.Pid);
            if (path.Length == 0 && k.Proto == 6 && _ownerFixes.TryGetValue(k.Pid, out var fixedPid) && fixedPid != k.Pid)
                path = _tracker.GetPath(fixedPid);
            entries.Add((k, v.In, v.Out, path.Length > 0 ? path : null));
        }

        var hasResolved = entries.Where(e => e.Path != null)
            .Select(e => (e.Key.Proto, e.Key.Host, e.Key.Port)).ToHashSet();
        var rows = new List<(int AppId, byte[] Host, int Port, int Proto, long In, long Out)>();
        foreach (var e in entries)
        {
            if (e.Path == null && hasResolved.Contains((e.Key.Proto, e.Key.Host, e.Key.Port))) continue;
            var path = e.Path;
            if (path == null)
            {
                if (e.Key.Proto == 6 && !_ownerFixes.ContainsKey(e.Key.Pid))
                {
                    var fixedPid = e.Key.Pid;
                    try
                    {
                        if (IPAddress.Parse(e.Key.Host) is { } remote && TcpOwners.Snapshot().TryGetValue((BitConverter.ToUInt32(remote.GetAddressBytes(), 0), (ushort)e.Key.Port), out var owner))
                            fixedPid = owner;
                    }
                    catch { }
                    _ownerFixes[e.Key.Pid] = fixedPid;
                    if (fixedPid != e.Key.Pid) path = _tracker.GetPath(fixedPid);
                }
                if (path == null)
                {
                    if (_diagSeen.Add(e.Key.Pid))
                        Console.WriteLine($"[diag] pid {e.Key.Pid} (proto={e.Key.Proto}, {e.Key.Host}:{e.Key.Port}) has no live process; recorded as unknown");
                    path = $"unknown:pid{e.Key.Pid}";
                }
            }
            var appId = _registry.GetOrCreate(path, ts);
            rows.Add((appId, IPAddress.Parse(e.Key.Host).GetAddressBytes(), e.Key.Port, e.Key.Proto, e.In, e.Out));
        }
        if (rows.Count > 0) _db.InsertBuckets("traffic_1s", ts, rows);
    }

    public readonly record struct LivePoint(long Ts, long In, long Out);
    public readonly record struct LiveApp(int AppId, string Name, long In, long Out);

    /// <summary>Per-second totals plus per-app totals over the last `seconds`
    /// retained buckets plus the current partial second.</summary>
    public (List<LivePoint> Points, List<LiveApp> Apps) LiveSnapshot(int seconds)
    {
        var points = new Dictionary<long, (long In, long Out)>();
        var apps = new Dictionary<int, (long In, long Out)>();

        void Accumulate(long ts, IReadOnlyDictionary<TrafficRing.FlowKey, (long In, long Out)> flows)
        {
            foreach (var (k, v) in flows)
            {
                var (pi, po) = points.TryGetValue(ts, out var p) ? p : (0L, 0L);
                points[ts] = (pi + v.In, po + v.Out);
                var (ai, ao) = apps.TryGetValue(k.Pid, out var a) ? a : (0L, 0L);
                apps[k.Pid] = (ai + v.In, ao + v.Out);
            }
        }

        List<TrafficRing.Bucket> buckets;
        lock (_historyLock)
        {
            var arr = _history.ToArray();
            int take = Math.Min(seconds, arr.Length);
            buckets = new List<TrafficRing.Bucket>(take + 1);
            for (int i = arr.Length - take; i < arr.Length; i++) buckets.Add(arr[i]);
        }
        foreach (var b in buckets) Accumulate(b.Ts, b.Flows);
        foreach (var b in _ring.RecentSeconds(1)) Accumulate(b.Ts, b.Flows);

        var pt = points.OrderBy(kv => kv.Key).Select(kv => new LivePoint(kv.Key, kv.Value.Item1, kv.Value.Item2)).ToList();
        var ap = apps.OrderByDescending(kv => kv.Value.Item1 + kv.Value.Item2)
            .Select(kv =>
            {
                var path = _tracker.GetPath(kv.Key);
                return new LiveApp(kv.Key, path.Length > 0 ? Path.GetFileName(path) : $"unknown:pid{kv.Key}", kv.Value.Item1, kv.Value.Item2);
            }).ToList();
        return (pt, ap);
    }
}
