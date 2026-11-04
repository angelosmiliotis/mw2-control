using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MW2Control.Network;

namespace MW2Control.App;

public partial class LobbyNetworkView : UserControl, INotifyPropertyChanged
{
    private readonly LobbyNetworkService _capture = new();
    private readonly FirewallBlockService _firewall = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _busy, _closed;
    private CaptureAdapter? _selectedAdapter;
    private FirewallBlock? _selectedBlock;
    public IReadOnlyList<CaptureAdapter> Adapters { get; private set; } = Array.Empty<CaptureAdapter>();
    public IReadOnlyList<PeerSnapshot> Peers { get; private set; } = Array.Empty<PeerSnapshot>();
    public IReadOnlyList<FirewallBlock> Blocks { get; private set; } = Array.Empty<FirewallBlock>();
    public CaptureAdapter? SelectedAdapter { get => _selectedAdapter; set { _selectedAdapter = value; Changed(); } }
    public PeerSnapshot? LikelyHost => Peers.SingleOrDefault(p => p.IsTrafficLeader);
    public string HostAddress => LikelyHost?.Address ?? "—";
    public string ScanButtonLabel => Capturing ? "Stop scan" : "Scan lobby";
    public string BlocksSummary => Blocks.Count == 0 ? "No blocked addresses." : $"{Blocks.Count} blocked";
    public string ScanSummary => Capturing
        ? LikelyHost is null ? "Waiting for lobby traffic…" : $"{Peers.Count} connections observed"
        : Status.StartsWith("Stopped.", StringComparison.Ordinal) ? "Scan stopped." : Status;
    public FirewallBlock? SelectedBlock { get => _selectedBlock; set { _selectedBlock = value; Changed(); } }
    public bool Capturing { get; private set; }
    public bool Idle => !_busy && !_closed;
    public bool CanChooseAdapter => Idle && !Capturing;
    public bool CanStart => CanChooseAdapter;
    public bool CanBlock => Idle && Capturing && LikelyHost is not null && !string.IsNullOrWhiteSpace(GamePath);
    public bool CanUnblock => Idle && SelectedBlock is not null;
    public string Status { get; private set; } = "Join a lobby, then select Scan lobby.";
    public string GamePath { get; private set; } = string.Empty;
    public string ActionResult { get; private set; } = string.Empty;
    public string ActionDetails { get; private set; } = string.Empty;

