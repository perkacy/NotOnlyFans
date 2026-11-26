using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NotOnlyFans.Controls;
using NotOnlyFans.Pages;
using NotOnlyFans.Services;

namespace NotOnlyFans
{
    /// <summary>
    /// App shell. All pages are created once at startup and kept in memory.
    /// Switching tabs only swaps which page is shown, so it is instant:
    /// no page is rebuilt, nothing is loaded, no transition animation.
    /// (Antipattern we avoid: Windows 11 Settings, which rebuilds each page on click.)
    ///
    /// The sidebar also shows the pinned sensors, always visible whatever page is open.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private readonly Dictionary<string, Page> _pages;
        private string _current = "home";
        private bool _active = true;

        // Pinned sidebar rows: sensor key → value text, updated every second.
        private readonly List<(string Key, TextBlock Value)> _pinValues = [];

        // Fan icons of pinned fans (their RPM or speed % rows): each turns with its fan's RPM.
        private readonly List<(FanSpin Spin, string RpmKey)> _pinSpins = [];

        // Pinned edit mode (Edit next to "Pinned"): arrows and unpin on each row.
        private const string PinTag = "pin:";
        private bool _editingPins;
        private readonly List<NavigationViewItem> _pinItems = [];
        private RowReorder? _pinReorder;

        /// <summary>The shell, so pages can switch tabs (e.g. a Home tile opens Sensors).</summary>
        public static MainPage? Current { get; private set; }

        public MainPage()
        {
            InitializeComponent();
            Current = this;

            _pages = new Dictionary<string, Page>
            {
                ["home"] = new HomePage(),
                ["cooling"] = new CoolingPage(),
                ["sensors"] = new SensorsPage(),
                ["colors"] = new ColorsPage(),
                ["oc"] = new OcPage(),
                ["settings"] = new SettingsPage(),
            };

            // No extra margin under Settings: the pane does not count it in, so it cut the top off Settings' frame.

            NavView.SelectedItem = NavView.MenuItems[0];
            Show("home");

            AddHandler(PointerPressedEvent, new PointerEventHandler(Page_PointerPressed), true);
            foreach (var (key, modifiers, back) in new[]
            {
                (Windows.System.VirtualKey.Left, Windows.System.VirtualKeyModifiers.Menu, true),
                (Windows.System.VirtualKey.Right, Windows.System.VirtualKeyModifiers.Menu, false),
                (Windows.System.VirtualKey.GoBack, Windows.System.VirtualKeyModifiers.None, true),
                (Windows.System.VirtualKey.GoForward, Windows.System.VirtualKeyModifiers.None, false),
            })
            {
                var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
                accelerator.Invoked += back ? Back_Invoked : Forward_Invoked;
                KeyboardAccelerators.Add(accelerator);
            }
            KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;   // no "Alt+Left" tooltips

            App.Pins.Changed += RebuildPins;
            App.Sensors.CatalogChanged += RebuildPins;
            App.Settings.NamesChanged += () =>
            {
                if (App.Sensors.Catalog is { } catalog)
                {
                    App.Pins.UpdateLabels(key => App.Sensors.TryGetIndex(key, out int i) ? SensorLayout.PinLabel(catalog, i) : null);
                }
                RebuildPins();
            };
            App.Sensors.ValuesChanged += UpdatePinValues;
            Theme.Follow(this, RebuildPins);   // rows are made in code: new colors
            RebuildPins();
        }

        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            string? tag = args.IsSettingsSelected
                ? "settings"
                : (args.SelectedItemContainer?.Tag as string);

