using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace NotOnlyFans.Controls;

/// <summary>
/// A text link ("Show all", "Open Cooling"...): just colored text. Under the pointer only the color changes
/// (lighter accent, darker while pressed) and the pointer becomes a hand - no box around the words, so nothing to
/// sit unevenly or with square corners next to the rounded cards.
/// As in Windows and the Fluent guidelines, only links get the hand; buttons, rows and tiles keep the arrow and show
/// they can be clicked by lighting up. (The hand also means "carry" while a row is dragged.)
/// </summary>
public sealed partial class LinkButton : HyperlinkButton
{
    private static readonly InputCursor Hand = InputSystemCursor.Create(InputSystemCursorShape.Hand);

    public LinkButton()
    {
        ProtectedCursor = Hand;
        Padding = new Thickness(0);
        MinWidth = 0;
        MinHeight = 0;
        // The template's own brushes for the box: transparent in every state (the text colors stay the theme's).
        var none = new SolidColorBrush(Colors.Transparent);
        foreach (var key in new[]
        {
            "HyperlinkButtonBackground", "HyperlinkButtonBackgroundPointerOver", "HyperlinkButtonBackgroundPressed",
            "HyperlinkButtonBackgroundDisabled", "HyperlinkButtonBorderBrush", "HyperlinkButtonBorderBrushPointerOver",
            "HyperlinkButtonBorderBrushPressed", "HyperlinkButtonBorderBrushDisabled",
        })
        {
            Resources[key] = none;
        }
    }
}
