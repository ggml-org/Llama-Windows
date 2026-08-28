using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace LlamaApp.Views;

/// <summary>
/// A progress ring that draws nothing but the arc — the model-row replacement
/// for WinUI's <see cref="ProgressRing"/>, whose template paints a lighter
/// disk behind the arc (exactly the size of the ring, ignoring
/// <c>Background="Transparent"</c>) that read as a stray blob behind the row's
/// 20px indicator. Determinate shows the value sweep from 12 o'clock
/// (<see cref="ProgressArcPresentation"/>); indeterminate spins a fixed arc.
/// Rendered through a Viewbox so it scales to any Width/Height (rows use 20,
/// the browse/details spinners 16/14).
/// </summary>
public sealed partial class ModelProgressRing : UserControl
{
    /// <summary>The indeterminate spinner's fixed sweep.</summary>
    private const double SpinArcFraction = 0.75;

    private Storyboard? _spin;

    public ModelProgressRing()
    {
        InitializeComponent();
        SpinPath.Data = ProgressArcPresentation.Arc(SpinArcFraction);

        // Storyboards keep ticking after the control leaves the visual tree —
        // stop on unload, resume on reload while still indeterminate.
        Loaded += (_, _) => UpdateVisualState();
        Unloaded += (_, _) => StopSpin();

        UpdateVisualState();
    }

    /// <summary>
    /// Completion fraction 0..1 — the determinate arc's sweep. Ignored while
    /// <see cref="IsIndeterminate"/> is true.
    /// </summary>
    public double Fraction
    {
        get => (double)GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public static readonly DependencyProperty FractionProperty =
        DependencyProperty.Register(nameof(Fraction), typeof(double), typeof(ModelProgressRing),
            new PropertyMetadata(0.0, OnStateChanged));

    /// <summary>True while the value is unknown — the ring spins.</summary>
    public bool IsIndeterminate
    {
        get => (bool)GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
    }

    public static readonly DependencyProperty IsIndeterminateProperty =
        DependencyProperty.Register(nameof(IsIndeterminate), typeof(bool), typeof(ModelProgressRing),
            new PropertyMetadata(true, OnStateChanged));

    /// <summary>
    /// Mirrors <c>ProgressRing.IsActive</c> so call sites that toggle the
    /// animation keep working: false hides both arcs and stops the spin.
    /// </summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(ModelProgressRing),
            new PropertyMetadata(true, OnStateChanged));

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ModelProgressRing)d).UpdateVisualState();

    private void UpdateVisualState()
    {
        if (!IsActive)
        {
            ArcPath.Visibility = Visibility.Collapsed;
            SpinPath.Visibility = Visibility.Collapsed;
            StopSpin();
            return;
        }

        if (IsIndeterminate)
        {
            ArcPath.Visibility = Visibility.Collapsed;
            SpinPath.Visibility = Visibility.Visible;
            StartSpin();
            return;
        }

        StopSpin();
        SpinPath.Visibility = Visibility.Collapsed;
        ArcPath.Data = ProgressArcPresentation.Arc(Fraction);
        ArcPath.Visibility = Fraction > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StartSpin()
    {
        if (_spin is not null) return;
        var rotate = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromMilliseconds(1100),
        };
        Storyboard.SetTarget(rotate, SpinTransform);
        Storyboard.SetTargetProperty(rotate, nameof(Microsoft.UI.Xaml.Media.RotateTransform.Angle));
        _spin = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        _spin.Children.Add(rotate);
        _spin.Begin();
    }

    private void StopSpin()
    {
        _spin?.Stop();
        _spin = null;
    }
}
