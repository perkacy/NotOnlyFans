using System.Runtime.InteropServices;
using NotOnlyFans.Contracts;

namespace NotOnlyFans.Service.Overclock;

/// <summary>
/// One NVIDIA card's overclocking, the way MSI Afterburner and NVIDIA Inspector do it:
/// clock offsets of the top performance state (P0) through NVAPI "P-states 2.0"; the power limit through
/// NVML (nvml.dll, part of the driver, as LACT and nvidia-smi use it). Ranges always come from the driver,
/// so a value the card can't take is never sent. Needs administrator rights (the service runs as Local System).
/// </summary>
internal sealed class NvidiaCard : IOcCard
{
    private const int GraphicsDomain = 0;
    private const int MemoryDomain = 4;

    // NV_GPU_PERF_PSTATES20_INFO_V1: 20-byte header, 16 P-states of 456 bytes (8 clocks of 44, 4 voltages of 24).
    private const int PstatesSize = 7316;
    private const int PstateSize = 456;
    private const int ClockSize = 44;
    private const uint PstatesVersion = PstatesSize | (1 << 16);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PstatesFn(IntPtr gpu, [In, Out] byte[] info);

    private static readonly Lazy<PstatesFn?> GetPstates = new(() => NvApi.Function<PstatesFn>(0x6FF81213));
    private static readonly Lazy<PstatesFn?> SetPstates = new(() => NvApi.Function<PstatesFn>(0x0F4DAE6B));

    private readonly NvApi.Gpu _gpu;
    private readonly IntPtr _nvml;   // NVML device handle, zero if NVML is missing

    private NvidiaCard(NvApi.Gpu gpu, IntPtr nvml)
    {
        _gpu = gpu;
        _nvml = nvml;
    }

    public string Key => $"nvidia/pci-{_gpu.Bus}";

    public string Name => _gpu.Name;

    /// <summary>The NVIDIA cards in this PC (none without the driver).</summary>
    public static List<NvidiaCard> Find()
    {
        var gpus = NvApi.PhysicalGpus();
        var cards = new List<NvidiaCard>();
        if (gpus.Count == 0)
        {
            return cards;
        }
        var nvmlByBus = Nvml.DevicesByBus();
        foreach (var gpu in gpus)
        {
            cards.Add(new NvidiaCard(gpu, nvmlByBus.GetValueOrDefault(gpu.Bus)));
        }
        return cards;
    }

    /// <summary>What can be changed and what runs now.</summary>
    public OcGpu Read()
    {
        var info = new OcGpu { Key = Key, Name = Name };
        var pstates = ReadPstates();
        if (pstates is null)
        {
            info.Unsupported = "The graphics driver doesn't allow changing the clocks of this card.";
        }
        else if (Clock(pstates, GraphicsDomain) is { } core)
        {
            info.CoreOffset = core.Delta;
            (info.CoreMin, info.CoreMax) = core.Editable ? (core.Min, core.Max) : (0, 0);
            info.BoostClock = core.Top;
            if (Clock(pstates, MemoryDomain) is { } memory)
            {
                info.MemoryOffset = memory.Delta;
                (info.MemoryMin, info.MemoryMax) = memory.Editable ? (memory.Min, memory.Max) : (0, 0);
                info.MemoryClock = memory.Top;
            }
        }

        if (_nvml != IntPtr.Zero && Nvml.PowerLimits(_nvml) is { } power)
        {
            (info.PowerMin, info.PowerMax, info.PowerDefault, info.PowerLimit) = power;
        }
        if (info.Unsupported is null && info.CoreMax == info.CoreMin && info.MemoryMax == info.MemoryMin && info.PowerMax <= info.PowerMin)
        {
            info.Unsupported = "This card doesn't allow changing clocks or power.";
        }
        return info;
    }

    /// <summary>Sends the setting to the card; null on success, else a message for the user.</summary>
    public string? Apply(OcSetting setting)
    {
        var limits = Read();
        if (limits.Unsupported is { } why)
        {
            return why;
        }
        string? error = null;
        if (limits.CoreMax > limits.CoreMin || limits.MemoryMax > limits.MemoryMin)
        {
            int core = Math.Clamp(setting.CoreOffset, limits.CoreMin, limits.CoreMax);
            int memory = Math.Clamp(setting.MemoryOffset, limits.MemoryMin, limits.MemoryMax);
            if (!WriteOffsets(core, memory, limits))
            {
                error = "The graphics driver didn't take the clock offsets.";
            }
        }
        if (limits.PowerMax > limits.PowerMin)
        {
            float watts = Math.Clamp(setting.PowerLimit ?? limits.PowerDefault, limits.PowerMin, limits.PowerMax);
            if (!Nvml.SetPowerLimit(_nvml, watts))
            {
                error ??= "The graphics driver didn't take the power limit.";
            }
        }
        return error;
    }

