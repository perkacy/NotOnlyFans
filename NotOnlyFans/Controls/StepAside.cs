using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace NotOnlyFans.Controls;

/// <summary>
/// While a row is dragged in a list, the rows it passes step aside to show where it will land.
/// Short (120 ms) and interruptible: a change of mind mid-move starts from where a row is now.
/// With Windows "Animation effects" off the rows jump. Used through RowReorder (Home, Cooling, Pinned).
/// </summary>
public sealed class StepAside
{
    public const double DragThreshold = 4;
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(120);

    private readonly bool _animate = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
    private readonly Dictionary<UIElement, (double Target, Storyboard? Story)> _moved = [];

    /// <summary>Moves a passed-over row by <paramref name="shift"/> (0 = back to its place).</summary>
    public void Move(UIElement element, double shift)
    {
        if (_moved.TryGetValue(element, out var current) && current.Target == shift)
        {
            return;
        }
        current.Story?.Stop();
        var transform = Translate(element);
        if (!_animate)
        {
            transform.Y = shift;
            _moved[element] = (shift, null);
            return;
        }

        var animation = new DoubleAnimation
        {
            From = transform.Y,
            To = shift,
            Duration = Duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, transform);
        Storyboard.SetTargetProperty(animation, "Y");
        var story = new Storyboard();
        story.Children.Add(animation);
        story.Begin();
        _moved[element] = (shift, story);
    }

    /// <summary>Every moved row back in place at once (the list is then reordered for real).</summary>
    public void Reset()
    {
        foreach (var (element, (_, story)) in _moved)
        {
            story?.Stop();
            Translate(element).Y = 0;
        }
        _moved.Clear();
    }

    public static TranslateTransform Translate(UIElement element)
    {
        if (element.RenderTransform is not TranslateTransform t)
        {
            t = new TranslateTransform();
            element.RenderTransform = t;
        }
        return t;
    }
}
