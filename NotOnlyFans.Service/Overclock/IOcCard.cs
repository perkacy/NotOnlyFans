using NotOnlyFans.Contracts;

namespace NotOnlyFans.Service.Overclock;

/// <summary>One graphics card the OC tab can tune, whatever its maker (NVIDIA, AMD, Intel).</summary>
internal interface IOcCard
{
    /// <summary>Stable key, e.g. "nvidia/pci-1", "amd/pci-3", "intel/pci-4" (saved in oc.json).</summary>
    string Key { get; }

    string Name { get; }

    /// <summary>What can be changed and what runs now.</summary>
    OcGpu Read();

    /// <summary>Sends the setting to the card (an empty setting = the card's defaults); null on success, else a message for the user.</summary>
    string? Apply(OcSetting setting);
}