            if (tag is not null)
            {
                Show(tag);
            }
        }

        /// <summary>Selects a tab in the left pane, exactly as if the user clicked it.</summary>
        public void Navigate(string tag)
        {
            if (tag == "settings")
            {
                NavView.SelectedItem = NavView.SettingsItem;
                return;
            }
            var item = NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag as string == tag);
            if (item is not null)
            {
                NavView.SelectedItem = item;
            }
        }

        /// <summary>Opens Cooling › fan (by the key of the fan's RPM sensor), e.g. from a fan on Home.</summary>
        public void OpenFan(string fanKey)
        {
            _quiet++;
            Navigate("cooling");
            Cooling.ShowFan(fanKey);
            _quiet--;
            Visited();
        }

        private CoolingPage Cooling => (CoolingPage)_pages["cooling"];

        private void Show(string tag)
        {
            _current = tag;
            // Clicking Cooling in the left pane always starts at the list of fans.
            if (tag == "cooling")
            {
                Cooling.ShowList();
            }

            if (_active && _pages.TryGetValue(tag, out var page) && !ReferenceEquals(ContentHost.Content, page))
            {
                ContentHost.Content = page;
            }
            Visited();
        }

        // ---------- Back and forward (mouse side buttons, Alt+Left / Alt+Right, the back arrow) ----------
        // Like a browser or Windows Settings: every page the user opens is remembered. A place is a tab, and in
        // Cooling also which fan is open, so Back from a fan's page returns to wherever the user came from.

        private readonly record struct Place(string Tab, string? Fan);

        private const int HistoryLimit = 50;
        private readonly List<Place> _back = [];
        private readonly List<Place> _forward = [];
        private Place _place = new("home", null);
        private int _quiet;   // > 0 while one move is made of several steps (only where it ends counts)

        /// <summary>
        /// Called after anything that may have changed the page on screen. <paramref name="replace"/>: the new place
        /// takes the old one's spot in the history (switching between fans on a fan's page: Back leaves Cooling's fan
        /// pages instead of stepping through every fan looked at).
        /// </summary>
        public void Visited(bool replace = false)
        {
            if (_quiet > 0)
            {
                return;
            }
            var now = new Place(_current, _current == "cooling" ? Cooling.CurrentFanKey : null);
            if (now == _place)
            {
                return;
            }
            if (!replace || _place.Fan is null || now.Fan is null)
            {
                _back.Add(_place);
                if (_back.Count > HistoryLimit)
                {
                    _back.RemoveAt(0);
                }
            }
            _forward.Clear();
            _place = now;
            NavView.IsBackEnabled = _back.Count > 0;
        }

        public void GoBack() => Travel(_back, _forward);

        public void GoForward() => Travel(_forward, _back);

        private void Travel(List<Place> from, List<Place> to)
        {
            if (from.Count == 0)
            {
                return;
            }
            var place = from[^1];
            from.RemoveAt(from.Count - 1);
            to.Add(_place);

            _quiet++;
            Navigate(place.Tab);
            if (place.Tab == "cooling")
            {
                if (place.Fan is null)
                {
                    Cooling.ShowList();
                }
                else
                {
                    Cooling.ShowFan(place.Fan);
                }
            }
            _quiet--;
            _place = new Place(_current, _current == "cooling" ? Cooling.CurrentFanKey : null);
            NavView.IsBackEnabled = _back.Count > 0;
        }

        private void NavView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args) => GoBack();

        /// <summary>The mouse's side buttons, anywhere in the window (also over buttons, which keep clicks to themselves).</summary>
        private void Page_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(this).Properties;
            if (point.IsXButton1Pressed)
            {
                GoBack();
                e.Handled = true;
            }
            else if (point.IsXButton2Pressed)
            {
                GoForward();
                e.Handled = true;
            }
        }

        private void Back_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            GoBack();
            args.Handled = true;
        }

        private void Forward_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            GoForward();
            args.Handled = true;
        }

        /// <summary>
        /// Window in the tray: the page leaves the screen (it stops updating, but stays in memory);
        /// back on screen: the same page returns at once and refreshes from the latest data.
        /// </summary>
        public void SetActive(bool active)
        {
            if (_active == active)
            {
                return;
            }
            _active = active;
            if (active)
            {
                ContentHost.Content = _pages[_current];   // as it was (not Show: that would reset Cooling to its list)
                UpdatePinValues();
            }
            else
            {
                ContentHost.Content = null;
                foreach (var (spin, _) in _pinSpins)
                {
                    spin.Stop();   // nothing turns while the window is in the tray
                }
            }
        }

        /// <summary>
        /// A narrow window: the pane shrinks to icons (as in Windows Settings) so the page keeps its room; the menu
        /// button opens it over the page, Pinned included. Pinned rows have no icon, so they show only in the open pane.
        /// </summary>
        private void NavView_DisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
        {
            NavView.IsPaneToggleButtonVisible = args.DisplayMode != NavigationViewDisplayMode.Expanded;
            ShowPinned();
        }

        private void NavView_PaneChanged(NavigationView sender, object args) => ShowPinned();

        private void ShowPinned()
        {
            bool show = NavView.DisplayMode == NavigationViewDisplayMode.Expanded || NavView.IsPaneOpen;
            var items = NavView.MenuItems;
            for (int i = Math.Max(0, items.IndexOf(PinnedHeader) - 1); i < items.Count; i++)
            {
                if (items[i] is UIElement element)
                {
                    element.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }

        /// <summary>Recreates the pinned rows under the "Pinned" header (only when pins change).</summary>
        private void RebuildPins()
        {
            var items = NavView.MenuItems;
            int first = items.IndexOf(PinnedHeader) + 1;
            while (items.Count > first)
            {
                items.RemoveAt(items.Count - 1);
            }
            _pinValues.Clear();
            _pinSpins.Clear();
            _pinItems.Clear();

            var pins = App.Pins.Pins;
            if (pins.Count == 0)
            {
                _editingPins = false;
            }
            EditPinsButton.Visibility = pins.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            EditPinsButton.Content = _editingPins
                ? new TextBlock { Text = "Done" }
                : new FontIcon { Glyph = "", FontSize = 14 };
            AutomationProperties.SetName(EditPinsButton, _editingPins ? "Done editing pinned" : "Edit pinned");
            ToolTipService.SetToolTip(EditPinsButton, _editingPins ? null : "Change order or unpin");

            if (pins.Count == 0)
            {
                items.Add(new NavigationViewItem
                {
                    SelectsOnInvoked = false,
                    IsEnabled = false,
                    Content = new TextBlock
                    {
                        Text = "Pin sensors on the Sensors page",
                        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                        TextWrapping = TextWrapping.Wrap,
                    },
                });
                ShowPinned();
                return;
            }

            for (int p = 0; p < pins.Count; p++)
            {
                var pin = pins[p];
                // Current name when the service has answered (follows renames), else the label saved with the pin.
                string text = App.Sensors.Catalog is { } catalog && App.Sensors.TryGetIndex(pin.Key, out int index)
                    ? SensorLayout.PinLabel(catalog, index)
                    : pin.Label;
                var label = new TextBlock
                {
                    Text = text,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Theme.Brush(this, "TextFillColorSecondaryBrush"),
                };

                var grid = new DragRow { ColumnSpacing = 8 };
                var item = new NavigationViewItem
                {
                    SelectsOnInvoked = false,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Content = grid,
                    Tag = PinTag + pin.Key,
                    // Right-click: move up / down and unpin, in both modes (also the keyboard way to reorder).
                    ContextFlyout = PinMenu(pin.Key, p, pins.Count),
                };
                AutomationProperties.SetName(item, text);

                if (_editingPins)
                {
                    // Edit mode: ≡ shows the row can be dragged up or down; unpin on the right. No values meanwhile.
                    string key = pin.Key;
                    var grip = new FontIcon
                    {
                        Glyph = "",
                        FontSize = 14,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = Theme.Brush(this, "TextFillColorSecondaryBrush"),
                    };
                    AddColumn(grid, grip);
                    AddColumn(grid, label, star: true);
                    AddColumn(grid, PinButton("", $"Unpin {text}", "Unpin", () => App.Pins.Unpin(key)));
                    grid.Cursor = RowReorder.HandCursor;   // the hand that will carry the row, not a resize arrow
                    ToolTipService.SetToolTip(grip, "Drag to move");

                    item.AddHandler(PointerPressedEvent, new PointerEventHandler(Pin_PointerPressed), true);
                    item.AddHandler(PointerMovedEvent, new PointerEventHandler((s, a) => PinReorder.PointerMoved(s, a)), true);
                    item.AddHandler(PointerReleasedEvent, new PointerEventHandler((s, a) => PinReorder.Released(s, a)), true);
                    item.PointerCaptureLost += (s, a) => PinReorder.CaptureLost(s, a);
                    _pinItems.Add(item);
                }
                else
                {
                    var value = new TextBlock
                    {
                        Text = SensorFormat.NoValue,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    // What the reading belongs to: processor, graphics card, drive... or a fan, turning with its speed.
                    // The label then starts where the tab names above it start.
                    if (App.Sensors.Catalog is { } cat && App.Sensors.TryGetIndex(pin.Key, out int sensor))
                    {
                        var icon = PinIcon.Create(this, cat, sensor, out var spin);
                        icon.Margin = new Thickness(0, 0, 8, 0);
                        AddColumn(grid, icon);
                        if (spin is not null)
                        {
                            var s = cat.Sensors[sensor];
                            int rpm = s.Kind == "Fan" ? sensor : SensorLayout.FanFor(cat, sensor);
                            _pinSpins.Add((spin, cat.Sensors[rpm].Key));
                        }
                    }
                    AddColumn(grid, label, star: true);
                    AddColumn(grid, value);
                    _pinValues.Add((pin.Key, value));
                }
                items.Add(item);
            }

            ShowPinned();
            UpdatePinValues();
        }

        private static void AddColumn(Grid grid, FrameworkElement element, bool star = false)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = star ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Grid.SetColumn(element, grid.ColumnDefinitions.Count - 1);
            grid.Children.Add(element);
        }

        private static Button PinButton(string glyph, string name, string tip, Action click)
        {
            var button = new Button
            {
                Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Content = new FontIcon { Glyph = glyph, FontSize = 12 },
            };
            AutomationProperties.SetName(button, name);
            ToolTipService.SetToolTip(button, tip);
            button.Click += (_, _) => click();
            return button;
        }

        // ----- Dragging a pinned row (edit mode) -----
        // RowReorder, the same feel as the fans on Home and in Cooling: the row follows the pointer,
        // the rows it passes step aside, the order is saved once, on release.

        private RowReorder PinReorder => _pinReorder ??= CreatePinReorder();

        private RowReorder CreatePinReorder()
        {
            var reorder = new RowReorder(NavView, () => _pinItems);
            reorder.Lifted += (item, lifted) =>
            {
                ProtectedCursor = lifted ? RowReorder.HandCursor : null;
                if (!lifted && (item as ContentControl)?.Content is DragRow row)
                {
                    row.Cursor = RowReorder.HandCursor;   // still in edit mode: the hand stays on the rows
                }
            };
            reorder.Moved += (from, to) =>
            {
                if (from < _pinItems.Count && _pinItems[from].Tag is string tag && tag.StartsWith(PinTag, StringComparison.Ordinal))
                {
                    App.Pins.MoveTo(tag[PinTag.Length..], to);   // rebuilds the rows in the new order
                }
            };
            return reorder;
        }

        private void Pin_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            // The unpin button does its own thing.
            if (_editingPins && sender is NavigationViewItem item && !IsInButton(e.OriginalSource as DependencyObject, item))
            {
                PinReorder.Pressed(sender, e);
            }
        }

        private static bool IsInButton(DependencyObject? element, DependencyObject stop)
        {
            for (var e = element; e is not null && !ReferenceEquals(e, stop); e = VisualTreeHelper.GetParent(e))
            {
                if (e is Button)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Right-click on a pinned row: the same actions as the edit mode, without entering it.</summary>
        private static MenuFlyout PinMenu(string key, int position, int count)
        {
            var menu = new MenuFlyout();
            menu.Items.Add(MenuItem("Move up", "", position > 0, () => App.Pins.Move(key, -1)));
            menu.Items.Add(MenuItem("Move down", "", position < count - 1, () => App.Pins.Move(key, +1)));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(MenuItem("Unpin", "", true, () => App.Pins.Unpin(key)));
            return menu;
        }

        private static MenuFlyoutItem MenuItem(string text, string glyph, bool enabled, Action click)
        {
            var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled, Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) => click();
            return item;
        }

        private void EditPins_Click(object sender, RoutedEventArgs e)
        {
            _editingPins = !_editingPins;
            RebuildPins();
            EditPinsButton.Focus(FocusState.Programmatic);
        }

        /// <summary>A pinned row clicked (not in edit mode): Sensors, scrolled to that sensor.</summary>
        private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (!_editingPins && args.InvokedItemContainer?.Tag is string tag && tag.StartsWith(PinTag, StringComparison.Ordinal))
            {
                OpenSensor(tag[PinTag.Length..]);
            }
        }

        /// <summary>Opens Sensors at the card of one device, e.g. from a Home tile (group id from <see cref="SensorLayout"/>).</summary>
        public void OpenSensorGroup(string groupId, string? chartKey = null)
        {
            Navigate("sensors");
            (_pages["sensors"] as SensorsPage)?.ShowGroup(groupId, chartKey);
        }

        /// <summary>Opens Sensors at one sensor (its card opened, the row lit for a moment).</summary>
        public void OpenSensor(string key)
        {
            Navigate("sensors");
            (_pages["sensors"] as SensorsPage)?.ShowSensor(key);
        }

        private void UpdatePinValues()
        {
            if (!_active)
            {
                return;
            }
            foreach (var (spin, rpmKey) in _pinSpins)
            {
                spin.SetRpm(App.Sensors.TryGetIndex(rpmKey, out int r) ? App.Sensors.GetNow(r) : null);
            }
            foreach (var (key, valueText) in _pinValues)
            {
                string text = SensorFormat.NoValue;
                if (App.Sensors.TryGetIndex(key, out int i) && App.Sensors.Catalog is { } catalog)
                {
                    text = SensorFormat.Format(catalog.Sensors[i].Kind, App.Sensors.GetNow(i));
                }
                if (valueText.Text != text)
                {
                    valueText.Text = text;
                }
            }
        }
    }
}
