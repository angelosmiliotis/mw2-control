using System.IO;
using System.Text.Json;

namespace MW2Control.App;

public sealed partial class MainViewModel
{
    private readonly PreferencesStore? _preferences;

    private void LoadPreferences()
    {
        try
        {
            var settings = _preferences?.Load() ?? new();
            RequestedFov = settings.Fov;
            RequestedFps = settings.Fps;
            UncappedFps = settings.UncappedFps;
            Music.Requested = settings.MuteStreams;
            Fullbright.Requested = settings.Fullbright;
            Fog.Requested = settings.NoFog;
            Glow.Requested = settings.NoGlow;
            Detail.Requested = settings.NoDetailOverlays;
            Ping.Requested = settings.NumericPing;
            SpecularChoice = settings.SpecularChoice;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AddActivity("Saved preferences could not be loaded. Using defaults. " + ex.Message);
        }
    }

    private void SavePreferences()
    {
        try
        {
            _preferences?.Update(current => new UserPreferences
            {
                Fov = RequestedFov, Fps = RequestedFps, UncappedFps = UncappedFps,
                MuteStreams = Music.Requested, Fullbright = Fullbright.Requested,
                NoFog = Fog.Requested, NoGlow = Glow.Requested,
                NoDetailOverlays = Detail.Requested, NumericPing = Ping.Requested,
                SpecularChoice = SpecularChoice, NetworkAdapterId = current.NetworkAdapterId
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddActivity("Preferences could not be saved. " + ex.Message);
        }
    }
}
