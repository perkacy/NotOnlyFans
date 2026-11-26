using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace NotOnlyFans.Controls;

/// <summary>Children side by side, continuing on the next line when the width runs out (e.g. the fan switcher in Cooling).</summary>
public sealed class WrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 8;

    public double VerticalSpacing { get; set; } = 8;

    protected override Size MeasureOverride(Size availableSize)
    {
        double x = 0, y = 0, line = 0, widest = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > availableSize.Width)
            {
                y += line + VerticalSpacing;
                x = 0;
                line = 0;
            }
            x += size.Width + HorizontalSpacing;
            line = Math.Max(line, size.Height);
            widest = Math.Max(widest, x - HorizontalSpacing);
        }
        return new Size(widest, y + line);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, line = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > finalSize.Width)
            {
                y += line + VerticalSpacing;
                x = 0;
                line = 0;
            }
            child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width + HorizontalSpacing;
            line = Math.Max(line, size.Height);
        }
        return finalSize;
    }
}
