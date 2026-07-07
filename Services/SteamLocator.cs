using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ConanModsSort;

public static class SteamLocator
{
    public const string ConanAppId = "440900";
    public const string ConanFolderName = "Conan Exiles";

    public static string? GetSteamPath()
    {
        try
        {
            var p = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string;
            if (!string.IsNullOrEmpty(p) && Directory.Exists(p))
                return Path.GetFullPath(p);
        }
        catch { }

        try
        {
            var p = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Wow6432Node\Valve\Steam")?.GetValue("InstallPath") as string
                 ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam")?.GetValue("InstallPath") as string;
            if (!string.IsNullOrEmpty(p) && Directory.Exists(p))
                return Path.GetFullPath(p);
        }
        catch { }

        return null;
    }

    public static List<string> GetLibraryFolders()
    {
        var result = new List<string>();
        var steam = GetSteamPath();
        if (steam == null) return result;

        result.Add(steam);

        foreach (var vdf in new[]
                 {
                     Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
                     Path.Combine(steam, "config", "libraryfolders.vdf"),
                 })
        {
            if (!File.Exists(vdf)) continue;
            try
            {
                var text = File.ReadAllText(vdf);
                foreach (Match m in Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase))
                {
                    var path = m.Groups[1].Value.Replace(@"\\", @"\");
                    if (Directory.Exists(path) && !result.Contains(path, StringComparer.OrdinalIgnoreCase))
                        result.Add(Path.GetFullPath(path));
                }
            }
            catch { }
        }

        return result;
    }

    public static string? FindModsFolder()
    {
        foreach (var lib in GetLibraryFolders())
        {
            var path = Path.Combine(lib, "steamapps", "workshop", "content", ConanAppId);
            if (Directory.Exists(path))
                return path;
        }
        return null;
    }

    public static string? FindDefaultModlistPath()
    {
        foreach (var lib in GetLibraryFolders())
        {
            var conan = Path.Combine(lib, "steamapps", "common", ConanFolderName);
            if (Directory.Exists(conan))
                return Path.Combine(conan, "ConanSandbox", "Mods", "modlist.txt");
        }
        return null;
    }

    public static Dictionary<string, long> ReadInstalledWorkshopTimes(string libraryRoot)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var acf = Path.Combine(libraryRoot, "steamapps", "workshop", $"appworkshop_{ConanAppId}.acf");
        if (!File.Exists(acf)) return result;

        string text;
        try { text = File.ReadAllText(acf); } catch { return result; }

        var block = ExtractBlock(text, "WorkshopItemsInstalled");
        if (block == null) return result;

        foreach (Match m in Regex.Matches(block, "\"(\\d+)\"\\s*\\{([^{}]*)\\}", RegexOptions.Singleline))
        {
            var tu = Regex.Match(m.Groups[2].Value, "\"timeupdated\"\\s*\"(\\d+)\"", RegexOptions.IgnoreCase);
            if (tu.Success && long.TryParse(tu.Groups[1].Value, out var t))
                result[m.Groups[1].Value] = t;
        }
        return result;
    }

    private static string? ExtractBlock(string text, string key)
    {
        var head = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s*\\{", RegexOptions.IgnoreCase);
        if (!head.Success) return null;

        int start = head.Index + head.Length, depth = 1, i = start;
        for (; i < text.Length && depth > 0; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}') depth--;
        }
        return text.Substring(start, i - start - 1);
    }

    public static string? FindGameExe()
    {
        foreach (var lib in GetLibraryFolders())
        {
            var exe = Path.Combine(lib, "steamapps", "common", ConanFolderName,
                "ConanSandbox", "Binaries", "Win64", "ConanSandbox-Win64-Shipping.exe");
            if (File.Exists(exe))
                return exe;
        }
        return null;
    }
}
