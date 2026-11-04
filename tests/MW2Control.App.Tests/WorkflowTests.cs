using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Net;
using MW2Control.App;
using MW2Control.Core;
using MW2Control.Network;

internal static class WorkflowTests
{
    internal static async Task RunAsync()
    {
        await StagingAndFailedApply();
        await FirewallRoutingAndProtocol();
        await InstanceActivation();
        AdapterPreferences();
    }

    private static async Task StagingAndFailedApply()
    {
        var memory = new FakeMemory();
        var source = new FakeSource(memory) { Running = false };
        var vm = new MainViewModel(new ControlEngine(source));
        try
        {
            vm.ScanCommand.Execute(null);
            await Until(() => !vm.Busy);
            Check(vm.Fullbright.Editable && vm.Music.Editable && vm.SpecularEditable,
                "settings can be prepared while the game is closed");
            Check(!vm.ApplyCommand.CanExecute(null), "Apply is disabled while the game is closed");
            vm.Ping.Requested = true;
            source.Running = true;
            vm.ScanCommand.Execute(null);
            await Until(() => !vm.Busy);
            vm.RequestedFov = 95;
            vm.RequestedFps = 165;
            vm.ApplyCommand.Execute(null);
            await Until(() => !vm.Busy && vm.MaintenanceActive);
            var writes = memory.Writes;
            vm.RequestedFov = 100;
            vm.Fullbright.Requested = true;
            vm.ApplyCommand.Execute(null);
            await Until(() => !vm.Busy);
            Check(vm.MaintenanceActive && memory.Writes == writes,
                "failed Apply keeps the previous successful settings active");
            Check(vm.MaintenanceStatus.Contains("Previous settings remain active", StringComparison.Ordinal),
                "the existing status line explains a failed Apply");
            await Task.Delay(700);
            Check(vm.MaintenanceStatus.Contains("Previous settings remain active", StringComparison.Ordinal),
                "background checks preserve the failed Apply notice");
            memory.SetFov(65);
            await Until(() => BitConverter.ToSingle(memory.ReadMemory(0x200010, 4)) == 95);
            Check(vm.MaintenanceActive, "recovery continues using the last successful request");
        }
        finally { await vm.DisposeAsync(); }
    }

