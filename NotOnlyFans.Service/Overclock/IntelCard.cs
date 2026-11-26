using System.Runtime.InteropServices;
using System.Text;
using NotOnlyFans.Contracts;

namespace NotOnlyFans.Service.Overclock;

/// <summary>
/// An Intel Arc card through IGCL, Intel's Graphics Control Library (ControlLib.dll, part of the Intel driver):
/// the GPU clock offset and the sustained power limit (watts on Arc A, % of the default on Arc B), as Intel
/// Graphics Software offers them. Memory speed is not changed yet.
/// Intel asks overclocking apps to get the user's OK to the risk once before overclocking (its "warranty waiver"):
/// the app asks (<see cref="OcGpu.NeedsConsent"/>), and only then is the waiver set.
/// Not tested on an Arc card by the author yet: every call is checked, ranges come from the driver, and the
/// first look and each apply run under <see cref="NativeGuard"/>. Structure layouts from igcl_api.h
/// (github.com/intel/drivers.gpu.control-library).
/// </summary>
internal sealed unsafe class IntelCard : IOcCard
{
    public const string Library = "intel-igcl";

    private const int Success = 0;
    private const int UnsupportedVersion = 0x40000009;
    private const int UnsupportedFeature = 0x4000000a;
    private const int InvalidSize = 0x4000000f;
    private const int DeviceTypeGraphics = 1;
    private const uint FlagIntegrated = 1;
    private const int UnitsPercent = 11;   // ctl_units_t: CTL_UNITS_PERCENT (watts are CTL_UNITS_POWER_WATTS = 4)

    // ctl_device_adapter_properties_t (version 2): name at 88, adapter properties at 188, PCI bus at 200; 320 bytes.
    private const int AdapterPropertiesSize = 320;
    // ctl_oc_properties_t: bSupported at 5, then ctl_oc_control_info_t of 48 bytes from 8 – nine in version 1 (440 bytes),
    // six in version 0 (296 bytes, older drivers: they refuse the longer one). What is read here is in both.
    private const int OcPropertiesSize = 440;
    private const int OcPropertiesSizeV0 = 296;
    private const int GpuFrequencyOffsetAt = 8;
    private const int PowerLimitAt = 200;

    private readonly IntPtr _device;   // ctl_device_adapter_handle_t

    private IntelCard(IntPtr device, string key, string name)
    {
        _device = device;
        Key = key;
        Name = name;
    }

    public string Key { get; }

    public string Name { get; }

