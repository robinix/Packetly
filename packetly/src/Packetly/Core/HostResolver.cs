using System.Net;
using Packetly.Storage;

namespace Packetly.Core;

/// <summary>
/// Best-effort reverse DNS for remote hosts. Claims hosts into the hosts
/// table as they appear, resolves missing hostnames with a small concurrency
/// cap and a per-IP retry cooldown; failures just leave the hostname null.
/// </summary>
internal sealed class HostResolver
{
    private readonly Db _db;
    private readonly SemaphoreSlim _slots = new(3);
    private readonly Dictionary<string, DateTimeOffset> _cooldowns = new();
    private static readonly TimeSpan Cooldown = TimeSpan.FromHours(1);
    private const int PerCycleLimit = 40;

    public HostResolver(Db db) => _db = db;

    public async Task RunCycleAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // discover hosts seen in recent traffic and claim them into the table
        var wm = _db.GetMetaLong("hosts_seen_wm", now - 600);
        var seen = _db.DistinctHosts(Math.Max(wm, now - 600));
        foreach (var ip in seen) _db.ClaimHost(ip, now);
        if (seen.Count > 0) _db.SetMetaLong("hosts_seen_wm", now);

        var pending = _db.HostsNeedingResolve(now, PerCycleLimit * 3);
        int started = 0;
        var tasks = new List<Task>();
        foreach (var ipBytes in pending)
        {
            if (started >= PerCycleLimit) break;
            var ip = ToIPAddress(ipBytes);
            if (ip == null) continue;
            var key = ip.ToString();
            lock (_cooldowns)
            {
                if (_cooldowns.TryGetValue(key, out var until) && DateTimeOffset.UtcNow < until) continue;
            }
            started++;
            tasks.Add(ResolveOne(ipBytes, ip, now, ct));
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task ResolveOne(byte[] ipBytes, IPAddress ip, long now, CancellationToken ct)
    {
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _db.ClaimHost(ipBytes, now);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            string? name = null;
            try
            {
                var entry = await Dns.GetHostEntryAsync(ip.ToString(), timeout.Token).ConfigureAwait(false);
                name = entry.HostName;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* resolution timed out */ }
            catch { /* NXDOMAIN / no PTR / socket errors — normal for most IPs */ }

            if (name != null) _db.SetHostname(ipBytes, name, now);
            else
            {
                lock (_cooldowns) _cooldowns[ip.ToString()] = DateTimeOffset.UtcNow.Add(Cooldown);
            }
        }
        finally
        {
            _slots.Release();
        }
    }

    private static IPAddress? ToIPAddress(byte[] bytes) => bytes.Length switch
    {
        4 => new IPAddress(bytes),
        16 => new IPAddress(bytes),
        _ => null,
    };
}
