using System.IO;
using System.Text.Json;

namespace MW2Control.App;

public sealed record UserPreferences
{
    public int Version { get; init; } = 1;
    public double Fov { get; init; } = 90;
    public double Fps { get; init; } = 333;
    public bool UncappedFps { get; init; }
    public bool MuteStreams { get; init; }
    public bool Fullbright { get; init; }
    public bool NoFog { get; init; }
    public bool NoGlow { get; init; }
    public bool NoDetailOverlays { get; init; }
    public bool NumericPing { get; init; }
    public int SpecularChoice { get; init; }

    public UserPreferences Normalize() => this with
    {
        Fov = double.IsFinite(Fov) ? Math.Clamp(Math.Round(Fov), 65, 100) : 90,
        Fps = double.IsFinite(Fps) ? Math.Clamp(Math.Round(Fps), 30, 500) : 333,
        SpecularChoice = Math.Clamp(SpecularChoice, 0, 3)
    };
}

public sealed class PreferencesStore(string path)
{
    public static PreferencesStore ForCurrentUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MW2Control", "settings.json"));

    public UserPreferences Load()
    {
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 65536) throw new IOException("Settings file is too large.");
        var settings = JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(path));
        return settings is { Version: 1 } ? settings.Normalize() : new();
    }

    public void Save(UserPreferences settings)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings.Normalize(), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
