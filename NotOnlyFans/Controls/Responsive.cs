using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace NotOnlyFans.Controls;

/// <summary>
/// Layouts that follow the width they get, so a narrow window stays readable (as Windows Settings does).
/// Set in XAML on a Grid, also inside item templates:
/// <list type="bullet">
/// <item><c>Responsive.Hide="3:600 1:440"</c> - column 3 (and what is in it) goes away when the grid is narrower
///   than 600, column 1 below 440. The less important columns go first; the name column keeps its room.</item>
/// <item><c>Responsive.LabelAbove="560"</c> - below 560 the label (column 0) moves above the controls (the other
///   columns), like a setting row in Windows Settings at a narrow width.</item>
/// </list>
/// Only crossing a width changes the layout, so resizing costs nothing in between. The width is the room the parent
/// gives the grid, not the grid's own size: a layout that came out wider than its room must not flip itself back
/// (that would loop forever and freeze the window).
/// </summary>
public static class Responsive
{
    public static readonly DependencyProperty HideProperty = DependencyProperty.RegisterAttached(
        "Hide", typeof(string), typeof(Responsive), new PropertyMetadata(null, (d, _) => Watch(d)));

    public static string GetHide(DependencyObject element) => (string)element.GetValue(HideProperty);

    public static void SetHide(DependencyObject element, string value) => element.SetValue(HideProperty, value);

    public static readonly DependencyProperty LabelAboveProperty = DependencyProperty.RegisterAttached(
        "LabelAbove", typeof(double), typeof(Responsive), new PropertyMetadata(0.0, (d, _) => Watch(d)));

    public static double GetLabelAbove(DependencyObject element) => (double)element.GetValue(LabelAboveProperty);

    public static void SetLabelAbove(DependencyObject element, double value) => element.SetValue(LabelAboveProperty, value);

    /// <summary>The grid as written in XAML, and what is applied now.</summary>
    private sealed class Layout
    {
        public required (GridLength Width, double MinWidth, double MaxWidth)[] Columns;
        public required Dictionary<FrameworkElement, (int Row, int Column, int Span)> Places;
        public required int Rows;
        public required double RowSpacing;
        public readonly HashSet<UIElement> Collapsed = [];
        public string Applied = "";
        public DateTime LastChange;
        public int QuickChanges;
    }

    /// <summary>
    /// The original layout is kept on the grid itself: WinUI may hand out a new C# wrapper for the same grid, so a
    /// table keyed by the wrapper would forget it (and take the narrow layout for the original).
    /// </summary>
    private static readonly DependencyProperty LayoutProperty = DependencyProperty.RegisterAttached(
        "Layout", typeof(object), typeof(Responsive), new PropertyMetadata(null));

    private static Layout GetLayout(Grid grid)
    {
        if (grid.GetValue(LayoutProperty) is not Layout layout)
        {
            layout = Capture(grid);
            grid.SetValue(LayoutProperty, layout);
        }
        return layout;
    }

    private static void Watch(DependencyObject d)
    {
        if (d is not Grid grid)
        {
            return;
        }
        grid.SizeChanged -= Grid_SizeChanged;
        grid.SizeChanged += Grid_SizeChanged;
        if (grid.ActualWidth > 0)
        {
            Update(grid, Room(grid));
        }
    }

