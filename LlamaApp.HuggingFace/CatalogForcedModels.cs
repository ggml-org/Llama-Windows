using LlamaApp.Common;

namespace LlamaApp.HuggingFace;

/// <summary>
/// A forced catalog supplement: a model family the app surfaces even when
/// <c>https://llama.app/v1/catalog.json</c> doesn't carry it yet (e.g. a fresh
/// release). <see cref="Source"/> names the original Hugging Face repo the
/// model was published as (drives branding), while <see cref="TargetUrl"/>
/// points at the HF repo holding the downloadable GGUF files — those two can
/// differ when the GGUF files live in a mirror.
/// </summary>
internal sealed record ForcedModel
{
    /// <summary>Family display name, e.g. "Aion 1.0 Instruct".</summary>
    public required string Name { get; init; }

    /// <summary>Brand/publisher label, e.g. "Microsoft".</summary>
    public required string Brand { get; init; }

    /// <summary>Original HF repo id, e.g. "microsoft/Aion-1.0-Instruct-2.3B".</summary>
    public required string Source { get; init; }

    /// <summary>Absolute huggingface.co URL of the HF repo holding the GGUF files.</summary>
    public required string TargetUrl { get; init; }

    /// <summary>One-line family description shown in the family row/details.</summary>
    public required string Description { get; init; }

    /// <summary>License label, e.g. "MIT".</summary>
    public required string License { get; init; }

    /// <summary>Human-readable parameter count, e.g. "2.3B".</summary>
    public required string Params { get; init; }

    /// <summary>Declared inputs; defaults to text-only.</summary>
    public IReadOnlyList<string> InputModalities { get; init; } = ["text"];

    /// <summary>Whether the family is surfaced as featured.</summary>
    public bool Featured { get; init; }
}

/// <summary>
/// The forced catalog supplements, plus the pure helpers that turn a
/// definition into a materialized <see cref="ModelFamily"/> (one build per
/// quantized GGUF file the target repo actually reports) and decide whether the remote
/// catalog already covers it. See <see cref="Catalog.FetchFamiliesAsync"/>.
/// </summary>
internal static class CatalogForcedModels
{
    /// <summary>
    /// Microsoft's on-device SLM — the catalog doesn't list it yet, so the app
    /// supplements it from the GGUF mirror
    /// (<c>https://huggingface.co/ggml-org/Aion-1.0-Instruct-2.3B</c>) while
    /// branding it with the original repo
    /// (<c>https://huggingface.co/microsoft/Aion-1.0-Instruct-2.3B</c>).
    /// Declared before <see cref="All"/> — static initializers run in
    /// textual order.
    /// </summary>
    private static readonly ForcedModel Aion = new()
    {
        Name = "Aion 1.0 Instruct",
        Brand = "Microsoft",
        Source = "microsoft/Aion-1.0-Instruct-2.3B",
        TargetUrl = "https://huggingface.co/ggml-org/Aion-1.0-Instruct-2.3B",
        Description = "Microsoft's 2.3B on-device small language model for summarization, rewriting and chat, with a 32K context window.",
        License = "MIT",
        Params = "2.3B",
        InputModalities = ["text"],
        Featured = true,
    };

    /// <summary>
    /// The supplements in the order they are appended to the remote catalog.
    /// </summary>
    internal static IReadOnlyList<ForcedModel> All { get; } = [Aion];

    /// <summary>
    /// Parses the target repo id (<c>owner/name</c>) out of an absolute
    /// huggingface.co URL. Returns null when the URL isn't an absolute
    /// huggingface.co repo URL — a supplement with an unparseable target can't
    /// be materialized and must not surface.
    /// </summary>
    internal static string? TryGetTargetRepo(string? targetUrl)
    {
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            return null;
        if (!uri.Host.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase))
            return null;

