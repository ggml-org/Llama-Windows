using System.Reflection;
using System.Runtime.InteropServices;

namespace LlamaApp.Common;

/// <summary>
/// The single HTTP User-Agent every <see cref="System.Net.Http.HttpClient"/>
/// the app creates must reuse:
/// <c>llama-win/&lt;version&gt; (&lt;os version&gt;; &lt;arch&gt;)</c> —
/// e.g. <c>llama-win/0.12.0 (10.0.26100; x64)</c> — so backends
/// (Hugging Face Hub, GitHub releases, the local llama-server) can tell our
/// traffic apart from curl, browsers, and other llama.cpp tooling, and see
/// which app version, Windows build, and processor architecture it came
/// from. This is the only usage signal the app sends: there is no telemetry.
///
/// Computed once from the entry assembly's version (the app exe), so it
/// tracks the package version automatically; a library-only host (unit
/// tests) falls back to its own assembly version. Attach with
/// <c>client.DefaultRequestHeaders.UserAgent.ParseAdd(HttpUserAgent.Value)</c>.
/// </summary>
public static class HttpUserAgent
{
    private static readonly Lazy<string> Cached = new(() =>
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version
                      ?? typeof(HttpUserAgent).Assembly.GetName().Version
                      ?? new Version(0, 0, 0);
        var os = Environment.OSVersion.Version;
        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        return $"llama-win/{version.ToString(3)} ({os.Major}.{os.Minor}.{os.Build}; {arch})";
    });

    /// <summary>The User-Agent header value, e.g. <c>llama-win/0.12.0 (10.0.26100; x64)</c>.</summary>
    public static string Value => Cached.Value;
}
