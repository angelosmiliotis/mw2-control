using System.Buffers.Binary;
using System.Net;

namespace MW2Control.Network;

/// <summary>Only parses IPv4 UDP header metadata. Payload is neither copied nor retained.</summary>
public static class PacketMetadata
{
    public static bool SupportsLinkType(int linkType) => linkType is 0 or 1 or 12 or 101 or 108 or 228;
    public static bool TryParse(ReadOnlySpan<byte> frame, int linkType, int wireLength, out UdpMetadata? packet)
    {
        packet = null;
        if (wireLength < frame.Length || !SupportsLinkType(linkType)) return false;
        var offset = 0;
        if (linkType == 1)
        {
            if (frame.Length < 14) return false;
            offset = 14;
            var protocol = BinaryPrimitives.ReadUInt16BigEndian(frame[12..]);
            for (var tags = 0; protocol is 0x8100 or 0x88a8; tags++)
            {
                if (tags >= 2 || frame.Length < offset + 4) return false;
                protocol = BinaryPrimitives.ReadUInt16BigEndian(frame[(offset + 2)..]);
                offset += 4;
            }
            if (protocol != 0x0800) return false;
        }
        else if (linkType is 0 or 108)
        {
            if (frame.Length < 4) return false;
            var family = linkType == 0 ? BinaryPrimitives.ReadUInt32LittleEndian(frame) : BinaryPrimitives.ReadUInt32BigEndian(frame);
            if (family != 2) return false;
            offset = 4;
        }
        if (frame.Length < offset + 20 || frame[offset] >> 4 != 4) return false;
        var ip = frame[offset..];
        var header = (ip[0] & 15) * 4;
        if (header < 20 || ip.Length < header + 8 || ip[9] != 17) return false;
        var total = BinaryPrimitives.ReadUInt16BigEndian(ip[2..]);
        if (total < header + 8 || total > wireLength - offset ||
            (BinaryPrimitives.ReadUInt16BigEndian(ip[6..]) & 0x3fff) != 0) return false;
        var udp = ip[header..];
        var length = BinaryPrimitives.ReadUInt16BigEndian(udp[4..]);
        if (length < 8 || length != total - header) return false;
        var sourcePort = BinaryPrimitives.ReadUInt16BigEndian(udp);
        var destinationPort = BinaryPrimitives.ReadUInt16BigEndian(udp[2..]);
        if (sourcePort == 0 || destinationPort == 0) return false;
        packet = new(new IPAddress(ip.Slice(12, 4)), sourcePort, new IPAddress(ip.Slice(16, 4)), destinationPort, length);
        return true;
    }
}

public sealed class PeerAggregator
{
    private readonly Dictionary<string, Peer> peers = [];
    private readonly Func<DateTimeOffset> clock;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(15);
    private sealed record Sample(DateTimeOffset Time, int Bytes, bool Sent);
    private sealed class Peer { public readonly Queue<Sample> Samples = new(); }
    public PeerAggregator(Func<DateTimeOffset>? clock = null) => this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    public void Clear() => peers.Clear();
    public void Add(UdpMetadata packet, GameSession session)
    {
        Prune();
        if (packet.Bytes < 8) return;
        bool Matches(IPAddress ip, ushort port) => session.LocalAddresses.Contains(ip) && session.Endpoints.Any(e => e.Port == port && (e.Address.Equals(IPAddress.Any) || e.Address.Equals(ip)));
        var sent = Matches(packet.Source, packet.SourcePort);
        var received = Matches(packet.Destination, packet.DestinationPort);
        // Local-to-local or ambiguous packets cannot identify a remote peer.
        if (sent == received) return;
        var remote = sent ? packet.Destination : packet.Source;
        if (session.LocalAddresses.Contains(remote) || IPAddress.IsLoopback(remote) || remote.Equals(IPAddress.Any) || remote.GetAddressBytes()[0] >= 224) return;
        var key = remote.ToString();
        if (!peers.TryGetValue(key, out var peer))
        {
            if (peers.Count >= 256) return;
            peers[key] = peer = new();
        }
        // Bound metadata even during floods; counters represent the retained rolling sample window.
        if (peer.Samples.Count >= 4096) peer.Samples.Dequeue();
        peer.Samples.Enqueue(new(clock(), packet.Bytes, sent));
    }
    private void Prune()
    {
        var cutoff = clock() - Window;
        foreach (var key in peers.Keys.ToArray())
        {
            var samples = peers[key].Samples;
            while (samples.TryPeek(out var sample) && sample.Time <= cutoff) samples.Dequeue();
            if (samples.Count == 0) peers.Remove(key);
        }
    }
    public IReadOnlyList<PeerSnapshot> Snapshot()
    {
        Prune();
        var items = peers.Select(pair =>
        {
            var samples = pair.Value.Samples.ToArray();
            var sent = samples.Where(s => s.Sent).Sum(s => (long)s.Bytes);
            var received = samples.Where(s => !s.Sent).Sum(s => (long)s.Bytes);
            return new PeerSnapshot(pair.Key, sent, received, samples.Length, (sent + received) / Window.TotalSeconds, samples[^1].Time, false);
        }).OrderByDescending(p => p.BytesPerSecond).ThenBy(p => p.Address, StringComparer.Ordinal).ToArray();
        if (items.Length > 0) items[0] = items[0] with { IsTrafficLeader = true };
        return items;
    }
}
