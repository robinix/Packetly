using Packetly.Storage;

namespace Packetly.Core;

/// <summary>Caches path → app_id in memory; persists new apps to SQLite.</summary>
internal sealed class AppRegistry
{
    private readonly Db _db;
    private readonly object _lock = new();
    private readonly Dictionary<string, int> _cache = new();

    public AppRegistry(Db db) => _db = db;

    public int GetOrCreate(string path, long now)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(path, out var id)) return id;
            id = _db.FindOrInsertApp(path, HostGrouping.AppDisplayName(path), now);
            _cache[path] = id;
            return id;
        }
    }

    public void InvalidatePaths(IEnumerable<string> paths)
    {
        lock (_lock)
        {
            foreach (var p in paths) _cache.Remove(p);
        }
    }
}
