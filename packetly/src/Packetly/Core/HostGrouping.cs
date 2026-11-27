using System.Net;

namespace Packetly.Core;

/// <summary>
/// Normalizes unresolved/system process attributions and groups unidentified
/// PIDs that talk to the same remote domain into a single clean entry so
/// short-lived or protected processes never clutter the UI as "unknown:pidNNNN".
/// </summary>
internal static class HostGrouping
{
    private static readonly HashSet<string> TwoPartTlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "co.uk", "org.uk", "ac.uk", "gov.uk",
        "com.au", "net.au", "org.au", "edu.au",
        "com.br", "net.br", "org.br",
        "co.jp", "ne.jp", "or.jp",
        "co.kr", "or.kr",
        "com.cn", "net.cn", "org.cn",
        "com.tr", "co.in", "net.in", "org.in",
        "com.mx", "co.nz", "co.za", "com.sg", "com.hk", "com.tw",
    };

    public static bool IsPseudoPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return path.StartsWith("unknown:", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("unidentified:", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("name:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts a clean registrable domain key (e.g. "googlevideo.com",
    /// "cloudflare.com", "bbc.co.uk") from a reverse-DNS hostname, or falls
    /// back to the IP string when no hostname is known yet.
    /// </summary>
    public static string GroupForHost(string? hostname, string? ip)
    {
        var domain = ExtractDomain(hostname);
        if (!string.IsNullOrEmpty(domain)) return domain;

        if (!string.IsNullOrWhiteSpace(ip))
        {
            var trimmed = ip.Trim();
            if (IPAddress.TryParse(trimmed, out var parsed))
            {
                if (IsLocalIp(parsed)) return "local-network";
                return parsed.ToString();
            }
            return trimmed.ToLowerInvariant();
        }
        return "general";
    }

    public static string? ExtractDomain(string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname)) return null;
        var h = hostname.Trim().TrimEnd('.').ToLowerInvariant();
        if (h.Length == 0) return null;
        if (IPAddress.TryParse(h, out _)) return null;

        var parts = h.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1) return h;
        if (parts.Length == 2) return $"{parts[0]}.{parts[1]}";

        var lastTwo = $"{parts[^2]}.{parts[^1]}";
        if (TwoPartTlds.Contains(lastTwo))
            return $"{parts[^3]}.{lastTwo}";

        return lastTwo;
    }

    public static bool IsIpGroupKey(string groupKey)
    {
        return !string.IsNullOrWhiteSpace(groupKey) && IPAddress.TryParse(groupKey.Trim(), out _);
    }

    public static string DisplayName(string groupKey)
    {
        if (string.IsNullOrWhiteSpace(groupKey) || groupKey == "general")
            return "Background Traffic";
        if (groupKey == "local-network")
            return "Local Network (System)";
        return $"{groupKey} (System)";
    }

    public static string AppDisplayName(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Background Traffic";
        if (path.StartsWith("unidentified:", StringComparison.OrdinalIgnoreCase))
            return DisplayName(path["unidentified:".Length..]);
        if (path.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
        {
            var n = path["name:".Length..].Trim();
            return n.Length > 0 ? n : "System Process";
        }
        if (path.StartsWith("unknown:", StringComparison.OrdinalIgnoreCase))
            return "Background Traffic";
        return Path.GetFileName(path);
    }

    private static bool IsLocalIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        var b = ip.GetAddressBytes();
        if (b.Length == 4)
        {
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 169 && b[1] == 254) return true;
            if (b[0] >= 224) return true; // multicast / broadcast
        }
        else if (b.Length == 16)
        {
            // Link-local fe80::/10, ULA fc00::/7, multicast ff00::/8
            if ((b[0] == 0xFE && (b[1] & 0xC0) == 0x80) || (b[0] & 0xFE) == 0xFC || b[0] == 0xFF)
                return true;
        }
        return false;
    }
}
