using System.IO;

namespace LlamaApp.Views;

/// <summary>
/// "Show model file in Explorer" support: the decision logic and the
/// <c>explorer.exe</c> argument building are pure so they stay unit-testable —
/// Explorer's argument parsing is quirky enough to deserve tests. The process
/// launch itself stays in the click handler.
/// </summary>
public static class ExplorerReveal
{
    /// <summary>Whether the row's model can be revealed at all.</summary>
    public static bool CanReveal(string? filePath)
        => !string.IsNullOrWhiteSpace(filePath);

    /// <summary>
    /// The launch target for revealing a model file: <c>explorer.exe</c> with
    /// <c>/select,"path"</c> when the file still exists (Explorer opens its
    /// folder with the file selected), else the containing folder alone when
    /// that exists (the file may have been deleted outside the app), else
    /// null (nothing on disk to show). A path containing a quote is refused:
    /// Explorer takes the classic raw argument form (it is not
    /// CommandLineToArgvW), and while Windows file names cannot carry quotes,
    /// the input may come from a server report and must not be trusted into a
    /// command line unchecked.
    /// </summary>
    public static (string FileName, string Arguments)? BuildReveal(string? filePath)
    {
        if (!CanReveal(filePath) || filePath!.Contains('"')) return null;

        if (File.Exists(filePath))
            return ("explorer.exe", $"/select,\"{filePath}\"");

        var directory = Path.GetDirectoryName(filePath);
        return !string.IsNullOrEmpty(directory) && Directory.Exists(directory)
            ? (directory, "")
            : null;
    }
}
