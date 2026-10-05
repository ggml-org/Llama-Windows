using System.Reflection;

namespace LlamaApp.Common;

/// <summary>
/// The single HTTP User-Agent every <see cref="System.Net.Http.HttpClient"/>
/// the app creates must reuse: <c>LlamaWindows/&lt;version&gt;</c> (e.g.
/// <c>LlamaWindows/0.12.0</c>) — so Hub and telemetry backends can tell our
/// traffic apart from curl, browsers, and other llama.cpp tooling.
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
        return $"LlamaWindows/{version.ToString(3)}";
    });

    /// <summary>The User-Agent header value, e.g. <c>LlamaWindows/0.12.0</c>.</summary>
    public static string Value => Cached.Value;
}
