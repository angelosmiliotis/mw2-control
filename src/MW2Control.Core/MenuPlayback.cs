using System.Buffers.Binary;

namespace MW2Control.Core;

internal sealed record MenuVolume(AliasSpec Spec, ulong Address, uint Original, int Slot, ulong Driver,
    ulong Alias, ulong File, uint PlaybackId) : SoundVolumeTarget(Spec, Address, Original);

internal static class MenuPlayback
{
    // Verified from the supported game's XAudio stream update and voice lookup.
    private const ulong VoicesRva = 0x654AD18;
    private const ulong DriversRva = 0x8BFFC78;
    private const ulong VoiceSize = 0xA8;
    private const ulong VolumeOffset = 0x60;
    private const int FirstStream = 40;
    private const int EndStream = 52;
    private const int MenuChannel = 13;
    private static readonly (string Alias, string File)[] Tracks =
    [
        ("music_mainmenu", "hz_t_menumusic.mp3"),
        ("music_mainmenu_mp", "hz_t_oilrig_themestealth_v1.mp3"),
        ("music_opening", "hz_t_openingmusic.mp3"),
    ];

    internal static bool IsMenuAlias(AliasSpec spec) =>
        spec.Channel == MenuChannel && spec.FileType == 2 && Tracks.Any(track => track.Alias == spec.Name);

    internal static IEnumerable<MenuVolume> Find(IProcessMemory memory)
    {
        for (var slot = FirstStream; slot < EndStream; slot++)
            if (Read(memory, slot) is { } current)
                yield return current;
    }

    internal static bool TryReadCurrent(IProcessMemory memory, MenuVolume target, out uint gain)
    {
        gain = 0;
        var current = Read(memory, target.Slot);
        if (current is null || current with { Original = target.Original } != target)
            return false;
        gain = current.Original;
        return true;
    }

    private static MenuVolume? Read(IProcessMemory memory, int slot)
    {
        if (slot is < FirstStream or >= EndStream) return null;
        var voiceAddress = Image(memory, VoicesRva + VoiceSize * (ulong)slot, VoiceSize);
        var voice = DvarValidator.ReadExact(memory, voiceAddress, 0x78);
        if (Word(voice, 0) != 2 || Word(voice, 0x10) != MenuChannel)
            return null;

        // Menu music uses a single alias for both endpoints. Leave any mixed
        // playback alone, even if one of its endpoints is a recognized track.
        var alias = Pointer(voice, 0x68);
        if (!TuningValidation.IsUserPointer(alias) || Pointer(voice, 0x70) != alias)
            return null;
        var row = DvarValidator.ReadExact(memory, alias, 0x54);
        if (((Word(row, 0x50) >> 9) & 63) != MenuChannel)
            return null;
        var name = Pointer(row, 0);
        foreach (var track in Tracks)
        {
            if (!SoundAliasBank.HasName(memory, name, track.Alias)) continue;
            var file = Pointer(row, 0x28);
            var sound = DvarValidator.ReadExact(memory, file, 24);
            if (sound[0] != 2 || sound[1] != 1 ||
                !SoundAliasBank.HasName(memory, Pointer(sound, 8), "music") ||
                !SoundAliasBank.HasName(memory, Pointer(sound, 16), track.File))
                return null;

            var driverSlot = Image(memory, DriversRva + 8UL * (ulong)slot, 8);
            var driver = Pointer(DvarValidator.ReadExact(memory, driverSlot, 8), 0);
            if (!TuningValidation.IsUserPointer(driver)) return null;
            var gainAddress = voiceAddress + VolumeOffset;
            var gain = Word(DvarValidator.ReadExact(memory, gainAddress, 4, requireWritable: true), 0);
            if (!SoundAliasBank.IsGain(gain)) return null;
            return new(new(track.Alias, MenuChannel, 2), gainAddress, gain, slot,
                driver, alias, file, Word(voice, 0x20));
        }
        return null;
    }

    private static ulong Image(IProcessMemory memory, ulong rva, ulong size)
    {
        DvarValidator.Require(rva + size <= memory.Identity.ModuleSize, "Menu voice lies outside the game image.");
        return checked(memory.Identity.ModuleBase + rva);
    }

    private static uint Word(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
    private static ulong Pointer(byte[] data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset));
}
