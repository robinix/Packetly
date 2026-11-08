using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace Packetly.Interop;

internal static class NativeMethods
{
    public const int AF_INET = 2;
    public const int TCP_TABLE_OWNER_PID_ALL = 4;
    public const uint TCP_STATE_ESTABLISHED = 5;
    public const int TCP_ESTATS_DATA_STATS = 1; // TcpConnectionEstatsData
    public const int TCP_ESTATS_DATA_ROD_V0_SIZE = 16; // DataBytesIn + DataBytesOut
    public const uint ERROR_INSUFFICIENT_BUFFER = 122;
    public const uint TH32CS_SNAPPROCESS = 0x2;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_TCPROW
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_TCPROW_OWNER_PID
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int dwOutBufLen, bool order, int family, int tableClass, int reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint GetPerTcpConnectionEStats(
        ref MIB_TCPROW row, int estatsType,
        IntPtr rw, int rwVersion, int rwSize,
        byte[] rod, int rodVersion, int rodSize,
        IntPtr rsSize, IntPtr rcSize);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase; // LONG in tlhelp32.h — not a pointer-sized field!
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder path, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    // Ports in the owner tables arrive in network byte order inside the low 16 bits.
    public static ushort NetPortToHost(uint portDword)
    {
        ushort net = (ushort)(portDword & 0xFFFF);
        return (ushort)((net << 8) | (net >> 8));
    }

    // dwAddr is in network byte order; least significant byte is the first octet.
    public static IPAddress UintToIpV4(uint addr) => new(BitConverter.GetBytes(addr));
}
