using System.Runtime.InteropServices;
using LlamaApp.Common;

namespace LlamaApp.Llama;

/// <summary>How <see cref="LocalModelImport.Import"/> materialized the file.</summary>
public enum LocalModelImportOutcome
{
    /// <summary>A new hard link was created in the models folder.</summary>
    Linked,

    /// <summary>The file was copied (hard link impossible — cross-volume, or a
    /// file system without link support).</summary>
    Copied,

    /// <summary>Nothing to do — an identical file is already imported.</summary>
    AlreadyPresent,
}

/// <summary>The result of importing one opened model file.</summary>
public sealed record LocalModelImportResult(
    LocalModelImportOutcome Outcome,
    string ModelId,
    string TargetPath,
    bool ReplacedExisting);

/// <summary>
/// Brings a model file the user "opened with" Llama into the llama server's
/// <c>--models-dir</c>, where the router discovers it as a loadable model.
///
/// <para><b>Why import at all:</b> the router's <c>POST /models/load</c> only
/// resolves ids in its model registry (HF-cache repos or files under
/// <c>--models-dir</c>) — an arbitrary filesystem path answers
/// <c>404 File Not Found</c>. A <c>.gguf</c> directly in the models dir gets the
/// id <c><filename-without-.gguf></c> (llama.cpp's
/// <c>load_from_models_dir</c>), and the registry rescans on every
/// <c>GET /models</c> — so link-then-refresh needs no server restart.</para>
///
/// <para><b>Hard link first:</b> opening a 40 GB model must not duplicate 40 GB
/// of disk. <see cref="File.CreateHardLink"/> needs no elevation and is free on
/// the same volume (Downloads → models dir is the common case); the copy
/// fallback covers other volumes.</para>
/// </summary>
public static class LocalModelImport
{
    /// <summary>
    /// Links (or, failing that, copies) <paramref name="sourcePath"/> into
    /// <paramref name="modelsDir"/> under a name the router scans. The target
    /// name always carries a lower-case <c>.gguf</c> extension — llama.cpp's
    /// scan is case-sensitive and skips <c>MODEL.GGUF</c>. Re-importing a file
    /// that's already there (same size + last-write time) is a no-op — a hard
    /// link IS the opened file, so later edits to it are automatically the
    /// import's content. A <i>stale</i> target with the same name (an old
    /// cross-volume copy, or a hand-placed file) is replaced by the new one.
    /// </summary>
    public static LocalModelImportResult Import(string sourcePath, string modelsDir)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Model file not found.", sourcePath);

        var modelId = DeriveModelId(sourcePath);
        Directory.CreateDirectory(modelsDir);

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

            // Same name, different content — the file was re-opened after being
            // replaced/updated. Drop the stale import so the new one lands.
            File.Delete(targetPath);
            replacedExisting = true;
        }

        var linked = TryCreateHardLink(targetPath, sourcePath);
        if (!linked)
        {
            // Cross-volume (or a link-hostile file system): a copy is the only
            // way — refuse up front instead of filling the disk halfway through
            // a multi-GB model.
            var needed = new FileInfo(sourcePath).Length;
            if (!DiskSpace.HasEnoughSpace(modelsDir, (ulong)needed, out var freeBytes))
                throw new IOException(
                    $"Not enough space on the models drive " +
                    $"({freeBytes / 1_073_741_824.0:F1} GiB free, {needed / 1_073_741_824.0:F1} GiB needed)");
            File.Copy(sourcePath, targetPath, overwrite: true);
        }

        return new LocalModelImportResult(
            linked ? LocalModelImportOutcome.Linked : LocalModelImportOutcome.Copied,
            modelId, targetPath, replacedExisting);
    }

    /// <summary>
    /// The model id the router derives from an imported file: the file name
    /// without its extension (<c>MyModel-Q4_0.gguf</c> → <c>MyModel-Q4_0</c> —
    /// unlike cache models there is no repo:quant split).
    /// </summary>
    internal static string DeriveModelId(string sourcePath)
    {
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
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
