using System.Runtime.InteropServices;
using LlamaApp.Common;

namespace LlamaApp.Llama;

/// <summary>How <see cref="LocalModelImport.Import"/> materialized the model.</summary>
public enum LocalModelImportOutcome
{
    /// <summary>New hard links were created in the models folder.</summary>
    Linked,

    /// <summary>The files were copied (hard link impossible — cross-volume, or a
    /// file system without link support).</summary>
    Copied,

    /// <summary>Nothing to do — an identical import is already present.</summary>
    AlreadyPresent,
}

/// <summary>The result of importing one opened model file.</summary>
/// <param name="FileCount">How many files the import links/copies (main model + sidecars).</param>
public sealed record LocalModelImportResult(
    LocalModelImportOutcome Outcome,
    string ModelId,
    string TargetPath,
    bool ReplacedExisting,
    int FileCount = 1);

/// <summary>
/// Brings a model file the user "opened with" Llama into the llama server's
/// <c>--models-dir</c>, where the router discovers it as a loadable model.
///
/// <para><b>Why import at all:</b> the router's <c>POST /models/load</c> only
/// resolves ids in its model registry (HF-cache repos or files under
/// <c>--models-dir</c>) — an arbitrary filesystem path answers
/// <c>404 File Not Found</c>. A <c>.gguf</c> directly in the models dir gets the
/// id <c><filename-without-.gguf></c> (llama.cpp's
/// <c>load_from_models_dir</c>), and the registry rescans on
/// <c>GET /models?reload=1</c> — so link-then-refresh needs no server
/// restart.</para>
///
/// <para><b>Sidecars:</b> llama.cpp scans a <i>subdirectory</i> of the models
/// dir as ONE model (id = directory name) and auto-matches the
/// <c>mmproj</c> multimodal projector, speculative-decoding draft and
/// multi-shard siblings it finds inside
/// (common/preset.cpp: <c>is_mmproj_file</c> = name contains "mmproj",
/// <c>is_draft_file</c> = name starts with "mtp-"/"dspark-"/"dflash-", shard
/// sets = "-00001-of-"). So when the opened model has sidecars next to it
/// on disk, the import switches to that directory layout and vision /
/// speculative decoding / sharding all keep working. Opening an
/// <c>mmproj</c> directly resolves to the (unambiguous) main model file
/// next to it instead.</para>
///
/// <para><b>Hard link first:</b> opening a 40 GB model must not duplicate 40 GB
/// of disk. <see cref="File.CreateHardLink"/> needs no elevation and is free on
/// the same volume (Downloads → models dir is the common case); the copy
/// fallback covers other volumes.</para>
/// </summary>
public static class LocalModelImport
{
    // llama.cpp's speculative-decoding draft prefixes (common/preset.cpp).
    private static readonly string[] DraftPrefixes = ["mtp-", "dspark-", "dflash-"];

    // "-00001-of-00002" style shard markers.
    private const string ShardMarker = "-of-";

    /// <summary>
    /// Links (or, failing that, copies) the opened model into
    /// <paramref name="modelsDir"/> under a shape the router scans: a lone
    /// <c>.gguf</c> at the top level (id = file stem), or — when the model has
    /// sidecars on disk — a subdirectory (id = stem) holding the model plus
    /// its <c>mmproj</c>/draft/shard companions. Re-importing an identical
    /// set is a no-op — a hard link IS the opened file, so later edits to it
    /// are automatically the import's content. A <i>stale</i> import with the
    /// same name is replaced by the new one.
    /// </summary>
    public static LocalModelImportResult Import(string sourcePath, string modelsDir)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Model file not found.", sourcePath);

        // An mmproj sidecar opened directly: it's a projector, not a model —
        // resolve to the main model file sitting next to it.
        if (IsMmprojPath(sourcePath))
            sourcePath = ResolveMainModelForSidecar(sourcePath);

        var files = CollectModelFiles(sourcePath);
        var modelId = DeriveModelId(sourcePath);
        Directory.CreateDirectory(modelsDir);

