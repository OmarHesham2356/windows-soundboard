using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using WindowsSoundboard.Models;

namespace WindowsSoundboard.Views;

/// <summary>
/// Non-destructive clip shaping: trim, gain, fades, speed and pitch, drawn against
/// the file's waveform. Nothing here writes to the audio file; the one action that
/// does, "Save cut copy", writes a new file and leaves the original alone.
/// </summary>
public sealed partial class ClipDialog : ContentDialog
{
    private enum DragTarget
    {
        None,
        Start,
        End,
    }

    private readonly float[] _peaks;
    private readonly double _seconds;
    private readonly Action<ClipEdit>? _preview;
    private bool _ready;
    private DragTarget _drag;
    private uint _dragPointer;

    public ClipEdit? Result { get; private set; }

    public bool Cleared { get; private set; }

    /// <summary>Set when the user asked for a cut copy instead of an edit.</summary>
    public CutRequest? Cut { get; private set; }

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
        SpeedSlider.Value = Math.Clamp(clip.EffectiveSpeed, 0.5, 2);
        PitchSlider.Value = Math.Round(clip.EffectivePitch);

        SyncCutBounds();
        var played = Math.Max(0.1, EndSlider.Value - StartSlider.Value);
        CutFromSlider.Value = StartSlider.Value + (played / 3);
        CutToSlider.Value = StartSlider.Value + (played * 2 / 3);

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
        var (start, end) = KeepRange();
        var gain = Math.Round(GainSlider.Value, 1);
        var speed = Math.Round(SpeedSlider.Value, 2);
        var pitch = Math.Round(PitchSlider.Value);

