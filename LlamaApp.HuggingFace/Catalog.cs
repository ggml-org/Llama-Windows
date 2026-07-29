using System.Text.Json;
using System.Text.Json.Serialization;
using LlamaApp.Common;

namespace LlamaApp.HuggingFace;

/// <summary>
/// Fetches and parses the remote model catalog from
/// <c>https://llama.app/v1/catalog.json</c>, flattening the family → size →
/// build hierarchy into a flat set of <see cref="Repository"/> objects — one
/// per downloadable build (quant). Also implements <see cref="IModelSource"/>
/// so it can plug into the generic catalog layer.
/// </summary>
public sealed class Catalog : IModelSource
{
    /// <summary>Remote catalog endpoint.</summary>
    private static readonly Uri CatalogUrl = new("https://llama.app/v1/catalog.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Fetches the catalog and returns one <see cref="Repository"/> per build
    /// (quant) across all families and sizes.
    /// </summary>
    public static async Task<IReadOnlyList<Repository>> FetchAsync(CancellationToken cancel = default)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LlamaApp/1.0");
        client.Timeout = TimeSpan.FromSeconds(15);

        using var response = await client.GetAsync(CatalogUrl, cancel);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(cancel);
        var families = await JsonSerializer.DeserializeAsync<CatalogFamily[]>(stream, JsonOptions, cancel);
        if (families is null || families.Length == 0)
            return [];

        return Flatten(families);
    }

    /// <summary>
    /// Flattens the nested family → size → build structure into a flat list of
    /// <see cref="Repository"/> records, one per build (quant). Internal for
    /// unit tests.
    /// </summary>
    internal static List<Repository> Flatten(CatalogFamily[] families)
    {
        var repos = new List<Repository>(families.Length * 4);
        repos.AddRange(
            from family in families
            from size in family.Sizes
            from build in size.Builds
            select new Repository
            {
                Name = build.Repo,
                Description = family.Description,
                License = family.License,
                Parameters = size.Params,
                Size = build.Size,
                Vision = size.Vision,
                DisplayName = size.Name,
                Brand = family.Brand,
                Quant = build.Quant,
                SizeBytes = build.SizeBytes,
                Featured = family.Featured,
            }
        );

        return repos;
    }

    // ---- IModelSource ----

    /// <summary>Returns all catalog (remote) models as <typeparamref name="T"/>.</summary>
    public async Task<ICollection<T>> GetModelsAsync<T>() where T : IModel
    {
        var repos = await FetchAsync();
        return repos.Cast<T>().ToList();
    }

