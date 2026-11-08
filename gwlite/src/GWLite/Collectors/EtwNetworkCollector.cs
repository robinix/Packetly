using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace GWLite.Collectors;

/// <summary>
/// Real-time collector on the NT Kernel Logger (NetworkTCPIP keyword).
/// Payload layouts follow the Microsoft-Windows-Kernel-Network manifest
/// (PID is always field 0, then size, then addresses, then ports); the typed
/// TraceEvent classes are used only for dispatch. Requires elevation; when
/// the session cannot be created Start returns false and the caller falls
/// back to PollCollector.
/// </summary>
internal sealed class EtwNetworkCollector : IDisposable
{
    private TraceEventSession? _session;
    private Thread? _thread;

    public bool Running { get; private set; }
    public string? Error { get; private set; }

    public bool Start(Action<FlowEvent> onEvent)
    {
        try
        {
            _session = new TraceEventSession(KernelTraceEventParser.KernelSessionName);
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            _session?.Dispose();
            _session = null;
            return false;
        }

        Running = true;
        _thread = new Thread(() =>
        {
            try
            {
                using var source = new ETWTraceEventSource(KernelTraceEventParser.KernelSessionName, TraceEventSourceType.Session);

                // IPv4 TCP: PID u32, size u32, daddr 4B, saddr 4B, dport u16, sport u16
                source.Kernel.TcpIpSend += d => Emit(onEvent, d, V4: true, isSend: true, proto: 6);
                source.Kernel.TcpIpRecv += d => Emit(onEvent, d, V4: true, isSend: false, proto: 6);
                // IPv4 UDP (manifest ids 42/43): same layout as TCP recv
                source.Kernel.UdpIpSend += d => Emit(onEvent, d, V4: true, isSend: true, proto: 17);
                source.Kernel.UdpIpRecv += d => Emit(onEvent, d, V4: true, isSend: false, proto: 17);

                // IPv6 (ids 26/27/58/59): PID u32, size u32, daddr 16B, saddr 16B, dport u16, sport u16
                source.Kernel.TcpIpSendIPV6 += d => Emit(onEvent, d, V4: false, isSend: true, proto: 6);
                source.Kernel.TcpIpRecvIPV6 += d => Emit(onEvent, d, V4: false, isSend: false, proto: 6);
                source.Kernel.UdpIpSendIPV6 += d => Emit(onEvent, d, V4: false, isSend: true, proto: 17);
                source.Kernel.UdpIpRecvIPV6 += d => Emit(onEvent, d, V4: false, isSend: false, proto: 17);

                source.Process();
            }
            catch (Exception ex)
            {
                Error = "ETW stream ended: " + ex.Message;
            }
            finally
            {
                Running = false;
            }
        })
        {
            IsBackground = true,
            Name = "gwlite-etw",
        };
        _thread.Start();
        return true;
    }

    public void Stop()
    {
        try { _session?.Stop(); } catch { /* session may already be gone */ }
        _thread?.Join(3000);
        _session?.Dispose();
        _session = null;
    }

    public void Dispose() => Stop();

    private static void Emit(Action<FlowEvent> on, TraceEvent d, bool V4, bool isSend, int proto)
    {
        if (d.EventDataLength < (V4 ? 20 : 44)) return;

        int pid = Marshal.ReadInt32(d.DataStart, 0);
        long size = Marshal.ReadInt32(d.DataStart, 4);
        if (size <= 0) return;
        if (pid == 0) pid = d.ProcessID; // payload pid should always exist; header is a fallback

        IPAddress remote;
        int remotePort;
        if (V4)
        {
            var addr = new byte[4];
            Marshal.Copy(d.DataStart + (isSend ? 8 : 12), addr, 0, 4);
            remote = new IPAddress(addr);
            remotePort = NetPort(d.DataStart + (isSend ? 16 : 18)); // send: dport; recv: sport
        }
        else
        {
            var addr = new byte[16];
            Marshal.Copy(d.DataStart + (isSend ? 8 : 24), addr, 0, 16);
            remote = new IPAddress(addr);
            remotePort = NetPort(d.DataStart + (isSend ? 40 : 42));
        }

        on(isSend
            ? new FlowEvent(Unix(d.TimeStamp), pid, remote, remotePort, proto, 0, size)
            : new FlowEvent(Unix(d.TimeStamp), pid, remote, remotePort, proto, size, 0));
    }

    private static int NetPort(IntPtr p)
    {
        int v = Marshal.ReadInt16(p) & 0xFFFF;
        return ((v & 0xFF) << 8) | (v >> 8); // u16 in network byte order
    }

    // TraceEvent's TimeStamp carries local-time ticks with Kind=Unspecified
    // (observed: stored epochs were ahead by exactly the local UTC offset).
    // Convert by explicit Kind: Utc ticks stay UTC, everything else is local.
    private static long Unix(DateTime ts) => ts.Kind switch
    {
        DateTimeKind.Utc => new DateTimeOffset(ts.Ticks, TimeSpan.Zero).ToUnixTimeSeconds(),
        _ => new DateTimeOffset(ts.Ticks, TimeZoneInfo.Local.GetUtcOffset(ts)).ToUnixTimeSeconds(),
    };
}
