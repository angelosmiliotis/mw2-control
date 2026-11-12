using System.Buffers.Binary;
using System.Text;

namespace MW2Control.Core;

internal interface ISelectiveAudioMemory : IProcessMemory
{
    int WriteSoundVolume(SoundVolumeTarget target, uint expected, uint desired);
}

internal sealed record AliasSpec(string Name, int Channel, byte FileType);
internal abstract record SoundVolumeTarget(AliasSpec Spec, ulong Address, uint Original);
internal sealed record AliasVolume(AliasSpec Spec, int Row, ulong List, ulong Head, ulong File,
    int Count, ulong Address, uint Original) : SoundVolumeTarget(Spec, Address, Original);

internal sealed class SelectiveAudio(ISelectiveAudioMemory memory)
{
    private readonly Dictionary<ulong, SoundVolumeTarget> owned = [];
    private readonly MenuAudioMute menu = new(memory);
    private bool muteActive;

    internal bool MenuNeedsCheck => muteActive || menu.ChangedCount > 0;

    private int AliasCount => owned.Values.OfType<AliasVolume>().Count() / 2;

    internal FeatureReading Inspect()
    {
        try
        {
            var stream = AdvancedDvarValidator.Resolve(memory, AdvancedDvar.StreamEnabled);
            return new("music", "Selective mute", true, owned.Count == 0 && menu.ChangedCount == 0 ? 0 : 1, stream.Value == 0
                ? "Streaming is off. Set snd_enableStream to 1 and restart the game before using selective mute."
                : $"Music, round announcements and match countdowns. {AliasCount} match sound rows changed. {menu.Status}");
        }
        catch (Exception ex) when (TuningTransaction.IsExpectedFailure(ex))
        {
            return new("music", "Selective mute", false, null, ex.Message);
        }
    }

    internal TransactionResult Apply(bool mute)
    {
        var result = ApplyTargets(mute);
        if (!result.Success) return result;
        muteActive = mute;
        menu.Update(mute);
        return new(true, result.Message + " " + menu.Status);
    }

    internal void MaintainMenuMute()
    {
        if (MenuNeedsCheck) menu.Update(muteActive);
    }

    private TransactionResult ApplyTargets(bool mute)
    {
        if (!mute && owned.Count == 0) return new(true, "No sound volumes need restoring.");
        var changes = new List<(SoundVolumeTarget Target, uint Before, uint After)>();
        var attempted = new List<(SoundVolumeTarget Target, uint Before, uint Written)>();
        var stillOwned = new Dictionary<ulong, SoundVolumeTarget>();
        try
        {
            SoundAliasBank.CheckSession(memory);
            // Mute min before max and restore max before min.
            foreach (var target in mute ? owned.Values.AsEnumerable() : owned.Values.Reverse())
            {
                if (!SoundAliasBank.TryReadOwned(memory, target, out var current)) continue;
                DvarValidator.Require(current == 0 || current == target.Original, $"{target.Spec.Name}: volume changed externally.");
                if (mute) stillOwned.Add(target.Address, target);
                if (current != (mute ? 0 : target.Original))
                    changes.Add((target, current, mute ? 0 : target.Original));
            }

            if (mute)
            {
                DvarValidator.Require(AdvancedDvarValidator.Resolve(memory, AdvancedDvar.StreamEnabled).Value == 1,
                    "Selective mute needs streaming enabled. Set snd_enableStream to 1 with the game closed, then relaunch.");
                var targets = SoundAliasCatalog.Targets.Where(spec => !MenuPlayback.IsMenuAlias(spec))
                    .SelectMany(spec => SoundAliasBank.Resolve(memory, spec));
                foreach (var target in targets)
                {
                    if (!stillOwned.TryAdd(target.Address, target)) continue;
                    if (target.Original == 0) stillOwned.Remove(target.Address);
                    else changes.Add((target, target.Original, 0));
                }
            }

            foreach (var (target, before, after) in changes)
            {
                int bytes;
                try { bytes = memory.WriteSoundVolume(target, before, after); }
                catch (MemoryWriteException ex)
                {
                    if (ex.BytesWritten is > 0 and <= 4)
                        attempted.Add((target, before, Partial(before, after, ex.BytesWritten)));
                    throw;
                }
                if (bytes is > 0 and <= 4) attempted.Add((target, before, Partial(before, after, bytes)));
                DvarValidator.Require(bytes == 4, "Short sound-volume write.");
                DvarValidator.Require(SoundAliasBank.TryReadOwned(memory, target, out var actual) && actual == after,
                    $"{target.Spec.Name}: sound volume could not be verified.");
            }
            owned.Clear();
            foreach (var pair in stillOwned) owned.Add(pair.Key, pair.Value);
            return new(true, mute ? $"Selective mute applied to {AliasCount} loaded match sound rows."
                : "Original owned sound volumes restored.");
        }
        catch (Exception ex) when (TuningTransaction.IsExpectedFailure(ex))
        {
            var rollbackIssues = 0;
            for (var i = attempted.Count - 1; i >= 0; i--)
            {
                var (target, before, written) = attempted[i];
                try
                {
                    if (memory.WriteSoundVolume(target, written, before) != 4 ||
                        !SoundAliasBank.TryReadOwned(memory, target, out var restored) || restored != before)
                        rollbackIssues++;
                }
                catch (Exception rollback) when (TuningTransaction.IsExpectedFailure(rollback))
                {
                    rollbackIssues++;
                }
            }
            return new(false, "Selective mute paused. " + ex.Message + (rollbackIssues == 0 ? "" :
                $" {rollbackIssues} volume restores could not be verified. Restart the game to reload its original sounds."));
        }
    }