    private static void Grid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width != e.PreviousSize.Width)
        {
            var grid = (Grid)sender;
            Update(grid, Room(grid));
        }
    }

    /// <summary>The width the parent offers the grid (its layout slot without margins).</summary>
    private static double Room(Grid grid)
    {
        double slot = LayoutInformation.GetLayoutSlot(grid).Width - grid.Margin.Left - grid.Margin.Right;
        return slot > 0 && double.IsFinite(slot) ? slot : grid.ActualWidth;
    }

    private static void Update(Grid grid, double width)
    {
        var hidden = new SortedSet<int>();
        foreach (var rule in (GetHide(grid) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = rule.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out int column)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double below) && width < below)
            {
                hidden.Add(column);
            }
        }
        double labelAbove = GetLabelAbove(grid);
        bool above = labelAbove > 0 && width < labelAbove;
        string state = string.Join(',', hidden) + (above ? "|above" : "");

        var layout = GetLayout(grid);
        if (state == layout.Applied)
        {
            return;
        }

        // Last line of defence against a layout feeding back into itself: a grid that keeps changing within moments
        // stays as it is until the next resize.
        var now = DateTime.UtcNow;
        layout.QuickChanges = now - layout.LastChange < TimeSpan.FromMilliseconds(100) ? layout.QuickChanges + 1 : 0;
        layout.LastChange = now;
        if (layout.QuickChanges >= 4)
        {
            return;
        }

        Restore(grid, layout);
        layout.Applied = state;

        foreach (int column in hidden)
        {
            if (column < grid.ColumnDefinitions.Count)
            {
                var definition = grid.ColumnDefinitions[column];
                definition.MinWidth = 0;
                definition.Width = new GridLength(0, GridUnitType.Pixel);
                definition.MaxWidth = 0;
                foreach (var (child, place) in layout.Places)
                {
                    if (place.Column == column && child.Visibility == Visibility.Visible)
                    {
                        child.Visibility = Visibility.Collapsed;
                        layout.Collapsed.Add(child);
                    }
                }
            }
        }

        if (above && grid.ColumnDefinitions.Count > 1)
        {
            // Column 0 goes away; the label takes a row of its own above everything else.
            var first = grid.ColumnDefinitions[0];
            first.MinWidth = 0;
            first.Width = new GridLength(0, GridUnitType.Pixel);
            first.MaxWidth = 0;
            if (!grid.ColumnDefinitions.Skip(1).Any(c => c.Width.IsStar))
            {
                // Only Auto/fixed columns left: an empty star column at the end lets the label wrap to the grid's width
                // (spanning just Auto columns, it would stretch them to its unwrapped length and get cut off).
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
            if (grid.RowDefinitions.Count == 0)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            grid.RowDefinitions.Insert(0, new RowDefinition { Height = GridLength.Auto });
            grid.RowSpacing = Math.Max(grid.RowSpacing, 8);
            foreach (var (child, place) in layout.Places)
            {
                if (place.Column == 0)
                {
                    Grid.SetRow(child, 0);
                    Grid.SetColumnSpan(child, grid.ColumnDefinitions.Count);
                }
                else
                {
                    Grid.SetRow(child, place.Row + 1);
                }
            }
        }

        // A child right after emptied columns spans them too: it starts at the left edge, not after their spacing.
        foreach (var (child, place) in layout.Places)
        {
            int start = place.Column;
            while (start > 0 && (hidden.Contains(start - 1) || (above && start - 1 == 0)))
            {
                start--;
            }
            if (start != place.Column && !(above && place.Column == 0))
            {
                Grid.SetColumn(child, start);
                Grid.SetColumnSpan(child, place.Span + place.Column - start);
            }
        }
    }

    private static Layout Capture(Grid grid) => new()
    {
        Columns = grid.ColumnDefinitions.Select(c => (c.Width, c.MinWidth, c.MaxWidth)).ToArray(),
        Places = grid.Children.OfType<FrameworkElement>().ToDictionary(c => c, c => (Grid.GetRow(c), Grid.GetColumn(c),
            Grid.GetColumnSpan(c))),
        Rows = grid.RowDefinitions.Count,
        RowSpacing = grid.RowSpacing,
    };

    private static void Restore(Grid grid, Layout layout)
    {
        if (layout.Applied.Length == 0)
        {
            return;
        }
        while (grid.ColumnDefinitions.Count > layout.Columns.Length)
        {
            grid.ColumnDefinitions.RemoveAt(grid.ColumnDefinitions.Count - 1);
        }
        for (int i = 0; i < layout.Columns.Length && i < grid.ColumnDefinitions.Count; i++)
        {
            var (width, min, max) = layout.Columns[i];
            grid.ColumnDefinitions[i].Width = width;
            grid.ColumnDefinitions[i].MinWidth = min;
            grid.ColumnDefinitions[i].MaxWidth = max;
        }
        while (grid.RowDefinitions.Count > layout.Rows)
        {
            grid.RowDefinitions.RemoveAt(0);
        }
        grid.RowSpacing = layout.RowSpacing;
        foreach (var (child, place) in layout.Places)
        {
            Grid.SetRow(child, place.Row);
            Grid.SetColumn(child, place.Column);
            Grid.SetColumnSpan(child, place.Span);
        }
        foreach (var child in layout.Collapsed)
        {
            child.Visibility = Visibility.Visible;
        }
        layout.Collapsed.Clear();
        layout.Applied = "";
    }
}
