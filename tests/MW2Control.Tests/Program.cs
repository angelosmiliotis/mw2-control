using System.Buffers.Binary;
using System.Text;
using MW2Control.Core;

var suite = new TestSuite();
suite.Run("request bounds accept inclusive endpoints", () =>
{
    Check.True(TuningValidation.ValidateRequest(new(30, 65)).Success);
    Check.True(TuningValidation.ValidateRequest(new(500, 130)).Success);
});
suite.Run("request bounds reject out of range and NaN/infinity", () =>
{
    foreach (var request in new TuningRequest?[]
    {
        null, new(29, 90), new(501, 90), new(144, 64.999), new(144, 130.001),
        new(144, double.NaN), new(144, double.PositiveInfinity), new(144, double.NegativeInfinity)
    }) Check.False(TuningValidation.ValidateRequest(request).Success);
});
suite.Run("profile rejects wrong hash, architecture, and process/executable name", () =>
{
    var identity = FakeMemory.ValidIdentity;
    Check.False(TuningValidation.ValidateProfile(identity with { Sha256 = new string('0', 64) }).Success);
    Check.False(TuningValidation.ValidateProfile(identity with { Is64Bit = false }).Success);
    Check.False(TuningValidation.ValidateProfile(identity with { ProcessName = "other" }).Success);
    Check.False(TuningValidation.ValidateProfile(identity with { ExecutablePath = @"C:\games\other.exe" }).Success);
});
suite.Run("valid profile accepted case-insensitively", () =>
{
    var identity = FakeMemory.ValidIdentity with { ProcessName = "IW4MP.EXE", ExecutablePath = @"C:\Games\IW4MP.EXE" };
    Check.True(TuningValidation.ValidateProfile(identity).Success);
});
suite.Run("pointer helpers reject null, misaligned, and out-of-range pointers", () =>
{
    Check.False(TuningValidation.IsUserPointer(0));
    Check.False(TuningValidation.IsUserPointer(0x200003, 8));
    Check.False(TuningValidation.IsUserPointer(TuningValidation.MaximumUserAddress + 1));
    Check.True(TuningValidation.IsUserPointer(0x200000, 8));
});
suite.Run("resolver accepts expected dvar and performs reads only", () =>
{
    var memory = FakeMemory.Create();
    var result = DvarValidator.Resolve(memory, TuningFeature.Fov);
    Check.Equal(90d, result.Value);
    Check.Equal(0, memory.WriteCalls);
    Check.True(memory.ReadCalls > 0);
});
suite.Run("resolver rejects null and misaligned dvar pointers", () =>
{
    var nullMemory = FakeMemory.Create();
    nullMemory.PutUInt64(FakeMemory.ValidIdentity.ModuleBase + KnownBuild.FovPointerRva, 0);
    Check.Throws<InvalidOperationException>(() => DvarValidator.Resolve(nullMemory, TuningFeature.Fov));

    var unalignedMemory = FakeMemory.Create();
    unalignedMemory.PutUInt64(FakeMemory.ValidIdentity.ModuleBase + KnownBuild.FovPointerRva, 0x200003);
    Check.Throws<InvalidOperationException>(() => DvarValidator.Resolve(unalignedMemory, TuningFeature.Fov));
});
suite.Run("resolver rejects wrong dvar name", () =>
{
    var memory = FakeMemory.Create();
    memory.PutBytes(0x300000, Encoding.ASCII.GetBytes("cg_fov_extra\0"));
    Check.Throws<InvalidOperationException>(() => DvarValidator.Resolve(memory, TuningFeature.Fov));
});
suite.Run("resolver rejects invalid/stale identity", () =>
{
    var memory = FakeMemory.Create();
    memory.Identity = memory.Identity with { Sha256 = new string('0', 64) };
    Check.Throws<InvalidOperationException>(() => DvarValidator.Resolve(memory, TuningFeature.Fov));

    var stale = FakeMemory.Create();
    stale.IdentityValid = false;
    Check.Throws<InvalidOperationException>(() => DvarValidator.Resolve(stale, TuningFeature.Fov));
});
suite.Run("resolver rejects short reads", () =>
{
    var memory = FakeMemory.Create();
    memory.ShortReadAddress = FakeMemory.ValidIdentity.ModuleBase + KnownBuild.FovPointerRva;
    Check.Throws<InvalidOperationException>(() => DvarValidator.Resolve(memory, TuningFeature.Fov));
});
suite.Run("resolver rejects guarded and uncommitted regions", () =>
{
    var guarded = FakeMemory.Create();
    guarded.SetProtection(FakeMemory.FovValueAddress, MemoryProtection.ReadWrite | MemoryProtection.Guard);
    Check.Throws<InvalidOperationException>(() => DvarValidator.Resolve(guarded, TuningFeature.Fov, requireWritable: true));

    var uncommitted = FakeMemory.Create();
    uncommitted.SetCommitted(FakeMemory.FovValueAddress, false);
    Check.Throws<InvalidOperationException>(() => DvarValidator.Resolve(uncommitted, TuningFeature.Fov));
});
suite.Run("writable validation rejects read-only and executable target pages", () =>
{
    foreach (var protection in new[] { MemoryProtection.ReadOnly, MemoryProtection.ExecuteRead, MemoryProtection.ExecuteReadWrite })
    {
        var memory = FakeMemory.Create();
        memory.SetProtection(FakeMemory.FovValueAddress, protection);
        Check.Throws<InvalidOperationException>(() => DvarValidator.Resolve(memory, TuningFeature.Fov, requireWritable: true));
        Check.Equal(0, memory.WriteCalls);
    }
});
suite.Run("writable validation permits only read-write nonexecutable data", () =>
{
    var memory = FakeMemory.Create();
    var result = DvarValidator.Resolve(memory, TuningFeature.Fov, requireWritable: true);
    Check.Equal(90d, result.Value);
    Check.Equal(0, memory.WriteCalls);
});
suite.Run("transaction inspection and apply permission gate perform no writes", () =>
{
    var memory = FakeMemory.Create();
    var transaction = new TuningTransaction(memory);
    Check.Equal(2, transaction.InspectFeatures().Count);
    Check.Equal(0, memory.WriteCalls);
    Check.False(transaction.Apply(new(144, 90), false).Success);
    Check.Equal(0, memory.WriteCalls);
});
suite.Run("transaction rolls back prior and partial writes after a short write", () =>
{
    var memory = FakeMemory.Create();
    memory.WriteResultOverrides.Add(null);
    memory.WriteResultOverrides.Add(2);
    var beforeFov = memory.ReadRaw(0x200010, 4);
    var beforeFps = memory.ReadRaw(0x200030, 4);
    var result = new TuningTransaction(memory).Apply(new(165, 95), true);
    Check.False(result.Success);
    Check.True(beforeFov.SequenceEqual(memory.ReadRaw(0x200010, 4)));
    Check.True(beforeFps.SequenceEqual(memory.ReadRaw(0x200030, 4)));
    Check.Equal(4, memory.WriteCalls);
});
suite.Run("transaction handles readback mismatch without claiming a write", () =>
{
    var memory = FakeMemory.Create();
    memory.IgnoreWriteCalls.Add(1);
    var before = memory.ReadRaw(0x200010, 4);
    var result = new TuningTransaction(memory).Apply(new(165, 95), true);
    Check.False(result.Success);
    Check.True(before.SequenceEqual(memory.ReadRaw(0x200010, 4)));
    Check.Equal(1, memory.WriteCalls);
});
suite.Run("transaction restores owned values and refuses another writer's changes", () =>
{
    var memory = FakeMemory.Create();
    var transaction = new TuningTransaction(memory);
    Check.True(transaction.Apply(new(165, 95), true).Success);
    Check.Equal(2, memory.WriteCalls);
    Check.False(transaction.Restore(false).Success);
    Check.Equal(2, memory.WriteCalls);
    Check.True(transaction.Restore(true).Success);
    Check.Equal(4, memory.WriteCalls);
    Check.Equal(90f, BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(memory.ReadRaw(0x200010, 4))));
    Check.Equal(144, BinaryPrimitives.ReadInt32LittleEndian(memory.ReadRaw(0x200030, 4)));

    var externalMemory = FakeMemory.Create();
    var externalTransaction = new TuningTransaction(externalMemory);
    Check.True(externalTransaction.Apply(new(165, 95), true).Success);
    externalMemory.PutInt32(0x200010, BitConverter.SingleToInt32Bits(88));
    var writesBeforeRestore = externalMemory.WriteCalls;
    Check.False(externalTransaction.Restore(true).Success);
    Check.Equal(writesBeforeRestore, externalMemory.WriteCalls);
    Check.Equal(88f, BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(externalMemory.ReadRaw(0x200010, 4))));
});
suite.Run("transaction refuses restore after session identity changes", () =>
{
    var memory = FakeMemory.Create();
    var transaction = new TuningTransaction(memory);
    Check.True(transaction.Apply(new(165, 95), true).Success);
    var writesBeforeRestore = memory.WriteCalls;
    memory.Identity = memory.Identity with { ProcessId = memory.Identity.ProcessId + 1 };
    Check.False(transaction.Restore(true).Success);
    Check.Equal(writesBeforeRestore, memory.WriteCalls);
});
suite.Run("ControlEngine inspection never writes", () =>
{
    var memory = FakeMemory.Create();
    using var engine = new ControlEngine(new FakeProcessSource(memory));
    Check.Equal(EngineState.ReadOnlyReady, engine.Inspect().State);
    Check.Equal(0, memory.WriteCalls);
});
suite.Run("ControlEngine rejects unknown builds before apply writes", () =>
{
    var memory = FakeMemory.Create();
    memory.Identity = memory.Identity with { Sha256 = new string('0', 64) };
    using var engine = new ControlEngine(new FakeProcessSource(memory));
    var result = engine.Apply(new(165, 95), privateSessionConfirmed: true);
    Check.False(result.Success);
    Check.Equal(EngineState.UnsupportedBuild, result.Snapshot.State);
    Check.Equal(0, memory.WriteCalls);
});
suite.Run("ControlEngine reports missing target as GameNotRunning", () =>
{
    using var engine = new ControlEngine(new FakeProcessSource(null));
    Check.Equal(EngineState.GameNotRunning, engine.Inspect().State);
});

