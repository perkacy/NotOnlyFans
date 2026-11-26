using System.Globalization;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using NotOnlyFans.Contracts;
using NotOnlyFans.Controls;
using NotOnlyFans.Services;
using NotOnlyFans.ViewModels;

namespace NotOnlyFans.Pages
{
    /// <summary>
    /// Home: everything at once. Tiles are filled from whatever the service reports
    /// (first CPU, the dedicated GPU if there is one, the motherboard's sensor chip, every drive, every fan).
    /// Like the Sensors page it only updates while it is on screen, and catches up instantly when shown;
    /// the 5-minute charts come from <see cref="SensorHistory"/>, which records all the time.
    /// </summary>
    public sealed partial class HomePage : Page
    {
        private const string NoValue = SensorFormat.NoValue;

        private sealed record FanSource(FanRowViewModel Row, int Rpm, int Control, FanInfo Info);

        /// <summary>A fan shown as a chip on the CPU or GPU tile.</summary>
        private sealed record FanChip(ValueChipViewModel Chip, string Key, string DefaultLabel, int Rpm, int Control);

        private int _cpuTemp = -1, _cpuLoad = -1, _cpuPower = -1;
        private int[] _cpuClocks = [];
        private readonly List<FanChip> _cpuFans = [];
        private string _shownCpuFans = "";
        private int _gpuTemp = -1, _gpuHot = -1, _gpuLoad = -1, _gpuPower = -1;
        private readonly List<FanChip> _gpuFans = [];
        private readonly List<(ValueChipViewModel Chip, int Index)> _boardTemps = [];
        private readonly List<(DriveViewModel Drive, int Temp, int Life)> _drives = [];

        // All fans in detected order; the list on screen (_fanRows) is the shown ones in the user's order.
        private readonly List<FanSource> _fans = [];
        private readonly System.Collections.ObjectModel.ObservableCollection<FanRowViewModel> _fanRows = [];
        private string _shownFans = "";

        private readonly float[] _history = new float[SensorHistory.Capacity];
        private string[] _keys = [];
        private bool _isShown;

        public HomePage()
        {
            InitializeComponent();
            Tiles.SizeChanged += (_, e) => PlaceTiles(e.NewSize.Width);

            App.Sensors.CatalogChanged += OnCatalogChanged;
            App.Sensors.ValuesChanged += Refresh;
            App.Sensors.ConnectionChanged += () => ServiceInfo.IsOpen = App.Sensors.IsServiceMissing;
            App.Settings.NamesChanged += ApplyNames;
            App.Sensors.ControlChanged += Refresh;
            Fans.ItemsSource = _fanRows;

            Loaded += (_, _) =>
            {
                _isShown = true;
                Refresh();
            };
            Unloaded += (_, _) => _isShown = false;
            // A row moved by drag keeps the divider brush set from code: give every row the new theme's one.
            Theme.Follow(this, () =>
            {
                for (int k = 0; k < VisualTreeHelper.GetChildrenCount(Fans); k++)
                {
                    if (VisualTreeHelper.GetChild(Fans, k) is DragRow row && row != _dragRow)
                    {
                        row.BorderBrush = Theme.Brush(this, "DividerStrokeColorDefaultBrush");
                    }
                }
            });

            OnCatalogChanged();
        }

        // ---------- Which sensors go where (once per catalog) ----------

        private void OnCatalogChanged()
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

            var cpu = catalog.Hardware.FirstOrDefault(h => h.Kind == "Cpu");
            // The graphics card the curves use (a card with fans before integrated graphics), else any graphics.
            int gpuCore = TemperatureSources.Gpu(catalog);
            var gpu = gpuCore >= 0
                ? catalog.Hardware.FirstOrDefault(h => h.Id == catalog.Sensors[gpuCore].HardwareId)
                : catalog.Hardware.FirstOrDefault(h => h.Kind is "GpuNvidia" or "GpuAmd") ?? catalog.Hardware.FirstOrDefault(h => h.Kind == "GpuIntel");
            var chip = catalog.Hardware.FirstOrDefault(h => h.Kind == "SuperIO");
            var board = catalog.Hardware.FirstOrDefault(h => h.Kind == "Motherboard");
            var drives = catalog.Hardware.Where(h => h.Kind == "Storage").ToList();

