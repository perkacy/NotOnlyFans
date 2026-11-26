using System.Runtime.InteropServices;
using NotOnlyFans.Contracts;

namespace NotOnlyFans.Service.Overclock;

/// <summary>
/// An AMD Radeon card through ADLX (amdadlx64.dll, part of the AMD driver): the "manual tuning" AMD Software offers
/// on Radeon RX 6000 cards and newer – the highest GPU clock, the highest memory clock and the power limit
/// (% from the default). Clocks are shown as offsets from the card's default highest clock, like on the other cards.
/// Not tested on real AMD hardware by the author yet: every call is checked, ranges come from the driver, and the
/// first look and each apply run under <see cref="NativeGuard"/>.
/// ADLX's C interface: an object is a pointer to a table of functions, in the order of the ADLX SDK headers
/// (github.com/GPUOpen-LibrariesAndSDKs/ADLX, SDK/Include) – the numbers below are positions in those tables.
/// </summary>
internal sealed unsafe class AmdCard : IOcCard
{
    public const string Library = "amd-adlx";

    // ADLX_RESULT: OK, ALREADY_ENABLED and ALREADY_INITIALIZED count as success.
    private static bool Ok(int result) => result is >= 0 and <= 2;

    // ADLX_GPU_TYPE
    private const int Integrated = 1;

    private readonly IntPtr _gfx;     // IADLXManualGraphicsTuning2_1
    private readonly IntPtr _vram;    // IADLXManualVRAMTuning2_1
    private readonly IntPtr _power;   // IADLXManualPowerTuning
    private readonly IntPtr _power1;  // IADLXManualPowerTuning1 (tells the default), may be zero
    private readonly string? _coreNote;
    private readonly string? _memoryNote;
    private readonly string? _powerNote;

    private AmdCard(string key, string name, IntPtr gfx, IntPtr vram, IntPtr power, IntPtr power1,
        string? coreNote, string? memoryNote, string? powerNote)
    {
        Key = key;
        Name = name;
        _gfx = gfx;
        _vram = vram;
        _power = power;
        _power1 = power1;
        _coreNote = coreNote;
        _memoryNote = memoryNote;
        _powerNote = powerNote;
    }

    public string Key { get; }

    public string Name { get; }

