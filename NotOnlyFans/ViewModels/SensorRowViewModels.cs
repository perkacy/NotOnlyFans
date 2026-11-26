using System.ComponentModel;
using System.Runtime.CompilerServices;
using NotOnlyFans.Services;

namespace NotOnlyFans.ViewModels;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One sensor on the Sensors page. Text only changes (and redraws) when the shown value changes.</summary>
public sealed class SensorRowViewModel(int sensorIndex, string key, string kind, string name, string pinLabel) : Observable
{
    private string _name = name;
    private string _pinLabel = pinLabel;
    private string _now = SensorFormat.NoValue;
    private string _min = SensorFormat.NoValue;
    private string _max = SensorFormat.NoValue;
    private bool _isPinned;

    public int SensorIndex { get; } = sensorIndex;
    public string Key { get; } = key;
    public string Kind { get; } = kind;
    /// <summary>Changes when the user renames (e.g. a fan).</summary>
    public string Name { get => _name; set => Set(ref _name, value); }

    public string PinLabel { get => _pinLabel; set => _pinLabel = value; }

    /// <summary>Key the custom name is stored under (a fan's speed row uses its fan's RPM key).</summary>
    public string NameKey { get; init; } = key;

    /// <summary>Name from the hardware, shown as the placeholder when renaming.</summary>
    public string OriginalName { get; init; } = name;

    private bool _isHidden;
    private bool _isRenamed;

    /// <summary>Hidden by the user; only on screen while "Show hidden" is on, dimmed.</summary>
    public bool IsHidden
    {
        get => _isHidden;
        set
        {
            if (Set(ref _isHidden, value))
            {
                Raise(nameof(IsShown));
                Raise(nameof(RowOpacity));
            }
        }
    }

    public bool IsShown => !_isHidden;
    public double RowOpacity => _isHidden ? 0.5 : 1;

    public bool IsRenamed { get => _isRenamed; set => Set(ref _isRenamed, value); }

    public string Now { get => _now; private set => Set(ref _now, value); }
    public string Min { get => _min; private set => Set(ref _min, value); }
    public string Max { get => _max; private set => Set(ref _max, value); }

    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (Set(ref _isPinned, value))
            {
                Raise(nameof(IsUnpinned));
                Raise(nameof(PinToolTip));
            }
        }
    }

    public bool IsUnpinned => !_isPinned;

    private bool _isChartOpen;

    /// <summary>The row's 5-minute chart is open under it (a click on the row opens and closes it).</summary>
    public bool IsChartOpen { get => _isChartOpen; set => Set(ref _isChartOpen, value); }

    public string PinToolTip => _isPinned ? "Unpin from sidebar" : "Pin to sidebar";

    public void Update(float? now, float? min, float? max)
    {
        Now = SensorFormat.Format(Kind, now);
        Min = SensorFormat.Format(Kind, min);
        Max = SensorFormat.Format(Kind, max);
    }

    public void TogglePin() => App.Pins.Toggle(Key, PinLabel);
}

/// <summary>
/// One card on the Sensors page (CPU, GPU, Motherboard, Fans, a drive...). Opens and closes instantly.
/// Shows the essential rows (temperatures, fans, usage); the rest appear with "Show all".
/// </summary>
public sealed class SensorGroupViewModel(
    string id, string title, string subtitle,
    IReadOnlyList<SensorRowViewModel> rows, IReadOnlyList<SensorRowViewModel> moreRows) : Observable
{
    private bool _isOpen = true;
    private bool _isShowingAll;

    public string Id { get; } = id;
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;

    /// <summary>Always visible while the card is open.</summary>
    public IReadOnlyList<SensorRowViewModel> Rows { get; } = rows;

    /// <summary>Only after "Show all".</summary>
    public IReadOnlyList<SensorRowViewModel> MoreRows { get; } = moreRows;

    public int TotalCount => Rows.Count + MoreRows.Count;
    public string CountText => TotalCount == 1 ? "1 sensor" : $"{TotalCount} sensors";
    public bool HasMore => MoreRows.Count > 0;

    public bool IsShowingAll
    {
        get => _isShowingAll;
        set
        {
            if (Set(ref _isShowingAll, value))
            {
                Raise(nameof(ShowAllText));
            }
        }
    }

    public string ShowAllText => _isShowingAll
        ? "Show fewer"
        : MoreRows.Count == 1 ? "Show all (1 more)" : $"Show all ({MoreRows.Count} more)";

    public void ToggleShowAll() => IsShowingAll = !IsShowingAll;

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (Set(ref _isOpen, value))
            {
                Raise(nameof(ChevronGlyph));
            }
        }
    }

    /// <summary>Chevron up when open, down when closed (Segoe Fluent Icons).</summary>
    public string ChevronGlyph => _isOpen ? "" : "";

    public void Toggle() => IsOpen = !IsOpen;
}
