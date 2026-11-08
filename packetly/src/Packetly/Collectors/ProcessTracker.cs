using System.Runtime.InteropServices;
using System.Text;
using Packetly.Interop;

namespace Packetly.Collectors;

/// <summary>
/// Keeps a live PID → image path map. Full paths come from
/// QueryFullProcessImageNameW; when a process cannot be opened
/// (protected processes, races) the snapshot's exe name is used as a
/// "name:" fallback after a few attempts.
/// </summary>
internal sealed class ProcessTracker
{
    private const int MaxResolveAttempts = 3;
    private readonly Dictionary<int, string> _paths = new();
    private readonly Dictionary<int, int> _attempts = new();

    public string GetPath(int pid) => _paths.TryGetValue(pid, out var p) ? p : "";

    public int Count => _paths.Count;

    public string? LastDiagnostic { get; private set; }
    public int LastOpenError { get; private set; }
    public int LastQueryError { get; private set; }
    private Dictionary<int, string> _lastAlive = new();

    public bool WasInLastSnapshot(int pid) => _lastAlive.ContainsKey(pid);

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

        var dead = _paths.Keys.Where(p => !alive.ContainsKey(p)).ToList();
        foreach (var pid in dead)
        {
            _paths.Remove(pid);
            _attempts.Remove(pid);
        }

        foreach (var (pid, exe) in alive)
        {
            if (_paths.TryGetValue(pid, out var known))
            {
                // Retry the full-path resolution for entries stuck on the name fallback.
                if (known.StartsWith("name:", StringComparison.Ordinal) && _attempts.GetValueOrDefault(pid) < MaxResolveAttempts)
                    TryResolve(pid, exe);
                continue;
            }
            TryResolve(pid, exe);
        }
    }

    private void TryResolve(int pid, string exeName)
    {
        var sb = new StringBuilder(1024);
        int size = sb.Capacity;
        var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        string? full = null;
        if (h != IntPtr.Zero)
        {
            try
            {
                if (NativeMethods.QueryFullProcessImageNameW(h, 0, sb, ref size))
                    full = sb.ToString();
                else
                    LastQueryError = Marshal.GetLastWin32Error();
            }
            finally
            {
                NativeMethods.CloseHandle(h);
            }
        }
        else
        {
            LastOpenError = Marshal.GetLastWin32Error();
        }

        if (full != null)
        {
            _paths[pid] = full;
            _attempts[pid] = MaxResolveAttempts;
        }
        else
        {
            var n = _attempts.GetValueOrDefault(pid) + 1;
            _attempts[pid] = n;
            if (n >= MaxResolveAttempts)
                _paths[pid] = "name:" + exeName;
        }
    }
}
