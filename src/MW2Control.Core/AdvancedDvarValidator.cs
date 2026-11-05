using System.Buffers.Binary;
using System.Text;

namespace MW2Control.Core;

/// <summary>Offline registration evidence for KnownBuild.Sha256 only; see the core README.</summary>
public static class AdvancedBuild
{
    public const ulong StreamPointerRva = 0x65449E0;
    public const ulong FullbrightPointerRva = 0x8CF78C8;
    public const ulong FogPointerRva = 0x8CF7C08;
    public const ulong GlowPointerRva = 0x8CF7D78;
    public const ulong SpecularPointerRva = 0x8CF7880;
    public const ulong DetailPointerRva = 0x8CF7DB0;
    public const ulong DetailMapPointerRva = 0x8CF78A8;
    public const ulong SpecularEnumTableRva = 0x577860;
    public const ulong DetailMapEnumTableRva = 0x5779C0;
    public const ulong PingTextPointerRva = 0x80E5C0;
    public const ulong PingGraphPointerRva = 0x80E5F8;
}

/// <summary>Read-only validation of exact fixed-cache/name/type/domain/value for optional controls.</summary>
public static class AdvancedDvarValidator
{
    private sealed record Descriptor(string Name, ulong Slot, ulong EnumTable = 0, string[]? Labels = null, ulong[]? LabelRvas = null);
    private static readonly IReadOnlyDictionary<AdvancedDvar, Descriptor> Descriptors = new Dictionary<AdvancedDvar, Descriptor>
    {
        [AdvancedDvar.StreamEnabled] = new("snd_enableStream", AdvancedBuild.StreamPointerRva),
        [AdvancedDvar.Fullbright] = new("r_fullbright", AdvancedBuild.FullbrightPointerRva),
        [AdvancedDvar.Fog] = new("r_fog", AdvancedBuild.FogPointerRva),
        [AdvancedDvar.Glow] = new("r_glow", AdvancedBuild.GlowPointerRva),
        [AdvancedDvar.SpecularMap] = new("r_specularMap", AdvancedBuild.SpecularPointerRva,
            AdvancedBuild.SpecularEnumTableRva, ["Black", "Unchanged", "White", "Gray"], [0x3C2F14, 0x3C2F20, 0x3C2F2C, 0x3C2F34]),
        [AdvancedDvar.Detail] = new("r_detail", AdvancedBuild.DetailPointerRva),
        [AdvancedDvar.DetailMap] = new("r_detailMap", AdvancedBuild.DetailMapPointerRva,
            AdvancedBuild.DetailMapEnumTableRva, ["Hidden", "Unchanged"], [0x3C2F3C, 0x3C2F20]),
        [AdvancedDvar.PingText] = new("cg_scoreboardPingText", AdvancedBuild.PingTextPointerRva),
        [AdvancedDvar.PingGraph] = new("cg_scoreboardPingGraph", AdvancedBuild.PingGraphPointerRva)
    };

