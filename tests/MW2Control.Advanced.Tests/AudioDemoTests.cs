using System.Buffers.Binary;
using System.Text;
using MW2Control.Core;

namespace MW2Control.Advanced.Tests;

internal static class AudioDemoTests
{
    internal static void MutesOnlyTheActiveMenuMusic()
    {
        using var memory = new AudioMemory();
        var track = memory.Add("music_mainmenu_mp", 13, 2);
        memory.RemoveLookup("music_mainmenu_mp");
        var music = memory.AddMenuVoice(44, track, "hz_t_oilrig_themestealth_v1.mp3");
        var clickAlias = memory.Add("mouse_click", 13, 2);
        var click = memory.AddMenuVoice(45, clickAlias, "ui_click.wav");
        var audio = new SelectiveAudio(memory);
        Assert(audio.Apply(true));
        if (memory.Gain(music) != 0 || memory.Gain(click) != 0.73f)
            throw new Exception("Only the active menu music should be silenced, including after its zone is unloaded.");
        Assert(audio.Apply(false));
        if (memory.Gain(music) != 0.73f || memory.Gain(click) != 0.73f)
            throw new Exception("Reset must restore the active menu music gain.");
        Assert(audio.Apply(true));
        memory.ReplaceVoiceDriver(44);
        var writes = memory.Inner.WriteCalls;
        Assert(audio.Apply(false));
        if (memory.Inner.WriteCalls != writes)
            throw new Exception("A reused voice slot must never receive a stale restore.");
    }

    internal static void MutesMenuMusicStartingAfterApply()
    {
        using var memory = new AudioMemory();
        var timer = memory.Add("ui_mp_timer_countdown", 10, 1);
        var audio = new SelectiveAudio(memory);
        Assert(audio.Apply(true));
        var track = memory.Add("music_mainmenu_mp", 13, 2);
        var music = memory.AddMenuVoice(44, track, "hz_t_oilrig_themestealth_v1.mp3");
        var clickAlias = memory.Add("mouse_click", 13, 2);
        var click = memory.AddMenuVoice(45, clickAlias, "ui_click.wav");
        audio.MaintainMenuMute();
        if (memory.Gain(music) != 0 || memory.Gain(click) != 0.73f || !memory.AtVolume(timer, 0))
            throw new Exception("Returning to the menu should mute only the new music stream.");
        Assert(audio.Apply(false));
        audio.MaintainMenuMute();
        if (memory.Gain(music) != 0.73f || memory.Gain(click) != 0.73f || !memory.AtVolume(timer, 0.73f))
            throw new Exception("Turning mute off must restore owned gains and stop the menu check.");
    }

    internal static void KeepsMatchSettingsWhenLobbyMusicChanges()
    {
        using var memory = new AudioMemory();
        var timer = memory.Add("ui_mp_timer_countdown", 10, 1);
        var track = memory.Add("music_mainmenu_mp", 13, 2);
        var music = memory.AddMenuVoice(44, track, "hz_t_oilrig_themestealth_v1.mp3");
        var audio = new SelectiveAudio(memory);
        memory.Inner.AfterWrite = (_, address, _) =>
        {
            if (address == music)
                memory.Inner.Put32(music, BitConverter.SingleToInt32Bits(0.52f));
        };
        var first = audio.Apply(true);
        Assert(first);
        if (!first.Message.Contains("Retrying", StringComparison.Ordinal) ||
            !memory.AtVolume(timer, 0) || memory.Gain(music) != 0.52f)
            throw new Exception("A changing lobby stream must not roll back the match mute.");

        memory.Inner.AfterWrite = null;
        audio.MaintainMenuMute();
        if (memory.Gain(music) != 0 || !memory.AtVolume(timer, 0))
            throw new Exception("The same validated stream should be muted on the next check.");
        Assert(audio.Apply(false));
        if (memory.Gain(music) != 0.73f || !memory.AtVolume(timer, 0.73f))
            throw new Exception("Retry must preserve the original restoration volumes.");
    }

