using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace NotOnlyFans.Services;

/// <summary>Draws a small history line (newest sample at the right edge, 5 minutes across).</summary>
public static class Sparkline
{
    /// <summary>
    /// Redraws <paramref name="line"/> to fill width × height. The vertical scale is at least
    /// <paramref name="minRange"/> wide, so a steady 1-degree wobble does not look like a mountain.
    /// Returns the highest value in the history (null if there is none).
    /// </summary>
    public static float? Draw(Polyline line, double width, double height, ReadOnlySpan<float> values, float minRange)
    {
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var v in values)
        {
            if (float.IsFinite(v))
            {
                lo = Math.Min(lo, v);
                hi = Math.Max(hi, v);
            }
        }

        var points = new PointCollection();
        if (hi < lo || width <= 0 || height <= 0)
        {
            line.Points = points;
            return null;
        }

        float max = hi;
        if (hi - lo < minRange)
        {
            float mid = (hi + lo) / 2;
            lo = mid - minRange / 2;
            hi = mid + minRange / 2;
        }

        const double pad = 2;
        double step = width / (SensorHistory.Capacity - 1);
        double x0 = width - (values.Length - 1) * step;
        for (int i = 0; i < values.Length; i++)
        {
            if (!float.IsFinite(values[i]))
            {
                continue;
            }
            double y = pad + (hi - values[i]) / (hi - lo) * (height - 2 * pad);
            points.Add(new Point(x0 + i * step, y));
        }
        line.Points = points;
        return max;
    }
}
