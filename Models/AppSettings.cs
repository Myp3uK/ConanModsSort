using System.IO;
using System.Security.Cryptography;
using System.Text;
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
    public List<ModPreset> Presets { get; set; } = new();

    // Вход в Steam для подписки. Токен хранится зашифрованным (DPAPI, текущий пользователь).
    public string? SteamAccountName { get; set; }
    public string? SteamRefreshTokenProtected { get; set; }

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

    // ---------- Вход в Steam (refresh-токен, шифрование DPAPI) ----------

    public bool HasSteamLogin =>
        !string.IsNullOrEmpty(SteamAccountName) && !string.IsNullOrEmpty(SteamRefreshTokenProtected);

    public void SaveSteamLogin(string accountName, string refreshToken)
    {
        SteamAccountName = accountName;
        try
        {
            var bytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(refreshToken), null, DataProtectionScope.CurrentUser);
            SteamRefreshTokenProtected = Convert.ToBase64String(bytes);
        }
        catch { SteamRefreshTokenProtected = null; }
        Save();
    }

    public string? GetSteamRefreshToken()
    {
        if (string.IsNullOrEmpty(SteamRefreshTokenProtected)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(SteamRefreshTokenProtected), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return null; }
    }

    public void ClearSteamLogin()
    {
        SteamAccountName = null;
        SteamRefreshTokenProtected = null;
        Save();
    }
}
