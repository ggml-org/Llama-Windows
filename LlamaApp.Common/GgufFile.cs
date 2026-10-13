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