suite.Run("ControlEngine refuses restore into a replacement process after refresh", () =>
{
    var first = FakeMemory.Create();
    var source = new MutableProcessSource { Current = first };
    using var engine = new ControlEngine(source);
    Check.True(engine.Apply(new(165, 95), true).Success);
    var replacement = FakeMemory.Create();
    replacement.Identity = replacement.Identity with { ProcessId = replacement.Identity.ProcessId + 10 };
    source.Current = replacement;
    Check.Equal(EngineState.ReadOnlyReady, engine.Inspect().State);
    Check.False(engine.Restore(true).Success);
    Check.Equal(0, replacement.WriteCalls);
});
suite.Run("ControlEngine rejects reused PID with a new creation time", () =>
{
    var first = FakeMemory.Create();
    var source = new MutableProcessSource { Current = first };
    using var engine = new ControlEngine(source);
    Check.True(engine.Apply(new(165, 95), true).Success);
    var replacement = FakeMemory.Create();
    replacement.Identity = replacement.Identity with { StartTimeUtcTicks = replacement.Identity.StartTimeUtcTicks + 100 };
    source.Current = replacement;
    Check.False(engine.Restore(true).Success);
    Check.Equal(0, replacement.WriteCalls);
});
suite.Run("ControlEngine dispose never performs an implicit restore", () =>
{
    var memory = FakeMemory.Create();
    var engine = new ControlEngine(new FakeProcessSource(memory));
    Check.True(engine.Apply(new(165, 95), true).Success);
    var before = memory.WriteCalls;
    engine.Dispose();
    Check.Equal(before, memory.WriteCalls);
    Check.False(engine.Apply(new(144, 90), true).Success);
    Check.Equal(before, memory.WriteCalls);
});
suite.Run("ControlEngine rejects invalid requests without writes", () =>
{
    var memory = FakeMemory.Create();
    using var engine = new ControlEngine(new FakeProcessSource(memory));
    Check.False(engine.Apply(new(999, 90), true).Success);
    Check.False(engine.Apply(new(144, double.NaN), true).Success);
    Check.Equal(0, memory.WriteCalls);
});

