using System.Buffers.Binary;
using System.Text;
using MW2Control.Core;

namespace MW2Control.Advanced.Tests;

// Sparse synthetic data only. Does not instantiate any native source or game process.
internal sealed class FakeMemory : IProcessMemory
{
    private readonly Dictionary<ulong, byte> bytes = [];
    public static readonly ProcessIdentity SupportedIdentity = new(1234, 638000000000000000, "iw4mp", @"C:\Games\iw4mp.exe",
        0x140000000, 0x9000000, KnownBuild.Sha256, true);
    public ProcessIdentity Identity { get; set; } = SupportedIdentity;
    public bool IdentityValid { get; set; } = true;
    public bool Disposed { get; private set; }
    public int WriteCalls { get; private set; }
    public int ReadCalls { get; private set; }
    public ulong? ShortRead { get; set; }
    public Dictionary<ulong, MemoryProtection> Protections { get; } = [];
    public Dictionary<int, int> WriteCounts { get; } = [];
    public HashSet<int> IgnoreWrites { get; } = [];
    public Action<int, ulong, byte[]>? BeforeWrite { get; set; }
    public Action<int, ulong, byte[]>? AfterWrite { get; set; }
    public bool FailPartialWithException { get; set; }
    public List<(ulong Address, byte[] Bytes)> Writes { get; } = [];

    internal static readonly IReadOnlyDictionary<AdvancedDvar, (string Name, ulong Slot)> Definitions =
        new Dictionary<AdvancedDvar, (string, ulong)>
        {
            [AdvancedDvar.StreamEnabled] = ("snd_enableStream", AdvancedBuild.StreamPointerRva),
            [AdvancedDvar.Fullbright] = ("r_fullbright", AdvancedBuild.FullbrightPointerRva),
            [AdvancedDvar.Fog] = ("r_fog", AdvancedBuild.FogPointerRva),
            [AdvancedDvar.Glow] = ("r_glow", AdvancedBuild.GlowPointerRva),
            [AdvancedDvar.SpecularMap] = ("r_specularMap", AdvancedBuild.SpecularPointerRva),
            [AdvancedDvar.Detail] = ("r_detail", AdvancedBuild.DetailPointerRva),
            [AdvancedDvar.DetailMap] = ("r_detailMap", AdvancedBuild.DetailMapPointerRva),
            [AdvancedDvar.PingText] = ("cg_scoreboardPingText", AdvancedBuild.PingTextPointerRva),
            [AdvancedDvar.PingGraph] = ("cg_scoreboardPingGraph", AdvancedBuild.PingGraphPointerRva)
        };
    public static ulong Dvar(AdvancedDvar feature) => 0x200000 + (ulong)feature * 0x100;
    public static ulong Value(AdvancedDvar feature) => Dvar(feature) + 0x10;
    public static ulong Name(AdvancedDvar feature) => 0x300000 + (ulong)feature * 0x100;

    public static FakeMemory Create()
    {
        var memory = new FakeMemory();
        foreach (var (feature, definition) in Definitions)
        {
            memory.Put64(SupportedIdentity.ModuleBase + definition.Slot, Dvar(feature));
            memory.Put64(Dvar(feature), Name(feature));
            memory.Put(Name(feature), Encoding.ASCII.GetBytes(definition.Name + "\0"));
            memory.Put(Dvar(feature) + 0x0C, [(byte)(feature is AdvancedDvar.SpecularMap or AdvancedDvar.DetailMap ? 6 : 0)]);
            memory.Put(Dvar(feature) + 0x0D, [0]);
            // Canary bytes in the union and defaults/latched fields must never be written.
            memory.Put(Value(feature), [0, 0xA5, 0xA5, 0xA5]);
            memory.Put(Dvar(feature) + 0x20, [0x31, 0x32, 0x33, 0x34]);
            memory.Put(Dvar(feature) + 0x30, [0x41, 0x42, 0x43, 0x44]);
            if (feature is AdvancedDvar.SpecularMap or AdvancedDvar.DetailMap) memory.Put32(Value(feature), 1);
            else memory.Put(Value(feature), [(byte)(feature is AdvancedDvar.Fullbright or AdvancedDvar.PingText ? 0 : 1)]);
        }
        memory.AddEnum(AdvancedDvar.SpecularMap, AdvancedBuild.SpecularEnumTableRva,
            ["Black", "Unchanged", "White", "Gray"], [0x3C2F14, 0x3C2F20, 0x3C2F2C, 0x3C2F34]);
        memory.AddEnum(AdvancedDvar.DetailMap, AdvancedBuild.DetailMapEnumTableRva,
            ["Hidden", "Unchanged"], [0x3C2F3C, 0x3C2F20]);
        memory.Put64(SupportedIdentity.ModuleBase + KnownBuild.FovPointerRva, 0x210000);
        memory.Put64(SupportedIdentity.ModuleBase + KnownBuild.MaxFpsPointerRva, 0x210100);
        memory.Put64(0x210000, 0x310000);
        memory.Put64(0x210100, 0x310100);
        memory.Put(0x310000, Encoding.ASCII.GetBytes("cg_fov\0"));
        memory.Put(0x310100, Encoding.ASCII.GetBytes("com_maxfps\0"));
        memory.Put32(0x210010, BitConverter.SingleToInt32Bits(90));
        memory.Put32(0x210110, 144);
        memory.Put(0x21000C, [1]);
        memory.Put32(0x210040, BitConverter.SingleToInt32Bits(65));
        memory.Put32(0x210044, BitConverter.SingleToInt32Bits(80));
        memory.Put(0x21010C, [5]);
        memory.Put32(0x210140, 0);
        memory.Put32(0x210144, 100);
        return memory;
    }

