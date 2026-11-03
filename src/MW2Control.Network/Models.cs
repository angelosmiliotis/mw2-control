using System.Net;

namespace MW2Control.Network;

public sealed record CaptureAdapter(string Id, string Label);
public sealed record PeerSnapshot(string Address, long SentBytes, long ReceivedBytes, int Packets, double BytesPerSecond, DateTimeOffset LastSeen, bool IsTrafficLeader);
public sealed record LobbySnapshot(bool Capturing, string Status, string GamePath, int? ProcessId, IReadOnlyList<PeerSnapshot> Peers);
public sealed record FirewallBlock(string Id, string Address, string GamePath, string Detail);
public sealed record NetworkResult(bool Success, string Message);
public sealed record LocalEndpoint(IPAddress Address, ushort Port);
public sealed record GameSession(int ProcessId, long StartTicks, string GamePath, IReadOnlyList<LocalEndpoint> Endpoints, IReadOnlySet<IPAddress> LocalAddresses);
public sealed record UdpMetadata(IPAddress Source, ushort SourcePort, IPAddress Destination, ushort DestinationPort, int Bytes);
public interface IGameEndpointSource { GameSession? Read(); }
public interface ICaptureDevice : IDisposable
{
    UdpMetadata? ReadPacket();
    void SetPorts(IReadOnlyList<ushort> ports);
}
public interface ICaptureBackend
{
    IReadOnlyList<CaptureAdapter> ListAdapters();
    ICaptureDevice Open(string adapterId);
}
