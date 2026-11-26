using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace NotOnlyFans.Controls;

/// <summary>
/// A number with a button on each side: ‹ 45 › (left = less, right = more), instead of NumberBox's tiny
/// up/down spin arrows. Hold a button to keep changing. The value can also be typed (Enter or leaving
/// the field applies it), and changed with ↑ / ↓ (PageUp / PageDown for bigger steps).
/// Whole numbers only. <see cref="ValueChanged"/> fires only for the user's changes, never for <see cref="Show"/>.
/// </summary>
public sealed partial class Stepper : UserControl
{
    // After the user changes the value, the service's echo of an older value must not pull it back.
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(1.5);

    private readonly RepeatButton _less;
    private readonly RepeatButton _more;
    private readonly TextBox _box;
    private double _value;
    private DateTime _touched;

    public Stepper()
    {
        _less = MakeButton("", "Less", -1);
        _more = MakeButton("", "More", +1);
        _box = new TextBox
        {
            Width = 56,
            MinWidth = 0,
            TextAlignment = TextAlignment.Center,
            Padding = new Thickness(4, 5, 4, 6),
            IsSpellCheckEnabled = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _box.PreviewKeyDown += Box_PreviewKeyDown;
        _box.LostFocus += (_, _) => CommitTyped();
        _box.Loaded += (_, _) => HideClearButton(_box);

        var grid = new Grid { ColumnSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_box, 1);
        Grid.SetColumn(_more, 2);
        grid.Children.Add(_less);
        grid.Children.Add(_box);
        grid.Children.Add(_more);
        Content = grid;

        IsTabStop = false;
        VerticalAlignment = VerticalAlignment.Center;
        Loaded += (_, _) =>
        {
            // The name given to the whole control (AutomationProperties.Name) describes the field and its buttons.
            string name = AutomationProperties.GetName(this);
            if (!string.IsNullOrEmpty(name))
            {
                AutomationProperties.SetName(_box, name);
                AutomationProperties.SetName(_less, $"Less {name}");
                AutomationProperties.SetName(_more, $"More {name}");
            }
            Update();
        };
    }

    public double Minimum { get; set; }
    public double Maximum { get; set; } = 100;
    public double SmallChange { get; set; } = 1;
    public double LargeChange { get; set; } = 10;

    public double Value => _value;

    /// <summary>The user changed the value (button, typing or keyboard).</summary>
    public event Action<Stepper, double>? ValueChanged;

    /// <summary>
    /// Shows a value from outside (e.g. what the service runs). Ignored while the user is typing
    /// or right after the user changed it.
    /// </summary>
    public void Show(double value)
    {
        if (_box.FocusState != FocusState.Unfocused || DateTime.UtcNow - _touched < Quiet)
        {
            return;
        }
        value = Math.Clamp(Math.Round(value), Minimum, Maximum);
        if (value != _value || _box.Text.Length == 0)
        {
            _value = value;
            Update();
        }
    }

    private RepeatButton MakeButton(string glyph, string tooltip, int direction)
    {
        var button = new RepeatButton
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Delay = 400,
            Interval = 60,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
        };
        ToolTipService.SetToolTip(button, tooltip);
        button.Click += (_, _) => Change(_value + direction * SmallChange);
        return button;
    }

    private void Change(double value)
    {
        value = Math.Clamp(Math.Round(value), Minimum, Maximum);
        _touched = DateTime.UtcNow;
        if (value == _value)
        {
            Update();   // e.g. typed text that rounds to the same value
            return;
        }
        _value = value;
        Update();
        ValueChanged?.Invoke(this, value);
    }

    private void Update()
    {
        _box.Text = _value.ToString("0", CultureInfo.CurrentCulture);
        _less.IsEnabled = _value > Minimum;
        _more.IsEnabled = _value < Maximum;
    }

    private void CommitTyped()
    {
        if (double.TryParse(_box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double typed) && double.IsFinite(typed))
        {
            Change(typed);
        }
        else
        {
            Update();   // not a number: back to the current value
        }
    }

    private void Box_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                CommitTyped();
                _box.SelectAll();
                break;
            case VirtualKey.Up:
                Change(_value + SmallChange);
                break;
            case VirtualKey.Down:
                Change(_value - SmallChange);
                break;
            case VirtualKey.PageUp:
                Change(_value + LargeChange);
                break;
            case VirtualKey.PageDown:
                Change(_value - LargeChange);
                break;
            case VirtualKey.Escape:
                Update();   // drop what was typed
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>The field is narrow; the text box's "clear" (×) button would cover the number.</summary>
    private static void HideClearButton(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button { Name: "DeleteButton" } delete)
            {
                delete.MaxWidth = 0;
                delete.IsTabStop = false;
                return;
            }
            HideClearButton(child);
        }
    }
}
