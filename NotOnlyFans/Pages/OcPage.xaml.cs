using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using NotOnlyFans.Contracts;
using NotOnlyFans.Controls;
using NotOnlyFans.Services;

namespace NotOnlyFans.Pages
{
    /// <summary>
    /// OC: graphics card overclocking like MSI Afterburner / LACT - core and memory clock offsets and the power limit.
    /// Changes stay on this page until Apply (marked "Not applied yet"), then the service sends them to the card and
    /// keeps them; ranges come from the driver. Live clocks, power and temperature come from the sensor stream.
    /// </summary>
    public sealed partial class OcPage : Page
    {
        private bool _isShown;
        private bool _settingControls;
        private bool _busy;
        private string? _selected;
        private string _shownCards = "";

        /// <summary>What the user set but has not applied yet, by card key (none = the card's current values).</summary>
        private readonly Dictionary<string, Draft> _drafts = [];

        private sealed record Draft(int Core, int Memory, float Power);

        public OcPage()
        {
            InitializeComponent();
            Tiles.SizeChanged += (_, e) => PlaceTiles(e.NewSize.Width);
            CoreBox.ValueChanged += (_, v) => Edit(d => d with { Core = (int)v });
            MemoryBox.ValueChanged += (_, v) => Edit(d => d with { Memory = (int)v });
            PowerBox.ValueChanged += (_, v) => Edit(d => d with { Power = (float)v });

            App.Sensors.OcChanged += OnOcChanged;
            App.Sensors.ConnectionChanged += Refresh;
            App.Sensors.CatalogChanged += RefreshLive;
            App.Sensors.ValuesChanged += RefreshLive;

            Loaded += (_, _) =>
            {
                _isShown = true;
                Refresh();
            };
            Unloaded += (_, _) => _isShown = false;
        }

        private int _tileColumns = 4;