return suite.Finish();

sealed class TestSuite
{
    private int _passed;
    private int _failed;

    public void Run(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            _failed++;
            Console.WriteLine($"FAIL {name}: {exception.Message}");
        }
    }

    public int Finish()
    {
        Console.WriteLine($"Results: {_passed} passed, {_failed} failed, {_passed + _failed} total");
        return _failed == 0 ? 0 : 1;
    }
}

static class Check
{
    public static void True(bool condition)
    {
        if (!condition) throw new Exception("Expected true.");
    }

    public static void False(bool condition)
    {
        if (condition) throw new Exception("Expected false.");
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Expected {expected}, got {actual}.");
    }

    public static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}

sealed class FakeMemory : IProcessMemory
{
    public const ulong FovValueAddress = 0x200010;
    public const ulong FpsValueAddress = 0x200030;
    private readonly List<Block> _blocks = [];

    private sealed class Block(ulong start, byte[] data, bool committed, MemoryProtection protection)
    {
        public ulong Start { get; } = start;
        public byte[] Data { get; } = data;
        public bool Committed { get; set; } = committed;
        public MemoryProtection Protection { get; set; } = protection;
        public ulong End => Start + (ulong)Data.Length;
    }

    public static ProcessIdentity ValidIdentity => new(1234, 638000000000000000, "iw4mp", @"C:\Games\iw4mp.exe",
        0x140000000, 0x1D27000, KnownBuild.Sha256, true);

