using System.ComponentModel;
using System.Runtime.CompilerServices;
using LlamaApp.Llama;
using Microsoft.UI.Xaml.Media;

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
    private HardwareFitLevel? _fitLevel;

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
    /// The three-level hardware-fit verdict for this option — the cell's
    /// hub-style green/yellow/red badge (see
    /// <see cref="HardwareFitPresentation"/>), the same
    /// <see cref="LlamaApp.Llama.HardwareFitHeuristic"/> bands the variant
    /// picker uses: fit (comfortable), might (tight, slightly over, or
    /// unknown), no way (past the optimism band). Null = no verdict yet —
    /// the cell shows no badge and stays selectable (fail open).
    ///
    /// <para>Settable, not init-only: the picker first tints from the fast
    /// weights + KV heuristic, then <c>llama fit-params</c> verdicts land
    /// per option (for on-disk models) and flip this in place — raising
    /// <see cref="IsSelectable"/>/<see cref="TooltipText"/> with it.</para>
    /// </summary>
    public HardwareFitLevel? FitLevel
    {
        get => _fitLevel;
        set
        {
            if (_fitLevel == value) return;
            _fitLevel = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FitBadge));
            OnPropertyChanged(nameof(IsSelectable));
            OnPropertyChanged(nameof(TooltipText));
            OnPropertyChanged(nameof(AccessibleName));
        }
    }

    /// <summary>The hub-style fit badge for the cell — the green/yellow/red
    /// tile with its white glyph (null when there is no verdict).</summary>
    public ImageSource? FitBadge => FitLevel is { } level
        ? HardwareFitPresentation.BadgeSource(level)
        : null;

    /// <summary>
    /// Whether the option can be selected — both guards must pass. "Might"
    /// stays selectable (yellow means try it — a smaller context is one tap
    /// away); only "no way" and above-max-context disable the cell.
    /// </summary>
    public bool IsSelectable => IsSupported && FitLevel != HardwareFitLevel.NoWay;

    /// <summary>
    /// Hover text for the cell (carried on an enabled wrapper — a disabled
    /// button never shows its own tooltip): the memory requirement behind
    /// the fit tint for selectable options, otherwise the reason the option
    /// is unavailable. Empty when nothing is known.
    /// </summary>
    public string TooltipText
    {
        get
        {
            if (!IsSupported)
                return $"This model supports up to {FormatTokenCount(MaxContextTokens)} of context";
            var hasEstimate = EstimatedMemoryBytes > 0;
            return FitLevel switch
            {
                HardwareFitLevel.NoWay => hasEstimate
                    ? $"Model requires at least {EstimatedMemoryDisplay} of memory"
                    : "Won't fit in this machine's memory",
                HardwareFitLevel.Might => hasEstimate
                    ? $"Might not fit: requires {EstimatedMemoryDisplay} of memory"
                    : "Memory requirement unknown — might fit",
                _ => hasEstimate ? $"Requires {EstimatedMemoryDisplay} of memory" : "",
            };
        }
    }

    /// <summary>The cell's accessible name, e.g. "32k, might fit".</summary>
    public string AccessibleName => FitLevel is { } level
        ? $"{Label}, {HardwareFitPresentation.Label(level).ToLowerInvariant()}"
        : Label;

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
