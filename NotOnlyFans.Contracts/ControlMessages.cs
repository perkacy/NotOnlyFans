using System.Text.Json.Serialization;

namespace NotOnlyFans.Contracts;

/// <summary>
/// Fan control. The service owns the settings (so fans are controlled with the window closed);
/// it publishes them on the sensor pipe as a "control" message, and the window app changes them
/// with requests on the control pipe (one JSON line per request, one JSON line per reply).
/// </summary>
public static class ControlPipe
{
    public const string Name = "NotOnlyFans.Control.v1";

    public const string ControlType = "control";
    public const string SetFanType = "setFan";
    public const string SetSafetyType = "setSafety";
    public const string CalibrateType = "calibrate";
    public const string CancelCalibrationType = "cancelCalibration";
}

/// <summary>
/// The safety rule (mandatory, see README › Safety): at or above the limit on any real temperature sensor,
/// every fan NotOnlyFans drives runs at 100% until all temperatures are <see cref="Release"/> °C below it.
/// </summary>
public static class SafetyRule
{
    public const float Default = 90;
    public const float Minimum = 60;
    public const float Maximum = 100;
    public const float Release = 5;

    /// <summary>
    /// Whether a temperature sensor measures a real temperature: not the distance to the CPU limit,
    /// not a drive's fixed warning / critical thresholds.
    /// </summary>
    public static bool Counts(string sensorName) =>
        !sensorName.Contains("Distance to TjMax", StringComparison.OrdinalIgnoreCase)
        && !sensorName.StartsWith("Warning Temperature", StringComparison.OrdinalIgnoreCase)
        && !sensorName.StartsWith("Critical Temperature", StringComparison.OrdinalIgnoreCase);
}

[JsonConverter(typeof(JsonStringEnumConverter<FanMode>))]
public enum FanMode
{
    /// <summary>NotOnlyFans does not touch the fan (motherboard: BIOS Smart Fan; graphics card: its own control).</summary>
    Bios,

    /// <summary>Same speed whatever the temperature.</summary>
    Fixed,

    /// <summary>Speed follows a temperature through a curve.</summary>
    Curve,

    /// <summary>Several curves, each with its own temperature, combined (by default the highest wins).</summary>
    Mix,

    /// <summary>Speed follows another fan's speed, plus or minus an offset.</summary>
    Offset,
}

[JsonConverter(typeof(JsonStringEnumConverter<MixFunction>))]
public enum MixFunction
{
    /// <summary>The highest speed of the curves.</summary>
    Max,

    /// <summary>The average speed of the curves.</summary>
    Average,

    /// <summary>The lowest speed of the curves.</summary>
    Min,
}

/// <summary>One curve of a <see cref="FanMode.Mix"/>: its points and its temperature source(s).</summary>
public sealed class MixCurve
{
    public List<CurvePoint> Curve { get; set; } = [];
    public List<string> Sources { get; set; } = [];

    public MixCurve Copy() => new()
    {
        Curve = Curve.Select(p => new CurvePoint { Temperature = p.Temperature, Speed = p.Speed }).ToList(),
        Sources = [.. Sources],
    };
}

public sealed class CurvePoint
{
    public float Temperature { get; set; }
    public float Speed { get; set; }
}

public static class FanCurve
{
    /// <summary>Speed at a temperature: linear between points; the first / last speed holds below / above the curve.</summary>
    public static float Evaluate(IReadOnlyList<CurvePoint> curve, float temperature)
    {
        if (curve.Count == 0)
        {
            return 100;
        }
        if (temperature <= curve[0].Temperature)
        {
            return curve[0].Speed;
        }
        for (int i = 1; i < curve.Count; i++)
        {
            var a = curve[i - 1];
            var b = curve[i];
            if (temperature <= b.Temperature)
            {
                float t = (temperature - a.Temperature) / (b.Temperature - a.Temperature);
                return a.Speed + t * (b.Speed - a.Speed);
            }
        }
        return curve[^1].Speed;
    }
}

public sealed class FanSetting
{
    public FanMode Mode { get; set; } = FanMode.Bios;

    /// <summary>Speed in % for <see cref="FanMode.Fixed"/>.</summary>
    public float Fixed { get; set; } = 50;

    /// <summary>Curve points (°C → %), ascending by temperature. Below the first / above the last point the end values hold.</summary>
    public List<CurvePoint> Curve { get; set; } = [];

    /// <summary>Temperature sensor keys the curve follows; with several, the highest counts (e.g. "Highest of CPU and GPU").</summary>
    public List<string> Sources { get; set; } = [];

    // ----- Response (curve and mix). Defaults from the Cooling mockup. -----

    /// <summary>Temperature must drop this much (°C) before the fan slows down.</summary>
    public float Hysteresis { get; set; } = 3;

    /// <summary>A lower speed must be asked for this long (s) before the fan slows down (ignores short dips).</summary>
    public float DelayDown { get; set; } = 5;

    /// <summary>Smooth transitions: speed glides at most <see cref="MaxChangePerSecond"/>; off = jumps straight to the target.</summary>
    public bool Smooth { get; set; } = true;

    /// <summary>Fastest change of speed, % per second, up and down, while <see cref="Smooth"/> is on.</summary>
    public float MaxChangePerSecond { get; set; } = 5;

    // ----- Limits (curve and fixed). 0 = not used, so nothing changes until the user (or calibration) sets them. -----

    /// <summary>The fan may stand still when its curve / fixed speed says 0%.</summary>
    public bool AllowStop { get; set; } = true;

    /// <summary>Kick (%) to spin up from standstill, held briefly, then down to the wanted speed.</summary>
    public float StartSpeed { get; set; }