    public ProcessIdentity Identity { get; set; } = ValidIdentity;
    public bool IdentityValid { get; set; } = true;
    public ulong? ShortReadAddress { get; set; }
    public int ReadCalls { get; private set; }
    public int WriteCalls { get; private set; }
    public List<int?> WriteResultOverrides { get; } = [];
    public HashSet<int> IgnoreWriteCalls { get; } = [];

    public static FakeMemory Create()
    {
        var memory = new FakeMemory();
        memory.AddBlock(ValidIdentity.ModuleBase, (int)ValidIdentity.ModuleSize, MemoryProtection.ReadOnly);
        memory.AddBlock(0x200000, 0x40, MemoryProtection.ReadWrite);
        memory.AddBlock(0x300000, 0x40, MemoryProtection.ReadOnly);
        memory.PutUInt64(ValidIdentity.ModuleBase + KnownBuild.FovPointerRva, 0x200000);
        memory.PutUInt64(ValidIdentity.ModuleBase + KnownBuild.MaxFpsPointerRva, 0x200020);
        memory.PutUInt64(0x200000, 0x300000);
        memory.PutUInt64(0x200020, 0x300010);
        memory.PutBytes(0x300000, Encoding.ASCII.GetBytes("cg_fov\0"));
        memory.PutBytes(0x300010, Encoding.ASCII.GetBytes("com_maxfps\0"));
        memory.PutInt32(0x200010, BitConverter.SingleToInt32Bits(90));
        memory.PutInt32(0x200030, 144);
        return memory;
    }

    public bool ValidateIdentity() => IdentityValid;

    public MemoryRegion QueryRegion(ulong address)
    {
        var block = _blocks.FirstOrDefault(candidate => address >= candidate.Start && address < candidate.End);
        return block is null
            ? new(address, 0, false, MemoryProtection.NoAccess)
            : new(block.Start, (ulong)block.Data.Length, block.Committed, block.Protection);
    }

    public byte[] ReadMemory(ulong address, int count)
    {
        ReadCalls++;
        var block = FindBlock(address, count);
        var offset = checked((int)(address - block.Start));
        var length = ShortReadAddress == address && count > 0 ? count - 1 : count;
        return block.Data.AsSpan(offset, length).ToArray();
    }

    public int WriteMemory(ulong address, byte[] data)
    {
        WriteCalls++;
        if (IgnoreWriteCalls.Contains(WriteCalls)) return data.Length;
        var count = WriteCalls <= WriteResultOverrides.Count ? WriteResultOverrides[WriteCalls - 1] ?? data.Length : data.Length;
        if (count < 0 || count > data.Length) return count;
        var block = FindBlock(address, count);
        data.AsSpan(0, count).CopyTo(block.Data.AsSpan(checked((int)(address - block.Start)), count));
        return count;
    }

    public void AddBlock(ulong address, int size, MemoryProtection protection, bool committed = true) =>
        _blocks.Add(new(address, new byte[size], committed, protection));

    public void PutBytes(ulong address, byte[] data)
    {
        var block = FindBlock(address, data.Length);
        data.CopyTo(block.Data, checked((int)(address - block.Start)));
    }

    public void PutUInt64(ulong address, ulong value)
    {
        var data = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(data, value);
        PutBytes(address, data);
    }

    public void PutInt32(ulong address, int value)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(data, value);
        PutBytes(address, data);
    }

    public void SetProtection(ulong address, MemoryProtection protection) => FindBlock(address, 1).Protection = protection;
    public void SetCommitted(ulong address, bool committed) => FindBlock(address, 1).Committed = committed;
    public byte[] ReadRaw(ulong address, int count) => FindBlock(address, count).Data.AsSpan(checked((int)(address - FindBlock(address, count).Start)), count).ToArray();

    private Block FindBlock(ulong address, int count) => _blocks.FirstOrDefault(block => address >= block.Start &&
        address <= block.End && (ulong)count <= block.End - address) ??
        throw new InvalidOperationException("Fake memory range is unmapped.");

    public void Dispose() { }
}

sealed class FakeProcessSource(IProcessMemory? memory) : IProcessSource
{
    public IProcessMemory? OpenTarget() => memory;
}

sealed class MutableProcessSource : IProcessSource
{
    public IProcessMemory? Current { get; set; }
    public IProcessMemory? OpenTarget() => Current;
}