        /// <summary>Four readings in a row; two by two when the window is narrow.</summary>
        private void PlaceTiles(double width)
        {
            int columns = width < 600 ? 2 : 4;
            if (columns == _tileColumns)
            {
                return;
            }
            _tileColumns = columns;
            Tiles.ColumnDefinitions.Clear();
            for (int c = 0; c < columns; c++)
            {
                Tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
            Tiles.RowDefinitions.Clear();
            Tiles.RowSpacing = 12;
            for (int r = 0; r < 4 / columns; r++)
            {
                Tiles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            for (int i = 0; i < Tiles.Children.Count; i++)
            {
                var tile = (FrameworkElement)Tiles.Children[i];
                Grid.SetRow(tile, i / columns);
                Grid.SetColumn(tile, i % columns);
            }
        }

        private static OcState? State => App.Sensors.Oc;

        private OcGpu? Gpu => State?.Gpus.FirstOrDefault(g => g.Key == _selected);

        /// <summary>What the card runs now, as a draft.</summary>
        private static Draft Running(OcGpu gpu) => new(gpu.CoreOffset, gpu.MemoryOffset, (float)Math.Round(gpu.PowerLimit));

        private Draft Current(OcGpu gpu) => _drafts.GetValueOrDefault(gpu.Key) ?? Running(gpu);

        private bool Pending(OcGpu gpu) => _drafts.TryGetValue(gpu.Key, out var d) && d != Running(gpu);

        // ----- Showing -----

        private void Refresh()
        {
            if (!_isShown)
            {
                return;
            }
            ServiceInfo.IsOpen = App.Sensors.IsServiceMissing;
            var gpus = State?.Gpus ?? [];
            NoCardInfo.IsOpen = App.Sensors.IsConnected && gpus.Count == 0;
            NoCardInfo.Message = State is null
                ? "The background service is older than this app and can't overclock yet. Update it with tools\\Install.ps1."
                : "NotOnlyFans can overclock NVIDIA GeForce cards, AMD Radeon RX 6000 cards and newer, and Intel Arc cards. Graphics built into the processor are left out.";

            // Card picker (only with more than one card).
            string cards = string.Join('|', gpus.Select(g => g.Key + "=" + g.Name));
            if (cards != _shownCards)
            {
                _shownCards = cards;
                _settingControls = true;
                CardBox.Items.Clear();
                foreach (var g in gpus)
                {
                    CardBox.Items.Add(new ComboBoxItem { Content = g.Name, Tag = g.Key });
                }
                _settingControls = false;
            }
            if (_selected is null || gpus.All(g => g.Key != _selected))
            {
                _selected = gpus.FirstOrDefault()?.Key;
            }
            CardBox.Visibility = gpus.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            _settingControls = true;
            CardBox.SelectedIndex = gpus.FindIndex(g => g.Key == _selected);
            _settingControls = false;

            var gpu = Gpu;
            // AMD and Intel support is new and not tested on real cards yet: said where it matters.
            bool experimental = gpu is not null && !gpu.Key.StartsWith("nvidia/", StringComparison.Ordinal);
            Subtitle.Text = gpu is null ? "Graphics card overclocking"
                : $"Graphics card overclocking · {gpu.Name}{(experimental ? " · experimental" : "")}";
            bool usable = gpu is not null && gpu.Unsupported is null && !App.Sensors.IsServiceMissing;
            UnsupportedInfo.IsOpen = gpu?.Unsupported is not null;
            UnsupportedInfo.Message = gpu?.Unsupported ?? "";
            string? notice = gpu is null ? null : State?.Notices.GetValueOrDefault(gpu.Key);
            NoticeInfo.IsOpen = notice is not null;
            NoticeInfo.Message = notice ?? "";
            Tiles.Visibility = gpu is null ? Visibility.Collapsed : Visibility.Visible;
            Editor.Visibility = usable ? Visibility.Visible : Visibility.Collapsed;
            if (gpu is not null && usable)
            {
                ShowEditor(gpu);
            }
            RefreshLive();
        }

        private void ShowEditor(OcGpu gpu)
        {
            if (_drafts.TryGetValue(gpu.Key, out var d) && d == Running(gpu))
            {
                _drafts.Remove(gpu.Key);   // the card runs it now
            }
            var draft = Current(gpu);
            var running = Running(gpu);
            var c = CultureInfo.CurrentCulture;
            _settingControls = true;

            bool core = gpu.CoreMax > gpu.CoreMin;
            // What the card or its driver doesn't allow stays greyed, with the reason on hover.
            Unavailable.SetCard(CoreCard, core ? null : gpu.CoreNote ?? "This card or its driver doesn't allow a core clock offset.", CoreSlider, CoreBox);
            if (!core)
            {
                SetRange(CoreSlider, CoreBox, 0, 0, 0);
                CoreHint.Text = "Not adjustable on this card";
            }
            else
            {
                SetRange(CoreSlider, CoreBox, gpu.CoreMin, gpu.CoreMax, draft.Core);
                string top = gpu.BoostClock > 0
                    ? string.Create(c, $"Boost up to {gpu.BoostClock - gpu.CoreOffset + draft.Core} MHz")
                    : "Added to every boost step";
                CoreHint.Text = draft.Core != running.Core ? $"{top} · now {Signed(running.Core)} MHz" : top;
            }

            bool memory = gpu.MemoryMax > gpu.MemoryMin;
            Unavailable.SetCard(MemoryCard, memory ? null : gpu.MemoryNote ?? "This card or its driver doesn't allow a memory clock offset.", MemorySlider, MemoryBox);
            if (!memory)
            {
                SetRange(MemorySlider, MemoryBox, 0, 0, 0);
                MemoryHint.Text = "Not adjustable on this card";
            }
            else
            {
                SetRange(MemorySlider, MemoryBox, gpu.MemoryMin, gpu.MemoryMax, draft.Memory);
                string top = gpu.MemoryClock > 0
                    ? string.Create(c, $"{gpu.MemoryClock - gpu.MemoryOffset + draft.Memory} MHz as the driver counts it")
                    : "Added to the memory clock";
                MemoryHint.Text = draft.Memory != running.Memory ? $"{top} · now {Signed(running.Memory)} MHz" : top;
            }

            bool power = gpu.PowerMax > gpu.PowerMin;
            Unavailable.SetCard(PowerCard, power ? null : gpu.PowerNote ?? "This card or its driver doesn't allow changing the power limit.", PowerSlider, PowerBox);
            bool percent = gpu.PowerUnit == OcUnits.Percent;
            PowerUnitText.Text = gpu.PowerUnit;
            AutomationProperties.SetName(PowerBox, percent ? "Power limit in percent" : "Power limit in watts");
            if (!power)
            {
                SetRange(PowerSlider, PowerBox, gpu.PowerLimit, gpu.PowerLimit, gpu.PowerLimit);
                PowerHint.Text = gpu.PowerLimit > 0 ? "Set by the card" : "Not adjustable on this card";
            }
            else
            {
                SetRange(PowerSlider, PowerBox, Math.Ceiling(gpu.PowerMin), Math.Floor(gpu.PowerMax), draft.Power);
                // Watts (NVIDIA, Intel Arc A) or % (AMD: from the default, Intel Arc B: of the default).
                string share = percent ? string.Create(c, $"Default {gpu.PowerDefault:0}%")
                    : gpu.PowerDefault > 0 ? string.Create(c, $"{draft.Power / gpu.PowerDefault * 100:0}% of the default {gpu.PowerDefault:0} W")
                    : "";
                string now = string.Create(c, $"now {running.Power:0}{(percent ? "%" : " W")}");
                PowerHint.Text = Math.Abs(draft.Power - running.Power) >= 1 ? $"{share} · {now}" : share;
            }

            bool pending = Pending(gpu);
            PendingNote.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
            ApplyButton.IsEnabled = pending && !_busy;
            DiscardButton.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
            bool atDefaults = running.Core == 0 && running.Memory == 0
                && (!power || Math.Abs(running.Power - gpu.PowerDefault) < 1);
            var saved = State?.Settings.GetValueOrDefault(gpu.Key);
            ResetButton.IsEnabled = !_busy && (!atDefaults || saved is not null || pending);

            StartupSwitch.IsEnabled = saved is not null && !_busy;
            StartupSwitch.IsOn = saved?.ApplyAtStartup == true;
            StartupHint.Text = saved is null
                ? "Apply an overclock first."
                : "Not applied after Windows or the graphics driver crashed, so an unstable overclock can't keep crashing the PC.";
            _settingControls = false;
        }

        private static void SetRange(Slider slider, Stepper box, double min, double max, double value)
        {
            slider.Minimum = min;
            slider.Maximum = max;
            slider.Value = value;
            box.Minimum = min;
            box.Maximum = max;
            box.Show(value);
        }

        private static string Signed(int value) => value > 0 ? $"+{value}" : value.ToString(CultureInfo.CurrentCulture);

        /// <summary>Live readings from the sensor stream (LibreHardwareMonitor's names for NVIDIA cards).</summary>
        private void RefreshLive()
        {
            if (!_isShown || Gpu is not { } gpu)
            {
                return;
            }
            string? hardware = HardwareId(gpu);
            float? Read(string kind, string name)
            {
                if (hardware is null || App.Sensors.Catalog is not { } catalog)
                {
                    return null;
                }
                var sensor = catalog.Sensors.FirstOrDefault(s => s.HardwareId == hardware && s.Kind == kind && s.Name == name);
                return sensor is not null && App.Sensors.TryGetIndex(sensor.Key, out int i) ? App.Sensors.GetNow(i) : null;
            }
            var c = CultureInfo.CurrentCulture;
            CoreNow.Text = Mhz(Read("Clock", "GPU Core"));
            LoadNow.Text = Read("Load", "GPU Core") is { } load ? string.Create(c, $"Load {load:0}%") : "";
            MemoryNow.Text = Mhz(Read("Clock", "GPU Memory"));
            MemoryLoadNow.Text = Read("Load", "GPU Memory Controller") is { } ml ? string.Create(c, $"Controller {ml:0}%") : "";
            float? watts = Read("Power", "GPU Package");
            PowerNow.Text = watts is { } w ? string.Create(c, $"{w:0} W") : SensorFormat.NoValue;
            PowerOfLimit.Text = gpu.PowerUnit == OcUnits.Percent
                ? gpu.PowerMax > gpu.PowerMin ? string.Create(c, $"Limit {gpu.PowerLimit:0}%") : ""
                : gpu.PowerLimit > 0 ? string.Create(c, $"Limit {gpu.PowerLimit:0} W") : "";
            TemperatureNow.Text = SensorFormat.Format("Temperature", Read("Temperature", "GPU Core"));
            HotSpotNow.Text = Read("Temperature", "GPU Hot Spot") is { } hot ? string.Create(c, $"Hot spot {hot:0} °C") : "";
        }

        private static string Mhz(float? value) =>
            value is { } v ? string.Create(CultureInfo.CurrentCulture, $"{v:0} MHz") : SensorFormat.NoValue;

        /// <summary>
        /// The card's sensors: LibreHardwareMonitor numbers each maker's cards (/gpu-nvidia/0, /gpu-amd/0…) in the
        /// driver's order, the order the service lists them in too (with one card of a maker, the usual case, it is simply 0).
        /// </summary>
        private static string? HardwareId(OcGpu gpu)
        {
            string maker = gpu.Key.Split('/')[0];
            if (maker is not ("nvidia" or "amd" or "intel"))
            {
                return null;
            }
            var same = State?.Gpus.Where(g => g.Key.StartsWith(maker + "/", StringComparison.Ordinal)).ToList() ?? [];
            int index = same.FindIndex(g => g.Key == gpu.Key);
            return index < 0 ? null : $"/gpu-{maker}/{index}";
        }

        // ----- Editing -----

        private void Edit(Func<Draft, Draft> change)
        {
            if (_settingControls || Gpu is not { } gpu)
            {
                return;
            }
            _drafts[gpu.Key] = change(Current(gpu));
            ShowEditor(gpu);
        }

        private void Slider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_settingControls)
            {
                return;
            }
            if (ReferenceEquals(sender, CoreSlider))
            {
                Edit(d => d with { Core = (int)Math.Round(e.NewValue) });
            }
            else if (ReferenceEquals(sender, MemorySlider))
            {
                Edit(d => d with { Memory = (int)Math.Round(e.NewValue) });
            }
            else if (ReferenceEquals(sender, PowerSlider))
            {
                Edit(d => d with { Power = (float)Math.Round(e.NewValue) });
            }
        }