    /// <summary>Lowest speed (%) at which the fan still spins steadily; anything between 0 and this runs at this.</summary>
    public float MinSpeed { get; set; }

    // ----- Mix -----

    /// <summary>Curves of <see cref="FanMode.Mix"/> (2–4), each with its own temperature source.</summary>
    public List<MixCurve> Mix { get; set; } = [];

    /// <summary>How the curves of a mix are combined.</summary>
    public MixFunction MixFunction { get; set; } = MixFunction.Max;

    // ----- Offset -----

    /// <summary>Control key of the fan that <see cref="FanMode.Offset"/> follows.</summary>
    public string? Follow { get; set; }

    /// <summary>Added to the followed fan's speed, in % (may be negative).</summary>
    public float Offset { get; set; }

    /// <summary>A deep copy (changes start from a copy, so switching modes keeps everything else).</summary>
    public FanSetting Copy() => new()
    {
        Mode = Mode,
        Fixed = Fixed,
        Curve = Curve.Select(p => new CurvePoint { Temperature = p.Temperature, Speed = p.Speed }).ToList(),
        Sources = [.. Sources],
        Hysteresis = Hysteresis,
        DelayDown = DelayDown,
        Smooth = Smooth,
        MaxChangePerSecond = MaxChangePerSecond,
        AllowStop = AllowStop,
        StartSpeed = StartSpeed,
        MinSpeed = MinSpeed,
        Mix = Mix.Select(m => m.Copy()).ToList(),
        MixFunction = MixFunction,
        Follow = Follow,
        Offset = Offset,
    };
}

/// <summary>Current control settings and safety state, as the service applies them.</summary>
public sealed class ControlState
{
    /// <summary>At or above this (any real temperature sensor) every fan NotOnlyFans drives runs at 100%.</summary>
    public float SafetyTemperature { get; set; } = SafetyRule.Default;

    /// <summary>True while the safety rule is active (it ends 5 °C below the limit).</summary>
    public bool Emergency { get; set; }

    /// <summary>Hottest sensor that triggered the safety rule (for the message in the app).</summary>
    public string? EmergencySensor { get; set; }

    /// <summary>Settings by key of the fan's Control (speed %) sensor. Fans not listed are in BIOS mode.</summary>
    public Dictionary<string, FanSetting> Fans { get; set; } = [];

    /// <summary>
    /// How a graphics card fan is stopped at 0%, as measured the first time it was asked to stop
    /// (by Control key): <see cref="ZeroStopMethod.Manual"/> = the card accepts a true 0%;
    /// <see cref="ZeroStopMethod.HandBack"/> = it doesn't, so at 0% the fan goes back to the card's own control.
    /// </summary>
    public Dictionary<string, ZeroStopMethod> ZeroStop { get; set; } = [];

    /// <summary>Last successful calibration of each fan (by Control key).</summary>
    public Dictionary<string, CalibrationResult> Calibrations { get; set; } = [];

    /// <summary>The calibration running now, or the last one (finished, failed or cancelled) until the next starts. Not kept over a restart.</summary>
    public CalibrationProgress? Calibration { get; set; }
}

/// <summary>What calibration measured on a fan.</summary>
public sealed class CalibrationResult
{
    public DateTimeOffset Time { get; set; }

    /// <summary>RPM at 100%.</summary>
    public float MaxRpm { get; set; }

    /// <summary>False when the fan keeps turning even at 0% (its own minimum); then no limits are needed.</summary>
    public bool Stops { get; set; }

    /// <summary>Lowest speed (%) at which it kept turning on the way down, with a small margin.</summary>
    public float MinSpeed { get; set; }

    /// <summary>RPM at <see cref="MinSpeed"/>.</summary>
    public float MinRpm { get; set; }

    /// <summary>Speed (%) that starts it from standstill, with a margin.</summary>
    public float StartSpeed { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<CalibrationStatus>))]
public enum CalibrationStatus
{
    Running,
    Done,
    Failed,
    Cancelled,
}

public sealed class CalibrationProgress
{
    /// <summary>Control key of the fan being calibrated.</summary>
    public string Key { get; set; } = "";

    public CalibrationStatus Status { get; set; }

    /// <summary>What happens now, or why it ended (for the app).</summary>
    public string Message { get; set; } = "";

    /// <summary>0–1, a rough estimate.</summary>
    public float Progress { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ZeroStopMethod>))]
public enum ZeroStopMethod
{
    Manual,
    HandBack,
}

public sealed class ControlRequest
{
    public string Type { get; set; } = "";

    /// <summary>Key of the fan's Control (speed %) sensor.</summary>
    public string Key { get; set; } = "";

    public FanSetting? Fan { get; set; }

    /// <summary>New safety limit (°C) for <see cref="ControlPipe.SetSafetyType"/>.</summary>
    public float? SafetyTemperature { get; set; }

    /// <summary>For <see cref="ColorPipe.SetColorType"/> (Key = light key) and <see cref="ColorPipe.SetColorSyncType"/> (the setting for all).</summary>
    public LightSetting? Light { get; set; }

    /// <summary>For <see cref="ColorPipe.SetColorSyncType"/>: Sync all on or off.</summary>
    public bool? Sync { get; set; }

    /// <summary>For <see cref="OcPipe.ApplyOcType"/> (Key = card key).</summary>
    public OcSetting? Oc { get; set; }

    /// <summary>For <see cref="OcPipe.SetOcStartupType"/>: apply when Windows starts.</summary>
    public bool? Startup { get; set; }
}

public sealed class ControlReply
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
}
