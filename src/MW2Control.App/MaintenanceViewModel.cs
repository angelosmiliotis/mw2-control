using MW2Control.Core;

namespace MW2Control.App;

public sealed partial class MainViewModel
{
    private CancellationTokenSource? _maintenanceCancellation;
    private Task? _maintenanceTask;
    private bool _maintenanceActive;
    public bool MaintenanceActive => _maintenanceActive;
    public string MaintenanceStatus { get; private set; } = "Choose your settings, then Apply.";

    private void StartMaintenance(EngineSnapshot session)
    {
        _maintenanceCancellation?.Dispose();
        _maintenanceCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _maintenanceActive = true;
        MaintenanceStatus = _operationNotice ?? "Settings are active.";
        _maintenanceTask = MaintainAsync(session, _maintenanceCancellation.Token);
        Refresh();
    }

    private async Task StopMaintenanceAsync()
    {
        _maintenanceCancellation?.Cancel();
        var task = _maintenanceTask;
        if (task is not null) await task;
    }

    private async Task MaintainAsync(EngineSnapshot session, CancellationToken token)
    {
        // Yield before the first native operation so Start can store the loop task.
        await Task.Yield();
        var displayClock = System.Diagnostics.Stopwatch.StartNew();
        var stoppedForError = false;
        var recovering = false;
        AddActivity("Settings retention started after Apply.");
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                await _gate.WaitAsync(token);
                OperationResult result;
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (_closed) break;
                    result = await Task.Run(() => _engine.MaintainOwned(true));
                }
                finally { _gate.Release(); }

                var snapshot = result.Snapshot;
                if (!result.CanMaintainOwned ||
                    snapshot.ProcessId != session.ProcessId ||
                    !string.Equals(snapshot.GamePath, session.GamePath, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(snapshot.Fingerprint, session.Fingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    _snapshot = snapshot;
                    MaintenanceStatus = "Settings paused. See Diagnostics.";
                    LastResult = "Automatic recovery paused. " + result.Message;
                    stoppedForError = true;
                    AddActivity(LastResult);
                    break;
                }
                if (!result.Success && !recovering)
                {
                    AddActivity("Waiting to restore settings in the same game session. " + result.Message);
                    LastResult = result.Message;
                }
                if (result.Success && recovering)
                {
                    AddActivity("Settings recovered automatically in the same game session.");
                    LastResult = "Settings recovered automatically.";
                }
                recovering = !result.Success;
                MaintenanceStatus = _operationNotice ?? (recovering ? "Restoring settings after a game change…" : "Settings are active.");
                if (displayClock.ElapsedMilliseconds >= 1000)
                {
                    _snapshot = snapshot;
                    Refresh();
                    displayClock.Restart();
                }
                await Task.Delay(recovering ? 1000 : 500, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            MaintenanceStatus = "Settings paused. See Diagnostics.";
            LastResult = "Automatic recovery paused: " + ex.Message;
            stoppedForError = true;
            AddActivity(LastResult);
        }
        finally
        {
            _maintenanceActive = false;
            if (!stoppedForError)
            {
                MaintenanceStatus = "Choose your settings, then Apply.";
                AddActivity("Settings retention stopped.");
            }
            Refresh();
        }
    }
}
