namespace MW2Control.Core;

/// <summary>The only permitted renderer notification: r_fullbright.modified = 1.</summary>
internal static class FullbrightRefresh
{
    internal static ulong Validate(IProcessMemory memory, ulong valueAddress)
    {
        var current = AdvancedDvarValidator.Resolve(memory, AdvancedDvar.Fullbright, requireWritable: true);
        DvarValidator.Require(current.Address == valueAddress, "Fullbright mapping changed before renderer notification.");
        var marker = checked(valueAddress - 3); // dvar + 0x0D, separate from permission flags at +0x08.
        var raw = DvarValidator.ReadExact(memory, marker, 1, requireWritable: true);
        DvarValidator.Require(raw[0] <= 1, "Fullbright modified marker is outside its boolean domain.");
        return marker;
    }

    internal static void Request(IProcessMemory memory, ulong valueAddress)
    {
        var marker = Validate(memory, valueAddress);
        DvarValidator.Require(memory.ValidateIdentity(), "Session changed before renderer notification.");
        var count = memory.WriteMemory(marker, [1]);
        DvarValidator.Require(count == 1, "Fullbright renderer notification returned a short write.");
        // The renderer consumes this marker itself: 0 (consumed) and 1 (pending)
        // are valid. Never restore or continuously maintain an engine-owned marker.
        var after = DvarValidator.ReadExact(memory, marker, 1);
        DvarValidator.Require(after[0] <= 1, "Fullbright renderer notification is no longer a valid marker.");
    }
}