        return new ClipEdit
        {
            StartSeconds = start < 0.02 ? 0 : Math.Round(start, 2),
            EndSeconds = end >= _seconds - 0.02 ? 0 : Math.Round(end, 2),
            GainDb = Math.Abs(gain) < 0.05 ? 0 : gain,
            FadeInMilliseconds = Math.Round(FadeInSlider.Value),
            FadeOutMilliseconds = Math.Round(FadeOutSlider.Value),
            Speed = Math.Abs(speed - 1) < 0.005 ? 1 : speed,
            PitchSemitones = Math.Abs(pitch) < 0.5 ? 0 : pitch,
        };
    }

    private (double Start, double End) KeepRange()
    {
        var start = Math.Max(0, StartSlider.Value);
        var end = EndSlider.Value >= _seconds - 0.02 ? _seconds : EndSlider.Value;
        return (start, Math.Max(start + 0.02, end));
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

        SyncCutBounds();
        UpdateTexts();
        Draw();
    }

    private void OnEndChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        SyncCutBounds();
        UpdateTexts();
        Draw();
    }

    private void OnGainChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_ready)
        {
            UpdateTexts();
        }
    }

    private void OnFadeInChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_ready)
        {
            UpdateTexts();
        }
    }

    private void OnFadeOutChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_ready)
        {
            UpdateTexts();
        }
    }

    private void OnSpeedChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_ready)
        {
            UpdateTexts();
        }
    }

    private void OnPitchChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_ready)
        {
            UpdateTexts();
        }
    }

    private void OnCutRangeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_ready)
        {
            UpdateTexts();
        }
    }

    private void OnWaveSizeChanged(object sender, SizeChangedEventArgs e) => Draw();

    private void OnPreviewClicked(object sender, RoutedEventArgs e) => _preview?.Invoke(Build());

    /// <summary>
    /// The cut range lives inside the played range, so it is remeasured whenever the
    /// trim moves. Values outside the new window are coerced by the sliders.
    /// </summary>
    private void SyncCutBounds()
    {
        var start = StartSlider.Value;
        var end = Math.Max(EndSlider.Value, start + 0.05);

        // Widen before narrowing: a slider must never be given a minimum above its
        // maximum, which is exactly what happens when the trim jumps forward.
        CutFromSlider.Maximum = _seconds;
        CutToSlider.Maximum = _seconds;
        CutFromSlider.Minimum = start;
        CutToSlider.Minimum = start;
        CutFromSlider.Maximum = end;
        CutToSlider.Maximum = end;
    }

    private void OnCutExportClicked(object sender, RoutedEventArgs e)
    {
        var (start, end) = KeepRange();
        var from = Math.Clamp(CutFromSlider.Value, start, end);
        var to = Math.Clamp(CutToSlider.Value, from, end);

        if (to - from < 0.03)
        {
            CutHintText.Text = "Pick a range of at least 0.03s to remove.";
            return;
        }

        if ((end - start) - (to - from) < 0.05)
        {
            CutHintText.Text = "That leaves nothing to keep.";
            return;
        }

        Cut = new CutRequest(start, end, from, to);
        Hide();
    }

    private void UpdateTexts()
    {
        StartText.Text = $"{StartSlider.Value:0.00}s";
        EndText.Text = EndSlider.Value >= _seconds - 0.02 ? "end" : $"{EndSlider.Value:0.00}s";
        GainText.Text = $"{GainSlider.Value:+0.0;-0.0;0.0} dB";
        FadeInText.Text = $"{FadeInSlider.Value:0} ms";
        FadeOutText.Text = $"{FadeOutSlider.Value:0} ms";
        SpeedText.Text = $"{SpeedSlider.Value:0.00}×";
        PitchText.Text = $"{PitchSlider.Value:+0;-0;0} st";

        var played = EndSlider.Value - StartSlider.Value;
        var speed = Math.Max(0.1, SpeedSlider.Value);
        var length = played > 0 ? $" ({played:0.00}s" : string.Empty;
        if (Math.Abs(speed - 1) > 0.005)
        {
            length += $", {played / speed:0.00}s at {speed:0.00}×";
        }

        RangeText.Text = $"Playing {StartText.Text} → {EndText.Text} of {_seconds:0.0#}s" +
                         (length.Length > 0 ? length + ")" : string.Empty);

        CutFromText.Text = $"{CutFromSlider.Value:0.00}s";
        CutToText.Text = $"{CutToSlider.Value:0.00}s";

        var cutLength = Math.Max(0, CutToSlider.Value - CutFromSlider.Value);
        CutHintText.Text = cutLength > 0
            ? $"Removes {cutLength:0.00}s, keeps {(Math.Max(0, played) - cutLength):0.00}s."
            : "Pick the range to remove.";
    }

    // ---- Dragging the trim markers -------------------------------------------------

    private void OnWavePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var width = WaveCanvas.ActualWidth;
        if (width <= 1)
        {
            return;
        }

        var x = e.GetCurrentPoint(WaveCanvas).Position.X;
        var toStart = Math.Abs(x - (width * (StartSlider.Value / _seconds)));
        var toEnd = Math.Abs(x - (width * (EndSlider.Value / _seconds)));

        if (Math.Min(toStart, toEnd) > 18)
        {
            return;
        }

        _drag = toStart <= toEnd ? DragTarget.Start : DragTarget.End;
        _dragPointer = e.Pointer.PointerId;
        WaveCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnWavePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag == DragTarget.None || e.Pointer.PointerId != _dragPointer)
        {
            return;
        }

        var width = WaveCanvas.ActualWidth;
        if (width <= 1)
        {
            return;
        }

        var seconds = Math.Clamp(e.GetCurrentPoint(WaveCanvas).Position.X / width, 0, 1) * _seconds;
        if (_drag == DragTarget.Start)
        {
            StartSlider.Value = Math.Clamp(seconds, 0, Math.Max(0, EndSlider.Value - 0.02));
        }
        else
        {
            EndSlider.Value = Math.Clamp(seconds, Math.Min(_seconds, StartSlider.Value + 0.02), _seconds);
        }

        e.Handled = true;
    }

    private void OnWavePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_drag == DragTarget.None)
        {
            return;
        }

        WaveCanvas.ReleasePointerCapture(e.Pointer);
        _drag = DragTarget.None;
        e.Handled = true;
    }

    // ---- Drawing -------------------------------------------------------------------

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

        // The cut range, when it is inside the played window.
        var cutFromRatio = _seconds > 0 ? Math.Clamp(CutFromSlider.Value / _seconds, 0, 1) : 0;
        var cutToRatio = _seconds > 0 ? Math.Clamp(CutToSlider.Value / _seconds, 0, 1) : 0;
        if (cutToRatio > cutFromRatio)
        {
            AddShade(
                width * cutFromRatio,
                width * (cutToRatio - cutFromRatio),
                height,
                new SolidColorBrush(Color.FromArgb(70, 255, 90, 90)));
        }

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
            Width = 3,
            Height = height,
            Fill = new SolidColorBrush(accent),
        };

        Canvas.SetLeft(line, Math.Clamp(x - 1.5, 0, Math.Max(0, WaveCanvas.ActualWidth - 3)));
        Canvas.SetTop(line, 0);
        WaveCanvas.Children.Add(line);

        // A grip at the top says this can be dragged without needing a tooltip.
        var grip = new Rectangle
        {
            Width = 9,
            Height = 4,
            RadiusX = 2,
            RadiusY = 2,
            Fill = new SolidColorBrush(accent),
        };

        Canvas.SetLeft(grip, Math.Clamp(x - 4.5, 0, Math.Max(0, WaveCanvas.ActualWidth - 9)));
        Canvas.SetTop(grip, 2);
        WaveCanvas.Children.Add(grip);
    }
}

/// <summary>What the dialog asks for when the user chooses to export a cut copy.</summary>
public sealed record CutRequest(double KeepFrom, double KeepTo, double CutFrom, double CutTo);