    /// <summary>
    /// Returns local (already-downloaded) models by scanning the HF cache.
    /// </summary>
    public async Task<ICollection<T>> GetLocalModelsAsync<T>() where T : IModel
    {
        // The cache directory defaults to the standard HF hub layout; callers
        // can override it via Settings. Here we use the default — MainWindow
        // passes the user-configured path to FetchLocalAsync directly.
        var repos = await FetchLocalAsync(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "huggingface", "hub")
        );
        return repos.Cast<T>().ToList();
    }

    /// <summary>
    /// Scans the local Hugging Face cache (see <c>Settings.CacheDirectory</c>)
    /// for downloaded GGUF models and returns one <see cref="Repository"/> per
    /// found build. Each entry is enriched with catalog metadata (display name,
    /// params, license, …) when its repo id matches a remote catalog entry;
    /// otherwise it's surfaced with basic info derived from the repo id and the
    /// on-disk file size.
    /// </summary>
    private static async Task<IReadOnlyList<Repository>> FetchLocalAsync(string cacheDirectory, CancellationToken cancel = default)
    {
        if (!Directory.Exists(cacheDirectory))
            return [];

        // Build a repo-id → catalog-entry lookup so local models inherit rich
        // metadata (display name, params, license, …) when available. Failures
        // here (offline, parse error) just mean we fall back to cache-derived info.
        Dictionary<string, Repository>? catalogLookup = null;
        try
        {
            var remote = await FetchAsync(cancel);
            // The catalog is flattened per-quant, so a repo can appear more than
            // once — GroupBy keeps a single entry (ToDictionary would throw on
            // the duplicate keys and silently skip enrichment).
            catalogLookup = remote
                .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // Offline or parse error — proceed with cache-only info.
            Log.Warn(ex, "catalog fetch for local enrichment failed");
        }

        var results = new List<Repository>();
        foreach (var modelDir in Directory.EnumerateDirectories(cacheDirectory, "models--*"))
        {
            cancel.ThrowIfCancellationRequested();

            var dirName = Path.GetFileName(modelDir);
            // "models--{org}--{repo}" → "{org}/{repo}"
            var repoId = dirName["models--".Length..].Replace("--", "/");

            var snapshotsDir = Path.Combine(modelDir, "snapshots");
            if (!Directory.Exists(snapshotsDir))
                continue;

            // Each snapshot is a commit hash; pick the first that has GGUF files.
            foreach (var snapshotDir in Directory.EnumerateDirectories(snapshotsDir))
            {
                cancel.ThrowIfCancellationRequested();

                foreach (var ggufFile in Directory.EnumerateFiles(snapshotDir, "*.gguf"))
                {
                    cancel.ThrowIfCancellationRequested();

                    var sizeBytes = TryGetFileSize(ggufFile);
                    var fileName = Path.GetFileNameWithoutExtension(ggufFile);

                    // Match against the remote catalog by repo id for metadata.
                    if (catalogLookup != null &&
                        catalogLookup.TryGetValue(repoId, out var matched))
                    {
                        results.Add(new Repository
                        {
                            Name = matched.Name,
                            Description = matched.Description,
                            License = matched.License,
                            Parameters = matched.Parameters,
                            // Prefer the actual on-disk size over the catalog's
                            // (which is the download size, not necessarily what landed).
                            Size = sizeBytes.HasValue ? FormatBytes(sizeBytes.Value) : matched.Size,
                            Vision = matched.Vision,
                            DisplayName = matched.DisplayName,
                            Brand = matched.Brand,
                            Quant = matched.Quant,
                            SizeBytes = sizeBytes ?? matched.SizeBytes,
                            Featured = matched.Featured,
                        });
                    }
                    else
                    {
                        // Not in the catalog — surface with basic info.
                        results.Add(new Repository
                        {
                            Name = repoId,
                            Description = "",
                            License = "Unknown",
                            Parameters = "",
                            Size = sizeBytes.HasValue ? FormatBytes(sizeBytes.Value) : "",
                            Vision = false,
                            DisplayName = fileName,
                            Brand = repoId.Split('/', StringSplitOptions.None).FirstOrDefault(),
                            Quant = ExtractQuant(fileName),
                            SizeBytes = sizeBytes ?? 0,
                        });
                    }
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Returns the size of a file, following symlinks/hardlinks to the actual
    /// blob. Returns null if the file can't be read (e.g. it's a broken symlink
    /// to a not-yet-downloaded blob).
    /// </summary>
    private static ulong? TryGetFileSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            // On Windows, HF cache snapshot files can be symlinks to blobs;
            // resolve the link target to get the real size. If the link is
            // broken (download incomplete), Length throws — return null.
            return info.LinkTarget != null
                ? (ulong?)new FileInfo(info.LinkTarget).Length
                : (ulong)info.Length;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Extracts a quant label from a GGUF filename, e.g. "model-Q4_K_M.gguf" → "Q4_K_M". Internal for unit tests.</summary>
    internal static string? ExtractQuant(string fileName)
    {
        var parts = fileName.Split('-');
        return parts.Select(p => p.Trim()).FirstOrDefault(
            trimmed => trimmed.Length > 1 && (trimmed[0] == 'Q' || trimmed.StartsWith("mxfp", StringComparison.OrdinalIgnoreCase))
        );
    }

    /// <summary>Formats a byte count as a human-readable size string, e.g. 2526080992 → "2.4 GB". Internal for unit tests.</summary>
    internal static string FormatBytes(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{size:0.#} {units[unit]}";
    }

    // ---- JSON DTOs matching the catalog.json schema ----

    internal sealed class CatalogFamily
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("brand")] public string Brand { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("details")] public string Details { get; set; } = "";
        [JsonPropertyName("released")] public string Released { get; set; } = "";
        [JsonPropertyName("license")] public string License { get; set; } = "";
        [JsonPropertyName("featured")] public bool Featured { get; set; }
        [JsonPropertyName("sizes")] public CatalogSize[] Sizes { get; set; } = [];
    }

    internal sealed class CatalogSize
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("params")] public string Params { get; set; } = "";
        [JsonPropertyName("vision")] public bool Vision { get; set; }
        [JsonPropertyName("builds")] public CatalogBuild[] Builds { get; set; } = [];
    }

    internal sealed class CatalogBuild
    {
        [JsonPropertyName("quant")] public string Quant { get; set; } = "";
        [JsonPropertyName("size")] public string Size { get; set; } = "";
        [JsonPropertyName("sizeBytes")] public ulong SizeBytes { get; set; }
        [JsonPropertyName("repo")] public string Repo { get; set; } = "";
    }
}
