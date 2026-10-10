using System.Security.Cryptography;
using System.Text;

namespace LlamaApp.Llama;

/// <summary>Finds GGUF weights in user-selected folders without moving them.</summary>
internal static class LocalModelDirectory
{
    internal static IReadOnlyDictionary<string, string> Scan(IEnumerable<string>? directories)
    {
        var models = new Dictionary<string, string>(StringComparer.Ordinal);
        if (directories is null) return models;

        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            try
            {
                var root = Path.GetFullPath(directory.Trim());
                if (!Directory.Exists(root)) continue;
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    MatchCasing = MatchCasing.CaseInsensitive,
                };
                foreach (var file in Directory.EnumerateFiles(root, "*.gguf", options))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    // Multimodal projectors are accessories, not standalone models.
                    if (name.Contains("mmproj", StringComparison.OrdinalIgnoreCase)) continue;

                    var slug = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'
                        ? c : '-').Take(64).ToArray()).Trim('-');
                    if (slug.Length == 0) slug = "model";
                    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.ToUpperInvariant())))[..12];
                    models[$"local/{slug}-{hash}"] = file;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // One unavailable folder must not hide models in the other folders.
            }
        }
        return models;
    }
}
