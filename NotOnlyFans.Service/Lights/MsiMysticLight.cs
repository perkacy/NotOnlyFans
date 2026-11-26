using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NotOnlyFans.Contracts;

namespace NotOnlyFans.Service.Lights;

/// <summary>
/// RGB headers of MSI motherboards with the Mystic Light USB controller that uses the 185-byte
/// feature report 0x52 (protocol as documented by the OpenRGB project; this is our own implementation).
/// The whole state of the board's lights is one report: read it, change our headers' blocks, send it back.
/// Only boards on the tested list are driven (a wrong report could upset the controller's firmware).
/// </summary>
internal sealed class MsiMysticLight : ILightDevice
{
    private const ushort Vendor = 0x1462;
    private const byte ReportId = 0x52;
    private const int ReportLength = 185;
    private const int SaveFlag = 184;

    // Effect values of the controller.
    private const byte EffectOff = 0;
    private const byte EffectStatic = 1;
    private const byte EffectBreathing = 2;
    private const byte EffectRainbowWave = 26;
    private const byte CustomColorFlag = 0x80;

    private static readonly Dictionary<byte, string> OtherEffects = new()
    {
        [3] = "Flashing", [4] = "Double flashing", [5] = "Lightning", [6] = "Marquee", [7] = "Meteor",
        [8] = "Water drop", [9] = "Rainbow", [15] = "Color ring", [16] = "Planetary", [17] = "Double meteor",
        [18] = "Energy", [19] = "Blink", [20] = "Clock", [21] = "Color pulse", [22] = "Color shift",
        [23] = "Color wave", [24] = "Marquee", [25] = "Rainbow", [27] = "Visor", [29] = "Rainbow flashing",
        [30] = "Rainbow double flashing", [31] = "Random", [36] = "Stack", [38] = "Fire", [39] = "Lava",
    };

    /// <summary>Header blocks in the report, per tested board.</summary>
    private sealed record Header(string Id, int Offset, string Name, string Detail);

    private static readonly Dictionary<ushort, (string Board, Header[] Headers)> Boards = new()
    {
        [0x7D25] = ("PRO Z690-A WIFI DDR4",
        [
            new("jrainbow1", 31, "ARGB header 1", "JRAINBOW1 · ARGB 5 V"),
            new("jrainbow2", 42, "ARGB header 2", "JRAINBOW2 · ARGB 5 V"),
            new("jrgb1", 1, "RGB header", "JRGB1 · RGB 12 V"),
        ]),
    };

    private readonly SafeFileHandle _handle;
    private readonly string _prefix;
    private readonly Header[] _headers;
    private readonly List<LightInfo> _lights = [];

    private MsiMysticLight(SafeFileHandle handle, ushort product, string board, Header[] headers)
    {
        _handle = handle;
        _headers = headers;
        _prefix = $"msi-{product:x4}/";
        foreach (var h in headers)
        {
            _lights.Add(new LightInfo
            {
                Key = _prefix + h.Id,
                Name = h.Name,
                Detail = h.Detail,
                Device = $"MSI {board}",
                Kind = "Header",
                Effects = [LightEffect.Static, LightEffect.Breathing, LightEffect.Rainbow, LightEffect.Temperature, LightEffect.Off],
            });
        }
    }

    public IReadOnlyList<LightInfo> Lights => _lights;

    /// <summary>Finds a tested MSI board's controller, or null.</summary>
    public static MsiMysticLight? TryOpen()
    {
        foreach (var path in Hid.Paths())
        {
            if (!Hid.TryParseIds(path, out ushort vid, out ushort pid) || vid != Vendor
                || !Boards.TryGetValue(pid, out var board))
            {
                continue;
            }
            var handle = Hid.Open(path);
            if (handle is null)
            {
                continue;
            }
            var device = new MsiMysticLight(handle, pid, board.Board, board.Headers);
            if (device.Read() is not null)
            {
                return device;
            }
            handle.Dispose();
        }
        return null;
    }

    public void Refresh()
    {
        var report = Read();
        if (report is null)
        {
            return;
        }
        for (int i = 0; i < _headers.Length; i++)
        {
            int o = _headers[i].Offset;
            byte effect = report[o];
            bool custom = (report[o + 8] & CustomColorFlag) != 0;
            var found = new LightSetting
            {
                Color = LightColor.ToHex(report[o + 1], report[o + 2], report[o + 3]),
                Brightness = Math.Min(10, (report[o + 4] >> 2) & 0x1F) * 10,
                Speed = (report[o + 4] & 3) switch { 0 => LightSpeed.Slow, 1 => LightSpeed.Medium, _ => LightSpeed.Fast },
            };
            LightEffect? known = effect switch
            {
                EffectOff => LightEffect.Off,
                EffectStatic => LightEffect.Static,
                EffectBreathing => LightEffect.Breathing,
                EffectRainbowWave when !custom => LightEffect.Rainbow,
                _ => null,
            };
            if (known is { } k)
            {
                found.Effect = k;
                _lights[i].Found = found;
                _lights[i].FoundOther = null;
            }
            else
            {
                _lights[i].Found = null;
                _lights[i].FoundOther = OtherEffects.GetValueOrDefault(effect, $"Effect {effect}");
            }
        }
    }

