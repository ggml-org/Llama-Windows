namespace LlamaApp.Common;

/// <summary>
/// A model entry — locally installed or available for download.
/// </summary>
public interface IModel
{
    string Name { get; }

    /// <summary>
    /// The canonical model id the llama server uses for its router API
    /// (<c>POST /models</c>, <c>/models/load</c>, <c>/models/sse</c>): the HF
    /// repo id with an optional <c>:&lt;quant&gt;</c> suffix, e.g.
    /// <c>ggml-org/gemma-3-270m-it-qat-GGUF:Q4_0</c>. <c>/models/load</c>
    /// requires the quant suffix, and <c>/models</c> (download) should carry it
    /// too — a bare repo id leaves the quant to the server's own default
    /// resolution, which can fetch a different variant than the user picked.
    /// This is the form passed to the server. <see cref="Name"/> stays the
    /// bare repo id for local cache-path matching.
    /// </summary>
    string ServerModelId { get; }

    string Description { get; }
    string License { get; }

    /// <summary>Human-readable parameter count, e.g. "20B", "270M", "E4B".</summary>
    string Parameters { get; }

    /// <summary>Human-readable size, e.g. "12.1 GB".</summary>
    string Size { get; }

    bool Vision { get; }
}