        private void CardBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_settingControls && CardBox.SelectedItem is ComboBoxItem { Tag: string key })
            {
                _selected = key;
                Refresh();
            }
        }

        private void Discard_Click(object sender, RoutedEventArgs e)
        {
            if (Gpu is { } gpu)
            {
                _drafts.Remove(gpu.Key);
                ShowEditor(gpu);
            }
        }

        private async void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (Gpu is not { } gpu)
            {
                return;
            }
            var draft = Current(gpu);
            var saved = State?.Settings.GetValueOrDefault(gpu.Key);
            var setting = new OcSetting
            {
                CoreOffset = draft.Core,
                MemoryOffset = draft.Memory,
                // The card's default stays "default" (it follows the driver if that changes).
                PowerLimit = gpu.PowerMax > gpu.PowerMin && Math.Abs(draft.Power - gpu.PowerDefault) >= 1 ? draft.Power : null,
                ApplyAtStartup = saved?.ApplyAtStartup == true,
                RiskAccepted = saved?.RiskAccepted == true,
            };
            if (gpu.NeedsConsent && !setting.RiskAccepted)
            {
                // The card's maker (Intel) asks apps to get the user's OK once; it is kept with the setting.
                if (!await AcceptRisk(gpu))
                {
                    return;
                }
                setting.RiskAccepted = true;
            }
            await Run(() => App.Control.ApplyOcAsync(gpu.Key, setting));
        }

        private async Task<bool> AcceptRisk(OcGpu gpu)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Overclock this card?",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"Higher clocks and power put more electrical and thermal stress on the {gpu.Name}, which can shorten its life, " +
                           "and the card's maker may not cover damage from overclocking. The maker asks apps to get your OK before overclocking.",
                },
                PrimaryButtonText = "I accept the risk",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        private async void Reset_Click(object sender, RoutedEventArgs e)
        {
            if (Gpu is { } gpu)
            {
                _drafts.Remove(gpu.Key);
                await Run(() => App.Control.ResetOcAsync(gpu.Key));
            }
        }

        private async void StartupSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_settingControls && Gpu is { } gpu)
            {
                await Run(() => App.Control.SetOcStartupAsync(gpu.Key, StartupSwitch.IsOn));
            }
        }

        /// <summary>One request at a time; the service publishes the new state, which updates the page.</summary>
        private async Task Run(Func<Task<string?>> request)
        {
            _busy = true;
            ErrorInfo.IsOpen = false;
            Refresh();
            string? error;
            try
            {
                error = await request();
            }
            finally
            {
                _busy = false;
            }
            if (error is not null)
            {
                ErrorInfo.Message = error;
                ErrorInfo.IsOpen = true;
            }
            else if (_selected is { } key && _drafts.ContainsKey(key))
            {
                // Applied. The draft stays on screen until the service's new state arrives (it may come a moment
                // after this answer), then that state is what the page shows - even if the driver rounded a value.
                _sent.Add(key);
            }
            Refresh();
        }

        private readonly HashSet<string> _sent = [];

        private void OnOcChanged()
        {
            foreach (var key in _sent)
            {
                _drafts.Remove(key);
            }
            _sent.Clear();
            Refresh();
        }
    }
}
