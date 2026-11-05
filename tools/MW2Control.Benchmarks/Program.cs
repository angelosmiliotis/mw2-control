using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using MW2Control.Advanced.Tests;
using MW2Control.Core;

if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
    throw new PlatformNotSupportedException("Run this fixture benchmark on Windows x64.");

// This child contains only synthetic dvars. It never discovers or opens a game.
if (args.Contains("--fixture"))
{
    var data = FakeMemory.Create().ExportBytes();
    var pages = new Dictionary<ulong, long>();
    foreach (var page in data.Keys.Select(a => a & ~0xFFFUL).Distinct())
    {
        var pointer = Marshal.AllocHGlobal(4096);
        Marshal.Copy(new byte[4096], 0, pointer, 4096);
        pages.Add(page, pointer.ToInt64());
    }
    foreach (var (address, value) in data)
        Marshal.WriteByte((nint)pages[address & ~0xFFFUL], (int)(address & 0xFFF), value);
    Console.WriteLine(JsonSerializer.Serialize(pages));
    Console.Out.Flush();
    Console.ReadLine();
    foreach (var pointer in pages.Values) Marshal.FreeHGlobal((nint)pointer);
    return;
}

var seconds = args.Length > 0 ? int.Parse(args[0]) : 30;
var repeats = args.Length > 1 ? int.Parse(args[1]) : 3;
if (seconds is < 5 or > 300 || repeats is < 1 or > 5) throw new ArgumentException("Duration 5..300 seconds, repeats 1..5.");
var start = new ProcessStartInfo(Environment.ProcessPath!)
{
    UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
    CreateNoWindow = true
};
// dotnet run starts the apphost; direct dotnet DLL invocation also works.
if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
    start.ArgumentList.Add(typeof(NativeFixture).Assembly.Location);