            // CPU
            CpuTile.Visibility = cpu is null ? Visibility.Collapsed : Visibility.Visible;
            CpuName.Text = cpu?.Name ?? "";
            CpuTile.Tag = cpu is null ? null : SensorLayout.GroupId(cpu);
            _cpuTemp = Find(catalog, cpu, "Temperature", "CPU Package", "Core (Tctl/Tdie)", "Core Max");
            _cpuLoad = Find(catalog, cpu, "Load", "CPU Total");
            _cpuPower = Find(catalog, cpu, "Power", "CPU Package", "Package");
            _cpuClocks = cpu is null ? [] : Enumerable.Range(0, catalog.Sensors.Count)
                .Where(i => catalog.Sensors[i].HardwareId == cpu.Id && catalog.Sensors[i].Kind == "Clock"
                            && catalog.Sensors[i].Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            // The CPU cooler: motherboard fan headers whose original name says CPU ("CPU Fan", "CPU OPT"...).
            _cpuFans.Clear();
            foreach (var (rpm, control) in FanPairs(catalog, chip))
            {
                if (catalog.Sensors[rpm].Name.Contains("CPU", StringComparison.OrdinalIgnoreCase))
                {
                    _cpuFans.Add(NewChip(catalog, rpm, control, catalog.Sensors[rpm].Name));
                }
            }
            _shownCpuFans = "";

            // GPU
            GpuTile.Visibility = gpu is null ? Visibility.Collapsed : Visibility.Visible;
            GpuName.Text = gpu?.Name ?? "";
            GpuTile.Tag = gpu is null ? null : SensorLayout.GroupId(gpu);
            _gpuTemp = Find(catalog, gpu, "Temperature", "GPU Core");
            _gpuHot = Find(catalog, gpu, "Temperature", "GPU Hot Spot");
            _gpuLoad = Find(catalog, gpu, "Load", "GPU Core", "D3D 3D");
            _gpuPower = Find(catalog, gpu, "Power", "GPU Package", "GPU Power");
            _gpuFans.Clear();
            foreach (var (rpm, control) in FanPairs(catalog, gpu))
            {
                string label = catalog.Sensors[rpm].Name;
                if (label.StartsWith("GPU ", StringComparison.OrdinalIgnoreCase))
                {
                    label = label[4..];
                }
                _gpuFans.Add(NewChip(catalog, rpm, control, label));
            }
            GpuFans.ItemsSource = _gpuFans.Select(f => f.Chip).ToList();

            // Motherboard: every temperature of the board's sensor chip.
            BoardTile.Visibility = chip is null ? Visibility.Collapsed : Visibility.Visible;
            BoardTile.Tag = chip is null ? null : SensorLayout.GroupId(chip);
            BoardName.Text = board is null || chip is null ? chip?.Name ?? "" : $"{board.Name} · {chip.Name}";
            _boardTemps.Clear();
            if (chip is not null)
            {
                for (int i = 0; i < catalog.Sensors.Count; i++)
                {
                    var s = catalog.Sensors[i];
                    if (s.HardwareId == chip.Id && s.Kind == "Temperature")
                    {
                        _boardTemps.Add((new ValueChipViewModel(s.Name), i));
                    }
                }
            }
            BoardTemps.ItemsSource = _boardTemps.Select(t => t.Chip).ToList();

            // Drives
            DrivesTile.Visibility = drives.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            DrivesTile.Tag = drives.Count == 0 ? null : SensorLayout.GroupId(drives[0]);   // the drive cards are next to each other
            DrivesCount.Text = drives.Count == 1 ? "1 drive" : $"{drives.Count} drives";
            _drives.Clear();
            foreach (var d in drives)
            {
                int temp = Find(catalog, d, "Temperature", "Composite Temperature");
                if (temp < 0)
                {
                    temp = Enumerable.Range(0, catalog.Sensors.Count)
                        .FirstOrDefault(i => catalog.Sensors[i].HardwareId == d.Id && catalog.Sensors[i].Kind == "Temperature", -1);
                }
                _drives.Add((new DriveViewModel(d.Name), temp, Find(catalog, d, "Level", "Life")));
            }
            Drives.ItemsSource = _drives.Select(d => d.Drive).ToList();

            // Fans: motherboard headers, then the graphics cards' own fans (same list as Cooling).
            _fans.Clear();
            foreach (var fan in FanCatalog.All(catalog))
            {
                var row = new FanRowViewModel(fan.Key, fan.OriginalName, fan.Source, FanCatalog.ModeText(fan))
                {
                    Name = SensorLayout.DisplayName(catalog, fan.Rpm),
                };
                _fans.Add(new FanSource(row, fan.Rpm, fan.Control, fan));
            }
            _shownFans = "";

            Refresh();
        }

