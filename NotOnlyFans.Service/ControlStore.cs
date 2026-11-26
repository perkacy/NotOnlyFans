using Microsoft.Extensions.Logging;
using NotOnlyFans.Contracts;

namespace NotOnlyFans.Service;

/// <summary>
/// Fan settings, owned by the service so fans are controlled with the window closed.
/// Saved to %ProgramData%\NotOnlyFans\fans.json (see <see cref="DataDirectory"/>).
/// </summary>
internal static class ControlStore
{
    private const string FileName = "fans.json";

    /// <summary>The saved settings; unreadable or damaged settings never stop the service (fans then stay with the BIOS).</summary>
    public static ControlState Load(ILogger? log)
    {
        if (DataDirectory.Load(FileName, ContractsJson.Default.ControlState, log) is not { } state)
        {
            return new ControlState();
        }

        state.Emergency = false;
        state.EmergencySensor = null;
        state.Calibration = null;   // a calibration never survives a restart
        if (!float.IsFinite(state.SafetyTemperature)
            || state.SafetyTemperature is < SafetyRule.Minimum or > SafetyRule.Maximum)
        {
            state.SafetyTemperature = SafetyRule.Default;   // damaged file: never a limit that can't work
        }
        state.Fans ??= [];
        state.ZeroStop ??= [];
        state.Calibrations ??= [];
        foreach (var (key, setting) in state.Fans.ToList())
        {
            if (FanController.CheckShape(key, setting) is { } problem)
            {
                state.Fans.Remove(key);   // back to the BIOS rather than guessing
                log?.LogWarning("Saved setting of fan {Key} is damaged ({Problem}) - the fan stays with the BIOS.", key, problem);
            }
        }
        return state;
    }

    public static void Save(ControlState state, ILogger? log) =>
        DataDirectory.Save(FileName, state, ContractsJson.Default.ControlState, log);
}
