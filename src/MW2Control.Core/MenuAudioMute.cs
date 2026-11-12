namespace MW2Control.Core;

/// <summary>Lobby assets and playback can be replaced during normal game transitions.</summary>
internal sealed class MenuAudioMute(ISelectiveAudioMemory memory)
{
    private sealed record OwnedVolume(SoundVolumeTarget Target, uint Written);
    private readonly Dictionary<ulong, OwnedVolume> owned = [];

    internal int ChangedCount => owned.Count;
    internal string Status { get; private set; } = "Lobby music is checked after Apply.";

    internal void Update(bool mute)
    {
        var issues = new List<string>();
        try
        {
            SoundAliasBank.CheckSession(memory);
            if (mute)
            {
                foreach (var previous in owned.Values.ToArray())
                {
                    if (!SoundAliasBank.TryReadOwned(memory, previous.Target, out _))
                        owned.Remove(previous.Target.Address);
                }
                foreach (var spec in SoundAliasCatalog.Targets.Where(MenuPlayback.IsMenuAlias))
                {
                    try
                    {
                        foreach (var target in SoundAliasBank.Resolve(memory, spec))
                            UpdateTarget(target, mute, issues);
                    }
                    catch (Exception ex) when (TuningTransaction.IsExpectedFailure(ex))
                    {
                        issues.Add(ex.Message);
                    }
                }
                foreach (var target in MenuPlayback.Find(memory))
                    UpdateTarget(target, mute, issues);
            }
            else
            {
                foreach (var previous in owned.Values.Reverse().ToArray())
                    UpdateTarget(previous.Target, mute, issues);
            }
        }
        catch (Exception ex) when (TuningTransaction.IsExpectedFailure(ex))
        {
            issues.Add(ex.Message);
        }

        Status = issues.Count > 0
            ? (mute ? "Lobby music is changing. Retrying. " : "Lobby sound restoration needs another check. ") + issues[0]
            : mute
                ? owned.Count > 0 ? "Lobby music muted." : "Waiting for lobby music."
                : "Lobby sound volumes restored.";
    }

    private void UpdateTarget(SoundVolumeTarget candidate, bool mute, List<string> issues)
    {
        try
        {
            if (owned.TryGetValue(candidate.Address, out var previous) &&
                previous.Target == (candidate with { Original = previous.Target.Original }))
                candidate = previous.Target;
            else
            {
                owned.Remove(candidate.Address);
                previous = null;
            }

            if (!SoundAliasBank.TryReadOwned(memory, candidate, out var before) ||
                !mute && (previous is null || before != previous.Written))
            {
                // An expired voice or a newer value belongs to the game.
                owned.Remove(candidate.Address);
                return;
            }

            var desired = mute ? 0u : candidate.Original;
            if (before == desired)
            {
                if (!mute) owned.Remove(candidate.Address);
                return;
            }
            DvarValidator.Require(SoundAliasBank.IsGain(before), "Lobby music has an unexpected volume.");

            int bytes;
            try { bytes = memory.WriteSoundVolume(candidate, before, desired); }
            catch (MemoryWriteException ex)
            {
                RememberWrite(candidate, before, desired, ex.BytesWritten);
                throw;
            }

            RememberWrite(candidate, before, desired, bytes);
            DvarValidator.Require(bytes == 4, "Short lobby volume write.");
            if (!SoundAliasBank.TryReadOwned(memory, candidate, out var actual))
            {
                owned.Remove(candidate.Address);
                issues.Add($"{candidate.Spec.Name} was replaced.");
                return;
            }
            if (actual != desired)
            {
                issues.Add($"{candidate.Spec.Name} changed during volume verification.");
                return;
            }
            if (!mute) owned.Remove(candidate.Address);
        }
        catch (Exception ex) when (TuningTransaction.IsExpectedFailure(ex))
        {
            issues.Add(ex.Message);
        }
    }

    private void RememberWrite(SoundVolumeTarget target, uint before, uint after, int bytes)
    {
        if (bytes is <= 0 or > 4) return;
        var mask = uint.MaxValue >> ((4 - bytes) * 8);
        var written = (before & ~mask) | (after & mask);
        owned[target.Address] = new(target, written);
    }
}
