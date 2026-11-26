using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NotOnlyFans.Services;
using Windows.Foundation;

namespace NotOnlyFans.Controls;

/// <summary>
/// The last 5 minutes of one reading (from <see cref="SensorHistory"/>, which records all the time), newest at the
/// right. Redraws itself every second while it is on screen; hovering shows the value at that moment.
/// Used on Sensors (a sensor's row opens it) and on a fan's page in Cooling.
/// </summary>
public sealed partial class HistoryChart : UserControl
{
    private readonly float[] _values = new float[SensorHistory.Capacity];
    private int _count;
    private float _lo, _hi;
    private double _step, _x0;
    private Point? _pointer;
    private bool _subscribed;

    public HistoryChart()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (!_subscribed)
            {
                App.Sensors.ValuesChanged += Redraw;
                _subscribed = true;
            }
            Redraw();
        };
        Unloaded += (_, _) =>
        {
            App.Sensors.ValuesChanged -= Redraw;
            _subscribed = false;
        };
    }

    /// <summary>Sensor kind ("Temperature", "Load", "Fan"...): sets the units and the scale.</summary>
    public string Kind { get; set; } = "";

    /// <summary>Shows one sensor's history (index in the catalog).</summary>
    public int SensorIndex
    {
        get => _sensorIndex;
        set
        {
            _sensorIndex = value;
            Source = buffer => App.History.CopyTo(value, buffer);
        }
    }

    private int _sensorIndex = -1;

    /// <summary>Fills the buffer with the samples, oldest first, and returns how many there are.</summary>
    public Func<float[], int>? Source { get; set; }

    /// <summary>"5 min ago … Now" under the chart (off for the upper of two charts that share it).</summary>
    public bool ShowTimeAxis
    {
        get => TimeAxis.Visibility == Visibility.Visible;
        set => TimeAxis.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Redraw()
    {
        double width = Plot.ActualWidth, height = Plot.ActualHeight;
        if (width <= 0 || height <= 0 || Visibility != Visibility.Visible)
        {
            return;
        }
        _count = Source?.Invoke(_values) ?? 0;

        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = 0; i < _count; i++)
        {
            if (float.IsFinite(_values[i]))
            {
                lo = Math.Min(lo, _values[i]);
                hi = Math.Max(hi, _values[i]);
            }
        }
        var points = new PointCollection();
        if (hi < lo)
        {
            Line.Points = points;
            TopLabel.Text = MidLabel.Text = BottomLabel.Text = "";
            HideHover();
            return;
        }
        (_lo, _hi) = Bounds(Kind, lo, hi);
        Set(TopLabel, Label(_hi));
        Set(MidLabel, Label((_lo + _hi) / 2));
        Set(BottomLabel, Label(_lo));

        _step = width / (SensorHistory.Capacity - 1);
        _x0 = width - (_count - 1) * _step;
        for (int i = 0; i < _count; i++)
        {
            if (float.IsFinite(_values[i]))
            {
                points.Add(new Point(_x0 + i * _step, Y(_values[i], height)));
            }
        }
        Line.Points = points;
        ShowHover();
    }

    private double Y(float value, double height) =>
        Math.Clamp((_hi - value) / (_hi - _lo), 0, 1) * (height - 2) + 1;

    /// <summary>
    /// Round bounds with the middle line on a round value too. Percentages always 0–100 and fan speeds from 0, so
    /// the line's height means the same every time; others fit the data, at least 10 °C for a temperature so a
    /// steady degree of wobble does not look like a mountain.
    /// </summary>
    private static (float Lo, float Hi) Bounds(string kind, float lo, float hi)
    {
        if (kind is "Load" or "Control" or "Level" or "Humidity")
        {
            return (0, hi > 100 ? MathF.Ceiling(hi / 50) * 50 : 100);
        }
        if (kind == "Fan")
        {
            lo = 0;
            hi = Math.Max(hi, 500);
        }
        float minSpan = kind == "Temperature" ? 10 : Math.Max(Math.Abs(hi) * 0.1f, 0.01f);
        if (hi - lo < minSpan)
        {
            float mid = (hi + lo) / 2;
            lo = mid - minSpan / 2;
            hi = mid + minSpan / 2;
        }
        float step = Nice(hi - lo, 2);
        float bottom = MathF.Floor(lo / step) * step;
        float top = MathF.Ceiling(hi / step) * step;
        if (bottom < 0 && lo >= 0)
        {
            bottom = 0;
        }
        if (top <= bottom)
        {
            top = bottom + 2 * step;
        }
        else if ((int)MathF.Round((top - bottom) / step) % 2 != 0)
        {
            top += step;   // two equal halves, so the middle line has a round value
        }
        return (bottom, top);
    }

    /// <summary>A round step (1, 2, 2.5 or 5 × 10ⁿ) that splits <paramref name="span"/> into about <paramref name="parts"/>.</summary>
    private static float Nice(float span, int parts)
    {
        float raw = span / parts;
        if (raw <= 0 || !float.IsFinite(raw))
        {
            return 1;
        }
        float magnitude = MathF.Pow(10, MathF.Floor(MathF.Log10(raw)));
        float f = raw / magnitude;
        float nice = f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5f ? 2.5f : f <= 5 ? 5 : 10;
        return nice * magnitude;
    }

    private string Label(float value) => Kind switch
    {
        "Temperature" => value.ToString("0", CultureInfo.CurrentCulture) + "°",
        "Fan" => value.ToString("0", CultureInfo.CurrentCulture),
        _ => SensorFormat.Format(Kind, value),
    };

    // ---------- Value under the pointer ----------

    private void Plot_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        _pointer = e.GetCurrentPoint(Plot).Position;
        ShowHover();
    }

    private void Plot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointer = null;
        HideHover();
    }

    private void Plot_SizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void ShowHover()
    {
        if (_pointer is not { } p || _count == 0 || _step <= 0)
        {
            HideHover();
            return;
        }
        int i = (int)Math.Round((p.X - _x0) / _step);
        if (i < 0 || i >= _count || !float.IsFinite(_values[i]))
        {
            HideHover();
            return;
        }
        double height = Plot.ActualHeight, width = Plot.ActualWidth;
        double x = _x0 + i * _step, y = Y(_values[i], height);
        HoverLine.X1 = HoverLine.X2 = x;
        HoverLine.Y1 = 0;
        HoverLine.Y2 = height;
        Canvas.SetLeft(HoverDot, x - HoverDot.Width / 2);
        Canvas.SetTop(HoverDot, y - HoverDot.Height / 2);

        HoverText.Text = $"{SensorFormat.Format(Kind, _values[i])} · {Ago(_count - 1 - i)}";
        HoverTip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var tip = HoverTip.DesiredSize;
        // Beside the line, on the side with room; above the dot if it fits, else below it.
        double left = x + 10 + tip.Width <= width ? x + 10 : x - 10 - tip.Width;
        double top = y - tip.Height - 6 >= 0 ? y - tip.Height - 6 : Math.Min(y + 6, height - tip.Height);
        Canvas.SetLeft(HoverTip, Math.Max(0, left));
        Canvas.SetTop(HoverTip, Math.Max(0, top));

        HoverLine.Visibility = HoverDot.Visibility = HoverTip.Visibility = Visibility.Visible;
    }

    private void HideHover() => HoverLine.Visibility = HoverDot.Visibility = HoverTip.Visibility = Visibility.Collapsed;

    private static string Ago(int seconds) => seconds switch
    {
        0 => "now",
        < 60 => $"{seconds} s ago",
        _ when seconds % 60 == 0 => $"{seconds / 60} min ago",
        _ => $"{seconds / 60} min {seconds % 60} s ago",
    };

    private static void Set(TextBlock block, string text)
    {
        if (block.Text != text)
        {
            block.Text = text;
        }
    }
}