start.ArgumentList.Add("--fixture");
using var child = Process.Start(start) ?? throw new IOException("Fixture could not start.");
try
{
    var pages = JsonSerializer.Deserialize<Dictionary<ulong, long>>(await child.StandardOutput.ReadLineAsync()
        ?? throw new IOException("Fixture did not return its own allocation map."))!;
    using var memory = new NativeFixture(child, pages);
    using var engine = new ControlEngine(new FakeSource(memory));
    var all = new AdvancedSettings(true, true, true, true, 2, true, true);
    var apply = engine.ApplySettings(new(333, 120), all);
    if (!apply.Success) throw new IOException(apply.Message);
    var rows = new List<object>();
    for (var repeat = 0; repeat < repeats; repeat++)
    {
        // Alternate order to reduce warm-up/temperature/order bias.
        foreach (var fast in repeat % 2 == 0 ? new[] { false, true } : new[] { true, false })
        {
            _ = engine.MaintainOwned(true);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var allocated = GC.GetTotalAllocatedBytes();
            var reads = memory.Reads;
            var writes = memory.Writes;
            var deep = 0;
            var hints = 0;
            var clock = Stopwatch.StartNew();
            var validation = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                if (fast && validation.ElapsedMilliseconds < 500 && await Task.Run(engine.OwnedValuesMatchReadOnly))
                    hints++;
                else
                {
                    var result = await Task.Run(() => engine.MaintainOwned(true));
                    if (!result.Success) throw new IOException(result.Message);
                    validation.Restart(); deep++;
                }
                await Task.Delay(fast ? 50 : 500);
            }
            clock.Stop();
            process.Refresh();
            var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
            var row = new
            {
                repeat = repeat + 1, mode = fast ? "50ms hints + 500ms full" : "500ms full only",
                elapsedMs = clock.Elapsed.TotalMilliseconds, cpuMs,
                oneCorePercent = cpuMs / clock.Elapsed.TotalMilliseconds * 100,
                machinePercent = cpuMs / clock.Elapsed.TotalMilliseconds * 100 / Environment.ProcessorCount,
                allocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
                workingSetBytes = process.WorkingSet64, nativeReads = memory.Reads - reads,
                stableWrites = memory.Writes - writes, fullChecks = deep, hintChecks = hints
            };
            if (row.stableWrites != 0) throw new IOException("A stable tick unexpectedly wrote.");
            rows.Add(row);
            Console.WriteLine(JsonSerializer.Serialize(row));
            Console.Out.Flush();
        }
    }
    var recoveries = new List<double>();
    using var recoveryStop = new CancellationTokenSource();
    var recoveryTask = Task.Run(async () =>
    {
        try
        {
            while (!recoveryStop.IsCancellationRequested)
            {
                if (!engine.OwnedValuesMatchReadOnly())
                {
                    var result = engine.MaintainOwned(true);
                    if (!result.Success) throw new IOException(result.Message);
                }
                await Task.Delay(50, recoveryStop.Token);
            }
        }
        catch (OperationCanceledException) when (recoveryStop.IsCancellationRequested) { }
    });
    try
    {
        for (var reset = 0; reset < 20; reset++)
        {
            await Task.Delay(3 + reset * 17 % 48);
            memory.WriteMemory(0x210010, BitConverter.GetBytes(65f));
            var clock = Stopwatch.StartNew();
            while (BitConverter.ToSingle(memory.ReadMemory(0x210010, 4)) != 120)
            {
                if (clock.ElapsedMilliseconds > 2000) throw new IOException("Reset was not recovered.");
                await Task.Delay(2);
            }
            recoveries.Add(clock.Elapsed.TotalMilliseconds);
        }
    }
    finally { recoveryStop.Cancel(); await recoveryTask; }
    for (var reset = 0; reset < 20; reset++)
    {
        // These are fixture mutations, never app writes to an external game.
        memory.WriteMemory(0x210010, BitConverter.GetBytes(65f));
        memory.WriteMemory(0x210110, BitConverter.GetBytes(85));
        foreach (var feature in Enum.GetValues<AdvancedDvar>())
        {
            var value = feature is AdvancedDvar.Fullbright or AdvancedDvar.PingText ? 0 : 1;
            memory.WriteMemory(FakeMemory.Value(feature), feature is AdvancedDvar.SpecularMap or AdvancedDvar.DetailMap
                ? BitConverter.GetBytes(value) : [(byte)value]);
        }
        memory.WriteMemory(FakeMemory.Value(AdvancedDvar.Fullbright) - 3, [0]);
        var result = engine.MaintainOwned(true);
        if (!result.Success || !engine.OwnedValuesMatchReadOnly()) throw new IOException("Compound fixture reset failed: " + result.Message);
        foreach (var feature in result.Snapshot.Features)
        {
            var expected = feature.Id switch { "fov" => 120, "fps" => 333, "specular" => 2, _ => 1 };
            if (!feature.Supported || feature.ObservedValue != expected)
                throw new IOException($"Fixture readback failed for {feature.Id}: {feature.ObservedValue} != {expected}.");
        }
    }
    var restored = engine.ResetSettings();
    if (!restored.Success || engine.OwnedValuesMatchReadOnly()) throw new IOException("Fixture restore failed: " + restored.Message);
    if (BitConverter.ToSingle(memory.ReadMemory(0x210010, 4)) != 90 || BitConverter.ToInt32(memory.ReadMemory(0x210110, 4)) != 144)
        throw new IOException("Fixture tuning baseline was not restored.");
    foreach (var feature in Enum.GetValues<AdvancedDvar>())
    {
        var current = AdvancedDvarValidator.Resolve(memory, feature);
        var expected = feature is AdvancedDvar.Fullbright or AdvancedDvar.PingText ? 0 : 1;
        if (current.Value != expected) throw new IOException($"Fixture baseline not restored for {feature}.");
    }
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        fixtureOnly = true, gameAccess = false, logicalProcessors = Environment.ProcessorCount,
        recoveries = recoveries.Count, meanRecoveryMs = recoveries.Average(), maxRecoveryMs = recoveries.Max(),
        compoundResets = 20, allElevenTargetsVerified = true, originalValuesRestored = true,
        note = "Actual ReadProcessMemory/WriteProcessMemory against this tool's synthetic child. Full core validators execute; native identity checks include image path, lifetime, module and committed-page queries. Excludes WPF, packet capture and game rendering. Not an in-game performance claim."
    }));
}
finally
{
    if (!child.HasExited) { await child.StandardInput.WriteLineAsync("stop"); await child.WaitForExitAsync(); }
}

