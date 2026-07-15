namespace LlamaApp.Common;

/// <summary>
/// A model entry — locally installed or available for download.
/// </summary>
public interface IModel
{
    string Name { get; }
    string Description { get; }
    string License { get; }
    /// <summary>Human-readable parameter count, e.g. "20B", "270M", "E4B".</summary>
    string Parameters { get; }
    /// <summary>Human-readable size, e.g. "12.1 GB".</summary>
    string Size { get; }
    bool Vision { get; }
}
