using System.Runtime.InteropServices.WindowsRuntime;
using LlamaApp.Common;
using LlamaApp.HuggingFace;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;

namespace LlamaApp.Views;

/// <summary>
/// Disk + memory cache for Hugging Face author avatars shown next to Hub
/// models. Files live under the app's LocalCacheFolder ("avatars") so they
/// survive restarts but can be evicted by the OS; nothing is stored inside
/// the MSIX package itself. Avatars are a best-effort decoration — every
/// accessor fails soft (null) and callers keep whatever fallback they
/// already render.
/// </summary>
public static class AvatarCache
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // Resolved images, keyed by author. Callers attach avatars from UI-thread
    // async continuations, so all access is on the UI thread.
    private static readonly Dictionary<string, ImageSource?> Images = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the author's cached avatar — memory, then disk. Never touches
    /// the network, so it's safe to call for every row of a populated list.
    /// </summary>
    public static async Task<ImageSource?> GetAsync(string author)
    {
        if (string.IsNullOrWhiteSpace(author)) return null;
        if (Images.TryGetValue(author, out var cached)) return cached;

        var path = PathFor(author);
        if (path is null || !File.Exists(path)) return null;

        var image = await LoadAsync(path);
        Images[author] = image;
        return image;
    }

    /// <summary>
    /// Returns the author's cached avatar, fetching it from the Hub on first
    /// use (memory → disk → network) and storing it under the local cache for
    /// reuse. Null when the author has no avatar or the fetch fails.
    /// </summary>
    public static async Task<ImageSource?> GetOrFetchAsync(string author)
    {
        if (string.IsNullOrWhiteSpace(author)) return null;
        if (Images.TryGetValue(author, out var cached)) return cached;

        var path = PathFor(author);
        if (path is null) return null;

        if (!File.Exists(path))
        {
            try
            {
                var bytes = await new HubClient(null).GetUserAvatarBytesAsync(author);
                if (bytes is null || bytes.Length == 0) return null;
                await File.WriteAllBytesAsync(path, bytes);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, $"avatar fetch failed for {author}");
                return null;
            }
        }

        var image = await LoadAsync(path);
        Images[author] = image;
        return image;
    }

    /// <summary>Cache-file path for an author, or null when storage is unavailable.</summary>
    private static string? PathFor(string author)
    {
        try
        {
            var dir = Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, "avatars");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, author.ToLowerInvariant() + ".img");
        }
        catch
        {
            // Storage unavailable (e.g. the unpackaged test host) — avatars
            // are optional.
            return null;
        }
    }

    private static async Task<ImageSource?> LoadAsync(string path)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path);
            var image = new BitmapImage { DecodePixelWidth = 64 };
            using var stream = new MemoryStream(bytes);
            await image.SetSourceAsync(stream.AsRandomAccessStream());
            return image;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "avatar load failed");
            return null;
        }
    }
}
