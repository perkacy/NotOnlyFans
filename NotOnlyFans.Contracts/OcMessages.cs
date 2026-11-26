namespace NotOnlyFans.Contracts;

/// <summary>
/// Graphics card overclocking (the OC tab). The service finds the cards it can tune, publishes them on the
/// sensor pipe as an "oc" message, and takes changes on the control pipe. A card the user never applied
/// anything to is not touched. Clocks are offsets on top of the card's own boost, as in MSI Afterburner / LACT.
/// </summary>
public static class OcPipe
{
    public const string OcType = "oc";

    /// <summary>Apply <see cref="ControlRequest.Oc"/> to the card <see cref="ControlRequest.Key"/> now, and keep it.</summary>
    public const string ApplyOcType = "applyOc";

    /// <summary>Back to the card's defaults (no offsets, default power limit); the saved setting is removed.</summary>
    public const string ResetOcType = "resetOc";

    /// <summary>Turn "Apply when Windows starts" on or off (<see cref="ControlRequest.Startup"/>), without applying anything.</summary>
    public const string SetOcStartupType = "setOcStartup";
}

/// <summary>What the user applied to a card.</summary>
public sealed class OcSetting
{
    /// <summary>Core clock offset, MHz.</summary>
    public int CoreOffset { get; set; }

    /// <summary>Memory clock offset, MHz (in the units the driver uses, as Afterburner shows them).</summary>
    public int MemoryOffset { get; set; }

    /// <summary>Power limit, W; null = the card's default.</summary>
    public float? PowerLimit { get; set; }

    /// <summary>The service applies this when it starts (with Windows), unless the PC or driver crashed since.</summary>
    public bool ApplyAtStartup { get; set; }

    /// <summary>When it was last applied (the startup check looks for crashes after this). Set by the service.</summary>
    public DateTimeOffset? AppliedAt { get; set; }

    public OcSetting Copy() => new()
    {
        CoreOffset = CoreOffset,
        MemoryOffset = MemoryOffset,
        PowerLimit = PowerLimit,
        ApplyAtStartup = ApplyAtStartup,
        AppliedAt = AppliedAt,
    };
}

/// <summary>One graphics card and what can be changed on it. A range of 0…0 means "can't be changed".</summary>
public sealed class OcGpu
{
    /// <summary>Stable key, e.g. "nvidia/pci-1" (saved in settings).</summary>
    public string Key { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Null when the card can be tuned; otherwise why not (shown in the app).</summary>
    public string? Unsupported { get; set; }

    public int CoreMin { get; set; }
    public int CoreMax { get; set; }
    public int MemoryMin { get; set; }
    public int MemoryMax { get; set; }

    /// <summary>Power limit range and default, W; 0 = can't be changed.</summary>
    public float PowerMin { get; set; }
    public float PowerMax { get; set; }
    public float PowerDefault { get; set; }

    /// <summary>What the card runs with now (read back from the driver).</summary>
    public int CoreOffset { get; set; }
    public int MemoryOffset { get; set; }
    public float PowerLimit { get; set; }

    /// <summary>Highest boost clock and memory clock at the current offsets, MHz (0 = unknown).</summary>
    public int BoostClock { get; set; }
    public int MemoryClock { get; set; }
}

public sealed class OcState
{
    public List<OcGpu> Gpus { get; set; } = [];

    /// <summary>Saved settings by card key (what was applied last).</summary>
    public Dictionary<string, OcSetting> Settings { get; set; } = [];

    /// <summary>
    /// Something the user should know, by card key: the overclock was not applied at startup because Windows or
    /// the graphics driver crashed after it was applied, or the driver reset and dropped it.
    /// </summary>
    public Dictionary<string, string> Notices { get; set; } = [];
}
