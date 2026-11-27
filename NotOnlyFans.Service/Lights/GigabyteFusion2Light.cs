using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NotOnlyFans.Contracts;

namespace NotOnlyFans.Service.Lights;

/// <summary>
/// Gigabyte RGB Fusion 2 USB lighting controllers on motherboards (ITE IT5702 / IT8297 / IT5711; protocol as
/// documented by the OpenRGB project; this is our own implementation, not yet tried on real hardware, so its light
/// is marked experimental). 64-byte feature reports with id 0xCC.
/// Which output of the controller is which header differs per board, so NotOnlyFans drives them all together
/// as one light ("all zones" effect, register 0x20), which every board has.
/// </summary>
internal sealed class GigabyteFusion2Light : ILightDevice
{
    private const ushort Vendor = 0x048D;
    private const byte ReportId = 0xCC;
    private const int ReportLength = 64;

    private static readonly ushort[] Products = [0x5702, 0x8297, 0x8950, 0x5711];

    private const byte EffectStatic = 1;
    private const byte EffectPulse = 2;
    private const byte EffectColorCycle = 4;

    private readonly SafeFileHandle _handle;
    private readonly ushort _product;
    private readonly byte _support;
    private readonly LightInfo _light;
    private bool _started;

    private GigabyteFusion2Light(SafeFileHandle handle, ushort product, byte support)
    {
        _handle = handle;
        _product = product;
        _support = support;
        _light = new LightInfo
        {
            Key = $"gigabyte-{product:x4}/all",
            Name = "Motherboard",
            Detail = "All lights on the board and its RGB headers",
            Device = Hid.BoardName() is { } name ? $"Gigabyte {name}" : "Gigabyte motherboard",
            Kind = "Board",
            Effects = [LightEffect.Static, LightEffect.Breathing, LightEffect.Rainbow, LightEffect.Temperature, LightEffect.Off],
            Experimental = true,
        };
    }

    public IReadOnlyList<LightInfo> Lights => [_light];

    public static GigabyteFusion2Light? TryOpen()
    {
        foreach (var (path, pid) in Hid.Find(Vendor))
        {
            if (!Products.Contains(pid))
            {
                continue;
            }
            var handle = Hid.Open(path);
            if (handle is null)
            {
                continue;
            }
            var caps = Hid.Caps(handle);
            // The lighting interface: vendor page 0xFF89, usage 0xCC.
            if (caps.UsagePage == 0xFF89 && caps.Usage == 0xCC && caps.Feature >= ReportLength
                && ReadConfig(handle) is { } config)
            {
                return new GigabyteFusion2Light(handle, pid, config[11]);
            }
            handle.Dispose();
        }
        return null;
    }

    /// <summary>The controller's description (0x60): product name, firmware, LED counts, what it supports.</summary>
    private static byte[]? ReadConfig(SafeFileHandle handle)
    {
        if (!Send(handle, [ReportId, 0x60]))
        {
            return null;
        }
        var buffer = new byte[Math.Max(ReportLength, Hid.FeatureLength(handle))];
        buffer[0] = ReportId;
        return Hid.GetFeature(handle, buffer) && buffer[0] == ReportId ? buffer : null;
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
        if (!_started)
        {
            if ((_support & 0x02) != 0)
            {
                Check(Send(_handle, [ReportId, 0x48]));   // board with Windows Dynamic Lighting: hand the lights to the controller's effects
            }
            Check(Send(_handle, [ReportId, 0x31, 0x00]));          // no music beat
            Check(Send(_handle, [ReportId, 0x32, 0x00, 0x00]));    // ARGB headers follow the built-in effects
            _started = true;
        }

        bool all5711 = _product == 0x5711;
        var p = new byte[ReportLength];
        p[0] = ReportId;
        p[1] = 0x20;                                                        // all zones
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(2), all5711 ? 0x07FFu : 0xFFu);
        p[12] = output.Effect == LightEffect.Off ? (byte)0 : (byte)Math.Clamp(Math.Round(output.Brightness * 2.55), 0, 255);
        p[13] = 0;
        if (output.Effect != LightEffect.Off && output.Effect != LightEffect.Rainbow)
        {
            p[14] = output.B;   // stored as 0x00RRGGBB, little-endian
            p[15] = output.G;
            p[16] = output.R;
        }
        int speed = output.Speed switch { LightSpeed.Slow => 8, LightSpeed.Medium => 4, _ => 1 };
        switch (output.Effect)
        {
            case LightEffect.Breathing:
                p[11] = EffectPulse;
                p[12] = (byte)Math.Min((int)p[12], 100);   // breathing misbehaves above 100 (as OpenRGB notes)
                ushort fade = (ushort)(speed <= 6 ? 400 + 100 * speed : 1000 + 200 * (speed - 6));
                BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(22), fade);
                BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(24), fade);
                BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(26), 200);
                break;
            case LightEffect.Rainbow:
                p[11] = EffectColorCycle;
                ushort cycle = (ushort)(100 * speed + 300);
                BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(22), cycle);
                BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(24), (ushort)(cycle - 200));
                p[30] = 7;
                break;
            default:
                p[11] = EffectStatic;
                break;
        }
        Check(Send(_handle, p));
        Check(Send(_handle, [ReportId, 0x28, 0xFF, all5711 ? (byte)0x07 : (byte)0x00]));   // show it

        if (save && (_support & 0x01) != 0)
        {
            Check(Send(_handle, [ReportId, 0x47, 0x01, 0x00]));
            Thread.Sleep(20);
            Check(Send(_handle, [ReportId, 0x5E, 0x00]));
        }
    }

    private static bool Send(SafeFileHandle handle, byte[] data)
    {
        var buffer = new byte[Math.Max(ReportLength, Hid.FeatureLength(handle))];
        Array.Copy(data, buffer, data.Length);
        return Hid.SetFeature(handle, buffer);
    }

    private void Check(bool ok)
    {
        if (!ok)
        {
            _started = false;
            throw new IOException($"The motherboard's lighting controller refused the change (error {Marshal.GetLastWin32Error()}).");
        }
    }

    public void Dispose() => _handle.Dispose();
}
