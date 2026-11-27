using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Packetly.Interop;

namespace Packetly.Collectors;

/// <summary>
/// Keeps a thread-safe live PID → image path map. Full paths come from
/// QueryFullProcessImageNameW; when a process cannot be opened (protected
/// processes, system services) the snapshot's exe name is recorded immediately
/// as a "name:" fallback while still retrying full-path resolution up to
/// MaxResolveAttempts times.
/// </summary>
internal sealed class ProcessTracker
{
    private const int MaxResolveAttempts = 3;
    private readonly ConcurrentDictionary<int, string> _paths = new();
    private readonly ConcurrentDictionary<int, int> _attempts = new();
    private volatile Dictionary<int, string> _lastAlive = new();

    public string GetPath(int pid)
    {
        if (pid == 0 || pid == 4) return "name:System";
        return _paths.TryGetValue(pid, out var p) ? p : "";
    }

    public int Count => _paths.Count;

    public string? LastDiagnostic { get; private set; }
    public int LastOpenError { get; private set; }
    public int LastQueryError { get; private set; }

    public bool WasInLastSnapshot(int pid) => _lastAlive.ContainsKey(pid);

    /// <summary>
    /// Fast on-demand resolution for PIDs that spawned after the last 2s
    /// snapshot refresh — catches short-lived processes before they exit.
    /// </summary>
    public string TryResolveLive(int pid)
    {
        if (pid == 0 || pid == 4) return "name:System";
        if (_paths.TryGetValue(pid, out var existing) && !existing.StartsWith("name:", StringComparison.Ordinal))
            return existing;

        var full = QueryProcessPath(pid);
        if (full != null)
        {
            _paths[pid] = full;
            _attempts[pid] = MaxResolveAttempts;
            return full;
        }
        return existing ?? "";
    }

    public void Refresh()
    {
        var alive = new Dictionary<int, string>();
        var h = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (h == IntPtr.Zero || h == (IntPtr)(-1))
        {
            LastDiagnostic = $"CreateToolhelp32Snapshot failed, err={Marshal.GetLastWin32Error()}";
            return;
        }
        try
        {
            var e = new NativeMethods.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<NativeMethods.PROCESSENTRY32W>() };
            if (!NativeMethods.Process32FirstW(h, ref e))
            {
                LastDiagnostic = $"Process32FirstW failed, err={Marshal.GetLastWin32Error()} (dwSize={e.dwSize})";
                return;
            }
            do
            {
                alive[(int)e.th32ProcessID] = e.szExeFile;
            } while (NativeMethods.Process32NextW(h, ref e));
            LastDiagnostic = $"snapshot ok: {alive.Count} processes";
            _lastAlive = alive;
        }
        finally
        {
            NativeMethods.CloseHandle(h);
        }

        foreach (var pid in _paths.Keys)
        {
            if (!alive.ContainsKey(pid))
            {
                _paths.TryRemove(pid, out _);
                _attempts.TryRemove(pid, out _);
            }
        }

        foreach (var (pid, exe) in alive)
        {
            if (pid == 0 || pid == 4)
            {
                _paths[pid] = "name:System";
                _attempts[pid] = MaxResolveAttempts;
                continue;
            }
            if (_paths.TryGetValue(pid, out var known))
            {
                // Retry the full-path resolution for entries sitting on the name fallback.
                if (known.StartsWith("name:", StringComparison.Ordinal) && _attempts.GetValueOrDefault(pid) < MaxResolveAttempts)
                    TryResolve(pid, exe);
                continue;
            }
            TryResolve(pid, exe);
        }
    }

    private string? QueryProcessPath(int pid)
    {
        var sb = new StringBuilder(1024);
        int size = sb.Capacity;
        var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
        {
            LastOpenError = Marshal.GetLastWin32Error();
            return null;
        }
        try
        {
            if (NativeMethods.QueryFullProcessImageNameW(h, 0, sb, ref size))
                return sb.ToString();
            LastQueryError = Marshal.GetLastWin32Error();
            return null;
        }
        finally
        {
            NativeMethods.CloseHandle(h);
        }
    }

    private void TryResolve(int pid, string exeName)
    {
        var full = QueryProcessPath(pid);
        if (full != null)
        {
            _paths[pid] = full;
            _attempts[pid] = MaxResolveAttempts;
        }
        else
        {
            var n = _attempts.GetValueOrDefault(pid) + 1;
            _attempts[pid] = n;
            // Immediately record the snapshot exe name on attempt 1 so protected
            // system/Store processes are never misattributed as unknown while
            // still retrying full-path resolution on subsequent refreshes.
            if (!string.IsNullOrWhiteSpace(exeName))
                _paths[pid] = "name:" + exeName;
        }
    }
}
