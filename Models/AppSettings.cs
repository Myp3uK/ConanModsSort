using System.IO;
using System.Text.Json;

namespace ConanModsSort;

public class ModPreset
{
    public string Name { get; set; } = "";
    public bool IsEnhanced { get; set; }
    public List<string> ModIds { get; set; } = new();

    public override string ToString() => Name;
}

public class AppSettings
{
    public string? ModsFolder { get; set; }
    public string? ModlistPath { get; set; }
    public string? GamePath { get; set; }
    public string? SteamCmdPath { get; set; }
    public string? SteamLogin { get; set; }
    public List<ModPreset> Presets { get; set; } = new();

    private static string SettingsPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ConanModsSort");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch {  }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch {  }
    }
}
