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
        public bool Editable => _enabled && (Available || Requested);
        public string Reading { get; private set; } = "Not inspected. Scan to validate this capability.";
        public AdvancedFeature(string id, string label, string description) => (Id, Label, Description) = (id, label, description);
        public void Update(FeatureReading? reading, bool enabled)
        {
            Available = reading?.Supported != false;
            _enabled = enabled;
            Reading = reading is null ? "Not inspected or mapping unavailable." :
                $"{(reading.Supported ? "Supported" : "Unavailable")} · observed: {reading.ObservedValue?.ToString("0.##") ?? "—"} · {reading.Detail}";
            PropertyChanged?.Invoke(this, new(string.Empty));
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public AdvancedFeature Music { get; } = new("music", "Mute streamed sounds", "Music and some streamed audio. Selective ambience and S&D filtering remain unresolved.");
    public AdvancedFeature Fullbright { get; } = new("fullbright", "Fullbright", "Validated lighting setting, when available for this build.");
    public AdvancedFeature Fog { get; } = new("fog", "No Fog", "Request fog removal through a validated setting.");
    public AdvancedFeature Glow { get; } = new("glow", "No Glow", "Request glow removal through a validated setting.");
    public AdvancedFeature Detail { get; } = new("detail", "No detail overlays", "Experimental No Camos approximation; actual camo removal is not guaranteed.");
    public AdvancedFeature Ping { get; } = new("ping", "Numeric ping", "Show the connection latency as a number on supported game builds.");
    public IReadOnlyList<AdvancedFeature> HudFeatures => new[] { Ping };
    public IReadOnlyList<AdvancedFeature> AudioFeatures => new[] { Music };
    public IReadOnlyList<AdvancedFeature> VisualFeatures => new[] { Fullbright, Fog, Glow, Detail };
    public IReadOnlyList<string> SpecularChoices { get; } = new[] { "Restore owned original", "0 · Black / off", "1 · Unchanged / native", "2 · White / shiny" };
    public int SpecularChoice { get; set; }
    public bool SpecularAvailable => InputsEnabled && !Features.Any(f => f.Id == "specular" && !f.Supported);
    public bool SpecularEditable => InputsEnabled && (SpecularAvailable || SpecularChoice != 0);
    public string SpecularReading => Features.FirstOrDefault(f => f.Id == "specular") is { } r
        ? $"{(r.Supported ? "Supported" : "Unavailable")} · observed: {r.ObservedValue?.ToString("0.##") ?? "—"} · {r.Detail}"
        : "Not inspected or mapping unavailable.";
    private AdvancedSettings CaptureAdvancedSettings() => new(
        Music.Requested, Fullbright.Requested, Fog.Requested, Glow.Requested,
        SpecularChoice is >= 1 and <= 3 ? SpecularChoice - 1 : null, Detail.Requested, Ping.Requested);

    private void RefreshAdvanced()
    {
        foreach (var feature in AudioFeatures.Concat(VisualFeatures).Concat(HudFeatures))
            feature.Update(Features.FirstOrDefault(f => f.Id == feature.Id), InputsEnabled);
    }
}