        if (files.Count == 1)
            return ImportSingleFile(sourcePath, modelId, modelsDir);

        return ImportModelDirectory(files, modelId, modelsDir);
    }

    /// <summary>
    /// The <c>.gguf</c> files that make up the opened model: the main file,
    /// plus the sidecars llama.cpp would auto-match in a models-dir
    /// subdirectory — at most one <c>mmproj</c> (preferring one whose name
    /// carries the model's quant tag, llama.cpp keeps exactly one), at most
    /// one draft companion, and every shard of a multi-shard set. Names keep
    /// their original forms — the scanner's mmproj/draft/shard rules match
    /// on names.
    /// </summary>
    internal static IReadOnlyList<string> CollectModelFiles(string mainFile)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(mainFile))!;
        var stem = Path.GetFileNameWithoutExtension(mainFile);

        var result = new List<string> { mainFile };
        string? mmproj = null;
        string? draft = null;
        var shards = new List<string>();
        var shardBase = ShardSetBase(mainFile);

        foreach (var file in Directory.EnumerateFiles(dir, "*.gguf"))
        {
            var name = Path.GetFileName(file);
            if (string.Equals(name, Path.GetFileName(mainFile), StringComparison.OrdinalIgnoreCase))
                continue;

            if (IsMmprojName(name))
            {
                // One mmproj per import — llama.cpp's scanner keeps exactly
                // one. The first wins, but a quant-matching one (same
                // -Q4_K_M-style tag as the model) displaces a non-matching
                // incumbent, so the right projector pairs with the right
                // model when a folder holds several quants of one family.
                if (mmproj is null ||
                    (QuantTagMatches(name, stem) && !QuantTagMatches(mmproj, stem)))
                {
                    mmproj = file;
                }
                continue;
            }

            if (DraftPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
            {
                // The scanner keeps the first draft sidecar found; order by
                // name for determinism.
                if (draft is null || string.CompareOrdinal(name, Path.GetFileName(draft)) < 0)
                    draft = file;
                continue;
            }

            if (shardBase is not null && IsShardOf(name, shardBase))
                shards.Add(file);
        }

        if (shards.Count > 0) result.AddRange(shards);
        if (draft is not null) result.Add(draft);
        if (mmproj is not null) result.Add(mmproj);
        return result;
    }

    /// <summary>True when the file name contains "mmproj" (llama.cpp's <c>is_mmproj_file</c>).</summary>
    internal static bool IsMmprojPath(string path)
        => Path.GetFileName(path).Contains("mmproj", StringComparison.Ordinal);

    private static bool IsMmprojName(string fileName)
        => fileName.Contains("mmproj", StringComparison.Ordinal);

    /// <summary>
    /// For a shard member (<c>base-00001-of-00002.gguf</c>), the set's base
    /// name; null for single-file models.
    /// </summary>
    internal static string? ShardSetBase(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var marker = stem.IndexOf(ShardMarker, StringComparison.Ordinal);
        if (marker < 0) return null;
        var head = stem[..marker];
        var tail = stem[(marker + ShardMarker.Length)..];
        // "-00001-of-00002" — both sides must be digits for a shard marker.
        var dash = head.LastIndexOf('-');
        if (dash < 0) return null;
        var index = head[(dash + 1)..];
        return index.Length > 0 && index.All(char.IsDigit) && tail.Length > 0 && tail.All(char.IsDigit)
            ? head[..dash]
            : null;
    }

    private static bool IsShardOf(string fileName, string shardBase)
        => ShardSetBase(fileName)?.Equals(shardBase, StringComparison.Ordinal) ?? false;

    /// <summary>
    /// llama.cpp keeps one mmproj per model directory; prefer one whose name
    /// mentions the model's quant (e.g. "-Q4_K_M" appears in both) so the
    /// right projector pairs with the right model when a folder holds
    /// several quants of the same family.
    /// </summary>
    private static bool QuantTagMatches(string mmprojName, string modelStem)
    {
        var tag = ExtractQuantTag(modelStem);
        return tag is not null && mmprojName.Contains(tag, StringComparison.Ordinal);
    }

    /// <summary>The trailing "-Q4_K_M" style quant tag of a model stem, if any.</summary>
    internal static string? ExtractQuantTag(string modelStem)
    {
        var dash = modelStem.LastIndexOf('-');
        if (dash < 0 || dash + 1 >= modelStem.Length) return null;
        var tag = modelStem[(dash + 1)..];
        // Quant tags: Q4_0, Q8_0, IQ4_NL, BF16, F16 … — at least one digit or
        // letter after a last dash; reject plain-English words like "-Instruct".
        return tag.Length is >= 2 and <= 6 &&
               tag.Any(char.IsDigit) &&
               tag.All(c => char.IsAsciiLetterOrDigit(c) || c is '_')
            ? tag
            : null;
    }

    /// <summary>
    /// An mmproj opened directly: the main model file is the unambiguous
    /// non-sidecar <c>.gguf</c> in the same directory. Zero or several → a
    /// clear error naming the thing to open instead.
    /// </summary>
    private static string ResolveMainModelForSidecar(string sidecarPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(sidecarPath))!;
        var candidates = Directory.EnumerateFiles(dir, "*.gguf")
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return !IsMmprojName(name) &&
                       !DraftPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)) &&
                       !name.Contains(ShardMarker);
            })
            .ToList();

        if (candidates.Count == 1)
            return candidates[0];

        throw new IOException(
            $"'{Path.GetFileName(sidecarPath)}' is a multimodal projector sidecar, not a model. " +
            "Open the model file it belongs to (the other .gguf in the same folder) instead.");
    }

    private static LocalModelImportResult ImportSingleFile(
        string sourcePath, string modelId, string modelsDir)
    {
        var targetPath = Path.Combine(modelsDir, modelId + ".gguf");
        var normalizedSource = Path.GetFullPath(sourcePath);
        var normalizedTarget = Path.GetFullPath(targetPath);
        var replacedExisting = false;

        // Opening a file that already lives in the models dir (a re-open of an
        // imported model) — it IS the import.
        if (string.Equals(normalizedSource, normalizedTarget, StringComparison.OrdinalIgnoreCase))
            return new LocalModelImportResult(
                LocalModelImportOutcome.AlreadyPresent, modelId, targetPath, ReplacedExisting: false);

        if (File.Exists(targetPath))
        {
            if (IsSameContent(sourcePath, targetPath))
                return new LocalModelImportResult(
                    LocalModelImportOutcome.AlreadyPresent, modelId, targetPath, ReplacedExisting: false);

            File.Delete(targetPath);
            replacedExisting = true;
        }

        var linked = LinkOrCopy(sourcePath, targetPath, modelsDir);
        return new LocalModelImportResult(
            linked ? LocalModelImportOutcome.Linked : LocalModelImportOutcome.Copied,
            modelId, targetPath, replacedExisting);
    }

    /// <summary>
    /// The directory layout: <c>&lt;modelsDir&gt;/&lt;id&gt;/</c> holding the main
    /// model plus its sidecars, each under its original name — the shape
    /// llama.cpp's scanner auto-matches companions in. The whole directory is
    /// the unit: an identical re-open is a no-op, a stale one is rebuilt.
    /// </summary>
    private static LocalModelImportResult ImportModelDirectory(
        IReadOnlyList<string> files, string modelId, string modelsDir)
    {
        var targetDir = Path.Combine(modelsDir, modelId);
        var replacedExisting = false;

        if (Directory.Exists(targetDir))
        {
            if (files.All(f => DirectoryHasFile(targetDir, f)))
                return new LocalModelImportResult(
                    LocalModelImportOutcome.AlreadyPresent, modelId,
                    Path.Combine(targetDir, Path.GetFileName(files[0])),
                    ReplacedExisting: false, FileCount: files.Count);

            Directory.Delete(targetDir, recursive: true);
            replacedExisting = true;
        }

        Directory.CreateDirectory(targetDir);
        var anyCopied = false;
        foreach (var file in files)
        {
            var target = Path.Combine(targetDir, Path.GetFileName(file));
            anyCopied |= !LinkOrCopy(file, target, targetDir);
        }

        return new LocalModelImportResult(
            anyCopied ? LocalModelImportOutcome.Copied : LocalModelImportOutcome.Linked,
            modelId, Path.Combine(targetDir, Path.GetFileName(files[0])),
            replacedExisting, FileCount: files.Count);
    }

    private static bool DirectoryHasFile(string targetDir, string sourcePath)
    {
        var target = Path.Combine(targetDir, Path.GetFileName(sourcePath));
        return File.Exists(target) && IsSameContent(sourcePath, target);
    }

    /// <summary>
    /// Hard-link <paramref name="sourcePath"/> to
    /// <paramref name="targetPath"/>; on failure (cross-volume, or a
    /// link-hostile file system) copy instead — refusing up front when the
    /// models drive doesn't have the space. True when linked, false when
    /// copied.
    ///
    /// <para>The HF cache stores snapshot files as SYMLINKS into
    /// <c>blobs/</c>. Hard-linking a symlink aliases the link itself: the
    /// import becomes another symlink whose RELATIVE target (e.g.
    /// <c>../../blobs/…</c>) now resolves against the models dir — a broken
    /// 0-byte entry the router's <c>is_regular_file</c> scan skips. So the
    /// source's ultimate link target is resolved first and the REAL file is
    /// what gets linked (also free: the link shares the cache blob's data
    /// rather than duplicating it).</para>
    /// </summary>
    private static bool LinkOrCopy(string sourcePath, string targetPath, string volumeHintDir)
    {
        if (TryCreateHardLink(targetPath, ResolveLinkTarget(sourcePath)))
            return true;

        var needed = new FileInfo(sourcePath).Length;
        if (!DiskSpace.HasEnoughSpace(volumeHintDir, (ulong)needed, out var freeBytes))
            throw new IOException(
                $"Not enough space on the models drive " +
                $"({freeBytes / 1_073_741_824.0:F1} GiB free, {needed / 1_073_741_824.0:F1} GiB needed)");
        File.Copy(sourcePath, targetPath, overwrite: true);
        return false;
    }

    /// <summary>
    /// The ultimate target when <paramref name="path"/> is a symbolic link
    /// (HF-cache snapshots), the path itself otherwise. Broken links resolve
    /// to the original path — the hard link attempt then fails and the copy
    /// fallback takes over.
    /// </summary>
    internal static string ResolveLinkTarget(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        }
        catch
        {
            return path;
        }
    }

    /// <summary>
    /// The model id the router derives from an import: the file name without
    /// its extension (<c>MyModel-Q4_0.gguf</c> → <c>MyModel-Q4_0</c> — unlike
    /// cache models there is no repo:quant split), with a multi-shard set's
    /// <c>-00001-of-00002</c> suffix stripped
    /// (<c>MyModel-00001-of-00002.gguf</c> → <c>MyModel</c>).
    /// </summary>
    internal static string DeriveModelId(string sourcePath)
    {
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        if (ShardSetBase(sourcePath) is { } shardBase)
            stem = shardBase;
        if (string.IsNullOrWhiteSpace(stem))
            throw new ArgumentException(
                $"The file '{Path.GetFileName(sourcePath)}' has no usable name.", nameof(sourcePath));
        return stem;
    }

    /// <summary>
    /// Same-content heuristic for skipping re-imports: a hard link shares the
    /// source's size and last-write time, and so does a copy made from the same
    /// revision — a changed file differs on either.
    /// </summary>
    internal static bool IsSameContent(string sourcePath, string targetPath)
    {
        var source = new FileInfo(sourcePath);
        var target = new FileInfo(targetPath);
        return source.Length == target.Length &&
               source.LastWriteTimeUtc == target.LastWriteTimeUtc;
    }

    /// <summary>CreateHardLinkW, best-effort: false (no throw) on cross-volume etc.</summary>
    private static bool TryCreateHardLink(string targetPath, string sourcePath)
    {
        try
        {
            return CreateHardLinkW(targetPath, sourcePath, IntPtr.Zero);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