    private bool WriteOffsets(int core, int memory, OcGpu limits)
    {
        if (SetPstates.Value is not { } set)
        {
            return false;
        }
        // One P-state (P0) with the clocks that can change; everything else in the request stays zero.
        var request = new byte[PstatesSize];
        BitConverter.GetBytes(PstatesVersion).CopyTo(request, 0);
        int clocks = 0;
        int p0 = 20;
        void Add(int domain, int deltaMHz)
        {
            int e = p0 + 8 + clocks * ClockSize;
            BitConverter.GetBytes(domain).CopyTo(request, e);
            BitConverter.GetBytes(deltaMHz * 1000).CopyTo(request, e + 12);
            clocks++;
        }
        if (limits.CoreMax > limits.CoreMin)
        {
            Add(GraphicsDomain, core);
        }
        if (limits.MemoryMax > limits.MemoryMin)
        {
            Add(MemoryDomain, memory);
        }
        BitConverter.GetBytes(1).CopyTo(request, 8);        // numPstates
        BitConverter.GetBytes(clocks).CopyTo(request, 12);  // numClocks
        BitConverter.GetBytes(0).CopyTo(request, p0);       // pstateId = P0
        if (set(_gpu.Handle, request) != 0)
        {
            return false;
        }
        // Read back: the driver may round or refuse a part silently.
        var now = Read();
        return now.CoreOffset == (limits.CoreMax > limits.CoreMin ? core : now.CoreOffset)
            && now.MemoryOffset == (limits.MemoryMax > limits.MemoryMin ? memory : now.MemoryOffset);
    }

    private byte[]? ReadPstates()
    {
        if (GetPstates.Value is not { } get)
        {
            return null;
        }
        var info = new byte[PstatesSize];
        BitConverter.GetBytes(PstatesVersion).CopyTo(info, 0);
        return get(_gpu.Handle, info) == 0 ? info : null;
    }

    private readonly record struct ClockInfo(bool Editable, int Delta, int Min, int Max, int Top);

    /// <summary>A clock of P0 (the top performance state) by domain, in MHz.</summary>
    private static ClockInfo? Clock(byte[] info, int domain)
    {
        int pstates = BitConverter.ToInt32(info, 8);
        int clocks = Math.Min(BitConverter.ToInt32(info, 12), 8);
        for (int p = 0; p < Math.Min(pstates, 16); p++)
        {
            int o = 20 + p * PstateSize;
            if (BitConverter.ToInt32(info, o) != 0)
            {
                continue;   // only P0
            }
            for (int c = 0; c < clocks; c++)
            {
                int e = o + 8 + c * ClockSize;
                if (BitConverter.ToInt32(info, e) != domain)
                {
                    continue;
                }
                bool editable = (BitConverter.ToUInt32(info, o + 4) & 1) != 0 && (BitConverter.ToUInt32(info, e + 8) & 1) != 0;
                int type = BitConverter.ToInt32(info, e + 4);
                // Single frequency (type 0): freq_kHz; range (type 1): min, max.
                int top = (int)(BitConverter.ToUInt32(info, type == 1 ? e + 28 : e + 24) / 1000);
                return new ClockInfo(editable, BitConverter.ToInt32(info, e + 12) / 1000,
                    BitConverter.ToInt32(info, e + 16) / 1000, BitConverter.ToInt32(info, e + 20) / 1000, top);
            }
        }
        return null;
    }

    /// <summary>NVML: just what the power limit needs.</summary>
    private static class Nvml
    {
        private const string Dll = "nvml.dll";

        [DllImport(Dll)] private static extern int nvmlInit_v2();
        [DllImport(Dll)] private static extern int nvmlDeviceGetCount_v2(out uint count);
        [DllImport(Dll)] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport(Dll)] private static extern int nvmlDeviceGetPciInfo_v3(IntPtr device, [Out] byte[] pci);
        [DllImport(Dll)] private static extern int nvmlDeviceGetPowerManagementLimit(IntPtr device, out uint milliwatts);
        [DllImport(Dll)] private static extern int nvmlDeviceGetPowerManagementDefaultLimit(IntPtr device, out uint milliwatts);
        [DllImport(Dll)] private static extern int nvmlDeviceGetPowerManagementLimitConstraints(IntPtr device, out uint min, out uint max);
        [DllImport(Dll)] private static extern int nvmlDeviceSetPowerManagementLimit(IntPtr device, uint milliwatts);

        private static readonly Lazy<bool> Ready = new(() =>
        {
            try
            {
                return nvmlInit_v2() == 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        });

        /// <summary>NVML devices by PCI bus number (to match NVAPI's cards).</summary>
        public static Dictionary<uint, IntPtr> DevicesByBus()
        {
            var map = new Dictionary<uint, IntPtr>();
            if (!Ready.Value || nvmlDeviceGetCount_v2(out uint count) != 0)
            {
                return map;
            }
            for (uint i = 0; i < count; i++)
            {
                var pci = new byte[68];   // nvmlPciInfo_t: busIdLegacy[16], domain, bus, device, pciDeviceId, pciSubSystemId, busId[32]
                if (nvmlDeviceGetHandleByIndex_v2(i, out IntPtr device) == 0 && nvmlDeviceGetPciInfo_v3(device, pci) == 0)
                {
                    map[BitConverter.ToUInt32(pci, 20)] = device;
                }
            }
            return map;
        }

        /// <summary>Min, max, default and current power limit in W; null if the card can't tell.</summary>
        public static (float Min, float Max, float Default, float Now)? PowerLimits(IntPtr device)
        {
            if (nvmlDeviceGetPowerManagementLimitConstraints(device, out uint min, out uint max) != 0
                || nvmlDeviceGetPowerManagementDefaultLimit(device, out uint def) != 0
                || nvmlDeviceGetPowerManagementLimit(device, out uint now) != 0)
            {
                return null;
            }
            return (min / 1000f, max / 1000f, def / 1000f, now / 1000f);
        }

        public static bool SetPowerLimit(IntPtr device, float watts) =>
            device != IntPtr.Zero && nvmlDeviceSetPowerManagementLimit(device, (uint)Math.Round(watts * 1000)) == 0;
    }
}
