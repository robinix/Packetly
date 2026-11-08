using System.Net;
using System.Runtime.InteropServices;
using GWLite.Interop;

namespace GWLite.Collectors;

/// <summary>
/// No-admin fallback collector. Polls the TCP owner table every second and
/// diffs the per-connection byte counters from GetPerTcpConnectionEStats.
/// TCP-only and blind to connections that live less than one poll, but needs
/// no elevation and no kernel session.
/// </summary>
internal sealed class PollCollector : IDisposable
{
    private readonly Dictionary<(uint La, ushort Lp, uint Ra, ushort Rp), (ulong In, ulong Out)> _last = new();
    private Thread? _thread;
    private volatile bool _stop;

    public bool Running { get; private set; }

    public void Start(Action<FlowEvent> onEvent)
    {
        Running = true;
        _thread = new Thread(() => Loop(onEvent)) { IsBackground = true, Name = "gwlite-poll" };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join(2000);
    }

    public void Dispose() => Stop();

    private void Loop(Action<FlowEvent> onEvent)
    {
        while (!_stop)
        {
            try
            {
                Poll(onEvent, DateTimeOffset.UtcNow);
            }
            catch
            {
                // table snapshots race with connection churn; next tick retries
            }
            Thread.Sleep(1000);
        }
        Running = false;
    }

    private void Poll(Action<FlowEvent> onEvent, DateTimeOffset now)
    {
        int size = 1 << 16;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            while (true)
            {
                uint ret = NativeMethods.GetExtendedTcpTable(buf, ref size, false, NativeMethods.AF_INET, NativeMethods.TCP_TABLE_OWNER_PID_ALL, 0);
                if (ret == NativeMethods.ERROR_INSUFFICIENT_BUFFER)
                {
                    Marshal.FreeHGlobal(buf);
                    buf = Marshal.AllocHGlobal(size);
                    continue;
                }
                if (ret != 0) return;
                break;
            }

            int count = Marshal.ReadInt32(buf);
            int rowSize = Marshal.SizeOf<NativeMethods.MIB_TCPROW_OWNER_PID>();
            var seen = new HashSet<(uint La, ushort Lp, uint Ra, ushort Rp)>();

            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<NativeMethods.MIB_TCPROW_OWNER_PID>(buf + 4 + i * rowSize);
                if (row.State != NativeMethods.TCP_STATE_ESTABLISHED) continue;

                ushort localPort = NativeMethods.NetPortToHost(row.LocalPort);
                ushort remotePort = NativeMethods.NetPortToHost(row.RemotePort);
                var key = (row.LocalAddr, localPort, row.RemoteAddr, remotePort);
                seen.Add(key);

                var tcpRow = new NativeMethods.MIB_TCPROW
                {
                    State = 0,
                    LocalAddr = row.LocalAddr,
                    LocalPort = row.LocalPort,
                    RemoteAddr = row.RemoteAddr,
                    RemotePort = row.RemotePort,
                };
                var rod = new byte[NativeMethods.TCP_ESTATS_DATA_ROD_V0_SIZE];
                if (NativeMethods.GetPerTcpConnectionEStats(ref tcpRow, NativeMethods.TCP_ESTATS_DATA_STATS,
                        IntPtr.Zero, 0, 0, rod, 0, rod.Length, IntPtr.Zero, IntPtr.Zero) != 0)
                    continue;

                ulong inb = BitConverter.ToUInt64(rod, 0);
                ulong outb = BitConverter.ToUInt64(rod, 8);

                if (_last.TryGetValue(key, out var prev) && inb >= prev.In && outb >= prev.Out)
                {
                    long dIn = (long)(inb - prev.In);
                    long dOut = (long)(outb - prev.Out);
                    if (dIn != 0 || dOut != 0)
                        onEvent(new FlowEvent(now.ToUnixTimeSeconds(), (int)row.OwningPid,
                            NativeMethods.UintToIpV4(row.RemoteAddr), remotePort, 6, dIn, dOut));
                }
                _last[key] = (inb, outb);
            }

            if (_last.Count > seen.Count * 2)
            {
                var gone = _last.Keys.Where(k => !seen.Contains(k)).ToList();
                foreach (var k in gone) _last.Remove(k);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }
}
