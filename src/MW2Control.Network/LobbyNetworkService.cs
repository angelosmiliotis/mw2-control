namespace MW2Control.Network;

public sealed class LobbyNetworkService : IDisposable
{
    private const string CaptureNotice = "IPv4 UDP metadata only; 15-second rolling counters (bounded samples). Traffic leader is not a confirmed host/player; relays may hide identity.";
    private readonly object lifecycle = new();
    private readonly object gate = new();
    private readonly ICaptureBackend backend;
    private readonly IGameEndpointSource source;
    private readonly PeerAggregator peers;
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private GameSession? session;
    private bool capturing, disposed;
    private string status = "Stopped. Capture starts only on explicit Start. IPv6 capture is unavailable.";
    public LobbyNetworkService() : this(new NpcapBackend(), new WindowsEndpointSource()) { }
    public LobbyNetworkService(ICaptureBackend backend, IGameEndpointSource source, Func<DateTimeOffset>? clock = null)
    { this.backend = backend; this.source = source; peers = new(clock); }
    public IReadOnlyList<CaptureAdapter> ListAdapters()
    {
        lock (lifecycle) { ObjectDisposedException.ThrowIf(disposed, this); return backend.ListAdapters(); }
    }
    public NetworkResult Start(string adapterId)
    {
        lock (lifecycle)
        {
            if (disposed) return new(false, "Network service is disposed.");
            StopWorker();
            ICaptureDevice? device = null;
            try
            {
                var current = source.Read() ?? throw new InvalidOperationException("Exactly one iw4mp.exe must be running before Start.");
                if (current.Endpoints.Count == 0) throw new InvalidOperationException("No unambiguous IPv4 UDP endpoints are owned by iw4mp.exe. IPv6 is unsupported.");
                if (!backend.ListAdapters().Any(a => a.Id == adapterId)) throw new InvalidOperationException("Select an available Npcap adapter.");
                device = backend.Open(adapterId);
                device.SetPorts(current.Endpoints.Select(e => e.Port).Distinct().ToArray());
                cancellation = new();
                lock (gate) { session = current; peers.Clear(); capturing = true; status = CaptureNotice; }
                var ownedDevice = device;
                var token = cancellation.Token;
                worker = Task.Run(() => Capture(ownedDevice, current, token));
                device = null;
                return new(true, CaptureNotice);
            }
            catch (Exception ex)
            {
                device?.Dispose();
                lock (gate) { capturing = false; session = null; peers.Clear(); status = "Capture unavailable: " + ex.Message; }
                return new(false, status);
            }
        }
    }
    private void Capture(ICaptureDevice device, GameSession initial, CancellationToken token)
    {
        try
        {
            using (device)
            {
                var current = initial;
                var refresh = DateTime.UtcNow.AddSeconds(1);
                while (!token.IsCancellationRequested)
                {
                    if (DateTime.UtcNow >= refresh)
                    {
                        var next = source.Read();
                        if (next is null || next.ProcessId != initial.ProcessId || next.StartTicks != initial.StartTicks ||
                            !string.Equals(next.GamePath, initial.GamePath, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Game session exited or changed. Start again to monitor a new session.");
                        if (next.Endpoints.Count == 0) throw new InvalidOperationException("Game IPv4 UDP endpoints disappeared. Start again after the lobby opens.");
                        if (!current.Endpoints.ToHashSet().SetEquals(next.Endpoints) || !current.LocalAddresses.SetEquals(next.LocalAddresses))
                        {
                            device.SetPorts(next.Endpoints.Select(e => e.Port).Distinct().ToArray());
                            lock (gate) peers.Clear();
                        }
                        current = next;
                        lock (gate) session = next;
                        refresh = DateTime.UtcNow.AddSeconds(1);
                    }
                    var packet = device.ReadPacket();
                    if (packet is not null) { lock (gate) peers.Add(packet, current); }
                    else if (token.WaitHandle.WaitOne(20)) break;
                }
            }
        }
        catch (Exception ex) { lock (gate) { peers.Clear(); session = null; status = "Capture stopped: " + ex.Message; } }
        finally { lock (gate) capturing = false; }
    }
    private void StopWorker()
    {
        cancellation?.Cancel();
        worker?.GetAwaiter().GetResult(); // Device is closed by its reader, never concurrently.
        worker = null;
        cancellation?.Dispose(); cancellation = null;
    }
    public void Stop()
    {
        lock (lifecycle)
        {
            StopWorker();
            lock (gate) { capturing = false; status = "Stopped. " + CaptureNotice; }
        }
    }
    public void ClearPeers() { lock (gate) peers.Clear(); }
    public LobbySnapshot Snapshot()
    {
        lock (gate) return new(capturing, status, session?.GamePath ?? "", session?.ProcessId, peers.Snapshot());
    }
    public void Dispose()
    {
        lock (lifecycle) { if (disposed) return; StopWorker(); disposed = true; lock (gate) { capturing = false; peers.Clear(); session = null; status = "Disposed."; } }
    }
}
