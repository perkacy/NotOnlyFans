using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using NotOnlyFans.Contracts;

namespace NotOnlyFans.Service;

/// <summary>
/// Drives the fans the user put under NotOnlyFans control, and keeps them safe.
///
/// Safety (mandatory, see README › Safety):
///  - Any real temperature at or above the safety limit (default 90 °C): every fan we drive runs at 100%,
///    whatever its setting, until all temperatures are 5 °C below the limit.
///  - Fans in BIOS mode are never written; a fan switched back to BIOS is handed back to the BIOS.
///  - When the service stops (also Windows shutdown, or an error in the service) every fan we drove
///    is handed back to the BIOS / the graphics card (<see cref="ReleaseAll"/>).
///
/// All methods are called under <see cref="SensorPoller.Sync"/>.
/// </summary>
internal sealed class FanController(SensorPoller poller, ILogger log)
{

    // Write again now and then even if unchanged, in case firmware or another program changed it
    // (after sleep the board's BIOS takes the fans back).
    private static readonly TimeSpan Rewrite = TimeSpan.FromSeconds(10);

    /// <summary>A gap this long between two passes means the PC slept (or hung): write every fan again at once.</summary>
    private static readonly TimeSpan SleptGap = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, (float Value, DateTime At, bool Nudged)> _written = [];
    private DateTime _lastPass;

    /// <summary>Per curve fan: speed it runs at now and what the response rules remember.</summary>
    private sealed class CurveState
    {
        public float? Output;
        // Per curve (one for Curve, one per curve of a Mix): the temperature after hysteresis.
        public float[] EffectiveTemperatures = [];
        public DateTime? LowerSince;
        public DateTime At;
        public bool SourceMissingLogged;
    }

    private readonly Dictionary<string, CurveState> _curves = [];

    public ControlState State { get; private set; } = ControlStore.Load(log);

    /// <summary>Raised (under the lock) when settings or the safety state change.</summary>
    public event Action<ControlState>? StateChanged;

    /// <summary>Applies settings and the safety rule. Called after every sensor read (and right after a change).</summary>
    public void Apply()
    {
        UpdateEmergency();
        var now = DateTime.UtcNow;
        if (_lastPass != default && now - _lastPass > SleptGap && _written.Count > 0)
        {
            log.LogInformation("No fan pass for {Seconds:0} s (the PC slept?) - taking the fans over again.", (now - _lastPass).TotalSeconds);
            foreach (var key in _written.Keys.ToList())
            {
                _written[key] = _written[key] with { At = DateTime.MinValue };
            }
        }
        _lastPass = now;

        foreach (var (key, setting) in State.Fans.ToList())
        {
            // One fan failing (driver error, unplugged card) must not stop the others - or the safety rule.
            try
            {
                ApplyFan(key, setting, now);
                _failing.Remove(key);
            }
            catch (Exception ex)
            {
                if (_failing.Add(key))
                {
                    log.LogError(ex, "Fan {Key}: setting its speed failed - handing it back to the BIOS (logged once until it works again).", key);
                }
                try
                {
                    if (poller.Find(key)?.Control is { } control)
                    {
                        Release(key, control);   // a fan we can't drive properly is safest with the BIOS
                    }
                }
                catch (Exception)
                {
                    // Already logged; the next second tries again.
                }
            }
        }
    }

    private readonly HashSet<string> _failing = [];

    private void ApplyFan(string key, FanSetting setting, DateTime now)
    {
        var sensor = poller.Find(key);
        var control = sensor?.Control;
        if (control is null)
        {
            return;   // fan not present now (unplugged header, other GPU...)
        }

        if (setting.Mode == FanMode.Bios)
        {
            Release(key, control);
            return;
        }

        // Calibration drives the fan itself (safety first: the rule ends it, see StepCalibration).
        if (_calibration?.Key == key && !State.Emergency)
        {
            Write(key, control, StepCalibration(sensor!, now), now);
            return;
        }

        float target;
        if (State.Emergency)
        {
            if (_calibration?.Key == key)
            {
                EndCalibration(CalibrationStatus.Failed, "Stopped by the safety rule – the fan runs at 100% until temperatures drop.");
            }
            target = 100;
            if (_curves.TryGetValue(key, out var st))
            {
                st.Output = 100;   // afterwards it slows down gradually from 100
                st.At = now;
            }
        }
        else if (setting.Mode is FanMode.Curve or FanMode.Mix)
        {
            target = ApplyLimits(key, setting, CurveTarget(key, setting, sensor!.Value, now), sensor.Value, now);
        }
        else if (setting.Mode == FanMode.Offset)
        {
            target = ApplyLimits(key, setting, OffsetTarget(key, setting), sensor!.Value, now);
        }
        else
        {
            target = ApplyLimits(key, setting, Math.Clamp(setting.Fixed, 0, 100), sensor!.Value, now);
        }

        target = MathF.Round(target);

        // Graphics cards: 0% may need special handling (see GpuZero); anything above 0 takes the fan (back) over.
        if (IsGraphicsCard(sensor!))
        {
            if (target <= 0 && GpuZero(key, control, sensor!, now))
            {
                return;
            }
            if (target > 0)
            {
                _zeroTests.Remove(key);
                _handedBack.Remove(key);
            }
        }

        Write(key, control, target, now);
    }

