using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using NotOnlyFans.Controls;
using NotOnlyFans.Services;
using NotOnlyFans.ViewModels;

namespace NotOnlyFans.Pages
{
    /// <summary>
    /// Every sensor the service reports, grouped by device, with Now / Min / Max and a pin.
    /// Right-click a row to rename or hide it; search filters all groups at once.
    ///
    /// Row view-models are made once per catalog and keep their live values; the groups on screen
    /// are rebuilt from them when the search, hidden sensors or "Show hidden" change.
    /// Each second only changed texts are updated, and only while the page is on screen.
    /// </summary>
    public sealed partial class SensorsPage : Page
    {
        private IReadOnlyList<SensorGroupInfo> _layout = [];
        private readonly Dictionary<int, SensorRowViewModel> _rows = [];
        private readonly HashSet<int> _essential = [];
        private readonly HashSet<string> _closedGroups = [];
        private readonly HashSet<string> _showAllGroups = [];
        private string[] _keys = [];
        private string _query = "";
        private bool _showHidden;
        private bool _isShown;
        private FrameworkElement? _contextRow;
        private List<SensorGroupViewModel> _groups = [];
        private (string GroupId, SensorRowViewModel? Row)? _reveal;
        private Storyboard? _flash;

        public SensorsPage()
        {
            InitializeComponent();

            App.Sensors.CatalogChanged += OnCatalogChanged;
            App.Sensors.ValuesChanged += OnValuesChanged;
            App.Sensors.ConnectionChanged += UpdateServiceInfo;
            App.Pins.Changed += UpdatePins;
            App.Settings.NamesChanged += UpdateNames;
            App.Settings.HiddenChanged += OnHiddenChanged;

            Loaded += (_, _) =>
            {
                _isShown = true;
                OnValuesChanged();
                RevealNow();   // opened from a Home tile or a pinned sensor
            };
            Unloaded += (_, _) => _isShown = false;
            Theme.Follow(this);   // InfoBars after a light/dark change

            OnCatalogChanged();
            UpdateServiceInfo();
        }

        // ---------- Building ----------

        private void OnCatalogChanged()
        {
            var catalog = App.Sensors.Catalog;
            if (catalog is null)
            {
                return;
            }

            // Same sensors as before (e.g. the service restarted): keep everything as it is.
            var keys = catalog.Sensors.Select(s => s.Key).ToArray();
            if (keys.AsSpan().SequenceEqual(_keys))
            {
                return;
            }
            _keys = keys;

            _layout = SensorLayout.BuildGroups(catalog);
            _rows.Clear();
            _essential.Clear();
            foreach (var g in _layout)
            {
                for (int k = 0; k < g.SensorIndexes.Count; k++)
                {
                    int i = g.SensorIndexes[k];
                    var s = catalog.Sensors[i];

                    // A fan's speed (%) row is renamed together with its fan.
                    int fan = s.Kind == "Control" ? SensorLayout.FanFor(catalog, i) : -1;
                    string nameKey = fan >= 0 ? catalog.Sensors[fan].Key : s.Key;
                    string original = fan >= 0 ? catalog.Sensors[fan].Name : s.Name;

                    _rows[i] = new SensorRowViewModel(i, s.Key, s.Kind, SensorLayout.RowName(catalog, g, i), SensorLayout.PinLabel(catalog, i))
                    {
                        NameKey = nameKey,
                        OriginalName = original,
                        IsPinned = App.Pins.IsPinned(s.Key),
                        IsHidden = App.Settings.IsHidden(s.Key),
                        IsRenamed = App.Settings.GetName(nameKey) is not null,
                    };
                    if (k < g.EssentialCount)
                    {
                        _essential.Add(i);
                    }
                }
            }

            BuildView();
            OnValuesChanged();
        }

