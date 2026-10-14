namespace LlamaApp.Common;

/// <summary>
/// Policy for the free-form "custom serve arguments" setting. llama.cpp honors
/// the last occurrence of a repeated flag, and the custom tokens are appended
/// after the app's own — so without this an entry like <c>--host 0.0.0.0</c>
/// would silently override the validated listen address (and the Settings UI
/// would keep claiming "Localhost"). The app owns the flags that define the
/// bind, the auth key, and the per-model preset, so those are reserved.
///
/// <para>Also provides redaction for the launch log: some llama.cpp flags take
/// a secret value, and the raw custom text is otherwise echoed verbatim into
/// <c>%LOCALAPPDATA%\Llama\logs</c>.</para>
/// </summary>
public static class ServeArgumentPolicy
{
    /// <summary>
    /// Flags the app sets itself and refuses in custom arguments. <c>--host</c>
    /// and <c>--port</c> define the reachable endpoint; <c>--api-key</c> the
    /// app-managed auth; <c>--models-preset</c> the per-model context file;
    /// <c>--models-dir</c> the "Open With" local-models folder; the
    /// <c>--ssl*</c> flags change the transport (and would break the
    /// loopback client's assumptions).
    ///
    /// <para>Also the secret-bearing flags: custom arguments become process
    /// argv, where a value leaks into command-line auditing (Event Log 4688),
    /// EDR telemetry, crash dumps and process listings. The app's own
    /// channels exist precisely to keep secrets off argv — the HF token
    /// travels as the server's HF_TOKEN environment variable, the API key as
    /// <c>--api-key-file</c>, the key password as <c>--ssl-key-pass</c>'s
    /// file-based counterpart — so a custom-args copy of any of them is
    /// refused (matching <see cref="SecretFlags"/>).</para>
    /// </summary>
    public static readonly IReadOnlyList<string> ReservedFlags = new[]
    {
        "--host",
        "--port",
        "--api-key",
        "--api-key-file",
        "--models-preset",
        "--models-dir",
        "--ssl",
        "--ssl-key-file",
        "--ssl-cert-file",
        "--hf-token",
        "--ssl-key-pass",
    };

    /// <summary>Flags whose value must never be written to the log.</summary>
    public static readonly IReadOnlyList<string> SecretFlags = new[]
    {
        "--api-key",
        "--api-key-file",
        "--hf-token",
        "--ssl-key-file",
        "--ssl-key-pass",
    };

    /// <summary>
    /// Returns a user-facing error when <paramref name="tokens"/> contains a
    /// reserved flag, or null when the arguments are acceptable.
    /// </summary>
    public static string? Validate(IReadOnlyList<string> tokens)
        => FindReservedFlag(tokens) is { } flag
            ? $"{flag} is managed by Llama and can't be used in custom arguments."
            : null;

    /// <summary>The first reserved flag present, matched as <c>--flag</c> or <c>--flag=value</c> (case-insensitive).</summary>
    public static string? FindReservedFlag(IReadOnlyList<string> tokens)
    {
        foreach (var token in tokens)
            foreach (var flag in ReservedFlags)
                if (IsFlag(token, flag)) return flag;
        return null;
    }

    /// <summary>
    /// Renders tokens for logging with secret-flag values replaced by
    /// <c>***</c> — both the <c>--flag value</c> and <c>--flag=value</c> forms.
    /// </summary>
    public static string RedactForLog(IEnumerable<string> tokens)
    {
        var output = new List<string>();
        var redactNext = false;

        foreach (var token in tokens)
        {
            if (redactNext)
            {
                output.Add("***");
                redactNext = false;
                continue;
            }

            var secret = SecretFlags.FirstOrDefault(f => IsFlag(token, f));
            if (secret is null)
            {
                output.Add(token);
                continue;
            }

            // "--flag=value" already carries the value; "--flag value" takes
            // the next token.
            if (token.Length > secret.Length && token[secret.Length] == '=')
                output.Add($"{secret}=***");
            else
            {
                output.Add(token);
                redactNext = true;
            }
        }

        return string.Join(' ', output);
    }

    private static bool IsFlag(string token, string flag)
        => token.Equals(flag, StringComparison.OrdinalIgnoreCase)
           || (token.Length > flag.Length
               && token.StartsWith(flag, StringComparison.OrdinalIgnoreCase)
               && token[flag.Length] == '=');
}
