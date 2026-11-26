using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NotOnlyFans.Contracts;
using NotOnlyFans.Controls;
using NotOnlyFans.Services;
using NotOnlyFans.ViewModels;

namespace NotOnlyFans.Pages
{
    /// <summary>
    /// Cooling: the list of fans, and one fan's page (Cooling › name) in the same page,
    /// switched instantly. Settings go to the service, which applies and saves them;
    /// what is shown always comes back from the service (so it is what really runs).
    /// </summary>
    public sealed partial class CoolingPage : Page
    {
        // After the user moves the slider, the service's echo must not pull it back mid-drag.
        private static readonly TimeSpan SliderQuiet = TimeSpan.FromSeconds(1.5);

        private List<FanInfo> _fans = [];
        private readonly Dictionary<string, FanRowViewModel> _rows = [];
        private string _shownFans = "";
        private List<FanRowViewModel> _listRows = [];   // the list on screen, in the user's order
        private string[] _keys = [];
        private FanInfo? _current;
        private bool _isShown;
        private bool _settingSlider;
        private DateTime _sliderTouched;
        // True while the page itself sets controls (and while it is being built: a slider with Minimum="1"
        // already fires ValueChanged inside InitializeComponent, before the controls next to it exist).
        private bool _settingControls = true;
        private DateTime _maxChangeTouched;

        // Temperature sources for curves: "Highest of CPU and GPU", then every real temperature sensor.
        private List<(string Label, List<string> Keys)> _sources = [];
        private bool _settingSource;

        // Mix: which of its curves the chart edits (the others are drawn dashed).
        private const int MaxMixCurves = 4;
        private int _mixPart;
        private bool _chartKeyboard;
        private string _mixPartsShown = "";

        // Offset: fans this one can follow (every other fan with a speed % reading), in the user's order.
        private List<FanInfo> _followFans = [];
        private DateTime _offsetTouched;

        private const string CurveHintText = "Drag points to shape the curve. Double-click to add a point, right-click a point to remove it.";
        private const string MixHintText = "Each curve has its own temperature. Choose a curve to edit it – the others show dashed.";

        // A new curve: quiet when cool, full speed at 85 °C (the user shapes it from there).
        private static readonly CurvePoint[] DefaultCurve =
        [
            new() { Temperature = 30, Speed = 30 },
            new() { Temperature = 50, Speed = 40 },
            new() { Temperature = 70, Speed = 70 },
            new() { Temperature = 85, Speed = 100 },
        ];

        public CoolingPage()
        {
            InitializeComponent();
            _settingControls = false;
            CurveChart.CurveCommitted += CurveChart_CurveCommitted;
            CurveChart.KeyboardFocusChanged += keyboard =>
            {
                _chartKeyboard = keyboard;   // the hint under the title lists the keys while the chart has keyboard focus
                Refresh();
            };
            HysteresisBox.ValueChanged += Response_ValueChanged;
            DelayBox.ValueChanged += Response_ValueChanged;
            StartBox.ValueChanged += Limits_ValueChanged;
            MinBox.ValueChanged += Limits_ValueChanged;

            App.Sensors.CatalogChanged += Rebuild;
            App.Sensors.ValuesChanged += Refresh;
            App.Sensors.ControlChanged += Refresh;
            App.Sensors.ConnectionChanged += Refresh;
            App.Settings.NamesChanged += ApplyNames;
            App.Pins.Changed += UpdatePins;

            Loaded += (_, _) =>
            {
                _isShown = true;
                Refresh();
            };
            Unloaded += (_, _) => _isShown = false;
            Theme.Follow(this);   // InfoBars after a light/dark change

            Rebuild();
        }

        // ---------- Navigation inside the page ----------

        /// <summary>The fan whose page is open (key of its RPM sensor), null on the list.</summary>
        public string? CurrentFanKey => _current?.Key;

        public void ShowList()
        {
            _current = null;
            FanView.Visibility = Visibility.Collapsed;
            ListPanel.Visibility = Visibility.Visible;
            Refresh();
            MainPage.Current?.Visited();
        }

        /// <summary>
        /// Opens one fan by the key of its RPM sensor (used by Home too). <paramref name="switching"/>: picked from the
        /// fans at the top of a fan's page, so Back returns to where the user was before the fan pages.
        /// </summary>
        public void ShowFan(string fanKey, bool switching = false)
        {
            var fan = _fans.FirstOrDefault(f => f.Key == fanKey);
            if (fan is null)
            {
                ShowList();
                return;
            }
            _current = fan;
            FanError.IsOpen = false;
            _sliderTouched = DateTime.MinValue;
            _offsetTouched = DateTime.MinValue;
            _mixPart = 0;
            _mixPartsShown = "";
            BuildFollow(fan);
            ListPanel.Visibility = Visibility.Collapsed;
            FanView.Visibility = Visibility.Visible;
            Refresh();
            MainPage.Current?.Visited(switching);
        }

        // ---------- The fans at the top of a fan's page ----------

        private readonly List<(string Key, Button Button)> _switcher = [];

