namespace MW2Control.Core;

public static class AdvancedValidation
{
    public static ValidationResult ValidateSettings(AdvancedSettings? settings) => settings is null
        ? new(false, "Advanced settings are required.")
        : settings.SpecularMode is < 0 or > 2
            ? new(false, "Specular mode must be 0 (Black), 1 (Unchanged), 2 (White), or Restore original.")
            : new(true, "Advanced settings are valid.");
}

/// <summary>
/// Independent, serialized optional-feature transaction ledger. False/null restores
/// only values previously written here. Missing unrequested features are not resolved.
/// Does not own or dispose its accessor, and never writes during inspection.
/// </summary>
public sealed class AdvancedTransaction
{
    private readonly IProcessMemory memory;
    private readonly ProcessIdentity identity;
    private readonly object gate = new();
    private readonly Dictionary<AdvancedDvar, Original> originals = [];
    private bool maintenanceSeeded;
    internal sealed class Original(ulong address, byte[] bytes)
    {
        public ulong Address { get; } = address;
        public byte[] Bytes { get; } = (byte[])bytes.Clone();
        public byte[]? Owned { get; set; }
        public byte[]? Target { get; set; }
    }

    internal sealed record Checkpoint(Dictionary<AdvancedDvar, Original> Originals,
        (AdvancedDvar Feature, ulong Address, byte[] RawValue)[] Values, bool MaintenanceSeeded);

    internal Checkpoint CaptureCheckpoint(AdvancedSettings settings)
    {
        lock (gate)
        {
            var validation = AdvancedValidation.ValidateSettings(settings);
            DvarValidator.Require(validation.Success, validation.Message);
            CheckSession();
            var prepared = Prepare(Requests(settings));
            var saved = originals.ToDictionary(pair => pair.Key, pair => new Original(pair.Value.Address, pair.Value.Bytes)
            { Owned = Clone(pair.Value.Owned), Target = Clone(pair.Value.Target) });
            var values = prepared.Plans.Select(plan => (plan.Feature, plan.Address, plan.Before)).ToArray();
            return new(saved, values, maintenanceSeeded);
        }
    }

    internal TransactionResult RollbackTo(Checkpoint checkpoint)
    {
        lock (gate)
        {
            try
            {
                CheckSession();
                var plans = new List<Plan>();
                foreach (var before in checkpoint.Values)
                {
                    var current = AdvancedDvarValidator.Resolve(memory, before.Feature, requireWritable: true);
                    DvarValidator.Require(current.Address == before.Address, "Dvar mapping changed during settings rollback.");
                    if (current.RawValue.AsSpan().SequenceEqual(before.RawValue)) continue;
                    DvarValidator.Require(originals.TryGetValue(before.Feature, out var original) &&
                        original.Address == current.Address && original.Owned is not null &&
                        current.RawValue.AsSpan().SequenceEqual(original.Owned), "Settings rollback cannot identify the current advanced bytes.");
                    plans.Add(new(before.Feature, current.Address, current.RawValue, before.RawValue, Clone(original!.Owned), false));
                }
                var result = Execute(plans);
                if (!result.Success) { maintenanceSeeded = false; return result; }
                originals.Clear();
                foreach (var pair in checkpoint.Originals) originals.Add(pair.Key, pair.Value);
                maintenanceSeeded = checkpoint.MaintenanceSeeded;
                return new(true, "Previous audio, visual and scoreboard settings restored.");
            }
            catch (Exception ex) when (TuningTransaction.IsExpectedFailure(ex))
            {
                maintenanceSeeded = false;
                return new(false, "Advanced rollback paused: " + ex.Message);
            }
        }
    }

