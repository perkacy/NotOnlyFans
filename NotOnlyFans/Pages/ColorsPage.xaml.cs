using System.Globalization;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NotOnlyFans.Contracts;
using NotOnlyFans.Controls;
using NotOnlyFans.Services;
using Windows.UI;

namespace NotOnlyFans.Pages
{
    /// <summary>
    /// Colors: a card per light the service found, and below it the look of the chosen light
    /// (or of all of them with Sync all). Every change goes to the service at once, so the light changes
    /// while the user drags or picks; the service stores it in the device once it has settled.
    /// </summary>
    public sealed partial class ColorsPage : Page
    {
        /// <summary>After the user changes something, the service's echo of an older state must not pull it back.</summary>
        private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(1.5);

        private static readonly (string Hex, string Name)[] Palette =
        [
            ("#FF3B30", "Red"), ("#FF6A00", "Orange"), ("#FFD60A", "Yellow"), ("#34C759", "Green"),
            ("#00C7BE", "Teal"), ("#0A84FF", "Blue"), ("#BF5AF2", "Purple"), ("#FFFFFF", "White"),
        ];

        private bool _isShown;
        private bool _settingControls = true;
        private string? _selected;
        private string _shownLights = "";
        private readonly Dictionary<string, Card> _cards = [];
        private List<(string Label, List<string> Keys)> _sources = [];

        // What the user just chose, shown until the service has it (by light key, or AllKey for Sync all).
        private const string AllKey = "\u0001all";
        private readonly Dictionary<string, (LightSetting Setting, DateTime Time)> _mine = [];
        private (bool Sync, DateTime Time)? _mySync;

        private sealed class Card
        {
            public required Button Button { get; init; }
            public required Ellipse Preview { get; init; }
            public required TextBlock Name { get; init; }
            public required TextBlock Detail { get; init; }
            public required TextBlock Effect { get; init; }
        }

        public ColorsPage()
        {
            InitializeComponent();
            foreach (var (hex, name) in Palette)
            {
                var swatch = MakeSwatch(hex, name);
                swatch.Click += Swatch_Click;
                Swatches.Children.Add(swatch);
            }
            BuildScaleFlyout();
            LightCards.SizeChanged += (_, _) =>
            {
                PlaceCards();
                FitEffects();
            };
            GradientBar.Background = GradientBrush(new LightSetting());
            CoolBox.ValueChanged += (_, v) => Change(s => s.Cool = (float)v);
            HotBox.ValueChanged += (_, v) => Change(s => s.Hot = (float)v);
            _settingControls = false;

            App.Sensors.ColorsChanged += OnColorsChanged;
            App.Sensors.CatalogChanged += Refresh;
            App.Sensors.ValuesChanged += RefreshTemperature;
            App.Sensors.ConnectionChanged += Refresh;
            App.Settings.NamesChanged += Refresh;

            Loaded += (_, _) =>
            {
                _isShown = true;
                Refresh();
            };
            Unloaded += (_, _) => _isShown = false;
            // Light/dark changed: the cards are made in code, so make them again in the new colors.
            Theme.Follow(this, () =>
            {
                _shownLights = "";
                Refresh();
            });
        }

        // ----- State -----

        private static ColorState? State => App.Sensors.Colors;

        private bool Sync =>
            _mySync is { } mine && (DateTime.UtcNow - mine.Time < Quiet || mine.Sync != State?.Sync) ? mine.Sync : State?.Sync == true;

        /// <summary>The setting a light follows now (null = NotOnlyFans hasn't set it).</summary>
        private LightSetting? Setting(string key)
        {
            if (Sync)
            {
                return Mine(AllKey) ?? State?.All;
            }
            return Mine(key) ?? State?.Settings.GetValueOrDefault(key);
        }

        private LightSetting? Mine(string key) =>
            _mine.TryGetValue(key, out var m) && DateTime.UtcNow - m.Time < Quiet ? m.Setting : null;

        private void OnColorsChanged()
        {
            // Drop my pending choices the service already shows.
            foreach (var (key, mine) in _mine.ToList())
            {
                var theirs = key == AllKey ? State?.All : State?.Settings.GetValueOrDefault(key);
                if (theirs is not null && Same(theirs, mine.Setting))
                {
                    _mine.Remove(key);
                }
            }
            if (_mySync is { } s && s.Sync == State?.Sync)
            {
                _mySync = null;
            }
            Refresh();
        }

