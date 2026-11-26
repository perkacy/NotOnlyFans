using NotOnlyFans.Contracts;

namespace NotOnlyFans.Services;

/// <summary>
/// The last 5 minutes of every sensor (one sample per second), recorded from app start-up
/// whatever page is open, so charts are full the moment a page is shown.
/// 241 sensors × 300 samples ≈ 290 KB.
/// </summary>
public sealed class SensorHistory
{
    public const int Capacity = 300;

    private float[][] _rings = [];
    private int _head;
    private string[] _keys = [];

    /// <summary>Number of samples stored (up to <see cref="Capacity"/>).</summary>
    public int Count { get; private set; }

    /// <summary>Start over when the set of sensors changes (a reconnect with the same sensors keeps the history).</summary>
    public void OnCatalog(SensorCatalog catalog)
    {
        var keys = catalog.Sensors.Select(s => s.Key).ToArray();
        if (keys.AsSpan().SequenceEqual(_keys))
        {
            return;
        }
        _keys = keys;
        _rings = new float[keys.Length][];
        for (int i = 0; i < _rings.Length; i++)
        {
            _rings[i] = new float[Capacity];
        }
        _head = 0;
        Count = 0;
    }

    public void Record(SensorValues values)
    {
        int n = Math.Min(_rings.Length, values.Now.Length);
        for (int i = 0; i < n; i++)
        {
            _rings[i][_head] = values.Now[i] ?? float.NaN;
        }
        _head = (_head + 1) % Capacity;
        Count = Math.Min(Count + 1, Capacity);
    }

    /// <summary>Copies one sensor's samples, oldest first, into <paramref name="destination"/> (length ≥ Count).</summary>
    public int CopyTo(int sensorIndex, Span<float> destination)
    {
        if (sensorIndex < 0 || sensorIndex >= _rings.Length)
        {
            return 0;
        }
        var ring = _rings[sensorIndex];
        int start = (_head - Count + Capacity) % Capacity;
        for (int k = 0; k < Count; k++)
        {
            destination[k] = ring[(start + k) % Capacity];
        }
        return Count;
    }
}
