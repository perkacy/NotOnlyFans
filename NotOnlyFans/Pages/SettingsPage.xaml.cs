using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NotOnlyFans.Contracts;
using NotOnlyFans.Controls;
using NotOnlyFans.Services;
using Windows.ApplicationModel;
using Windows.ApplicationModel.DataTransfer;

namespace NotOnlyFans.Pages
{
    /// <summary>
    /// Settings. Safety: the emergency temperature lives in the service (it applies the rule with the window
    /// closed); the page shows what the service runs and sends changes to it.
    /// </summary>
    public sealed partial class SettingsPage : Page
    {
        private bool _isShown;
        // True while the page itself sets controls (and while it is being built).
        private bool _settingControls = true;

        public SettingsPage()
        {
            InitializeComponent();
            SafetyBox.Minimum = SafetyRule.Minimum;
            SafetyBox.Maximum = SafetyRule.Maximum;
            SafetyReset.Content = string.Create(CultureInfo.CurrentCulture, $"Restore {SafetyRule.Default:0} °C");
            SafetyBox.ValueChanged += SafetyBox_ValueChanged;
            ThemeBox.SelectedItem = ThemeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == App.Settings.Theme)
                                    ?? ThemeBox.Items[^1];
            _settingControls = false;

            App.Sensors.ValuesChanged += Refresh;
            App.Sensors.ControlChanged += Refresh;
            App.Sensors.ConnectionChanged += Refresh;
            App.Settings.NamesChanged += Refresh;
            App.Overlay.Changed += () =>
            {
                if (_isShown)
                {
                    RefreshOverlay();
                }
            };

            Loaded += (_, _) =>
            {
                _isShown = true;
                Refresh();
                RefreshOverlay();
                _ = RefreshStartup();
            };
            Unloaded += (_, _) => _isShown = false;
            Theme.Follow(this);   // InfoBars after a light/dark change
        }

        private void Refresh()
        {
            if (!_isShown)
            {
                return;
            }

            var control = App.Sensors.Control;
            float limit = control?.SafetyTemperature ?? SafetyRule.Default;
            bool connected = App.Sensors.IsConnected && control is not null;

            ServiceInfo.IsOpen = App.Sensors.IsServiceMissing;
            SafetyActiveInfo.IsOpen = control?.Emergency == true;
            SafetyActiveInfo.Message = string.Create(CultureInfo.CurrentCulture,
                $"{control?.EmergencySensor ?? "A sensor"} reached {limit:0} °C. Fans controlled by NotOnlyFans run at 100% until temperatures drop below {limit - SafetyRule.Release:0} °C.");

            SafetyHint.Text = string.Create(CultureInfo.CurrentCulture,
                $"When any temperature sensor reaches this, every fan controlled by NotOnlyFans runs at 100% – whatever its curve – until all temperatures are {SafetyRule.Release:0} °C lower. " +
                $"Fans left to the BIOS or the graphics card aren't touched. {SafetyRule.Minimum:0}–{SafetyRule.Maximum:0} °C, default {SafetyRule.Default:0} °C.");

            SafetyBox.IsEnabled = connected;
            SafetyBox.Show(limit);
            SafetyReset.Visibility = connected && limit != SafetyRule.Default ? Visibility.Visible : Visibility.Collapsed;

            var (nowName, now, maxName, max) = Hottest();
            HottestNow.Text = now is null ? SensorFormat.NoValue : $"{nowName} · {SensorFormat.Format("Temperature", now)}";
            HottestMax.Text = max is null ? SensorFormat.NoValue : $"{maxName} · {SensorFormat.Format("Temperature", max)}";
        }

        /// <summary>The hottest sensor now and the highest reading since the service started, among the sensors the rule watches.</summary>
        private static (string NowName, float? Now, string MaxName, float? Max) Hottest()
        {
            string nowName = "", maxName = "";
            float? now = null, max = null;
            if (App.Sensors.Catalog is not { } catalog || App.Sensors.Values is not { } values)
            {
                return (nowName, now, maxName, max);
            }
            for (int i = 0; i < catalog.Sensors.Count && i < values.Now.Length; i++)
            {
                var s = catalog.Sensors[i];
                if (s.Kind != "Temperature" || !SafetyRule.Counts(s.Name))
                {
                    continue;
                }
                if (values.Now[i] is { } v && (now is null || v > now))
                {
                    now = v;
                    nowName = SensorLayout.PinLabel(catalog, i);
                }
                if (values.Max[i] is { } m && (max is null || m > max))
                {
                    max = m;
                    maxName = SensorLayout.PinLabel(catalog, i);
                }
            }
            return (nowName, now, maxName, max);
        }

        private async void SafetyBox_ValueChanged(Stepper sender, double value)
        {
            if (_settingControls)
            {
                return;
            }
            await SetSafety((float)value);
        }

