using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NotOnlyFans.Contracts;
using NotOnlyFans.Services;

namespace NotOnlyFans.Controls;

/// <summary>
/// The small line icon in front of a pinned reading: what the reading belongs to (processor, graphics card, drive,
/// motherboard, memory), or a fan - which turns at a pace that follows the fan's real speed.
/// Same drawings as the Home tiles (24 Ã— 24, outlined).
/// </summary>
public static class PinIcon
{
    public const double Size = 16;

    private const string Cpu = "M7.5,6 H16.5 A1.5,1.5 0 0 1 18,7.5 V16.5 A1.5,1.5 0 0 1 16.5,18 H7.5 A1.5,1.5 0 0 1 6,16.5 V7.5 A1.5,1.5 0 0 1 7.5,6 Z M9.5,9.5 H14.5 V14.5 H9.5 Z M9,2.5 V6 M15,2.5 V6 M9,18 V21.5 M15,18 V21.5 M2.5,9 H6 M2.5,15 H6 M18,9 H21.5 M18,15 H21.5";
    private const string Gpu = "M4,6.5 H20 A1.5,1.5 0 0 1 21.5,8 V16 A1.5,1.5 0 0 1 20,17.5 H4 A1.5,1.5 0 0 1 2.5,16 V8 A1.5,1.5 0 0 1 4,6.5 Z M6,12 A3,3 0 1 0 12,12 A3,3 0 1 0 6,12 Z M14.5,12 A2,2 0 1 0 18.5,12 A2,2 0 1 0 14.5,12 Z M5,17.5 V19.5";
    private const string Board = "M5,3 H19 A2,2 0 0 1 21,5 V19 A2,2 0 0 1 19,21 H5 A2,2 0 0 1 3,19 V5 A2,2 0 0 1 5,3 Z M7,7 H12 V12 H7 Z M15,7 H17.5 M15,10 H17.5 M7,15.5 H17 M7,18 H13";
    private const string Drive = "M4,8 H20 A1.5,1.5 0 0 1 21.5,9.5 V14.5 A1.5,1.5 0 0 1 20,16 H4 A1.5,1.5 0 0 1 2.5,14.5 V9.5 A1.5,1.5 0 0 1 4,8 Z M6,12 H12 M16.7,12 A0.8,0.8 0 1 0 18.3,12 A0.8,0.8 0 1 0 16.7,12 Z";
    private const string Memory = "M3,7 H21 V15 H3 Z M6.5,10 H8.5 V12 H6.5 Z M11,10 H13 V12 H11 Z M15.5,10 H17.5 V12 H15.5 Z M5.5,15 V18 M9.5,15 V18 M14.5,15 V18 M18.5,15 V18";
    private const string Thermometer = "M10,13.6 V5 A2,2 0 0 1 14,5 V13.6 A4,4 0 1 1 10,13.6 Z M12,9 V16.5";
    private const string Pulse = "M2.5,12 H7 L10,6 L14,18 L17,12 H21.5";
    // Three blades around the hub; drawn around the middle (12, 12), so it turns in place.
    private const string Fan = "M10.4,12 A1.6,1.6 0 1 0 13.6,12 A1.6,1.6 0 1 0 10.4,12 Z M12,10.4 C11.6,6 12.6,3 15.6,3.4 C18.2,3.8 17.4,7.8 12,10.4 M13.4,12.8 C17.2,15 19.2,17.6 17.3,19.8 C15.6,21.8 12.7,18.8 13.4,12.8 M10.6,12.8 C6.6,14.8 3.6,15.2 3,12.4 C2.5,9.8 6.5,9 10.6,12.8";

