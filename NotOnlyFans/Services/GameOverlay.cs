using System.Runtime.InteropServices;

namespace NotOnlyFans.Services;

/// <summary>
/// The game overlay: the pinned readings in a small dark panel over everything, also over games
/// (borderless or windowed; a game in exclusive fullscreen draws over it). Nothing is injected into games,
/// so anti-cheat has nothing to object to.
///
/// A plain Win32 layered window (WinUI windows can't be click-through and see-through at once): drawn with GDI into
/// a bitmap, then given its transparency per pixel (semi-transparent panel, smooth rounded corners).
/// Clicks go through it to the game and it never takes the focus. Redrawn once a second with the readings.
///
/// Hotkeys (anywhere, also in a game): Ctrl+Shift+O shows / hides it, Ctrl+Shift+M lets it be dragged to another
/// place (again to finish; the place is saved). Created on the UI thread: its messages come through the UI thread's
/// message loop, so everything here runs on the UI thread.
/// </summary>
public sealed class GameOverlay : IDisposable
{
    private const int HotkeyToggle = 1;
    private const int HotkeyMove = 2;
    public const string ToggleKeys = "Ctrl+Shift+O";
    public const string MoveKeys = "Ctrl+Shift+M";

    // Look (in 96-DPI pixels; scaled to the monitor).
    private const int PaddingX = 14, PaddingY = 10, RowGap = 3, ColumnGap = 20, Radius = 8;
    private const byte PanelAlpha = 225;
    private const uint Background = 0x00201E1E;   // COLORREF 0x00BBGGRR: #1E1E20
    private const uint LabelColor = 0x00C8C8C8;
    private const uint ValueColor = 0x00FFFFFF;
    private const uint WarmColor = 0x0047B3FF;    // #FFB347
    private const uint HotColor = 0x006B6BFF;     // #FF6B6B
    private const uint MoveColor = 0x00FFCD60;    // #60CDFF (accent-like outline while moving)

    private readonly WndProc _wndProc;   // kept alive: Windows calls it
    private readonly IntPtr _window;
    private readonly IntPtr _moveCursor = LoadCursor(IntPtr.Zero, 32646 /* IDC_SIZEALL */);
    private bool _shown;
    private bool _moving;
    private int _x, _y;

    /// <summary>Shown or hidden (by the hotkey or from Settings).</summary>
    public event Action? Changed;

    /// <summary>False if another app already uses the hotkeys (then only Settings switches the overlay).</summary>
    public bool HotkeysWork { get; }

    public bool IsShown => _shown;

    public bool IsMoving => _moving;

    public GameOverlay()
    {
        _wndProc = WindowProc;
        var cls = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "NotOnlyFans.Overlay",
        };
        RegisterClassEx(ref cls);
        _window = CreateWindowEx(ExStyle(moving: false), cls.lpszClassName, "NotOnlyFans overlay", 0x80000000 /* WS_POPUP */,
            0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, cls.hInstance, IntPtr.Zero);

        HotkeysWork = RegisterHotKey(_window, HotkeyToggle, 0x0002 | 0x0004 | 0x4000 /* CTRL | SHIFT | NOREPEAT */, 'O')
                    & RegisterHotKey(_window, HotkeyMove, 0x0002 | 0x0004 | 0x4000, 'M');

        App.Sensors.ValuesChanged += Render;
        App.Pins.Changed += Render;
        App.Settings.NamesChanged += Render;

