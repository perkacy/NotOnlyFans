using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using NotOnlyFans.Contracts;

namespace NotOnlyFans.Services;

/// <summary>
/// Live connection to NotOnlyFans.Service. Reads the sensor pipe on a background task,
/// reconnects on its own, and raises events on the UI thread.
/// Values are coalesced: if the UI is busy, only the newest reading is delivered.
/// </summary>
public sealed class SensorClient
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private readonly DispatcherQueue _ui;
    private SensorValues? _pendingValues;
    private int _valuesQueued;

    private Dictionary<string, int> _indexByKey = new(StringComparer.Ordinal);

    public SensorClient(DispatcherQueue ui) => _ui = ui;

    /// <summary>What sensors exist (UI thread). Kept after a disconnect so pages keep their layout.</summary>
    public SensorCatalog? Catalog { get; private set; }

    /// <summary>Latest reading for <see cref="Catalog"/> (UI thread); null while disconnected.</summary>
    public SensorValues? Values { get; private set; }

    public bool IsConnected { get; private set; }

    /// <summary>
    /// True once a connection attempt has failed and we are still not connected.
    /// (Not true during the first moments after start-up, so no warning flashes on screen.)
    /// </summary>
    public bool IsServiceMissing { get; private set; }

    /// <summary>Fan control settings and safety state as the service applies them (UI thread).</summary>
    public ControlState? Control { get; private set; }

    public event Action? ControlChanged;

    /// <summary>Lights and their settings as the service applies them (UI thread).</summary>
    public ColorState? Colors { get; private set; }

    public event Action? ColorsChanged;

    /// <summary>Graphics cards that can be overclocked and what was applied (UI thread).</summary>
    public OcState? Oc { get; private set; }

    public event Action? OcChanged;

    public event Action? CatalogChanged;
    public event Action? ValuesChanged;
    public event Action? ConnectionChanged;

    public void Start() => _ = Task.Run(RunAsync);

    public bool TryGetIndex(string key, out int index) => _indexByKey.TryGetValue(key, out index);

    public SensorInfo? FindSensor(string key) =>
        Catalog is not null && TryGetIndex(key, out int i) ? Catalog.Sensors[i] : null;

    public float? GetNow(int index) =>
        Values is { } v && index < v.Now.Length ? v.Now[index] : null;

    private async Task RunAsync()
    {
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", SensorPipe.Name, PipeDirection.In, PipeOptions.Asynchronous);
                await pipe.ConnectAsync((int)RetryDelay.TotalMilliseconds);
                PipeCheck.EnsureService(pipe);
                Post(() => SetConnected(true));

                using var reader = new StreamReader(pipe, Encoding.UTF8);
                while (await reader.ReadLineAsync() is { } line)
                {
                    Handle(line);
                }
            }
            catch (Exception)
            {
                // Service not running, stopped, not ours (PipeCheck), or sent something we do not understand:
                // try again shortly. Nothing here may end this loop - the app would never reconnect.
            }

            Post(() => SetConnected(false));
            await Task.Delay(RetryDelay);
        }
    }

    private void Handle(string line)
    {
        var message = JsonSerializer.Deserialize(line, ContractsJson.Default.PipeMessage);
        switch (message?.Type)
        {
            case SensorPipe.CatalogType when message.Catalog is { } catalog:
                var index = new Dictionary<string, int>(catalog.Sensors.Count, StringComparer.Ordinal);
                for (int i = 0; i < catalog.Sensors.Count; i++)
                {
                    index[catalog.Sensors[i].Key] = i;
                }
                Post(() =>
                {
                    Catalog = catalog;
                    _indexByKey = index;
                    Values = null;
                    CatalogChanged?.Invoke();
                });
                break;

            case ControlPipe.ControlType when message.Control is { } control:
                Post(() =>
                {
                    Control = control;
                    ControlChanged?.Invoke();
                });
                break;

            case ColorPipe.ColorsType when message.Colors is { } colors:
                Post(() =>
                {
                    Colors = colors;
                    ColorsChanged?.Invoke();
                });
                break;

            case OcPipe.OcType when message.Oc is { } oc:
                Post(() =>
                {
                    Oc = oc;
                    OcChanged?.Invoke();
                });
                break;

            case SensorPipe.ValuesType when message.Values is { } values:
                Volatile.Write(ref _pendingValues, values);
                if (Interlocked.Exchange(ref _valuesQueued, 1) == 0)
                {
                    Post(DeliverValues);
                }
                break;
        }
    }

    private void DeliverValues()
    {
        Volatile.Write(ref _valuesQueued, 0);
        var values = Interlocked.Exchange(ref _pendingValues, null);
        if (values is null || Catalog is null || values.CatalogVersion != Catalog.Version)
        {
            return;
        }
        Values = values;
        ValuesChanged?.Invoke();
    }

    private void SetConnected(bool connected)
    {
        if (IsConnected == connected && IsServiceMissing == !connected)
        {
            return;
        }
        IsConnected = connected;
        IsServiceMissing = !connected;
        if (!connected && Values is not null)
        {
            Values = null;
            ValuesChanged?.Invoke();
        }
        ConnectionChanged?.Invoke();
    }

    /// <summary>Runs on the UI thread; an error in one page's handler is logged and does not close the app.</summary>
    private void Post(DispatcherQueueHandler action) => _ui.TryEnqueue(() =>
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    });
}
