using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TrayVoiceNotes.Services;
using Windows.Foundation;

namespace TrayVoiceNotes.Controls;

/// <summary>
/// Draws stored peaks as rounded bars sized to fit the control, with the played part in the
/// accent color. Two paths share one geometry; the accent one is clipped to the progress, so a
/// playback tick only moves a clip rectangle instead of rebuilding anything.
/// </summary>
public sealed partial class WaveformView : Grid
{
    public static readonly DependencyProperty PeaksProperty = DependencyProperty.Register(
        nameof(Peaks), typeof(byte[]), typeof(WaveformView), new PropertyMetadata(null, (d, _) => ((WaveformView)d).Rebuild()));

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(WaveformView), new PropertyMetadata(0.0, (d, _) => ((WaveformView)d).UpdateClip()));

    public static readonly DependencyProperty IsSeekEnabledProperty = DependencyProperty.Register(
        nameof(IsSeekEnabled), typeof(bool), typeof(WaveformView), new PropertyMetadata(false));

    // Brushes come from XAML as {ThemeResource}, so they follow the flyout's (taskbar) theme
    // rather than the app theme.
    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(WaveformView), new PropertyMetadata(null, (d, e) => ((WaveformView)d)._background.Fill = (Brush)e.NewValue));

    public static readonly DependencyProperty PlayedBrushProperty = DependencyProperty.Register(
        nameof(PlayedBrush), typeof(Brush), typeof(WaveformView), new PropertyMetadata(null, (d, e) => ((WaveformView)d)._played.Fill = (Brush)e.NewValue));

    private const double BarWidth = 2;
    private const double BarGap = 1.5;
    private const double MinBarHeight = 2;

    private readonly Microsoft.UI.Xaml.Shapes.Path _background = new();
    private readonly Microsoft.UI.Xaml.Shapes.Path _played = new();
    private readonly RectangleGeometry _clip = new();

    public WaveformView()
    {
        _played.Clip = _clip;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); // hit-testable across the gaps
        Children.Add(_background);
        Children.Add(_played);
        AutomationProperties.SetName(this, "Waveform");

        SizeChanged += (_, _) => Rebuild();
        PointerPressed += OnPointerPressed;
    }

    /// <summary>Raised with a 0–1 position when the user clicks the waveform.</summary>
    public event EventHandler<double>? SeekRequested;

    public byte[]? Peaks
    {
        get => (byte[]?)GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
    }

    public Brush? BarBrush
    {
        get => (Brush?)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public Brush? PlayedBrush
    {
        get => (Brush?)GetValue(PlayedBrushProperty);
        set => SetValue(PlayedBrushProperty, value);
    }

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public bool IsSeekEnabled
    {
        get => (bool)GetValue(IsSeekEnabledProperty);
        set => SetValue(IsSeekEnabledProperty, value);
    }

    private void Rebuild()
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        int bars = Math.Max(1, (int)((width + BarGap) / (BarWidth + BarGap)));
        byte[] peaks = Peaks is { Length: > 0 } stored ? WaveformMath.Resample(stored, bars) : new byte[bars];

        // A GeometryGroup can't be shared between two Paths, so build one per path.
        _background.Data = BuildGeometry(peaks, height);
        _played.Data = BuildGeometry(peaks, height);
        UpdateClip();
    }

    private static GeometryGroup BuildGeometry(byte[] peaks, double height)
    {
        GeometryGroup group = new();
        for (int i = 0; i < peaks.Length; i++)
        {
            double barHeight = Math.Max(MinBarHeight, peaks[i] / 255.0 * height);
            group.Children.Add(new RectangleGeometry
            {
                Rect = new Rect(i * (BarWidth + BarGap), (height - barHeight) / 2, BarWidth, barHeight),
            });
        }

        return group;
    }

    private void UpdateClip() =>
        _clip.Rect = new Rect(0, 0, Math.Clamp(Progress, 0, 1) * ActualWidth, Math.Max(0, ActualHeight));

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsSeekEnabled || ActualWidth <= 0)
        {
            return;
        }

        e.Handled = true;
        SeekRequested?.Invoke(this, Math.Clamp(e.GetCurrentPoint(this).Position.X / ActualWidth, 0, 1));
    }
}