        if (App.Settings.OverlayShown)
        {
            Show(true);
        }
    }

    public void Show(bool show)
    {
        if (show == _shown)
        {
            return;
        }
        _shown = show;
        App.Settings.OverlayShown = show;
        if (show)
        {
            PlaceAtStart();
            Render();
            ShowWindow(_window, 4 /* SW_SHOWNOACTIVATE */);
        }
        else
        {
            SetMoving(false);
            ShowWindow(_window, 0 /* SW_HIDE */);
        }
        Changed?.Invoke();
    }

    /// <summary>Move mode: the overlay catches the mouse and can be dragged; off again, clicks go through it.</summary>
    public void SetMoving(bool moving)
    {
        if (moving && !_shown)
        {
            Show(true);
        }
        if (moving == _moving)
        {
            return;
        }
        _moving = moving;
        SetWindowLong(_window, -20 /* GWL_EXSTYLE */, (int)ExStyle(moving));
        Render();
        Changed?.Invoke();
    }

    /// <summary>Forgets the place the user chose: back to the default corner.</summary>
    public void ResetPosition()
    {
        App.Settings.OverlayPosition = null;
        PlaceAtStart();
        Render();
        Changed?.Invoke();
    }

    private static uint ExStyle(bool moving) =>
        0x00080000 /* LAYERED */ | 0x00000008 /* TOPMOST */ | 0x00000080 /* TOOLWINDOW: not in Alt+Tab */
        | 0x08000000 /* NOACTIVATE */ | (moving ? 0 : 0x00000020u /* TRANSPARENT: clicks go through */);

    /// <summary>Where the user left it, if that is still on a screen; else the top-left corner of the main screen.</summary>
    private void PlaceAtStart()
    {
        if (App.Settings.OverlayPosition is { } p && MonitorFromPoint(new POINT { X = p.X + 8, Y = p.Y + 8 }, 0 /* NULL */) != IntPtr.Zero)
        {
            (_x, _y) = p;
            return;
        }
        var work = new RECT();
        SystemParametersInfo(0x0030 /* SPI_GETWORKAREA */, 0, ref work, 0);
        int margin = Scale(24, Dpi());
        (_x, _y) = (work.Left + margin, work.Top + margin);
    }

    private uint Dpi()
    {
        uint dpi = GetDpiForWindow(_window);
        return dpi == 0 ? 96 : dpi;
    }

    private static int Scale(int value, uint dpi) => (int)Math.Round(value * dpi / 96.0);

    // ---------- Drawing ----------

    /// <summary>The rows: every pinned reading, as in the sidebar (temperatures colored when warm or hot).</summary>
    private List<(string Label, string Value, uint Color)> Rows()
    {
        var rows = new List<(string, string, uint)>();
        var catalog = App.Sensors.Catalog;
        foreach (var pin in App.Pins.Pins)
        {
            if (catalog is null || !App.Sensors.TryGetIndex(pin.Key, out int i))
            {
                rows.Add((pin.Label, SensorFormat.NoValue, ValueColor));
                continue;
            }
            var sensor = catalog.Sensors[i];
            float? now = App.Sensors.GetNow(i);
            uint color = sensor.Kind == "Temperature" && now is { } t ? t >= 85 ? HotColor : t >= 75 ? WarmColor : ValueColor : ValueColor;
            rows.Add((SensorLayout.PinLabel(catalog, i), SensorFormat.Format(sensor.Kind, now), color));
        }
        if (rows.Count == 0)
        {
            rows.Add(("Pin readings on the Sensors page", "", ValueColor));
        }
        if (_moving)
        {
            rows.Add(("Drag to move", MoveKeys + " to finish", MoveColor));
        }
        return rows;
    }

    private void Render()
    {
        if (!_shown)
        {
            return;
        }
        // While it's dragged Windows moves it: start from where it is now, not where it was.
        if (_moving && GetWindowRect(_window, out var now) && now.Right - now.Left > 1)
        {
            (_x, _y) = (now.Left, now.Top);
        }
        uint dpi = Dpi();
        var rows = Rows();

        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr dc = CreateCompatibleDC(screen);
        var (labelFont, valueFont) = Fonts(dpi);
        try
        {
            // Measure: labels in one column, values right-aligned in the next.
            int labelWidth = 0, valueWidth = 0, lineHeight = 0;
            foreach (var (label, value, _) in rows)
            {
                SelectObject(dc, labelFont);
                GetTextExtentPoint32(dc, label, label.Length, out var l);
                SelectObject(dc, valueFont);
                GetTextExtentPoint32(dc, value, value.Length, out var v);
                labelWidth = Math.Max(labelWidth, l.cx);
                valueWidth = Math.Max(valueWidth, v.cx);
                lineHeight = Math.Max(lineHeight, Math.Max(l.cy, v.cy));
            }
            int padX = Scale(PaddingX, dpi), padY = Scale(PaddingY, dpi), gap = Scale(RowGap, dpi);
            int width = padX * 2 + labelWidth + (valueWidth > 0 ? Scale(ColumnGap, dpi) + valueWidth : 0);
            int height = padY * 2 + rows.Count * lineHeight + (rows.Count - 1) * gap;

            var info = new BITMAPINFO
            {
                biSize = Marshal.SizeOf<BITMAPINFO>(),
                biWidth = width,
                biHeight = -height,   // top-down
                biPlanes = 1,
                biBitCount = 32,
            };
            IntPtr bitmap = CreateDIBSection(dc, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
            IntPtr old = SelectObject(dc, bitmap);
            try
            {
                // Opaque panel and text first (so ClearType has a real background), transparency after.
                var all = new RECT { Right = width, Bottom = height };
                IntPtr brush = CreateSolidBrush(Background);
                FillRect(dc, ref all, brush);
                DeleteObject(brush);
                SetBkMode(dc, 1 /* TRANSPARENT */);
                int y = padY;
                foreach (var (label, value, color) in rows)
                {
                    SelectObject(dc, labelFont);
                    SetTextColor(dc, _moving && color == MoveColor ? MoveColor : LabelColor);
                    TextOut(dc, padX, y, label, label.Length);
                    SelectObject(dc, valueFont);
                    GetTextExtentPoint32(dc, value, value.Length, out var v);
                    SetTextColor(dc, color);
                    TextOut(dc, width - padX - v.cx, y, value, value.Length);
                    y += lineHeight + gap;
                }

                ApplyAlpha(bits, width, height, Scale(Radius, dpi), _moving ? Math.Max(1, Scale(2, dpi)) : 0);

                var size = new SIZE { cx = width, cy = height };
                var position = new POINT { X = _x, Y = _y };
                var source = new POINT();
                var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
                UpdateLayeredWindow(_window, screen, ref position, ref size, dc, ref source, 0, ref blend, 2 /* ULW_ALPHA */);
                // Games can put themselves on top: stay above them.
                SetWindowPos(_window, new IntPtr(-1) /* TOPMOST */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200 /* NOSIZE NOMOVE NOACTIVATE NOOWNERZORDER */);
            }
            finally
            {
                SelectObject(dc, old);
                DeleteObject(bitmap);
            }
        }
        finally
        {
            DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    // The two fonts, made once per screen scale (not every second).
    private uint _fontDpi;
    private IntPtr _labelFont, _valueFont;

    private (IntPtr Label, IntPtr Value) Fonts(uint dpi)
    {
        if (_fontDpi != dpi)
        {
            DeleteFonts();
            _labelFont = CreateFont(-Scale(13, dpi), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5 /* CLEARTYPE */, 0, "Segoe UI Variable Text");
            _valueFont = CreateFont(-Scale(14, dpi), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI Variable Text");
            _fontDpi = dpi;
        }
        return (_labelFont, _valueFont);
    }

    private void DeleteFonts()
    {
        DeleteObject(_labelFont);
        DeleteObject(_valueFont);
        (_labelFont, _valueFont, _fontDpi) = (IntPtr.Zero, IntPtr.Zero, 0);
    }

    /// <summary>
    /// Makes the drawn panel see-through: every pixel gets the panel's alpha (premultiplied, as layered windows want),
    /// the corners are cut round with soft edges, and in move mode an outline marks the panel.
    /// </summary>
    private static void ApplyAlpha(IntPtr bits, int width, int height, int radius, int outline)
    {
        var pixels = new int[width * height];
        Marshal.Copy(bits, pixels, 0, pixels.Length);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // Distance outside the rounded rectangle (negative inside), from the nearest corner's circle.
                double cx = Math.Clamp(x + 0.5, radius, width - radius), cy = Math.Clamp(y + 0.5, radius, height - radius);
                double d = Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy)) - radius;
                bool corner = (x + 0.5 < radius || x + 0.5 > width - radius) && (y + 0.5 < radius || y + 0.5 > height - radius);
                double coverage = corner ? Math.Clamp(0.5 - d, 0, 1) : 1;
                int p = pixels[y * width + x];
                uint r = (uint)(p >> 16) & 0xFF, g = (uint)(p >> 8) & 0xFF, b = (uint)p & 0xFF;

                // Distance from the edge inwards, for the move-mode outline.
                double edge = corner ? -d : Math.Min(Math.Min(x + 0.5, width - x - 0.5), Math.Min(y + 0.5, height - y - 0.5));
                double alpha = PanelAlpha / 255.0;
                if (outline > 0 && edge < outline)
                {
                    (r, g, b) = (0x60, 0xCD, 0xFF);
                    alpha = 1;
                }
                double a = alpha * coverage;
                pixels[y * width + x] = (int)((uint)Math.Round(a * 255) << 24 | (uint)(r * a) << 16 | (uint)(g * a) << 8 | (uint)(b * a));
            }
        }
        Marshal.Copy(pixels, 0, bits, pixels.Length);
    }

    // ---------- Messages ----------

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case 0x0312:   // WM_HOTKEY
                if (wParam == HotkeyToggle)
                {
                    Show(!_shown);
                }
                else if (wParam == HotkeyMove)
                {
                    SetMoving(!_moving);
                }
                return IntPtr.Zero;
            case 0x0084:   // WM_NCHITTEST: in move mode the whole panel is a handle to drag
                return _moving ? new IntPtr(2 /* HTCAPTION */) : new IntPtr(-1 /* HTTRANSPARENT */);
            case 0x0020 when _moving:   // WM_SETCURSOR: the four-way arrow says "drag me"
                SetCursor(_moveCursor);
                return new IntPtr(1);
            case 0x0021:   // WM_MOUSEACTIVATE: dragging never takes the focus from the game
                return new IntPtr(3 /* MA_NOACTIVATE */);
            case 0x0232:   // WM_EXITSIZEMOVE: dropped - remember the place
                if (GetWindowRect(hwnd, out var rect))
                {
                    (_x, _y) = (rect.Left, rect.Top);
                    App.Settings.OverlayPosition = (_x, _y);
                    Changed?.Invoke();
                }
                return IntPtr.Zero;
            case 0x02E0:   // WM_DPICHANGED (moved to a screen with another scale)
                Render();
                return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        App.Sensors.ValuesChanged -= Render;
        App.Pins.Changed -= Render;
        App.Settings.NamesChanged -= Render;
        UnregisterHotKey(_window, HotkeyToggle);
        UnregisterHotKey(_window, HotkeyMove);
        DestroyWindow(_window);
        DeleteFonts();
    }

    // ----- Win32 -----

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx, cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        public int bmiColors;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX cls);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, int name);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfo(uint action, uint param, ref RECT value, uint winIni);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr screenDc, ref POINT position, ref SIZE size,
        IntPtr sourceDc, ref POINT source, uint key, ref BLENDFUNCTION blend, uint flags);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr dc, ref RECT rect, IntPtr brush);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic,
        uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr dc, int mode);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(IntPtr dc, uint color);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool TextOut(IntPtr dc, int x, int y, string text, int length);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetTextExtentPoint32W")]
    private static extern bool GetTextExtentPoint32(IntPtr dc, string text, int length, out SIZE size);
}