        // ----- App -----

        private const string StartupTaskId = "NotOnlyFansStartup";

        private async Task RefreshStartup()
        {
            _settingControls = true;
            TraySwitch.IsOn = App.Settings.CloseToTray;
            try
            {
                var task = await StartupTask.GetAsync(StartupTaskId);
                ShowStartup(task.State);
            }
            catch (Exception)
            {
                StartupSwitch.IsEnabled = false;
                StartupHint.Text = "Not available in this copy of NotOnlyFans.";
            }
            finally
            {
                _settingControls = false;
            }
        }

        private void ShowStartup(StartupTaskState state)
        {
            _settingControls = true;
            StartupSwitch.IsOn = state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            StartupSwitch.IsEnabled = state is StartupTaskState.Enabled or StartupTaskState.Disabled;
            StartupHint.Text = state switch
            {
                StartupTaskState.DisabledByUser => "Turned off in Windows Settings › Apps › Startup. Turn it on there.",
                StartupTaskState.DisabledByPolicy => "Turned off by your organization.",
                StartupTaskState.EnabledByPolicy => "Turned on by your organization.",
                _ => "Opens quietly in the tray when you sign in.",
            };
            StartupAppsLink.Visibility = state == StartupTaskState.DisabledByUser ? Visibility.Visible : Visibility.Collapsed;
            _settingControls = false;
        }

        private async void StartupSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_settingControls)
            {
                return;
            }
            try
            {
                var task = await StartupTask.GetAsync(StartupTaskId);
                if (StartupSwitch.IsOn)
                {
                    ShowStartup(await task.RequestEnableAsync());
                }
                else
                {
                    task.Disable();
                    ShowStartup(task.State);
                }
            }
            catch (Exception)
            {
                await RefreshStartup();
            }
        }

        private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_settingControls && ThemeBox.SelectedItem is ComboBoxItem { Tag: string theme })
            {
                App.Settings.Theme = theme;
            }
        }

        private void TraySwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_settingControls)
            {
                App.Settings.CloseToTray = TraySwitch.IsOn;
            }
        }

        // ----- Game overlay -----

        private void RefreshOverlay()
        {
            var overlay = App.Overlay;
            if (OverlaySwitch.IsOn != overlay.IsShown)
            {
                _settingControls = true;
                OverlaySwitch.IsOn = overlay.IsShown;
                _settingControls = false;
            }
            string keys = overlay.HotkeysWork ? $" {GameOverlay.ToggleKeys} shows or hides it, also in a game." : "";
            OverlayHint.Text = "Your pinned readings in a corner of the screen, over games in borderless or windowed mode "
                               + "(a game in exclusive fullscreen draws over it). Clicks go through it." + keys;
            OverlayMoveButton.Content = overlay.IsMoving ? "Done" : "Move";
            AutomationProperties.SetName(OverlayMoveButton, overlay.IsMoving ? "Done moving the overlay" : "Move the overlay");
            OverlayMoveHint.Text = overlay.IsMoving
                ? "Drag the overlay with the mouse, then press Done" + (overlay.HotkeysWork ? $" or {GameOverlay.MoveKeys}." : ".")
                : "Drag it anywhere on any screen" + (overlay.HotkeysWork ? $" – also with {GameOverlay.MoveKeys} in a game." : ".");
            OverlayResetLink.Visibility = App.Settings.OverlayPosition is null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OverlaySwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_settingControls)
            {
                App.Overlay.Show(OverlaySwitch.IsOn);
            }
        }

        private void OverlayMove_Click(object sender, RoutedEventArgs e) => App.Overlay.SetMoving(!App.Overlay.IsMoving);

        private void OverlayReset_Click(object sender, RoutedEventArgs e) => App.Overlay.ResetPosition();

        // ----- Help -----

        private async void Diagnostics_Click(object sender, RoutedEventArgs e)
        {
            DiagnosticsButton.IsEnabled = false;
            try
            {
                var package = new DataPackage();
                package.SetText(await Diagnostics.BuildAsync());
                Clipboard.SetContent(package);
                Clipboard.Flush();   // stays on the clipboard after the app closes
                DiagnosticsButton.Content = "Copied";
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                DiagnosticsButton.Content = "Couldn't copy";
            }
            DiagnosticsButton.IsEnabled = true;
            await Task.Delay(2500);
            DiagnosticsButton.Content = "Copy diagnostics";
        }

        private async void SafetyReset_Click(object sender, RoutedEventArgs e) => await SetSafety(SafetyRule.Default);

        private async Task SetSafety(float limit)
        {
            string? error = await App.Control.SetSafetyAsync(limit);
            SafetyError.Message = error ?? "";
            SafetyError.IsOpen = error is not null;
        }
    }
}