    public LobbyNetworkView()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += async (_, _) => await Run(LoadBlocks);
        _timer.Tick += async (_, _) => await Run(async () => Accept(await Task.Run(_capture.Snapshot)), reportErrors: true);
    }

    private void Accept(LobbySnapshot snapshot)
    {
        Capturing = snapshot.Capturing;
        Status = snapshot.Status;
        GamePath = snapshot.GamePath;
        Peers = snapshot.Peers.Take(256).ToArray();
        if (Capturing && !_closed) _timer.Start(); else _timer.Stop();
    }

    private async Task Run(Func<Task> operation, bool reportErrors = true)
    {
        if (_closed || !await _gate.WaitAsync(0)) return;
        _busy = true;
        Changed();
        try { await operation(); }
        catch (Exception ex)
        {
            if (reportErrors)
            {
                Status = "Network operation failed: " + ex.Message;
                ActionResult = Status;
            }
            // Stop polling after a backend error; the user can explicitly retry.
            _timer.Stop();
        }
        finally { _busy = false; _gate.Release(); Changed(); }
    }

    private async void RefreshAdapters(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (Capturing) { Status = "Stop capture before changing adapters."; return; }
        Adapters = await Task.Run(_capture.ListAdapters);
        _selectedAdapter = Adapters.FirstOrDefault(a => a.Id == SelectedAdapter?.Id);
        Status = Adapters.Count == 0 ? "No capture adapters available. Check Npcap installation and permissions." : "Choose an adapter before starting capture.";
    });
    private void ToggleCapture(object sender, RoutedEventArgs e)
    {
        if (Capturing) StopCapture(sender, e);
        else StartCapture(sender, e);
    }

    private async void StartCapture(object sender, RoutedEventArgs e)
    {
        if (!CanStart) return;
        await Run(async () =>
        {
            var selectedId = SelectedAdapter?.Id;
            var selection = await Task.Run(() =>
            {
                var adapters = _capture.ListAdapters();
                var adapter = adapters.FirstOrDefault(a => a.Id == selectedId);
                return (Adapters: adapters, Adapter: adapter ?? SuggestAdapter(adapters));
            });
            Adapters = selection.Adapters;
            var adapter = selection.Adapter;
            if (adapter is null)
            {
                _selectedAdapter = null;
                Status = Adapters.Count == 0 ? "No Npcap adapters available." :
                    "The game adapter is ambiguous. Open Network options, choose the game's network interface, then scan again.";
                return;
            }
            _selectedAdapter = adapter;
            var result = await Task.Run(() => _capture.Start(adapter.Id));
            Accept(await Task.Run(_capture.Snapshot));
            Status = result.Message;
        });
    }
    private static CaptureAdapter? SuggestAdapter(IReadOnlyList<CaptureAdapter> adapters)
    {
        var game = new WindowsEndpointSource().Read();
        if (game is null || game.Endpoints.Count == 0)
            throw new InvalidOperationException("Join a lobby in iw4mp.exe before scanning. No game IPv4 UDP endpoints were found.");
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .ToArray();
        var bound = game.Endpoints.Where(e => !e.Address.Equals(System.Net.IPAddress.Any)).Select(e => e.Address).ToHashSet();
        var candidates = interfaces.Where(n =>
        {
            var properties = n.GetIPProperties();
            return properties.UnicastAddresses.Any(a => bound.Contains(a.Address)) ||
                (game.Endpoints.Any(e => e.Address.Equals(System.Net.IPAddress.Any)) &&
                 properties.GatewayAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !a.Address.Equals(System.Net.IPAddress.Any)));
        }).ToArray();
        if (candidates.Length != 1 || !Guid.TryParse(candidates[0].Id, out var id)) return null;
        var expected = @"\Device\NPF_" + id.ToString("B");
        return adapters.SingleOrDefault(a => string.Equals(a.Id, expected, StringComparison.OrdinalIgnoreCase));
    }
    private async void StopCapture(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        _timer.Stop();
        await Task.Run(_capture.Stop);
        Accept(await Task.Run(_capture.Snapshot));
    });
    private async void RefreshBlocks(object sender, RoutedEventArgs e) => await Run(LoadBlocks);
    private async Task LoadBlocks()
    {
        var selectedId = _selectedBlock?.Id;
        Blocks = await Task.Run(_firewall.ListOwnedBlocks);
        _selectedBlock = Blocks.FirstOrDefault(block => block.Id == selectedId) ?? Blocks.FirstOrDefault();
    }

    private async void BlockPeer(object sender, RoutedEventArgs e)
    {
        if (!CanBlock) return;
        await Run(async () =>
        {
            var snapshot = await Task.Run(_capture.Snapshot);
            Accept(snapshot);
            var peer = LikelyHost;
            var path = GamePath;
            if (_closed || !Capturing || peer is null || string.IsNullOrWhiteSpace(path)) return;
            if (!Confirm("Block exact IP?", peer.Address,
                "Block this IP for MW2? You may disconnect from the lobby. Other lobbies sharing this IP or relay may also be blocked. The host is an estimate.")) return;
            var latest = await Task.Run(_capture.Snapshot);
            Accept(latest);
            if (_closed || !Capturing || latest.ProcessId != snapshot.ProcessId || GamePath != path || LikelyHost?.Address != peer.Address)
            {
                ActionResult = "The lobby or traffic leader changed during confirmation. Review the current IP and try again.";
                return;
            }
            var result = await Task.Run(() => _firewall.Block(peer.Address, path, true));
            ActionDetails = result.Message;
            ActionResult = result.Success ? $"Blocked {peer.Address} for MW2." : FriendlyFailure(result.Message);
            await LoadBlocks();
        });
    }
    private async void UnblockPeer(object sender, RoutedEventArgs e)
    {
        var block = SelectedBlock;
        if (!CanUnblock || block is null) return;
        await Run(async () =>
        {
            if (!Confirm("Unblock this address?", block.Address,
                "Remove this MW2 Control block and allow connections to this address again?")) return;
            var result = await Task.Run(() => _firewall.Unblock(block.Id, true));
            ActionDetails = result.Message;
            ActionResult = result.Success ? $"Unblocked {block.Address}." : FriendlyFailure(result.Message);
            await LoadBlocks();
        });
    }
    private static string FriendlyFailure(string message) =>
        message.Contains("0x80070005", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
            ? "Open MW2 Control as administrator to block or unblock an address."
            : "The firewall change failed. Open Network options for details.";

    private bool Confirm(string title, string address, string warning) =>
        MessageBox.Show(Window.GetWindow(this), $"IP address: {address}\n\n{warning}\n\nContinue?",
            title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    public async Task DisposeAsync()
    {
        _closed = true;
        _timer.Stop();
        await _gate.WaitAsync();
        try { await Task.Run(_capture.Dispose); }
        finally { _gate.Release(); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed() => PropertyChanged?.Invoke(this, new(string.Empty));
}
