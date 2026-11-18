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
        Pan,
    }

    private readonly float[] _peaks;
    private readonly double _seconds;
    private readonly Action<ClipEdit>? _preview;
    private const double MaxZoom = 32;

    private bool _ready;
    private DragTarget _drag;
    private uint _dragPointer;
    private double _viewStart;
    private double _viewSpan;
    private double _panStart;
    private double _panX;
    private bool _zoomLock;

    public ClipEdit? Result { get; private set; }

    public bool Cleared { get; private set; }

    /// <summary>Set when the user asked for a cut copy instead of an edit.</summary>
    public CutRequest? Cut { get; private set; }

    public ClipDialog(string name, float[] peaks, double seconds, ClipEdit? current, Action<ClipEdit>? preview = null)
    {
        InitializeComponent();

        _peaks = peaks;
        _seconds = Math.Max(seconds, 0.1);
        _viewSpan = _seconds;
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

        EnsureVisible(e.NewValue);
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

        EnsureVisible(e.NewValue);
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

        var zoom = _seconds / Math.Max(_viewSpan, 0.001);
        ZoomText.Text = $"{zoom:0.0}×";

        var played = EndSlider.Value - StartSlider.Value;
        var speed = Math.Max(0.1, SpeedSlider.Value);
        var length = played > 0 ? $" ({played:0.00}s" : string.Empty;
        if (Math.Abs(speed - 1) > 0.005)
        {
            length += $", {played / speed:0.00}s at {speed:0.00}×";
        }

        RangeText.Text = $"Playing {StartText.Text} → {EndText.Text} of {_seconds:0.0#}s" +
                         (length.Length > 0 ? length + ")" : string.Empty);

        if (zoom > 1.05)
        {
            RangeText.Text += $" · showing {_viewStart:0.00}–{ViewEnd:0.00}s";
        }

        CutFromText.Text = $"{CutFromSlider.Value:0.00}s";
        CutToText.Text = $"{CutToSlider.Value:0.00}s";

        var cutLength = Math.Max(0, CutToSlider.Value - CutFromSlider.Value);
        CutHintText.Text = cutLength > 0
            ? $"Removes {cutLength:0.00}s, keeps {(Math.Max(0, played) - cutLength):0.00}s."
            : "Pick the range to remove.";
    }

    // ---- Zooming the waveform -------------------------------------------------------

    private double ViewEnd => Math.Min(_seconds, _viewStart + _viewSpan);

    private void ClampView()
    {
        var minSpan = Math.Max(0.05, _seconds / MaxZoom);
        _viewSpan = Math.Clamp(_viewSpan, minSpan, _seconds);
        _viewStart = Math.Clamp(_viewStart, 0, Math.Max(0, _seconds - _viewSpan));
    }

    /// <summary>Where a pointer x lands in the file, inside the visible window.</summary>
    private double SecondsAt(double x, double width) =>
        Math.Clamp(_viewStart + (Math.Clamp(x, 0, width) / Math.Max(width, 1)) * _viewSpan, 0, _seconds);

    /// <summary>The screen x of a time in the file; can sit off the canvas when zoomed.</summary>
    private double ScreenX(double seconds, double width) =>
        _viewSpan <= 0 ? 0 : (seconds - _viewStart) / _viewSpan * width;

    /// <summary>Scrolls only as far as needed to put a time back on screen.</summary>
    private void EnsureVisible(double seconds)
    {
        if (seconds < _viewStart)
        {
            _viewStart = seconds - (_viewSpan * 0.1);
        }
        else if (seconds > ViewEnd)
        {
            _viewStart = seconds - (_viewSpan * 0.9);
        }
        else
        {
            return;
        }

        ClampView();
        SyncZoom();
        Draw();
    }

    /// <summary>Writes the window back to the zoom slider without re-entering its handler.</summary>
    private void SyncZoom()
    {
        _zoomLock = true;
        ZoomSlider.Value = Math.Clamp(_seconds / Math.Max(_viewSpan, 0.001), 1, MaxZoom);
        _zoomLock = false;
    }

    private void OnZoomChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_ready || _zoomLock)
        {
            return;
        }

        var span = Math.Clamp(_seconds / Math.Max(e.NewValue, 1), _seconds / MaxZoom, _seconds);

        // Coming out of the whole file, zoom towards the trim; once zoomed in, keep the
        // middle of what is already on screen instead.
        var anchor = _viewSpan >= _seconds - 0.001
            ? (StartSlider.Value + EndSlider.Value) / 2
            : _viewStart + (_viewSpan / 2);

        _viewStart = anchor - (span / 2);
        _viewSpan = span;
        ClampView();
        SyncZoom();
        UpdateTexts();
        Draw();
    }

    private void OnWavePointerWheel(object sender, PointerRoutedEventArgs e)
    {
        var width = WaveCanvas.ActualWidth;
        if (width <= 1 || _seconds <= 0)
        {
            return;
        }

        var point = e.GetCurrentPoint(WaveCanvas);
        var delta = point.Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control))
        {
            // Zoom around the pointer, so the part under the cursor stays put.
            var anchor = SecondsAt(point.Position.X, width);
            var factor = delta > 0 ? 0.85 : 1 / 0.85;
            var span = Math.Clamp(_viewSpan * factor, _seconds / MaxZoom, _seconds);
            _viewStart = anchor - ((point.Position.X / width) * span);
            _viewSpan = span;
            ClampView();
            SyncZoom();
        }
        else if (_viewSpan < _seconds - 0.001)
        {
            _viewStart -= Math.Sign(delta) * _viewSpan * 0.15;
            ClampView();
        }
        else
        {
            // Nothing to pan: let the dialog scroll instead.
            return;
        }

        UpdateTexts();
        Draw();
        e.Handled = true;
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
        var startX = ScreenX(StartSlider.Value, width);
        var endX = ScreenX(EndSlider.Value, width);

        // Only markers that are actually on screen can be grabbed.
        var toStart = startX >= -2 && startX <= width + 2 ? Math.Abs(x - startX) : double.MaxValue;
        var toEnd = endX >= -2 && endX <= width + 2 ? Math.Abs(x - endX) : double.MaxValue;

        if (Math.Min(toStart, toEnd) <= 18)
        {
            _drag = toStart <= toEnd ? DragTarget.Start : DragTarget.End;
        }
        else if (_viewSpan < _seconds - 0.001)
        {
            // Empty space: drag the view along the file.
            _drag = DragTarget.Pan;
            _panX = x;
            _panStart = _viewStart;
        }
        else
        {
            return;
        }

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

        var x = e.GetCurrentPoint(WaveCanvas).Position.X;

        if (_drag == DragTarget.Pan)
        {
            _viewStart = _panStart - (((x - _panX) / width) * _viewSpan);
            ClampView();
            UpdateTexts();
            Draw();
            e.Handled = true;
            return;
        }

        // Zoomed in: nudge the view when the pointer reaches an edge, so a marker can be
        // dragged past the edge of the screen instead of sticking to it.
        var panned = false;
        if (_viewSpan < _seconds - 0.001)
        {
            if (x < 24)
            {
                _viewStart -= _viewSpan * 0.02;
                panned = true;
            }
            else if (x > width - 24)
            {
                _viewStart += _viewSpan * 0.02;
                panned = true;
            }

            if (panned)
            {
                ClampView();
            }
        }

        var seconds = SecondsAt(x, width);
        if (_drag == DragTarget.Start)
        {
            StartSlider.Value = Math.Clamp(seconds, 0, Math.Max(0, EndSlider.Value - 0.02));
        }
        else
        {
            EndSlider.Value = Math.Clamp(seconds, Math.Min(_seconds, StartSlider.Value + 0.02), _seconds);
        }

        if (panned)
        {
            Draw();
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

        // One column per pixel of the visible window. Each takes the loudest stored peak
        // it covers, so zooming in reveals detail rather than stretching a few buckets.
        var count = _peaks.Length;
        var columns = Math.Max(2, (int)Math.Round(width));
        var columnPeaks = new float[columns];
        for (var i = 0; i < columns; i++)
        {
            var from = _viewStart + (_viewSpan * i / columns);
            var to = _viewStart + (_viewSpan * (i + 1) / columns);
            var first = (int)Math.Clamp(from / _seconds * count, 0, count - 1);
            var last = (int)Math.Clamp(to / _seconds * count, 0, count - 1);
            var peak = 0f;
            for (var b = first; b <= last; b++)
            {
                peak = Math.Max(peak, _peaks[b]);
            }

            columnPeaks[i] = Math.Clamp(peak, 0f, 1f);
        }

        var points = new PointCollection();
        for (var i = 0; i < columns; i++)
        {
            var x = width * (i + 0.5) / columns;
            points.Add(new Point(x, middle - (columnPeaks[i] * (middle - 4))));
        }

        for (var i = columns - 1; i >= 0; i--)
        {
            var x = width * (i + 0.5) / columns;
            points.Add(new Point(x, middle + (columnPeaks[i] * (middle - 4))));
        }

        shape.Points = points;
        WaveCanvas.Children.Add(shape);

        // Shade what will not be played.
        var startX = Math.Clamp(ScreenX(StartSlider.Value, width), 0, width);
        var endX = Math.Clamp(ScreenX(EndSlider.Value, width), 0, width);
        var dim = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0));

        AddShade(0, startX, height, dim);
        AddShade(endX, width - endX, height, dim);

        // The cut range, where it falls inside the visible window.
        var cutFromX = Math.Clamp(ScreenX(CutFromSlider.Value, width), 0, width);
        var cutToX = Math.Clamp(ScreenX(CutToSlider.Value, width), 0, width);
        if (cutToX > cutFromX)
        {
            AddShade(
                cutFromX,
                cutToX - cutFromX,
                height,
                new SolidColorBrush(Color.FromArgb(70, 255, 90, 90)));
        }

        if (startX >= 0 && startX <= width)
        {
            AddMarker(startX, height, accent);
        }

        if (endX >= 0 && endX <= width)
        {
            AddMarker(endX, height, accent);
        }
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
