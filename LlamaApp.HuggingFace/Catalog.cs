using System.Net.Http.Headers;
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
    /// <see cref="Repository"/> records, one per build (quant).
    /// </summary>
    private static List<Repository> Flatten(CatalogFamily[] families)
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
    /// Local (already-downloaded) models. Not yet implemented — returns an
    /// empty collection until the Hugging Face cache scan is wired up.
    /// </summary>
    public Task<ICollection<T>> GetLocalModelsAsync<T>() where T : IModel
    {
        ICollection<T> empty = Array.Empty<T>();
        return Task.FromResult(empty);
    }

    // ---- JSON DTOs matching the catalog.json schema ----

    private sealed class CatalogFamily
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

    private sealed class CatalogSize
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("params")] public string Params { get; set; } = "";
        [JsonPropertyName("vision")] public bool Vision { get; set; }
        [JsonPropertyName("builds")] public CatalogBuild[] Builds { get; set; } = [];
    }

    private sealed class CatalogBuild
    {
        [JsonPropertyName("quant")] public string Quant { get; set; } = "";
        [JsonPropertyName("size")] public string Size { get; set; } = "";
        [JsonPropertyName("sizeBytes")] public ulong SizeBytes { get; set; }
        [JsonPropertyName("repo")] public string Repo { get; set; } = "";
    }
}