        private static FanChip NewChip(SensorCatalog catalog, int rpm, int control, string defaultLabel)
        {
            string key = catalog.Sensors[rpm].Key;
            var chip = new ValueChipViewModel(App.Settings.GetName(key) ?? defaultLabel);
            return new FanChip(chip, key, defaultLabel, rpm, control);
        }

        /// <summary>After a rename anywhere: new names on the fan list and the CPU/GPU chips.</summary>
        private void ApplyNames()
        {
            if (App.Sensors.Catalog is not { } catalog)
            {
                return;
            }
            foreach (var fan in _fans)
            {
                fan.Row.Name = SensorLayout.DisplayName(catalog, fan.Rpm);
            }
            foreach (var f in _cpuFans.Concat(_gpuFans))
            {
                f.Chip.Label = App.Settings.GetName(f.Key) ?? f.DefaultLabel;
            }
        }

        /// <summary>First sensor of <paramref name="hw"/> of this kind whose name is one of <paramref name="names"/> (in that order of preference).</summary>
        private static int Find(SensorCatalog catalog, HardwareInfo? hw, string kind, params string[] names)
        {
            if (hw is null)
            {
                return -1;
            }
            foreach (var name in names)
            {
                for (int i = 0; i < catalog.Sensors.Count; i++)
                {
                    var s = catalog.Sensors[i];
                    if (s.HardwareId == hw.Id && s.Kind == kind && s.Name == name)
                    {
                        return i;
                    }
                }
            }
            return -1;
        }

        /// <summary>Each fan's RPM sensor with its speed (%) sensor of the same name, if any.</summary>
        private static IEnumerable<(int Rpm, int Control)> FanPairs(SensorCatalog catalog, HardwareInfo? hw)
        {
            if (hw is null)
            {
                yield break;
            }
            for (int i = 0; i < catalog.Sensors.Count; i++)
            {
                var s = catalog.Sensors[i];
                if (s.HardwareId == hw.Id && s.Kind == "Fan")
                {
                    yield return (i, Find(catalog, hw, "Control", s.Name));
                }
            }
        }

        // ---------- Values (every second while shown) ----------