    private static uint Partial(uint before, uint after, int count)
    {
        var mask = uint.MaxValue >> ((4 - count) * 8);
        return (before & ~mask) | (after & mask);
    }
}

internal static class SoundAliasBank
{
    // The installed Steam x64 build's sound lookup and asset database. Match the
    // full executable fingerprint before interpreting any of these fields.
    private const ulong Buckets = 0x1658020;
    private const ulong Entries = 0x166E160;
    private const ulong DatabaseWriter = 0x166A134;
    private const uint BucketCount = 0x9088;
    private const uint SoundAssetType = 11;
    private const ulong RowSize = 0x88;

    internal static void CheckSession(IProcessMemory memory)
    {
        var profile = TuningValidation.ValidateProfile(memory.Identity);
        DvarValidator.Require(profile.Success && memory.ValidateIdentity(), "The game build or process lifetime changed.");
        CheckDatabase(memory);
    }

    internal static void CheckDatabase(IProcessMemory memory) =>
        DvarValidator.Require(ReadUInt32(DvarValidator.ReadExact(memory, Image(memory, DatabaseWriter, 4), 4)) == 0,
            "The game is changing its audio assets. Apply again after loading.");

    internal static AliasVolume[] Resolve(IProcessMemory memory, AliasSpec spec)
    {
        CheckDatabase(memory);
        var list = FindList(memory, spec.Name);
        if (list == 0) return [];
        var header = DvarValidator.ReadExact(memory, list, 20);
        var head = ReadUInt64(header, 8);
        var count = (int)ReadUInt32(header, 16);
        // The game's zone loader can place x64 alias rows on a four-byte boundary.
        DvarValidator.Require(count is > 0 and <= 64 && head % 4 == 0, $"{spec.Name}: unexpected sound alias array.");
        var volumes = new List<AliasVolume>(count * 2);
        for (var i = 0; i < count; i++)
        {
            var row = ReadRow(memory, spec, head, i);
            var min = ReadUInt32(row, 0x34);
            var max = ReadUInt32(row, 0x38);
            DvarValidator.Require(IsGain(min) && IsGain(max), $"{spec.Name}: invalid volume values.");
            var file = ReadUInt64(row, 0x28);
            var address = checked(head + (ulong)i * RowSize + 0x34);
            volumes.Add(new(spec, i, list, head, file, count, address, min));
            volumes.Add(new(spec, i, list, head, file, count, address + 4, max));
        }
        CheckDatabase(memory);
        return volumes.ToArray();
    }

