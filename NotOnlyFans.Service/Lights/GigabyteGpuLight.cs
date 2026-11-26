using System.Runtime.InteropServices;
using NotOnlyFans.Contracts;

namespace NotOnlyFans.Service.Lights;

/// <summary>
/// The light of Gigabyte graphics cards with the first-generation RGB Fusion controller, on the card's
/// I²C bus through NVIDIA's driver (NVAPI). Protocol as documented by the OpenRGB project; our own code.
/// The controller takes a stream of single bytes: 0x88 mode speed brightness, 0x40 R G B, 0xAA 0 0 0 (store).
/// It can't be read back, so what NotOnlyFans set is the only record of what it shows.
/// Only cards on the tested list are touched, and only after the controller answers the handshake.
/// </summary>
internal sealed class GigabyteGpuLight : ILightDevice
{
    private const byte ModeStatic = 0x01;
    private const byte ModeBreathing = 0x02;
    private const byte ModeSpectrum = 0x11;

    /// <summary>Tested cards: NVIDIA device ids, Gigabyte subsystem id, controller address.</summary>
    private static readonly (ushort[] Devices, ushort SubDevice, byte Address, string Name)[] Cards =
    [
        ([0x2503, 0x2504, 0x2487], 0x4072, 0x63, "GeForce RTX 3060 EAGLE OC"),
    ];

    private readonly NvApi.I2cBus _bus;
    private readonly byte _address;
    private readonly LightInfo _light;
    private LightOutput? _shown;

    private GigabyteGpuLight(NvApi.I2cBus bus, byte address, ushort subDevice, string name)
    {
        _bus = bus;
        _address = address;
        _light = new LightInfo
        {
            Key = $"gpu/1458-{subDevice:x4}",
            Name = "Graphics card",
            Detail = "Logo on the side",
            Device = $"Gigabyte {name}",
            Kind = "Gpu",
            Effects = [LightEffect.Static, LightEffect.Breathing, LightEffect.Rainbow, LightEffect.Temperature, LightEffect.Off],
        };
    }

    public IReadOnlyList<LightInfo> Lights => [_light];

    public static GigabyteGpuLight? TryOpen()
    {
        foreach (var bus in NvApi.Gpus())
        {
            if (bus.Vendor != 0x10DE || bus.SubVendor != 0x1458)
            {
                continue;
            }
            foreach (var card in Cards)
            {
                if (card.SubDevice == bus.SubDevice && card.Devices.Contains(bus.Device) && Handshake(bus, card.Address))
                {
                    return new GigabyteGpuLight(bus, card.Address, card.SubDevice, card.Name);
                }
            }
        }
        return null;
    }

    /// <summary>The controller echoes 0xAB and tells its version (0x10, 0x11, 0x12 or 0x14).</summary>
    private static bool Handshake(NvApi.I2cBus bus, byte address)
    {
        if (!bus.WriteByte(address, 0xAB) || !bus.WriteByte(address, 0) || !bus.WriteByte(address, 0) || !bus.WriteByte(address, 0))
        {
            return false;
        }
        if (bus.ReadByte(address) != 0xAB)
        {
            return false;
        }
        bool known = bus.ReadByte(address) is 0x10 or 0x11 or 0x12 or 0x14;
        bus.ReadByte(address);
        bus.ReadByte(address);
        return known;
    }

    public void Refresh()
    {
    }

    public void Apply(IReadOnlyDictionary<string, LightOutput> outputs, bool save)
    {
        if (!outputs.TryGetValue(_light.Key, out var output))
        {
            return;
        }
        bool off = output.Effect == LightEffect.Off;
        byte mode = output.Effect switch
        {
            LightEffect.Breathing => ModeBreathing,
            LightEffect.Rainbow => ModeSpectrum,
            _ => ModeStatic,
        };
        byte speed = output.Speed switch { LightSpeed.Slow => 2, LightSpeed.Medium => 5, _ => 8 };
        byte brightness = off ? (byte)0 : (byte)Math.Clamp(Math.Round(output.Brightness * 0.99), 0, 99);

        // Temperature colors change every second or so: then only the color is sent.
        if (save || _shown is not { } shown || !shown.SameModeAs(output))
        {
            Send(0x88, mode, speed, brightness);
        }
        // Writing the color during a color cycle would interrupt it.
        if (mode != ModeSpectrum)
        {
            Send(0x40, off ? (byte)0 : output.R, off ? (byte)0 : output.G, off ? (byte)0 : output.B);
        }
        if (save)
        {
            Send(0xAA, 0, 0, 0);
        }
        _shown = output;
    }

    private void Send(byte a, byte b, byte c, byte d)
    {
        if (!_bus.WriteByte(_address, a) || !_bus.WriteByte(_address, b) || !_bus.WriteByte(_address, c) || !_bus.WriteByte(_address, d))
        {
            _shown = null;   // unknown now: send everything next time
            throw new IOException("The graphics card's lighting controller did not take the change.");
        }
    }

    public void Dispose()
    {
    }
}