        private void Refresh()
        {
            ServiceInfo.IsOpen = App.Sensors.IsServiceMissing;
            if (!_isShown || App.Sensors.Catalog is not { } catalog)
            {
                return;
            }

            // CPU
            Set(CpuTemp, Number(_cpuTemp));
            Set(CpuLoad, Format(catalog, _cpuLoad));
            Set(CpuPower, Format(catalog, _cpuPower));
            float? clock = _cpuClocks.Select(Now).Where(v => v.HasValue).DefaultIfEmpty(null).Max();
            Set(CpuClock, SensorFormat.Format("Clock", clock));
            var cpuFans = _cpuFans.Where(f => !IsEmptyHeader(f.Rpm, f.Control)).ToList();
            string cpuFansKey = string.Join(',', cpuFans.Select(f => f.Rpm));
            if (cpuFansKey != _shownCpuFans)
            {
                _shownCpuFans = cpuFansKey;
                CpuFans.ItemsSource = cpuFans.Select(f => f.Chip).ToList();
            }
            foreach (var f in cpuFans)
            {
                f.Chip.Value = FanText(f.Rpm, f.Control);
            }

            // GPU
            Set(GpuTemp, Number(_gpuTemp));
            Set(GpuHot, Format(catalog, _gpuHot));
            Set(GpuLoad, Format(catalog, _gpuLoad));
            Set(GpuPower, Format(catalog, _gpuPower));
            foreach (var f in _gpuFans)
            {
                f.Chip.Value = FanText(f.Rpm, f.Control);
            }

            foreach (var (chip, index) in _boardTemps)
            {
                chip.Value = Format(catalog, index);
            }

            foreach (var (drive, temp, life) in _drives)
            {
                drive.Temperature = Format(catalog, temp);
                drive.Detail = Now(life) is { } health ? string.Create(CultureInfo.CurrentCulture, $"Health {health:0}%") : "";
            }

            RefreshFans();
            DrawSparklines();
        }

        private void RefreshFans()
        {
            // Which fans to list (no empty headers), in the user's order.
            var shown = OrderedFans().Where(f => !IsEmptyHeader(f.Rpm, f.Control)).ToList();
            string shownKey = string.Join(',', shown.Select(f => f.Rpm));
            if (shownKey != _shownFans)
            {
                _shownFans = shownKey;
                _fanRows.Clear();
                foreach (var fan in shown)
                {
                    _fanRows.Add(fan.Row);
                }
            }

            int stopped = 0;
            foreach (var fan in shown)
            {
                float? rpm = Now(fan.Rpm);
                float? duty = Now(fan.Control);
                if (rpm is 0)
                {
                    stopped++;
                }
                fan.Row.Rpm = rpm is 0 ? "Stopped" : SensorFormat.Format("Fan", rpm);
                fan.Row.Percent = duty is null ? "" : SensorFormat.Format("Control", duty);
                fan.Row.BarWidth = Math.Clamp(duty ?? 0, 0, 100) / 100 * FanRowViewModel.BarLength;
                fan.Row.Mode = FanCatalog.ModeText(fan.Info);
            }

            string count = shown.Count == 1 ? "1 fan" : $"{shown.Count} fans";
            FansSummary.Text = stopped == 0 ? count : $"{count} · {stopped} stopped";
        }

        private static bool IsEmptyHeader(int rpm, int control) => FanCatalog.IsEmptyHeader(rpm, control);

        /// <summary>All fans: first in the saved order, then any the user has not placed yet, in detected order.</summary>
        private IEnumerable<FanSource> OrderedFans() =>
            FanCatalog.Ordered(_fans.Select(f => f.Info)).Select(info => _fans.First(f => f.Info == info));

        // ---------- Fans list: order and names ----------

        /// <summary>Saves the order on screen. Hidden fans (empty headers) keep their place after the shown ones.</summary>
        private void SaveFanOrder()
        {
            FanCatalog.SaveOrder(_fans.Select(f => f.Info), _fanRows.Select(r => r.Key));
            _shownFans = string.Join(',', _fanRows.Select(r => _fans.First(f => f.Row == r).Rpm));
        }

        // ---------- Dragging a fan to a new place (RowReorder: the same feel as Cooling and Pinned) ----------

        private RowReorder? _reorder;
        private DragRow? _dragRow;

        private RowReorder Reorder => _reorder ??= CreateReorder();

