using System.Text;
using LlamaApp.Common;

namespace LlamaApp.Llama;

/// <summary>
/// One model found in a user-enrolled folder: its INI section
/// <see cref="Name"/>, the main GGUF (single file or the first shard of a
/// set), and the sidecars that pair with it.
/// </summary>
public sealed record EnrolledFolderModel(string Name, string ModelPath, string? MmprojPath, string? DraftPath);

/// <summary>
/// Finds the models inside the folders a user enrolled for discovery
/// (Settings → General → Model folders), recursively, and renders each one
/// as a <c>--models-preset</c> INI section pointing the server straight at
/// the files where they are — nothing is copied, moved or linked, so a
/// folder on another drive enrolls as cheaply as one on the app's own.
///
/// <para><b>Why INI presets and not a second models-dir:</b> llama.cpp takes
/// a single <c>--models-dir</c> (its own scan is one level deep), but its
/// <c>--models-preset</c> INI accepts a <c>model = path</c> section per
/// custom model (the server's <c>SERVER_MODEL_SOURCE_PRESET</c> source),
/// and the app already writes and reloads that INI on every server start
/// and preset change. Each discovered model becomes one section; loading
/// it spawns a child with <c>--model</c> at the original path.</para>
///
/// <para><b>What counts as a model</b> (llama.cpp's own models-dir rules,
/// shared with <see cref="LocalModelImport"/>): in any directory at any
/// depth, a <c>.gguf</c> whose name isn't a sidecar — <c>mmproj</c> is a
/// vision projector, <c>mtp-/dspark-/dflash-</c> are draft heads, and a
/// shard set counts once by its <c>…-00001-of-…</c> first shard. Every
/// main in a directory is its own model (a folder with several quants of
/// one family lists each), paired with at most one quant-matching mmproj
/// and at most one draft sidecar from the same directory.</para>
///
/// <para>Scan is <see cref="Scan"/> (I/O, temp-dir testable); the INI shape
/// is <see cref="RenderSections"/> (pure).</para>
/// </summary>
public static class EnrolledFolderScanner
{
    /// <summary>
    /// Scans every enrolled folder recursively. Missing or unreadable roots
    /// are skipped with a log line (enrollment must never fault a launch).
    /// <paramref name="excludeUnder"/> roots (the HF cache, the app's managed
    /// models folder) contribute nothing: the server already lists those, and
    /// a user enrolling their %LOCALAPPDATA% must not duplicate the cache.
    /// Symlinked GGUF files count (the HF-cache layout uses them); junction
    /// directories are never followed — a loop must not spiral the walk.
    /// </summary>
    public static IReadOnlyList<EnrolledFolderModel> Scan(
        IEnumerable<string?> roots, IEnumerable<string?>? excludeUnder = null)
    {
        var excluded = excludeUnder?
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(NormalizeRoot)
            .OfType<string>()
            .ToList();

        // Main-file path → classification, deduped across overlapping roots.
        var byPath = new Dictionary<string, (string Path, string? Mmproj, string? Draft)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string fullRoot;
            try { fullRoot = NormalizeRoot(root) ?? Path.GetFullPath(root); }
            catch
            {
                Log.Debug($"model folders: '{root}' is not a valid path; skipped");
                continue;
            }
            if (!Directory.Exists(fullRoot))
            {
                Log.Debug($"model folders: '{fullRoot}' does not exist; skipped");
                continue;
            }

            // A manual walk instead of RecurseSubdirectories, because the two
            // reparse-point rules differ and must be enforced separately:
            // FILE links (a symlinked GGUF — the HF-cache layout uses them)
            // are legitimate models the server can open by path, while
            // DIRECTORY links (junctions) are never descended into — a
            // junction loop must not spiral the walk.
            var pending = new Stack<string>();
            pending.Push(fullRoot);
            while (pending.Count > 0)
            {
                var dir = pending.Pop();
                IEnumerable<System.IO.FileInfo> ggufs = [];
                IEnumerable<System.IO.DirectoryInfo> subdirs = [];
                try
                {
                    var info = new DirectoryInfo(dir);
                    ggufs = info.EnumerateFiles("*.gguf");
                    subdirs = info.EnumerateDirectories();
                }
                catch (Exception ex)
                {
                    Log.Debug($"model folders: '{dir}' can't be read ({ex.Message}); skipped");
                    continue;
                }

                foreach (var file in ggufs)
                {
                    try
                    {
                        if (!IsMainModelFile(file.FullName)) continue;
                        var full = file.FullName;
                        if (excluded is not null && excluded.Any(e => IsUnder(full, e))) continue;
                        if (byPath.ContainsKey(full)) continue;

                        // Sidecars pair from the same directory: reuse the Open With
                        // collector — one quant-matching mmproj, one draft, shards.
                        var parts = LocalModelImport.CollectModelFiles(full);
                        string? mmproj = null, draft = null;
                        foreach (var part in parts)
                        {
                            if (LocalModelImport.IsMmprojPath(part)) mmproj ??= part;
                            else if (IsDraftName(Path.GetFileName(part))) draft ??= part;
                        }
                        byPath[full] = (full, mmproj, draft);
                    }
                    catch (Exception ex)
                    {
                        Log.Debug($"model folders: '{file.FullName}' can't be enrolled ({ex.Message})");
                    }
                }

                foreach (var sub in subdirs)
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    pending.Push(sub.FullName);
                }
            }
        }

        return NameAndOrder(byPath);
    }

    /// <summary>
    /// Renders the INI sections for the scanned models:
    /// <c>[name]</c> with <c>model = path</c>, plus <c>mmproj</c> and
    /// <c>spec-draft-model</c> when the sidecars were found — the same keys
    /// llama.cpp's own models-dir scan sets (the INI parser's key grammar is
    /// <c>ident</c> and its section names accept anything but <c>]</c>, and
    /// the section names here are file stems, which can never carry
    /// <c>/</c>, <c>:</c> or <c>]</c> on Windows). Empty for no models.
    /// </summary>
    public static string RenderSections(IEnumerable<EnrolledFolderModel> models)
    {
        var sb = new StringBuilder();
        foreach (var model in models)
        {
            sb.Append('[').Append(model.Name).Append(']').Append('\n');
            sb.Append("model = ").Append(model.ModelPath).Append('\n');
            if (model.MmprojPath is not null)
                sb.Append("mmproj = ").Append(model.MmprojPath).Append('\n');
            if (model.DraftPath is not null)
                sb.Append("spec-draft-model = ").Append(model.DraftPath).Append('\n');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// A <c>.gguf</c> that names a model rather than a sidecar: not an mmproj,
    /// not a draft head, and either a plain single file or the FIRST shard of
    /// its set (<c>…-00001-of-…</c> — later shards and non-conforming names
    /// with the marker don't count).
    /// </summary>
    private static bool IsMainModelFile(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        if (LocalModelImport.IsMmprojPath(path)) return false;
        if (IsDraftName(stem)) return false;

        if (stem.Contains(LocalModelImport.ShardMarker, StringComparison.Ordinal))
        {
            // First shard only; the marker without a valid set base is a
            // malformed name, not a model.
            return LocalModelImport.ShardSetBase(path) is not null &&
                   stem.Contains("-00001-of-", StringComparison.Ordinal);
        }
        return true;
    }

    private static bool IsDraftName(string fileName)
        => fileName.StartsWith("mtp-", StringComparison.OrdinalIgnoreCase) ||
           fileName.StartsWith("dspark-", StringComparison.OrdinalIgnoreCase) ||
           fileName.StartsWith("dflash-", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Assigns the displayed/section names and orders the result
    /// deterministically (by path). A stem names its model directly; when
    /// several models share a stem, EVERY one of them gets its parent
    /// folder prepended (a bare stem for one twin and a prefixed name for
    /// the other would leave no way to tell which folder is which), then
    /// grandparents until free, with a counter breaking the (theoretical)
    /// exhaustion — the outcome never depends on enumeration order.
    /// </summary>
    private static IReadOnlyList<EnrolledFolderModel> NameAndOrder(
        Dictionary<string, (string Path, string? Mmproj, string? Draft)> byPath)
    {
        // Unique stems claim their name first, so a later duplicate can never
        // shadow a distinct model that happens to equal a prefixed form.
        var byStem = byPath
            .Select(k => (k.Value.Path, k.Value.Mmproj, k.Value.Draft, Stem: StemOf(k.Value.Path)))
            .Where(e => e.Stem is not null)
            .GroupBy(e => e.Stem!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in byStem.Where(g => g.Count() == 1))
            taken.Add(group.Key);

        var result = new List<EnrolledFolderModel>(byPath.Count);
        foreach (var group in byStem)
        {
            foreach (var entry in group.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
            {
                var name = group.Key;
                if (group.Count() > 1)
                {
                    // Shared stem: EVERY member starts from its parent-prefixed
                    // form — "parent-stem" — and grandparents extend it while
                    // the name is taken.
                    var parent = Path.GetDirectoryName(Path.GetFullPath(entry.Path));
                    if (parent is { Length: > 3 } && Path.GetFileName(parent) is { Length: > 0 } firstParent)
                        name = firstParent + "-" + name;
                    var fallback = 2;
                    while (taken.Contains(name))
                    {
                        if (parent is { Length: > 3 } && Path.GetFileName(parent) is { Length: > 0 } nextParent)
                        {
                            name = nextParent + "-" + name;
                            parent = Path.GetDirectoryName(parent);
                        }
                        else
                        {
                            // Out of parents (or at a drive root) — number it.
                            name = $"{group.Key}-{fallback++}";
                        }
                    }
                }
                taken.Add(name);
                result.Add(new EnrolledFolderModel(name, entry.Path, entry.Mmproj, entry.Draft));
            }
        }
        return result;
    }

    /// <summary>The section-name stem of a model file: its name, with a shard
    /// set's base replacing the <c>…-00001-of-…</c> part. Null when the name
    /// can't head an INI section (empty, or carries ']').</summary>
    private static string? StemOf(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        if (LocalModelImport.ShardSetBase(path) is { } shardBase)
            stem = shardBase;
        return stem.Length == 0 || stem.Contains(']') ? null : stem;
    }

    /// <summary>Full path with a trailing separator, for prefix checks; null when not resolvable.</summary>
    private static string? NormalizeRoot(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var full = Path.GetFullPath(path.Trim());
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="path"/> is inside <paramref name="root"/> (a normalized, trailing-separator root).</summary>
    private static bool IsUnder(string path, string root)
        => path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
}
