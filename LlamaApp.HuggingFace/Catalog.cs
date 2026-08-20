using System.Globalization;
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
    /// Fetches the catalog and returns the family → size → build hierarchy as
    /// public <see cref="ModelFamily"/> objects — the shape the browse list
    /// renders (one row per family, sizes and quants a click deeper).
    /// </summary>
    public static async Task<IReadOnlyList<ModelFamily>> FetchFamiliesAsync(CancellationToken cancel = default)
    {
        var families = await FetchCatalogFamiliesAsync(cancel);
        if (families.Length == 0)
            return [];

        return families.Select(ToModelFamily).ToList();
    }

    /// <summary>
    /// Fetches the catalog and returns one <see cref="Repository"/> per build
    /// (quant) across all families and sizes.
    /// </summary>
    public static async Task<IReadOnlyList<Repository>> FetchAsync(CancellationToken cancel = default)
    {
        var families = await FetchFamiliesAsync(cancel);
        if (families.Count == 0)
            return [];

        return Flatten(families);
    }

    /// <summary>
    /// Downloads and deserializes catalog.json into the internal DTOs. Shared
    /// by both public fetches so a single HTTP request feeds both shapes.
    /// </summary>
    private static async Task<CatalogFamily[]> FetchCatalogFamiliesAsync(CancellationToken cancel)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Llama/1.0");
        client.Timeout = TimeSpan.FromSeconds(15);

        using var response = await client.GetAsync(CatalogUrl, cancel);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(cancel);
        return await JsonSerializer.DeserializeAsync<CatalogFamily[]>(stream, JsonOptions, cancel) ?? [];
    }

    /// <summary>Maps one internal DTO family onto the public <see cref="ModelFamily"/> shape.</summary>
    private static ModelFamily ToModelFamily(CatalogFamily family) => new()
    {
        Name = family.Name,
        Brand = family.Brand,
        Description = family.Description,
        License = family.License,
        Featured = family.Featured,
        Sizes = family.Sizes.Select(size => new ModelFamilySize
        {
            Name = size.Name,
            Params = size.Params,
            Vision = size.Vision,
            Builds = size.Builds.Select(build => new ModelFamilyBuild
            {
                Quant = build.Quant,
                Size = build.Size,
                SizeBytes = build.SizeBytes,
                Repo = build.Repo,
            }).ToList(),
        }).ToList(),
    };

    /// <summary>
    /// Flattens the family → size → build hierarchy into a flat list of
    /// <see cref="Repository"/> records, one per build (quant).
    /// </summary>
    public static List<Repository> Flatten(IReadOnlyList<ModelFamily> families)
    {
        var repos = new List<Repository>(families.Count * 4);
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

    /// <summary>
    /// Flattens the nested family → size → build structure into a flat list of
    /// <see cref="Repository"/> records, one per build (quant). Internal for
    /// unit tests.
    /// </summary>
    internal static List<Repository> Flatten(CatalogFamily[] families)
        => Flatten(families.Select(ToModelFamily).ToList());

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

    /// <summary>
    /// Formats a byte count as a human-readable size string, e.g. 2526080992 → "2.5 GB".
    /// Internal for unit tests.
    /// </summary>
    /// <remarks>
    /// Decimal units (1 GB = 1e9 B), whole KB/MB and one fractional digit at GB/TB.
    /// This mirrors the pre-formatted <c>size</c> strings in catalog.json — and must:
    /// <see cref="FetchLocalAsync"/> substitutes this value into the very same
    /// <see cref="Repository.Size"/> field the catalog fills, so a different base
    /// would make an installed model's size differ from its Discover listing
    /// (12109566560 B reads "12.1 GB" decimal but "11.3 GB" binary). Invariant
    /// culture for the same reason: the catalog strings are period-separated.
    /// </remarks>
    internal static string FormatBytes(ulong bytes)
    {
        if (bytes >= 1_000_000_000_000)
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_000_000_000_000.0:0.#} TB");
        if (bytes >= 1_000_000_000)
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_000_000_000.0:0.#} GB");
        if (bytes >= 1_000_000)
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_000_000.0:0} MB");
        if (bytes >= 1_000)
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_000.0:0} KB");
        return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
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
