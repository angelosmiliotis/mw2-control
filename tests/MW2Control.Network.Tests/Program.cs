using System.Buffers.Binary;
using System.Net;
using MW2Control.Network;

var tests = new Suite();
tests.Run("Ethernet IPv4 UDP metadata", () =>
{
    var data = Fixtures.Frame(1);
    Check.That(PacketMetadata.TryParse(data, 1, data.Length, out var packet));
    Check.Equal("8.8.8.8", packet!.Destination.ToString()); Check.Equal((ushort)28960, packet.SourcePort); Check.Equal(8, packet.Bytes);
});
tests.Run("raw and loopback link types", () =>
{
    foreach (var type in new[] { 0, 12, 101, 108, 228 })
    { var frame = Fixtures.Frame(type); Check.That(PacketMetadata.TryParse(frame, type, frame.Length, out _)); }
});
tests.Run("VLAN and QinQ bounds", () =>
{
    var raw = Fixtures.Frame(12);
    foreach (var tags in new[] { 1, 2 })
    {
        var frame = new byte[14 + 4 * tags + raw.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), 0x8100);
        for (var i = 0; i < tags; i++) BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16 + i * 4), (ushort)(i == tags - 1 ? 0x800 : 0x8100));
        raw.CopyTo(frame, 14 + 4 * tags);
        Check.That(PacketMetadata.TryParse(frame, 1, frame.Length, out _));
        Check.That(!PacketMetadata.TryParse(frame.AsSpan(0, 17), 1, frame.Length, out _));
    }
});
tests.Run("truncated headers and invalid wire length rejected", () =>
{
    var frame = Fixtures.Frame(1);
    for (var size = 0; size < frame.Length; size++) Check.That(!PacketMetadata.TryParse(frame.AsSpan(0, size), 1, frame.Length, out _));
    Check.That(!PacketMetadata.TryParse(frame, 1, 2, out _));
});
tests.Run("unsupported link types, IPv6, TCP and invalid lengths rejected", () =>
{
    var frame = Fixtures.Frame(12);
    Check.That(!PacketMetadata.TryParse(frame, 999, frame.Length, out _));
    foreach (var modification in new Action<byte[]>[] { a => a[0] = 0x65, a => a[9] = 6, a => a[0] = 0x44, a => a[25] = 1, a => a[3] = 80, a => a[20] = a[21] = 0 })
    { var modified = (byte[])frame.Clone(); modification(modified); Check.That(!PacketMetadata.TryParse(modified, 12, modified.Length, out _)); }
});
tests.Run("all fragmented IPv4 packets skipped", () =>
{
    foreach (var flag in new ushort[] { 0x2000, 1, 0x2001 })
    { var frame = Fixtures.Frame(12); BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(6), flag); Check.That(!PacketMetadata.TryParse(frame, 12, frame.Length, out _)); }
});
tests.Run("payload truncation accepted only when header lengths fit original wire", () =>
{
    var frame = Fixtures.Frame(12);
    BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 100);
    BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(24), 80);
    Check.That(PacketMetadata.TryParse(frame, 12, 100, out var metadata)); Check.Equal(80, metadata!.Bytes);
    Check.That(!PacketMetadata.TryParse(frame, 12, 99, out _));
});
tests.Run("endpoint attribution and send/receive directions", () =>
{
    var peers = new PeerAggregator(); var session = Fixtures.Session();
    peers.Add(Fixtures.Packet(), session); peers.Add(Fixtures.Packet() with { Source = IPAddress.Parse("8.8.8.8"), SourcePort = 30000, Destination = Fixtures.Local, DestinationPort = 28960 }, session);
    var peer = peers.Snapshot().Single(); Check.Equal(8L, peer.SentBytes); Check.Equal(8L, peer.ReceivedBytes); Check.Equal(2, peer.Packets); Check.That(peer.IsTrafficLeader);
});
tests.Run("unrelated local ports and nonlocal addresses ignored", () =>
{
    var peers = new PeerAggregator(); var session = Fixtures.Session();
    peers.Add(Fixtures.Packet() with { SourcePort = 1234 }, session);
    peers.Add(Fixtures.Packet() with { Source = IPAddress.Parse("192.168.1.7") }, session);
    Check.Equal(0, peers.Snapshot().Count);
});
tests.Run("specific bind addresses and wildcard binds both matched correctly", () =>
{
    var peers = new PeerAggregator(); var session = Fixtures.Session() with { Endpoints = [new(Fixtures.Local, 28960)] };
    peers.Add(Fixtures.Packet(), session); Check.Equal(1, peers.Snapshot().Count);
    peers.Clear(); peers.Add(Fixtures.Packet(), session with { Endpoints = [new(IPAddress.Parse("192.168.1.7"), 28960)] }); Check.Equal(0, peers.Snapshot().Count);
});
tests.Run("rolling counters prune while snapshots receive no new packets", () =>
{
    var now = DateTimeOffset.UtcNow; var peers = new PeerAggregator(() => now);
    peers.Add(Fixtures.Packet(), Fixtures.Session()); now += TimeSpan.FromSeconds(14);
    peers.Add(Fixtures.Packet() with { Bytes = 30 }, Fixtures.Session()); now += TimeSpan.FromSeconds(2);
    var peer = peers.Snapshot().Single(); Check.Equal(30L, peer.SentBytes); Check.Equal(2d, peer.BytesPerSecond);
    now += TimeSpan.FromSeconds(15); Check.Equal(0, peers.Snapshot().Count);
});
tests.Run("peer and sample state bounded", () =>
{
    var peers = new PeerAggregator();
    for (var i = 0; i < 300; i++) peers.Add(Fixtures.Packet() with { Destination = IPAddress.Parse($"8.9.{i / 250}.{i % 250 + 1}") }, Fixtures.Session());
    Check.Equal(256, peers.Snapshot().Count);
    peers.Clear(); for (var i = 0; i < 5000; i++) peers.Add(Fixtures.Packet(), Fixtures.Session());
    Check.Equal(4096, peers.Snapshot().Single().Packets);
});
tests.Run("capture constructor has no native/source activity; explicit Start and Stop dispose reader", () =>
{
    var backend = new FakeCapture(); var source = new FakeSource();
    using var service = new LobbyNetworkService(backend, source);
    Check.Equal(0, backend.Lists); Check.Equal(0, source.Reads); Check.That(!service.Snapshot().Capturing);
    Check.That(service.Start("fake").Success); Check.That(service.Snapshot().Capturing);
    service.Stop(); Check.That(backend.Device.Disposed); Check.That(!service.Snapshot().Capturing); Check.That(!backend.Device.ClosedDuringRead);
});
tests.Run("missing target and unowned ports refuse capture", () =>
{
    var backend = new FakeCapture(); var source = new FakeSource { Session = null };
    using var service = new LobbyNetworkService(backend, source);
    Check.That(!service.Start("fake").Success); Check.Equal(0, backend.Opens);
    source.Session = Fixtures.Session() with { Endpoints = [] };
    Check.That(!service.Start("fake").Success); Check.Equal(0, backend.Opens);
});
tests.Run("endpoint refresh then session exit updates filter and closes reader", () =>
{
    var backend = new FakeCapture(); var source = new TransitionSource();
    using var service = new LobbyNetworkService(backend, source);
    Check.That(service.Start("fake").Success);
    Check.That(backend.Device.PortsChanged.Wait(TimeSpan.FromSeconds(4)));
    Check.That(backend.Device.Closed.Wait(TimeSpan.FromSeconds(4)));
    service.Stop();
    Check.That(!service.Snapshot().Capturing); Check.Equal<int?>(null, service.Snapshot().ProcessId);
    Check.That(!backend.Device.ClosedDuringRead); Check.That(backend.Device.LastPorts.SequenceEqual(new ushort[] { 30001 }));
});
tests.Run("address validation rejects local/CIDR/wildcard/alternate forms", () =>
{
    foreach (var ip in new[] { "", "8.8.8.8/32", "*", "8.8.8", "134744072", " 8.8.8.8", "0.0.0.0", "127.0.0.1", "10.2.3.4", "192.168.1.1", "172.16.0.1", "169.254.1.1", "100.64.1.1", "224.0.0.1", "255.255.255.255", "192.0.2.1", "::1", "::", "fe80::1", "fc00::1", "ff02::1", "2001:db8::1", "::ffff:8.8.8.8" })
        Check.Throws(() => FirewallValidation.PublicAddress(ip));
    Check.Throws(() => FirewallValidation.PublicAddress("8.8.8.8", [IPAddress.Parse("8.8.8.8")]));
    Check.Equal("8.8.8.8", FirewallValidation.PublicAddress("8.8.8.8"));
    Check.Equal("2606:4700:4700::1111", FirewallValidation.PublicAddress("2606:4700:4700::1111"));
});
tests.Run("public IPv4 final octets do not imply a remote subnet boundary", () =>
{
    Check.Equal("8.8.8.0", FirewallValidation.PublicAddress("8.8.8.0"));
    Check.Equal("8.8.8.255", FirewallValidation.PublicAddress("8.8.8.255"));
    var policy = new FakePolicy(); var service = Fixtures.Firewall(policy);
    Check.That(service.Block("8.8.8.0", Fixtures.Path, true).Success);
    Check.That(service.Block("8.8.8.255", Fixtures.Path, true).Success);
    Check.That(policy.Rules.All(r => r.Address is "8.8.8.0" or "8.8.8.255"));
    Check.Equal(4, policy.Rules.Count);
});
tests.Run("network changes preserve owned block listing and removal but reject new local blocks", () =>
{
    var policy = new FakePolicy(); var localAddresses = new List<IPAddress>();
    var service = new FirewallBlockService(policy, _ => true, () => localAddresses);
    Check.That(service.Block("8.8.8.8", Fixtures.Path, true).Success);
    var id = service.ListOwnedBlocks().Single().Id;
    localAddresses.Add(IPAddress.Parse("8.8.8.8"));
    Check.Equal(id, service.ListOwnedBlocks().Single().Id);
    var adds = policy.AddCalls;
    Check.That(!service.Block("8.8.8.8", Fixtures.Path, true).Success);
    Check.Equal(adds, policy.AddCalls);
    Check.That(service.Unblock(id, true).Success);
    Check.Equal(0, policy.Rules.Count);
});
tests.Run("firewall requires absolute existing iw4mp path", () =>
{
    foreach (var path in new[] { "iw4mp.exe", @"C:\games\other.exe", @"\\server\games\iw4mp.exe" }) Check.Throws(() => FirewallValidation.GamePath(path, _ => true));
    Check.Throws(() => FirewallValidation.GamePath(Fixtures.Path, _ => false));
});
tests.Run("confirmation gates prevent even policy reads", () =>
{
    var policy = new FakePolicy(); var service = Fixtures.Firewall(policy);
    Check.That(!service.Block("8.8.8.8", Fixtures.Path, false).Success); Check.That(!service.Unblock("anything", false).Success);
    Check.Equal(0, policy.Reads); Check.Equal(0, policy.AddCalls);
});
tests.Run("scoped paired rules and idempotent duplicate block", () =>
{
    var policy = new FakePolicy(); var service = Fixtures.Firewall(policy);
    Check.That(service.Block("8.8.8.8", Fixtures.Path, true).Success); Check.Equal(2, policy.Rules.Count);
    Check.That(policy.Rules.All(r => r.Protocol == 17 && r.Action == 0 && r.GamePath == Fixtures.Path && r.Address == "8.8.8.8"));
    Check.That(service.Block("8.8.8.8", Fixtures.Path, true).Success); Check.Equal(2, policy.AddCalls); Check.Equal(1, service.ListOwnedBlocks().Count);
});
tests.Run("second-add failure rolls back only newly created rules", () =>
{
    var policy = new FakePolicy { FailAdd = 2 }; var service = Fixtures.Firewall(policy);
    policy.Rules.Add(new("someone else's rule", "other", "9.9.9.9", Fixtures.Path, 1));
    Check.That(!service.Block("8.8.8.8", Fixtures.Path, true).Success); Check.Equal(1, policy.Rules.Count); Check.Equal("someone else's rule", policy.Rules.Single().Name);
});
tests.Run("add failure after creation rolls back its own new entry", () =>
{
    var policy = new FakePolicy { FailAdd = 2, ThrowAfterAdd = true }; var service = Fixtures.Firewall(policy);
    Check.That(!service.Block("8.8.8.8", Fixtures.Path, true).Success); Check.Equal(0, policy.Rules.Count);
});
tests.Run("failed rollback removal is reported rather than claimed complete", () =>
{
    var policy = new FakePolicy { FailAdd = 2, IgnoreRemove = true }; var service = Fixtures.Firewall(policy);
    var result = service.Block("8.8.8.8", Fixtures.Path, true);
    Check.That(!result.Success); Check.That(result.Message.Contains("could not be rolled back", StringComparison.Ordinal));
    Check.Equal(1, policy.Rules.Count);
});
tests.Run("partial pair repair failure preserves preexisting owned rule", () =>
{
    var policy = new FakePolicy(); var service = Fixtures.Firewall(policy);
    Check.That(service.Block("8.8.8.8", Fixtures.Path, true).Success);
    policy.Rules.RemoveAt(1); var original = policy.Rules.Single(); policy.FailAdd = 3;
    Check.That(!service.Block("8.8.8.8", Fixtures.Path, true).Success); Check.Equal(original, policy.Rules.Single());
});
tests.Run("unblock deletes only validated owned IDs", () =>
{
    var policy = new FakePolicy(); var service = Fixtures.Firewall(policy);
    policy.Rules.Add(new("external", "other", "9.9.9.9", Fixtures.Path, 1));
    Check.That(service.Block("8.8.8.8", Fixtures.Path, true).Success);
    Check.That(!service.Unblock("external", true).Success);
    Check.That(service.Unblock(service.ListOwnedBlocks().Single().Id, true).Success);
    Check.Equal("external", policy.Rules.Single().Name);
});
tests.Run("conflicting and duplicate owned names fail closed", () =>
{
    var policy = new FakePolicy(); var service = Fixtures.Firewall(policy);
    Check.That(service.Block("8.8.8.8", Fixtures.Path, true).Success); var id = service.ListOwnedBlocks().Single().Id;
    policy.Rules.Add(policy.Rules[0]); Check.That(!service.Unblock(id, true).Success); Check.Equal(3, policy.Rules.Count);
    policy.Rules.RemoveAt(2); policy.Rules[0] = policy.Rules[0] with { Protocol = 6 };
    Check.That(!service.Unblock(id, true).Success); Check.Equal(2, policy.Rules.Count);
});
return tests.Finish();

