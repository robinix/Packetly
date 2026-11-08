namespace Packetly.Core;

/// <summary>
/// In-memory per-second traffic buckets keyed by (pid, host, port, proto).
/// PIDs are resolved to app ids by the writer at flush time, so freshly
/// spawned processes are attributed correctly. Keeps the last N closed
/// seconds for live queries; late events are folded in on the next rotation.
/// </summary>
internal sealed class TrafficRing
{
    public readonly record struct FlowKey(int Pid, string Host, int Port, int Proto);
    public readonly record struct Bucket(long Ts, Dictionary<FlowKey, (long In, long Out)> Flows);

    private readonly object _lock = new();
    private readonly int _historySeconds;
    private long _currentSecond = -1;
    private Dictionary<FlowKey, (long In, long Out)> _current = new();
    private readonly Queue<Bucket> _closed = new();
    private readonly Dictionary<long, Dictionary<FlowKey, (long In, long Out)>> _late = new();

    public TrafficRing(int historySeconds = 300) => _historySeconds = historySeconds;

    public void Add(long ts, int pid, string host, int port, int proto, long inBytes, long outBytes)
    {
        lock (_lock)
        {
            if (ts < _currentSecond)
            {
                if (!_late.TryGetValue(ts, out var late))
                    _late[ts] = late = new Dictionary<FlowKey, (long, long)>();
                AddTo(late, pid, host, port, proto, inBytes, outBytes);
                return;
            }
            if (ts > _currentSecond)
            {
                if (_currentSecond >= 0) CloseCurrentLocked();
                _currentSecond = ts;
            }
            AddTo(_current, pid, host, port, proto, inBytes, outBytes);
        }
    }

    /// <summary>Rotates the current second if time has moved past it, so the
    /// writer can flush even when traffic went silent.</summary>
    public void AdvanceTo(long nowUtc)
    {
        lock (_lock)
        {
            if (_currentSecond >= 0 && nowUtc > _currentSecond)
            {
                CloseCurrentLocked();
                _currentSecond = nowUtc;
            }
        }
    }

    /// <summary>Returns and clears all closed seconds. Call AdvanceTo first.</summary>
    public List<Bucket> DrainClosed()
    {
        lock (_lock)
        {
            var list = new List<Bucket>(_closed.Count);
            while (_closed.Count > 0) list.Add(_closed.Dequeue());
            return list;
        }
    }

    /// <summary>Read-only view of the last `count` closed seconds plus the
    /// current partial second, for live displays.</summary>
    public List<Bucket> RecentSeconds(int count)
    {
        lock (_lock)
        {
            var arr = _closed.ToArray();
            int take = Math.Min(count, arr.Length);
            var result = new List<Bucket>(take + 1);
            for (int i = arr.Length - take; i < arr.Length; i++) result.Add(arr[i]);
            if (_currentSecond >= 0)
                result.Add(new Bucket(_currentSecond, new Dictionary<FlowKey, (long, long)>(_current)));
            return result;
        }
    }

    private void CloseCurrentLocked()
    {
        _closed.Enqueue(new Bucket(_currentSecond, _current));
        while (_closed.Count > _historySeconds) _closed.Dequeue();
        _current = new Dictionary<FlowKey, (long, long)>();

        foreach (var ts in _late.Keys.OrderBy(t => t).ToArray())
        {
            _closed.Enqueue(new Bucket(ts, _late[ts]));
            _late.Remove(ts);
        }
    }

    private static void AddTo(Dictionary<FlowKey, (long In, long Out)> target, int pid, string host, int port, int proto, long inBytes, long outBytes)
    {
        var key = new FlowKey(pid, host, port, proto);
        var (inB, outB) = target.TryGetValue(key, out var v) ? v : (0L, 0L);
        target[key] = (inB + inBytes, outB + outBytes);
    }
}
