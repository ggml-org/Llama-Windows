namespace LlamaApp.Views;

/// <summary>
/// Builds the sample API request shown by the model details view's "Build an
/// API request" action. Pure string building, kept separate from the ViewModel
/// so the exact request shape stays unit-testable — it must match what the
/// running llama server actually implements (OpenAI-compatible
/// <c>POST /v1/chat/completions</c>, the same endpoint
/// <see cref="Llama.LlamaManager.StreamChatAsync"/> uses).
/// </summary>
public static class ApiRequestPresentation
{
    /// <summary>
    /// A ready-to-run curl command against the local server's chat endpoint
    /// for <paramref name="serverModelId"/> (the canonical <c>repo:quant</c>
    /// id — the <c>model</c> field the server requires).
    /// </summary>
    public static string BuildCurlCommand(int serverPort, string serverModelId)
    {
        var body = "{\"model\":\"" + serverModelId + "\"," +
            "\"messages\":[{\"role\":\"user\",\"content\":\"Hello, how are you?\"}]}";
        return $"curl http://localhost:{serverPort}/v1/chat/completions " +
               $"-H \"Content-Type: application/json\" " +
               $"-d \"{body.Replace("\"", "\\\"")}\"";
    }

    /// <summary>
    /// The running server's WebUI URL scoped to a model
    /// (<c>?model=&lt;id&gt;</c> makes the server auto-load it) — the same URL
    /// the Available row's open glyph launches.
    /// </summary>
    public static string BuildWebUiUrl(int serverPort, string serverModelId)
        => $"http://localhost:{serverPort}?model={Uri.EscapeDataString(serverModelId)}";
}
