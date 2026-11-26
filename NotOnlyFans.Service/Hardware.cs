using System.Security.Principal;
using LibreHardwareMonitor.Hardware;

namespace NotOnlyFans.Service;

internal static class Hardware
{
    /// <summary>The set of hardware NotOnlyFans reads. Same for the probe and the live service.</summary>
    public static Computer CreateComputer() => new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMotherboardEnabled = true,
        IsControllerEnabled = true,
        IsStorageEnabled = true,
        IsMemoryEnabled = true,
    };

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

/// <summary>Reads fresh values from every piece of hardware (including sub-hardware such as the Super I/O chip).</summary>
internal sealed class UpdateVisitor(Action<IHardware, Exception>? failed = null) : IVisitor
{
    public void VisitComputer(IComputer computer) => computer.Traverse(this);

    public void VisitHardware(IHardware hardware)
    {
        // One device failing (driver reset, a drive removed) must not stop reading the others.
        try
        {
            hardware.Update();
        }
        catch (Exception ex) when (failed is not null)
        {
            failed(hardware, ex);
        }
        foreach (var sub in hardware.SubHardware)
        {
            sub.Accept(this);
        }
    }

    public void VisitSensor(ISensor sensor) { }

    public void VisitParameter(IParameter parameter) { }
}
