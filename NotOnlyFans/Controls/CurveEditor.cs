using System.Globalization;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NotOnlyFans.Contracts;
using NotOnlyFans.Services;
using Windows.Foundation;

namespace NotOnlyFans.Controls;

/// <summary>
/// Fan curve chart from the Cooling mockup: 20–100 °C across, 0–100 % up, the curve with a light fill,
/// and a "Now" marker (source temperature and the fan's speed).
/// Drag a point to move it (1 °C / 1 % steps, never past its neighbours), double-click empty space to add
/// a point, right-click a point to remove it. <see cref="CurveCommitted"/> fires when the user lets go.
/// Keyboard (Tab to the chart): ← → choose a point, ↑ ↓ its speed, Ctrl + ← → its temperature, Shift = 5× steps,
/// Insert / + add a point after it, Delete remove it, Home / End first / last point.
/// Shapes are made once and only moved, so a drag survives the once-a-second refresh.
/// </summary>
public sealed partial class CurveEditor : UserControl
{
    /// <summary>Keys, for a hint next to the chart while it has keyboard focus.</summary>
    public const string KeyboardHint =
        "← → choose a point · ↑ ↓ speed · Ctrl + ← → temperature · Shift: steps of 5 · Insert add · Delete remove";

    // After a key press, the service's echo of the older curve must not pull the point back.
    private static readonly TimeSpan KeyQuiet = TimeSpan.FromSeconds(1.5);

    private const float MinTemp = 20, MaxTemp = 100;
    private const int MaxPoints = 12, MinPoints = 2;
    private const double PadLeft = 44, PadRight = 16, PadTop = 12, PadBottom = 28, HandleSize = 12;

    private static readonly InputCursor Hand = InputSystemCursor.Create(InputSystemCursorShape.Hand);

    private readonly Canvas _canvas = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly Canvas _gridLayer = new();
    private readonly Polygon _fill = new();
    private readonly Polyline _line = new() { StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
    private readonly Line _nowLine = new() { StrokeThickness = 1, StrokeDashArray = [4, 4] };
    private readonly Ellipse _nowDot = new() { Width = 14, Height = 14, StrokeThickness = 3 };
    private readonly TextBlock _nowText = new() { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _dragText = new() { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Visibility = Visibility.Collapsed };
    private readonly List<Ellipse> _handles = [];
    private readonly List<Polyline> _ghosts = [];

    private List<CurvePoint> _points = [];
    private List<List<CurvePoint>> _ghostPoints = [];
    private float? _nowTemp, _nowSpeed;
    private string _nowLabel = "";
    private int _dragIndex = -1;
    private int _selected;
    private DateTime _keyTouched;

    public CurveEditor()
    {
        Height = 250;
        Content = _canvas;
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        KeyDown += CurveEditor_KeyDown;
        KeyUp += CurveEditor_KeyUp;
        GotFocus += (_, _) =>
        {
            _focused = true;
            FocusChanged();
        };
        LostFocus += (_, _) =>
        {
            _focused = false;
            _byPointer = false;
            _ctrl = _shift = false;   // a key released elsewhere must not stick
            FocusChanged();
        };
        // A click means mouse use (no keyboard highlight); the next key press switches to keyboard use.
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) =>
        {
            _byPointer = true;
            FocusChanged();
        }), handledEventsToo: true);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(this, KeyboardHint);
        _canvas.Children.Add(_gridLayer);
        _canvas.Children.Add(_fill);
        _canvas.Children.Add(_line);
        _canvas.Children.Add(_nowLine);
        _canvas.Children.Add(_nowText);
        _canvas.Children.Add(_nowDot);
        _canvas.Children.Add(_dragText);