    internal static void PreservesGameCues()
    {
        using var memory = new AudioMemory();
        var wanted = new[]
        {
            memory.Add("music_mainmenu_mp", 13, 2),
            memory.Add("us_1mc_encourage_win", 35, 2, fourByteAligned: true),
            memory.Add("ui_mp_timer_countdown", 10, 1),
            memory.Add("match_countdown_tick", 13, 1),
        };
        var preserved = new[]
        {
            memory.Add("mp_killstreak_ac130", 10, 2),
            memory.Add("mp_killstreak_choppergunner", 10, 2),
            memory.Add("harrier_engine_high", 11, 2),
            memory.Add("predator_drone_static", 10, 2),
            memory.Add("oxygen_tank_leak_loop", 3, 2),
            memory.Add("cobra_helicopter_crash_dist", 24, 2),
            memory.Add("us_1mc_enemy_ac130", 35, 2),
            memory.Add("bomb_tick", 25, 1),
            memory.Add("ui_mp_suitcasebomb_timer", 10, 1),
            memory.Add("ui_mp_suitcasebomb_double_timer", 10, 1),
            memory.Add("exp_suitcase_bomb_main", 20, 1),
            memory.Add("mouse_click", 13, 1),
        };
        var audio = new SelectiveAudio(memory);
        Assert(audio.Apply(true));
        if (wanted.Any(address => !memory.AtVolume(address, 0)) || preserved.Any(address => !memory.AtVolume(address, 0.73f)))
            throw new Exception("Only allowlisted music, narration and countdown aliases should be silent.");
        if (memory.Inner.Current(AdvancedDvar.StreamEnabled) != 1)
            throw new Exception("Audio streaming must remain enabled.");
        Assert(audio.Apply(false));
        if (wanted.Concat(preserved).Any(address => !memory.AtVolume(address, 0.73f)))
            throw new Exception("Reset failed to restore the exact original audio volumes.");
    }

    internal static void RefusesAndRollsBack()
    {
        using var memory = new AudioMemory();
        var music = memory.Add("mp_defeat", 32, 2);
        var timer = memory.Add("ui_mp_timer_countdown", 10, 1);
        var audio = new SelectiveAudio(memory);
        memory.Inner.SetCurrent(AdvancedDvar.StreamEnabled, 0);
        if (audio.Apply(true).Success || memory.Inner.WriteCalls != 0)
            throw new Exception("The demo must not alter audio if the legacy mute is already active.");
        memory.Inner.SetCurrent(AdvancedDvar.StreamEnabled, 1);
        memory.Inner.Put32(music + 0x50, 11 << 9);
        if (audio.Apply(true).Success || memory.Inner.WriteCalls != 0)
            throw new Exception("The complete audio preflight must reject an unexpected channel.");
        memory.Inner.Put32(music + 0x50, 32 << 9);
        memory.Inner.WriteCounts[2] = 2;
        if (audio.Apply(true).Success || !memory.AtVolume(music, 0.73f) || !memory.AtVolume(timer, 0.73f))
            throw new Exception("A short volume write must roll back both the complete and partial writes.");
    }

    internal static void RefusesStaleRestore()
    {
        using var memory = new AudioMemory();
        var old = memory.Add("music_mainmenu_mp", 13, 2);
        var audio = new SelectiveAudio(memory);
        Assert(audio.Apply(true));
        var current = memory.Add("music_mainmenu_mp", 13, 2);
        int writes = memory.Inner.WriteCalls;
        Assert(audio.Apply(false));
        if (memory.Inner.WriteCalls != writes || !memory.AtVolume(current, 0.73f))
            throw new Exception("Reset must not apply an original volume to a replacement asset.");
    }

    private static void Assert(TransactionResult result)
    {
        if (!result.Success) throw new Exception(result.Message);
    }

    private sealed class AudioMemory : ISelectiveAudioMemory
    {
        internal FakeMemory Inner { get; } = FakeMemory.Create();
        private readonly HashSet<ulong> populatedBuckets = [];
        private ushort nextIndex = 1;

        public ProcessIdentity Identity => Inner.Identity;
        public bool ValidateIdentity() => Inner.ValidateIdentity();
        public MemoryRegion QueryRegion(ulong address)
        {
            var voices = Identity.ModuleBase + 0x654ad18;
            return address >= voices && address < voices + 0xa8 * 52
                ? new(voices, 0xa8 * 52, true, MemoryProtection.ReadWrite)
                : Inner.QueryRegion(address);
        }
        public byte[] ReadMemory(ulong address, int count)
        {
            if (address >= Identity.ModuleBase + 0x1658020 && address < Identity.ModuleBase + 0x166a130 && count == 2)
                return populatedBuckets.Contains(address) ? Inner.ReadMemory(address, count) : [0, 0];
            return Inner.ReadMemory(address, count);
        }
        public int WriteMemory(ulong address, byte[] data) => Inner.WriteMemory(address, data);
        public int WriteSoundVolume(SoundVolumeTarget target, uint expected, uint desired)
        {
            if (!SoundAliasBank.TryReadOwned(this, target, out var actual) || actual != expected)
                throw new IOException("Alias no longer matches its expected state.");
            return Inner.WriteMemory(target.Address, BitConverter.GetBytes(desired));
        }