    /// <summary>The Intel graphics cards in this PC. Integrated graphics are left out unless asked (a test).</summary>
    public static List<IntelCard> Find(bool withIntegrated = false)
    {
        var cards = new List<IntelCard>();
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 || Igcl.Api() is not { } api)
        {
            return cards;
        }
        uint count = 0;
        if (Igcl.EnumerateDevices(api, &count, null) != Success || count == 0)
        {
            return cards;
        }
        var devices = new IntPtr[count];
        fixed (IntPtr* list = devices)
        {
            if (Igcl.EnumerateDevices(api, &count, list) != Success)
            {
                return cards;
            }
        }
        byte* p = stackalloc byte[AdapterPropertiesSize];
        for (int index = 0; index < count; index++)
        {
            new Span<byte>(p, AdapterPropertiesSize).Clear();
            long luid = 0;
            *(uint*)p = AdapterPropertiesSize;   // Size
            p[4] = 2;                            // Version
            *(long**)(p + 8) = &luid;            // pDeviceID (a LUID)
            *(uint*)(p + 16) = sizeof(long);     // device_id_size
            var device = devices[index];
            if (Igcl.GetDeviceProperties(device, p) != Success || *(int*)(p + 20) != DeviceTypeGraphics)
            {
                continue;
            }
            bool integrated = (*(uint*)(p + 188) & FlagIntegrated) != 0;
            if (integrated && !withIntegrated)
            {
                continue;
            }
            var nameBytes = new ReadOnlySpan<byte>(p + 88, 100);
            int end = nameBytes.IndexOf((byte)0);
            string name = Encoding.UTF8.GetString(end < 0 ? nameBytes : nameBytes[..end]).Trim();
            // Older drivers leave the PCI address out (0xFF): then the PCI device id and the order.
            byte bus = p[200];
            string key = bus != 0xFF ? $"intel/pci-{bus}" : $"intel/{*(uint*)(p + 68):x4}-{index}";
            cards.Add(new IntelCard(device, key, name.Length > 0 ? name : "Intel Graphics"));
        }
        return cards;
    }

    private readonly record struct Control(bool Supported, int Units, double Min, double Max, double Step, double Default);

    /// <summary>ctlOverclockGetProperties: whether overclocking works at all, the GPU clock offset and the power limit.</summary>
    private (bool Supported, Control Clock, Control Power)? Properties(out int result)
    {
        byte* p = stackalloc byte[OcPropertiesSize];
        result = Ask(OcPropertiesSize, 1);
        if (result is InvalidSize or UnsupportedVersion)
        {
            result = Ask(OcPropertiesSizeV0, 0);
        }
        if (result != Success)
        {
            return null;
        }
        int Ask(int size, byte version)
        {
            new Span<byte>(p, OcPropertiesSize).Clear();
            *(uint*)p = (uint)size;
            p[4] = version;
            return Igcl.OverclockGetProperties(_device, p);
        }
        static Control At(byte* c) => new(c[0] != 0, *(int*)(c + 4), *(double*)(c + 8), *(double*)(c + 16), *(double*)(c + 24), *(double*)(c + 32));
        return (p[5] != 0, At(p + GpuFrequencyOffsetAt), At(p + PowerLimitAt));
    }

    public OcGpu Read()
    {
        var info = new OcGpu
        {
            Key = Key,
            Name = Name,
            NeedsConsent = true,
            MemoryNote = "NotOnlyFans doesn't change the memory speed of Intel cards yet.",
        };
        if (Properties(out int result) is not { } props)
        {
            info.Unsupported = result == UnsupportedFeature
                ? "This graphics card doesn't allow overclocking."
                : "The Intel driver didn't answer. Updating it may help.";
            return info;
        }
        if (!props.Supported)
        {
            info.Unsupported = "This graphics card doesn't allow overclocking.";
            return info;
        }

        double offset;
        if (props.Clock.Supported && props.Clock.Max > props.Clock.Min && Igcl.FrequencyOffsetGet(_device, &offset) == Success)
        {
            (info.CoreMin, info.CoreMax) = ((int)Math.Ceiling(props.Clock.Min), (int)Math.Floor(props.Clock.Max));
            info.CoreOffset = (int)Math.Round(offset);
        }
        else
        {
            info.CoreNote = "This card or its driver doesn't allow a GPU clock offset.";
        }

        double limit;
        if (props.Power.Supported && props.Power.Max > props.Power.Min && Igcl.PowerLimitGet != null
            && Igcl.PowerLimitGet(_device, &limit) == Success)
        {
            info.PowerUnit = props.Power.Units == UnitsPercent ? OcUnits.Percent : OcUnits.Watts;
            (info.PowerMin, info.PowerMax, info.PowerDefault, info.PowerLimit) =
                ((float)props.Power.Min, (float)props.Power.Max, (float)props.Power.Default, (float)limit);
        }
        else
        {
            info.PowerNote = Igcl.PowerLimitGet == null
                ? "Update the Intel driver: this one can't change the power limit through NotOnlyFans."
                : "This card or its driver doesn't allow changing the power limit.";
        }

        if (info.CoreMax == info.CoreMin && info.PowerMax <= info.PowerMin)
        {
            info.Unsupported = "This card doesn't allow changing its clock or power limit.";
        }
        return info;
    }

    public string? Apply(OcSetting setting) => NativeGuard.Run(Library, () =>
    {
        var limits = Read();
        if (limits.Unsupported is { } why)
        {
            return why;
        }
        bool defaults = setting.CoreOffset == 0 && setting.PowerLimit is null;
        if (!defaults)
        {
            if (!setting.RiskAccepted)
            {
                return "Overclocking this card needs your OK to the risk first.";
            }
            // Intel: the user accepted the risk (asked by the app), so the waiver may be set.
            if (Igcl.OverclockWaiverSet(_device) != Success)
            {
                return "The Intel driver didn't allow overclocking.";
            }
        }
        string? error = null;
        if (limits.CoreMax > limits.CoreMin
            && Igcl.FrequencyOffsetSet(_device, Math.Clamp(setting.CoreOffset, limits.CoreMin, limits.CoreMax)) != Success)
        {
            error = "The Intel driver didn't take the GPU clock offset.";
        }
        if (limits.PowerMax > limits.PowerMin && Igcl.PowerLimitSet != null
            && Igcl.PowerLimitSet(_device, Math.Clamp(setting.PowerLimit ?? limits.PowerDefault, limits.PowerMin, limits.PowerMax)) != Success)
        {
            error ??= "The Intel driver didn't take the power limit.";
        }
        return error;
    });

    /// <summary>The IGCL functions used here (cdecl exports of ControlLib.dll).</summary>
    private static class Igcl
    {
        private static IntPtr _api;
        private static bool _tried;

        public static delegate* unmanaged[Cdecl]<IntPtr, uint*, IntPtr*, int> EnumerateDevices;
        public static delegate* unmanaged[Cdecl]<IntPtr, byte*, int> GetDeviceProperties;
        public static delegate* unmanaged[Cdecl]<IntPtr, byte*, int> OverclockGetProperties;
        public static delegate* unmanaged[Cdecl]<IntPtr, int> OverclockWaiverSet;
        public static delegate* unmanaged[Cdecl]<IntPtr, double*, int> FrequencyOffsetGet;
        public static delegate* unmanaged[Cdecl]<IntPtr, double, int> FrequencyOffsetSet;
        public static delegate* unmanaged[Cdecl]<IntPtr, double*, int> PowerLimitGet;   // null with older drivers
        public static delegate* unmanaged[Cdecl]<IntPtr, double, int> PowerLimitSet;

        /// <summary>The IGCL handle, or null without the Intel driver.</summary>
        public static IntPtr? Api()
        {
            if (!_tried)
            {
                _tried = true;
                _api = Start();
            }
            return _api == IntPtr.Zero ? null : _api;
        }

        private static IntPtr Start()
        {
            // Only from System32, where the Intel driver puts it.
            string path = Path.Combine(Environment.SystemDirectory, "ControlLib.dll");
            if (!File.Exists(path) || !NativeLibrary.TryLoad(path, out IntPtr library))
            {
                return IntPtr.Zero;
            }
            IntPtr Export(string name) => NativeLibrary.TryGetExport(library, name, out IntPtr f) ? f : IntPtr.Zero;
            IntPtr init = Export("ctlInit");
            EnumerateDevices = (delegate* unmanaged[Cdecl]<IntPtr, uint*, IntPtr*, int>)Export("ctlEnumerateDevices");
            GetDeviceProperties = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)Export("ctlGetDeviceProperties");
            OverclockGetProperties = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)Export("ctlOverclockGetProperties");
            OverclockWaiverSet = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export("ctlOverclockWaiverSet");
            // Version 2 of the clock offset functions where the driver has them (same unit, MHz).
            IntPtr getV2 = Export("ctlOverclockGpuFrequencyOffsetGetV2"), setV2 = Export("ctlOverclockGpuFrequencyOffsetSetV2");
            FrequencyOffsetGet = (delegate* unmanaged[Cdecl]<IntPtr, double*, int>)(getV2 != IntPtr.Zero ? getV2 : Export("ctlOverclockGpuFrequencyOffsetGet"));
            FrequencyOffsetSet = (delegate* unmanaged[Cdecl]<IntPtr, double, int>)(setV2 != IntPtr.Zero ? setV2 : Export("ctlOverclockGpuFrequencyOffsetSet"));
            // The power limit only in version 2: it uses the unit the driver tells (version 1 is in mW with ranges in another unit).
            PowerLimitGet = (delegate* unmanaged[Cdecl]<IntPtr, double*, int>)Export("ctlOverclockPowerLimitGetV2");
            PowerLimitSet = (delegate* unmanaged[Cdecl]<IntPtr, double, int>)Export("ctlOverclockPowerLimitSetV2");
            if (PowerLimitGet == null || PowerLimitSet == null)
            {
                PowerLimitGet = null;
                PowerLimitSet = null;
            }
            if (init == IntPtr.Zero || EnumerateDevices == null || GetDeviceProperties == null || OverclockGetProperties == null
                || OverclockWaiverSet == null || FrequencyOffsetGet == null || FrequencyOffsetSet == null)
            {
                return IntPtr.Zero;
            }

            // ctl_init_args_t: Size, Version (0), AppVersion 1.1, flags (none), SupportedVersion (out), ApplicationUID (zero); 36 bytes.
            byte* args = stackalloc byte[36];
            new Span<byte>(args, 36).Clear();
            *(uint*)args = 36;
            *(uint*)(args + 8) = (1u << 16) | 1u;
            IntPtr api;
            return ((delegate* unmanaged[Cdecl]<byte*, IntPtr*, int>)init)(args, &api) == Success ? api : IntPtr.Zero;
        }
    }
}
