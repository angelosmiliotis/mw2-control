using System.Text;
using System.Windows.Threading;
using MW2Control.App;
using MW2Control.Core;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var exit = 0;
        dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await Smoke(); Console.WriteLine("PASS: synthetic view-model maintenance lifecycle (no windows/native APIs)"); }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
            finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
        }));
        Dispatcher.Run();
        return exit;
    }

    private static async Task Smoke()
    {
        var memory = new FakeMemory();
        var vm = new MainViewModel(new ControlEngine(new FakeSource(memory)));
        try
        {
            Assert(!vm.MaintenanceActive && memory.Writes == 0, "default stopped/no writes");
            Assert(vm.ApplyCommand.CanExecute(null), "Apply is available before manual inspection");
            vm.RequestedFov = 95;
            vm.RequestedFps = 165;
            vm.Ping.Requested = true;
            Assert(memory.Writes == 0, "staging requests does not write");

            vm.ApplyCommand.Execute(null);
            await Until(() => !vm.Busy);
            Assert(vm.MaintenanceActive, "Apply starts retention: " + vm.LastResult);
            Assert(vm.ObservedFov == "95" && vm.ObservedFps == "165" &&
                vm.Features.Single(f => f.Id == "ping").ObservedValue == 1,
                "one Apply inspects and applies tuning and HUD");
            Assert(vm.InputsEnabled && vm.ApplyCommand.CanExecute(null) && vm.ResetCommand.CanExecute(null),
                "retention allows editing, Apply and Reset");
            Assert(!vm.ScanCommand.CanExecute(null), "manual scan is disabled during retention");

            var before = memory.Writes;
            vm.RequestedFov = 100;
            vm.Ping.Requested = false;
            await Task.Delay(250);
            Assert(memory.Writes == before, "editing during retention does not apply staged changes");
            memory.ResetToBaseline();
            await Until(() => memory.Writes >= before + 4);
            Assert(memory.ReadMemory(0x200210, 1)[0] == 1, "retention reapplies owned HUD value after baseline reset");

            vm.ResetCommand.Execute(null);
            await Until(() => !vm.Busy && !vm.MaintenanceActive);
            Assert(vm.LastResult.StartsWith("Reset complete.") && vm.ObservedFov == "90" && vm.ObservedFps == "144" &&
                vm.Features.Single(f => f.Id == "ping").ObservedValue == 0,
                "one Reset stops retention and restores both groups");

            vm.Fullbright.Requested = true; // Exercise core refusal independently of the UI toggle guard.
            vm.ApplyCommand.Execute(null);
            await Until(() => !vm.Busy);
            Assert(vm.LastResult.StartsWith("Partially applied.") && !vm.MaintenanceActive,
                "partial Apply reports the failed group and leaves retention stopped");
            vm.Fullbright.Requested = false;
            vm.ApplyCommand.Execute(null);
            await Until(() => !vm.Busy && vm.MaintenanceActive);
            await vm.DisposeAsync();
            before = memory.Writes;
            memory.ResetToBaseline();
            await Task.Delay(300);
            Assert(!vm.MaintenanceActive && memory.Writes == before, "Dispose stops retention");
        }
        finally { await vm.DisposeAsync(); }
    }

    private static void Assert(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Console.WriteLine("PASS: " + description);
    }
    private static async Task Until(Func<bool> predicate, int timeout = 4000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate())
        {
            if (clock.ElapsedMilliseconds > timeout) throw new TimeoutException("View-model transition timed out.");
            await Task.Delay(20);
        }
    }
}

internal sealed class FakeSource(FakeMemory memory) : IProcessSource
{
    public IProcessMemory OpenTarget() => memory;
}

internal sealed class FakeMemory : IProcessMemory
{
    private readonly object _gate = new();
    private readonly Dictionary<ulong, byte> _bytes = new();
    private int _writes;
    public int Writes => Volatile.Read(ref _writes);
    public ProcessIdentity Identity { get; } = new(1234, 638000000000000000, "iw4mp", @"C:\Synthetic\iw4mp.exe", 0x140000000, 0x1D27000, KnownBuild.Sha256, true);
    public FakeMemory()
    {
        Put(Identity.ModuleBase + KnownBuild.FovPointerRva, BitConverter.GetBytes(0x200000UL));
        Put(Identity.ModuleBase + KnownBuild.MaxFpsPointerRva, BitConverter.GetBytes(0x200100UL));
        Put(0x200000, BitConverter.GetBytes(0x300000UL));
        Put(0x200100, BitConverter.GetBytes(0x300010UL));
        Put(0x20000C, [1]);
        Put(0x200040, BitConverter.GetBytes(65f));
        Put(0x200044, BitConverter.GetBytes(80f));
        Put(0x20010C, [5]);
        Put(0x200140, BitConverter.GetBytes(0));
        Put(0x200144, BitConverter.GetBytes(100));
        Put(0x300000, Encoding.ASCII.GetBytes("cg_fov\0"));
        Put(0x300010, Encoding.ASCII.GetBytes("com_maxfps\0"));
        Put(Identity.ModuleBase + AdvancedBuild.PingTextPointerRva, BitConverter.GetBytes(0x200200UL));
        Put(Identity.ModuleBase + AdvancedBuild.PingGraphPointerRva, BitConverter.GetBytes(0x200220UL));
        Put(0x200200, BitConverter.GetBytes(0x300040UL));
        Put(0x200220, BitConverter.GetBytes(0x300060UL));
        Put(0x300040, Encoding.ASCII.GetBytes("cg_scoreboardPingText\0"));
        Put(0x300060, Encoding.ASCII.GetBytes("cg_scoreboardPingGraph\0"));
        ResetToBaseline();
    }
    public void ResetToBaseline()
    {
        lock (_gate)
        {
            Put(0x200010, BitConverter.GetBytes(90f));
            Put(0x200110, BitConverter.GetBytes(144));
            Put(0x200210, [0]);
            Put(0x200230, [1]);
        }
    }
    private void Put(ulong address, byte[] data)
    {
        lock (_gate) for (var i = 0; i < data.Length; i++) _bytes[address + (ulong)i] = data[i];
    }
    public bool ValidateIdentity() => true;
    public MemoryRegion QueryRegion(ulong address) => address switch
    {
        >= 0x200000 and < 0x200280 => new(0x200000, 0x280, true, MemoryProtection.ReadWrite),
        >= 0x300000 and < 0x300080 => new(0x300000, 0x80, true, MemoryProtection.ReadOnly),
        _ when address >= Identity.ModuleBase && address < Identity.ModuleBase + Identity.ModuleSize => new(Identity.ModuleBase, Identity.ModuleSize, true, MemoryProtection.ReadOnly),
        _ => new(address, 0, false, MemoryProtection.NoAccess)
    };
    public byte[] ReadMemory(ulong address, int count)
    {
        lock (_gate) return Enumerable.Range(0, count).Select(i => _bytes.GetValueOrDefault(address + (ulong)i)).ToArray();
    }
    public int WriteMemory(ulong address, byte[] data)
    {
        Put(address, data);
        Interlocked.Increment(ref _writes);
        return data.Length;
    }
    public void Dispose() { }
}