    private static async Task FirewallRoutingAndProtocol()
    {
        var policy = new RecordingPolicy();
        var service = new FirewallBlockService(policy, _ => true, () => Array.Empty<IPAddress>());
        var elevatedCalls = 0;
        Task<NetworkResult> Elevate(FirewallRequest request)
        {
            elevatedCalls++;
            return Task.FromResult(FirewallActions.Execute(request, service));
        }
        var actions = new FirewallActions(service, () => false, Elevate);
        var block = new FirewallRequest("block", "8.8.8.8", @"C:\Synthetic\iw4mp.exe");
        Check(!(await actions.RunAsync(block, false)).Success && elevatedCalls == 0 && policy.Rules.Count == 0,
            "cancelled firewall actions never request elevation");
        Check((await actions.RunAsync(block, true)).Success && elevatedCalls == 1 && policy.Rules.Count == 2,
            "a confirmed block routes through the elevated action boundary");
        var id = service.ListOwnedBlocks().Single().Id;
        Check((await actions.RunAsync(new("unblock", BlockId: id), true)).Success && policy.Rules.Count == 0,
            "unblock uses the same scoped action boundary");
        var admin = new FirewallActions(service, () => true, _ => throw new Exception("Unexpected elevation"));
        Check((await admin.RunAsync(block, true)).Success, "an already elevated app uses its existing permissions");
        foreach (var invalid in new[]
        {
            new FirewallRequest("execute", "8.8.8.8", @"C:\Synthetic\iw4mp.exe"),
            new FirewallRequest("block", "192.168.1.1", @"C:\Synthetic\iw4mp.exe"),
            new FirewallRequest("block", "8.8.8.8", @"C:\Windows\other.exe"),
            new FirewallRequest("unblock", BlockId: "arbitrary-rule")
        })
        {
            var rejected = false;
            try { await actions.RunAsync(invalid, true); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "invalid helper request is rejected before elevation");
        }
        Check(!ElevatedFirewall.Failure(new Win32Exception(1223)).Success &&
            ElevatedFirewall.Failure(new Win32Exception(1223)).Message.Contains("cancelled", StringComparison.Ordinal),
            "UAC cancellation has an explicit result");
        Check(ElevatedFirewall.Failure(new OperationCanceledException()).Message.Contains("Check blocked addresses", StringComparison.Ordinal),
            "a timeout asks the user to verify the firewall result");
        using var stream = new MemoryStream();
        await LocalPipe.WriteAsync(stream, block, CancellationToken.None);
        stream.Position = 0;
        Check(await LocalPipe.ReadAsync<FirewallRequest>(stream, CancellationToken.None) == block,
            "helper messages round-trip without command-line IPs or files");
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, 20000);
        using var oversized = new MemoryStream(prefix);
        var rejectedSize = false;
        try { await LocalPipe.ReadAsync<FirewallRequest>(oversized, CancellationToken.None); }
        catch (InvalidDataException) { rejectedSize = true; }
        Check(rejectedSize, "oversized helper messages are rejected before allocation");
        Check(await ElevatedFirewall.ServeAsync(new[] { "--firewall", "invalid-pipe", "1" }) == 2,
            "invalid helper launch arguments exit without performing an action");
    }

    private static async Task InstanceActivation()
    {
        var scope = "test-" + Guid.NewGuid().ToString("N");
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                using var instance = new SingleInstance(scope);
                if (!instance.IsPrimary) throw new Exception("Primary instance was not acquired.");
                instance.Listen(() => activated.TrySetResult(true));
                ready.TrySetResult(true);
                stop.Wait(TimeSpan.FromSeconds(15));
            }
            catch (Exception ex) { ready.TrySetException(ex); activated.TrySetException(ex); }
        }) { IsBackground = true };
        thread.Start();
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var secondary = new SingleInstance(scope);
            Check(!secondary.IsPrimary, "a second instance cannot acquire the app lock");
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            using (var malformed = new NamedPipeClientStream(".", secondary.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await malformed.ConnectAsync(timeout.Token);
                await LocalPipe.ReadAsync<int>(malformed, timeout.Token);
                await LocalPipe.WriteAsync(malformed, 123, timeout.Token);
            }
            await secondary.ActivatePrimaryAsync(allowForeground: false);
            Check(await activated.Task.WaitAsync(TimeSpan.FromSeconds(5)), "activation still works after a malformed local message");
        }
        finally
        {
            stop.Set();
            if (!thread.Join(5000)) throw new TimeoutException("Instance listener did not stop.");
        }
        using var next = new SingleInstance(scope);
        Check(next.IsPrimary, "closing the primary releases its lock and IPC listener");
    }

    private static void AdapterPreferences()
    {
        var path = Path.Combine(Path.GetTempPath(), "MW2Control-network-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new PreferencesStore(path);
        try
        {
            store.Save(new() { Fov = 97, Fps = 240 });
            store.Update(current => current with { NetworkAdapterId = @"\Device\NPF_{test}" });
            Check(store.Load().Fov == 97 && store.Load().NetworkAdapterId == @"\Device\NPF_{test}",
                "saving an adapter preserves game preferences");
            store.Update(current => current with { Fps = 333 });
            Check(store.Load().NetworkAdapterId == @"\Device\NPF_{test}", "saving game settings preserves the remembered adapter");
            Check(NetworkMessages.Failure(new DllNotFoundException()).Contains("Npcap", StringComparison.Ordinal),
                "missing Npcap has a readable explanation");
            Check(NetworkMessages.Failure(new Win32Exception(126)).Contains("Npcap", StringComparison.Ordinal),
                "the native Npcap loader failure has a readable explanation");
            Check(NetworkMessages.Failure(new InvalidOperationException("Join a lobby in iw4mp.exe")).StartsWith("Join a lobby", StringComparison.Ordinal),
                "a missing lobby has a readable explanation");
            Check(NetworkMessages.ScanSummary(true, 0, string.Empty, true).Contains("Network options", StringComparison.Ordinal),
                "a scan without traffic explains where to check the adapter");
        }
        finally { File.Delete(path); }
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(20, timeout.Token);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }

    private sealed class RecordingPolicy : IFirewallPolicy
    {
        internal List<FirewallRule> Rules { get; } = new();
        public IReadOnlyList<FirewallRule> ReadRules() => Rules.ToArray();
        public void Add(FirewallRule rule) => Rules.Add(rule);
        public void Remove(string name) => Rules.RemoveAll(rule => rule.Name == name);
    }
}
