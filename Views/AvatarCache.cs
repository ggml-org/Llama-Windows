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
    // Coalesces concurrent Hub fetches per author and bounds total in-flight
    // fetches, so populating a whole installed list (or racing a poller tick)
    // never storms the Hub. Width 2 is deliberately modest: each avatar is two
    // serial HTTP hops with 10s timeouts, and avatars are a decoration.
    private static readonly AvatarFetchGate FetchGate = new(2, FetchAndStoreAsync);

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
        // Never poison the memory cache with a null (e.g. a corrupt cache file
        // that fails to decode) — a later call should be free to retry.
        if (image is not null) Images[author] = image;
        return image;
    }

    /// <summary>
    /// Returns the author's cached avatar, fetching it from the Hub on first
    /// use (memory → disk → network) and storing it under the local cache for
    /// reuse. Fetch-on-miss is coalesced per author and globally bounded (see
    /// <see cref="AvatarFetchGate"/>), so a burst of rows for the same org
    /// costs a single request. Null when the author has no avatar or the fetch
    /// fails.
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
                var bytes = await FetchGate.FetchAsync(author);
                if (bytes is null || bytes.Length == 0) return null;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, $"avatar fetch failed for {author}");
                return null;
            }
        }

        var image = await LoadAsync(path);
        // See GetAsync: a failed decode must not be stored as a resolved image.
        if (image is not null) Images[author] = image;
        return image;
    }

    // Runs inside the coalesced gate task: N rows racing for the same author
    // share one Hub fetch and one file write, then each reads via LoadAsync.
    private static async Task<byte[]?> FetchAndStoreAsync(string author)
    {
        var bytes = await new HubClient(null).GetUserAvatarBytesAsync(author);
        if (bytes is null || bytes.Length == 0) return null;

        var path = PathFor(author);
        if (path is null) return null;

        await File.WriteAllBytesAsync(path, bytes);
        return bytes;
    }

    /// <summary>Cache-file path for an author, or null when storage is unavailable.</summary>
    private static string? PathFor(string author)
    {
        try
        {
            var dir = Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, "avatars");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, SanitizeFileName(author) + ".img");
        }
        catch
        {
            // Storage unavailable (e.g. the unpackaged test host) — avatars
            // are optional.
            return null;
        }
    }

    /// <summary>
    /// Reduces an author/org name to a safe single cache-file stem: ASCII
    /// letters, digits, dash and underscore are kept (lowercased); everything
    /// else — separators, dots (".."!), Unicode, control characters — becomes
    /// an underscore. The name comes from Hub data, so it must never be
    /// trusted to shape a path segment. Distinct authors can only collide by
    /// sharing a stem when they differ solely in such characters; worst case
    /// two avatars share a file, which is harmless for a decoration.
    /// </summary>
    internal static string SanitizeFileName(string author)
    {
        var sb = new System.Text.StringBuilder(author.Length);
        foreach (var c in author.ToLowerInvariant())
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return sb.Length == 0 ? "_" : sb.ToString();
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
