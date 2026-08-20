namespace LlamaApp.Llama;

/// <summary>
/// Resolves a server model id (<c>owner/repo:quant</c>) to its on-disk GGUF
/// when the server doesn't report a path — the router <c>/models</c> payload
/// carries no <c>path</c> field, so without this the details view could never
/// read a model's GGUF header. Probes the cache layouts llama.cpp downloads
/// into, in order:
///
/// <list type="bullet">
/// <item>the Hugging Face hub cache:
/// <c>{root}/models--{owner}--{repo}/snapshots/{sha}/{file}.gguf</c>
/// (current llama.cpp resolves <c>--hf-repo</c> through the hub cache),</item>
/// <item>llama.cpp's own flat cache:
/// <c>{root}/{owner}_{repo}_{file}.gguf</c> (older/docker-style pulls).</item>
/// </list>
///
/// <para>The quant selects among the repo's files by filename match
/// (<c>…-Q4_K_M.gguf</c>, multi-shard <c>-00001-of-…</c> included); vision
/// projector files (<c>mmproj</c>) never match. Pure filesystem probing — no
/// server round-trip — so it also works while the server is down.</para>
/// </summary>
public static class ModelFileLocator
{
    /// <summary>
    /// A located model: the shard to read the GGUF header from
    /// (<see cref="PrimaryFilePath"/>) and the summed size of all its shards
    /// (<see cref="TotalSizeBytes"/>).
    /// </summary>
    public sealed record LocatedModel(string PrimaryFilePath, long TotalSizeBytes);

    /// <summary>Probes the machine's default cache roots (env overrides first).</summary>
    public static LocatedModel? TryFind(string serverModelId)
        => TryFind(serverModelId, DefaultCacheRoots());

    /// <summary>
    /// Probes <paramref name="cacheRoots"/> (each checked under both layouts)
    /// for the model's GGUF. Returns <c>null</c> when no root holds it.
    /// </summary>
    public static LocatedModel? TryFind(string serverModelId, IReadOnlyList<string> cacheRoots)
    {
        if (string.IsNullOrWhiteSpace(serverModelId)) return null;

        // The quant separator is the LAST colon after the repo slash — repo
        // ids themselves contain no colon, but a stray one in the owner part
        // must not be mistaken for it.
        var slash = serverModelId.IndexOf('/');
        var colon = serverModelId.LastIndexOf(':');
        var repo = colon > slash ? serverModelId[..colon] : serverModelId;
        var quant = colon > slash ? serverModelId[(colon + 1)..] : null;
        if (repo.Length == 0) return null;

        var hubDirName = "models--" + repo.Replace("/", "--");
        var flatPrefix = repo.Replace('/', '_') + "_";

        foreach (var root in cacheRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;

            // Hub layout: models--owner--repo/snapshots/{sha}/*.gguf — one
            // snapshot is the live one; the first with a match wins.
            var found = ProbeHubLayout(Path.Combine(root, hubDirName, "snapshots"), quant);
            if (found is not null) return found;

            // Flat layout: owner_repo_*.gguf directly under the root.
            found = ProbeFiles(SafeEnumerate(root, flatPrefix + "*.gguf"), quant);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>
    /// The candidate cache roots, most specific first: llama.cpp's own
    /// override, the HF hub overrides, then the observed platform defaults
    /// (hub cache under the user profile; llama.cpp's flat cache).
    /// </summary>
    public static IReadOnlyList<string> DefaultCacheRoots()
    {
        var roots = new List<string>();
        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) &&
                !roots.Contains(path, StringComparer.OrdinalIgnoreCase))
                roots.Add(path);
        }

        Add(Environment.GetEnvironmentVariable("LLAMA_CACHE"));
        Add(Environment.GetEnvironmentVariable("HF_HUB_CACHE"));
        var hfHome = Environment.GetEnvironmentVariable("HF_HOME");
        if (!string.IsNullOrWhiteSpace(hfHome)) Add(Path.Combine(hfHome, "hub"));

        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add(Path.Combine(user, ".cache", "huggingface", "hub"));
        Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "llama.cpp"));
        Add(Path.Combine(user, ".cache", "llama.cpp"));
        return roots;
    }

    private static LocatedModel? ProbeHubLayout(string snapshotsDir, string? quant)
    {
        foreach (var snapshot in SafeEnumerateDirs(snapshotsDir))
        {
            var found = ProbeFiles(SafeEnumerate(snapshot, "*.gguf"), quant);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>
    /// Picks the matching files out of <paramref name="files"/>: the quant
    /// must appear in the filename (a bare repo id matches any model file),
    /// mmproj vision projectors never count. Sorted so shard 00001 is the
    /// primary; the size sums every shard.
    /// </summary>
    private static LocatedModel? ProbeFiles(IEnumerable<string> files, string? quant)
    {
        var matches = files
            .Where(f => Matches(Path.GetFileName(f), quant))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (matches.Count == 0) return null;

        long size = 0;
        foreach (var f in matches)
            size += FileSize(f);
        return new LocatedModel(matches[0], size);
    }

    /// <summary>
    /// The file's size in bytes, following symlinks: HF hub cache snapshots
    /// are reparse points into the blobs dir, and <see cref="FileInfo.Length"/>
    /// on the link itself reports 0 — the target must be resolved explicitly
    /// (a few levels, in case of chained links). 0 on any failure.
    /// </summary>
    private static long FileSize(string path)
    {
        try
        {
            for (var depth = 0; depth < 3; depth++)
            {
                var info = new FileInfo(path);
                if (info.LinkTarget is not { } target) return info.Length;
                path = Path.GetFullPath(Path.Combine(info.Directory!.FullName, target));
            }
        }
        catch { /* unreadable — treated as unknown size */ }
        return 0;
    }

    private static bool Matches(string name, string? quant)
    {
        if (name.Contains("mmproj", StringComparison.OrdinalIgnoreCase)) return false;
        return quant is null || name.Contains(quant, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SafeEnumerate(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern).ToList(); }
        catch { return []; }
    }

    private static IEnumerable<string> SafeEnumerateDirs(string dir)
    {
        try { return Directory.EnumerateDirectories(dir).ToList(); }
        catch { return []; }
    }
}