    private void AddEnum(AdvancedDvar feature, ulong table, string[] labels, ulong[] rvas)
    {
        Put32(Dvar(feature) + 0x40, labels.Length);
        Put64(Dvar(feature) + 0x48, SupportedIdentity.ModuleBase + table);
        for (var i = 0; i < labels.Length; i++)
        {
            Put64(SupportedIdentity.ModuleBase + table + (ulong)i * 8, SupportedIdentity.ModuleBase + rvas[i]);
            Put(SupportedIdentity.ModuleBase + rvas[i], Encoding.ASCII.GetBytes(labels[i] + "\0"));
        }
        Put64(SupportedIdentity.ModuleBase + table + (ulong)labels.Length * 8, 0);
    }

    public void Missing(AdvancedDvar feature) => Put64(SupportedIdentity.ModuleBase + Definitions[feature].Slot, 0);
    public bool ValidateIdentity() => IdentityValid && !Disposed;
    public MemoryRegion QueryRegion(ulong address)
    {
        if (address >= SupportedIdentity.ModuleBase && address - SupportedIdentity.ModuleBase < SupportedIdentity.ModuleSize)
            return new(SupportedIdentity.ModuleBase, SupportedIdentity.ModuleSize, true, MemoryProtection.ReadOnly);
        var protection = Protections.TryGetValue(address, out var custom) ? custom :
            address is >= 0x200000 and < 0x220000 ? MemoryProtection.ReadWrite : MemoryProtection.ReadOnly;
        return new(address & ~0xFFFUL, 0x1000, true, protection);
    }
    public byte[] ReadMemory(ulong address, int count)
    {
        ReadCalls++;
        return Raw(address, ShortRead == address ? count - 1 : count);
    }
    public byte[] Raw(ulong address, int count) => Enumerable.Range(0, count).Select(i => bytes.TryGetValue(address + (ulong)i, out var value)
        ? value : throw new IOException("Synthetic range is unmapped.")).ToArray();
    public int WriteMemory(ulong address, byte[] data)
    {
        WriteCalls++;
        BeforeWrite?.Invoke(WriteCalls, address, data);
        Writes.Add((address, (byte[])data.Clone()));
        if (IgnoreWrites.Contains(WriteCalls)) return data.Length;
        var count = WriteCounts.TryGetValue(WriteCalls, out var custom) ? custom : data.Length;
        if (count >= 0 && count <= data.Length) Put(address, data.AsSpan(0, count).ToArray());
        AfterWrite?.Invoke(WriteCalls, address, data);
        if (FailPartialWithException && count != data.Length) throw new MemoryWriteException("Synthetic partial failure.", count);
        return count;
    }
    public int Current(AdvancedDvar feature) => feature is AdvancedDvar.SpecularMap or AdvancedDvar.DetailMap
        ? BinaryPrimitives.ReadInt32LittleEndian(Raw(Value(feature), 4)) : Raw(Value(feature), 1)[0];
    public void SetCurrent(AdvancedDvar feature, int value)
    {
        if (feature is AdvancedDvar.SpecularMap or AdvancedDvar.DetailMap) Put32(Value(feature), value);
        else Put(Value(feature), [(byte)value]);
    }
    public void Put(ulong address, byte[] value)
    {
        for (var i = 0; i < value.Length; i++) bytes[address + (ulong)i] = value[i];
    }
    public void Put64(ulong address, ulong value)
    {
        var valueBytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(valueBytes, value);
        Put(address, valueBytes);
    }
    public void Put32(ulong address, int value)
    {
        var valueBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(valueBytes, value);
        Put(address, valueBytes);
    }
    internal IReadOnlyDictionary<ulong, byte> ExportBytes() => new Dictionary<ulong, byte>(bytes);
    public void Dispose() => Disposed = true;
}

internal sealed class FakeSource(IProcessMemory memory) : IProcessSource
{
    public IProcessMemory Memory { get; set; } = memory;
    public int OpenCalls { get; private set; }
    public IProcessMemory? OpenTarget() { OpenCalls++; return Memory; }
}