    private static (AdvancedDvar Feature, int? Target)[] Requests(AdvancedSettings settings) =>
    [
        (AdvancedDvar.StreamEnabled, settings.MuteStreams ? 0 : null),
        (AdvancedDvar.Fullbright, settings.Fullbright ? 1 : null),
        (AdvancedDvar.Fog, settings.NoFog ? 0 : null),
        (AdvancedDvar.Glow, settings.NoGlow ? 0 : null),
        (AdvancedDvar.SpecularMap, settings.SpecularMode),
        (AdvancedDvar.Detail, settings.NoDetailOverlays ? 0 : null),
        (AdvancedDvar.DetailMap, settings.NoDetailOverlays ? 0 : null),
        (AdvancedDvar.PingText, settings.NumericPing ? 1 : null),
        (AdvancedDvar.PingGraph, settings.NumericPing ? 0 : null)
    ];
    private sealed record Plan(AdvancedDvar Feature, ulong Address, byte[] Before, byte[] Desired, byte[]? PreviousOwned, bool Restore);
    private sealed class Attempt(Plan plan)
    {
        public Plan Plan { get; } = plan;
        public bool Invoked { get; set; }
        public byte[]? KnownWritten { get; set; }
    }

    public AdvancedTransaction(IProcessMemory memory)
    {
        this.memory = memory ?? throw new ArgumentNullException(nameof(memory));
        identity = memory.Identity;
    }

    internal bool HasMaintenanceTargets
    {
        get { lock (gate) return originals.Values.Any(value => value.Target is not null); }
    }

    internal bool CanMaintainOwned
    {
        get { lock (gate) return maintenanceSeeded; }
    }

    public IReadOnlyList<FeatureReading> InspectFeatures()
    {
        lock (gate)
        {
            CheckSession();
            return
            [
                ReadFeature("music", "Mute streamed sounds", [AdvancedDvar.StreamEnabled], values => values[0].Value == 0 ? 1 : 0,
                    "Disables streamed audio. In a live multiplayer test, footsteps, gunfire, explosions, and the bomb timer remained audible."),
                ReadFeature("fullbright", "Fullbright", [AdvancedDvar.Fullbright], values => values[0].Value,
                    "Changes the validated fullbright data value; visual effect may require a map/session transition."),
                ReadFeature("fog", "No fog", [AdvancedDvar.Fog], values => values[0].Value == 0 ? 1 : 0,
                    "Disables the validated fog toggle only."),
                ReadFeature("glow", "No glow", [AdvancedDvar.Glow], values => values[0].Value == 0 ? 1 : 0,
                    "Disables r_glow only; no glow tweak gates or executable patches are changed."),
                ReadFeature("specular", "Specular map mode", [AdvancedDvar.SpecularMap], values => values[0].Value,
                    "0 Black / 1 Unchanged / 2 White. Existing 3 Gray can be restored, but is not a requested mode."),
                ReadFeature("detail", "Hide detail overlays (experimental)", [AdvancedDvar.Detail, AdvancedDvar.DetailMap],
                    values => values[0].Value == 0 && values[1].Value == 0 ? 1 : 0,
                    "Compound r_detail=0 and r_detailMap=Hidden. This is not guaranteed No Camos."),
                ReadFeature("ping", "Numeric scoreboard ping", [AdvancedDvar.PingText, AdvancedDvar.PingGraph],
                    values => values[0].Value == 1 && values[1].Value == 0 ? 1 : 0,
                    "Shows the game's numeric ping and hides its scoreboard ping bars.")
            ];
        }
    }

    private FeatureReading ReadFeature(string id, string label, AdvancedDvar[] features,
        Func<ValidatedAdvancedDvar[], double> observed, string scope)
    {
        try
        {
            var values = features.Select(feature => AdvancedDvarValidator.Resolve(memory, feature)).ToArray();
            var raw = string.Join(", ", values.Select(value => $"{value.Feature}={value.Value}"));
            var owned = values.Any(value => originals.TryGetValue(value.Feature, out var original) &&
                original.Address == value.Address && original.Owned is not null && value.RawValue.AsSpan().SequenceEqual(original.Owned));
            var drift = values.Select(value =>
            {
                if (!originals.TryGetValue(value.Feature, out var original) || original.Address != value.Address ||
                    value.RawValue.AsSpan().SequenceEqual(original.Owned ?? original.Bytes)) return null;
                return ConflictDiagnostics.Advanced(value.Feature, value.RawValue, original.Owned ?? original.Bytes,
                    original.Owned is null ? "captured baseline" : "last owned", operation: null,
                    explicitRetryAllowed: original.Owned is not null && value.RawValue.AsSpan().SequenceEqual(original.Bytes));
            }).Where(detail => detail is not null).ToArray();
            return new(id, label, true, observed(values), scope + " Raw observed: " + raw +
                (drift.Length > 0 ? ". " + string.Join(" ", drift) : owned ? ". Matches session-owned bytes." : ". No ownership claimed."));
        }
        catch (Exception ex) when (TuningTransaction.IsExpectedFailure(ex))
        {
            return new(id, label, false, null, scope + " Unavailable: " + ex.Message);
        }
    }

