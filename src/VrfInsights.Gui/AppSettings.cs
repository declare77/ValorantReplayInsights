using System.Text.Json;

namespace VrfInsights.Gui;

/// <summary>
/// Small persisted settings file so the user only has to point the GUI at their vrfkit.exe (and
/// pick an output folder) once, rather than every time they open the app. Stored as plain JSON
/// under <c>%AppData%\VrfInsights\settings.json</c> — nothing sensitive, just local file paths.
/// </summary>
public sealed class AppSettings
{
    public string? VrfkitExePath { get; set; }
    public string? LastOutputDirectory { get; set; }
    public double FovDegrees { get; set; } = VrfInsights.Analysis.Vision.VisionConeCalculator.DefaultFovDegrees;
    public double RangeCm { get; set; } = VrfInsights.Analysis.Vision.VisionConeCalculator.DefaultRangeCm;
    public double EyeHeightCm { get; set; } = VrfInsights.Analysis.Vision.VisionConeCalculator.EyeHeightCm;
    public bool WithVision { get; set; }

    private static string SettingsFilePath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "VrfInsights", "settings.json");
    }

    /// <summary>Loads settings from disk, or returns defaults if the file doesn't exist or can't
    /// be read/parsed — a corrupt or missing settings file should never prevent the app from
    /// starting.</summary>
    public static AppSettings Load()
    {
        try
        {
            string path = SettingsFilePath();
            if (!File.Exists(path)) return new AppSettings();

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    /// <summary>Saves settings to disk. Failure here (e.g. no write permission) is not fatal to
    /// the app, so it's swallowed rather than shown as an error dialog.</summary>
    public void Save()
    {
        try
        {
            string path = SettingsFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort persistence only.
        }
    }
}
