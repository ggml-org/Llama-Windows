namespace LlamaApp.Common;

/// <summary>
/// Recognition helpers for image files arriving through the Windows share
/// sheet (.jpg/.jpeg/.png, from a StorageItems share or a converted Bitmap
/// share). The llama WebUI's chat accepts these as attachments for
/// image-capable models.
/// </summary>
public static class SharedImage
{
    /// <summary>Image file extensions the app accepts from a share (any casing).</summary>
    public static readonly IReadOnlyList<string> Extensions = new[] { ".jpg", ".jpeg", ".png" };

    /// <summary>True when <paramref name="path"/> carries a shareable image extension.</summary>
    public static bool IsImagePath(string? path)
        => !string.IsNullOrWhiteSpace(path) &&
           Extensions.Contains(
               System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The MIME type for <paramref name="path"/>'s extension — what the WebUI
    /// attachment pipeline expects. Defaults to <c>image/png</c> for
    /// unrecognized image extensions (the app writes converted Bitmap shares
    /// as PNG, so anything it hands over itself is covered).
    /// </summary>
    public static string MimeTypeFor(string path)
        => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            _ => "image/png",
        };
}