    public void Apply(IReadOnlyDictionary<string, LightOutput> outputs, bool save)
    {
        var before = Read() ?? throw new IOException("Can't read the motherboard's lighting controller.");
        var report = (byte[])before.Clone();
        bool any = false;
        foreach (var h in _headers)
        {
            if (outputs.TryGetValue(_prefix + h.Id, out var output))
            {
                Write(report, h.Offset, output);
                any = true;
            }
        }
        if (!any)
        {
            return;
        }

        if (save)
        {
            // Like MSI's own software: the current state, then the new one, both flagged to be stored.
            before[SaveFlag] = 1;
            report[SaveFlag] = 1;
            Send(before);
        }
        else
        {
            report[SaveFlag] = 0;
        }
        Send(report);
    }

    private static void Write(byte[] report, int o, LightOutput output)
    {
        byte brightness = (byte)Math.Clamp(Math.Round(output.Brightness / 10), 0, 10);
        byte speed = output.Speed switch { LightSpeed.Slow => 0, LightSpeed.Medium => 1, _ => 2 };
        switch (output.Effect)
        {
            case LightEffect.Off:
                report[o] = EffectOff;
                return;
            case LightEffect.Rainbow:
                report[o] = EffectRainbowWave;
                report[o + 8] = (byte)(report[o + 8] & ~CustomColorFlag);
                break;
            default:
                report[o] = output.Effect == LightEffect.Breathing ? EffectBreathing : EffectStatic;
                report[o + 1] = report[o + 5] = output.R;
                report[o + 2] = report[o + 6] = output.G;
                report[o + 3] = report[o + 7] = output.B;
                report[o + 8] = (byte)(report[o + 8] | CustomColorFlag);
                break;
        }
        report[o + 4] = (byte)((report[o + 4] & 0x80) | (brightness << 2) | speed);
    }

    private byte[]? Read()
    {
        var buffer = new byte[Math.Max(ReportLength, Hid.FeatureLength(_handle))];
        buffer[0] = ReportId;
        if (!Hid.GetFeature(_handle, buffer) || buffer[0] != ReportId)
        {
            return null;
        }
        return buffer[..ReportLength];
    }

    private void Send(byte[] report)
    {
        if (!Hid.SetFeature(_handle, report))
        {
            throw new IOException($"The motherboard's lighting controller refused the change (error {Marshal.GetLastWin32Error()}).");
        }
    }

    public void Dispose() => _handle.Dispose();
}

/// <summary>Plain Windows HID access (no extra libraries).</summary>
internal static class Hid
{
    public static IEnumerable<string> Paths()
    {
        HidD_GetHidGuid(out Guid guid);
        if (CM_Get_Device_Interface_List_Size(out int length, ref guid, null, 0) != 0 || length <= 1)
        {
            yield break;
        }
        var buffer = new char[length];
        if (CM_Get_Device_Interface_List(ref guid, null, buffer, length, 0) != 0)
        {
            yield break;
        }
        foreach (var path in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            yield return path;
        }
    }

    /// <summary>Vendor and product id from a path like \\?\HID#VID_1462&amp;PID_7D25#…</summary>
    public static bool TryParseIds(string path, out ushort vid, out ushort pid)
    {
        vid = pid = 0;
        string upper = path.ToUpperInvariant();
        int v = upper.IndexOf("VID_", StringComparison.Ordinal);
        int p = upper.IndexOf("PID_", StringComparison.Ordinal);
        return v >= 0 && p >= 0 && v + 8 <= upper.Length && p + 8 <= upper.Length
            && ushort.TryParse(upper.AsSpan(v + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out vid)
            && ushort.TryParse(upper.AsSpan(p + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out pid);
    }

    public static SafeFileHandle? Open(string path)
    {
        // Shared, so vendor software that has it open keeps working.
        var handle = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }
        return handle;
    }

    public static int FeatureLength(SafeFileHandle handle)
    {
        if (!HidD_GetPreparsedData(handle, out IntPtr data))
        {
            return 0;
        }
        try
        {
            return HidP_GetCaps(data, out var caps) == 0x00110000 ? caps.FeatureReportByteLength : 0;   // HIDP_STATUS_SUCCESS
        }
        finally
        {
            HidD_FreePreparsedData(data);
        }
    }

    public static bool GetFeature(SafeFileHandle handle, byte[] buffer) => HidD_GetFeature(handle, buffer, buffer.Length);

    public static bool SetFeature(SafeFileHandle handle, byte[] buffer) => HidD_SetFeature(handle, buffer, buffer.Length);

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid guid);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetFeature(SafeFileHandle handle, byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);

    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(IntPtr data);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr data, out HidpCaps caps);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_Size(out int length, ref Guid guid, string? deviceId, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List(ref Guid guid, string? deviceId, char[] buffer, int length, int flags);
}
