using GWLite.Storage;

namespace GWLite.Core;

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
            id = _db.FindOrInsertApp(path, Path.GetFileName(path), now);
            _cache[path] = id;
            return id;
        }
    }
}
