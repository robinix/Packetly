using System.Net;
using System.Text;
using System.Text.Json;
using Packetly.Storage;

namespace Packetly.Core;

/// <summary>
/// Opt-in country lookup for remote hosts via ip-api.com's free batch
/// endpoint (HTTP, no key, 100 IPs per call). OFF by default because it
/// shares remote-host IPs with a third party. When direct access fails
/// (geo-blocking, sanctions), retries once through the local v2ray SOCKS
/// proxy on 127.0.0.1:10808 via curl.
/// </summary>
internal sealed class GeoIpJob
{
    private const string Endpoint = "http://ip-api.com/batch?fields=status,countryCode,query";
    /// <summary>Local SOCKS5 proxy used only when GeoIP is enabled and direct access
    /// fails. Override with the PACKETLY_GEOIP_PROXY environment variable (host:port).</summary>
    private static string Proxy =>
        Environment.GetEnvironmentVariable("PACKETLY_GEOIP_PROXY") is { Length: > 0 } v
            ? v
            : "127.0.0.1:10808";
    private readonly Db _db;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private long _failUntil;

    public GeoIpJob(Db db) => _db = db;

    public bool Enabled => _db.GetMetaLong("geoip_enabled", 0) == 1;

    public async Task RunCycleAsync(CancellationToken ct)
    {
        if (!Enabled || DateTimeOffset.UtcNow.ToUnixTimeSeconds() < _failUntil) return;
        var hosts = _db.HostsForGeoIp(100);
        if (hosts.Count == 0) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var ips = hosts.Select(ToIp).Where(ip => ip != null).Cast<string>().ToList();
        if (ips.Count == 0) return;

        string? body = null;
        try
        {
            body = await PostDirect(ips, ct).ConfigureAwait(false);
        }
        catch
        {
            body = await PostViaProxy(ips, ct).ConfigureAwait(false);
        }
        if (body == null)
        {
            _failUntil = now + 600; // both paths failed — back off 10 minutes
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("status", out var st) && st.GetString() != "success") continue;
                if (!item.TryGetProperty("query", out var q) || !item.TryGetProperty("countryCode", out var cc)) continue;
                var ipStr = q.GetString();
                var code = cc.GetString();
                if (ipStr == null || code == null || code.Length == 0) continue;
                if (IPAddress.TryParse(ipStr, out var ip))
                    _db.SetRegion(ip.GetAddressBytes(), code.ToUpperInvariant(), now);
            }
        }
        catch { /* malformed body — try again next cycle */ }
    }

    private async Task<string?> PostDirect(List<string> ips, CancellationToken ct)
    {
        using var content = new StringContent(JsonSerializer.Serialize(ips), Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync(Endpoint, content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static async Task<string?> PostViaProxy(List<string> ips, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"packetly-geoip-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(ips), ct).ConfigureAwait(false);
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "curl",
                Arguments = $"-s --max-time 20 --socks5-hostname {Proxy} -H \"Content-Type: application/json\" -d @{tmp} {Endpoint}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return null;
            var stdout = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return p.ExitCode == 0 && stdout.TrimStart().StartsWith("[") ? stdout : null;
        }
        catch { return null; }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    private static string? ToIp(byte[] bytes) => bytes.Length switch
    {
        4 => new IPAddress(bytes).ToString(),
        // ip-api's free batch endpoint is IPv4-only; skip IPv6 hosts
        _ => null,
    };
}