    public TransactionResult Apply(AdvancedSettings settings, bool privateSessionConfirmed)
    {
        lock (gate)
        {
            if (!privateSessionConfirmed) return new(false, "Confirm a private/offline session before advanced changes. No writes performed; anti-cheat safety is not guaranteed.");
            var validation = AdvancedValidation.ValidateSettings(settings);
            if (!validation.Success) return new(false, validation.Message);
            try
            {
                CheckSession();
                var result = PrepareAndExecute(Requests(settings));
                maintenanceSeeded = result.Success && HasMaintenanceTargets;
                return result;
            }
            catch (Exception ex) when (TuningTransaction.IsExpectedFailure(ex))
            {
                maintenanceSeeded = false;
                return new(false, "Advanced changes refused before writing: " + ex.Message);
            }
        }
    }

    public TransactionResult Restore(bool privateSessionConfirmed)
    {
        lock (gate)
        {
            if (!privateSessionConfirmed) return new(false, "Confirm a private/offline session before restoring advanced changes. No writes performed.");
            try
            {
                CheckSession();
                var result = PrepareAndExecute(Enum.GetValues<AdvancedDvar>().Select(feature => (feature, (int?)null)));
                maintenanceSeeded = false;
                return result;
            }
            catch (Exception ex) when (TuningTransaction.IsExpectedFailure(ex))
            {
                maintenanceSeeded = false;
                return new(false, "Advanced restore refused before writing: " + ex.Message);
            }
        }
    }

    /// <summary>One caller-scheduled opt-in tick for successfully applied owned targets only.</summary>
    public TransactionResult ReapplyOwned()
    {
        lock (gate)
        {
            if (!maintenanceSeeded) return new(false, "Keep applied needs a successful manual advanced Apply first; no values were written.");
            try
            {
                CheckSession();
                var plans = new List<Plan>();
                foreach (var feature in Enum.GetValues<AdvancedDvar>())
                {
                    if (!originals.TryGetValue(feature, out var original) || original.Target is null) continue;
                    var current = AdvancedDvarValidator.Resolve(memory, feature, requireWritable: true);
                    DvarValidator.Require(current.Address == original.Address, $"{feature}: Keep applied stopped because the dvar mapping changed.");
                    DvarValidator.Require(current.RawValue.AsSpan().SequenceEqual(original.Target) || IsRecognizedReset(feature, current.RawValue, original),
                        ConflictDiagnostics.Advanced(feature, current.RawValue, original.Target, "last applied", "Keep applied"));
                    plans.Add(new(feature, current.Address, current.RawValue, Clone(original.Target)!, Clone(original.Owned), Restore: false));
                }
                if (plans.Count == 0)
                {
                    maintenanceSeeded = false;
                    return new(false, "No owned advanced targets remain; Keep applied stopped.");
                }
                var result = Execute(plans, maintaining: true);
                // Keep successful targets while a transition temporarily prevents a tick.
                return result with { Message = result.Success ? "Owned advanced targets checked/reapplied; captured originals are unchanged." : "Keep applied stopped. " + result.Message };
            }
            catch (Exception ex) when (TuningTransaction.IsExpectedFailure(ex))
            {
                return new(false, "Keep applied stopped before advanced writes: " + ex.Message);
            }
        }
    }

