using System.Globalization;
using NotOnlyFans.Contracts;

namespace NotOnlyFans.Service;

/// <summary>
/// Measures one fan's limits, usually two to three minutes, driven once a second by <see cref="FanController"/>:
///  1. 100% – the fan must turn (else nothing is connected) – its top RPM;
///  2. down step by step (80, 60, 50, 45 … 5, 0 %) until it stops: the last speed it still turned at
///     is the minimum while running (a fan that turns even at 0% has its own minimum and needs no limits);
///  3. from standstill up in 5% steps, from where it stopped, until it turns again: the start speed.
/// A fan needs time to spin up or slow down (a heavy 140 mm one several seconds), so a step is not timed:
/// it waits until the RPM has stopped changing (at least 4 s, the same reading for 3 s), with an upper limit.
/// Small margins are added, so the measured values hold on a cold day or a dusty fan too.
/// Pure state machine: it only says which speed to write; the controller writes it and handles safety.
/// </summary>
internal sealed class Calibration(string key)
{
    private enum Phase { SpinUp, Down, WaitStop, Up }

    private static readonly float[] DownSteps = [80, 60, 50, 45, 40, 35, 30, 25, 20, 15, 10, 5, 0];
    // A step ends when the RPM is steady (or at its limit, for a fan that never quite settles).
    private const double SettleMinSeconds = 4, SteadySeconds = 3;
    private const double SpinUpMaxSeconds = 30, DownMaxSeconds = 25, CoastMaxSeconds = 30;
    private const double NoFanSeconds = 8, StopTimeoutSeconds = 30, UpWaitSeconds = 6, StartedSeconds = 2;
    private const float SteadyRpm = 20, SteadyShare = 0.03f;
    private const float MinMargin = 2, StartMargin = 5, UpStep = 5;

    private Phase _phase = Phase.SpinUp;
    private DateTime _stepStart;
    private int _down;
    private float _up;
    private float _maxRpm, _lastRunning = 100, _lastRunningRpm, _stoppedAt;
    private float? _steadyRpm;        // the reading the RPM has stayed near…
    private DateTime _steadySince;    // …since this moment
    private DateTime? _turningSince;

    public string Key => key;

    /// <summary>What happens now (for the app).</summary>
    public string Message { get; private set; } = "Spinning up to 100%";

    public float Progress { get; private set; }

    public CalibrationResult? Result { get; private set; }
    public string? Error { get; private set; }
    public bool Finished => Result is not null || Error is not null;

    /// <summary>One step (call about once a second): takes the fan's RPM now, returns the speed to write.</summary>
    public float Step(float? rpm, DateTime now)
    {
        if (_stepStart == default)
        {
            _stepStart = now;
        }
        double t = (now - _stepStart).TotalSeconds;
        bool turning = rpm is > 0;
        bool steady = Steady(rpm, now);

        switch (_phase)
        {
            case Phase.SpinUp:
                Message = "Spinning up to 100%";
                Progress = (float)(0.1 * Math.Min(1, t / 10));
                // Nothing turning after a while: no fan. Otherwise wait for its top speed.
                if (turning ? !Settled(t, steady, SpinUpMaxSeconds) : t < NoFanSeconds)
                {
                    return 100;
                }
                if (!turning)
                {
                    Fail("The fan didn't turn at 100%. Check that a fan is connected to this header.");
                    return 100;
                }
                _maxRpm = rpm!.Value;
                _lastRunningRpm = rpm.Value;
                Next(Phase.Down, now);
                return DownSteps[0];

            case Phase.Down:
            {
                float speed = DownSteps[_down];
                Message = Text($"Slowing down: {speed:0}%");
                Progress = 0.1f + 0.5f * _down / DownSteps.Length;
                if (!Settled(t, steady, speed <= 0 ? CoastMaxSeconds : DownMaxSeconds))
                {
                    return speed;
                }
                if (turning)
                {
                    _lastRunning = speed;
                    _lastRunningRpm = rpm!.Value;
                    if (speed <= 0)
                    {
                        Finish(stops: false, startSpeed: 0);   // turns even at 0%
                        return 0;
                    }
                    _down++;
                    Next(Phase.Down, now);
                    return DownSteps[_down];
                }
                _stoppedAt = speed;
                Next(Phase.WaitStop, now);
                return 0;
            }

            case Phase.WaitStop:
                Message = "Waiting for the fan to stop";
                Progress = 0.65f;
                if (!turning && t >= 2)
                {
                    // Try to start it from where it stopped (it won't start lower than that).
                    _up = Math.Max(UpStep, _stoppedAt);
                    Next(Phase.Up, now);
                    return _up;
                }
                if (t >= StopTimeoutSeconds)
                {
                    Fail("The fan didn't stop at 0%.");
                }
                return 0;

            default:   // Phase.Up
                Message = Text($"Starting from standstill: {_up:0}%");
                Progress = 0.7f + 0.3f * Math.Min(1, (_up - _stoppedAt) / 40);
                if (turning)
                {
                    // Turning for a moment (not one twitch): it started.
                    _turningSince ??= now;
                    if ((now - _turningSince.Value).TotalSeconds >= StartedSeconds)
                    {
                        Finish(stops: true, startSpeed: _up);
                    }
                    return _up;
                }
                _turningSince = null;
                if (t < UpWaitSeconds)
                {
                    return _up;
                }
                if (_up >= 100)
                {
                    Fail("The fan didn't start again, even at 100%.");
                    return 100;
                }
                _up = Math.Min(100, _up + UpStep);
                Next(Phase.Up, now);
                return _up;
        }
    }

    /// <summary>The RPM has stayed near one value for <see cref="SteadySeconds"/> (a missing reading is not steady).</summary>
    private bool Steady(float? rpm, DateTime now)
    {
        if (rpm is not { } value)
        {
            _steadyRpm = null;
            return false;
        }
        if (_steadyRpm is not { } near || Math.Abs(value - near) > Math.Max(SteadyRpm, SteadyShare * near))
        {
            _steadyRpm = value;
            _steadySince = now;
        }
        return (now - _steadySince).TotalSeconds >= SteadySeconds;
    }

    /// <summary>This step has its answer: the fan had time to react and its RPM no longer changes, or the step's limit is up.</summary>
    private static bool Settled(double t, bool steady, double maxSeconds) =>
        t >= maxSeconds || (t >= SettleMinSeconds && steady);

    private void Next(Phase phase, DateTime now)
    {
        _phase = phase;
        _stepStart = now;
        _steadyRpm = null;
        _turningSince = null;
    }

    private void Finish(bool stops, float startSpeed)
    {
        float min = stops ? Math.Min(100, _lastRunning + MinMargin) : 0;
        Result = new CalibrationResult
        {
            Time = DateTimeOffset.Now,
            MaxRpm = MathF.Round(_maxRpm),
            Stops = stops,
            MinSpeed = min,
            MinRpm = MathF.Round(_lastRunningRpm),
            StartSpeed = stops ? Math.Min(100, Math.Max(startSpeed + StartMargin, min)) : 0,
        };
        Progress = 1;
        Message = stops
            ? Text($"Done: starts at {Result.StartSpeed:0}%, runs down to {Result.MinSpeed:0}% ({Result.MinRpm:0} RPM), {Result.MaxRpm:0} RPM at 100%. Start speed and minimum are set.")
            : Text($"Done: this fan keeps turning even at 0% ({Result.MinRpm:0} RPM), so it needs no start speed or minimum. {Result.MaxRpm:0} RPM at 100%.");
    }

    private void Fail(string message)
    {
        Error = message;
        Message = message;
    }

    private static string Text(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