        internal AudioMemory()
        {
            Inner.Put32(Identity.ModuleBase + 0x166a134, 0);
            Inner.Put(Voice(40), new byte[0xa8 * 12]);
        }

        internal ulong Add(string name, int channel, byte type, bool fourByteAligned = false)
        {
            var index = nextIndex++;
            var address = 0x211000UL + 0x200UL * index;
            var aliasList = address + 0x80;
            var row = address + (fourByteAligned ? 0xa4UL : 0xa0UL);
            var file = address + 0x130;
            var entry = Identity.ModuleBase + 0x166e160 + 24UL * index;
            var bucket = Bucket(name);
            var previous = ReadMemory(bucket, 2);
            Inner.Put(bucket, BitConverter.GetBytes(index));
            populatedBuckets.Add(bucket);
            Inner.Put(entry, new byte[24]);
            Inner.Put32(entry, 11);
            Inner.Put64(entry + 8, aliasList);
            Inner.Put(entry + 18, previous);
            Inner.Put(address, new byte[0x80]);
            Inner.Put(address, Encoding.ASCII.GetBytes(name + "\0"));
            Inner.Put64(aliasList, address);
            Inner.Put64(aliasList + 8, row);
            Inner.Put32(aliasList + 16, 1);
            Inner.Put(row, new byte[0x54]);
            Inner.Put64(row, address);
            Inner.Put64(row + 0x28, file);
            Inner.Put32(row + 0x34, BitConverter.SingleToInt32Bits(0.73f));
            Inner.Put32(row + 0x38, BitConverter.SingleToInt32Bits(0.73f));
            Inner.Put32(row + 0x50, channel << 9);
            Inner.Put(file, [type, 1]);
            return row;
        }

        internal void RemoveLookup(string name) => Inner.Put(Bucket(name), [0, 0]);

        internal ulong AddMenuVoice(int slot, ulong row, string fileName)
        {
            var address = Voice(slot);
            var driver = Identity.ModuleBase + 0x8bffc78 + 8UL * (ulong)slot;
            var file = BinaryPrimitives.ReadUInt64LittleEndian(Inner.Raw(row + 0x28, 8));
            var directoryName = 0x250000UL + 0x100UL * (ulong)slot;
            var trackName = directoryName + 0x30;
            Inner.Put(directoryName, Encoding.ASCII.GetBytes("music\0"));
            Inner.Put(trackName, Encoding.ASCII.GetBytes(fileName + "\0"));
            Inner.Put(file, new byte[24]);
            Inner.Put(file, [2, 1]);
            Inner.Put64(file + 8, directoryName);
            Inner.Put64(file + 16, trackName);
            Inner.Put32(address, 2);
            Inner.Put32(address + 0x10, 13);
            Inner.Put32(address + 0x60, BitConverter.SingleToInt32Bits(0.73f));
            Inner.Put64(address + 0x68, row);
            Inner.Put64(address + 0x70, row);
            Inner.Put64(driver, 0x260000UL + 0x100UL * (ulong)slot);
            return address + 0x60;
        }

        internal void ReplaceVoiceDriver(int slot) =>
            Inner.Put64(Identity.ModuleBase + 0x8bffc78 + 8UL * (ulong)slot, 0x270000);

        internal float Gain(ulong address) => BinaryPrimitives.ReadSingleLittleEndian(Inner.Raw(address, 4));

        private ulong Voice(int slot) => Identity.ModuleBase + 0x654ad18 + 0xa8UL * (ulong)slot;

        private ulong Bucket(string name)
        {
            uint hash = 11;
            foreach (char c in name) hash = unchecked(hash * 31 + char.ToLowerInvariant(c));
            return Identity.ModuleBase + 0x1658020 + 2 * (hash % 37000);
        }

        internal bool AtVolume(ulong row, float gain) =>
            BinaryPrimitives.ReadSingleLittleEndian(Inner.Raw(row + 0x34, 4)) == gain &&
            BinaryPrimitives.ReadSingleLittleEndian(Inner.Raw(row + 0x38, 4)) == gain;

        public void Dispose() => Inner.Dispose();
    }
}