internal sealed class NativeFixture : IProcessMemory
{
    private readonly Process child;
    private readonly Dictionary<ulong, long> pages;
    private readonly SafeProcessHandle handle;
    private readonly long started;
    private readonly string path;
    private readonly nint module;
    private readonly int moduleSize;
    public long Reads { get; private set; }
    public long Writes { get; private set; }
    public ProcessIdentity Identity { get; }
    public NativeFixture(Process child, Dictionary<ulong, long> pages)
    {
        this.child = child; this.pages = pages;
        handle = OpenProcess(0x0400 | 0x0010 | 0x0020 | 0x0008, false, child.Id);
        if (handle.IsInvalid) throw new IOException("Could not open the synthetic child.");
        started = child.StartTime.ToUniversalTime().Ticks;
        path = child.MainModule!.FileName; module = child.MainModule.BaseAddress; moduleSize = child.MainModule.ModuleMemorySize;
        Identity = FakeMemory.SupportedIdentity with { ProcessId = child.Id, StartTimeUtcTicks = started };
    }
    private nint Physical(ulong address) => pages.TryGetValue(address & ~0xFFFUL, out var page)
        ? checked((nint)(page + (long)(address & 0xFFF))) : throw new IOException("Unknown fixture page.");
    public bool ValidateIdentity()
    {
        if (handle.IsClosed || handle.IsInvalid || !GetExitCodeProcess(handle, out var code) || code != 259 ||
            !GetProcessTimes(handle, out var created, out _, out _, out _) || GetProcessId(handle) != child.Id) return false;
        var image = new StringBuilder(32768);
        var length = image.Capacity;
        if (!QueryFullProcessImageName(handle, 0, image, ref length) || Path.GetFullPath(image.ToString()) != path) return false;
        using var current = Process.GetProcessById(child.Id);
        var currentModule = current.MainModule;
        if (created != started - DateTime.FromFileTimeUtc(0).Ticks ||
            currentModule?.FileName != path || currentModule.BaseAddress != module || currentModule.ModuleMemorySize != moduleSize) return false;
        return Committed(module) && Committed(Physical(Identity.ModuleBase + KnownBuild.FovPointerRva)) &&
            Committed(Physical(Identity.ModuleBase + KnownBuild.MaxFpsPointerRva));
    }
    private bool Committed(nint address) => VirtualQueryEx(handle, address, out var page, (nuint)Marshal.SizeOf<NativeRegion>()) != 0 && page.State == 0x1000;
    public MemoryRegion QueryRegion(ulong address)
    {
        if (VirtualQueryEx(handle, Physical(address), out var region, (nuint)Marshal.SizeOf<NativeRegion>()) == 0 || region.State != 0x1000)
            throw new IOException("Fixture page is unavailable.");
        var writable = address is >= 0x200000 and < 0x220000;
        return new(address & ~0xFFFUL, 4096, true, writable ? MemoryProtection.ReadWrite : MemoryProtection.ReadOnly);
    }
    public byte[] ReadMemory(ulong address, int count)
    {
        var data = new byte[count];
        if (!ReadProcessMemory(handle, Physical(address), data, (nuint)count, out var read) || read != (nuint)count)
            throw new IOException("Native fixture read failed.");
        Reads++; return data;
    }
    public int WriteMemory(ulong address, byte[] data)
    {
        if (!WriteProcessMemory(handle, Physical(address), data, (nuint)data.Length, out var written))
            throw new IOException("Native fixture write failed.");
        Writes++; return checked((int)written);
    }
    public void Dispose() => handle.Dispose();
    [StructLayout(LayoutKind.Sequential)] private struct NativeRegion
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(SafeProcessHandle process, nint address, byte[] data, nuint size, out nuint read);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(SafeProcessHandle process, nint address, byte[] data, nuint size, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nuint VirtualQueryEx(SafeProcessHandle process, nint address, out NativeRegion region, nuint size);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
}
