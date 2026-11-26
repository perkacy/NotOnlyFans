using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using NotOnlyFans.Services;

namespace NotOnlyFans
{
    /// <summary>
    /// The application window: Mica backdrop, custom title bar and a frame
    /// that hosts the app shell (MainPage with the navigation).
    /// The close button hides it to the tray (unless the user turned that off); the pages stay in memory,
    /// so opening it again is instant, but they stop updating while it is hidden.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private bool _exiting;

        public MainWindow()
        {
            InitializeComponent();

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            AppWindow.SetIcon("Assets/AppIcon.ico");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 860));
            if (AppWindow.Presenter is OverlappedPresenter overlapped)
            {
                // Below this the pages have no room left (the pane is already down to icons then).
                overlapped.PreferredMinimumWidth = 560;
                overlapped.PreferredMinimumHeight = 420;
            }
            AppWindow.Closing += AppWindow_Closing;
            AppWindow.Changed += AppWindow_Changed;

            // Theme before the pages are made, so they start in the right colors.
            Theme.Apply(this, App.Settings.Theme);
            App.Settings.ThemeChanged += () => Theme.Apply(this, App.Settings.Theme);
            Theme.FollowWindows(this, () => App.Settings.Theme);

            RootFrame.Navigate(typeof(MainPage));
        }

        /// <summary>True while the window is on screen (shown, maybe minimized); false while it is in the tray.</summary>
        public bool IsOpen { get; private set; }

        /// <summary>Raised when the window is closed to the tray (the first time the app tells the user where it went).</summary>
        public event Action? HiddenToTray;

        /// <summary>Shows the window (from the tray, or restores it when minimized) and brings it to the front.</summary>
        public void Open()
        {
            if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            {
                presenter.Restore();
            }
            AppWindow.Show();
            IsOpen = true;
            MainPage.Current?.SetActive(true);
            Activate();
            SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        }

        /// <summary>Exits the app for real (tray menu, or the close button with "close to tray" off).</summary>
        public void Exit()
        {
            _exiting = true;
            Close();
        }

        private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_exiting || !App.Settings.CloseToTray)
            {
                return;
            }
            args.Cancel = true;
            AppWindow.Hide();
            IsOpen = false;
            MainPage.Current?.SetActive(false);
            HiddenToTray?.Invoke();
        }

        /// <summary>Minimized, nothing of the pages is seen: they stop updating, as in the tray, and go on when restored.</summary>
        private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
        {
            // Checked on every change (minimizing is a change of the presenter's state; SetActive ignores repeats).
            if (!IsOpen || sender.Presenter is not OverlappedPresenter presenter)
            {
                return;
            }
            MainPage.Current?.SetActive(presenter.State != OverlappedPresenterState.Minimized);
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hwnd);
    }
}
