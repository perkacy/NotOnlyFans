using System.Diagnostics;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using NotOnlyFans.Contracts;

namespace NotOnlyFans.Service;

/// <summary>
/// Owns the LibreHardwareMonitor <see cref="Computer"/> and reads it on a fixed interval.
/// Only <see cref="Poll"/> touches LibreHardwareMonitor, and it is never called concurrently.
/// Each poll produces ready-to-send pipe lines (serialized once, shared by all clients).
/// </summary>
internal sealed class SensorPoller : IDisposable
{
    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor;
    private readonly ILogger? _log;

    // Hardware whose last update failed: its readings count as missing (a frozen value could mislead a fan curve).
    private readonly HashSet<IHardware> _failed = [];
    private readonly HashSet<IHardware> _failedNow = [];

    private volatile bool _catalogDirty = true;
    private int _catalogVersion;
    private ISensor[] _sensors = [];
    private string[] _keys = [];
    private Dictionary<string, ISensor> _byKey = new(StringComparer.Ordinal);

    /// <summary>
    /// Every use of LibreHardwareMonitor (reading, driving fans) happens under this lock,
    /// so a fan command from the window app never runs in the middle of a read.
    /// </summary>
    public object Sync { get; } = new();

    public SensorPoller(ILogger? log = null)
    {
        _log = log;
        _visitor = new UpdateVisitor((hw, ex) =>
        {
            _failedNow.Add(hw);
            if (!_failed.Contains(hw))
            {
                _log?.LogWarning(ex, "Reading {Hardware} failed - its readings count as missing until it works again.", hw.Name);
            }
        });
        _computer = Hardware.CreateComputer();
        _computer.HardwareAdded += _ => _catalogDirty = true;
        _computer.HardwareRemoved += _ => _catalogDirty = true;
        _computer.Open();
    }

    public int SensorCount => _sensors.Length;

    /// <summary>How long the last read of all hardware took.</summary>
    public TimeSpan LastPollDuration { get; private set; }

    /// <summary>
    /// Reads all hardware and returns the pipe lines: the catalog when the set of sensors changed
    /// (else null) and the values. <paramref name="afterRead"/> runs between reading and packing
    /// (fan control), still under <see cref="Sync"/>.
    /// </summary>
    public (byte[]? CatalogLine, byte[] ValuesLine) Poll(Action? afterRead = null)
    {
        lock (Sync)
        {
            var sw = Stopwatch.StartNew();
            _failedNow.Clear();
            _computer.Accept(_visitor);
            foreach (var hw in _failed.Where(hw => !_failedNow.Contains(hw)))
            {
                _log?.LogInformation("Reading {Hardware} works again.", hw.Name);
            }
            _failed.Clear();
            _failed.UnionWith(_failedNow);
            if (_catalogDirty)
            {
                _pendingCatalog = BuildCatalog();
                _catalogDirty = false;
            }
            afterRead?.Invoke();
            var lines = Pack();
            LastPollDuration = sw.Elapsed;
            return lines;
        }
    }

    private SensorCatalog? _pendingCatalog;

    /// <summary>The live LibreHardwareMonitor sensor for a catalog key (call under <see cref="Sync"/>).</summary>
    public ISensor? Find(string key) => _byKey.GetValueOrDefault(key);

    /// <summary>A sensor's current reading; null if it has none, or its hardware failed to update (call under <see cref="Sync"/>).</summary>
    public float? Reading(string key) =>
        Find(key) is { } s && !_failed.Contains(s.Hardware) && s.Value is { } v && float.IsFinite(v) ? v : null;

    /// <summary>
    /// Real temperature readings for the safety rule: every temperature sensor except values that are
    /// not temperatures of anything (distance to the CPU limit, a drive's fixed warning/critical thresholds).
    /// </summary>
    public IEnumerable<(string Name, float Value)> SafetyTemperatures()
    {
        foreach (var s in _sensors)
        {
            if (s.SensorType != SensorType.Temperature || s.Value is not { } v || !float.IsFinite(v))
            {
                continue;
            }
            if (!SafetyRule.Counts(s.Name))
            {
                continue;
            }
            yield return ($"{s.Hardware.Name} · {s.Name}", v);
        }
    }

