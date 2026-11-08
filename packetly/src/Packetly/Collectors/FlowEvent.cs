using System.Net;

namespace Packetly.Collectors;

public readonly record struct FlowEvent(long Ts, int Pid, IPAddress Remote, int Port, int Proto, long In, long Out);

internal interface ITrafficCollector : IDisposable
{
    bool Running { get; }
}
