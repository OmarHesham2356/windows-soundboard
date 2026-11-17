using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using WindowsSoundboard.Models;

namespace WindowsSoundboard.Views;

/// <summary>
/// Non-destructive clip shaping: trim, gain and fades, drawn against the file's
/// waveform. Nothing here writes to the audio file.
/// </summary>
public sealed partial class ClipDialog : ContentDialog
{
    private readonly float[] _peaks;
    private readonly double _seconds;
    private readonly Action<ClipEdit>? _preview;
    private bool _ready;

    public ClipEdit? Result { get; private set; }

    public bool Cleared { get; private set; }

    public ClipDialog(string name, float[] peaks, double seconds, ClipEdit? current, Action<ClipEdit>? preview = null)
    {
        InitializeComponent();

        _peaks = peaks;
        _seconds = Math.Max(seconds, 0.1);
        _preview = preview;

        HeaderText.Text = $"{name} — {_seconds:0.0#}s";
        StartSlider.Maximum = _seconds;
        EndSlider.Maximum = _seconds;

        var clip = current ?? new ClipEdit();
        StartSlider.Value = Math.Clamp(clip.StartSeconds, 0, _seconds);
        EndSlider.Value = clip.EndSeconds > 0.02 ? Math.Clamp(clip.EndSeconds, 0, _seconds) : _seconds;
        GainSlider.Value = Math.Clamp(clip.GainDb, -24, 24);
        FadeInSlider.Value = Math.Clamp(clip.FadeInMilliseconds, 0, 2000);
        FadeOutSlider.Value = Math.Clamp(clip.FadeOutMilliseconds, 0, 2000);

        PrimaryButtonClick += (_, _) => Result = Build();
        SecondaryButtonClick += (_, _) =>
        {
            Cleared = true;
            Result = null;
        };

        _ready = true;
        UpdateTexts();
        Draw();
    }

    private ClipEdit Build()
    {
        var start = Math.Max(0, StartSlider.Value);
        var end = EndSlider.Value;
        var gain = Math.Round(GainSlider.Value, 1);

        return new ClipEdit
        {
            StartSeconds = start < 0.02 ? 0 : Math.Round(start, 2),
            EndSeconds = end >= _seconds - 0.02 ? 0 : Math.Round(end, 2),
            GainDb = Math.Abs(gain) < 0.05 ? 0 : gain,
            FadeInMilliseconds = Math.Round(FadeInSlider.Value),
            FadeOutMilliseconds = Math.Round(FadeOutSlider.Value),
        };
    }

    private void OnStartChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        if (EndSlider.Value < e.NewValue + 0.05)
        {
            EndSlider.Value = Math.Min(_seconds, e.NewValue + 0.05);
        }

        UpdateTexts();
        Draw();
    }

    private void OnEndChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        UpdateTexts();
        Draw();
    }

    private void OnGainChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        UpdateTexts();
    }

    private void OnFadeInChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        UpdateTexts();
    }

    private void OnFadeOutChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        UpdateTexts();
    }

    private void OnWaveSizeChanged(object sender, SizeChangedEventArgs e) => Draw();

    private void OnPreviewClicked(object sender, RoutedEventArgs e) => _preview?.Invoke(Build());

    private void UpdateTexts()
    {
        StartText.Text = $"{StartSlider.Value:0.00}s";
        EndText.Text = EndSlider.Value >= _seconds - 0.02 ? "end" : $"{EndSlider.Value:0.00}s";
        GainText.Text = $"{GainSlider.Value:+0.0;-0.0;0.0} dB";
        FadeInText.Text = $"{FadeInSlider.Value:0} ms";
        FadeOutText.Text = $"{FadeOutSlider.Value:0} ms";
        RangeText.Text = $"Playing {StartText.Text} → {EndText.Text} of {_seconds:0.0#}s" +
                         (EndSlider.Value > StartSlider.Value
                             ? $" ({(EndSlider.Value - StartSlider.Value):0.00}s)"
                             : string.Empty);
    }

    private void Draw()
    {
        WaveCanvas.Children.Clear();

        var width = WaveCanvas.ActualWidth > 0 ? WaveCanvas.ActualWidth : 560;
        var height = WaveCanvas.ActualHeight > 0 ? WaveCanvas.ActualHeight : 110;
        var middle = height / 2;

        if (_peaks.Length == 0)
        {
            WaveCanvas.Children.Add(new TextBlock
            {
                Text = "No waveform available",
                Opacity = 0.5,
                Margin = new Thickness(12, middle - 10, 0, 0),
            });
            return;
        }

        var accent = (Color)Application.Current.Resources["SystemAccentColor"];
        var shape = new Polygon
        {
            Fill = new SolidColorBrush(Color.FromArgb(160, accent.R, accent.G, accent.B)),
        };

        var points = new PointCollection();
        var count = _peaks.Length;
        for (var i = 0; i < count; i++)
        {
            var x = width * i / (count - 1.0);
            var peak = Math.Clamp(_peaks[i], 0f, 1f);
            points.Add(new Point(x, middle - (peak * (middle - 4))));
        }

        for (var i = count - 1; i >= 0; i--)
        {
            var x = width * i / (count - 1.0);
            var peak = Math.Clamp(_peaks[i], 0f, 1f);
            points.Add(new Point(x, middle + (peak * (middle - 4))));
        }

        shape.Points = points;
        WaveCanvas.Children.Add(shape);

        // Shade what will not be played.
        var startRatio = _seconds > 0 ? Math.Clamp(StartSlider.Value / _seconds, 0, 1) : 0;
        var endRatio = _seconds > 0 ? Math.Clamp(EndSlider.Value / _seconds, 0, 1) : 1;
        var dim = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0));

        AddShade(0, width * startRatio, height, dim);
        AddShade(width * endRatio, width * (1 - endRatio), height, dim);

        AddMarker(width * startRatio, height, accent);
        AddMarker(width * endRatio, height, accent);
    }

    private void AddShade(double left, double shadeWidth, double height, Brush brush)
    {
        if (shadeWidth <= 0.5)
        {
            return;
        }

        var rectangle = new Rectangle
        {
            Width = shadeWidth,
            Height = height,
            Fill = brush,
        };

        Canvas.SetLeft(rectangle, left);
        Canvas.SetTop(rectangle, 0);
        WaveCanvas.Children.Add(rectangle);
    }

    private void AddMarker(double x, double height, Color accent)
    {
        var line = new Rectangle
        {
            Width = 2,
            Height = height,
            Fill = new SolidColorBrush(accent),
        };

        Canvas.SetLeft(line, Math.Clamp(x - 1, 0, Math.Max(0, WaveCanvas.ActualWidth - 2)));
        Canvas.SetTop(line, 0);
        WaveCanvas.Children.Add(line);
    }
}
