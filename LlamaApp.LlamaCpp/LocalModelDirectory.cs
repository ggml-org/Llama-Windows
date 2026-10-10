using System.Security.Cryptography;
using System.Text;

namespace LlamaApp.Llama;

/// <summary>Finds GGUF weights in user-selected folders without moving them.</summary>
internal static class LocalModelDirectory
{
    internal static IReadOnlyDictionary<string, string> Scan(IEnumerable<string>? directories)
    {
        var models = new Dictionary<string, string>(StringComparer.Ordinal);
        if (directories is null) return models;

        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            try
            {
                var root = Path.GetFullPath(directory.Trim());
                if (!Directory.Exists(root)) continue;
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    MatchCasing = MatchCasing.CaseInsensitive,
                };
                foreach (var file in Directory.EnumerateFiles(root, "*.gguf", options))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    // Multimodal projectors are accessories, not standalone models.
                    if (name.Contains("mmproj", StringComparison.OrdinalIgnoreCase)) continue;

                    var slug = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'
                        ? c : '-').Take(64).ToArray()).Trim('-');
                    if (slug.Length == 0) slug = "model";
                    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.ToUpperInvariant())))[..12];
                    models[$"local/{slug}-{hash}"] = file;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                LlamaApp.Common.Log.Warn(ex, $"could not scan local model folder: {directory}");
            }

        }
        return models;
    }
}

/// <summary>Publishes complete background scans; preset rendering only reads the snapshot.</summary>
internal sealed class LocalModelDirectoryCache
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<IReadOnlyList<string>, IReadOnlyDictionary<string, string>> _scan;
    private string[]? _directories;
    private IReadOnlyDictionary<string, string> _snapshot =
        new Dictionary<string, string>(StringComparer.Ordinal);

    internal LocalModelDirectoryCache(
        Func<IReadOnlyList<string>, IReadOnlyDictionary<string, string>>? scan = null)
    {
        _scan = scan ?? (directories => LocalModelDirectory.Scan(directories));
    }

    internal IReadOnlyDictionary<string, string> Snapshot => Volatile.Read(ref _snapshot);

    internal async Task RefreshAsync(IEnumerable<string>? directories,
        CancellationToken cancel = default, bool force = false)
    {
        var roots = directories?.ToArray() ?? [];
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (!force && _directories is not null &&
                _directories.SequenceEqual(roots, StringComparer.OrdinalIgnoreCase)) return;
            var snapshot = await Task.Run(() => _scan(roots), cancel).ConfigureAwait(false);
            cancel.ThrowIfCancellationRequested();
            Volatile.Write(ref _snapshot, snapshot);
            _directories = roots;
        }
        finally { _gate.Release(); }
    }
}
