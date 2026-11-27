using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace NotOnlyFans.Service.Lights;

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

    /// <summary>Paths of HID interfaces of one maker, with their product ids.</summary>
    public static IEnumerable<(string Path, ushort Product)> Find(ushort vendor)
    {
        foreach (var path in Paths())
        {
            if (TryParseIds(path, out ushort vid, out ushort pid) && vid == vendor)
            {
                yield return (path, pid);
            }
        }
    }

    public static SafeFileHandle? Open(string path, bool overlapped = false)
    {
        // Shared, so vendor software that has it open keeps working.
        var handle = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, overlapped ? 0x40000000u : 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }
        return handle;
    }

    public readonly record struct Capabilities(ushort UsagePage, ushort Usage, int Input, int Output, int Feature);

    public static Capabilities Caps(SafeFileHandle handle)
    {
        if (!HidD_GetPreparsedData(handle, out IntPtr data))
        {
            return default;
        }
        try
        {
            return HidP_GetCaps(data, out var caps) == 0x00110000   // HIDP_STATUS_SUCCESS
                ? new(caps.UsagePage, caps.Usage, caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength)
                : default;
        }
        finally
        {
            HidD_FreePreparsedData(data);
        }
    }

    public static int FeatureLength(SafeFileHandle handle) => Caps(handle).Feature;

    public static bool GetFeature(SafeFileHandle handle, byte[] buffer) => HidD_GetFeature(handle, buffer, buffer.Length);

    public static bool SetFeature(SafeFileHandle handle, byte[] buffer) => HidD_SetFeature(handle, buffer, buffer.Length);

    /// <summary>The motherboard's name as the BIOS tells it (e.g. "PRO Z690-A WIFI DDR4 (MS-7D25)"), or null.</summary>
    public static string? BoardName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            return key?.GetValue("BaseBoardProduct") is string name && !string.IsNullOrWhiteSpace(name) ? name.Trim() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

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

    [DllImport("hid.dll")]
    internal static extern bool HidD_FlushQueue(SafeFileHandle handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_Size(out int length, ref Guid guid, string? deviceId, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List(ref Guid guid, string? deviceId, char[] buffer, int length, int flags);
}

/// <summary>
/// A HID interface used with output and input reports (WriteFile / ReadFile), each with a time limit,
/// so a controller that doesn't answer can never hang the service.
/// </summary>
internal sealed unsafe class HidPort : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(500);

    private readonly SafeFileHandle _handle;

    private HidPort(SafeFileHandle handle, Hid.Capabilities caps)
    {
        _handle = handle;
        Caps = caps;
    }

    public Hid.Capabilities Caps { get; }

    /// <summary>Opens a HID interface whose output reports are at least <paramref name="minOutput"/> bytes long.</summary>
    public static HidPort? Open(string path, int minOutput, Func<Hid.Capabilities, bool>? accept = null)
    {
        var handle = Hid.Open(path, overlapped: true);
        if (handle is null)
        {
            return null;
        }
        var caps = Hid.Caps(handle);
        if (caps.Output < minOutput || caps.Input == 0 || accept?.Invoke(caps) == false)
        {
            handle.Dispose();
            return null;
        }
        return new HidPort(handle, caps);
    }

    /// <summary>Sends one output report (padded to the device's length; byte 0 is the report id).</summary>
    public void Write(byte[] report)
    {
        var buffer = new byte[Caps.Output];
        Array.Copy(report, buffer, Math.Min(report.Length, buffer.Length));
        if (Transfer(write: true, buffer) != buffer.Length)
        {
            throw new IOException("The lighting controller didn't take the change.");
        }
    }

    /// <summary>The next input report (byte 0 is the report id), or null if none comes in time.</summary>
    public byte[]? Read()
    {
        var buffer = new byte[Caps.Input];
        return Transfer(write: false, buffer) > 0 ? buffer : null;
    }

    /// <summary>Drops input reports that arrived before a request, so the next read is its answer.</summary>
    public void Flush() => Hid.HidD_FlushQueue(_handle);

    /// <summary>Sends a request and waits for the reply <paramref name="isReply"/> accepts (skipping others), or null.</summary>
    public byte[]? Ask(byte[] request, Func<byte[], bool> isReply)
    {
        Flush();
        Write(request);
        for (int i = 0; i < 8; i++)
        {
            if (Read() is not { } reply)
            {
                return null;
            }
            if (isReply(reply))
            {
                return reply;
            }
        }
        return null;
    }

    private int Transfer(bool write, byte[] buffer)
    {
        using var done = new ManualResetEvent(false);
        var overlapped = new NativeOverlapped { EventHandle = done.SafeWaitHandle.DangerousGetHandle() };
        fixed (byte* data = buffer)
        {
            bool ok = write
                ? WriteFile(_handle, data, buffer.Length, IntPtr.Zero, &overlapped)
                : ReadFile(_handle, data, buffer.Length, IntPtr.Zero, &overlapped);
            if (!ok && Marshal.GetLastWin32Error() != 997)   // ERROR_IO_PENDING
            {
                return -1;
            }
            if (!done.WaitOne(Timeout))
            {
                CancelIoEx(_handle, &overlapped);
            }
            // Waits for the cancellation too: the buffer and the overlapped block must outlive the request.
            return GetOverlappedResult(_handle, &overlapped, out int transferred, true) ? transferred : -1;
        }
    }

    public void Dispose() => _handle.Dispose();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle handle, byte* buffer, int length, IntPtr written, NativeOverlapped* overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle handle, byte* buffer, int length, IntPtr read, NativeOverlapped* overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle handle, NativeOverlapped* overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeFileHandle handle, NativeOverlapped* overlapped, out int transferred, bool wait);
}
