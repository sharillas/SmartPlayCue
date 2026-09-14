using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace StagePlayout.App.Controls;

/// <summary>
/// Timebar de fades por cue: arrastar a pega esquerda = fade-in, direita = fade-out.
/// Liga-se (TwoWay) a Cue.FadeInSeconds / Cue.FadeOutSeconds.
/// </summary>
public partial class FadeBar : UserControl
{
    private const double MaxSeconds = 5.0;

    public static readonly DependencyProperty FadeInSecondsProperty = DependencyProperty.Register(
        nameof(FadeInSeconds), typeof(double), typeof(FadeBar),
        new FrameworkPropertyMetadata(0.5, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnChanged));

    public static readonly DependencyProperty FadeOutSecondsProperty = DependencyProperty.Register(
        nameof(FadeOutSeconds), typeof(double), typeof(FadeBar),
        new FrameworkPropertyMetadata(0.5, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnChanged));

    public double FadeInSeconds
    {
        get => (double)GetValue(FadeInSecondsProperty);
        set => SetValue(FadeInSecondsProperty, value);
    }

    public double FadeOutSeconds
    {
        get => (double)GetValue(FadeOutSecondsProperty);
        set => SetValue(FadeOutSecondsProperty, value);
    }

    public FadeBar()
    {
        InitializeComponent();
        Loaded += (_, _) => Update();
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((FadeBar)d).Update();

    private void Update()
    {
        var w = Root.Width;
        if (w <= 0) return;

        var fx = Math.Clamp(FadeInSeconds, 0, MaxSeconds) / MaxSeconds * w;
        Canvas.SetLeft(InThumb, Math.Min(fx, w - InThumb.Width));
        InFill.Width = fx;

        var ox = Math.Clamp(FadeOutSeconds, 0, MaxSeconds) / MaxSeconds * w;
        Canvas.SetLeft(OutThumb, Math.Max(0, w - ox - OutThumb.Width));
        Canvas.SetLeft(OutFill, w - ox);
        OutFill.Width = ox;

        Lbl.Text = $"in {FadeInSeconds:0.#}s · out {FadeOutSeconds:0.#}s";
    }

    private void InThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var w = Root.Width;
        if (w <= 0) return;
        var v = Math.Clamp(FadeInSeconds + e.HorizontalChange / w * MaxSeconds, 0, MaxSeconds);
        FadeInSeconds = Math.Round(v * 10) / 10;
    }

    private void OutThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var w = Root.Width;
        if (w <= 0) return;
        var v = Math.Clamp(FadeOutSeconds - e.HorizontalChange / w * MaxSeconds, 0, MaxSeconds);
        FadeOutSeconds = Math.Round(v * 10) / 10;
    }
}