    /// <summary>The AMD cards in this PC (none without the AMD driver). Integrated graphics are left out.</summary>
    public static List<AmdCard> Find()
    {
        var cards = new List<AmdCard>();
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 || Adlx.System() is not { } system)
        {
            return cards;
        }
        IntPtr tuning;
        if (!Ok(Call(system, 8, &tuning)))   // IADLXSystem::GetGPUTuningServices
        {
            return cards;
        }
        IntPtr list;
        if (!Ok(Call(system, 1, &list)))     // IADLXSystem::GetGPUs
        {
            return cards;
        }
        try
        {
            uint count = ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Fn(list, 3))(list);   // Size
            for (uint i = 0; i < count; i++)
            {
                IntPtr gpu;
                if (!Ok(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Fn(list, 11))(list, i, &gpu)))   // At_GPUList
                {
                    continue;
                }
                int type;
                if (Ok(Call(gpu, 5, &type)) && type == Integrated)   // IADLXGPU::Type
                {
                    Release(gpu);
                    continue;
                }
                cards.Add(Open(tuning, gpu, i));   // keeps gpu (the tuning interfaces belong to it)
            }
        }
        finally
        {
            Release(list);
        }
        return cards;
    }

    private static AmdCard Open(IntPtr tuning, IntPtr gpu, uint index)
    {
        string name = Text(gpu, 7) ?? "AMD Radeon";                       // IADLXGPU::Name
        string? pnp = Text(gpu, 9);                                       // IADLXGPU::PNPString, "PCI\VEN_1002&...\<instance>"
        string key = "amd/" + (pnp is not null && pnp.LastIndexOf('\\') is int at and >= 0 ? pnp[(at + 1)..].ToLowerInvariant() : $"{index}");

        // GPU clock: IsSupportedManualGFXTuning (8), GetManualGFXTuning (14).
        IntPtr gfx = Tuning(tuning, gpu, 8, 14, "IADLXManualGraphicsTuning2_1", out string? coreNote,
            older: ["IADLXManualGraphicsTuning2", "IADLXManualGraphicsTuning1"]);
        // Memory clock: IsSupportedManualVRAMTuning (9), GetManualVRAMTuning (15).
        IntPtr vram = Tuning(tuning, gpu, 9, 15, "IADLXManualVRAMTuning2_1", out string? memoryNote,
            older: ["IADLXManualVRAMTuning2", "IADLXManualVRAMTuning1"]);
        // Power limit: IsSupportedManualPowerTuning (11), GetManualPowerTuning (17).
        IntPtr power = Tuning(tuning, gpu, 11, 17, "IADLXManualPowerTuning", out string? powerNote, older: []);
        IntPtr power1 = power == IntPtr.Zero ? IntPtr.Zero : Query(power, "IADLXManualPowerTuning1");
        return new AmdCard(key, name, gfx, vram, power, power1, coreNote, memoryNote, powerNote);
    }

    /// <summary>One kind of manual tuning as <paramref name="wanted"/>, or zero with the reason.</summary>
    private static IntPtr Tuning(IntPtr tuning, IntPtr gpu, int isSupported, int get, string wanted, out string? note, string[] older)
    {
        note = "This card doesn't allow changing it.";
        byte supported = 0;
        if (!Ok(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, byte*, int>)Fn(tuning, isSupported))(tuning, gpu, &supported)) || supported == 0)
        {
            return IntPtr.Zero;
        }
        IntPtr any;
        if (!Ok(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Fn(tuning, get))(tuning, gpu, &any)) || any == IntPtr.Zero)
        {
            note = "The AMD driver didn't answer.";
            return IntPtr.Zero;
        }
        try
        {
            IntPtr found = Query(any, wanted);
            if (found != IntPtr.Zero)
            {
                note = null;
                return found;
            }
            if (older.Length > 0 && Query(any, older[0]) is var newer && newer != IntPtr.Zero)
            {
                Release(newer);
                note = "Update the AMD driver: this one doesn't tell the card's default clocks.";
            }
            else
            {
                note = "This card uses AMD's older clock-step tuning, which NotOnlyFans doesn't support yet.";
            }
            return IntPtr.Zero;
        }
        finally
        {
            Release(any);
        }
    }

    public OcGpu Read()
    {
        var info = new OcGpu { Key = Key, Name = Name, PowerUnit = OcUnits.Percent };

        // GPU clock: GetGPUMaxFrequencyRange (6), GetGPUMaxFrequency (7), GetGPUMaxFrequencyDefault (13).
        if (Clock(_gfx, 6, 7, 13) is { } core)
        {
            (info.CoreMin, info.CoreMax) = (core.Range.Min - core.Default, core.Range.Max - core.Default);
            info.CoreOffset = core.Now - core.Default;
            info.BoostClock = core.Now;
        }
        else
        {
            info.CoreNote = _coreNote ?? "The AMD driver didn't tell the clock range.";
        }

        // Memory clock: GetMaxVRAMFrequencyRange (7), GetMaxVRAMFrequency (8), GetMaxVRAMFrequencyDefault (10).
        if (Clock(_vram, 7, 8, 10) is { } memory)
        {
            (info.MemoryMin, info.MemoryMax) = (memory.Range.Min - memory.Default, memory.Range.Max - memory.Default);
            info.MemoryOffset = memory.Now - memory.Default;
            info.MemoryClock = memory.Now;
        }
        else
        {
            info.MemoryNote = _memoryNote ?? "The AMD driver didn't tell the clock range.";
        }

        // Power limit, % from the default: GetPowerLimitRange (3), GetPowerLimit (4); IADLXManualPowerTuning1::GetPowerLimitDefault (10).
        if (_power != IntPtr.Zero && GetRange(_power, 3) is { } range && GetInt(_power, 4) is { } now)
        {
            (info.PowerMin, info.PowerMax, info.PowerLimit) = (range.Min, range.Max, now);
            info.PowerDefault = _power1 != IntPtr.Zero && GetInt(_power1, 10) is { } d ? d : 0;
        }
        else
        {
            info.PowerNote = _powerNote ?? "The AMD driver didn't tell the power limit range.";
        }

        if (info.CoreMax == info.CoreMin && info.MemoryMax == info.MemoryMin && info.PowerMax <= info.PowerMin)
        {
            info.Unsupported = _coreNote ?? "This card doesn't allow changing clocks or power through the AMD driver.";
        }
        return info;
    }

    public string? Apply(OcSetting setting) => NativeGuard.Run(Library, () =>
    {
        string? error = null;
        // SetGPUMaxFrequency (8) / SetMaxVRAMFrequency (9): the default highest clock plus the offset.
        if (Clock(_gfx, 6, 7, 13) is { } core && core.Range.Max > core.Range.Min
            && !Ok(SetInt(_gfx, 8, Fit(core.Default + setting.CoreOffset, core.Range))))
        {
            error = "The AMD driver didn't take the GPU clock.";
        }
        if (Clock(_vram, 7, 8, 10) is { } memory && memory.Range.Max > memory.Range.Min
            && !Ok(SetInt(_vram, 9, Fit(memory.Default + setting.MemoryOffset, memory.Range))))
        {
            error ??= "The AMD driver didn't take the memory clock.";
        }
        // SetPowerLimit (5).
        if (_power != IntPtr.Zero && GetRange(_power, 3) is { } range && range.Max > range.Min)
        {
            int def = _power1 != IntPtr.Zero && GetInt(_power1, 10) is { } d ? d : 0;
            int target = Fit((int)Math.Round(setting.PowerLimit ?? def), range);
            if (!Ok(SetInt(_power, 5, target)))
            {
                error ??= "The AMD driver didn't take the power limit.";
            }
        }
        return error;
    });

    private readonly record struct IntRange(int Min, int Max, int Step);

    private readonly record struct ClockInfo(IntRange Range, int Now, int Default);

    private static ClockInfo? Clock(IntPtr tuning, int range, int now, int @default) =>
        tuning != IntPtr.Zero && GetRange(tuning, range) is { } r && GetInt(tuning, now) is { } n && GetInt(tuning, @default) is { } d
            ? new ClockInfo(r, n, d)
            : null;

    /// <summary>Within the range, on its step.</summary>
    private static int Fit(int value, IntRange range)
    {
        value = Math.Clamp(value, range.Min, range.Max);
        return range.Step > 1 ? range.Min + (value - range.Min) / range.Step * range.Step : value;
    }

    // ----- Calling ADLX -----

    private static IntPtr Fn(IntPtr obj, int index) => ((IntPtr*)*(IntPtr*)obj)[index];

    private static int Call<T>(IntPtr obj, int index, T* result) where T : unmanaged =>
        ((delegate* unmanaged[Stdcall]<IntPtr, T*, int>)Fn(obj, index))(obj, result);

    private static int? GetInt(IntPtr obj, int index)
    {
        int value;
        return Ok(Call(obj, index, &value)) ? value : null;
    }

    private static int SetInt(IntPtr obj, int index, int value) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Fn(obj, index))(obj, value);

    private static IntRange? GetRange(IntPtr obj, int index)
    {
        IntRange range;
        return Ok(Call(obj, index, &range)) && range.Max >= range.Min ? range : null;
    }

    private static string? Text(IntPtr obj, int index)
    {
        IntPtr text;
        return Ok(Call(obj, index, &text)) && text != IntPtr.Zero ? Marshal.PtrToStringUTF8(text) : null;
    }

    /// <summary>IADLXInterface::QueryInterface (2): the object as another interface (acquired), or zero.</summary>
    private static IntPtr Query(IntPtr obj, string interfaceId)
    {
        IntPtr result = IntPtr.Zero;
        fixed (char* id = interfaceId)
        {
            return Ok(((delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr*, int>)Fn(obj, 2))(obj, id, &result)) ? result : IntPtr.Zero;
        }
    }

    /// <summary>IADLXInterface::Release (1).</summary>
    private static void Release(IntPtr obj)
    {
        if (obj != IntPtr.Zero)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)Fn(obj, 1))(obj);
        }
    }

    /// <summary>ADLX itself: started once, kept for the life of the service.</summary>
    private static class Adlx
    {
        private static IntPtr _system;
        private static bool _tried;

        /// <summary>IADLXSystem (not reference counted), or null without the AMD driver.</summary>
        public static IntPtr? System()
        {
            if (!_tried)
            {
                _tried = true;
                _system = Start();
            }
            return _system == IntPtr.Zero ? null : _system;
        }

        private static IntPtr Start()
        {
            // Only from System32, where the AMD driver puts it.
            string path = Path.Combine(Environment.SystemDirectory, "amdadlx64.dll");
            if (!File.Exists(path) || !NativeLibrary.TryLoad(path, out IntPtr library)
                || !NativeLibrary.TryGetExport(library, "ADLXQueryFullVersion", out IntPtr queryVersion)
                || !NativeLibrary.TryGetExport(library, "ADLXInitialize", out IntPtr initialize))
            {
                return IntPtr.Zero;
            }
            // Ask for the version the driver has: what is used here exists in every ADLX that has the interfaces
            // (checked one by one with QueryInterface).
            ulong version;
            if (!Ok(((delegate* unmanaged[Cdecl]<ulong*, int>)queryVersion)(&version)))
            {
                return IntPtr.Zero;
            }
            IntPtr system;
            return Ok(((delegate* unmanaged[Cdecl]<ulong, IntPtr*, int>)initialize)(version, &system)) ? system : IntPtr.Zero;
        }
    }
}
