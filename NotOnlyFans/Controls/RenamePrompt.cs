using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace NotOnlyFans.Controls;

/// <summary>
/// The small "rename" flyout used everywhere (fans on Home, sensors on Sensors):
/// a text box next to the item, Enter or Save keeps the name, Esc or Cancel closes,
/// an empty name goes back to the original one (shown as the placeholder).
/// </summary>
public static class RenamePrompt
{
    public static void Show(FrameworkElement target, string header, string currentName, string originalName, Action<string> save)
    {
        var box = new TextBox
        {
            Header = header,
            Text = currentName,
            PlaceholderText = originalName,
        };
        var saveButton = new Button
        {
            Content = "Save",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        var cancelButton = new Button { Content = "Cancel" };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(saveButton);
        buttons.Children.Add(cancelButton);

        var panel = new StackPanel { Spacing = 12, Width = 280 };
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var flyout = new Flyout { Placement = FlyoutPlacementMode.Bottom, Content = panel };

        void Commit()
        {
            save(box.Text);
            flyout.Hide();
        }

        saveButton.Click += (_, _) => Commit();
        cancelButton.Click += (_, _) => flyout.Hide();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                Commit();
            }
        };
        flyout.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };

        flyout.ShowAt(target);
    }
}
