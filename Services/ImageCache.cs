using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;

namespace ConanModsSort;

public static class ImageCache
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ConanModsSort", "cache");

    private static string FileFor(string modId) => Path.Combine(Dir, modId + ".img");

    public static bool IsCached(string modId)
    {
        var f = FileFor(modId);
        return File.Exists(f) && new FileInfo(f).Length > 0;
    }

    public static BitmapImage? LoadCached(string modId)
    {
        if (!IsCached(modId)) return null;
        try { return LoadFrozen(FileFor(modId)); }
        catch { return null; }
    }

    public static async Task<BitmapImage?> DownloadAsync(string modId, string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        try
        {
            Directory.CreateDirectory(Dir);
            var file = FileFor(modId);
            if (!IsCached(modId))
            {
                var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
                await File.WriteAllBytesAsync(file, bytes).ConfigureAwait(false);
            }
            return LoadFrozen(file);
        }
        catch
        {
            return null;
        }
    }

    private static BitmapImage LoadFrozen(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bmp.DecodePixelWidth = 96;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