        private static bool Same(LightSetting a, LightSetting b) =>
            JsonSerializer.Serialize(a, ContractsJson.Default.LightSetting) == JsonSerializer.Serialize(b, ContractsJson.Default.LightSetting);

        // ----- Showing -----

        private void Refresh()
        {
            if (!_isShown)
            {
                return;
            }
            _settingControls = true;
            try
            {
                var state = State;
                ServiceInfo.IsOpen = App.Sensors.IsServiceMissing;
                var lights = state?.Lights ?? [];
                NoLightsInfo.IsOpen = state is not null && lights.Count == 0;
                RefreshWarnings(state?.Warnings ?? []);

                BuildCards(lights);
                if (_selected is null || lights.All(l => l.Key != _selected))
                {
                    _selected = lights.FirstOrDefault()?.Key;
                }
                foreach (var light in lights)
                {
                    RefreshCard(light);
                }

                SyncSwitch.IsEnabled = App.Sensors.IsConnected && lights.Count > 1;
                SyncSwitch.IsOn = Sync;

                var selected = lights.FirstOrDefault(l => l.Key == _selected);
                Editor.Visibility = selected is null ? Visibility.Collapsed : Visibility.Visible;
                if (selected is not null)
                {
                    RefreshEditor(selected);
                }
            }
            finally
            {
                _settingControls = false;
            }
        }

        private void RefreshWarnings(List<string> warnings)
        {
            if (WarningBars.Children.Count == warnings.Count
                && WarningBars.Children.Cast<InfoBar>().Select(b => b.Message).SequenceEqual(warnings))
            {
                return;
            }
            WarningBars.Children.Clear();
            foreach (var warning in warnings)
            {
                WarningBars.Children.Add(new InfoBar { Severity = InfoBarSeverity.Warning, IsOpen = true, IsClosable = true, Message = warning });
            }
        }

        private void BuildCards(List<LightInfo> lights)
        {
            string shape = string.Join("|", lights.Select(l => l.Key));
            if (shape == _shownLights)
            {
                return;
            }
            _shownLights = shape;
            _cards.Clear();
            LightCards.Children.Clear();
            foreach (var light in lights)
            {
                var card = MakeCard(light.Key);
                LightCards.Children.Add(card.Button);
                _cards[light.Key] = card;
            }
            _cardColumns = 0;
            PlaceCards();
        }

        private int _cardColumns;

        /// <summary>
        /// The effect segments are 80 px each when there is room; in a narrow window they shrink to their text, so all
        /// five fit (the cards row is as wide as the effect card).
        /// </summary>
        private void FitEffects()
        {
            double min = LightCards.ActualWidth is > 0 and < 500 ? 0 : 80;
            foreach (var button in EffectButtons.Children.OfType<Button>())
            {
                if (button.MinWidth != min)
                {
                    button.MinWidth = min;
                }
            }
        }

