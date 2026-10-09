namespace LlamaApp.Views;

using System.Text.Json;

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
    public static string BuildCurlCommand(string serverAddress, int serverPort, string serverModelId)
    {
        // Serialize, never interpolate: the id can come from the llama.app
        // catalog, Hub search, or an adopted server, and a quote/backslash/
        // newline in it must not corrupt the body or inject extra JSON fields.
        var body = JsonSerializer.Serialize(new
        {
            model = serverModelId,
            messages = new[] { new { role = "user", content = "Hello, how are you?" } },
        });

        // Single-quote the payload for a POSIX shell so an id containing
        // $(...) or backticks is never command-substituted on paste; an
        // embedded single quote is escaped the POSIX way ('\'').
        var quotedBody = "'" + body.Replace("'", "'\\''") + "'";

        return $"curl http://{serverAddress}:{serverPort}/v1/chat/completions " +
               "-H \"Content-Type: application/json\" " +
               $"-d {quotedBody}";
    }

    /// <summary>
    /// The running server's WebUI URL scoped to a model
    /// (<c>?model=&lt;id&gt;</c> makes the server auto-load it) — the same URL
    /// the Available row's open glyph launches.
    /// </summary>
    public static string BuildWebUiUrl(string serverAddress, int serverPort, string serverModelId)
        => $"http://{serverAddress}:{serverPort}?model={Uri.EscapeDataString(serverModelId)}";
}
