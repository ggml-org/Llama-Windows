using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LlamaApp.Views;

/// <summary>
/// Lightweight view-model for one Hugging Face Hub search-result row in the
/// models panel's bottom search section. Mirrors the shape of the installed /
/// catalog rows (name line + gray metadata line + a trailing action cell) but
/// carries only what a Hub search can know: the repo id, its split-out author,
/// and the Hub's download/like counts. The download action itself reuses the
/// catalog download pipeline — the row flips to a static "added" checkmark and
/// the live progress ring appears on the model in the installed list above.
/// </summary>
public sealed class HubModelItemViewModel : INotifyPropertyChanged
{
    // Settable (not init-only): the XAML type-info generator the compiled
    // bindings emit requires settable properties.

    /// <summary>Hugging Face repo id, e.g. "ggml-org/gemma-3-4b-it-GGUF".</summary>
    public string RepoId { get; set; } = "";

    /// <summary>Short display name: the part of the repo id after the last '/'.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Author/org part of the repo id (the metadata line's first token).</summary>
    public string Author { get; set; } = "";

    /// <summary>All-time download count from the Hub (0 when unknown).</summary>
    public long Downloads { get; set; }

    /// <summary>Like count from the Hub (0 when unknown).</summary>
    public long Likes { get; set; }

    private Microsoft.UI.Xaml.Media.ImageSource? _logo;

    /// <summary>
    /// The author's Hub avatar, attached by the shell once it lands (memory →
    /// disk cache; fetched from the Hub when the model is downloaded). Null
    /// renders the empty tile — same as catalog rows with an unknown brand.
    /// </summary>
    public Microsoft.UI.Xaml.Media.ImageSource? Logo
    {
        get => _logo;
        set
        {
            if (ReferenceEquals(_logo, value)) return;
            _logo = value;
            OnPropertyChanged();
        }
    }

    private bool _downloadStarted;

    /// <summary>
    /// True once the download was started from this row (or the repo is
    /// already installed) — the row's action cell swaps the download glyph
    /// for a static checkmark; the installed list owns the live progress.
    /// </summary>
    public bool DownloadStarted
    {
        get => _downloadStarted;
        set
        {
            if (_downloadStarted == value) return;
            _downloadStarted = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DownloadGlyphVisible));
            OnPropertyChanged(nameof(AddedGlyphVisible));
        }
    }

    /// <summary>True while the row still offers its download action.</summary>
    public bool DownloadGlyphVisible => !_downloadStarted;

    /// <summary>True once the row's action cell shows the "added" checkmark.</summary>
    public bool AddedGlyphVisible => _downloadStarted;

    /// <summary>
    /// True for the synthetic "Show more" row appended at the end of a full
    /// page of results — it drives the template selector and is never a real
    /// model row (no repo id, no download action).
    /// </summary>
    public bool IsLoadMoreRow { get; set; }

    private bool _loadMoreBusy;

    /// <summary>
    /// True while the next page is in flight — the load-more row shows an
    /// inline progress ring and further clicks are ignored.
    /// </summary>
    public bool LoadMoreBusy
    {
        get => _loadMoreBusy;
        set
        {
            if (_loadMoreBusy == value) return;
            _loadMoreBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowMoreVisible));
            OnPropertyChanged(nameof(LoadMoreBusyVisible));
            OnPropertyChanged(nameof(RetryVisible));
        }
    }

    private bool _loadMoreFailed;

    /// <summary>
    /// True when the last page fetch failed — the row offers a recoverable
    /// "Retry" instead of "Show more"; the shown results are untouched.
    /// </summary>
    public bool LoadMoreFailed
    {
        get => _loadMoreFailed;
        set
        {
            if (_loadMoreFailed == value) return;
            _loadMoreFailed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowMoreVisible));
            OnPropertyChanged(nameof(LoadMoreBusyVisible));
            OnPropertyChanged(nameof(RetryVisible));
        }
    }

    /// <summary>True when the load-more row shows the idle "Show more" affordance.</summary>
    public bool ShowMoreVisible => !_loadMoreBusy && !_loadMoreFailed;

    /// <summary>True when the load-more row shows the inline progress ring.</summary>
    public bool LoadMoreBusyVisible => _loadMoreBusy;

    /// <summary>True when the load-more row shows the recoverable "Retry" affordance.</summary>
    public bool RetryVisible => _loadMoreFailed;

    /// <summary>
    /// The gray metadata line under the name: author · downloads · likes,
    /// each part omitted when unknown, e.g. "ggml-org · 1.2M downloads".
    /// </summary>
    public string MetaText
    {
        get
        {
            var parts = new List<string>(3);
            if (Author.Length > 0) parts.Add(Author);
            if (Downloads > 0) parts.Add(FormatCount(Downloads) + " downloads");
            if (Likes > 0) parts.Add(FormatCount(Likes) + " likes");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// Compact count, e.g. 1234 → "1.2K", 12345678 → "12.3M". One fractional
    /// digit below 10, whole numbers above — the usual Hub-counter style.
    /// </summary>
    private static string FormatCount(long n) => n switch
    {
        >= 1_000_000_000 => $"{n / 1_000_000_000.0:0.#}B",
        >= 1_000_000 => $"{n / 1_000_000.0:0.#}M",
        >= 10_000 => $"{n / 1_000.0:0}K",
        >= 1_000 => $"{n / 1_000.0:0.#}K",
        _ => n.ToString(),
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? prop = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}