    /// <summary>
    /// The icon for one sensor. For a fan (its RPM, or its speed %) <paramref name="spin"/> turns it; give it the
    /// fan's RPM each second. Null for everything else.
    /// </summary>
    public static FrameworkElement Create(FrameworkElement owner, SensorCatalog catalog, int sensor, out FanSpin? spin)
    {
        var s = catalog.Sensors[sensor];
        spin = null;
        string data;
        if (s.Kind is "Fan" || (s.Kind == "Control" && SensorLayout.FanFor(catalog, sensor) >= 0))
        {
            data = Fan;
        }
        else
        {
            string hardware = catalog.Hardware.FirstOrDefault(h => h.Id == s.HardwareId)?.Kind ?? "";
            data = hardware switch
            {
                "Cpu" => Cpu,
                "GpuNvidia" or "GpuAmd" or "GpuIntel" => Gpu,
                "Storage" => Drive,
                "Memory" => Memory,
                "Motherboard" or "SuperIO" or "EmbeddedController" => Board,
                _ => s.Kind == "Temperature" ? Thermometer : Pulse,
            };
        }

        var path = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data),
            Stroke = Theme.Brush(owner, "TextFillColorSecondaryBrush"),
            StrokeThickness = 1.7,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 24,
            Height = 24,
        };
        var icon = new Viewbox
        {
            Width = Size,
            Height = Size,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Canvas { Width = 24, Height = 24, Children = { path } },
        };
        if (ReferenceEquals(data, Fan))
        {
            spin = new FanSpin(icon);
        }
        return icon;
    }
}

/// <summary>
/// Turns a fan icon at a pace that follows the fan's speed: a quiet fan turns slowly, a fan at full speed briskly,
/// a stopped fan stands still. The turning runs in the compositor (no work on the UI thread per frame);
/// a new speed carries on from the angle the icon has reached, so it never jumps.
/// With Windows "Animation effects" off the icon stays still.
/// </summary>
public sealed class FanSpin
{
    // Degrees per second for each RPM: 1000 RPM = one turn in 1.2 s. Capped so blades never blur or seem to stand.
    private const float DegreesPerRpm = 0.3f;
    private const float MaxDegreesPerSecond = 900;

    private static readonly bool Animate = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;

    private readonly Visual _visual;
    private float _speed;          // degrees per second now running
    private float _angle;          // angle when the current speed started
    private long _since;           // Stopwatch timestamp of that moment

    public FanSpin(FrameworkElement icon)
    {
        _visual = ElementCompositionPreview.GetElementVisual(icon);
        _visual.CenterPoint = new Vector3((float)PinIcon.Size / 2, (float)PinIcon.Size / 2, 0);
    }

    /// <summary>The fan's speed now (RPM; null or 0 = stopped).</summary>
    public void SetRpm(float? rpm)
    {
        float speed = Animate && rpm is > 0 ? Math.Min(rpm.Value * DegreesPerRpm, MaxDegreesPerSecond) : 0;
        // Small changes (a few RPM each second) are not worth a new animation.
        if (speed == _speed || (_speed > 0 && speed > 0 && Math.Abs(speed - _speed) < _speed * 0.04f))
        {
            return;
        }
        float angle = Angle();
        _visual.StopAnimation("RotationAngleInDegrees");
        _visual.RotationAngleInDegrees = angle;
        _angle = angle;
        _since = Stopwatch.GetTimestamp();
        _speed = speed;
        if (speed <= 0)
        {
            return;
        }

        var turn = _visual.Compositor.CreateScalarKeyFrameAnimation();
        turn.InsertKeyFrame(0, angle, _visual.Compositor.CreateLinearEasingFunction());
        turn.InsertKeyFrame(1, angle + 360, _visual.Compositor.CreateLinearEasingFunction());
        turn.Duration = TimeSpan.FromSeconds(360 / speed);
        turn.IterationBehavior = AnimationIterationBehavior.Forever;
        _visual.StartAnimation("RotationAngleInDegrees", turn);
    }

    /// <summary>Stops turning where it is (the icon leaves the screen).</summary>
    public void Stop() => SetRpm(null);

    /// <summary>Where the running animation is now (worked out from its speed and start, the compositor's own value can't be read back).</summary>
    private float Angle()
    {
        double seconds = Stopwatch.GetElapsedTime(_since).TotalSeconds;
        return (float)((_angle + _speed * seconds) % 360);
    }
}