    private TransactionResult PrepareAndExecute(IEnumerable<(AdvancedDvar Feature, int? Target)> requests)
    {
        var prepared = Prepare(requests);
        foreach (var feature in prepared.Forget) originals.Remove(feature);
        foreach (var pair in prepared.Captured) originals[pair.Key] = pair.Value;
        return prepared.Plans.Count == 0 ? new(true, "No advanced values need changing or restoring.") : Execute(prepared.Plans);
    }

    private (List<Plan> Plans, Dictionary<AdvancedDvar, Original> Captured, List<AdvancedDvar> Forget)
        Prepare(IEnumerable<(AdvancedDvar Feature, int? Target)> requests)
    {
        var plans = new List<Plan>();
        var captured = new Dictionary<AdvancedDvar, Original>();
        var forget = new List<AdvancedDvar>();
        foreach (var (feature, target) in requests)
        {
            originals.TryGetValue(feature, out var original);
            // False/null is a restore, never a guessed enable/default write. Missing
            // unsupported features with no owned writes are deliberately not resolved.
            if (target is null && original?.Owned is null) { forget.Add(feature); continue; }
            var value = AdvancedDvarValidator.Resolve(memory, feature, requireWritable: true);
            if (original?.Owned is not null)
            {
                DvarValidator.Require(original.Address == value.Address, $"{feature}: dvar mapping changed; originals cannot move to a new address.");
                // Only an explicit requested target may retry exact captured originals.
                // An exact original is also safe for Restore. Execute skips its write
                // and clears ownership only after the whole transaction succeeds.
                DvarValidator.Require(value.RawValue.AsSpan().SequenceEqual(original.Owned ?? original.Bytes) ||
                    value.RawValue.AsSpan().SequenceEqual(original.Bytes),
                    ConflictDiagnostics.Advanced(feature, value.RawValue, original.Owned ?? original.Bytes,
                        original.Owned is null ? "captured baseline" : "last owned", target is null ? "Restore" : "Apply"));
            }
            else
            {
                original = new(value.Address, value.RawValue);
                captured.Add(feature, original);
            }
            var desired = target is null ? Clone(original.Bytes)! : AdvancedDvarValidator.Encode(feature, target.Value);
            plans.Add(new(feature, value.Address, value.RawValue, desired, Clone(original.Owned), target is null));
        }
        // Nothing is captured and no write starts until every requested feature,
        // including both halves of the detail control, passes the entire preflight.
        return (plans, captured, forget);
    }

    private TransactionResult Execute(IReadOnlyList<Plan> plans, bool maintaining = false)
    {
        var attempts = new List<Attempt>();
        try
        {
            foreach (var plan in plans)
            {
                if (plan.Before.AsSpan().SequenceEqual(plan.Desired)) continue;
                var attempt = new Attempt(plan);
                attempts.Add(attempt);
                WriteChecked(plan.Feature, plan.Address, plan.Before, plan.Desired,
                    () => attempt.Invoked = true,
                    bytes => { attempt.KnownWritten = Clone(bytes); originals[plan.Feature].Owned = Clone(bytes); }, allowBaselineReset: maintaining);
            }
            foreach (var plan in plans) AssertCurrent(plan.Feature, plan.Address, plan.Desired, allowBaselineReset: maintaining);
            foreach (var plan in plans)
            {
                var original = originals[plan.Feature];
                original.Target = plan.Restore ? null : Clone(plan.Desired);
                original.Owned = plan.Restore || original.Bytes.AsSpan().SequenceEqual(plan.Desired)
                    ? null : Clone(plan.Desired);
                if (original.Owned is null && original.Target is null) originals.Remove(plan.Feature);
            }
            return new(true, "Audio, visual and scoreboard settings updated and verified.");
        }
        catch (Exception ex) when (TuningTransaction.IsExpectedFailure(ex))
        {
            var errors = new List<string>();
            foreach (var attempt in attempts.AsEnumerable().Reverse())
            {
                if (!attempt.Invoked) continue;
                var plan = attempt.Plan;
                try
                {
                    CheckSession();
                    var current = AdvancedDvarValidator.Resolve(memory, plan.Feature, requireWritable: true);
                    DvarValidator.Require(current.Address == plan.Address, "Address changed during rollback.");
                    if (current.RawValue.AsSpan().SequenceEqual(plan.Before))
                    {
                        originals[plan.Feature].Owned = Clone(plan.PreviousOwned);
                        continue;
                    }
                    var owned = attempt.KnownWritten ?? plan.Desired;
                    DvarValidator.Require(current.RawValue.AsSpan().SequenceEqual(owned),
                        "Current bytes cannot be attributed to this operation; rollback skipped.");
                    originals[plan.Feature].Owned = Clone(owned);
                    WriteChecked(plan.Feature, plan.Address, owned, plan.Before, () => { },
                        bytes => originals[plan.Feature].Owned = Clone(bytes));
                    originals[plan.Feature].Owned = Clone(plan.PreviousOwned);
                }
                catch (Exception rollbackEx) when (TuningTransaction.IsExpectedFailure(rollbackEx))
                { errors.Add($"{plan.Feature}: {rollbackEx.Message}"); }
            }
            return new(false, "Advanced transaction failed: " + ex.Message + (errors.Count == 0
                ? " Any changed values were rolled back with checked ownership."
                : " Rollback incomplete; unowned bytes were not overwritten. " + string.Join(" ", errors)));
        }
    }

