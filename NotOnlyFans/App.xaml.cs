using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using NotOnlyFans.Services;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace NotOnlyFans
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        /// <summary>Live sensor data from NotOnlyFans.Service (events on the UI thread).</summary>
        public static SensorClient Sensors { get; private set; } = null!;

        /// <summary>Last 5 minutes of every sensor, for the charts.</summary>
        public static SensorHistory History { get; } = new();

        /// <summary>Sensors pinned to the sidebar.</summary>
        public static PinStore Pins { get; private set; } = null!;

        /// <summary>Sends fan settings to the service.</summary>
        public static ControlClient Control { get; } = new();

        /// <summary>The user's names and fan order.</summary>
        public static UserSettings Settings { get; private set; } = null!;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            // The theme can only be chosen here, before any window: then everything starts in its colors.
            Settings = UserSettings.Load();
            RequestedTheme = Theme.ForApplication(Settings.Theme);
            InitializeComponent();

            // Errors leave their reason in crash.log next to settings.json (Windows error reporting may be off).
            // An error in a click or a page update is logged and the app carries on (the service keeps the fans
            // safe either way); only errors that keep repeating close it, so it can't get stuck in a loop.
            UnhandledException += (_, e) =>
            {
                LogError(e.Exception);
                e.Handled = !RepeatingErrors();
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) => LogError(e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                LogError(e.Exception);
                e.SetObserved();
            };
        }

        private static readonly Queue<DateTime> _recentErrors = new();

        /// <summary>More than 5 errors in 10 seconds: something is broken for good.</summary>
        private static bool RepeatingErrors()
        {
            var now = DateTime.UtcNow;
            _recentErrors.Enqueue(now);
            while (_recentErrors.Count > 0 && now - _recentErrors.Peek() > TimeSpan.FromSeconds(10))
            {
                _recentErrors.Dequeue();
            }
            return _recentErrors.Count > 5;
        }

        /// <summary>Appends an error to crash.log (LocalAppData\NotOnlyFans). Never throws.</summary>
        public static void LogError(Exception? ex)
        {
            try
            {
                string path = Path.Combine(AppFiles.Folder, "crash.log");
                Directory.CreateDirectory(AppFiles.Folder);
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                {
                    File.Move(path, path + ".old", overwrite: true);   // keep it small: the last ~1 MB and the one before
                }
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch (Exception)
            {
                // Nothing more to do.
            }
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            // Connect to the service first, so data is already on its way while the window is built.
            Pins = PinStore.Load();
            Sensors = new SensorClient(DispatcherQueue.GetForCurrentThread());
            // History subscribes first, so pages that draw charts on the same event already see the new sample.
            Sensors.ValuesChanged += () =>
            {
                if (Sensors.Values is { } values)
                {
                    History.Record(values);
                }
            };
            Sensors.CatalogChanged += () =>
            {
                if (Sensors.Catalog is { } catalog)
                {
                    History.OnCatalog(catalog);
                    Pins.EnsureDefaults(SensorLayout.DefaultPins(catalog).Select(i => new PinnedSensor
                    {
                        Key = catalog.Sensors[i].Key,
                        Label = SensorLayout.PinLabel(catalog, i),
                    }));
                }
            };
            Sensors.Start();

            // Windows Dynamic Lighting devices are driven from here (Windows gives them to apps, not to the service).
            Lighting = new DynamicLighting(DispatcherQueue.GetForCurrentThread());
            Lighting.Start();

            // The pinned readings over games (shown again at start if it was on). Before the window: Settings uses it.
            Overlay = new GameOverlay();

            Window = new MainWindow();
            Window.Closed += (_, _) =>
            {
                _tray?.Dispose();
                Overlay?.Dispose();
            };
            Window.HiddenToTray += () =>
            {
                if (!Settings.TrayHintShown)
                {
                    _tray?.Notify("NotOnlyFans is still running",
                        "It's here in the tray. Click the icon to open it again; right-click it to exit.");
                    Settings.TrayHintShown = true;
                }
            };

            _tray = new TrayIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
            _tray.Open += () => Window.Open();
            _tray.Exit += ExitApp;
            Sensors.ValuesChanged += UpdateTrayTooltip;
            App.Pins.Changed += UpdateTrayTooltip;

            // Another start (Start menu, shortcut) while running: show this window instead of a second copy.
            var ui = DispatcherQueue.GetForCurrentThread();
            Program.Reactivated += _ => ui.TryEnqueue(() => Window.Open());

            // Started with Windows: stay in the tray until the user opens the window.
            if (AppInstance.GetCurrent().GetActivatedEventArgs().Kind == ExtendedActivationKind.StartupTask)
            {
                MainPage.Current?.SetActive(false);
            }
            else
            {
                Window.Open();
            }
        }

        private TrayIcon? _tray;

        /// <summary>The game overlay (pinned readings over everything; its hotkeys work while the window is hidden).</summary>
        public static GameOverlay Overlay { get; private set; } = null!;

        public static DynamicLighting Lighting { get; private set; } = null!;

        /// <summary>The one window (it may be hidden in the tray).</summary>
        public static MainWindow Window { get; private set; } = null!;

        /// <summary>Exit for real: the window app closes; the background service keeps driving fans and lights.</summary>
        public static void ExitApp()
        {
            Window.Exit();
            Current.Exit();
        }

        /// <summary>Hovering the tray icon shows the pinned readings (the first three).</summary>
        private void UpdateTrayTooltip()
        {
            if (_tray is null)
            {
                return;
            }
            var text = new System.Text.StringBuilder("NotOnlyFans");
            if (Sensors.Catalog is { } catalog)
            {
                foreach (var pin in Pins.Pins.Take(3))
                {
                    if (Sensors.TryGetIndex(pin.Key, out int i))
                    {
                        text.Append('\n').Append(SensorLayout.PinLabel(catalog, i)).Append(' ')
                            .Append(SensorFormat.Format(catalog.Sensors[i].Kind, Sensors.GetNow(i)));
                    }
                }
            }
            _tray.SetTooltip(text.ToString());
        }
    }
}