        /// <summary>One button per fan of the list (rebuilt only when the list changes); names and the open fan each update.</summary>
        private void RefreshSwitcher(List<FanInfo> shown)
        {
            if (!_switcher.Select(s => s.Key).SequenceEqual(shown.Select(f => f.Key)))
            {
                _switcher.Clear();
                FanSwitcher.Children.Clear();
                foreach (var fan in shown)
                {
                    var button = new Button { Tag = fan.Key, Padding = new Thickness(12, 4, 12, 5) };
                    button.Click += (s, _) =>
                    {
                        if ((s as FrameworkElement)?.Tag is string key && key != _current?.Key)
                        {
                            ShowFan(key, switching: true);
                        }
                    };
                    _switcher.Add((fan.Key, button));
                    FanSwitcher.Children.Add(button);
                }
            }
            foreach (var (key, button) in _switcher)
            {
                string name = _rows[key].Name;
                if (button.Content as string != name)
                {
                    button.Content = name;
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
                }
                Select(button, key == _current?.Key);
            }
            // A single fan has nothing to switch to.
            FanSwitcher.Visibility = shown.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void FanRow_Click(object sender, RoutedEventArgs e)
        {
            // Released after carrying the row: that was a move, not a click.
            if (!Reorder.JustDragged && (sender as FrameworkElement)?.Tag is FanRowViewModel row)
            {
                ShowFan(row.Key);
            }
        }

        // "Cooling › name": the first part goes back to the list (the last is the page itself).
        private void FanCrumbs_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
        {
            if (args.Index == 0)
            {
                ShowList();
            }
        }

        private string _shownTitle = "";

        private void ShowTitle(string name)
        {
            if (name != _shownTitle)
            {
                _shownTitle = name;
                FanCrumbs.ItemsSource = new[] { "Cooling", name };
            }
        }

        /// <summary>The breadcrumb leaves room for rename and pin, so a long name folds into "…" instead of pushing them out.</summary>
        private void TitleRow_SizeChanged(object sender, SizeChangedEventArgs e) =>
            FanCrumbs.MaxWidth = Math.Max(0, e.NewSize.Width - RenameButton.Width - PinButton.Width - 24);

        // ---------- Name and pin (the fan page's title, and each row of the list) ----------

        private void RenameFan_Click(object sender, RoutedEventArgs e)
        {
            if (_current is { } fan)
            {
                Rename(_rows[fan.Key], FanCrumbs);
            }
        }

        private void RenameRow_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is FanRowViewModel row && RowElement(row) is { } element)
            {
                Rename(row, element);
            }
        }

        private static void Rename(FanRowViewModel row, FrameworkElement target) =>
            RenamePrompt.Show(target, "Fan name", row.Name, row.OriginalName,
                name => App.Settings.SetName(row.Key, name, row.OriginalName));

