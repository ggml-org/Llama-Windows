namespace LlamaApp.Llama;

/// <summary>
/// Lightweight accessor over the local llama.cpp binary, kept as a static
/// surface so the rest of the app can read the version without a direct
/// reference to <see cref="LlamaManager"/>. The heavy lifting — detecting,
/// downloading, and executing the install script — lives in
/// <see cref="LlamaManager"/>.
/// </summary>
public static class LlamaRunner
{
    /// <summary>
    /// Version of the resolved llama.cpp binary in use, or <c>null</c> when no
    /// binary is installed yet. Backed by <see cref="LlamaManager.Version"/>
    /// via <see cref="VersionCache"/>; the footer reads this live.
    /// </summary>
    public static string? Version => VersionCache;

    /// <summary>
    /// Set by <see cref="LlamaManager"/> whenever it resolves (or fails to
    /// resolve) a binary, so <see cref="Version"/> reflects the current state
    /// without callers needing to subscribe to <see cref="LlamaManager.StateChanged"/>.
    /// </summary>
    internal static string? VersionCache;
}