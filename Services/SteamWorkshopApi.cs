using System.Net.Http;
using System.Text.Json;

namespace ConanModsSort;

public static class SteamWorkshopApi
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private const string Url = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";

    public record Details(string PublishedFileId, string? Title, long FileSize, string? PreviewUrl,
        bool IsEnhanced, long TimeUpdated);

    public static async Task<Dictionary<string, Details>> GetDetailsAsync(IEnumerable<string> ids)
    {
        var idList = ids.Distinct().ToList();
        var map = new Dictionary<string, Details>();
        if (idList.Count == 0) return map;

        var form = new List<KeyValuePair<string, string>>
        {
            new("itemcount", idList.Count.ToString())
        };
        for (int i = 0; i < idList.Count; i++)
            form.Add(new($"publishedfileids[{i}]", idList[i]));

        using var content = new FormUrlEncodedContent(form);
        using var resp = await Http.PostAsync(Url, content);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);

        if (!doc.RootElement.TryGetProperty("response", out var response) ||
            !response.TryGetProperty("publishedfiledetails", out var arr) ||
            arr.ValueKind != JsonValueKind.Array)
            return map;

        foreach (var el in arr.EnumerateArray())
        {
            var id = GetString(el, "publishedfileid");
            if (string.IsNullOrEmpty(id)) continue;

            var title = GetString(el, "title");
            long size = GetLong(el, "file_size");
            var preview = GetString(el, "preview_url");
            long timeUpdated = GetLong(el, "time_updated");
            map[id] = new Details(id, title, size, preview, HasEnhancedTag(el), timeUpdated);
        }

        return map;
    }

    private static bool HasEnhancedTag(JsonElement el)
    {
        if (!el.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var t in tags.EnumerateArray())
        {
            if (t.TryGetProperty("tag", out var tag) &&
                string.Equals(tag.GetString(), "Enhanced", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string? GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long GetLong(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
        return 0;
    }
}
