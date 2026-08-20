using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LlamaApp.Views;

/// <summary>
/// One selectable context length in the model details view ("32k · 29 GB").
/// Pure data — the selector is data-driven rather than encoded in XAML, so
/// the supported values come from the model's metadata (see
/// <see cref="ModelItemDetailsViewModel"/>), not from the template.
///
/// <para>Instances are immutable except for <see cref="IsSelected"/>, the
/// view-state that highlights the current option; the details ViewModel flips
/// it on the old/new option when the selection changes.</para>
/// </summary>
public sealed class ContextLengthOption : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _fitsInMemory = true;

    /// <summary>The context length in tokens (e.g. 32768).</summary>
    public int Tokens { get; init; }

    /// <summary>Short label, e.g. "32k".</summary>
    public string Label { get; init; } = "";

    /// <summary>The model's maximum context length in tokens (0 = unknown).</summary>
    public int MaxContextTokens { get; init; }

    /// <summary>Short token-count formatting shared by labels and tooltips ("32k").</summary>
    internal static string FormatTokenCount(int tokens)
        => tokens % 1024 == 0 ? $"{tokens / 1024}k" : $"{tokens}";

    /// <summary>
    /// Estimated total memory (weights + KV cache) at this context length,
    /// computed by the runtime layer (see <see cref="Llama.ContextMemoryEstimate"/>)
    /// — never by the view.
    /// </summary>
    public long EstimatedMemoryBytes { get; init; }

    /// <summary>
    /// <see cref="EstimatedMemoryBytes"/> formatted for the option cell —
    /// empty when the estimate is unknown (no local file found), so a failed
    /// probe shows just the label rather than a misleading "0 B".
    /// </summary>
    public string EstimatedMemoryDisplay => EstimatedMemoryBytes > 0
        ? DownloadProgressPresentation.FormatBytes(EstimatedMemoryBytes)
        : "";

    /// <summary>
    /// False when the option exceeds the model's maximum context length (from
    /// the GGUF metadata) — the cell shows disabled rather than offering a
    /// context the model can't run.
    /// </summary>
    public bool IsSupported { get; init; } = true;

    /// <summary>
    /// False when the estimated memory at this context length exceeds the
    /// machine's memory budget (free VRAM across all accelerator devices
    /// plus the usable share of free RAM) — the cell shows disabled rather
    /// than offering a configuration that can't load. True when the
    /// estimate is unknown (never gray out on a guess).
    ///
    /// <para>Settable, not init-only: the picker first grays from the fast
    /// weights + KV heuristic, then <c>llama fit-params</c> verdicts land
    /// per option (for on-disk models) and flip this in place — raising
    /// <see cref="IsSelectable"/>/<see cref="TooltipText"/> with it.</para>
    /// </summary>
    public bool FitsInMemory
    {
        get => _fitsInMemory;
        set
        {
            if (_fitsInMemory == value) return;
            _fitsInMemory = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSelectable));
            OnPropertyChanged(nameof(TooltipText));
        }
    }

    /// <summary>Whether the option can be selected — both guards must pass.</summary>
    public bool IsSelectable => IsSupported && FitsInMemory;

    /// <summary>
    /// Hover text for the cell (carried on an enabled wrapper — a disabled
    /// button never shows its own tooltip): the memory requirement for
    /// selectable options, otherwise the reason the option is unavailable.
    /// Empty when nothing is known.
    /// </summary>
    public string TooltipText
    {
        get
        {
            if (!IsSupported)
                return $"This model supports up to {FormatTokenCount(MaxContextTokens)} of context";
            if (!FitsInMemory)
                return $"Model requires at least {EstimatedMemoryDisplay} of memory";
            return EstimatedMemoryBytes > 0
                ? $"Requires {EstimatedMemoryDisplay} of memory"
                : "";
        }
    }

    /// <summary>View-state: whether this is the selected option.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? prop = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}
