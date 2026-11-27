using NotOnlyFans.Contracts;

namespace NotOnlyFans.Service.Lights;

/// <summary>What one light should show now. <see cref="LightEffect.Temperature"/> is already turned into a static color.</summary>
internal readonly record struct LightOutput(LightEffect Effect, byte R, byte G, byte B, float Brightness, LightSpeed Speed)
{
    /// <summary>Same effect and settings, so only the color may need writing.</summary>
    public bool SameModeAs(LightOutput other) =>
        Effect == other.Effect && Brightness == other.Brightness && Speed == other.Speed;

    /// <summary>The color dimmed by the brightness, for controllers without a brightness setting of their own.</summary>
    public (byte R, byte G, byte B) Dimmed()
    {
        float k = Math.Clamp(Brightness, 0, 100) / 100f;
        return ((byte)Math.Round(R * k), (byte)Math.Round(G * k), (byte)Math.Round(B * k));
    }
}

/// <summary>
/// A piece of hardware with one or more lights. All calls come from one thread at a time
/// (under <see cref="SensorPoller.Sync"/>), so drivers need no locking of their own.
/// </summary>
internal interface ILightDevice : IDisposable
{
    IReadOnlyList<LightInfo> Lights { get; }

    /// <summary>
    /// Shows the given outputs (by light key; lights not given keep what they show).
    /// <paramref name="save"/> also stores them in the device, so they come back after a restart
    /// even before the service runs; it wears the device's memory, so only for settled choices.
    /// </summary>
    void Apply(IReadOnlyDictionary<string, LightOutput> outputs, bool save);

    /// <summary>Reads what the lights show now into <see cref="LightInfo.Found"/>, where the device can tell.</summary>
    void Refresh();
}

/// <summary>
/// Windows Dynamic Lighting devices reported by the window app (see <see cref="LightController.SetAppLights"/>).
/// The app shows what the color state says for them, so there is nothing to send from here.
/// </summary>
internal sealed class AppLights : ILightDevice
{
    public const string Prefix = "lamp/";

    private List<LightInfo> _lights = [];

    public IReadOnlyList<LightInfo> Lights => _lights;

    public void Set(List<LightInfo> lights) => _lights = lights;

    public void Apply(IReadOnlyDictionary<string, LightOutput> outputs, bool save)
    {
    }

    public void Refresh()
    {
    }

    public void Dispose()
    {
    }
}
