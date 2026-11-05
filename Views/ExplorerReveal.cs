using System.IO;

namespace LlamaApp.Views;

/// <summary>
/// "Show model folder in Explorer" support: the launch decision is pure so it
/// stays unit-testable. The process launch itself stays in the click handler.
/// </summary>
public static class ExplorerReveal
{
    /// <summary>Whether the row's model can be revealed at all.</summary>
    public static bool CanReveal(string? filePath)
        => !string.IsNullOrWhiteSpace(filePath);

    /// <summary>
    /// Opens the model's containing folder itself, rather than selecting only
    /// the LLM file: mmproj and speculative-draft sidecars must be visible too.
    /// The folder is used as the shell launch target with no arguments. Returns
    /// null when the folder no longer exists. Quotes are refused and malformed
    /// server-reported paths fail closed instead of escaping into a shell command.
    /// </summary>
    public static (string FileName, string Arguments)? BuildReveal(string? filePath)
    {
        if (!CanReveal(filePath) || filePath!.Contains('"')) return null;

        try
        {
            var directory = Path.GetDirectoryName(filePath);
            return !string.IsNullOrEmpty(directory) && Directory.Exists(directory)
                ? (directory, "")
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException
            or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