        private void ResetRowName_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is FanRowViewModel row)
            {
                App.Settings.SetName(row.Key, null, row.OriginalName);
            }
        }

        private void PinFan_Click(object sender, RoutedEventArgs e)
        {
            if (_current is { } fan)
            {
                TogglePin(fan);
            }
        }

        private void PinRow_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is FanRowViewModel row && _fans.FirstOrDefault(f => f.Key == row.Key) is { } fan)
            {
                TogglePin(fan);
            }
        }

        /// <summary>Pins the fan's RPM reading to the sidebar (the same reading Sensors pins), or unpins it.</summary>
        private static void TogglePin(FanInfo fan)
        {
            if (App.Sensors.Catalog is { } catalog && fan.Rpm >= 0)
            {
                App.Pins.Toggle(fan.Key, SensorLayout.PinLabel(catalog, fan.Rpm));
            }
        }

        private void UpdatePins()
        {
            foreach (var row in _rows.Values)
            {
                row.IsPinned = App.Pins.IsPinned(row.Key);
            }
            if (_current is { } fan)
            {
                ShowPin(_rows[fan.Key]);
            }
        }

        private void ShowPin(FanRowViewModel row)
        {
            PinGlyph.Glyph = row.IsPinned ? "" : "";
            PinGlyph.Foreground = Theme.Brush(this, row.IsPinned ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
            ToolTipService.SetToolTip(PinButton, row.PinText);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PinButton, row.PinText);
        }

        // ---------- The list's order: drag a row (RowReorder, as on Home), or Move up / down in its menu ----------

        private RowReorder? _reorder;
        private PointerEventHandler? _rowPressed, _rowMoved, _rowReleased, _rowCaptureLost;

        private RowReorder Reorder => _reorder ??= CreateReorder();

        private RowReorder CreateReorder()
        {
            var reorder = new RowReorder(FanList, () =>
            {
                var rows = new List<FrameworkElement>();
                foreach (var vm in _listRows)
                {
                    if (RowElement(vm) is not { } row)
                    {
                        return [];   // not all on screen yet: no drag this time
                    }
                    rows.Add(row);
                }
                return rows;
            });
            reorder.Lifted += (element, lifted) =>
            {
                // A solid card under the carried row, so the rows it passes don't show through it.
                ProtectedCursor = lifted ? RowReorder.HandCursor : null;
                if ((element as Button)?.Content is DragRow row)
                {
                    row.Background = lifted ? Theme.Brush(this, "SolidBackgroundFillColorQuarternaryBrush") : null;
                    row.BorderBrush = lifted ? Theme.Brush(this, "CardStrokeColorDefaultBrush") : null;
                    row.BorderThickness = new Thickness(lifted ? 1 : 0);
                }
            };
            reorder.Moved += MoveFan;
            return reorder;
        }

        /// <summary>
        /// A row is a Button, which keeps pointer events to itself: listen to the handled ones too.
        /// (Loaded can come more than once for a recycled row; the handlers are attached once.)
        /// </summary>
        private void FanRow_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Button row)
            {
                return;
            }
            // The pin button in the row keeps its press (carrying the row would take the pointer away from it).
            _rowPressed ??= (s, a) =>
            {
                if (!InInnerButton(a.OriginalSource as DependencyObject, s as DependencyObject))
                {
                    Reorder.Pressed(s, a);
                }
            };
            _rowMoved ??= (s, a) => Reorder.PointerMoved(s, a);
            _rowReleased ??= (s, a) => Reorder.Released(s, a);
            _rowCaptureLost ??= (s, a) => Reorder.CaptureLost(s, a);
            Attach(row, PointerPressedEvent, _rowPressed);
            Attach(row, PointerMovedEvent, _rowMoved);
            Attach(row, PointerReleasedEvent, _rowReleased);
            Attach(row, PointerCaptureLostEvent, _rowCaptureLost);

            static void Attach(UIElement element, RoutedEvent routedEvent, PointerEventHandler handler)
            {
                element.RemoveHandler(routedEvent, handler);
                element.AddHandler(routedEvent, handler, true);
            }
        }

        private static bool InInnerButton(DependencyObject? element, DependencyObject? row)
        {
            for (var d = element; d is not null && !ReferenceEquals(d, row); d = VisualTreeHelper.GetParent(d))
            {
                if (d is Button)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>The row on screen that shows this fan (found by the fan, not by ItemsRepeater's index).</summary>
        private Button? RowElement(FanRowViewModel vm)
        {
            int count = VisualTreeHelper.GetChildrenCount(FanList);
            for (int k = 0; k < count; k++)
            {
                if (VisualTreeHelper.GetChild(FanList, k) is Button row && ReferenceEquals(row.Tag, vm) && FanList.GetElementIndex(row) >= 0)
                {
                    return row;
                }
            }
            return null;
        }

        private void MoveFan(int from, int to)
        {
            if (from < 0 || to < 0 || from >= _listRows.Count || to >= _listRows.Count || from == to)
            {
                return;
            }
            var keys = _listRows.Select(r => r.Key).ToList();
            string key = keys[from];
            keys.RemoveAt(from);
            keys.Insert(to, key);
            FanCatalog.SaveOrder(_fans, keys);
            RefreshList();   // fresh rows in the new order (Home follows on its next update)
        }

        private void MoveFanUp_Click(object sender, RoutedEventArgs e) => MoveFan(sender, -1);

        private void MoveFanDown_Click(object sender, RoutedEventArgs e) => MoveFan(sender, +1);

        private void MoveFan(object sender, int delta)
        {
            if ((sender as FrameworkElement)?.Tag is FanRowViewModel row)
            {
                int from = _listRows.IndexOf(row);
                MoveFan(from, from + delta);
            }
        }

        // ---------- Data ----------

        private void Rebuild()
        {
            if (App.Sensors.Catalog is not { } catalog)
            {
                return;
            }
            var keys = catalog.Sensors.Select(s => s.Key).ToArray();
            if (keys.AsSpan().SequenceEqual(_keys))
            {
                return;
            }
            _keys = keys;

            _fans = FanCatalog.All(catalog);
            _rows.Clear();
            foreach (var fan in _fans)
            {
                _rows[fan.Key] = new FanRowViewModel(fan.Key, fan.OriginalName, fan.Source, FanCatalog.ModeText(fan))
                {
                    Name = SensorLayout.DisplayName(catalog, fan.Rpm),
                };
            }
            _shownFans = "";
            _historyShown = "";
            UpdatePins();
            BuildSources(catalog);
            if (_current is not null)
            {
                ShowFan(_current.Key);
            }
            Refresh();
        }

        private void BuildSources(SensorCatalog catalog)
        {
            var sources = TemperatureSources.Build(catalog);
            _sources = sources;
            _settingSource = true;
            SourceBox.ItemsSource = sources.Select(s => s.Label).ToList();
            _settingSource = false;
        }

        /// <summary>Where a new curve looks first: a graphics card fan → that card's core; anything else → the CPU.</summary>
        private List<string> DefaultSources(FanInfo fan)
        {
            if (App.Sensors.Catalog is { } catalog)
            {
                if (fan.IsGpu)
                {
                    string hwId = catalog.Sensors[fan.Rpm].HardwareId;
                    int gpu = TemperatureSources.Find(catalog, h => h.Id == hwId, "GPU Core");
                    if (gpu >= 0)
                    {
                        return [catalog.Sensors[gpu].Key];
                    }
                }
                int cpu = TemperatureSources.Cpu(catalog);
                if (cpu >= 0)
                {
                    return [catalog.Sensors[cpu].Key];
                }
            }
            return _sources.Count > 0 ? [.. _sources[0].Keys] : [];
        }

        private void ApplyNames()
        {
            if (App.Sensors.Catalog is not { } catalog)
            {
                return;
            }
            foreach (var fan in _fans)
            {
                _rows[fan.Key].Name = SensorLayout.DisplayName(catalog, fan.Rpm);
            }
            if (_current is not null)
            {
                BuildFollow(_current);
            }
            Refresh();
        }

        /// <summary>Fans the Offset mode can follow: every other fan that reports a speed %, in the user's order.</summary>
        private void BuildFollow(FanInfo fan)
        {
            _followFans = FanCatalog.Ordered(_fans)
                .Where(f => f.Key != fan.Key && f.Control >= 0 && !FanCatalog.IsEmptyHeader(f))
                .ToList();
            bool was = _settingControls;
            _settingControls = true;
            FollowBox.ItemsSource = _followFans.Select(f => _rows[f.Key].Name).ToList();
            _settingControls = was;
        }

        /// <summary>Key of a fan's speed % sensor (what Offset follows), also for fans that can't be driven.</summary>
        private static string? ControlSensorKey(FanInfo fan) =>
            fan.Control >= 0 && App.Sensors.Catalog is { } catalog ? catalog.Sensors[fan.Control].Key : null;

        private void Refresh()
        {
            if (!_isShown)
            {
                return;
            }

            var control = App.Sensors.Control;
            bool emergency = control?.Emergency == true;
            float limit = control?.SafetyTemperature ?? 90;
            string safetyNote = string.Create(CultureInfo.CurrentCulture,
                $"Safety: every fan controlled by NotOnlyFans runs at 100% when any temperature sensor reaches {limit:0} °C, whatever its setting.");
            string emergencyText = string.Create(CultureInfo.CurrentCulture,
                $"{control?.EmergencySensor ?? "A sensor"} reached {limit:0} °C. Fans controlled by NotOnlyFans run at 100% until temperatures drop by 5 °C.");

            ListServiceInfo.IsOpen = App.Sensors.IsServiceMissing;
            ListSafetyInfo.IsOpen = emergency;
            ListSafetyInfo.Message = emergencyText;
            ListSafetyNote.Text = safetyNote;
            FanSafetyInfo.IsOpen = emergency;
            FanSafetyInfo.Message = emergencyText;
            FanSafetyNote.Text = safetyNote;

            RefreshList();
            if (_current is not null)
            {
                RefreshFan(_current);
            }
        }

        private void RefreshList()
        {
            var shown = FanCatalog.Ordered(_fans).Where(f => !FanCatalog.IsEmptyHeader(f)).ToList();
            string shownKey = string.Join(',', shown.Select(f => f.Key));
            if (shownKey != _shownFans)
            {
                _shownFans = shownKey;
                _listRows = shown.Select(f => _rows[f.Key]).ToList();
                FanList.ItemsSource = _listRows;
            }
            foreach (var fan in shown)
            {
                var row = _rows[fan.Key];
                float? rpm = FanCatalog.Now(fan.Rpm);
                float? duty = FanCatalog.Now(fan.Control);
                row.Rpm = rpm is 0 ? "Stopped" : SensorFormat.Format("Fan", rpm);
                row.Percent = duty is null ? "" : SensorFormat.Format("Control", duty);
                row.BarWidth = Math.Clamp(duty ?? 0, 0, 100) / 100 * FanRowViewModel.BarLength;
                row.Mode = FanCatalog.ModeText(fan);
            }
            if (_current is not null)
            {
                RefreshSwitcher(shown);
            }
        }

        private void RefreshFan(FanInfo fan)
        {
            var row = _rows[fan.Key];
            ShowTitle(row.Name);
            FanSubtitle.Text = row.Detail;
            ShowPin(row);

            float? rpm = FanCatalog.Now(fan.Rpm);
            float? duty = FanCatalog.Now(fan.Control);
            StatusRpm.Text = rpm is 0 ? "Stopped" : SensorFormat.Format("Fan", rpm);
            StatusSpeed.Text = duty is null ? SensorFormat.NoValue : SensorFormat.Format("Control", duty);
            // Modes this fan can't use stay greyed, with the reason on hover.
            bool connected = App.Sensors.IsConnected;
            NoControlText.Visibility = fan.ControlKey is null ? Visibility.Visible : Visibility.Collapsed;
            string? noControl = fan.ControlKey is null
                ? "This fan reports its speed, but the motherboard or the graphics card doesn't let Windows set it."
                : null;
            string? noTemperature = _sources.Count == 0 ? "No temperature sensor was found to follow." : null;
            string? noFan = _followFans.Count == 0 ? "There is no other fan here NotOnlyFans can control to follow." : null;
            Unavailable.Set(ModeCurve, noControl ?? noTemperature, connected);
            Unavailable.Set(ModeFixed, noControl, connected);
            Unavailable.Set(ModeMix, noControl ?? noTemperature, connected);
            Unavailable.Set(ModeOffset, noControl ?? noFan, connected);
            Unavailable.Set(ModeBios, noControl, connected);
            ModeBios.Content = fan.IsGpu ? "Auto" : "BIOS";

            var setting = FanCatalog.Setting(fan);
            Select(ModeCurve, setting.Mode == FanMode.Curve);
            Select(ModeFixed, setting.Mode == FanMode.Fixed);
            Select(ModeMix, setting.Mode == FanMode.Mix);
            Select(ModeOffset, setting.Mode == FanMode.Offset);
            Select(ModeBios, setting.Mode == FanMode.Bios);

            bool hasControl = fan.ControlKey is not null;
            bool curve = hasControl && setting.Mode == FanMode.Curve;
            bool mix = hasControl && setting.Mode == FanMode.Mix && setting.Mix.Count > 0;
            bool offset = hasControl && setting.Mode == FanMode.Offset;

            // Curve / Mix: chart, source, and the "Now" marker. A mix edits one of its curves at a time.
            CurveCard.Visibility = curve || mix ? Visibility.Visible : Visibility.Collapsed;
            MixBar.Visibility = mix ? Visibility.Visible : Visibility.Collapsed;
            int part = mix ? MixPart(setting) : 0;
            var (points, sources) = mix ? (setting.Mix[part].Curve, setting.Mix[part].Sources) : (setting.Curve, setting.Sources);
            float? sourceTemp = curve || mix ? SourceTemperature(sources) : null;
            StatusSource.Text = curve || mix ? SensorFormat.Format("Temperature", sourceTemp)
                : offset ? SensorFormat.Format("Control", FollowedSpeed(setting))
                : SensorFormat.NoValue;
            if (curve || mix)
            {
                CurveTitle.Text = mix ? "Mix curves" : "Fan curve";
                CurveHint.Text = _chartKeyboard ? CurveEditor.KeyboardHint : mix ? MixHintText : CurveHintText;
                CurveChart.SetCurve(points);
                CurveChart.SetGhosts(mix
                    ? setting.Mix.Where((_, i) => i != part).Select(m => (IReadOnlyList<CurvePoint>)m.Curve).ToList()
                    : []);

                string stopped = rpm is 0 ? " · Stopped" : "";
                if (mix)
                {
                    // The dot sits on this curve; the fan's real speed may come from another curve of the mix.
                    float? partSpeed = sourceTemp is { } t ? MathF.Round(FanCurve.Evaluate(points, t)) : null;
                    CurveChart.SetNow(sourceTemp, partSpeed, string.Create(CultureInfo.CurrentCulture,
                        $"Now {SensorFormat.Format("Temperature", sourceTemp)} · this curve {SensorFormat.Format("Control", partSpeed)} · fan {SensorFormat.Format("Control", duty)}{stopped}"));
                    RefreshMixBar(setting, part);
                }
                else
                {
                    CurveChart.SetNow(sourceTemp, duty, string.Create(CultureInfo.CurrentCulture,
                        $"Now {SensorFormat.Format("Temperature", sourceTemp)} · {SensorFormat.Format("Control", duty)}{stopped}"));
                }

                int selected = _sources.FindIndex(s => s.Keys.Count == sources.Count && s.Keys.All(sources.Contains));
                if (SourceBox.SelectedIndex != selected)
                {
                    _settingSource = true;
                    SourceBox.SelectedIndex = selected;
                    _settingSource = false;
                }
            }

            FixedCard.Visibility = hasControl && setting.Mode == FanMode.Fixed ? Visibility.Visible : Visibility.Collapsed;
            BiosCard.Visibility = hasControl && setting.Mode == FanMode.Bios ? Visibility.Visible : Visibility.Collapsed;
            OffsetCard.Visibility = offset ? Visibility.Visible : Visibility.Collapsed;
            if (offset)
            {
                RefreshOffset(setting);
            }
            BiosTitle.Text = fan.IsGpu ? "Graphics card control" : "BIOS control";
            BiosText.Text = fan.IsGpu
                ? "NotOnlyFans does not touch this fan. The graphics card sets its speed itself."
                : "NotOnlyFans does not touch this fan. Speed follows the BIOS Smart Fan setting.";

            if (setting.Mode == FanMode.Fixed && DateTime.UtcNow - _sliderTouched > SliderQuiet)
            {
                SetSlider(setting.Fixed);
            }

            // Graphics card fans: what 0% means on this card.
            bool driven = hasControl && setting.Mode != FanMode.Bios;
            GpuZeroCard.Visibility = fan.IsGpu && driven ? Visibility.Visible : Visibility.Collapsed;
            if (fan.IsGpu && driven)
            {
                GpuZeroText.Text = GpuZeroExplanation(fan);
            }

            // Response (curve and mix) and Limits (every mode that drives the fan), filled from what the service runs.
            ResponseSection.Visibility = curve || mix ? Visibility.Visible : Visibility.Collapsed;
            LimitsSection.Visibility = driven ? Visibility.Visible : Visibility.Collapsed;
            ShowResponseAndLimits(setting);
            if (driven)
            {
                RefreshCalibration(fan);
            }
            RefreshHistory(fan, setting, curve || mix ? sources : null, offset);
        }

        // ---------- Last 5 minutes ----------

        private string _historyShown = "";
        private readonly float[] _historyPart = new float[SensorHistory.Capacity];

        /// <summary>
        /// Two charts: the speed (% where the fan reports it, else RPM), and under it what sets the speed - the curve's
        /// temperature (in a mix, the curve being edited), the fan an Offset follows, or else the fan's RPM.
        /// Set up again only when what they show changes; they redraw themselves every second.
        /// </summary>
        private void RefreshHistory(FanInfo fan, FanSetting setting, List<string>? sources, bool offset)
        {
            bool percent = fan.Control >= 0;
            int followed = offset && setting.Follow is { } follow && App.Sensors.TryGetIndex(follow, out int f) ? f : -1;
            string driver = sources is { Count: > 0 } ? "temp:" + string.Join(',', sources)
                : followed >= 0 ? $"follow:{followed}"
                : percent ? "rpm" : "";
            string state = $"{fan.Key}|{driver}|{_rows[fan.Key].Name}";
            if (state == _historyShown)
            {
                return;
            }
            _historyShown = state;

            SpeedChartTitle.Text = percent ? "Speed" : "RPM";
            SpeedChart.Kind = percent ? "Control" : "Fan";
            SpeedChart.SensorIndex = percent ? fan.Control : fan.Rpm;

            if (sources is { Count: > 0 })
            {
                var keys = sources.ToList();
                DriverChartTitle.Text = $"Source · {SourceLabel(keys) ?? "Temperature"}";
                DriverChart.Kind = "Temperature";
                DriverChart.Source = buffer => Highest(keys, buffer);
            }
            else if (followed >= 0)
            {
                var leader = _fans.FirstOrDefault(other => ControlSensorKey(other) == setting.Follow);
                DriverChartTitle.Text = $"Follows · {(leader is null ? "another fan" : _rows[leader.Key].Name)}";
                DriverChart.Kind = "Control";
                DriverChart.SensorIndex = followed;
            }
            else if (percent)
            {
                DriverChartTitle.Text = "RPM";
                DriverChart.Kind = "Fan";
                DriverChart.SensorIndex = fan.Rpm;
            }
            bool two = driver.Length > 0;
            DriverChartTitle.Visibility = DriverChart.Visibility = two ? Visibility.Visible : Visibility.Collapsed;
            SpeedChart.ShowTimeAxis = !two;   // one time axis, under the lower chart
            SpeedChart.Redraw();
            DriverChart.Redraw();
        }

        /// <summary>A source of several sensors (e.g. "Highest of CPU and GPU"): the highest of them at each moment.</summary>
        private int Highest(List<string> keys, float[] buffer)
        {
            int count = 0;
            bool first = true;
            foreach (var key in keys)
            {
                if (!App.Sensors.TryGetIndex(key, out int index))
                {
                    continue;
                }
                if (first)
                {
                    count = App.History.CopyTo(index, buffer);
                    first = false;
                    continue;
                }
                int n = Math.Min(count, App.History.CopyTo(index, _historyPart));
                for (int i = 0; i < n; i++)
                {
                    if (float.IsNaN(buffer[i]) || _historyPart[i] > buffer[i])
                    {
                        buffer[i] = _historyPart[i];
                    }
                }
            }
            return count;
        }

        // ---------- Calibration ----------

        private const string CalibrationAbout =
            "Measures start speed and minimum and sets them. The fan goes to 100%, slows down until it stops, then starts again. Each step waits until the speed settles – usually two to three minutes.";

        private void RefreshCalibration(FanInfo fan)
        {
            var control = App.Sensors.Control;
            var run = control?.Calibration;
            string? key = fan.ControlKey;
            bool here = run is not null && run.Key == key;
            bool running = run?.Status == CalibrationStatus.Running;
            CalibrationResult? last = key is not null && control?.Calibrations.TryGetValue(key, out var r) == true ? r : null;

            CalibrateButton.Content = here && running ? "Cancel" : last is null ? "Calibrate" : "Calibrate again";
            Unavailable.Set(CalibrateButton,
                control?.Emergency == true ? "Every fan runs at 100% while a sensor is at the emergency temperature."
                : running && !here ? "Another fan is being calibrated. One fan at a time."
                : null,
                App.Sensors.IsConnected);
            CalibrationBar.Visibility = here && running ? Visibility.Visible : Visibility.Collapsed;
            CalibrationBar.Value = here ? run!.Progress * 100 : 0;

            CalibrationText.Text = here && running ? run!.Message
                : running ? "Another fan is being calibrated. One fan at a time."
                : here ? run!.Message   // how the last run here ended (done, failed, cancelled)
                : CalibrationAbout;

            // The measured values, unless the message above already says them.
            bool showLast = last is not null && !(here && run!.Status == CalibrationStatus.Done);
            CalibrationLast.Visibility = showLast ? Visibility.Visible : Visibility.Collapsed;
            if (showLast)
            {
                string when = last!.Time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
                CalibrationLast.Text = last.Stops
                    ? string.Create(CultureInfo.CurrentCulture, $"Last calibration ({when}): starts at {last.StartSpeed:0}%, runs down to {last.MinSpeed:0}% ({last.MinRpm:0} RPM), {last.MaxRpm:0} RPM at 100%.")
                    : string.Create(CultureInfo.CurrentCulture, $"Last calibration ({when}): keeps turning even at 0% ({last.MinRpm:0} RPM), {last.MaxRpm:0} RPM at 100%.");
            }
        }

        private async void Calibrate_Click(object sender, RoutedEventArgs e)
        {
            if (_current is not { ControlKey: { } key } fan)
            {
                return;
            }
            var run = App.Sensors.Control?.Calibration;
            if (run is { Status: CalibrationStatus.Running } && run.Key == key)
            {
                await ReportCalibration(App.Control.CancelCalibrationAsync(key));
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"Calibrate {_rows[fan.Key].Name}?",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "The fan goes to 100%, slows down until it stops, then starts again. It takes two to three minutes; " +
                           "the result replaces this fan's start speed and minimum.\n\n" +
                           "Don't calibrate a header with a water pump on it – the pump would stop.",
                },
                PrimaryButtonText = "Calibrate",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await ReportCalibration(App.Control.CalibrateAsync(key));
            }
        }

        private async Task ReportCalibration(Task<string?> request)
        {
            string? error = await request;
            FanError.Title = "Couldn't calibrate this fan";
            FanError.Message = error ?? "";
            FanError.IsOpen = error is not null;
        }

        /// <summary>The mix curve being edited (the stored choice may be ahead of the service while a change is on its way).</summary>
        private int MixPart(FanSetting s) => Math.Clamp(_mixPart, 0, Math.Max(0, s.Mix.Count - 1));

        /// <summary>One button per mix curve, named after its temperature source; add / remove; how they combine.</summary>
        private void RefreshMixBar(FanSetting setting, int part)
        {
            var labels = setting.Mix.Select((m, i) => SourceLabel(m.Sources) ?? $"Curve {i + 1}").ToList();
            string shown = string.Join('\n', labels);
            if (shown != _mixPartsShown)
            {
                _mixPartsShown = shown;
                MixParts.Children.Clear();
                for (int i = 0; i < labels.Count; i++)
                {
                    var button = new Button
                    {
                        Tag = i,
                        MinWidth = 72,
                        MaxWidth = 220,
                        Content = new TextBlock { Text = labels[i], TextTrimming = TextTrimming.CharacterEllipsis },
                    };
                    ToolTipService.SetToolTip(button, labels[i]);
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"Edit curve {i + 1}: {labels[i]}");
                    button.Click += MixPart_Click;
                    MixParts.Children.Add(button);
                }
            }
            for (int i = 0; i < MixParts.Children.Count; i++)
            {
                Select((Button)MixParts.Children[i], i == part);
            }
            MixAdd.IsEnabled = setting.Mix.Count < MaxMixCurves;
            MixRemove.IsEnabled = setting.Mix.Count > 2;

            int function = setting.MixFunction switch { MixFunction.Average => 1, MixFunction.Min => 2, _ => 0 };
            if (MixFunctionBox.SelectedIndex != function)
            {
                bool was = _settingControls;
                _settingControls = true;
                MixFunctionBox.SelectedIndex = function;
                _settingControls = was;
            }
        }

        private string? SourceLabel(List<string> keys)
        {
            int i = _sources.FindIndex(s => s.Keys.Count == keys.Count && s.Keys.All(keys.Contains));
            return i >= 0 ? _sources[i].Label : null;
        }

        private void RefreshOffset(FanSetting setting)
        {
            bool was = _settingControls;
            _settingControls = true;
            int follow = _followFans.FindIndex(f => ControlSensorKey(f) == setting.Follow);
            if (FollowBox.SelectedIndex != follow)
            {
                FollowBox.SelectedIndex = follow;
            }
            if (DateTime.UtcNow - _offsetTouched > SliderQuiet)
            {
                OffsetSlider.Value = Math.Clamp(setting.Offset, OffsetSlider.Minimum, OffsetSlider.Maximum);
                OffsetValue.Text = OffsetText(setting.Offset);
            }
            _settingControls = was;
        }

        private static string OffsetText(float value) =>
            string.Create(CultureInfo.CurrentCulture, $"{(value > 0 ? "+" : "")}{value:0}%");

        /// <summary>Speed (%) of the fan an Offset follows, as the service sees it now.</summary>
        private static float? FollowedSpeed(FanSetting setting) =>
            setting.Follow is { } key && App.Sensors.TryGetIndex(key, out int i) ? App.Sensors.GetNow(i) : null;

        private static string GpuZeroExplanation(FanInfo fan)
        {
            string minimum = "";
            if (App.Sensors.Catalog is { } catalog && fan.Control >= 0 && catalog.Sensors[fan.Control].ControlMinimum is > 0 and var min)
            {
                minimum = string.Create(CultureInfo.CurrentCulture, $" The card's driver reports {min:0}% as its lowest running speed.");
            }

            ZeroStopMethod? method = fan.ControlKey is { } key && App.Sensors.Control?.ZeroStop.TryGetValue(key, out var m) == true ? m : null;
            return method switch
            {
                ZeroStopMethod.Manual =>
                    "At 0% NotOnlyFans stops this fan itself – a true 0%, like a curve in MSI Afterburner." + minimum,
                ZeroStopMethod.HandBack =>
                    "This card doesn't accept 0% from programs. At 0% the fan is handed back to the card, which keeps it still while cool " +
                    "but may start it on its own now and then. Above 0% NotOnlyFans takes it over again." + minimum,
                _ =>
                    "The first time this fan is asked for 0%, NotOnlyFans checks for about 15 seconds whether the card accepts a true 0% " +
                    "and remembers the result. Close MSI Afterburner first, or it will fight over the fan." + minimum,
            };
        }

        private void ShowResponseAndLimits(FanSetting s)
        {
            _settingControls = true;
            SmoothSwitch.IsOn = s.Smooth;
            SmoothLabel.Text = s.Smooth ? "On" : "Off";
            MaxChangeSlider.IsEnabled = s.Smooth;
            if (DateTime.UtcNow - _maxChangeTouched > SliderQuiet)
            {
                MaxChangeSlider.Value = Math.Clamp(s.MaxChangePerSecond, 1, 20);
                MaxChangeValue.Text = string.Create(CultureInfo.CurrentCulture, $"{s.MaxChangePerSecond:0} %/s");
            }
            SetBox(HysteresisBox, s.Hysteresis);
            SetBox(DelayBox, s.DelayDown);
            AllowStopSwitch.IsOn = s.AllowStop;
            AllowStopLabel.Text = s.AllowStop ? "On" : "Off";
            SetBox(StartBox, s.StartSpeed);
            SetBox(MinBox, s.MinSpeed);
            _settingControls = false;
        }

        /// <summary>Shows the service's value (the stepper ignores it while the user is changing it).</summary>
        private static void SetBox(Stepper box, float value) => box.Show(value);

        private static void Select(Button button, bool selected)
        {
            var accent = (Style)Application.Current.Resources["AccentButtonStyle"];
            if (selected && !ReferenceEquals(button.Style, accent))
            {
                button.Style = accent;
            }
            else if (!selected && button.Style is not null)
            {
                button.ClearValue(StyleProperty);
            }
        }

        private void SetSlider(float value)
        {
            _settingSlider = true;
            FixedSlider.Value = value;
            FixedValue.Text = SensorFormat.Format("Control", value);
            _settingSlider = false;
        }

        // ---------- Changes ----------
        // Every change starts from a copy of the fan's current setting, so switching modes keeps the curve,
        // the fixed speed and the response settings the user made before.

        private async void Mode_Click(object sender, RoutedEventArgs e)
        {
            if (_current is not { ControlKey: { } key } fan || (sender as FrameworkElement)?.Tag is not string tag ||
                !Enum.TryParse<FanMode>(tag, out var mode))
            {
                return;
            }

            var setting = FanCatalog.Setting(fan).Copy();
            if (setting.Mode != FanMode.Fixed)
            {
                // Fixed starts from the speed the fan runs at now, so nothing jumps.
                setting.Fixed = MathF.Round(FanCatalog.Now(fan.Control) ?? setting.Fixed);
            }
            if (mode is FanMode.Curve or FanMode.Mix)
            {
                if (setting.Curve.Count < 2)
                {
                    setting.Curve = NewCurve();
                }
                if (setting.Sources.Count == 0)
                {
                    setting.Sources = DefaultSources(fan);
                }
            }
            if (mode == FanMode.Mix && setting.Mix.Count < 2)
            {
                // A new mix starts from this fan's curve plus a second one for another temperature (CPU ↔ GPU).
                var first = new MixCurve { Curve = setting.Curve.Select(p => new CurvePoint { Temperature = p.Temperature, Speed = p.Speed }).ToList(), Sources = [.. setting.Sources] };
                setting.Mix = [first, new MixCurve { Curve = NewCurve(), Sources = OtherSource([first.Sources]) }];
                _mixPart = 0;
            }
            if (mode == FanMode.Offset && !_followFans.Any(f => ControlSensorKey(f) == setting.Follow))
            {
                if (_followFans.Count == 0)
                {
                    return;
                }
                setting.Follow = ControlSensorKey(_followFans[0]);
            }
            setting.Mode = mode;
            await Send(key, setting);
        }

        private static List<CurvePoint> NewCurve() =>
            DefaultCurve.Select(p => new CurvePoint { Temperature = p.Temperature, Speed = p.Speed }).ToList();

        /// <summary>
        /// A temperature source not used yet by a mix: the CPU or the graphics card first (the usual pair),
        /// then any other sensor.
        /// </summary>
        private List<string> OtherSource(List<List<string>> used)
        {
            bool IsUsed(IReadOnlyCollection<string> keys) => used.Any(u => u.Count == keys.Count && u.All(keys.Contains));

            if (App.Sensors.Catalog is { } catalog)
            {
                int cpu = TemperatureSources.Cpu(catalog);
                int gpu = TemperatureSources.Gpu(catalog);
                foreach (int i in new[] { cpu, gpu })
                {
                    if (i >= 0 && !IsUsed((string[])[catalog.Sensors[i].Key]))
                    {
                        return [catalog.Sensors[i].Key];
                    }
                }
            }
            foreach (var s in _sources)
            {
                if (!IsUsed(s.Keys))
                {
                    return [.. s.Keys];
                }
            }
            return _sources.Count > 0 ? [.. _sources[0].Keys] : [];
        }

        // ----- Mix -----

        private void MixPart_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is int i)
            {
                _mixPart = i;
                Refresh();
            }
        }

        private async void MixAdd_Click(object sender, RoutedEventArgs e)
        {
            if (_current is not { ControlKey: { } key } fan)
            {
                return;
            }
            var setting = FanCatalog.Setting(fan).Copy();
            if (setting.Mode != FanMode.Mix || setting.Mix.Count >= MaxMixCurves)
            {
                return;
            }
            setting.Mix.Add(new MixCurve { Curve = NewCurve(), Sources = OtherSource(setting.Mix.Select(m => m.Sources).ToList()) });
            _mixPart = setting.Mix.Count - 1;   // edit the new curve
            await Send(key, setting);
        }

        private async void MixRemove_Click(object sender, RoutedEventArgs e)
        {
            if (_current is not { ControlKey: { } key } fan)
            {
                return;
            }
            var setting = FanCatalog.Setting(fan).Copy();
            if (setting.Mode != FanMode.Mix || setting.Mix.Count <= 2)
            {
                return;
            }
            int part = MixPart(setting);
            setting.Mix.RemoveAt(part);
            _mixPart = Math.Min(part, setting.Mix.Count - 1);
            await Send(key, setting);
        }

        private async void MixFunctionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var function = MixFunctionBox.SelectedIndex switch { 1 => MixFunction.Average, 2 => MixFunction.Min, _ => MixFunction.Max };
            await Change(s => s.MixFunction = function);
        }

        // ----- Offset -----

        private async void FollowBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int i = FollowBox.SelectedIndex;
            if (i < 0 || i >= _followFans.Count)
            {
                return;
            }
            string? follow = ControlSensorKey(_followFans[i]);
            await Change(s => s.Follow = follow);
        }

        private async void OffsetSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_settingControls)
            {
                return;
            }
            _offsetTouched = DateTime.UtcNow;
            float value = (float)Math.Round(e.NewValue);
            OffsetValue.Text = OffsetText(value);
            await Change(s => s.Offset = value);
        }

        private async void FixedSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_settingSlider || _current is not { ControlKey: { } key } fan)
            {
                return;
            }
            _sliderTouched = DateTime.UtcNow;
            float value = (float)Math.Round(e.NewValue);
            FixedValue.Text = SensorFormat.Format("Control", value);
            var setting = FanCatalog.Setting(fan).Copy();
            setting.Mode = FanMode.Fixed;
            setting.Fixed = value;
            await Send(key, setting);
        }

        private async void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_settingSource || SourceBox.SelectedIndex < 0 || _current is not { ControlKey: { } key } fan)
            {
                return;
            }
            var setting = FanCatalog.Setting(fan).Copy();
            List<string> keys = [.. _sources[SourceBox.SelectedIndex].Keys];
            if (setting.Mode == FanMode.Mix && setting.Mix.Count > 0)
            {
                setting.Mix[MixPart(setting)].Sources = keys;
            }
            else
            {
                setting.Sources = keys;
            }
            await Send(key, setting);
        }

        private async void CurveChart_CurveCommitted(List<CurvePoint> points)
        {
            if (_current is not { ControlKey: { } key } fan)
            {
                return;
            }
            var setting = FanCatalog.Setting(fan).Copy();
            if (setting.Mode == FanMode.Mix && setting.Mix.Count > 0)
            {
                setting.Mix[MixPart(setting)].Curve = points;
            }
            else
            {
                setting.Curve = points;
            }
            await Send(key, setting);
        }

        // ----- Response and Limits -----

        private async Task Change(Action<FanSetting> change)
        {
            if (_settingControls || _current is not { ControlKey: { } key } fan)
            {
                return;
            }
            var setting = FanCatalog.Setting(fan).Copy();
            change(setting);
            await Send(key, setting);
        }

        private async void Smooth_Toggled(object sender, RoutedEventArgs e) =>
            await Change(s => s.Smooth = SmoothSwitch.IsOn);

        private async void AllowStop_Toggled(object sender, RoutedEventArgs e) =>
            await Change(s => s.AllowStop = AllowStopSwitch.IsOn);

        private async void MaxChange_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_settingControls)
            {
                return;
            }
            _maxChangeTouched = DateTime.UtcNow;
            float value = (float)Math.Round(e.NewValue);
            MaxChangeValue.Text = string.Create(CultureInfo.CurrentCulture, $"{value:0} %/s");
            await Change(s => s.MaxChangePerSecond = value);
        }

        private async void Response_ValueChanged(Stepper sender, double newValue)
        {
            if (_settingControls)
            {
                return;
            }
            float value = (float)newValue;
            await Change(s =>
            {
                if (ReferenceEquals(sender, HysteresisBox))
                {
                    s.Hysteresis = value;
                }
                else
                {
                    s.DelayDown = value;
                }
            });
        }

        private async void Limits_ValueChanged(Stepper sender, double newValue)
        {
            if (_settingControls)
            {
                return;
            }
            float value = (float)newValue;
            await Change(s =>
            {
                if (ReferenceEquals(sender, StartBox))
                {
                    s.StartSpeed = value;
                }
                else
                {
                    s.MinSpeed = value;
                }
            });
        }

        private async Task Send(string controlKey, FanSetting setting)
        {
            string? error = await App.Control.SetFanAsync(controlKey, setting);
            FanError.Title = "Couldn't change this fan";
            FanError.Message = error ?? "";
            FanError.IsOpen = error is not null;
        }


        /// <summary>Highest current reading among the curve's source sensors (as the service sees it).</summary>
        private static float? SourceTemperature(List<string> keys)
        {
            float? best = null;
            foreach (var key in keys)
            {
                if (App.Sensors.TryGetIndex(key, out int i) && App.Sensors.GetNow(i) is { } v && (best is null || v > best))
                {
                    best = v;
                }
            }
            return best;
        }
    }
}
