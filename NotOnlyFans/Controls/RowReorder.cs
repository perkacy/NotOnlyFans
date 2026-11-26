using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace NotOnlyFans.Controls;

/// <summary>
/// Drag to reorder rows in a vertical list: Home › Fans, Cooling's fan list and the Pinned list.
/// Our own drag instead of ListView reordering: the row follows the pointer 1:1 with a hand cursor,
/// the rows it passes step aside at once (<see cref="StepAside"/>), and the list is reordered once, on release.
/// The page forwards the row's pointer events (Pressed / Moved / Released / CaptureLost) and says what a move does.
/// </summary>
public sealed class RowReorder(UIElement list, Func<IReadOnlyList<FrameworkElement>> rows)
{
    public static readonly InputCursor HandCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);

    // After a drag the row is under the pointer, so a Button row would also report a click: ignore it.
    private static readonly TimeSpan ClickAfterDrag = TimeSpan.FromMilliseconds(300);

    private readonly StepAside _stepAside = new();
    private FrameworkElement? _row;
    private IReadOnlyList<FrameworkElement> _rows = [];
    private int _from, _to;
    private double _startY;
    private bool _dragging;
    private DateTime _dragEnded;
    private double[] _tops = [], _heights = [];

    /// <summary>Released at a new place: (from, to) list positions. The page reorders and saves.</summary>
    public event Action<int, int>? Moved;

    /// <summary>Pressed and released without dragging (Home opens the fan).</summary>
    public event Action<FrameworkElement>? Clicked;

    /// <summary>
    /// The row is picked up (true) or put down (false): the page shows the hand on itself too (the pointer can
    /// leave the row at the list's ends) and may change the row's look.
    /// </summary>
    public event Action<FrameworkElement, bool>? Lifted;

    /// <summary>True while a row is carried, and just after: a Button row's Click then is the end of a drag.</summary>
    public bool JustDragged => _dragging || DateTime.UtcNow - _dragEnded < ClickAfterDrag;

    public void Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement row || !e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
        {
            return;
        }
        var shown = rows();
        int index = IndexOf(shown, row);
        if (index < 0)
        {
            return;
        }
        _row = row;
        _rows = shown;
        _from = _to = index;
        _startY = e.GetCurrentPoint(list).Position.Y;
        _dragging = false;
        row.CapturePointer(e.Pointer);
    }

    public void PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_row is null || !ReferenceEquals(sender, _row))
        {
            return;
        }
        double dy = e.GetCurrentPoint(list).Position.Y - _startY;
        if (!_dragging)
        {
            if (Math.Abs(dy) < StepAside.DragThreshold)
            {
                return;
            }
            Start();
        }

        // Keep the row inside the list.
        double top = _tops[_from];
        double h = _heights[_from];
        double end = _tops[^1] + _heights[^1];
        dy = Math.Clamp(dy, _tops[0] - top, end - h - top);
        StepAside.Translate(_row).Y = dy;

        // Where it would land: the slot under the row's centre.
        double centre = top + h / 2 + dy;
        int to = 0;
        while (to < _tops.Length - 1 && centre > _tops[to] + _heights[to])
        {
            to++;
        }
        if (to == _to)
        {
            return;
        }
        _to = to;
        for (int i = 0; i < _rows.Count; i++)
        {
            if (i != _from)
            {
                double shift =
                    _from < to && i > _from && i <= to ? -h :
                    to < _from && i >= to && i < _from ? h : 0;
                _stepAside.Move(_rows[i], shift);
            }
        }
    }

    public void Released(object sender, PointerRoutedEventArgs e)
    {
        if (_row is not { } row || !ReferenceEquals(sender, row))
        {
            return;
        }
        bool click = !_dragging;
        row.ReleasePointerCapture(e.Pointer);
        End();
        if (click)
        {
            Clicked?.Invoke(row);
        }
    }

    public void CaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _row))
        {
            End();
        }
    }

    private void Start()
    {
        _dragging = true;
        _tops = new double[_rows.Count];
        _heights = new double[_rows.Count];
        for (int i = 0; i < _rows.Count; i++)
        {
            _tops[i] = _rows[i].TransformToVisual(list).TransformPoint(default).Y;
            _heights[i] = _rows[i].ActualHeight;
        }
        Canvas.SetZIndex(_row, 1);   // carried above the others
        SetCursor(_row!, HandCursor);
        Lifted?.Invoke(_row!, true);
    }

    private void End()
    {
        var row = _row;
        _row = null;
        if (row is null || !_dragging)
        {
            return;
        }
        _dragging = false;
        _dragEnded = DateTime.UtcNow;
        _stepAside.Reset();
        StepAside.Translate(row).Y = 0;
        Canvas.SetZIndex(row, 0);
        SetCursor(row, null);
        Lifted?.Invoke(row, false);
        if (_to != _from)
        {
            Moved?.Invoke(_from, _to);
        }
    }

    /// <summary>The hand on the carried row (a <see cref="DragRow"/>, or the one inside a Button / list item).</summary>
    private static void SetCursor(FrameworkElement row, InputCursor? cursor)
    {
        var target = row as DragRow ?? (row as ContentControl)?.Content as DragRow;
        if (target is not null)
        {
            target.Cursor = cursor;
        }
    }

    private static int IndexOf(IReadOnlyList<FrameworkElement> shown, FrameworkElement row)
    {
        for (int i = 0; i < shown.Count; i++)
        {
            if (ReferenceEquals(shown[i], row))
            {
                return i;
            }
        }
        return -1;
    }
}