    public static ValidatedAdvancedDvar Resolve(IProcessMemory memory, AdvancedDvar feature, bool requireWritable = false)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var profile = TuningValidation.ValidateProfile(memory.Identity);
        DvarValidator.Require(profile.Success, profile.Message);
        DvarValidator.Require(memory.ValidateIdentity(), "Process lifetime or main module mapping changed.");
        if (!Descriptors.TryGetValue(feature, out var descriptor)) throw new ArgumentOutOfRangeException(nameof(feature));
        var slot = ModuleAddress(memory.Identity, descriptor.Slot, 8);
        var dvar = BinaryPrimitives.ReadUInt64LittleEndian(DvarValidator.ReadExact(memory, slot, 8));
        DvarValidator.Require(TuningValidation.IsUserPointer(dvar, 8), $"{descriptor.Name}: invalid dvar pointer.");
        var name = BinaryPrimitives.ReadUInt64LittleEndian(DvarValidator.ReadExact(memory, dvar, 8));
        var expected = Encoding.ASCII.GetBytes(descriptor.Name + "\0");
        DvarValidator.Require(DvarValidator.ReadExact(memory, name, expected.Length).AsSpan().SequenceEqual(expected),
            $"Dvar name does not exactly match {descriptor.Name}.");
        var kind = DvarValidator.ReadExact(memory, checked(dvar + 0x0C), 1)[0];
        var isEnum = descriptor.Labels is not null;
        DvarValidator.Require(kind == (isEnum ? 6 : 0), $"{descriptor.Name}: unsupported dvar type.");
        if (isEnum) ValidateEnumDomain(memory, dvar, descriptor);
        var address = checked(dvar + KnownBuild.ValueOffset);
        var raw = DvarValidator.ReadExact(memory, address, isEnum ? 4 : 1, requireWritable);
        var value = isEnum ? BinaryPrimitives.ReadInt32LittleEndian(raw) : raw[0];
        DvarValidator.Require(value >= 0 && value < (descriptor.Labels?.Length ?? 2), $"{descriptor.Name}: value is outside its validated domain.");
        return new(feature, address, value, raw);
    }

    private static void ValidateEnumDomain(IProcessMemory memory, ulong dvar, Descriptor descriptor)
    {
        var count = BinaryPrimitives.ReadInt32LittleEndian(DvarValidator.ReadExact(memory, checked(dvar + 0x40), 4));
        DvarValidator.Require(count == descriptor.Labels!.Length, $"{descriptor.Name}: enum count does not match this build.");
        var table = BinaryPrimitives.ReadUInt64LittleEndian(DvarValidator.ReadExact(memory, checked(dvar + 0x48), 8));
        var expectedTable = ModuleAddress(memory.Identity, descriptor.EnumTable, checked((count + 1) * 8));
        DvarValidator.Require(table == expectedTable && TuningValidation.IsUserPointer(table, 8), $"{descriptor.Name}: enum table pointer changed.");
        var pointers = DvarValidator.ReadExact(memory, table, checked((count + 1) * 8));
        for (var index = 0; index < count; index++)
        {
            var label = Encoding.ASCII.GetBytes(descriptor.Labels[index] + "\0");
            var expectedAddress = ModuleAddress(memory.Identity, descriptor.LabelRvas![index], label.Length);
            DvarValidator.Require(BinaryPrimitives.ReadUInt64LittleEndian(pointers.AsSpan(index * 8, 8)) == expectedAddress,
                $"{descriptor.Name}: enum entry pointer changed.");
            DvarValidator.Require(DvarValidator.ReadExact(memory, expectedAddress, label.Length).AsSpan().SequenceEqual(label),
                $"{descriptor.Name}: enum labels do not match this build.");
        }
        DvarValidator.Require(BinaryPrimitives.ReadUInt64LittleEndian(pointers.AsSpan(count * 8, 8)) == 0,
            $"{descriptor.Name}: enum table terminator is invalid.");
    }

    internal static byte[] Encode(AdvancedDvar feature, int value)
    {
        var descriptor = Descriptors[feature];
        DvarValidator.Require(value >= 0 && value < (descriptor.Labels?.Length ?? 2), "Advanced value is outside its validated domain.");
        if (descriptor.Labels is null) return [(byte)value];
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static ulong ModuleAddress(ProcessIdentity identity, ulong rva, int count)
    {
        DvarValidator.Require(count > 0 && (ulong)count <= identity.ModuleSize && rva <= identity.ModuleSize - (ulong)count,
            "Optional feature slot/domain lies outside the main module; this feature is unavailable.");
        return checked(identity.ModuleBase + rva);
    }
}

/// <summary>Fixed whitelist used again at the native write boundary, also testable with fake memory.</summary>
public static class DvarWritePolicy
{
    public static bool IsAllowed(IProcessMemory memory, ulong address, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(data);
        if (!TuningValidation.ValidateProfile(memory.Identity).Success || !memory.ValidateIdentity()) return false;
        if (data.Length == 1 && data[0] == 1)
        {
            try
            {
                var fullbright = AdvancedDvarValidator.Resolve(memory, AdvancedDvar.Fullbright, requireWritable: true);
                if (address == fullbright.Address - 3)
                    return FullbrightRefresh.Validate(memory, fullbright.Address) == address;
            }
            catch (Exception ex) when (TuningTransaction.IsExpectedFailure(ex)) { }
        }
        if (data.Length == 4)
        {
            foreach (var feature in Enum.GetValues<TuningFeature>())
            {
                try
                {
                    var current = DvarValidator.Resolve(memory, feature, requireWritable: true);
                    var bits = BinaryPrimitives.ReadInt32LittleEndian(data);
                    var value = feature == TuningFeature.Fov ? BitConverter.Int32BitsToSingle(bits) : (double)bits;
                    if (current.Address == address && TuningValidation.IsSensibleValue(feature, value)) return true;
                }
                catch (Exception ex) when (TuningTransaction.IsExpectedFailure(ex)) { }
            }
        }
        foreach (var feature in Enum.GetValues<AdvancedDvar>())
        {
            try
            {
                var current = AdvancedDvarValidator.Resolve(memory, feature, requireWritable: true);
                if (current.Address != address || current.RawValue.Length != data.Length) continue;
                if (feature == AdvancedDvar.Fullbright) FullbrightRefresh.Validate(memory, current.Address);
                var value = data.Length == 1 ? data[0] : BinaryPrimitives.ReadInt32LittleEndian(data);
                return AdvancedDvarValidator.Encode(feature, value).AsSpan().SequenceEqual(data);
            }
            catch (Exception ex) when (TuningTransaction.IsExpectedFailure(ex)) { }
        }
        return false;
    }
}
