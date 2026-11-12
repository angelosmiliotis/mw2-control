using System.Diagnostics;

namespace MW2Control.Core;

/// <summary>Serialized facade. Inspect and Dispose never write; disposal does not implicitly restore.</summary>
public sealed class ControlEngine : IDisposable
{
    private readonly IProcessSource source;
    private readonly bool useSelectiveAudio;
    private readonly object gate = new();
    private IProcessMemory? memory;
    private TuningTransaction? transaction;
    private AdvancedTransaction? advancedTransaction;
    private SelectiveAudio? selectiveAudio;
    private long lastMenuCheck;
    private ProcessIdentity? lastWriteSession;
    private ProcessIdentity? lastAdvancedWriteSession;
    private bool maintenanceFaulted;
    private bool disposed;

    public ControlEngine() : this(new WindowsProcessSource(), useSelectiveAudio: true) { }
    public ControlEngine(IProcessSource source) : this(source, useSelectiveAudio: false) { }
    private ControlEngine(IProcessSource source, bool useSelectiveAudio)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.useSelectiveAudio = useSelectiveAudio;
    }

    public EngineSnapshot Inspect()
    {
        lock (gate)
        {
            if (disposed) return Empty(EngineState.Error, "The engine is disposed.");
            try
            {
                if (memory is not null && memory.ValidateIdentity())
                    return TuningValidation.ValidateProfile(memory.Identity).Success ? Snapshot() :
                        ForIdentity(EngineState.UnsupportedBuild, "This game build is not supported.", UnsupportedFeatures("Unsupported build."));
                var unavailable = Acquire();
                return unavailable ?? Snapshot();
            }
            catch (Exception ex) when (TuningTransaction.IsExpectedFailure(ex)) { return Failure(ex); }
        }
    }

    public OperationResult Apply(TuningRequest request, bool privateSessionConfirmed)
    {
        lock (gate)
        {
            if (!privateSessionConfirmed) return new(false, "Confirm a private/offline session before applying. No writes performed.", Inspect());
            var validation = TuningValidation.ValidateRequest(request);
            if (!validation.Success) return new(false, validation.Message, Inspect());
            return Operate(() => transaction!.Apply(request, privateSessionConfirmed));
        }
    }

    public OperationResult Restore(bool privateSessionConfirmed)
    {
        lock (gate)
        {
            if (!privateSessionConfirmed) return new(false, "Confirm a private/offline session before restoring. No writes performed.", Inspect());
            return Operate(() => transaction!.Restore(privateSessionConfirmed), restoring: true);
        }
    }

    public OperationResult ApplyAdvanced(AdvancedSettings settings, bool privateSessionConfirmed)
    {
        lock (gate)
        {
            if (!privateSessionConfirmed) return new(false, "Confirm a private/offline session before advanced changes. No writes performed.", Inspect());
            if (useSelectiveAudio && settings is { MuteStreams: true })
                return new(false, "Use Apply settings for selective mute.", Inspect());
            var validation = AdvancedValidation.ValidateSettings(settings);
            if (!validation.Success) return new(false, validation.Message, Inspect());
            return Operate(() => advancedTransaction!.Apply(settings, privateSessionConfirmed), advanced: true);
        }
    }

    public OperationResult RestoreAdvanced(bool privateSessionConfirmed)
    {
        lock (gate)
        {
            if (!privateSessionConfirmed) return new(false, "Confirm a private/offline session before restoring advanced changes. No writes performed.", Inspect());
            return Operate(() => advancedTransaction!.Restore(privateSessionConfirmed), restoring: true, advanced: true);
        }
    }

    /// <summary>Applies tuning, then advanced settings, using one acquired process session.</summary>
    public OperationResult ApplySettings(TuningRequest tuning, AdvancedSettings advanced)
    {
        lock (gate)
        {
            var validation = TuningValidation.ValidateRequest(tuning);
            if (validation.Success) validation = AdvancedValidation.ValidateSettings(advanced);
            if (!validation.Success) return new(false, validation.Message, Inspect());
            var mute = useSelectiveAudio && advanced.MuteStreams;
            if (useSelectiveAudio) advanced = advanced with { MuteStreams = false };

            return WithSession(() =>
            {
                TuningTransaction.Checkpoint tuningBefore;
                AdvancedTransaction.Checkpoint advancedBefore;
                try
                {
                    tuningBefore = transaction!.CaptureCheckpoint(tuning);
                    advancedBefore = advancedTransaction!.CaptureCheckpoint(advanced);
                }
                catch (Exception ex) when (TuningTransaction.IsExpectedFailure(ex))
                {
                    return new(false, "Settings were not applied. " + ex.Message);
                }

                var previousFault = maintenanceFaulted;
                var tuningResult = OperateHeld(() => transaction!.Apply(tuning, true, validateMetadata: true), restoring: false, advanced: false);
                var advancedResult = tuningResult.Success
                    ? OperateHeld(() => advancedTransaction!.Apply(advanced, true), restoring: false, advanced: true)
                    : new TransactionResult(false, "Advanced settings were not changed.");
                var audioResult = tuningResult.Success && advancedResult.Success && useSelectiveAudio
                    ? selectiveAudio?.Apply(mute) ?? new TransactionResult(!mute, mute ? "Selective audio is unavailable." : "")
                    : new TransactionResult(true, "");
                if (tuningResult.Success && advancedResult.Success && audioResult.Success)
                    return new(true, ("Applied. " + tuningResult.Message + " " + advancedResult.Message + " " + audioResult.Message).TrimEnd());

                var advancedRollback = advancedTransaction!.RollbackTo(advancedBefore);
                var tuningRollback = transaction!.RollbackTo(tuningBefore);
                maintenanceFaulted = previousFault || !advancedRollback.Success || !tuningRollback.Success;
                lastWriteSession = transaction.HasMaintenanceTargets ? memory!.Identity : null;
                lastAdvancedWriteSession = advancedTransaction.HasMaintenanceTargets ? memory!.Identity : null;
                var rollback = advancedRollback.Success && tuningRollback.Success
                    ? "Previous settings were restored."
                    : "Rollback needs attention. " + tuningRollback.Message + " " + advancedRollback.Message;
                var summary = advancedRollback.Success && tuningRollback.Success
                    ? "Settings were not applied. " : "Settings were only partly applied. ";
                var detail = audioResult.Success
                    ? tuningResult.Message + " " + advancedResult.Message
                    : audioResult.Message;
                return new(false, summary + detail + " " + rollback);
            }, preserveMaintenance: true);
        }
    }

    /// <summary>Restores both owned groups using one acquired process session.</summary>
    public OperationResult ResetSettings()
    {
        lock (gate)
        {
            return WithSession(() =>
            {
                var tuning = OperateHeld(() => transaction!.Restore(true), restoring: true, advanced: false);
                var advanced = OperateHeld(() => advancedTransaction!.Restore(true), restoring: true, advanced: true);
                var audio = selectiveAudio?.Apply(false) ?? new TransactionResult(true, "");
                var summary = tuning.Success && advanced.Success && audio.Success ? "Reset complete."
                    : tuning.Success || advanced.Success ? "Reset partially completed." : "Reset failed.";
                return new(tuning.Success && advanced.Success && audio.Success,
                    $"{summary} Tuning ({(tuning.Success ? "OK" : "failed")}): {tuning.Message} " +
                    $"Audio, visuals and HUD ({(advanced.Success && audio.Success ? "OK" : "failed")}): {advanced.Message} {audio.Message}".TrimEnd());
            });
        }
    }

    /// <summary>
    /// Read-only target comparison over the existing handle. False requests a full
    /// validated tick; true never grants permission to write or extends a session.
    /// </summary>
    public bool OwnedValuesMatchReadOnly()
    {
        lock (gate)
        {
            if (disposed || maintenanceFaulted || memory is null || transaction is null || advancedTransaction is null || !HasMaintainableTargets()) return false;
            return transaction.MatchesTargetsReadOnly() && advancedTransaction.MatchesTargetsReadOnly();
        }
    }

    /// <summary>Fully validated maintenance of the current session's applied targets.</summary>
    public OperationResult MaintainOwned(bool privateSessionConfirmed)
    {
        lock (gate)
        {
            if (disposed) return new(false, "Keep applied stopped: the engine is disposed.", Empty(EngineState.Error, "The engine is disposed."));
            if (!privateSessionConfirmed)
            {
                maintenanceFaulted = true;
                return MaintenanceResult(false, "Keep applied stopped: private/offline confirmation is required. No writes performed.");
            }
            if (maintenanceFaulted) return MaintenanceResult(false, "Keep applied remains stopped after a guard/error. Manually Apply again before enabling it.");
            if (memory is null || transaction is null || advancedTransaction is null)
                return MaintenanceResult(false, "Keep applied needs an existing inspected session and a successful manual Apply first. No target was opened.");
            var tuningOwned = transaction.HasMaintenanceTargets;
            var advancedOwned = advancedTransaction.HasMaintenanceTargets;
            if (!tuningOwned && !advancedOwned)
                return MaintenanceResult(false, "Keep applied has no owned targets. Apply manually first; no writes performed.");
            try
            {
                DvarValidator.Require(memory.ValidateIdentity(), "Process lifetime or main module changed.");
                DvarValidator.Require(!tuningOwned || lastWriteSession == memory.Identity && transaction.CanMaintainOwned,
                    "Tuning targets are not from a successful manual Apply in this session.");
                DvarValidator.Require(!advancedOwned || lastAdvancedWriteSession == memory.Identity && advancedTransaction.CanMaintainOwned,
                    "Advanced targets are not from a successful manual Apply in this session.");
                if (tuningOwned)
                {
                    var result = transaction.ReapplyOwned();
                    if (!result.Success)
                    {
                        return MaintenanceResult(false, result.Message + " Advanced targets were not written. Keep applied groups are not a cross-group atomic transaction.");
                    }
                }
                if (advancedOwned)
                {
                    var result = advancedTransaction.ReapplyOwned();
                    if (!result.Success)
                    {
                        return MaintenanceResult(false, result.Message +
                            (tuningOwned ? " Tuning may already have been reapplied; groups are not a cross-group atomic transaction." : ""));
                    }
                }
                if (selectiveAudio is { MenuNeedsCheck: true } &&
                    Stopwatch.GetElapsedTime(lastMenuCheck) >= TimeSpan.FromMilliseconds(500))
                {
                    lastMenuCheck = Stopwatch.GetTimestamp();
                    selectiveAudio.MaintainMenuMute();
                }
                return MaintenanceResult(true, "Owned targets checked/reapplied in the held session. No baseline was recaptured. Keep applied does not guarantee rendered/audio effects or persistence between ticks.");
            }
            catch (Exception ex) when (TuningTransaction.IsExpectedFailure(ex))
            {
                maintenanceFaulted = true;
                return MaintenanceResult(false, "Keep applied stopped: " + ex.Message);
            }
        }
    }

    private OperationResult MaintenanceResult(bool success, string message)
    {
        EngineSnapshot snapshot;
        try { snapshot = memory is null ? Empty(EngineState.Error, message) : Snapshot(); }
        catch (Exception ex) when (TuningTransaction.IsExpectedFailure(ex))
        {
            return new(false, message + " Read-only verification is temporarily unavailable: " + ex.Message, Failure(ex))
            {
                CanMaintainOwned = !maintenanceFaulted && memory is not null &&
                    transaction is not null && advancedTransaction is not null && HasMaintainableTargets()
            };
        }
        return new(success, message, snapshot)
        {
            CanMaintainOwned = !maintenanceFaulted && memory is not null &&
                transaction is not null && advancedTransaction is not null && HasMaintainableTargets()
        };
    }

    /// <summary>Explicit read-only reset: forget every restore ledger, release handles, and reinspect.</summary>
    public EngineSnapshot ResetSession()
    {
        lock (gate)
        {
            if (disposed) return Empty(EngineState.Error, "The engine is disposed.");
            lastWriteSession = null;
            lastAdvancedWriteSession = null;
            try
            {
                Release();
                var snapshot = Inspect();
                return snapshot with { Detail = "Restore history forgotten without game writes. " + snapshot.Detail };
            }
            catch (Exception ex) when (TuningTransaction.IsExpectedFailure(ex)) { return Failure(ex); }
        }
    }

    private OperationResult Operate(Func<TransactionResult> operation, bool restoring = false, bool advanced = false) =>
        WithSession(() => OperateHeld(operation, restoring, advanced));

    private TransactionResult OperateHeld(Func<TransactionResult> operation, bool restoring, bool advanced)
    {
        var previousSession = advanced ? lastAdvancedWriteSession : lastWriteSession;
        if (restoring && previousSession is not null && previousSession != memory!.Identity)
            return new(false, "Restore refused: the values belong to a previous process lifetime or module. No writes performed.");
        var result = operation();
        if (result.Success && !restoring) maintenanceFaulted = false;
        if (advanced) lastAdvancedWriteSession = advancedTransaction!.HasMaintenanceTargets ? memory!.Identity : null;
        else lastWriteSession = transaction!.HasMaintenanceTargets ? memory!.Identity : null;
        return result;
    }

    private OperationResult WithSession(Func<TransactionResult> operation, bool preserveMaintenance = false)
    {
        if (disposed) return new(false, "The engine is disposed.", Empty(EngineState.Error, "The engine is disposed."));
        try
        {
            var unavailable = Acquire();
            if (unavailable is not null) return new(false, unavailable.Detail, unavailable);
            var result = operation();
            EngineSnapshot snapshot;
            try { snapshot = Snapshot(); }
            catch (Exception ex) when (TuningTransaction.IsExpectedFailure(ex)) { snapshot = Failure(ex); }
            return new(result.Success, result.Message, snapshot)
            {
                CanMaintainOwned = (result.Success || preserveMaintenance) && snapshot.State == EngineState.ReadOnlyReady && !maintenanceFaulted &&
                    HasMaintainableTargets()
            };
        }
        catch (Exception ex) when (TuningTransaction.IsExpectedFailure(ex))
        {
            var snapshot = Failure(ex);
            return new(false, snapshot.Detail, snapshot);
        }
    }

    private bool HasMaintainableTargets()
    {
        var tuningOwned = transaction!.HasMaintenanceTargets;
        var advancedOwned = advancedTransaction!.HasMaintenanceTargets;
        return (tuningOwned || advancedOwned) &&
            (!tuningOwned || lastWriteSession == memory!.Identity && transaction.CanMaintainOwned) &&
            (!advancedOwned || lastAdvancedWriteSession == memory!.Identity && advancedTransaction.CanMaintainOwned);
    }

    private EngineSnapshot? Acquire()
    {
        var candidate = source.OpenTarget();
        if (candidate is null)
        {
            Release();
            return Empty(EngineState.GameNotRunning, "iw4mp.exe is not running. Read-only inspection only; no game is launched.");
        }
        if (memory is not null && candidate.Identity == memory.Identity)
        {
            if (!ReferenceEquals(candidate, memory)) candidate.Dispose();
        }
        else
        {
            try { Release(); }
            catch { candidate.Dispose(); throw; }
            memory = candidate;
            transaction = new TuningTransaction(candidate);
            advancedTransaction = new AdvancedTransaction(candidate);
            if (useSelectiveAudio && candidate is ISelectiveAudioMemory audioMemory)
                selectiveAudio = new SelectiveAudio(audioMemory);
        }
        var profile = TuningValidation.ValidateProfile(memory!.Identity);
        if (!profile.Success) return ForIdentity(EngineState.UnsupportedBuild, profile.Message, UnsupportedFeatures(profile.Message));
        if (!memory.ValidateIdentity()) return ForIdentity(EngineState.Error, "Process lifetime or main module changed. Refresh to inspect the current session.", UnsupportedFeatures("Stale session."));
        return null;
    }

    private EngineSnapshot Snapshot() => ForIdentity(EngineState.ReadOnlyReady,
        "Supported game build detected. Optional settings are validated independently. Apply starts settings retention for this game process.",
        transaction!.InspectFeatures().Concat(advancedTransaction!.InspectFeatures().Select(feature =>
            useSelectiveAudio && feature.Id == "music"
                ? selectiveAudio?.Inspect() ?? new("music", "Selective mute", false, null, "Selective audio is unavailable.")
                : feature)).ToArray());

    private EngineSnapshot ForIdentity(EngineState state, string detail, IReadOnlyList<FeatureReading> features)
    {
        var identity = memory!.Identity;
        return new(state, "iw4mp.exe", identity.ProcessId, identity.ExecutablePath, identity.Sha256, detail, features);
    }

    private EngineSnapshot Failure(Exception exception)
    {
        var state = exception is TargetProcessException target ? target.State : EngineState.Error;
        return memory is null ? Empty(state, exception.Message) : ForIdentity(state, exception.Message, UnsupportedFeatures(exception.Message));
    }

    private static IReadOnlyList<FeatureReading> UnsupportedFeatures(string detail) =>
    [
        new("fov", "Field of view", false, null, detail), new("fps", "Maximum FPS", false, null, detail),
        new("music", "Mute streamed sounds", false, null, detail), new("fullbright", "Fullbright", false, null, detail),
        new("fog", "No fog", false, null, detail), new("glow", "No glow", false, null, detail),
        new("specular", "Specular map mode", false, null, detail), new("detail", "Hide detail overlays (experimental)", false, null, detail),
        new("ping", "Numeric scoreboard ping", false, null, detail)
    ];
    private static EngineSnapshot Empty(EngineState state, string detail) => new(state, "iw4mp.exe", null, "", "", detail, UnsupportedFeatures(detail));

    private void Release()
    {
        var previous = memory;
        memory = null;
        transaction = null;
        advancedTransaction = null;
        selectiveAudio = null;
        lastMenuCheck = 0;
        maintenanceFaulted = false;
        previous?.Dispose();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            Release();
        }
    }
}