    private (byte[]? CatalogLine, byte[] ValuesLine) Pack()
    {
        byte[]? catalogLine = null;
        if (_pendingCatalog is { } catalog)
        {
            _pendingCatalog = null;
            catalogLine = Line(new PipeMessage { Type = SensorPipe.CatalogType, Catalog = catalog });
        }

        int n = _sensors.Length;
        var values = new SensorValues
        {
            CatalogVersion = _catalogVersion,
            Time = DateTimeOffset.Now,
            Now = new float?[n],
            Min = new float?[n],
            Max = new float?[n],
        };
        for (int i = 0; i < n; i++)
        {
            var s = _sensors[i];
            values.Now[i] = _failed.Contains(s.Hardware) ? null : Clean(s.Value);
            values.Min[i] = Clean(s.Min);
            values.Max[i] = Clean(s.Max);
        }

        var valuesLine = Line(new PipeMessage { Type = SensorPipe.ValuesType, Values = values });
        return (catalogLine, valuesLine);
    }

    private SensorCatalog BuildCatalog()
    {
        var catalog = new SensorCatalog { Version = ++_catalogVersion };
        var sensors = new List<ISensor>();
        var keys = new List<string>();
        var usedKeys = new HashSet<string>(StringComparer.Ordinal);

        void Add(IHardware hw)
        {
            catalog.Hardware.Add(new HardwareInfo
            {
                Id = hw.Identifier.ToString(),
                Name = hw.Name,
                Kind = hw.HardwareType.ToString(),
                ParentId = hw.Parent?.Identifier.ToString(),
            });

            foreach (var s in hw.Sensors.OrderBy(s => s.SensorType).ThenBy(s => s.Index))
            {
                string key = UniqueKey(s, usedKeys);
                keys.Add(key);
                catalog.Sensors.Add(new SensorInfo
                {
                    Key = key,
                    HardwareId = hw.Identifier.ToString(),
                    Kind = s.SensorType.ToString(),
                    Name = s.Name,
                    Index = s.Index,
                    HasControl = s.Control is not null,
                    ControlMinimum = s.Control is { } c ? Clean(c.MinSoftwareValue) : null,
                    ControlMaximum = s.Control is { } c2 ? Clean(c2.MaxSoftwareValue) : null,
                });
                sensors.Add(s);
            }

            foreach (var sub in hw.SubHardware)
            {
                Add(sub);
            }
        }

        foreach (var hw in _computer.Hardware)
        {
            Add(hw);
        }

        _sensors = [.. sensors];
        _keys = [.. keys];
        _byKey = new Dictionary<string, ISensor>(StringComparer.Ordinal);
        for (int i = 0; i < _keys.Length; i++)
        {
            _byKey[_keys[i]] = _sensors[i];
        }
        return catalog;
    }

    /// <summary>
    /// LibreHardwareMonitor identifiers are not always unique
    /// (e.g. "GPU Bus" and "GPU Memory" both use /gpu-nvidia/0/load/3), so fall back to identifier + name.
    /// </summary>
    private static string UniqueKey(ISensor s, HashSet<string> used)
    {
        string id = s.Identifier.ToString();
        if (used.Add(id))
        {
            return id;
        }

        string withName = $"{id}|{s.Name}";
        if (used.Add(withName))
        {
            return withName;
        }

        for (int i = 2; ; i++)
        {
            string numbered = $"{withName}|{i}";
            if (used.Add(numbered))
            {
                return numbered;
            }
        }
    }

    /// <summary>JSON has no NaN/Infinity; treat them as "no reading".</summary>
    private static float? Clean(float? v) => v is { } x && float.IsFinite(x) ? x : null;

    /// <summary>One message as a pipe line (JSON + newline).</summary>
    public static byte[] Line(PipeMessage message)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, ContractsJson.Default.PipeMessage);
        var line = new byte[json.Length + 1];
        json.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        return line;
    }

    public void Dispose() => _computer.Close();
}