    private void WriteChecked(AdvancedDvar feature, ulong address, byte[] before, byte[] desired,
        Action beforeInvoke, Action<byte[]> onKnownWrite, bool allowBaselineReset = false)
    {
        AssertCurrent(feature, address, before);
        CheckSession();
        beforeInvoke();
        int count;
        try { count = memory.WriteMemory(address, Clone(desired)!); }
        catch (MemoryWriteException ex)
        {
            if (ex.BytesWritten > 0 && ex.BytesWritten <= desired.Length) onKnownWrite(Partial(before, desired, ex.BytesWritten));
            throw;
        }
        DvarValidator.Require(count >= 0 && count <= desired.Length, "WriteProcessMemory returned an invalid byte count.");
        if (count > 0) onKnownWrite(Partial(before, desired, count));
        DvarValidator.Require(count == desired.Length, "WriteProcessMemory returned a short write.");
        AssertCurrent(feature, address, desired, allowBaselineReset);
    }

    private void AssertCurrent(AdvancedDvar feature, ulong address, byte[] expected, bool allowBaselineReset = false)
    {
        CheckSession();
        var current = AdvancedDvarValidator.Resolve(memory, feature, requireWritable: true);
        DvarValidator.Require(current.Address == address, "Dvar pointer changed before write/readback.");
        if (allowBaselineReset && IsRecognizedReset(feature, current.RawValue, originals[feature])) return;
        DvarValidator.Require(current.RawValue.AsSpan().SequenceEqual(expected),
            ConflictDiagnostics.Advanced(feature, current.RawValue, expected, "expected before write/readback", "Write/readback"));
    }

    // Recognized reset values for the supported Steam build, checked only after
    // resolving the same dvar and validating its type, domain and writable data page.
    private static bool IsRecognizedReset(AdvancedDvar feature, byte[] bytes, Original original) =>
        bytes.AsSpan().SequenceEqual(original.Bytes) ||
        bytes.AsSpan().SequenceEqual(AdvancedDvarValidator.Encode(feature,
            feature is AdvancedDvar.Fullbright or AdvancedDvar.PingText ? 0 : 1));

    private void CheckSession()
    {
        DvarValidator.Require(memory.Identity == identity, "The advanced transaction belongs to another process session.");
        var profile = TuningValidation.ValidateProfile(memory.Identity);
        DvarValidator.Require(profile.Success, profile.Message);
        DvarValidator.Require(memory.ValidateIdentity(), "Process lifetime or main module changed; advanced operation refused.");
    }

    private static byte[] Partial(byte[] before, byte[] desired, int count)
    {
        var bytes = (byte[])before.Clone();
        desired.AsSpan(0, count).CopyTo(bytes);
        return bytes;
    }
    private static byte[]? Clone(byte[]? bytes) => bytes is null ? null : (byte[])bytes.Clone();
}
