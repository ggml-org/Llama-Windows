namespace LlamaApp.Common;

/// <summary>
/// GGUF file-path helpers for the "Open With" flow (right-click a
/// <c>.gguf</c> → Open With → Llama). The extension test is case-insensitive
/// because Windows filenames are — a file named <c>MODEL.GGUF</c> is opened
/// just fine; the importer normalizes the name it lands on.
/// </summary>
public static class GgufFile
{
    /// <summary>True when <paramref name="path"/> has a <c>.gguf</c> extension (any casing).</summary>
    public static bool IsGguf(string? path)
        => !string.IsNullOrWhiteSpace(path) &&
           path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the file starts with the GGUF magic — i.e. it is real GGUF
    /// content, not just a <c>.gguf</c>-named file (an import fixture, a
    /// download stub). Follows symlinks (HF cache entries are reparse
    /// points). False for anything unreadable. This is what separates a
    /// loadable model from a leftover artifact.
    /// </summary>
    public static bool HasGgufMagic(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            return stream.Read(magic) == 4 &&
                   magic[0] == (byte)'G' && magic[1] == (byte)'G' &&
                   magic[2] == (byte)'U' && magic[3] == (byte)'F';
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The first <c>.gguf</c> among <paramref name="paths"/> (a file
    /// activation may carry several), or null. Null/blank entries are
    /// skipped.
    /// </summary>
    public static string? FirstGgufPath(IEnumerable<string?> paths)
    {
        foreach (var path in paths)
            if (IsGguf(path))
                return path;
        return null;
    }
}
