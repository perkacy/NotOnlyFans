using System.Runtime.InteropServices;

namespace NotOnlyFans.Service;

/// <summary>
/// NVIDIA's driver interface (nvapi64.dll), shared by the graphics card light (I²C bus) and overclocking
/// (P-state clock offsets). Functions are looked up by id; a missing driver or function means "not available".
/// </summary>
internal static class NvApi
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr QueryInterfaceFn(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitializeFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumPhysicalGpusFn([Out] IntPtr[] handles, out int count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetPciIdentifiersFn(IntPtr gpu, out uint deviceId, out uint subSystemId, out uint revisionId, out uint extDeviceId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetBusIdFn(IntPtr gpu, out uint busId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetFullNameFn(IntPtr gpu, [Out] byte[] name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int I2cFn(IntPtr gpu, ref I2cInfoV3 info, ref uint unknown);

    [StructLayout(LayoutKind.Sequential)]
    private struct I2cInfoV3
    {
        public uint Version;
        public uint DisplayMask;
        public byte IsDdcPort;
        public byte DeviceAddress;
        public IntPtr RegisterAddress;
        public uint RegisterAddressSize;
        public IntPtr Data;
        public uint Size;
        public uint Speed;
        public uint SpeedKhz;
        public byte PortId;
        public uint IsPortIdSet;
    }

    /// <summary>nvapi_QueryInterface after NvAPI_Initialize succeeded; null without an NVIDIA driver.</summary>
    private static readonly Lazy<QueryInterfaceFn?> Query = new(() =>
    {
        if (!NativeLibrary.TryLoad("nvapi64.dll", out IntPtr lib)
            || !NativeLibrary.TryGetExport(lib, "nvapi_QueryInterface", out IntPtr qi))
        {
            return null;
        }
        var query = Marshal.GetDelegateForFunctionPointer<QueryInterfaceFn>(qi);
        var init = Get<InitializeFn>(query, 0x0150E828);
        return init is not null && init() == 0 ? query : null;
    });

    private static T? Get<T>(QueryInterfaceFn query, uint id) where T : Delegate =>
        query(id) is var p && p != IntPtr.Zero ? Marshal.GetDelegateForFunctionPointer<T>(p) : null;

    /// <summary>An NVAPI function by id (null without the driver or when this driver doesn't have it).</summary>
    public static T? Function<T>(uint id) where T : Delegate =>
        Query.Value is { } query ? Get<T>(query, id) : null;

    private static readonly Lazy<(EnumPhysicalGpusFn Enum, GetPciIdentifiersFn Pci, I2cFn Write, I2cFn Read)?> I2cApi = new(() =>
    {
        var enumGpus = Function<EnumPhysicalGpusFn>(0xE5AC921F);
        var pci = Function<GetPciIdentifiersFn>(0x2DDFB66E);
        var write = Function<I2cFn>(0x283AC65A);
        var read = Function<I2cFn>(0x4D7B0709);
        return enumGpus is null || pci is null || write is null || read is null ? null : (enumGpus, pci, write, read);
    });

    /// <summary>One NVIDIA card: its NVAPI handle, PCI bus number and name.</summary>
    public sealed record Gpu(IntPtr Handle, uint Bus, string Name);

    /// <summary>The NVIDIA cards in this PC (none without the driver).</summary>
    public static List<Gpu> PhysicalGpus()
    {
        var list = new List<Gpu>();
        var enumGpus = Function<EnumPhysicalGpusFn>(0xE5AC921F);
        if (enumGpus is null)
        {
            return list;
        }
        var handles = new IntPtr[64];
        if (enumGpus(handles, out int count) != 0)
        {
            return list;
        }
        var busId = Function<GetBusIdFn>(0x1BE0B8E5);
        var fullName = Function<GetFullNameFn>(0xCEEE8E9F);
        for (int i = 0; i < count; i++)
        {
            uint bus = busId is not null && busId(handles[i], out uint b) == 0 ? b : (uint)i;
            var name = new byte[64];
            string text = fullName is not null && fullName(handles[i], name) == 0
                ? System.Text.Encoding.ASCII.GetString(name).TrimEnd('\0').Trim()
                : "NVIDIA graphics card";
            list.Add(new Gpu(handles[i], bus, text.StartsWith("NVIDIA", StringComparison.OrdinalIgnoreCase) ? text : "NVIDIA " + text));
        }
        return list;
    }

    /// <summary>The cards' I²C buses (for the light controllers on them).</summary>
    public static IEnumerable<I2cBus> Gpus()
    {
        if (I2cApi.Value is not { } api)
        {
            yield break;
        }
        var handles = new IntPtr[64];
        if (api.Enum(handles, out int count) != 0)
        {
            yield break;
        }
        for (int i = 0; i < count; i++)
        {
            if (api.Pci(handles[i], out uint device, out uint sub, out _, out _) == 0)
            {
                yield return new I2cBus(handles[i], (ushort)device, (ushort)(device >> 16), (ushort)sub, (ushort)(sub >> 16));
            }
        }
    }

    /// <summary>One graphics card's I²C bus (port 1, the one board makers put their RGB controllers on).</summary>
    public sealed class I2cBus(IntPtr gpu, ushort vendor, ushort device, ushort subVendor, ushort subDevice)
    {
        public ushort Vendor => vendor;
        public ushort Device => device;
        public ushort SubVendor => subVendor;
        public ushort SubDevice => subDevice;

        public bool WriteByte(byte address, byte value) => Transfer(address, value, write: true, out _);

        public int ReadByte(byte address) => Transfer(address, 0, write: false, out byte value) ? value : -1;

        private bool Transfer(byte address, byte value, bool write, out byte result)
        {
            result = 0;
            if (I2cApi.Value is not { } api)
            {
                return false;
            }
            IntPtr data = Marshal.AllocHGlobal(1);
            IntPtr register = Marshal.AllocHGlobal(1);
            try
            {
                Marshal.WriteByte(data, value);
                Marshal.WriteByte(register, value);
                var info = new I2cInfoV3
                {
                    Version = (uint)((3 << 16) | Marshal.SizeOf<I2cInfoV3>()),
                    IsDdcPort = 0,
                    DeviceAddress = (byte)(address << 1),
                    RegisterAddress = register,
                    RegisterAddressSize = 0,   // a plain byte, no register
                    Data = data,
                    Size = 1,
                    Speed = 0xFFFF,            // "use SpeedKhz"
                    SpeedKhz = 0,              // driver default
                    PortId = 1,
                    IsPortIdSet = 1,
                };
                uint unknown = 0;
                int status = write ? api.Write(gpu, ref info, ref unknown) : api.Read(gpu, ref info, ref unknown);
                result = Marshal.ReadByte(data);
                return status == 0;
            }
            finally
            {
                Marshal.FreeHGlobal(data);
                Marshal.FreeHGlobal(register);
            }
        }
    }
}