        private RowReorder CreateReorder()
        {
            var reorder = new RowReorder(Fans, () =>
            {
                var rows = new List<FrameworkElement>();
                for (int i = 0; i < _fanRows.Count; i++)
                {
                    if (RowElement(i) is not { } row)
                    {
                        return [];   // not all on screen yet: no drag this time
                    }
                    rows.Add(row);
                }
                return rows;
            });
            reorder.Lifted += (element, lifted) =>
            {
                // Lift the carried row above the others; then back to its look from the XAML template.
                // The background stays Transparent rather than empty, so the whole row keeps catching the pointer.
                var row = (DragRow)element;
                _dragRow = lifted ? row : null;
                ProtectedCursor = lifted ? RowReorder.HandCursor : null;
                row.Background = lifted
                    ? Theme.Brush(this, "SolidBackgroundFillColorQuarternaryBrush")
                    : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                row.BorderBrush = Theme.Brush(this, lifted ? "CardStrokeColorDefaultBrush" : "DividerStrokeColorDefaultBrush");
                row.BorderThickness = lifted ? new Thickness(1) : new Thickness(0, 1, 0, 0);
            };
            reorder.Moved += (from, to) =>
            {
                _fanRows.Move(from, to);
                RebuildFanElements();
                SaveFanOrder();
            };
            // A click without dragging opens the fan's page in Cooling.
            reorder.Clicked += row =>
            {
                if (row.Tag is FanRowViewModel vm)
                {
                    MainPage.Current?.OpenFan(vm.Key);
                }
            };
            return reorder;
        }

        private void FanRow_PointerPressed(object sender, PointerRoutedEventArgs e) => Reorder.Pressed(sender, e);

        private void FanRow_PointerMoved(object sender, PointerRoutedEventArgs e) => Reorder.PointerMoved(sender, e);

        private void FanRow_PointerReleased(object sender, PointerRoutedEventArgs e) => Reorder.Released(sender, e);

        private void FanRow_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => Reorder.CaptureLost(sender, e);

        /// <summary>
        /// The row on screen for list position <paramref name="index"/>, found by the fan it shows.
        /// (ItemsRepeater's own index → element lookup was seen to be off by one after a move.)
        /// </summary>
        private DragRow? RowElement(int index)
        {
            var vm = _fanRows[index];
            int count = VisualTreeHelper.GetChildrenCount(Fans);
            for (int k = 0; k < count; k++)
            {
                if (VisualTreeHelper.GetChild(Fans, k) is DragRow row && ReferenceEquals(row.Tag, vm) && Fans.GetElementIndex(row) >= 0)
                {
                    return row;
                }
            }
            return null;
        }

        /// <summary>After a reorder: fresh row elements, so recycled ones never keep a stale position or look.</summary>
        private void RebuildFanElements()
        {
            Fans.ItemsSource = null;
            Fans.ItemsSource = _fanRows;
        }

        private void MoveFanUp_Click(object sender, RoutedEventArgs e) => MoveFan(sender, -1);

        private void MoveFanDown_Click(object sender, RoutedEventArgs e) => MoveFan(sender, +1);

        private void MoveFan(object sender, int delta)
        {
            if ((sender as FrameworkElement)?.Tag is not FanRowViewModel row)
            {
                return;
            }
            int from = _fanRows.IndexOf(row);
            int to = from + delta;
            if (from < 0 || to < 0 || to >= _fanRows.Count)
            {
                return;
            }
            _fanRows.Move(from, to);
            RebuildFanElements();
            SaveFanOrder();
        }