        _canvas.DoubleTapped += Canvas_DoubleTapped;
        SizeChanged += (_, _) => Redraw();
        Theme.Follow(this, Redraw);   // also when the theme changed while the chart was off screen
        AutomationProperties_SetName("Fan curve");
    }

    /// <summary>The user changed the curve (released a point, added or removed one).</summary>
    public event Action<List<CurvePoint>>? CurveCommitted;

    /// <summary>The chart got or lost keyboard focus (true = show <see cref="KeyboardHint"/>).</summary>
    public event Action<bool>? KeyboardFocusChanged;

    public bool IsDragging => _dragIndex >= 0;

    private bool _focused, _byPointer;

    // Focused by Tab, a key press or a program such as Narrator – not by a mouse click.
    private bool HasKeyboardFocus => _focused && !_byPointer;

    /// <summary>Shows a curve (ignored while the user is dragging or moving a point with the keyboard).</summary>
    public void SetCurve(IReadOnlyList<CurvePoint> points)
    {
        if (IsDragging || (FocusState != FocusState.Unfocused && DateTime.UtcNow - _keyTouched < KeyQuiet))
        {
            return;
        }
        _points = points.Select(p => new CurvePoint { Temperature = p.Temperature, Speed = p.Speed }).ToList();
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _points.Count - 1));
        SyncHandles();
        UpdateShapes();
    }

    /// <summary>Other curves drawn faintly behind the edited one (the rest of a Mix), not editable.</summary>
    public void SetGhosts(IEnumerable<IReadOnlyList<CurvePoint>> curves)
    {
        _ghostPoints = curves.Select(c => c.ToList()).ToList();
        while (_ghosts.Count < _ghostPoints.Count)
        {
            var g = new Polyline
            {
                StrokeThickness = 1.5,
                StrokeDashArray = [3, 3],
                StrokeLineJoin = PenLineJoin.Round,
                Stroke = Res("TextFillColorTertiaryBrush"),
                IsHitTestVisible = false,
            };
            _ghosts.Add(g);
            _canvas.Children.Insert(1, g);   // above the grid, below the edited curve
        }
        while (_ghosts.Count > _ghostPoints.Count)
        {
            _canvas.Children.Remove(_ghosts[^1]);
            _ghosts.RemoveAt(_ghosts.Count - 1);
        }
        UpdateGhosts();
    }

    public void SetNow(float? temperature, float? speed, string label)
    {
        _nowTemp = temperature;
        _nowSpeed = speed;
        _nowLabel = label;
        UpdateNow();
    }

    // ---------- Drawing ----------

    private double PlotWidth => Math.Max(1, ActualWidth - PadLeft - PadRight);
    private double PlotHeight => Math.Max(1, ActualHeight - PadTop - PadBottom);
    private double X(float t) => PadLeft + (Math.Clamp(t, MinTemp, MaxTemp) - MinTemp) / (MaxTemp - MinTemp) * PlotWidth;
    private double Y(float s) => PadTop + (1 - Math.Clamp(s, 0, 100) / 100) * PlotHeight;
    private float TempAt(double x) => (float)(MinTemp + (x - PadLeft) / PlotWidth * (MaxTemp - MinTemp));
    private float SpeedAt(double y) => (float)((1 - (y - PadTop) / PlotHeight) * 100);

    private Brush Res(string key) => Theme.Brush(this, key);   // follows the app theme, not only Windows

    private void Redraw()
    {
        var divider = Res("DividerStrokeColorDefaultBrush");
        var tertiary = Res("TextFillColorTertiaryBrush");
        var accent = Res("AccentFillColorDefaultBrush");

        _gridLayer.Children.Clear();
        for (int s = 0; s <= 100; s += 25)
        {
            double y = Y(s);
            _gridLayer.Children.Add(new Line { X1 = PadLeft, X2 = PadLeft + PlotWidth, Y1 = y, Y2 = y, Stroke = divider, StrokeThickness = 1 });
            var label = new TextBlock { Text = $"{s}%", FontSize = 11, Foreground = tertiary, Width = PadLeft - 8, TextAlignment = TextAlignment.Right };
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, y - 8);
            _gridLayer.Children.Add(label);
        }
        for (int t = (int)MinTemp; t <= MaxTemp; t += 20)
        {
            double x = X(t);
            _gridLayer.Children.Add(new Line { X1 = x, X2 = x, Y1 = PadTop, Y2 = PadTop + PlotHeight, Stroke = divider, StrokeThickness = 1 });
            var label = new TextBlock { Text = $"{t} °C", FontSize = 11, Foreground = tertiary, Width = 60, TextAlignment = TextAlignment.Center };
            Canvas.SetLeft(label, x - 30);
            Canvas.SetTop(label, PadTop + PlotHeight + 6);
            _gridLayer.Children.Add(label);
        }

        var accentColor = (accent as SolidColorBrush)?.Color ?? Microsoft.UI.Colors.DodgerBlue;
        _fill.Fill = new SolidColorBrush(accentColor) { Opacity = 0.12 };
        _line.Stroke = accent;
        _nowLine.Stroke = Res("TextFillColorSecondaryBrush");
        _nowText.Foreground = Res("TextFillColorPrimaryBrush");
        _dragText.Foreground = Res("TextFillColorPrimaryBrush");
        _nowDot.Fill = accent;
        _nowDot.Stroke = Res("CardBackgroundFillColorDefaultBrush");
        foreach (var h in _handles)
        {
            h.Fill = Res("SolidBackgroundFillColorBaseBrush");
            h.Stroke = accent;
        }
        foreach (var g in _ghosts)
        {
            g.Stroke = tertiary;
        }

        UpdateGhosts();
        UpdateShapes();
    }

    /// <summary>The curve as a line across the whole chart (end values held to the edges).</summary>
    private PointCollection LinePoints(List<CurvePoint> points)
    {
        var line = new PointCollection { new Point(PadLeft, Y(points[0].Speed)) };
        foreach (var p in points)
        {
            line.Add(new Point(X(p.Temperature), Y(p.Speed)));
        }
        line.Add(new Point(PadLeft + PlotWidth, Y(points[^1].Speed)));
        return line;
    }

    private void UpdateGhosts()
    {
        if (ActualWidth <= 0)
        {
            return;
        }
        for (int i = 0; i < _ghosts.Count; i++)
        {
            _ghosts[i].Points = _ghostPoints[i].Count > 0 ? LinePoints(_ghostPoints[i]) : [];
        }
    }

    private void UpdateShapes()
    {
        if (_points.Count == 0 || ActualWidth <= 0)
        {
            return;
        }
        var line = LinePoints(_points);
        _line.Points = line;

        var fill = new PointCollection();
        foreach (var p in line)
        {
            fill.Add(p);
        }
        fill.Add(new Point(PadLeft + PlotWidth, PadTop + PlotHeight));
        fill.Add(new Point(PadLeft, PadTop + PlotHeight));
        _fill.Points = fill;

        for (int i = 0; i < _handles.Count && i < _points.Count; i++)
        {
            Canvas.SetLeft(_handles[i], X(_points[i].Temperature) - HandleSize / 2);
            Canvas.SetTop(_handles[i], Y(_points[i].Speed) - HandleSize / 2);
        }
        UpdateSelection();
        UpdateNow();
    }

    /// <summary>With keyboard focus, the chosen point is filled and labelled.</summary>
    private void UpdateSelection()
    {
        bool keyboard = HasKeyboardFocus && !IsDragging;
        var normal = Res("SolidBackgroundFillColorBaseBrush");
        var accent = Res("AccentFillColorDefaultBrush");
        for (int i = 0; i < _handles.Count; i++)
        {
            _handles[i].Fill = keyboard && i == _selected ? accent : normal;
        }
        if (IsDragging)
        {
            return;   // the drag shows its own label
        }
        if (keyboard && _selected < _points.Count && ActualWidth > 0)
        {
            var p = _points[_selected];
            _dragText.Text = string.Create(CultureInfo.CurrentCulture, $"{p.Temperature:0} °C · {p.Speed:0}%");
            _dragText.Visibility = Visibility.Visible;
            _dragText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double x = X(p.Temperature) + 10;
            if (x + _dragText.DesiredSize.Width > PadLeft + PlotWidth)
            {
                x = X(p.Temperature) - 10 - _dragText.DesiredSize.Width;
            }
            Canvas.SetLeft(_dragText, x);
            Canvas.SetTop(_dragText, Math.Max(0, Y(p.Speed) - 24));
        }
        else
        {
            _dragText.Visibility = Visibility.Collapsed;
        }
    }

    private bool _hadKeyboardFocus;

    private void FocusChanged()
    {
        UpdateSelection();
        if (HasKeyboardFocus != _hadKeyboardFocus)
        {
            _hadKeyboardFocus = HasKeyboardFocus;
            KeyboardFocusChanged?.Invoke(_hadKeyboardFocus);
        }
    }

    // ---------- Keyboard ----------

    // Modifiers as seen by this control's own key events (and the system's key state as a fallback).
    private bool _ctrl, _shift;

    private static bool IsDown(Windows.System.VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void CurveEditor_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Control)
        {
            _ctrl = false;
        }
        else if (e.Key == Windows.System.VirtualKey.Shift)
        {
            _shift = false;
        }
    }

    private void CurveEditor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Control)
        {
            _ctrl = true;
            return;
        }
        if (e.Key == Windows.System.VirtualKey.Shift)
        {
            _shift = true;
            return;
        }
        if (_points.Count == 0 || IsDragging)
        {
            return;
        }
        if (_byPointer)
        {
            _byPointer = false;   // keyboard use from now on: show the chosen point and the keys
            FocusChanged();
        }
        bool ctrl = _ctrl || IsDown(Windows.System.VirtualKey.Control);
        float step = _shift || IsDown(Windows.System.VirtualKey.Shift) ? 5 : 1;
        _selected = Math.Clamp(_selected, 0, _points.Count - 1);
        var p = _points[_selected];
        bool changed = false;

        switch (e.Key)
        {
            case Windows.System.VirtualKey.Left when ctrl:
            case Windows.System.VirtualKey.Right when ctrl:
            {
                float lo = _selected > 0 ? _points[_selected - 1].Temperature + 1 : MinTemp;
                float hi = _selected < _points.Count - 1 ? _points[_selected + 1].Temperature - 1 : MaxTemp;
                float t = Math.Clamp(p.Temperature + (e.Key == Windows.System.VirtualKey.Left ? -step : step), lo, Math.Max(lo, hi));
                changed = t != p.Temperature;
                p.Temperature = t;
                break;
            }
            case Windows.System.VirtualKey.Left:
                _selected = Math.Max(0, _selected - 1);
                break;
            case Windows.System.VirtualKey.Right:
                _selected = Math.Min(_points.Count - 1, _selected + 1);
                break;
            case Windows.System.VirtualKey.Home:
                _selected = 0;
                break;
            case Windows.System.VirtualKey.End:
                _selected = _points.Count - 1;
                break;
            case Windows.System.VirtualKey.Up:
            case Windows.System.VirtualKey.Down:
            {
                float s = Math.Clamp(p.Speed + (e.Key == Windows.System.VirtualKey.Down ? -step : step), 0, 100);
                changed = s != p.Speed;
                p.Speed = s;
                break;
            }
            case Windows.System.VirtualKey.Insert:
            case Windows.System.VirtualKey.Add:
            case (Windows.System.VirtualKey)187:   // "=" / "+" key
                changed = AddAfterSelected();
                break;
            case Windows.System.VirtualKey.Delete:
            case Windows.System.VirtualKey.Back:
                if (_points.Count > MinPoints)
                {
                    _points.RemoveAt(_selected);
                    _selected = Math.Min(_selected, _points.Count - 1);
                    SyncHandles();
                    changed = true;
                }
                break;
            default:
                return;   // Tab and the rest: normal behaviour
        }

        e.Handled = true;   // arrows must not scroll the page or move focus
        UpdateShapes();
        if (changed)
        {
            _keyTouched = DateTime.UtcNow;
            Commit();
        }
    }

    /// <summary>A new point halfway to the next one (or 5 °C after the last), on the curve; it becomes the chosen point.</summary>
    private bool AddAfterSelected()
    {
        if (_points.Count >= MaxPoints)
        {
            return false;
        }
        var a = _points[_selected];
        CurvePoint added;
        if (_selected < _points.Count - 1)
        {
            var b = _points[_selected + 1];
            float t = MathF.Round((a.Temperature + b.Temperature) / 2);
            if (t <= a.Temperature || t >= b.Temperature)
            {
                return false;   // no room between them
            }
            added = new CurvePoint { Temperature = t, Speed = MathF.Round(FanCurve.Evaluate(_points, t)) };
        }
        else
        {
            float t = Math.Min(MaxTemp, a.Temperature + 5);
            if (t <= a.Temperature)
            {
                return false;
            }
            added = new CurvePoint { Temperature = t, Speed = a.Speed };
        }
        _points.Insert(_selected + 1, added);
        _selected++;
        SyncHandles();
        return true;
    }

    private void UpdateNow()
    {
        bool has = _nowTemp is not null && ActualWidth > 0;
        _nowLine.Visibility = _nowText.Visibility = _nowDot.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (!has)
        {
            return;
        }
        double x = X(_nowTemp!.Value);
        _nowLine.X1 = _nowLine.X2 = x;
        _nowLine.Y1 = PadTop;
        _nowLine.Y2 = PadTop + PlotHeight;
        Canvas.SetLeft(_nowDot, x - 7);
        Canvas.SetTop(_nowDot, Y(_nowSpeed ?? 0) - 7);
        _nowText.Text = _nowLabel;
        // Label to the right of the line, or to the left near the right edge.
        _nowText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double left = x + 8 + _nowText.DesiredSize.Width > PadLeft + PlotWidth ? x - 8 - _nowText.DesiredSize.Width : x + 8;
        Canvas.SetLeft(_nowText, left);
        Canvas.SetTop(_nowText, PadTop + 4);
    }

    // ---------- Points ----------

    private void SyncHandles()
    {
        while (_handles.Count < _points.Count)
        {
            var h = new Ellipse
            {
                Width = HandleSize,
                Height = HandleSize,
                StrokeThickness = 2,
                Fill = Res("SolidBackgroundFillColorBaseBrush"),
                Stroke = Res("AccentFillColorDefaultBrush"),
            };
            ToolTipService.SetToolTip(h, "Drag to move · right-click to remove");
            h.PointerEntered += (_, _) => ProtectedCursor = Hand;
            h.PointerExited += (_, _) =>
            {
                if (!IsDragging)
                {
                    ProtectedCursor = null;
                }
            };
            h.PointerPressed += Handle_PointerPressed;
            h.PointerMoved += Handle_PointerMoved;
            h.PointerReleased += Handle_PointerReleased;
            h.PointerCaptureLost += (_, _) => EndDrag(commit: true);
            h.RightTapped += Handle_RightTapped;
            _handles.Add(h);
            _canvas.Children.Add(h);
        }
        while (_handles.Count > _points.Count)
        {
            _canvas.Children.Remove(_handles[^1]);
            _handles.RemoveAt(_handles.Count - 1);
        }
    }

    private void Handle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Ellipse h || !e.GetCurrentPoint(h).Properties.IsLeftButtonPressed)
        {
            return;
        }
        _dragIndex = _handles.IndexOf(h);
        _selected = _dragIndex;              // the keyboard continues from the point last touched
        Focus(FocusState.Pointer);
        h.CapturePointer(e.Pointer);
        ProtectedCursor = Hand;
        e.Handled = true;
    }

    private void Handle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragIndex < 0)
        {
            return;
        }
        var pos = e.GetCurrentPoint(_canvas).Position;
        float lo = _dragIndex > 0 ? _points[_dragIndex - 1].Temperature + 1 : MinTemp;
        float hi = _dragIndex < _points.Count - 1 ? _points[_dragIndex + 1].Temperature - 1 : MaxTemp;
        var p = _points[_dragIndex];
        p.Temperature = Math.Clamp(MathF.Round(TempAt(pos.X)), lo, Math.Max(lo, hi));
        p.Speed = Math.Clamp(MathF.Round(SpeedAt(pos.Y)), 0, 100);
        UpdateShapes();

        _dragText.Text = string.Create(CultureInfo.CurrentCulture, $"{p.Temperature:0} °C · {p.Speed:0}%");
        _dragText.Visibility = Visibility.Visible;
        Canvas.SetLeft(_dragText, X(p.Temperature) + 10);
        Canvas.SetTop(_dragText, Math.Max(0, Y(p.Speed) - 24));
        e.Handled = true;
    }

    private void Handle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
        EndDrag(commit: true);
    }

    private void EndDrag(bool commit)
    {
        if (_dragIndex < 0)
        {
            return;
        }
        _dragIndex = -1;
        _dragText.Visibility = Visibility.Collapsed;
        ProtectedCursor = null;
        if (commit)
        {
            Commit();
        }
    }

    private void Handle_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        int i = _handles.IndexOf((Ellipse)sender);
        if (i >= 0 && _points.Count > MinPoints)
        {
            _points.RemoveAt(i);
            _selected = Math.Clamp(i <= _selected ? _selected - 1 : _selected, 0, _points.Count - 1);
            SyncHandles();
            UpdateShapes();
            Commit();
        }
        e.Handled = true;
    }

    private void Canvas_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_points.Count >= MaxPoints)
        {
            return;
        }
        var pos = e.GetPosition(_canvas);
        float t = MathF.Round(TempAt(pos.X));
        float s = Math.Clamp(MathF.Round(SpeedAt(pos.Y)), 0, 100);
        if (t < MinTemp || t > MaxTemp || _points.Any(p => Math.Abs(p.Temperature - t) < 1))
        {
            return;
        }
        var added = new CurvePoint { Temperature = t, Speed = s };
        _points.Add(added);
        _points.Sort((a, b) => a.Temperature.CompareTo(b.Temperature));
        _selected = _points.IndexOf(added);
        SyncHandles();
        UpdateShapes();
        Commit();
    }

    private void Commit() =>
        CurveCommitted?.Invoke(_points.Select(p => new CurvePoint { Temperature = p.Temperature, Speed = p.Speed }).ToList());

    private void AutomationProperties_SetName(string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, name);
}