        /// <summary>The cards on screen, from the current search and hidden sensors.</summary>
        private void BuildView()
        {
            bool searching = _query.Length > 0;
            var groups = new List<SensorGroupViewModel>();
            int hidden = 0;

            foreach (var g in _layout)
            {
                var visible = new List<SensorRowViewModel>();
                foreach (int i in g.SensorIndexes)
                {
                    var row = _rows[i];
                    if (row.IsHidden)
                    {
                        hidden++;
                        if (!_showHidden)
                        {
                            continue;
                        }
                    }
                    if (searching && !Matches(row, g))
                    {
                        continue;
                    }
                    visible.Add(row);
                }
                if (visible.Count == 0)
                {
                    continue;
                }

                // While searching every match is shown, no "Show all" step.
                var main = searching ? visible : visible.Where(r => _essential.Contains(r.SensorIndex)).ToList();
                var more = searching ? [] : visible.Where(r => !_essential.Contains(r.SensorIndex)).ToList();

                var vm = new SensorGroupViewModel(g.Id, g.Title, g.Subtitle, main, more)
                {
                    IsOpen = searching || !_closedGroups.Contains(g.Id),
                    IsShowingAll = _showAllGroups.Contains(g.Id),
                };
                vm.PropertyChanged += OnGroupChanged;
                groups.Add(vm);
            }

            _groups = groups;
            GroupsList.ItemsSource = groups;
            NoMatches.Visibility = searching && groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (hidden == 0)
            {
                _showHidden = false;
            }
            HiddenCount.Text = hidden switch
            {
                0 => "",
                1 => "1 hidden sensor ·",
                _ => $"{hidden} hidden sensors ·",
            };
            ShowHiddenLink.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
            ShowHiddenLink.Content = _showHidden ? "Hide them again" : "Show hidden";
        }

        /// <summary>A row matches when the search text is in its name or in its card's title or device name.</summary>
        private bool Matches(SensorRowViewModel row, SensorGroupInfo group) =>
            row.Name.Contains(_query, StringComparison.CurrentCultureIgnoreCase)
            || group.Title.Contains(_query, StringComparison.CurrentCultureIgnoreCase)
            || group.Subtitle.Contains(_query, StringComparison.CurrentCultureIgnoreCase);

