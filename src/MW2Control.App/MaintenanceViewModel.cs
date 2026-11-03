using MW2Control.Core;

namespace MW2Control.App;

public sealed partial class MainViewModel
{
    private CancellationTokenSource? _maintenanceCancellation;
    private Task? _maintenanceTask;
    private bool _maintenanceActive;
    public bool MaintenanceActive => _maintenanceActive;
    public string MaintenanceStatus { get; private set; } = "Inactive. Apply starts retention for settings changed by this app.";

    private void StartMaintenance(EngineSnapshot session)
    {
        _maintenanceCancellation?.Dispose();
        _maintenanceCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _maintenanceActive = true;
        MaintenanceStatus = "Keeping applied settings active in this game session.";
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
                if (!result.Success || snapshot.State != EngineState.ReadOnlyReady ||
                    snapshot.ProcessId != session.ProcessId ||
                    !string.Equals(snapshot.GamePath, session.GamePath, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(snapshot.Fingerprint, session.Fingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    _snapshot = snapshot;
                    MaintenanceStatus = "Stopped: " + result.Message;
                    LastResult = "Keep applied stopped after a refusal, error or session change. " + result.Message;
                    stoppedForError = true;
                    AddActivity(LastResult);
                    break;
                }
                if (displayClock.ElapsedMilliseconds >= 1000)
                {
                    _snapshot = snapshot;
                    MaintenanceStatus = "Active. Keeping applied settings in this game session.";
                    Refresh();
                    displayClock.Restart();
                }
                await Task.Delay(100, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            MaintenanceStatus = "Stopped: " + ex.Message;
            LastResult = "Keep applied stopped after an error: " + ex.Message;
            stoppedForError = true;
            AddActivity(LastResult);
        }
        finally
        {
            _maintenanceActive = false;
            if (!stoppedForError)
            {
                MaintenanceStatus = "Inactive.";
                AddActivity("Settings retention stopped.");
            }
            Refresh();
        }
    }
}