        private void RenameFan_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not FanRowViewModel row)
            {
                return;
            }
            int index = _fanRows.IndexOf(row);
            if (index < 0 || RowElement(index) is not FrameworkElement container)
            {
                return;
            }
            RenamePrompt.Show(container, "Fan name", row.Name, row.OriginalName,
                name => App.Settings.SetName(row.Key, name, row.OriginalName));
        }

        private void ResetFanName_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is FanRowViewModel row)
            {
                App.Settings.SetName(row.Key, null, row.OriginalName);
            }
        }

        // ---------- Charts ----------

        private void DrawSparklines()
        {
            Draw(CpuSpark, CpuSparkHost, _cpuTemp, CpuSparkCaption);
            Draw(GpuSpark, GpuSparkHost, _gpuTemp, null);
        }

        private void Draw(Polyline line, FrameworkElement host, int sensor, TextBlock? caption)
        {
            if (sensor < 0)
            {
                line.Points.Clear();
                return;
            }
            int n = App.History.CopyTo(sensor, _history);
            float? max = Sparkline.Draw(line, host.ActualWidth, host.ActualHeight, _history.AsSpan(0, n), minRange: 10);
            if (caption is not null)
            {
                Set(caption, max is { } m ? string.Create(CultureInfo.CurrentCulture, $"Last 5 minutes · max {m:0} °C") : "Last 5 minutes");
            }
        }

        private int _tileColumns = 2;

        /// <summary>Two columns of tiles; one when the window is narrow, so a tile's readings never get squeezed.</summary>
        private void PlaceTiles(double width)
        {
            int columns = width < 860 ? 1 : 2;
            if (columns == _tileColumns)
            {
                return;
            }
            _tileColumns = columns;
            Tiles.ColumnDefinitions.Clear();
            Tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (columns == 2)
            {
                Tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
            }
            Tiles.RowDefinitions.Clear();
            var tiles = new FrameworkElement[] { CpuTile, GpuTile, BoardTile, DrivesTile };
            for (int r = 0; r < (tiles.Length + columns - 1) / columns; r++)
            {
                Tiles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            for (int i = 0; i < tiles.Length; i++)
            {
                Grid.SetRow(tiles[i], i / columns);
                Grid.SetColumn(tiles[i], i % columns);
            }
        }

        private void SparkHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_isShown)
            {
                DrawSparklines();
            }
        }

        // ---------- Helpers ----------

        private static float? Now(int index) => index < 0 ? null : App.Sensors.GetNow(index);

        private static float? Max(int index) =>
            index >= 0 && App.Sensors.Values is { } v && index < v.Max.Length ? v.Max[index] : null;

        private static string Format(SensorCatalog catalog, int index) =>
            index < 0 ? NoValue : SensorFormat.Format(catalog.Sensors[index].Kind, Now(index));

        private static string Number(int index) =>
            Now(index) is { } v ? v.ToString("0", CultureInfo.CurrentCulture) : NoValue;

        private static string FanText(int rpm, int control)
        {
            float? r = Now(rpm);
            if (r is null)
            {
                return NoValue;
            }
            if (r == 0)
            {
                return "Stopped";
            }
            string speed = SensorFormat.Format("Fan", r);
            return Now(control) is { } duty ? $"{SensorFormat.Format("Control", duty)} · {speed}" : speed;
        }

        private static void Set(TextBlock block, string text)
        {
            if (block.Text != text)
            {
                block.Text = text;
            }
        }

        private static void Set(Run run, string text)
        {
            if (run.Text != text)
            {
                run.Text = text;
            }
        }

        // Each tile opens Sensors at its own card (Tag = the card id), not at the top.
        private void ToSensors_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string group)
            {
                // The CPU and GPU tiles show a temperature chart: Sensors opens with that sensor's chart.
                int chart = ReferenceEquals(sender, CpuTile) ? _cpuTemp : ReferenceEquals(sender, GpuTile) ? _gpuTemp : -1;
                string? chartKey = chart >= 0 && App.Sensors.Catalog is { } catalog ? catalog.Sensors[chart].Key : null;
                MainPage.Current?.OpenSensorGroup(group, chartKey);
            }
            else
            {
                MainPage.Current?.Navigate("sensors");
            }
        }

        private void ToCooling_Click(object sender, RoutedEventArgs e) => MainPage.Current?.Navigate("cooling");
    }
}
