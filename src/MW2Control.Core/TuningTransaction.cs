using System.Buffers.Binary;

namespace MW2Control.Core;

/// <summary>
/// A transaction/ownership ledger for exactly one process lifetime and main module.
/// It never owns the accessor. Construction and inspection perform no writes.
/// </summary>
public sealed class TuningTransaction
{
    private static readonly TuningFeature[] Features = [TuningFeature.Fov, TuningFeature.MaxFps];
    private readonly IProcessMemory memory;
    private readonly ProcessIdentity identity;
    private readonly object gate = new();
    private readonly Dictionary<TuningFeature, Original> originals = [];
    private bool maintenanceSeeded;

    internal sealed class Original(ulong address, byte[] bytes)
    {
        public ulong Address { get; } = address;
        public byte[] Bytes { get; } = (byte[])bytes.Clone();
        public byte[]? Owned { get; set; }
        public byte[]? Target { get; set; }
        public byte[]? MaintenanceMetadata { get; init; }
    }

    internal sealed record Checkpoint(Dictionary<TuningFeature, Original> Originals,
        ValidatedFeature[] Values, bool MaintenanceSeeded);

    internal Checkpoint CaptureCheckpoint(TuningRequest request)
    {
        lock (gate)
        {
            var validation = TuningValidation.ValidateRequest(request);
            DvarValidator.Require(validation.Success, validation.Message);
            CheckSession();
            var values = ReadApplyValues();
            foreach (var value in values) ReadMaintenanceMetadata(value.Feature, value.Address);
            return new(CloneOriginals(), values, maintenanceSeeded);
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
                    var current = DvarValidator.Resolve(memory, before.Feature, requireWritable: true);
                    DvarValidator.Require(current.Address == before.Address, "Dvar mapping changed during settings rollback.");
                    ReadMaintenanceMetadata(before.Feature, current.Address);
                    if (current.RawValue.AsSpan().SequenceEqual(before.RawValue)) continue;
                    DvarValidator.Require(originals.TryGetValue(before.Feature, out var original) &&
                        original.Address == current.Address && original.Owned is not null &&
                        current.RawValue.AsSpan().SequenceEqual(original.Owned), "Settings rollback cannot identify the current tuning bytes.");
                    plans.Add(new(before.Feature, current.Address, current.RawValue, before.RawValue, Clone(original!.Owned)));
                }
                var result = Execute(plans, restoring: false, maintaining: true);
                if (!result.Success) { maintenanceSeeded = false; return result; }
                originals.Clear();
                foreach (var pair in checkpoint.Originals) originals.Add(pair.Key, pair.Value);
                maintenanceSeeded = checkpoint.MaintenanceSeeded;
                return new(true, "Previous tuning settings restored.");
            }
            catch (Exception ex) when (IsExpectedFailure(ex))
            {
                maintenanceSeeded = false;
                return new(false, "Tuning rollback paused: " + ex.Message);
            }
        }
    }

    private Dictionary<TuningFeature, Original> CloneOriginals() => originals.ToDictionary(pair => pair.Key,
        pair => new Original(pair.Value.Address, pair.Value.Bytes)
        {
            Owned = Clone(pair.Value.Owned), Target = Clone(pair.Value.Target),
            MaintenanceMetadata = Clone(pair.Value.MaintenanceMetadata)
        });

    private ValidatedFeature[] ReadApplyValues()
    {
        var current = Features.Select(f => DvarValidator.Resolve(memory, f, requireWritable: true)).ToArray();
        foreach (var value in current)
        {
            if (!originals.TryGetValue(value.Feature, out var original) || original.Owned is null) continue;
            DvarValidator.Require(original.Address == value.Address, "Dvar mapping changed since originals were captured.");
            DvarValidator.Require(value.RawValue.AsSpan().SequenceEqual(original.Owned) ||
                value.RawValue.AsSpan().SequenceEqual(original.Bytes),
                ConflictDiagnostics.Tuning(value.Feature, value.RawValue, original.Owned, "last owned", "Apply"));
        }
        return current;
    }

    private sealed record Plan(TuningFeature Feature, ulong Address, byte[] Before, byte[] Desired, byte[]? PreviousOwned);
    private sealed class Attempt(Plan plan)
    {
        public Plan Plan { get; } = plan;
        public byte[]? KnownWritten { get; set; }
        public bool Invoked { get; set; }
    }

    public TuningTransaction(IProcessMemory memory)
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
            return Features.Select(feature =>
            {
                var value = DvarValidator.Resolve(memory, feature);
                var owned = originals.TryGetValue(feature, out var original) && original.Owned is not null &&
                    original.Address == value.Address && value.RawValue.AsSpan().SequenceEqual(original.Owned);
                var drifted = original is not null && original.Address == value.Address &&
                    !value.RawValue.AsSpan().SequenceEqual(original.Owned ?? original.Bytes);
                return new FeatureReading(feature == TuningFeature.Fov ? "fov" : "fps",
                    feature == TuningFeature.Fov ? "Field of view" : "Maximum FPS", true, value.Value,
                    drifted ? ConflictDiagnostics.Tuning(feature, value.RawValue, original!.Owned ?? original.Bytes,
                        original.Owned is null ? "captured baseline" : "last owned", operation: null,
                        explicitRetryAllowed: original.Owned is not null && value.RawValue.AsSpan().SequenceEqual(original.Bytes)) :
                        owned ? "Observed value matches this session's last write." : "Observed from game memory; no ownership claimed.");
            }).ToArray();
        }
    }

    public TransactionResult Apply(TuningRequest request, bool privateSessionConfirmed) => Apply(request, privateSessionConfirmed, false);

    internal TransactionResult Apply(TuningRequest request, bool privateSessionConfirmed, bool validateMetadata)
    {
        lock (gate)
        {
            if (!privateSessionConfirmed) return new(false, "Confirm a private/offline session before applying. This is not an anti-cheat safety guarantee.");
            var validation = TuningValidation.ValidateRequest(request);
            if (!validation.Success) return new(false, validation.Message);
            try
            {
                CheckSession();
                // A no-op or restored value has no ownership to protect on the next Apply.
                foreach (var feature in originals.Where(pair => pair.Value.Owned is null).Select(pair => pair.Key).ToArray())
                    originals.Remove(feature);
                var current = ReadApplyValues();
                foreach (var value in current)
                    originals.TryAdd(value.Feature, new Original(value.Address, value.RawValue)
                    { MaintenanceMetadata = TryCaptureMaintenanceMetadata(value.Feature, value.Address) });
                var plans = current.Select(value => new Plan(value.Feature, value.Address, value.RawValue,
                    DvarValidator.Encode(value.Feature, request), Clone(originals[value.Feature].Owned))).ToArray();
                var result = Execute(plans, restoring: false, maintaining: validateMetadata);
                maintenanceSeeded = result.Success && HasMaintenanceTargets && originals.Values.Where(original => original.Target is not null)
                    .All(original => original.MaintenanceMetadata is not null);
                return result;
            }
            catch (Exception ex) when (IsExpectedFailure(ex))
            {
                maintenanceSeeded = false;
                return new(false, "Apply refused before writing: " + ex.Message);
            }
        }
    }

    public TransactionResult Restore(bool privateSessionConfirmed)
    {
        lock (gate)
        {
            if (!privateSessionConfirmed) return new(false, "Confirm a private/offline session before restoring. This is not an anti-cheat safety guarantee.");
            try
            {
                CheckSession();
                var plans = new List<Plan>();
                foreach (var feature in Features)
                {
                    if (!originals.TryGetValue(feature, out var original)) continue;
                    if (original.Owned is null) { originals.Remove(feature); continue; }
                    var value = DvarValidator.Resolve(memory, feature, requireWritable: true);
                    DvarValidator.Require(original.Address == value.Address, "Dvar mapping changed. Originals cannot be restored to a different address.");
                    DvarValidator.Require(value.RawValue.AsSpan().SequenceEqual(original.Owned) ||
                        value.RawValue.AsSpan().SequenceEqual(original.Bytes),
                        ConflictDiagnostics.Tuning(feature, value.RawValue, original.Owned, "last owned", "Restore"));
                    plans.Add(new(feature, value.Address, value.RawValue, original.Bytes, Clone(original.Owned)));
                }
                if (plans.Count == 0)
                {
                    maintenanceSeeded = false;
                    return new(true, "No values owned by this session need restoring.");
                }
                var result = Execute(plans, restoring: true);
                maintenanceSeeded = false;
                return result;
            }
            catch (Exception ex) when (IsExpectedFailure(ex))
            {
                maintenanceSeeded = false;
                return new(false, "Restore refused before writing: " + ex.Message);
            }
        }
    }

    /// <summary>One caller-scheduled opt-in tick; never captures originals or adopts new targets.</summary>
    public TransactionResult ReapplyOwned()
    {
        lock (gate)
        {
            if (!maintenanceSeeded) return new(false, "Keep applied needs a successful manual Apply with validated type/domain evidence first; no values were written.");
            try
            {
                CheckSession();
                var plans = new List<Plan>();
                foreach (var feature in Features)
                {
                    if (!originals.TryGetValue(feature, out var original) || original.Target is null) continue;
                    var current = DvarValidator.Resolve(memory, feature, requireWritable: true);
                    DvarValidator.Require(current.Address == original.Address, $"{feature}: Keep applied stopped because the dvar mapping changed.");
                    ValidateMaintenanceMetadata(feature, current.Address);
                    DvarValidator.Require(current.RawValue.AsSpan().SequenceEqual(original.Target) || IsRecognizedReset(feature, current.RawValue, original),
                        ConflictDiagnostics.Tuning(feature, current.RawValue, original.Target, "last applied", "Keep applied"));
                    plans.Add(new(feature, current.Address, current.RawValue, Clone(original.Target)!, Clone(original.Owned)));
                }
                if (plans.Count == 0)
                {
                    maintenanceSeeded = false;
                    return new(false, "No owned tuning targets remain; Keep applied stopped.");
                }
                var result = Execute(plans, restoring: false, maintaining: true);
                // A failed tick keeps the last successful targets for a guarded retry.
                return result with { Message = result.Success ? "Owned tuning targets checked/reapplied; captured originals are unchanged." : "Keep applied stopped. " + result.Message };
            }
            catch (Exception ex) when (IsExpectedFailure(ex))
            {
                return new(false, "Keep applied stopped before tuning writes: " + ex.Message);
            }
        }
    }

    private TransactionResult Execute(IReadOnlyList<Plan> plans, bool restoring, bool maintaining = false)
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
                    bytes => { attempt.KnownWritten = bytes; originals[plan.Feature].Owned = Clone(bytes); }, maintaining, allowBaselineReset: maintaining);
            }
            // Confirm both values once more, including any no-op feature, before success.
            foreach (var plan in plans) AssertCurrent(plan.Feature, plan.Address, plan.Desired, maintaining, allowBaselineReset: maintaining);
            foreach (var plan in plans)
            {
                var original = originals[plan.Feature];
                original.Target = restoring ? null : Clone(plan.Desired);
                original.Owned = restoring || original.Bytes.AsSpan().SequenceEqual(plan.Desired) ? null : Clone(plan.Desired);
                if (original.Owned is null && original.Target is null) originals.Remove(plan.Feature);
            }
            return new(true, restoring ? "Original FOV and FPS restored and verified." : "FOV and FPS applied and verified.");
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            var rollbackErrors = new List<string>();
            foreach (var attempt in attempts.AsEnumerable().Reverse())
            {
                if (!attempt.Invoked) continue;
                var plan = attempt.Plan;
                try
                {
                    var current = DvarValidator.Resolve(memory, plan.Feature, requireWritable: true);
                    DvarValidator.Require(current.Address == plan.Address, "Dvar address changed during rollback.");
                    if (current.RawValue.AsSpan().SequenceEqual(plan.Before))
                    {
                        originals[plan.Feature].Owned = Clone(plan.PreviousOwned);
                        continue;
                    }
                    // A failed native call can still have written the full value. A short write
                    // can own only the returned prefix. Any other bytes are never overwritten.
                    var owned = attempt.KnownWritten ?? plan.Desired;
                    DvarValidator.Require(current.RawValue.AsSpan().SequenceEqual(owned),
                        "Current bytes cannot be attributed to this operation; rollback skipped.");
                    originals[plan.Feature].Owned = Clone(owned);
                    WriteChecked(plan.Feature, plan.Address, owned, plan.Before, () => { },
                        bytes => originals[plan.Feature].Owned = Clone(bytes), maintaining);
                    originals[plan.Feature].Owned = Clone(plan.PreviousOwned);
                }
                catch (Exception rollbackEx) when (IsExpectedFailure(rollbackEx))
                {
                    rollbackErrors.Add($"{plan.Feature}: {rollbackEx.Message}");
                }
            }
            return new(false, (restoring ? "Restore" : "Apply") + " failed: " + ex.Message +
                (rollbackErrors.Count == 0 ? " Any changed values were rolled back with checked ownership." :
                    " Rollback incomplete; no unowned bytes were overwritten. " + string.Join(" ", rollbackErrors)));
        }
    }

    private void WriteChecked(TuningFeature feature, ulong address, byte[] before, byte[] desired,
        Action beforeInvoke, Action<byte[]> onKnownWrite, bool maintaining = false, bool allowBaselineReset = false)
    {
        AssertCurrent(feature, address, before, maintaining);
        // Re-check lifetime/mapping after pointer, name, protection and expected-value checks,
        // immediately before invoking the native write. External memory has no atomic CAS;
        // callers are told explicitly that a third-party writer can still race these checks.
        CheckSession();
        beforeInvoke();
        int count;
        try { count = memory.WriteMemory(address, (byte[])desired.Clone()); }
        catch (MemoryWriteException ex)
        {
            if (ex.BytesWritten > 0 && ex.BytesWritten <= desired.Length)
            {
                var partial = (byte[])before.Clone();
                desired.AsSpan(0, ex.BytesWritten).CopyTo(partial);
                onKnownWrite(partial);
            }
            throw;
        }
        DvarValidator.Require(count >= 0 && count <= desired.Length, "WriteProcessMemory returned an invalid byte count.");
        if (count > 0)
        {
            var written = (byte[])before.Clone();
            desired.AsSpan(0, count).CopyTo(written);
            onKnownWrite(written);
        }
        DvarValidator.Require(count == desired.Length, "WriteProcessMemory returned a short write.");
        AssertCurrent(feature, address, desired, maintaining, allowBaselineReset);
    }

    private void AssertCurrent(TuningFeature feature, ulong address, byte[] expected, bool maintaining = false, bool allowBaselineReset = false)
    {
        CheckSession();
        var value = DvarValidator.Resolve(memory, feature, requireWritable: true);
        DvarValidator.Require(value.Address == address, "Dvar pointer changed before write/readback.");
        if (maintaining) ValidateMaintenanceMetadata(feature, address);
        // The game can restore this exact baseline between the write and readback.
        // Keep the existing target so the next scheduled tick can retry.
        if (allowBaselineReset && IsRecognizedReset(feature, value.RawValue, originals[feature])) return;
        DvarValidator.Require(value.RawValue.AsSpan().SequenceEqual(expected),
            ConflictDiagnostics.Tuning(feature, value.RawValue, expected, "expected before write/readback", "Write/readback"));
    }

    private byte[]? TryCaptureMaintenanceMetadata(TuningFeature feature, ulong valueAddress)
    {
        try { return ReadMaintenanceMetadata(feature, valueAddress); }
        catch (Exception ex) when (IsExpectedFailure(ex)) { return null; }
    }

    private byte[] ReadMaintenanceMetadata(TuningFeature feature, ulong valueAddress)
    {
        var dvar = checked(valueAddress - KnownBuild.ValueOffset);
        var kind = DvarValidator.ReadExact(memory, checked(dvar + 0x0C), 1)[0];
        var domain = DvarValidator.ReadExact(memory, checked(dvar + 0x40), 8);
        // Confirmed from the approved executable's registration wrappers and call sites.
        // These are guards, not permission to modify or bypass the registered domains.
        var matches = feature == TuningFeature.Fov
            ? kind == 1 && BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(domain)) == 65f &&
                BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(domain.AsSpan(4))) == 80f
            : kind == 5 && BinaryPrimitives.ReadInt32LittleEndian(domain) == 0 &&
                BinaryPrimitives.ReadInt32LittleEndian(domain.AsSpan(4)) == 100;
        DvarValidator.Require(matches, $"{feature}: Keep applied type/domain does not match the approved registration evidence.");
        return new[] { kind }.Concat(domain).ToArray();
    }

    private void ValidateMaintenanceMetadata(TuningFeature feature, ulong address)
    {
        var original = originals[feature];
        DvarValidator.Require(original.MaintenanceMetadata is not null &&
            ReadMaintenanceMetadata(feature, address).AsSpan().SequenceEqual(original.MaintenanceMetadata),
            $"{feature}: Keep applied stopped because captured type/domain metadata changed.");
    }

    // Recognized reset values for the supported Steam build, checked only after
    // resolving the same dvar and validating its type, domain and writable data page.
    private static bool IsRecognizedReset(TuningFeature feature, byte[] bytes, Original original) =>
        bytes.AsSpan().SequenceEqual(original.Bytes) ||
        (feature == TuningFeature.Fov
            ? BitConverter.ToSingle(bytes) == 65f
            : BinaryPrimitives.ReadInt32LittleEndian(bytes) == 85);

    private void CheckSession()
    {
        DvarValidator.Require(memory.Identity == identity, "The transaction belongs to another process session.");
        var profile = TuningValidation.ValidateProfile(memory.Identity);
        DvarValidator.Require(profile.Success, profile.Message);
        DvarValidator.Require(memory.ValidateIdentity(), "Process lifetime or main module changed; operation refused.");
    }

    private static byte[]? Clone(byte[]? value) => value is null ? null : (byte[])value.Clone();
    internal static bool IsExpectedFailure(Exception ex) => ex is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;
}
