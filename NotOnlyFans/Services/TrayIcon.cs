using System.Runtime.InteropServices;

namespace NotOnlyFans.Services;

/// <summary>
/// The icon in the notification area (Windows has no WinUI control for it): plain Shell_NotifyIcon
/// with a small hidden window of our own that receives its clicks. Must be created on the UI thread
/// (its window gets messages through the UI thread's message loop, so events arrive on the UI thread).
/// Click = <see cref="Open"/>; right click = menu (Open, Exit). Comes back by itself if Explorer restarts.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 1;   // WM_APP + 1
    private const uint IconId = 1;
    private const int CommandOpen = 1;
    private const int CommandExit = 2;

    private static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private readonly WndProc _wndProc;   // kept alive: Windows calls it
    private readonly IntPtr _window;
    private readonly IntPtr _icon;
    private string _tooltip = "NotOnlyFans";
    private bool _added;

    /// <summary>Click on the icon, or "Open NotOnlyFans" in its menu.</summary>
    public event Action? Open;

    /// <summary>"Exit" in its menu.</summary>
    public event Action? Exit;

    public TrayIcon(string iconPath)
    {
        _wndProc = WindowProc;
        var cls = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "NotOnlyFans.Tray",
        };
        RegisterClassEx(ref cls);
        // A hidden top-level window (not message-only: the menu needs a window that can take the foreground).
        _window = CreateWindowEx(0, cls.lpszClassName, "NotOnlyFans tray", 0x80000000 /* WS_POPUP */, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, cls.hInstance, IntPtr.Zero);

        int size = GetSystemMetrics(49);   // SM_CXSMICON
        _icon = LoadImage(IntPtr.Zero, iconPath, 1 /* IMAGE_ICON */, size, size, 0x10 /* LR_LOADFROMFILE */);
        Add();
    }

    /// <summary>Text shown when hovering the icon (up to 127 characters).</summary>
    public void SetTooltip(string text)
    {
        text = text.Length > 127 ? text[..127] : text;
        if (text == _tooltip)
        {
            return;
        }
        _tooltip = text;
        var data = Data(0x04 | 0x80);   // NIF_TIP | NIF_SHOWTIP
        Shell_NotifyIcon(1 /* NIM_MODIFY */, ref data);
    }

    /// <summary>A one-off notification next to the icon (Windows shows it as a regular notification).</summary>
    public void Notify(string title, string text)
    {
        var data = Data(0x10);   // NIF_INFO
        data.szInfoTitle = title;
        data.szInfo = text;
        data.dwInfoFlags = 0x04 | 0x80;   // NIIF_USER (our icon) | NIIF_RESPECT_QUIET_TIME
        Shell_NotifyIcon(1 /* NIM_MODIFY */, ref data);
    }

    private void Add()
    {
        var data = Data(0x01 | 0x02 | 0x04 | 0x80);   // NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP
        _added = Shell_NotifyIcon(0 /* NIM_ADD */, ref data);
        data.uVersion = 4;   // NOTIFYICON_VERSION_4: clicks come as NIN_SELECT / WM_CONTEXTMENU
        Shell_NotifyIcon(4 /* NIM_SETVERSION */, ref data);
    }

    private NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _window,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tooltip,
        szInfo = "",
        szInfoTitle = "",
        hBalloonIcon = _icon,
    };

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == CallbackMessage)
        {
            switch ((uint)(lParam.ToInt64() & 0xFFFF))
            {
                case 0x400:   // NIN_SELECT (click)
                case 0x401:   // NIN_KEYSELECT (Enter / Space)
                    Open?.Invoke();
                    break;
                case 0x7B:    // WM_CONTEXTMENU (right click, Shift+F10)
                    ShowMenu((short)(wParam.ToInt64() & 0xFFFF), (short)((wParam.ToInt64() >> 16) & 0xFFFF));
                    break;
            }
            return IntPtr.Zero;
        }
        if (msg == TaskbarCreated && TaskbarCreated != 0)
        {
            Add();   // Explorer restarted: the icon is gone, add it again
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu(int x, int y)
    {
        IntPtr menu = CreatePopupMenu();
        AppendMenu(menu, 0, CommandOpen, "Open NotOnlyFans");
        AppendMenu(menu, 0x800 /* MF_SEPARATOR */, 0, null);
        AppendMenu(menu, 0, CommandExit, "Exit");
        SetMenuDefaultItem(menu, CommandOpen, 0);

        // Without this the menu would not close when the user clicks elsewhere.
        SetForegroundWindow(_window);
        int command = TrackPopupMenuEx(menu, 0x100 /* TPM_RETURNCMD */ | 0x80 /* TPM_NONOTIFY */ | 0x20 /* TPM_BOTTOMALIGN */,
            x, y, _window, IntPtr.Zero);
        DestroyMenu(menu);

        if (command == CommandOpen)
        {
            Open?.Invoke();
        }
        else if (command == CommandExit)
        {
            Exit?.Invoke();
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = Data(0);
            Shell_NotifyIcon(2 /* NIM_DELETE */, ref data);
            _added = false;
        }
        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
        }
        DestroyWindow(_window);
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX cls);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, int id, string? text);

    [DllImport("user32.dll")]
    private static extern bool SetMenuDefaultItem(IntPtr menu, int item, int byPosition);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
