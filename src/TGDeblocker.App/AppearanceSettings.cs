using System.Text.Json;

namespace TGDeblocker.App;

public sealed class AppearanceSettings
{
    public string Theme { get; set; } = "system";
    public string Language { get; set; } = "auto";
    public double Density { get; set; } = 0.12;
    public bool ReduceMotion { get; set; }
    public bool ReduceTransparency { get; set; }
    private static string FileName => Path.Combine(AppIdentity.DataDirectory, "appearance.json");
    public static AppearanceSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AppearanceSettings>(File.ReadAllText(FileName)) ?? new();
            if (settings.Language is not ("auto" or "ru" or "en")) settings.Language = "auto";
            settings.Theme = TGDeblocker.Appearance.ThemePreference.Normalize(settings.Theme);
            settings.Density = double.IsFinite(settings.Density) ? Math.Clamp(settings.Density, 0, 1) : 0.12;
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FileName)!);
        var temp = FileName + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this));
        File.Move(temp, FileName, true);
    }
}