static class Fixtures
{
    public const string Path = @"C:\Games\iw4mp.exe";
    public static readonly IPAddress Local = IPAddress.Parse("192.168.1.5");
    public static GameSession Session() => new(5, 123, Path, [new(IPAddress.Any, 28960)], new HashSet<IPAddress> { Local });
    public static UdpMetadata Packet() => new(Local, 28960, IPAddress.Parse("8.8.8.8"), 30000, 8);
    public static FirewallBlockService Firewall(FakePolicy policy) => new(policy, _ => true, () => []);
    public static byte[] Frame(int dlt)
    {
        var offset = dlt == 1 ? 14 : dlt is 0 or 108 ? 4 : 0;
        var frame = new byte[offset + 28];
        if (dlt == 1) BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), 0x800);
        if (dlt == 0) BinaryPrimitives.WriteUInt32LittleEndian(frame, 2);
        if (dlt == 108) BinaryPrimitives.WriteUInt32BigEndian(frame, 2);
        var ip = frame.AsSpan(offset); ip[0] = 0x45; ip[9] = 17; BinaryPrimitives.WriteUInt16BigEndian(ip[2..], 28);
        Local.GetAddressBytes().CopyTo(ip[12..]); IPAddress.Parse("8.8.8.8").GetAddressBytes().CopyTo(ip[16..]);
        BinaryPrimitives.WriteUInt16BigEndian(ip[20..], 28960); BinaryPrimitives.WriteUInt16BigEndian(ip[22..], 30000); BinaryPrimitives.WriteUInt16BigEndian(ip[24..], 8);
        return frame;
    }
}
sealed class FakePolicy : IFirewallPolicy
{
    public List<FirewallRule> Rules { get; } = []; public int Reads, AddCalls, FailAdd; public bool ThrowAfterAdd, IgnoreRemove;
    public IReadOnlyList<FirewallRule> ReadRules() { Reads++; return Rules.ToArray(); }
    public void Add(FirewallRule rule) { AddCalls++; if (AddCalls == FailAdd && !ThrowAfterAdd) throw new UnauthorizedAccessException("fake denied"); Rules.Add(rule); if (AddCalls == FailAdd) throw new UnauthorizedAccessException("fake after mutation"); }
    public void Remove(string name) { if (!IgnoreRemove) Rules.RemoveAll(r => r.Name == name); }
}
sealed class TransitionSource : IGameEndpointSource
{
    private int reads;
    public GameSession? Read() => ++reads switch { 1 => Fixtures.Session(), 2 => Fixtures.Session() with { Endpoints = [new(IPAddress.Any, 30001)] }, _ => null };
}
sealed class FakeSource : IGameEndpointSource
{
    public int Reads; public GameSession? Session = Fixtures.Session();
    public GameSession? Read() { Reads++; return Session; }
}
sealed class FakeCapture : ICaptureBackend
{
    public int Lists, Opens; public FakeDevice Device = new();
    public IReadOnlyList<CaptureAdapter> ListAdapters() { Lists++; return [new("fake", "Synthetic")]; }
    public ICaptureDevice Open(string adapterId) { Opens++; return Device; }
}
sealed class FakeDevice : ICaptureDevice
{
    public bool Disposed, ClosedDuringRead; private bool reading; private int filters;
    public readonly ManualResetEventSlim PortsChanged = new(), Closed = new();
    public ushort[] LastPorts = [];
    public UdpMetadata? ReadPacket() { reading = true; try { Thread.Sleep(1); return null; } finally { reading = false; } }
    public void SetPorts(IReadOnlyList<ushort> ports) { LastPorts = ports.ToArray(); if (++filters > 1) PortsChanged.Set(); }
    public void Dispose() { ClosedDuringRead = reading; Disposed = true; Closed.Set(); }
}
sealed class Suite
{
    private int passed, failed;
    public void Run(string name, Action test) { try { test(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); } }
    public int Finish() { Console.WriteLine($"Results: {passed} passed, {failed} failed, {passed + failed} total"); return failed == 0 ? 0 : 1; }
}
static class Check
{
    public static void That(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
    public static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    public static void Throws(Action operation) { try { operation(); } catch { return; } throw new Exception("Expected rejection."); }
}