        /// <summary>Remembers which cards the user closed or expanded, across searches and rebuilds.</summary>
        private void OnGroupChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not SensorGroupViewModel g)
            {
                return;
            }
            if (e.PropertyName == nameof(SensorGroupViewModel.IsOpen) && _query.Length == 0)
            {
                if (g.IsOpen)
                {
                    _closedGroups.Remove(g.Id);
                }
                else
                {
                    _closedGroups.Add(g.Id);
                }
            }
            else if (e.PropertyName == nameof(SensorGroupViewModel.IsShowingAll))
            {
                if (g.IsShowingAll)
                {
                    _showAllGroups.Add(g.Id);
                }
                else
                {
                    _showAllGroups.Remove(g.Id);
                }
            }
        }

        // ---------- Live values ----------

        private void OnValuesChanged()
        {
            if (!_isShown)
            {
                return;
            }

            var values = App.Sensors.Values;
            foreach (var row in _rows.Values)
            {
                int i = row.SensorIndex;
                if (values is null || i >= values.Now.Length)
                {
                    row.Update(null, null, null);
                }
                else
                {
                    row.Update(values.Now[i], values.Min[i], values.Max[i]);
                }
            }
        }

        // ---------- Names, hiding, pins ----------

        /// <summary>After a rename: new texts in place, the list keeps its open groups and scroll position.</summary>
        private void UpdateNames()
        {
            if (App.Sensors.Catalog is not { } catalog)
            {
                return;
            }
            foreach (var g in _layout)
            {
                foreach (int i in g.SensorIndexes)
                {
                    var row = _rows[i];
                    row.Name = SensorLayout.RowName(catalog, g, i);
                    row.PinLabel = SensorLayout.PinLabel(catalog, i);
                    row.IsRenamed = App.Settings.GetName(row.NameKey) is not null;
                }
            }
            if (_query.Length > 0)
            {
                BuildView();
            }
        }

        private void OnHiddenChanged()
        {
            foreach (var row in _rows.Values)
            {
                row.IsHidden = App.Settings.IsHidden(row.Key);
            }
            BuildView();
        }

        private void UpdatePins()
        {
            foreach (var row in _rows.Values)
            {
                row.IsPinned = App.Pins.IsPinned(row.Key);
            }
        }

        private void Row_ContextRequested(UIElement sender, ContextRequestedEventArgs args) =>
            _contextRow = sender as FrameworkElement;

        // ---------- A row's 5-minute chart ----------

        /// <summary>A click on a row opens its chart, another closes it (the pin button and the chart itself do their own thing).</summary>
        private void Row_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: SensorRowViewModel row } element)
            {
                return;
            }
            for (var d = e.OriginalSource as DependencyObject; d is not null && !ReferenceEquals(d, element); d = VisualTreeHelper.GetParent(d))
            {
                if (d is Button or HistoryChart)
                {
                    return;
                }
            }
            row.IsChartOpen = !row.IsChartOpen;
            e.Handled = true;
        }

        // Rows that open a chart light up under the pointer, like a list in Settings.
        private void Row_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Panel row && _flash?.GetCurrentState() != ClockState.Active)
            {
                row.Background = Theme.Brush(this, "SubtleFillColorSecondaryBrush");
            }
        }

        private void Row_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Panel row && _flash?.GetCurrentState() != ClockState.Active)
            {
                row.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
        }

        private void Rename_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not SensorRowViewModel row || _contextRow is null)
            {
                return;
            }
            string current = App.Settings.GetName(row.NameKey) ?? row.OriginalName;
            RenamePrompt.Show(_contextRow, "Sensor name", current, row.OriginalName,
                name => App.Settings.SetName(row.NameKey, name, row.OriginalName));
        }

        private void ResetName_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is SensorRowViewModel row)
            {
                App.Settings.SetName(row.NameKey, null, row.OriginalName);
            }
        }

        private void ToggleHidden_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is SensorRowViewModel row)
            {
                App.Settings.SetHidden(row.Key, !row.IsHidden);
            }
        }

        private void ShowHidden_Click(object sender, RoutedEventArgs e)
        {
            _showHidden = !_showHidden;
            BuildView();
        }

        private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            string query = sender.Text.Trim();
            if (query == _query)
            {
                return;   // e.g. the box cleared by opening a card from elsewhere: already shown
            }
            _query = query;
            BuildView();
        }

        // ---------- Opening a card from elsewhere (Home tiles, pinned sensors) ----------

        /// <summary>A Home tile: the card of that device (e.g. "drive:…"), opened, at the top of the page.</summary>
        public void ShowGroup(string groupId, string? chartKey = null)
        {
            // From a Home tile with a chart: the same reading's chart, open in the card.
            if (chartKey is not null && _rows.Values.FirstOrDefault(r => r.Key == chartKey) is { } row)
            {
                row.IsChartOpen = true;
            }
            Reveal(groupId, null);
        }

        /// <summary>A pinned sensor: its card opened (with "Show all" if needed), scrolled to it, the row lit for a moment.</summary>
        public void ShowSensor(string key)
        {
            var row = _rows.Values.FirstOrDefault(r => r.Key == key);
            var group = row is null ? null : _layout.FirstOrDefault(g => g.SensorIndexes.Contains(row.SensorIndex));
            if (group is not null)
            {
                Reveal(group.Id, row);
            }
        }

        private void Reveal(string groupId, SensorRowViewModel? row)
        {
            bool rebuild = false;
            if (_query.Length > 0)
            {
                _query = "";
                SearchBox.Text = "";
                rebuild = true;
            }
            if (row is { IsHidden: true } && !_showHidden)
            {
                _showHidden = true;
                rebuild = true;
            }
            bool more = row is not null && !_essential.Contains(row.SensorIndex);
            _closedGroups.Remove(groupId);
            if (more)
            {
                _showAllGroups.Add(groupId);
            }
            if (rebuild)
            {
                BuildView();
            }
            if (_groups.FirstOrDefault(g => g.Id == groupId) is { } group)
            {
                group.IsOpen = true;
                group.IsShowingAll |= more;
            }

            _reveal = (groupId, row);
            if (_isShown)
            {
                RevealNow();
            }
        }

        /// <summary>Scrolls to the pending card or row, at once (no scrolling animation: the page just opens there).</summary>
        private void RevealNow()
        {
            if (_reveal is not { } target)
            {
                return;
            }
            _reveal = null;
            int index = _groups.FindIndex(g => g.Id == target.GroupId);
            if (index < 0 || Scroller.Content is not UIElement content)
            {
                return;
            }
            UpdateLayout();   // the card may have just been opened (or "Show all"): measure its rows now
            if (GroupsList.ContainerFromIndex(index) is not FrameworkElement card)
            {
                return;
            }
            double cardTop = card.TransformToVisual(content).TransformPoint(default).Y;
            double y = index == 0 ? 0 : cardTop - 12;

            if (target.Row is { } row && RowElement(card, row) is { } rowElement)
            {
                double rowTop = rowElement.TransformToVisual(content).TransformPoint(default).Y;
                // Card header and row together if they fit on screen, else the row in the middle.
                if (rowTop + rowElement.ActualHeight > y + Scroller.ViewportHeight)
                {
                    y = rowTop - (Scroller.ViewportHeight - rowElement.ActualHeight) / 2;
                }
                Flash(rowElement);
            }
            Scroller.ChangeView(null, Math.Max(0, y), null, true);
        }

        /// <summary>The row's element in the card (each row template is a Grid tagged with its row).</summary>
        private static Panel? RowElement(FrameworkElement card, SensorRowViewModel row) =>
            Descendants(card).OfType<Grid>().FirstOrDefault(g => ReferenceEquals(g.Tag, row));

        private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (int k = 0; k < VisualTreeHelper.GetChildrenCount(parent); k++)
            {
                var child = VisualTreeHelper.GetChild(parent, k);
                yield return child;
                foreach (var d in Descendants(child))
                {
                    yield return d;
                }
            }
        }

        /// <summary>Lights the row in the accent color for a moment, so the eye finds it; fades out (or just goes, with animations off).</summary>
        private void Flash(Panel row)
        {
            _flash?.Stop();
            var accent = Theme.Brush(this, "AccentFillColorDefaultBrush") is SolidColorBrush a ? a.Color : Microsoft.UI.Colors.DodgerBlue;
            var light = new SolidColorBrush(accent) { Opacity = 0.16 };
            row.Background = light;

            bool animate = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
            var fade = new DoubleAnimation
            {
                To = 0,
                BeginTime = TimeSpan.FromSeconds(1.2),
                Duration = TimeSpan.FromSeconds(animate ? 0.4 : 0),
                EnableDependentAnimation = true,
            };
            Storyboard.SetTarget(fade, light);
            Storyboard.SetTargetProperty(fade, "Opacity");
            var story = new Storyboard();
            story.Children.Add(fade);
            story.Completed += (_, _) =>
            {
                if (ReferenceEquals(row.Background, light))
                {
                    row.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);   // as in the template: keeps catching right-clicks
                }
            };
            _flash = story;
            story.Begin();
        }

        // ---------- Group cards ----------

        // The group comes through Tag="{x:Bind}": inside this item template the button's
        // DataContext is null, so {x:Bind Toggle} / DataContext-based handlers never reached the group.
        private void GroupHeader_Click(object sender, RoutedEventArgs e) =>
            ((sender as FrameworkElement)?.Tag as SensorGroupViewModel)?.Toggle();

        private void ShowAll_Click(object sender, RoutedEventArgs e) =>
            ((sender as FrameworkElement)?.Tag as SensorGroupViewModel)?.ToggleShowAll();

        private void UpdateServiceInfo() => ServiceInfo.IsOpen = App.Sensors.IsServiceMissing;
    }
}
