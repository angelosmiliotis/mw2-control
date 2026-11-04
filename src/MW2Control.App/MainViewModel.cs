using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MW2Control.Core;

namespace MW2Control.App;

public sealed partial class MainViewModel : INotifyPropertyChanged
{
    private readonly ControlEngine _engine;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private EngineSnapshot? _snapshot;
    private bool _busy;
    private bool _closed;
    private string? _operationNotice;
    private double _fov = 90;
    private double _fps = 333;
    private string _lastResult = "Ready. Apply checks the game build before changing settings.";

    public MainViewModel() : this(new ControlEngine(), PreferencesStore.ForCurrentUser()) { }

    public MainViewModel(ControlEngine engine, PreferencesStore? preferences = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _preferences = preferences;
        HudFeatures = new[] { Ping };
        AudioFeatures = new[] { Music };
        VisualFeatures = new[] { Fullbright, Fog, Glow, Detail };
        ScanCommand = new AsyncCommand(() => RunAsync("Inspecting process…", () =>
        {
            var snapshot = _engine.Inspect();
            return new OperationResult(true, snapshot.Detail, snapshot);
        }, "Choose your settings, then Apply.", "Could not check the game."), () => InputsEnabled && !MaintenanceActive);

        ApplyCommand = new AsyncCommand(() =>
        {
            SavePreferences();
            var tuning = new TuningRequest(UncappedFps ? 0 : (int)Math.Round(RequestedFps), RequestedFov);
            var advanced = CaptureAdvancedSettings();
            return RunAsync("Checking and applying settings…", () =>
            {
                return _engine.ApplySettings(tuning, advanced);
            }, "Settings applied.", "Apply could not be completed.");
        }, () => InputsEnabled && _snapshot?.State == EngineState.ReadOnlyReady);

        ResetCommand = new AsyncCommand(() => RunAsync("Restoring original settings…", () =>
        {
            return _engine.ResetSettings();
        }, "Original settings restored.", "Reset could not be completed."), () => InputsEnabled && (MaintenanceActive || _snapshot?.State == EngineState.ReadOnlyReady));
        LoadPreferences();
        RefreshAdvanced();
    }

    public ICommand ScanCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand ResetCommand { get; }
    public string AppVersion => "Version " + typeof(MainViewModel).Assembly.GetName().Version!.ToString(2);
    private bool _uncappedFps;
    public bool UncappedFps { get => _uncappedFps; set { _uncappedFps = value; Refresh(); } }
    public bool FpsSliderEnabled => InputsEnabled && !UncappedFps;
    public string RequestedFpsDisplay => UncappedFps ? "Uncapped" : RequestedFps.ToString("0", CultureInfo.InvariantCulture);
    public ObservableCollection<string> Activity { get; } = new();
    public IReadOnlyList<FeatureReading> Features => _snapshot?.Features ?? Array.Empty<FeatureReading>();
    public bool Busy { get => _busy; private set { _busy = value; Refresh(); } }
    public bool InputsEnabled => !Busy && !_closed;
    public double RequestedFov
    {
        get => _fov;
        set { _fov = Math.Clamp(Math.Round(value), 65, 100); Notify(); }
    }
    public double RequestedFps
    {
        get => _fps;
        set { _fps = Math.Clamp(Math.Round(value), 30, 500); Notify(); Notify(nameof(RequestedFpsDisplay)); }
    }
    public string StateLabel => Busy ? "WORKING" : MaintenanceActive ? "CONNECTED" : _snapshot?.State switch
    {
        EngineState.GameNotRunning => "GAME NOT RUNNING",
        EngineState.UnsupportedBuild => "UNSUPPORTED BUILD",
        EngineState.ReadOnlyReady => "CONNECTED",
        EngineState.Error => "INSPECTION ERROR",
        _ => "CONNECTING"
    };
    public string SessionTitle => _snapshot?.State switch
    {
        EngineState.ReadOnlyReady => "Compatible session detected",
        EngineState.GameNotRunning => "Waiting for your session",
        EngineState.UnsupportedBuild => "This build is not supported",
        EngineState.Error => "Inspection needs attention",
        _ => "Your session, under your control"
    };
    public string SessionDetail => _snapshot?.Detail ?? "Apply checks iw4mp.exe and validates its build before changing settings.";
    public string LastResult { get => _lastResult; private set { _lastResult = value; Notify(); } }
    public string ObservedFov => Observed("fov");
    public string ObservedFps => Observed("fps") == "0" ? "Uncapped" : Observed("fps");
    public string ProcessName => string.IsNullOrWhiteSpace(_snapshot?.ProcessName) ? "—" : _snapshot.ProcessName;
    public string ProcessId => _snapshot?.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public string GamePath => string.IsNullOrWhiteSpace(_snapshot?.GamePath) ? "No executable inspected" : _snapshot.GamePath;
    public string Fingerprint => string.IsNullOrWhiteSpace(_snapshot?.Fingerprint) ? "No fingerprint available" : _snapshot.Fingerprint;

