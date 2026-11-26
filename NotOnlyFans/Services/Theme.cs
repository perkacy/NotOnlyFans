using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace NotOnlyFans.Services;

/// <summary>
/// The app theme the user chose in Settings (Light, Dark, or the Windows setting), and theme brushes for code.
///
/// At start the choice becomes Application.RequestedTheme (in the App constructor, the only place it can be set),
/// so the pages, made in memory before they are on screen, start in the right colors. Later changes go to
/// RequestedTheme of the window's content: at once, no restart. "System" is resolved here to light or dark,
/// and followed when Windows switches.
/// Application.Current.Resources[...] answers in the start theme only, so code that makes elements itself
/// asks <see cref="Brush"/> with the element, which follows the element's theme.
/// </summary>
public static class Theme
{
    public const string System = "System", Light = "Light", Dark = "Dark";

    private static readonly Dictionary<(string Theme, string Key), Brush> Cache = [];
    private static readonly Windows.UI.ViewManagement.AccessibilitySettings Accessibility = new();
    // Kept in a field: its ColorValuesChanged tells when Windows switches light/dark.
    private static readonly Windows.UI.ViewManagement.UISettings Ui = new();

    /// <summary>Light or dark for a choice ("System": what Windows uses for apps now).</summary>
    public static ElementTheme Resolve(string? choice) => choice switch
    {
        Light => ElementTheme.Light,
        Dark => ElementTheme.Dark,
        _ => WindowsIsDark() ? ElementTheme.Dark : ElementTheme.Light,
    };

    public static ApplicationTheme ForApplication(string? choice) =>
        Resolve(choice) == ElementTheme.Light ? ApplicationTheme.Light : ApplicationTheme.Dark;

    // Windows' app mode shows in the default app background: black in dark mode, white in light.
    private static bool WindowsIsDark() =>
        Ui.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background).R < 128;

    /// <summary>Applies the choice to the window: its content (pages, flyouts, dialogs) and the title bar buttons.</summary>
    public static void Apply(Window window, string? choice)
    {
        var theme = Resolve(choice);
        if (window.Content is FrameworkElement root && root.RequestedTheme != theme)
        {
            root.RequestedTheme = theme;
        }
        window.AppWindow.TitleBar.PreferredTheme = theme == ElementTheme.Light ? TitleBarTheme.Light : TitleBarTheme.Dark;
    }

    /// <summary>With "System", switches the window when Windows switches between light and dark.</summary>
    public static void FollowWindows(Window window, Func<string> choice)
    {
        var ui = window.DispatcherQueue;
        Ui.ColorValuesChanged += (_, _) => ui.TryEnqueue(() =>
        {
            if (choice() == System)
            {
                Apply(window, System);
            }
        });
    }

    /// <summary>
    /// Keeps a page right after light/dark changes, also when it changed while the page was off screen
    /// (pages are kept in memory, and an element outside the window gets no ActualThemeChanged):
    /// on the next Loaded or ActualThemeChanged it fixes the InfoBars and calls <paramref name="rebuild"/>
    /// for what the page made in code.
    /// </summary>
    public static void Follow(FrameworkElement page, Action? rebuild = null)
    {
        var shown = page.ActualTheme;
        void Check()
        {
            if (page.ActualTheme == shown)
            {
                return;
            }
            shown = page.ActualTheme;
            FixInfoBars(page);
            rebuild?.Invoke();
        }
        page.Loaded += (_, _) => Check();
        page.ActualThemeChanged += (_, _) => Check();
    }

    /// <summary>
    /// An InfoBar sets its colors from a visual state, which a theme change doesn't redo: it stays in the old theme.
    /// Setting the severity again redoes it.
    /// </summary>
    private static void FixInfoBars(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Microsoft.UI.Xaml.Controls.InfoBar bar)
            {
                var severity = bar.Severity;
                bar.Severity = severity == Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational
                    ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success
                    : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational;
                bar.Severity = severity;
            }
            FixInfoBars(child);
        }
    }

    /// <summary>A theme brush (e.g. "TextFillColorSecondaryBrush") as it is in the theme of <paramref name="element"/>.</summary>
    public static Brush Brush(FrameworkElement element, string key)
    {
        string theme = Accessibility.HighContrast ? "HighContrast"
            : element.ActualTheme == ElementTheme.Light ? Light : Dark;
        if (Cache.TryGetValue((theme, key), out var cached))
        {
            return cached;
        }

        var found = Find(Application.Current.Resources, theme, key)
                    ?? (theme == Dark ? Find(Application.Current.Resources, "Default", key) : null);
        if (found is Brush brush)
        {
            Cache[(theme, key)] = brush;
            return brush;
        }
        return (Brush)Application.Current.Resources[key];   // not found by theme: the start theme's (not remembered)
    }

    private static object? Find(ResourceDictionary dictionary, string theme, string key)
    {
        if (dictionary.ThemeDictionaries.TryGetValue(theme, out var t) && t is ResourceDictionary themed
            && themed.TryGetValue(key, out var value))
        {
            return value;
        }
        // Later merged dictionaries win, as in XAML lookup.
        for (int i = dictionary.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            if (Find(dictionary.MergedDictionaries[i], theme, key) is { } v)
            {
                return v;
            }
        }
        return null;
    }
}