        // "/owner/name" (a trailing slash is tolerated) — anything deeper is
        // a file/tree page, not a repo URL.
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.None);
        return segments.Length == 2 && segments.All(s => s.Length > 0)
            ? $"{segments[0]}/{segments[1]}"
            : null;
    }

    /// <summary>
    /// Whether the given families already carry this supplement: a family
    /// with the same name (case-insensitive) or any build already downloading
    /// from the target repo. Remote entries win — a covered supplement is
    /// never re-appended.
    /// </summary>
    internal static bool IsAlreadyPresent(IEnumerable<ModelFamily> families, ForcedModel model)
    {
        var targetRepo = TryGetTargetRepo(model.TargetUrl);
        return families.Any(family =>
            string.Equals(family.Name, model.Name, StringComparison.OrdinalIgnoreCase)
            || (targetRepo != null && family.Sizes.Any(size => size.Builds.Any(
                build => string.Equals(build.Repo, targetRepo, StringComparison.OrdinalIgnoreCase)))));
    }

    /// <summary>
    /// Materializes a supplement from the target repo's actual file listing:
    /// one build per GGUF file with an extractable quant label, in file-listing
    /// order. Quant labels are never invented — a file without one is skipped.
    /// Sizes come from the listing as-is: an unreported size (0, which
    /// <see cref="HubClient.RepoFile"/> uses for unknown) keeps its 0
    /// <c>SizeBytes</c> and yields a blank display size rather than an invented
    /// one. Returns null when nothing usable remains (empty/unusable target).
    /// </summary>
    internal static ModelFamily? Materialize(ForcedModel model, string targetRepo, IReadOnlyList<HubClient.RepoFile> files)
    {
        var builds = new List<ModelFamilyBuild>(files.Count);
        foreach (var file in files)
        {
            if (!file.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                continue;

            var quant = Catalog.ExtractQuant(Path.GetFileNameWithoutExtension(file.Path));
            // No quant label → no truthful download entry; skip rather than
            // invent one. An unreported size (0) is fine: the download target
            // exists even when the Hub doesn't report its blob size.
            if (quant is null)
                continue;

            builds.Add(new ModelFamilyBuild
            {
                Quant = quant,
                Size = file.SizeBytes == 0 ? "" : Catalog.FormatBytes(file.SizeBytes),
                SizeBytes = file.SizeBytes,
                Repo = targetRepo,
            });
        }

        if (builds.Count == 0)
            return null;

        return new ModelFamily
        {
            Name = model.Name,
            Brand = model.Brand,
            Source = model.Source,
            Description = model.Description,
            License = model.License,
            Featured = model.Featured,
            Sizes =
            [
                new ModelFamilySize
                {
                    Name = $"{model.Name} {model.Params}",
                    Params = model.Params,
                    InputModalities = model.InputModalities,
                    Builds = builds,
                }
            ],
        };
    }

    /// <summary>
    /// Materializes a supplement from its target repo's live file listing.
    /// Returns null (after logging) when the target URL is invalid, the target
    /// repo is unavailable (missing/gated/network) or has no usable GGUF files
    /// — the base catalog must never fail because of a supplement. Cancellation
    /// is preserved.
    /// </summary>
    internal static async Task<ModelFamily?> MaterializeAsync(ForcedModel model, CancellationToken cancel = default)
    {
        var targetRepo = TryGetTargetRepo(model.TargetUrl);
        if (targetRepo is null)
        {
            Log.Warn($"forced model '{model.Name}': invalid target URL '{model.TargetUrl}'");
            return null;
        }

        List<HubClient.RepoFile> files;
        try
        {
            files = await new HubClient(null).ListRepoFileSizesAsync(targetRepo, cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, $"forced model '{model.Name}': target '{targetRepo}' unavailable");
            return null;
        }

        var family = Materialize(model, targetRepo, files);
        if (family is null)
            Log.Warn($"forced model '{model.Name}': target '{targetRepo}' has no usable GGUF builds");
        return family;
    }

    /// <summary>
    /// Appends every supplement that is missing from <paramref name="families"/>
    /// and materializes. Existing entries (remote or already appended) win and
    /// are left untouched.
    /// </summary>
    internal static async Task AppendMissingAsync(List<ModelFamily> families, CancellationToken cancel = default)
        => await AppendMissingAsync(families, MaterializeAsync, cancel);

    /// <summary>
    /// Same as <see cref="AppendMissingAsync(List{ModelFamily},CancellationToken)"/>
    /// with the materializer injected so tests can drive the append/suppress
    /// behavior without hitting the Hub.
    /// </summary>
    internal static async Task AppendMissingAsync(
        List<ModelFamily> families,
        Func<ForcedModel, CancellationToken, Task<ModelFamily?>> materialize,
        CancellationToken cancel = default)
    {
        foreach (var model in All)
        {
            if (IsAlreadyPresent(families, model))
                continue;

            var family = await materialize(model, cancel);
            if (family is not null)
                families.Add(family);
        }
    }
}