    internal static bool TryReadOwned(IProcessMemory memory, SoundVolumeTarget target, out uint current)
    {
        CheckDatabase(memory);
        if (target is MenuVolume menu)
        {
            var found = MenuPlayback.TryReadCurrent(memory, menu, out current);
            CheckDatabase(memory);
            return found;
        }
        return TryReadAlias(memory, (AliasVolume)target, out current);
    }

    private static bool TryReadAlias(IProcessMemory memory, AliasVolume target, out uint current)
    {
        current = 0;
        if (FindList(memory, target.Spec.Name) != target.List) return false;
        var header = DvarValidator.ReadExact(memory, target.List, 20);
        if (ReadUInt64(header, 8) != target.Head || ReadUInt32(header, 16) != target.Count) return false;
        if (target.Row < 0 || target.Row >= target.Count) return false;
        var rowAddress = checked(target.Head + (ulong)target.Row * RowSize);
        if (target.Address != rowAddress + 0x34 && target.Address != rowAddress + 0x38) return false;
        var row = ReadRow(memory, target.Spec, target.Head, target.Row);
        if (ReadUInt64(row, 0x28) != target.File) return false;
        current = ReadUInt32(DvarValidator.ReadExact(memory, target.Address, 4, requireWritable: true));
        CheckDatabase(memory);
        return true;
    }

    internal static bool IsGain(uint bits) =>
        BitConverter.UInt32BitsToSingle(bits) is >= 0 and <= 4;

    private static byte[] ReadRow(IProcessMemory memory, AliasSpec spec, ulong head, int row)
    {
        var data = DvarValidator.ReadExact(memory, checked(head + (ulong)row * RowSize), 0x54, requireWritable: true);
        DvarValidator.Require(HasName(memory, ReadUInt64(data), spec.Name) &&
            ((ReadUInt32(data, 0x50) >> 9) & 63) == spec.Channel, $"{spec.Name}: row identity or channel differs.");
        var file = DvarValidator.ReadExact(memory, ReadUInt64(data, 0x28), 2);
        DvarValidator.Require(file[0] == spec.FileType && file[1] == 1, $"{spec.Name}: sound file type or availability differs.");
        return data;
    }

    private static ulong FindList(IProcessMemory memory, string name)
    {
        uint hash = SoundAssetType;
        foreach (var character in name) hash = unchecked(hash * 31 + char.ToLowerInvariant(character));
        var index = BinaryPrimitives.ReadUInt16LittleEndian(DvarValidator.ReadExact(memory,
            Image(memory, Buckets + 2 * (hash % BucketCount), 2), 2));
        var visited = new HashSet<ushort>();
        while (index != 0)
        {
            DvarValidator.Require(visited.Count < 128 && visited.Add(index), "Invalid asset bucket chain.");
            var entry = DvarValidator.ReadExact(memory, Image(memory, Entries + 24UL * index, 24), 24);
            if (ReadUInt32(entry) == SoundAssetType)
            {
                var list = ReadUInt64(entry, 8);
                var aliasName = ReadUInt64(DvarValidator.ReadExact(memory, list, 8));
                if (HasName(memory, aliasName, name))
                    return (entry[17] & 5) == 0 ? list : 0;
            }
            index = BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(18));
        }
        return 0;
    }

    internal static bool HasName(IProcessMemory memory, ulong address, string name)
    {
        var raw = DvarValidator.ReadExact(memory, address, name.Length + 1);
        return raw[^1] == 0 && string.Equals(Encoding.ASCII.GetString(raw, 0, name.Length), name,
            StringComparison.OrdinalIgnoreCase);
    }

    private static ulong Image(IProcessMemory memory, ulong rva, int count)
    {
        DvarValidator.Require(rva + (ulong)count <= memory.Identity.ModuleSize, "Sound database address is outside the game image.");
        return checked(memory.Identity.ModuleBase + rva);
    }

    private static uint ReadUInt32(byte[] data, int offset = 0) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
    private static ulong ReadUInt64(byte[] data, int offset = 0) => BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset));
}
