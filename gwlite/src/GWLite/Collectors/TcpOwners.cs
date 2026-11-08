using System.Net;
using System.Runtime.InteropServices;
using GWLite.Interop;

namespace GWLite.Collectors;

/// <summary>
/// Point-in-time snapshot of the TCP owner table keyed by remote endpoint.
/// Used to attribute flows whose kernel-reported PID cannot be resolved
/// (intercepted, proxied or translated connections) to the process that
/// actually owns the live connection.
/// </summary>
internal static class TcpOwners
{
    public static Dictionary<(uint Addr, ushort Port), int> Snapshot()
    {
        var map = new Dictionary<(uint, ushort), int>();
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
                if (ret != 0) return map;
                break;
            }

            int count = Marshal.ReadInt32(buf);
            int rowSize = Marshal.SizeOf<NativeMethods.MIB_TCPROW_OWNER_PID>();
            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<NativeMethods.MIB_TCPROW_OWNER_PID>(buf + 4 + i * rowSize);
                if (row.State != NativeMethods.TCP_STATE_ESTABLISHED) continue;
                ushort remotePort = NativeMethods.NetPortToHost(row.RemotePort);
                map[(row.RemoteAddr, remotePort)] = (int)row.OwningPid;
            }
            return map;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    public static string KeyOf(IPAddress remote, int port) => $"{remote}:{port}";
}