    private string Observed(string key)
    {
        var reading = Features.FirstOrDefault(f => f.Id.Contains(key, StringComparison.OrdinalIgnoreCase));
        return reading is { Supported: true, ObservedValue: double value } && double.IsFinite(value)
            ? value.ToString("0.##", CultureInfo.InvariantCulture) : "—";
    }

    private async Task RunAsync(string pending, Func<OperationResult> operation, string completed, string failed)
    {
        if (!InputsEnabled) return;
        Busy = true;
        _operationNotice = null;
        LastResult = pending;
        var maintain = false;
        await StopMaintenanceAsync();
        await _gate.WaitAsync();
        try
        {
            if (_closed) return;
            var result = await Task.Run(operation);
            _snapshot = result.Snapshot;
            LastResult = result.Message;
            AddActivity(result.Message);
            maintain = result.CanMaintainOwned;
            if (!result.Success)
                _operationNotice = failed + (maintain ? " Previous settings remain active." : " See Diagnostics.");
            MaintenanceStatus = _operationNotice ?? completed;
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex)
        {
            // A failed operation invalidates the UI's prior permission to write.
            LastResult = "Operation failed: " + ex.Message;
            _operationNotice = "The operation failed. See Diagnostics.";
            MaintenanceStatus = _operationNotice;
            _snapshot = new EngineSnapshot(EngineState.Error, string.Empty, null,
                string.Empty, string.Empty, LastResult, Array.Empty<FeatureReading>());
            AddActivity(LastResult);
        }
        finally
        {
            _gate.Release();
            Busy = false;
            if (maintain && !_closed && _snapshot is { State: EngineState.ReadOnlyReady } session)
                StartMaintenance(session);
        }
    }

    private void AddActivity(string message)
    {
        Activity.Insert(0, $"{DateTime.Now:HH:mm:ss}   {message}");
        while (Activity.Count > 30) Activity.RemoveAt(Activity.Count - 1);
    }

    public async Task DisposeAsync()
    {
        if (_closed) return;
        SavePreferences();
        _closed = true;
        _lifetime.Cancel();
        await StopMaintenanceAsync();
        if (_monitorTask is not null) await _monitorTask;
        await _gate.WaitAsync();
        try { _engine.Dispose(); }
        finally { _gate.Release(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new(propertyName));
    private void Refresh()
    {
        RefreshAdvanced();
        Notify(string.Empty);
        CommandManager.InvalidateRequerySuggested();
    }

    private sealed class AsyncCommand(Func<Task> action, Func<bool> canExecute) : ICommand
    {
        public bool CanExecute(object? parameter) => canExecute();
        public async void Execute(object? parameter)
        {
            if (CanExecute(parameter)) await action();
        }
        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }
}