    private void Write(string key, IControl control, float target, DateTime now)
    {
        bool known = _written.TryGetValue(key, out var last);
        if (!known || last.Value != target || now - last.At > Rewrite)
        {
            // LibreHardwareMonitor talks to the chip only when the value it holds changes: the same value again is
            // ignored, so a rewrite would never reach the hardware (a fixed fan stayed with the BIOS after sleep).
            // A rewrite of the same value is nudged by a hair - the chip still gets the same duty - and the next
            // one goes back to the exact value.
            bool nudge = known && last.Value == target && !last.Nudged;
            control.SetSoftware(nudge ? target + (target < 50 ? 0.01f : -0.01f) : target);
            _written[key] = (target, now, nudge);
        }
    }

    // ---------- Calibration (one fan at a time) ----------

    private Calibration? _calibration;

    /// <summary>The fan's RPM sensor (same hardware, same name as its speed % sensor).</summary>
    private static ISensor? RpmSensor(ISensor control) =>
        control.Hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan && s.Name == control.Name);

    /// <summary>Starts calibrating a fan that NotOnlyFans drives. Returns null when started, else why not.</summary>
    public string? StartCalibration(string key)
    {
        var sensor = poller.Find(key);
        if (sensor?.Control is null)
        {
            return $"No controllable fan with key '{key}'.";
        }
        if (!State.Fans.TryGetValue(key, out var setting) || setting.Mode == FanMode.Bios)
        {
            return "Choose Curve, Fixed, Mix or Offset first – calibration measures fans that NotOnlyFans drives.";
        }
        if (_calibration is { } running)
        {
            return running.Key == key ? null : "Another fan is being calibrated. Wait until it finishes.";
        }
        if (State.Emergency)
        {
            return "The safety rule is running all fans at 100%. Calibrate when temperatures are lower.";
        }
        if (RpmSensor(sensor) is null)
        {
            return "This fan doesn't report its RPM, so it can't be measured.";
        }
        if (IsGraphicsCard(sensor) && (!State.ZeroStop.TryGetValue(key, out var zero) || zero != ZeroStopMethod.Manual))
        {
            return "Calibration needs a true 0% on this card. Set the fan to 0% once (Fixed) so NotOnlyFans can check the card first.";
        }

        _calibration = new Calibration(key);
        log.LogInformation("Fan {Key}: calibration started.", key);
        Publish(CalibrationStatus.Running);
        Apply();
        return null;
    }

    public void CancelCalibration(string key)
    {
        if (_calibration?.Key == key)
        {
            EndCalibration(CalibrationStatus.Cancelled, "Calibration cancelled. Nothing was changed.");
            Apply();
        }
    }

    private float StepCalibration(ISensor sensor, DateTime now)
    {
        var calibration = _calibration!;
        float speed = calibration.Step(RpmSensor(sensor)?.Value, now);

        if (calibration.Result is { } result)
        {
            State.Calibrations[calibration.Key] = result;
            if (State.Fans.TryGetValue(calibration.Key, out var setting))
            {
                setting.StartSpeed = result.StartSpeed;
                setting.MinSpeed = result.MinSpeed;
            }
            EndCalibration(CalibrationStatus.Done, calibration.Message);
        }
        else if (calibration.Error is { } error)
        {
            EndCalibration(CalibrationStatus.Failed, error);
        }
        else
        {
            Publish(CalibrationStatus.Running);
        }
        return speed;
    }

    private void Publish(CalibrationStatus status)
    {
        if (_calibration is not { } c)
        {
            return;
        }
        State.Calibration = new CalibrationProgress { Key = c.Key, Status = status, Message = c.Message, Progress = c.Progress };
        StateChanged?.Invoke(State);
    }

    /// <summary>Ends the calibration; the fan goes back to its setting from the speed it runs at now.</summary>
    private void EndCalibration(CalibrationStatus status, string message)
    {
        if (_calibration is not { } c)
        {
            return;
        }
        _calibration = null;
        _curves.Remove(c.Key);
        _kicked.Remove(c.Key);
        State.Calibration = new CalibrationProgress { Key = c.Key, Status = status, Message = message, Progress = c.Progress };
        ControlStore.Save(State, log);
        log.LogInformation("Fan {Key}: calibration {Status}: {Message}", c.Key, status, message);
        StateChanged?.Invoke(State);
    }

    // ---------- Graphics card fans at 0% ----------
    //
    // Some cards accept a true 0% from programs (fan stops, like MSI Afterburner's curve); others clamp manual
    // speeds to a minimum (often 30%). The first time a card fan is asked for 0%, we write 0% and watch it for
    // 15 s: stopped → "Manual" (true 0% from then on); still turning → "HandBack": at 0% the fan is handed back
    // to the card, which keeps it still while cool (its own zero-RPM mode), and taken over again above 0%.
    // The result is saved, so the test runs once per fan.

    private static readonly TimeSpan ZeroTestLength = TimeSpan.FromSeconds(15);
    private readonly Dictionary<string, DateTime> _zeroTests = [];
    private readonly HashSet<string> _handedBack = [];

    private static bool IsGraphicsCard(ISensor sensor) =>
        sensor.Hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    /// <summary>Handles 0% for a graphics card fan. Returns true when the fan must not be written this time.</summary>
    private bool GpuZero(string key, IControl control, ISensor sensor, DateTime now)
    {
        if (State.ZeroStop.TryGetValue(key, out var method))
        {
            if (method == ZeroStopMethod.Manual)
            {
                return false;   // write 0% like any other value
            }
            if (_handedBack.Add(key))
            {
                control.SetDefault();
                _written.Remove(key);
                log.LogInformation("Fan {Key} at 0%: handed to the graphics card (it keeps the fan still while cool).", key);
            }
            return true;
        }

        // Not measured yet: write 0% and watch.
        if (!_zeroTests.TryGetValue(key, out var since))
        {
            _zeroTests[key] = now;
            log.LogInformation("Fan {Key}: first 0% - checking for {Seconds} s whether the card accepts a true 0%.", key, ZeroTestLength.TotalSeconds);
            return false;
        }
        if (now - since < ZeroTestLength)
        {
            return false;
        }

        _zeroTests.Remove(key);
        var rpm = sensor.Hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan && s.Name == sensor.Name)?.Value;
        method = rpm is null or <= 0 ? ZeroStopMethod.Manual : ZeroStopMethod.HandBack;
        State.ZeroStop[key] = method;
        ControlStore.Save(State, log);
        log.LogInformation("Fan {Key}: true 0% {Result} (fan at {Rpm} RPM after {Seconds} s at 0%).",
            key, method == ZeroStopMethod.Manual ? "works" : "is not accepted - handing back to the card at 0%", rpm, ZeroTestLength.TotalSeconds);
        StateChanged?.Invoke(State);
        return GpuZero(key, control, sensor, now);
    }

    /// <summary>
    /// Curve speed with the response rules: hysteresis (slow down only after the temperature fell this much),
    /// delay before slowing down, and a limit on how fast the speed may change (by real time, not by calls).
    /// </summary>
    private float CurveTarget(string key, FanSetting setting, float? currentDuty, DateTime now)
    {
        if (!_curves.TryGetValue(key, out var st))
        {
            st = new CurveState { At = now };
            _curves[key] = st;
        }

        bool mix = setting.Mode == FanMode.Mix;
        int count = mix ? setting.Mix.Count : 1;
        if (st.EffectiveTemperatures.Length != count)
        {
            st.EffectiveTemperatures = Enumerable.Repeat(float.NaN, count).ToArray();
        }

        float hysteresis = Math.Max(0, setting.Hysteresis);
        float wanted = 0;
        for (int i = 0; i < count; i++)
        {
            var (curve, sources) = mix ? (setting.Mix[i].Curve, setting.Mix[i].Sources) : (setting.Curve, setting.Sources);
            if (SourceTemperature(sources) is not { } temp)
            {
                // No reading from a source: safest is full speed until it comes back.
                if (!st.SourceMissingLogged)
                {
                    log.LogWarning("Fan {Key}: a temperature source has no reading - running at 100%.", key);
                    st.SourceMissingLogged = true;
                }
                st.Output = 100;
                st.At = now;
                return 100;
            }

            // Hysteresis: follow rises at once; follow falls only once they exceed the hysteresis.
            ref float effective = ref st.EffectiveTemperatures[i];
            if (float.IsNaN(effective) || temp > effective)
            {
                effective = temp;
            }
            else if (temp < effective - hysteresis)
            {
                effective = temp + hysteresis;
            }

            float speed = FanCurve.Evaluate(curve, effective);
            wanted = i == 0 ? speed : setting.MixFunction switch
            {
                MixFunction.Min => Math.Min(wanted, speed),
                MixFunction.Average => wanted + speed,
                _ => Math.Max(wanted, speed),
            };
        }
        if (mix && setting.MixFunction == MixFunction.Average)
        {
            wanted /= count;
        }
        st.SourceMissingLogged = false;

        float current = st.Output ?? currentDuty ?? wanted;

        // Delay before slowing down: a lower speed must be wanted for a while first.
        if (wanted < current)
        {
            st.LowerSince ??= now;
            if ((now - st.LowerSince.Value).TotalSeconds < setting.DelayDown)
            {
                wanted = current;
            }
        }
        else
        {
            st.LowerSince = null;
        }

        // Smooth: at most MaxChangePerSecond per second of real time.
        double seconds = Math.Clamp((now - st.At).TotalSeconds, 0, 5);
        st.At = now;
        if (setting.Smooth && setting.MaxChangePerSecond > 0)
        {
            float step = (float)(setting.MaxChangePerSecond * seconds);
            wanted = Math.Clamp(wanted, current - step, current + step);
        }

        st.Output = Math.Clamp(wanted, 0, 100);
        return st.Output.Value;
    }

    private readonly HashSet<string> _followMissingLogged = [];

    /// <summary>
    /// Offset: the speed the followed fan runs at now (as its hardware reports it, whatever controls it)
    /// plus the offset. The followed fan already changes smoothly, so no response rules here.
    /// </summary>
    private float OffsetTarget(string key, FanSetting setting)
    {
        if (setting.Follow is { } follow && poller.Reading(follow) is { } speed)
        {
            _followMissingLogged.Remove(key);
            return Math.Clamp(speed + setting.Offset, 0, 100);
        }
        if (_followMissingLogged.Add(key))
        {
            log.LogWarning("Fan {Key}: the fan it follows has no speed reading - running at 100%.", key);
        }
        return 100;
    }

    private static readonly TimeSpan KickLength = TimeSpan.FromSeconds(3);

    // Fans kicked since they last stood still (value: when the kick ends).
    private readonly Dictionary<string, DateTime> _kicked = [];

    /// <summary>
    /// Limits: between 0 and the minimum the fan runs at the minimum; at 0 it stops only if allowed
    /// (otherwise the minimum); starting from standstill it gets the start speed for 3 s first.
    /// </summary>
    private float ApplyLimits(string key, FanSetting s, float target, float? currentDuty, DateTime now)
    {
        float min = Math.Clamp(s.MinSpeed, 0, 100);
        if (target < 0.5f)
        {
            target = s.AllowStop ? 0 : min;
        }
        else if (target < min)
        {
            target = min;
        }

        if (target <= 0)
        {
            _kicked.Remove(key);   // stands still: the next start may need a kick again
            return 0;
        }

        float start = Math.Clamp(s.StartSpeed, 0, 100);
        if (_kicked.TryGetValue(key, out var until))
        {
            return now < until ? Math.Max(target, start) : target;
        }

        float lastWritten = _written.TryGetValue(key, out var last) ? last.Value : currentDuty ?? 0;
        if (lastWritten <= 0 && start > target)
        {
            _kicked[key] = now + KickLength;
            return start;
        }
        _kicked[key] = now;   // running: no kick until it stops again
        return target;
    }

    /// <summary>The highest current reading among the source sensors (null if none has one).</summary>
    private float? SourceTemperature(List<string> sources)
    {
        float? best = null;
        foreach (var key in sources)
        {
            if (poller.Reading(key) is { } v && (best is null || v > best))
            {
                best = v;
            }
        }
        return best;
    }

    /// <summary>A change from the window app. Applied at once (not on the next second).</summary>
    public string? SetFan(string key, FanSetting setting)
    {
        var control = poller.Find(key)?.Control;
        if (control is null)
        {
            return $"No controllable fan with key '{key}'.";
        }
        if (Validate(key, setting) is { } problem)
        {
            return problem;
        }

        if (setting.Mode == FanMode.Bios)
        {
            State.Fans.Remove(key);   // BIOS is the default: no entry needed
        }
        else
        {
            State.Fans[key] = setting.Copy();
        }
        ControlStore.Save(State, log);
        log.LogInformation("Fan {Key}: {Mode} (fixed {Fixed}%, {Points} curve points, {Sources} source sensors, {Mix} mix curves, follows {Follow} {Offset:+0;-0;0}%)",
            key, setting.Mode, setting.Fixed, setting.Curve.Count, setting.Sources.Count, setting.Mix.Count, setting.Follow ?? "-", setting.Offset);

        // A new mode starts from the speed the fan runs at now (no jump), with fresh response memory.
        _followMissingLogged.Remove(key);
        if (_curves.TryGetValue(key, out var st) && setting.Mode is FanMode.Curve or FanMode.Mix)
        {
            st.EffectiveTemperatures = [];
            st.LowerSince = null;
        }
        else
        {
            _curves.Remove(key);
        }

        if (setting.Mode == FanMode.Bios)
        {
            Release(key, control);
        }
        Apply();
        StateChanged?.Invoke(State);
        return null;
    }

    /// <summary>New safety limit from the window app (whole °C, within <see cref="SafetyRule"/>'s range). Applied at once.</summary>
    public string? SetSafety(float temperature)
    {
        if (!float.IsFinite(temperature) || temperature is < SafetyRule.Minimum or > SafetyRule.Maximum)
        {
            return $"The safety temperature must be between {SafetyRule.Minimum:0} and {SafetyRule.Maximum:0} °C.";
        }
        temperature = MathF.Round(temperature);
        if (temperature == State.SafetyTemperature)
        {
            return null;
        }
        State.SafetyTemperature = temperature;
        ControlStore.Save(State, log);
        log.LogInformation("Safety temperature set to {Limit:0} °C.", temperature);
        Apply();   // re-checks the rule against the new limit
        StateChanged?.Invoke(State);
        return null;
    }

    private const int MaxMixCurves = 4;

    /// <summary>A change from the window app: the setting must make sense and its sensors must exist now.</summary>
    private string? Validate(string key, FanSetting s)
    {
        if (CheckShape(key, s) is { } problem)
        {
            return problem;
        }

        var curveSources = s.Mode switch
        {
            FanMode.Curve => s.Sources,
            FanMode.Mix => s.Mix.SelectMany(m => m.Sources),
            _ => [],
        };
        if (curveSources.Any(source => poller.Find(source) is not { SensorType: SensorType.Temperature }))
        {
            return "The temperature source isn't available.";
        }

        if (s.Mode == FanMode.Offset)
        {
            if (poller.Find(s.Follow!) is not { SensorType: SensorType.Control })
            {
                return "Choose a fan to follow.";
            }
            // Fans following each other in a circle would only chase each other's readings.
            string? next = s.Follow;
            for (int i = 0; next is not null && i <= State.Fans.Count; i++)
            {
                if (next == key)
                {
                    return "These fans would follow each other in a circle.";
                }
                next = State.Fans.TryGetValue(next, out var other) && other.Mode == FanMode.Offset ? other.Follow : null;
            }
        }
        return null;
    }

    /// <summary>
    /// Whether a setting makes sense on its own (numbers in range, nothing missing), without looking at the
    /// hardware. Also used for settings read from the file, before the sensors are known.
    /// </summary>
    public static string? CheckShape(string key, FanSetting? s)
    {
        if (s is null || s.Curve is null || s.Sources is null || s.Mix is null)
        {
            return "Incomplete setting.";
        }
        if (!Enum.IsDefined(s.Mode) || !Enum.IsDefined(s.MixFunction))
        {
            return "Unknown mode.";
        }
        if (!float.IsFinite(s.Fixed) || s.Fixed < 0 || s.Fixed > 100)
        {
            return "Fixed speed must be between 0 and 100 %.";
        }
        if (!float.IsFinite(s.StartSpeed) || s.StartSpeed is < 0 or > 100
            || !float.IsFinite(s.MinSpeed) || s.MinSpeed is < 0 or > 100)
        {
            return "Start speed and minimum must be between 0 and 100 %.";
        }

        if (s.Mode == FanMode.Offset)
        {
            if (!float.IsFinite(s.Offset) || s.Offset is < -100 or > 100)
            {
                return "Offset must be between -100 and +100 %.";
            }
            if (string.IsNullOrEmpty(s.Follow))
            {
                return "Choose a fan to follow.";
            }
            return s.Follow == key ? "A fan can't follow itself." : null;
        }
        if (s.Mode == FanMode.Curve)
        {
            if (CheckCurve(s.Curve, s.Sources) is { } problem)
            {
                return problem;
            }
        }
        else if (s.Mode == FanMode.Mix)
        {
            if (s.Mix.Count is < 2 or > MaxMixCurves)
            {
                return $"A mix needs 2 to {MaxMixCurves} curves.";
            }
            foreach (var part in s.Mix)
            {
                if ((part is null ? "Incomplete setting." : CheckCurve(part.Curve, part.Sources)) is { } problem)
                {
                    return problem;
                }
            }
        }
        else
        {
            return null;
        }

        if (!float.IsFinite(s.Hysteresis) || s.Hysteresis is < 0 or > 20
            || !float.IsFinite(s.DelayDown) || s.DelayDown is < 0 or > 120
            || !float.IsFinite(s.MaxChangePerSecond) || s.MaxChangePerSecond is < 0 or > 100)
        {
            return "Response settings are out of range.";
        }
        return null;
    }

    private static string? CheckCurve(List<CurvePoint>? curve, List<string>? sources)
    {
        if (curve is null || sources is null)
        {
            return "Incomplete setting.";
        }
        if (curve.Count is < 2 or > 12)
        {
            return "A curve needs 2 to 12 points.";
        }
        for (int i = 0; i < curve.Count; i++)
        {
            var p = curve[i];
            if (p is null || !float.IsFinite(p.Temperature) || !float.IsFinite(p.Speed)
                || p.Temperature < 0 || p.Temperature > 150 || p.Speed < 0 || p.Speed > 100)
            {
                return "Curve points must be 0–150 °C and 0–100 %.";
            }
            if (i > 0 && p.Temperature <= curve[i - 1].Temperature)
            {
                return "Curve points must go up in temperature.";
            }
        }
        if (sources.Count is < 1 or > 8 || sources.Any(string.IsNullOrEmpty))
        {
            return "Choose a temperature source.";
        }
        return null;
    }

    /// <summary>Hands every fan we drove back to the BIOS / graphics card. Safe to call more than once.</summary>
    public void ReleaseAll()
    {
        foreach (var key in _written.Keys.ToList())
        {
            if (poller.Find(key)?.Control is { } control)
            {
                Release(key, control);
            }
        }
        _written.Clear();
    }

    private void Release(string key, IControl control)
    {
        if (_calibration?.Key == key)
        {
            EndCalibration(CalibrationStatus.Cancelled, "Calibration cancelled – the fan went back to the BIOS.");
        }
        _curves.Remove(key);
        _followMissingLogged.Remove(key);
        _kicked.Remove(key);
        _zeroTests.Remove(key);
        _handedBack.Remove(key);
        if (_written.Remove(key))
        {
            control.SetDefault();
            log.LogInformation("Fan {Key} handed back to the BIOS.", key);
        }
    }

    private void UpdateEmergency()
    {
        float limit = State.SafetyTemperature;
        (string Name, float Value) hottest = ("", float.MinValue);
        foreach (var t in poller.SafetyTemperatures())
        {
            if (t.Value > hottest.Value)
            {
                hottest = t;
            }
        }

        bool emergency = State.Emergency
            ? hottest.Value >= limit - SafetyRule.Release
            : hottest.Value >= limit;

        if (emergency != State.Emergency)
        {
            State.Emergency = emergency;
            State.EmergencySensor = emergency ? hottest.Name : null;
            if (emergency)
            {
                log.LogWarning("Safety: {Sensor} at {Value:0} °C (limit {Limit:0} °C) - all controlled fans to 100%.", hottest.Name, hottest.Value, limit);
            }
            else
            {
                log.LogInformation("Safety: temperatures back below {Release:0} °C - fans follow their settings again.", limit - SafetyRule.Release);
            }
            StateChanged?.Invoke(State);
        }
    }
}