        /// <summary>Up to four cards in a row; fewer when the window is narrow (each card needs about 200 px).</summary>
        private void PlaceCards()
        {
            int count = LightCards.Children.Count;
            double width = LightCards.ActualWidth > 0 ? LightCards.ActualWidth : 900;
            int columns = Math.Clamp((int)((width + LightCards.ColumnSpacing) / (200 + LightCards.ColumnSpacing)), 1, Math.Clamp(count, 1, 4));
            if (columns == _cardColumns)
            {
                return;
            }
            _cardColumns = columns;
            LightCards.ColumnDefinitions.Clear();
            LightCards.RowDefinitions.Clear();
            for (int c = 0; c < columns; c++)
            {
                LightCards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
            for (int r = 0; r < (count + columns - 1) / columns; r++)
            {
                LightCards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            for (int i = 0; i < count; i++)
            {
                var element = (FrameworkElement)LightCards.Children[i];
                Grid.SetColumn(element, i % columns);
                Grid.SetRow(element, i / columns);
            }
        }

        private Card MakeCard(string key)
        {
            var preview = new Ellipse
            {
                Width = 40,
                Height = 40,
                StrokeThickness = 1,
                Stroke = Theme.Brush(this, "CardStrokeColorDefaultBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var name = new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            var detail = new TextBlock { Style = (Style)Resources["Sub"], TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
            var effect = new TextBlock
            {
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = Theme.Brush(this, "TextFillColorTertiaryBrush"),
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(name);
            text.Children.Add(detail);
            text.Children.Add(effect);
            var content = new Grid { ColumnSpacing = 14 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(text, 1);
            content.Children.Add(preview);
            content.Children.Add(text);

            var button = new Button
            {
                Content = content,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(16),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
            };
            button.Click += (_, _) =>
            {
                _selected = key;
                Refresh();
            };

            var rename = new MenuFlyoutItem { Text = "Rename", Icon = new FontIcon { Glyph = "" } };
            rename.Click += (_, _) => Rename(key, button);
            var menu = new MenuFlyout();
            menu.Items.Add(rename);
            button.ContextFlyout = menu;

            return new Card { Button = button, Preview = preview, Name = name, Detail = detail, Effect = effect };
        }

        private void Rename(string key, FrameworkElement target)
        {
            if (State?.Lights.FirstOrDefault(l => l.Key == key) is not { } light)
            {
                return;
            }
            RenamePrompt.Show(target, "Light name", NameOf(light), light.Name,
                name => App.Settings.SetName(key, name, light.Name));
        }

        private static string NameOf(LightInfo light) => App.Settings.GetName(light.Key) ?? light.Name;

        private void RefreshCard(LightInfo light)
        {
            var card = _cards[light.Key];
            string name = NameOf(light);
            card.Name.Text = name;
            card.Detail.Text = light.Detail;
            ToolTipService.SetToolTip(card.Button, $"{light.Device} · {light.Detail}");
            AutomationProperties.SetName(card.Button, name);

            var setting = Setting(light.Key);
            if (setting is not null)
            {
                card.Effect.Text = EffectLabel(setting, light.Key);
                card.Preview.Fill = PreviewBrush(setting, light.Key);
            }
            else if (light.Found is { } found)
            {
                card.Effect.Text = $"{EffectLabel(found, null)} · as found";
                card.Preview.Fill = PreviewBrush(found, null);
            }
            else
            {
                card.Effect.Text = light.FoundOther is { } other ? $"{other} · as found" : "Not set yet";
                card.Preview.Fill = Theme.Brush(this, "SubtleFillColorSecondaryBrush");
            }

            bool selected = Sync || light.Key == _selected;
            var accent = Theme.Brush(this, "AccentFillColorDefaultBrush");
            card.Button.BorderBrush = selected ? accent : Theme.Brush(this, "CardStrokeColorDefaultBrush");
            card.Button.BorderThickness = new Thickness(selected ? 2 : 1);
            card.Button.Padding = new Thickness(selected ? 15 : 16);
        }

        private string EffectLabel(LightSetting s, string? key) => s.Effect switch
        {
            LightEffect.Temperature => NowTemperature(s) is { } t
                ? string.Create(CultureInfo.CurrentCulture, $"Temperature · {t:0} °C")
                : "Temperature",
            LightEffect.Off => "Off",
            _ => s.Effect.ToString(),
        };

        private static Brush PreviewBrush(LightSetting s, string? key) => s.Effect switch
        {
            LightEffect.Off => new SolidColorBrush(Colors.Transparent),
            LightEffect.Rainbow => RainbowBrush(),
            LightEffect.Temperature => key is not null && App.Sensors.Colors?.Live.GetValueOrDefault(key) is { } live
                ? new SolidColorBrush(ToColor(live))
                : GradientBrush(s),
            _ => new SolidColorBrush(ToColor(s.Color)),
        };

        private void RefreshEditor(LightInfo light)
        {
            bool sync = Sync;
            var setting = Editing(light);
            bool untouched = !sync && Setting(light.Key) is null;

            EditingTitle.Text = sync ? "All lights" : NameOf(light);
            EditingHint.Text = sync
                ? "Every light shows the same. Turn Sync all off to set each light on its own."
                : untouched
                    ? light.Found is not null || light.FoundOther is not null
                        ? "NotOnlyFans hasn't changed this light yet. Pick an effect to take it over."
                        : "NotOnlyFans hasn't changed this light yet and can't read what it shows. Pick an effect to take it over."
                    : "";
            EditingHint.Visibility = EditingHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (Button b in EffectButtons.Children.Cast<Button>())
            {
                Select(b, !untouched && (string)b.Tag == setting.Effect.ToString());
            }
            foreach (Button b in SpeedButtons.Children.Cast<Button>())
            {
                Select(b, (string)b.Tag == setting.Speed.ToString());
            }

            var effect = untouched ? (LightEffect?)null : setting.Effect;
            ColorCard.Visibility = Show(effect is LightEffect.Static or LightEffect.Breathing);
            TemperatureCard.Visibility = Show(effect == LightEffect.Temperature);
            OffCard.Visibility = Show(effect == LightEffect.Off);
            OffText.Text = sync ? "Lighting is off for all lights" : "Lighting is off for this light";
            BrightnessCard.Visibility = Show(effect is not null and not LightEffect.Off);
            SpeedCard.Visibility = Show(effect is LightEffect.Breathing or LightEffect.Rainbow);

            ColorHex.Text = setting.Color;
            MarkSwatches(Swatches, setting.Color);

            if (Math.Abs(BrightnessSlider.Value - setting.Brightness) > 0.5 && BrightnessSlider.FocusState == FocusState.Unfocused)
            {
                BrightnessSlider.Value = setting.Brightness;
            }
            BrightnessText.Text = string.Create(CultureInfo.CurrentCulture, $"{BrightnessSlider.Value:0}%");

            if (effect == LightEffect.Temperature)
            {
                BuildSources();
                int index = _sources.FindIndex(s => s.Keys.SequenceEqual(setting.Sources));
                if (SourceBox.SelectedIndex != index)
                {
                    SourceBox.SelectedIndex = index;
                }
                CoolBox.Show(setting.Cool);
                HotBox.Show(setting.Hot);
                RefreshScale(setting);
                RefreshTemperature();
            }
        }

        /// <summary>The setting the editor shows: the light's own, else what it shows now, else a plain white.</summary>
        private LightSetting Editing(LightInfo light) =>
            Setting(light.Key)?.Copy() ?? light.Found?.Copy() ?? new LightSetting();

        private void RefreshTemperature()
        {
            if (!_isShown)
            {
                return;
            }
            if (State is { } state)
            {
                foreach (var light in state.Lights)
                {
                    if (_cards.TryGetValue(light.Key, out var card) && Setting(light.Key) is { Effect: LightEffect.Temperature } s)
                    {
                        card.Effect.Text = EffectLabel(s, light.Key);
                        card.Preview.Fill = PreviewBrush(s, light.Key);
                    }
                }
            }
            if (TemperatureCard.Visibility != Visibility.Visible || EditedLight() is not { } edited)
            {
                return;
            }
            var setting = Editing(edited);
            float? now = NowTemperature(setting);
            NowText.Text = now is { } t ? string.Create(CultureInfo.CurrentCulture, $"Now {t:0} °C") : "Now —";
            double width = GradientBar.ActualWidth;
            if (now is { } v && width > 0 && setting.Hot > setting.Cool)
            {
                double k = Math.Clamp((v - setting.Cool) / (setting.Hot - setting.Cool), 0, 1);
                NowMarker.Margin = new Thickness(k * (width - NowMarker.Width), 0, 0, 0);
                NowMarker.Visibility = Visibility.Visible;
            }
            else
            {
                NowMarker.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>Highest reading of the setting's temperature sources.</summary>
        private static float? NowTemperature(LightSetting s)
        {
            float? hottest = null;
            foreach (var key in s.Sources)
            {
                if (App.Sensors.TryGetIndex(key, out int i) && App.Sensors.GetNow(i) is { } v && (hottest is null || v > hottest))
                {
                    hottest = v;
                }
            }
            return hottest;
        }

        private void BuildSources()
        {
            if (App.Sensors.Catalog is not { } catalog)
            {
                return;
            }
            var sources = TemperatureSources.Build(catalog);
            if (sources.Select(s => s.Label).SequenceEqual(_sources.Select(s => s.Label)))
            {
                return;
            }
            _sources = sources;
            SourceBox.ItemsSource = sources.Select(s => s.Label).ToList();
        }

        // ----- Changing -----

        private LightInfo? EditedLight() => State?.Lights.FirstOrDefault(l => l.Key == _selected);

        /// <summary>Applies a change to what the editor shows and sends it (to the light, or to all with Sync all).</summary>
        private void Change(Action<LightSetting> change)
        {
            if (_settingControls || EditedLight() is not { } light)
            {
                return;
            }
            var setting = Editing(light);
            change(setting);
            if (setting.Effect == LightEffect.Temperature)
            {
                if (setting.Sources.Count == 0 && App.Sensors.Catalog is { } catalog && TemperatureSources.Cpu(catalog) is int cpu and >= 0)
                {
                    setting.Sources = [catalog.Sensors[cpu].Key];
                }
                if (setting.Hot - setting.Cool < 5)
                {
                    setting.Hot = Math.Min(125, setting.Cool + 5);
                    setting.Cool = setting.Hot - 5;
                }
            }
            _ = Send(Sync ? AllKey : light.Key, setting);
        }

        private async Task Send(string key, LightSetting setting)
        {
            _mine[key] = (setting, DateTime.UtcNow);
            Refresh();
            string? error = key == AllKey
                ? await App.Control.SetColorSyncAsync(true, setting)
                : await App.Control.SetLightAsync(key, setting);
            ShowError(error);
            if (error is not null)
            {
                _mine.Remove(key);
                Refresh();
            }
        }

        private void ShowError(string? error)
        {
            ErrorInfo.Message = error ?? "";
            ErrorInfo.IsOpen = error is not null;
        }

        private void Effect_Click(object sender, RoutedEventArgs e)
        {
            var effect = Enum.Parse<LightEffect>((string)((Button)sender).Tag);
            Change(s => s.Effect = effect);
        }

        private void Speed_Click(object sender, RoutedEventArgs e)
        {
            var speed = Enum.Parse<LightSpeed>((string)((Button)sender).Tag);
            Change(s => s.Speed = speed);
        }

        private void Swatch_Click(object sender, RoutedEventArgs e)
        {
            string hex = (string)((Button)sender).Tag;
            Change(s => s.Color = hex);
        }

        // Opened, not Opening: a ColorPicker given its color before it is on screen leaves the spectrum cursor in the corner.
        private void CustomFlyout_Opened(object sender, object e)
        {
            if (EditedLight() is { } light)
            {
                _settingControls = true;
                Picker.Color = ToColor(Editing(light).Color);
                _settingControls = false;
            }
        }

        private void Picker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
        {
            var c = args.NewColor;
            string hex = LightColor.ToHex(c.R, c.G, c.B);
            Change(s => s.Color = hex);
        }

        private void BrightnessSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            BrightnessText.Text = string.Create(CultureInfo.CurrentCulture, $"{e.NewValue:0}%");
            float value = (float)Math.Round(e.NewValue);
            Change(s => s.Brightness = value);
        }

        private void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SourceBox.SelectedIndex < 0 || SourceBox.SelectedIndex >= _sources.Count)
            {
                return;
            }
            List<string> keys = [.. _sources[SourceBox.SelectedIndex].Keys];
            Change(s => s.Sources = keys);
        }

        private async void SyncSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_settingControls || EditedLight() is not { } light)
            {
                return;
            }
            bool on = SyncSwitch.IsOn;
            // Turning it on: all lights take the look of the light being edited.
            LightSetting? all = on ? Setting(light.Key)?.Copy() ?? light.Found?.Copy() ?? new LightSetting() : null;
            _mySync = (on, DateTime.UtcNow);
            if (all is not null)
            {
                _mine[AllKey] = (all, DateTime.UtcNow);
            }
            Refresh();
            string? error = await App.Control.SetColorSyncAsync(on, all);
            ShowError(error);
            if (error is not null)
            {
                _mySync = null;
                _mine.Remove(AllKey);
                Refresh();
            }
        }

        // ----- Temperature scale colors -----

        private readonly Flyout _scaleFlyout = new() { Placement = FlyoutPlacementMode.Bottom };
        private readonly TextBlock _scaleTitle = new() { Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
        private readonly StackPanel _scaleSwatches = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        private readonly ColorPicker _scalePicker = new()
        {
            IsAlphaEnabled = false,
            IsMoreButtonVisible = false,
            IsColorSliderVisible = true,
            IsColorChannelTextInputVisible = false,
            IsHexInputVisible = true,
        };
        private bool _editingHot;
        private string _scaleOpenColor = "";

        /// <summary>One flyout for both ends of the scale: the palette for a quick pick, and the full picker.</summary>
        private void BuildScaleFlyout()
        {
            foreach (var (hex, name) in Palette)
            {
                var swatch = MakeSwatch(hex, name);
                swatch.Click += (_, _) => SetScaleColor(hex);
                _scaleSwatches.Children.Add(swatch);
            }
            _scalePicker.ColorChanged += (_, args) => SetScaleColor(LightColor.ToHex(args.NewColor.R, args.NewColor.G, args.NewColor.B));
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(_scaleTitle);
            content.Children.Add(_scaleSwatches);
            content.Children.Add(_scalePicker);
            _scaleFlyout.Content = content;
            // The picker gets its color once on screen (given earlier, its spectrum cursor stays in the corner).
            _scaleFlyout.Opened += (_, _) =>
            {
                _settingControls = true;
                _scalePicker.Color = ToColor(_scaleOpenColor);
                _settingControls = false;
            };
        }

        private void ScaleColor_Click(object sender, RoutedEventArgs e)
        {
            if (EditedLight() is not { } light || sender is not Button button)
            {
                return;
            }
            _editingHot = (string)button.Tag == "Hot";
            var setting = Editing(light);
            string color = ScaleEnd(setting, _editingHot);
            _scaleTitle.Text = _editingHot ? "Hot color" : "Cool color";
            _scaleOpenColor = color;
            MarkSwatches(_scaleSwatches, color);
            _scaleFlyout.ShowAt(button);
        }

        private void SetScaleColor(string hex)
        {
            bool hot = _editingHot;
            Change(s =>
            {
                // The other end keeps what it shows now, so picking one color never changes the other.
                s.CoolColor = hot ? ScaleEnd(s, false) : hex;
                s.HotColor = hot ? hex : ScaleEnd(s, true);
            });
            MarkSwatches(_scaleSwatches, hex);
        }

        private void ScaleReset_Click(object sender, RoutedEventArgs e) => Change(s =>
        {
            s.CoolColor = null;
            s.HotColor = null;
        });

        private static string ScaleEnd(LightSetting s, bool hot) =>
            hot ? s.HotColor ?? LightColor.DefaultHot : s.CoolColor ?? LightColor.DefaultCool;

        private void RefreshScale(LightSetting setting)
        {
            GradientBar.Background = GradientBrush(setting);
            CoolColorDot.Fill = new SolidColorBrush(ToColor(ScaleEnd(setting, false)));
            HotColorDot.Fill = new SolidColorBrush(ToColor(ScaleEnd(setting, true)));
            ScaleReset.Visibility = Show(LightColor.HasOwnColors(setting));
        }

        private Button MakeSwatch(string hex, string name)
        {
            var swatch = new Button
            {
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(16),
                Tag = hex,
                Background = new SolidColorBrush(ToColor(hex)),
                BorderThickness = new Thickness(1),
            };
            swatch.Resources["ButtonBackgroundPointerOver"] = swatch.Background;
            swatch.Resources["ButtonBackgroundPressed"] = swatch.Background;
            AutomationProperties.SetName(swatch, name);
            ToolTipService.SetToolTip(swatch, name);
            return swatch;
        }

        /// <summary>The swatch of the current color gets a thick accent ring.</summary>
        private void MarkSwatches(Panel swatches, string color)
        {
            foreach (Button swatch in swatches.Children.Cast<Button>())
            {
                bool on = string.Equals((string)swatch.Tag, color, StringComparison.OrdinalIgnoreCase);
                swatch.BorderBrush = Theme.Brush(this, on ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush");
                swatch.BorderThickness = new Thickness(on ? 3 : 1);
            }
        }

        // ----- Helpers -----

        private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

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

        private static Color ToColor(string hex) =>
            LightColor.TryParse(hex, out byte r, out byte g, out byte b) ? Color.FromArgb(255, r, g, b) : Colors.White;

        private static LinearGradientBrush GradientBrush(LightSetting setting)
        {
            var brush = new LinearGradientBrush { StartPoint = new(0, 0.5), EndPoint = new(1, 0.5) };
            foreach (var (at, color) in LightColor.Scale(setting))
            {
                brush.GradientStops.Add(new GradientStop { Offset = at, Color = ToColor(color) });
            }
            return brush;
        }

        private static LinearGradientBrush RainbowBrush()
        {
            string[] colors = ["#FF3B30", "#FFD60A", "#34C759", "#00C7BE", "#0A84FF", "#BF5AF2"];
            var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1) };
            for (int i = 0; i < colors.Length; i++)
            {
                brush.GradientStops.Add(new GradientStop { Offset = i / (double)(colors.Length - 1), Color = ToColor(colors[i]) });
            }
            return brush;
        }
    }
}
