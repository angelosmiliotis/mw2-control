using MW2Control.Core;

namespace MW2Control.App;

public sealed partial class MainViewModel
{
    private Task? _monitorTask;

    public void StartMonitoring()
    {
        if (!_closed && _monitorTask is null) _monitorTask = MonitorAsync(_lifetime.Token);
    }

    private async Task MonitorAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!Busy && !MaintenanceActive && await _gate.WaitAsync(0, token))
                {
                    try
                    {
                        if (!Busy && !MaintenanceActive && !_closed)
                        {
                            var previous = _snapshot;
                            _snapshot = await Task.Run(_engine.Inspect, token);
                            if (previous?.State != _snapshot.State || previous?.ProcessId != _snapshot.ProcessId)
                                AddActivity(_snapshot.Detail);
                            Refresh();
                        }
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested)
                    {
                        if (_snapshot?.Detail != ex.Message) AddActivity("Connection check failed. " + ex.Message);
                        _snapshot = new(EngineState.Error, "iw4mp.exe", null, "", "", ex.Message, Array.Empty<FeatureReading>());
                        Refresh();
                    }
                    finally { _gate.Release(); }
                }
                await Task.Delay(2000, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
