using System.ComponentModel;
using MW2Control.Core;

namespace MW2Control.App;

public sealed partial class MainViewModel
{
    public sealed class AdvancedFeature : INotifyPropertyChanged
    {
        public string Id { get; }
        public string Label { get; }
        public string Description { get; }
        private bool _requested;
        public bool Requested { get => _requested; set { _requested = value; PropertyChanged?.Invoke(this, new(nameof(Requested))); PropertyChanged?.Invoke(this, new(nameof(Editable))); } }
        public bool Available { get; private set; }
        private bool _enabled;
        private bool _allowStaging;
        public bool Editable => _enabled && (_allowStaging || Available || Requested);
        public string Reading { get; private set; } = "Not inspected. Scan to validate this capability.";
        public AdvancedFeature(string id, string label, string description) => (Id, Label, Description) = (id, label, description);
        public void Update(FeatureReading? reading, bool enabled) => Update(reading, enabled, false);

        internal void Update(FeatureReading? reading, bool enabled, bool allowStaging)
        {
            var available = reading?.Supported != false;
            var description = reading is null ? "Not inspected or mapping unavailable." :
                $"{(reading.Supported ? "Supported" : "Unavailable")} · observed: {reading.ObservedValue?.ToString("0.##") ?? "—"} · {reading.Detail}";
            if (Available == available && _enabled == enabled && _allowStaging == allowStaging && Reading == description) return;
            Available = available;
            _enabled = enabled;
            _allowStaging = allowStaging;
            Reading = description;
            PropertyChanged?.Invoke(this, new(string.Empty));
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public AdvancedFeature Music { get; } = new("music", "Mute streamed sounds", "Mute streamed audio while keeping gameplay cues such as footsteps, gunfire, explosions, and the bomb timer audible.");
    public AdvancedFeature Fullbright { get; } = new("fullbright", "Fullbright", "Validated lighting setting, when available for this build.");
    public AdvancedFeature Fog { get; } = new("fog", "No Fog", "Request fog removal through a validated setting.");
    public AdvancedFeature Glow { get; } = new("glow", "No Glow", "Request glow removal through a validated setting.");
    public AdvancedFeature Detail { get; } = new("detail", "No detail overlays", "Experimental No Camos approximation; actual camo removal is not guaranteed.");
    public AdvancedFeature Ping { get; } = new("ping", "Numeric ping", "Show the connection latency as a number on supported game builds.");
    public IReadOnlyList<AdvancedFeature> HudFeatures { get; }
    public IReadOnlyList<AdvancedFeature> AudioFeatures { get; }
    public IReadOnlyList<AdvancedFeature> VisualFeatures { get; }
    public IReadOnlyList<string> SpecularChoices { get; } = new[] { "Original", "Black / off", "Native", "White / shiny" };
    private int _specularChoice;
    public int SpecularChoice { get => _specularChoice; set { _specularChoice = value; Notify(); } }
    public bool SpecularAvailable => InputsEnabled && !Features.Any(f => f.Id == "specular" && !f.Supported);
    private bool CanStageSettings => _snapshot is null || _snapshot.State == EngineState.GameNotRunning;
    public bool SpecularEditable => InputsEnabled && (CanStageSettings || SpecularAvailable || SpecularChoice != 0);
    public string SpecularReading => Features.FirstOrDefault(f => f.Id == "specular") is { } r
        ? $"{(r.Supported ? "Supported" : "Unavailable")} · observed: {r.ObservedValue?.ToString("0.##") ?? "—"} · {r.Detail}"
        : "Not inspected or mapping unavailable.";
    private AdvancedSettings CaptureAdvancedSettings() => new(
        Music.Requested, Fullbright.Requested, Fog.Requested, Glow.Requested,
        SpecularChoice is >= 1 and <= 3 ? SpecularChoice - 1 : null, Detail.Requested, Ping.Requested);

    private void RefreshAdvanced()
    {
        foreach (var feature in AudioFeatures.Concat(VisualFeatures).Concat(HudFeatures))
            feature.Update(Features.FirstOrDefault(f => f.Id == feature.Id), InputsEnabled, CanStageSettings);
    }
}
