using System.Globalization;
using System.Text.Json.Serialization;

namespace NotOnlyFans.Contracts;

/// <summary>
/// Lighting. NotOnlyFans drives RGB itself (no OpenRGB): the service finds the lights it knows how to
/// drive, publishes them on the sensor pipe as a "colors" message, and takes changes on the control pipe
/// (<see cref="SetColorType"/>, <see cref="SetColorSyncType"/>). Lights the user never set are not touched.
/// </summary>
public static class ColorPipe
{
    public const string ColorsType = "colors";
    public const string SetColorType = "setColor";
    public const string SetColorSyncType = "setColorSync";

    /// <summary>The window app reports the Windows Dynamic Lighting devices it drives (<see cref="ControlRequest.Lights"/>).</summary>
    public const string SetAppLightsType = "setAppLights";
}

[JsonConverter(typeof(JsonStringEnumConverter<LightEffect>))]
public enum LightEffect
{
    Off,
    Static,
    Breathing,
    Rainbow,

    /// <summary>The color follows a temperature, from the cool color to the hot one (default blue → orange); the service sets it every second.</summary>
    Temperature,
}

[JsonConverter(typeof(JsonStringEnumConverter<LightSpeed>))]
public enum LightSpeed
{
    Slow,
    Medium,
    Fast,
}

/// <summary>What the user chose for a light (or for all of them with Sync all).</summary>
public sealed class LightSetting
{
    public LightEffect Effect { get; set; } = LightEffect.Static;

    /// <summary>"#RRGGBB", for <see cref="LightEffect.Static"/> and <see cref="LightEffect.Breathing"/>.</summary>
    public string Color { get; set; } = "#FFFFFF";

    /// <summary>0–100 %. Devices have fewer steps (a motherboard header has 11), so it is rounded to theirs.</summary>
    public float Brightness { get; set; } = 100;

    public LightSpeed Speed { get; set; } = LightSpeed.Medium;

    /// <summary>Temperature sensor keys for <see cref="LightEffect.Temperature"/>; with several, the highest counts.</summary>
    public List<string> Sources { get; set; } = [];

    /// <summary>At or below this (°C) the color is fully "cool".</summary>
    public float Cool { get; set; } = 35;

    /// <summary>At or above this (°C) the color is fully "hot".</summary>
    public float Hot { get; set; } = 80;

    /// <summary>
    /// The user's own color at <see cref="Cool"/> ("#RRGGBB"); null = the default scale (blue → cyan → yellow → orange).
    /// With either own color the scale goes from one to the other (see <see cref="LightColor.Scale"/>).
    /// </summary>
    public string? CoolColor { get; set; }

    /// <summary>The user's own color at <see cref="Hot"/>; null = the default scale.</summary>
    public string? HotColor { get; set; }

    public LightSetting Copy() => new()
    {
        Effect = Effect,
        Color = Color,
        Brightness = Brightness,
        Speed = Speed,
        Sources = [.. Sources],
        Cool = Cool,
        Hot = Hot,
        CoolColor = CoolColor,
        HotColor = HotColor,
    };
}

/// <summary>One light the service can drive (a header on the motherboard, a graphics card's logo…).</summary>
public sealed class LightInfo
{
    /// <summary>Stable key, e.g. "msi-7d25/jrainbow1" or "gpu/1458-4072" (saved in settings).</summary>
    public string Key { get; set; } = "";

    /// <summary>Default name; the user can rename it in the app.</summary>
    public string Name { get; set; } = "";

    /// <summary>Where it is, short, e.g. "JRAINBOW1 · ARGB 5 V".</summary>
    public string Detail { get; set; } = "";

    /// <summary>The device it belongs to, e.g. "MSI PRO Z690-A WIFI DDR4".</summary>
    public string Device { get; set; } = "";

    /// <summary>"Header", "Board", "Gpu", "DynamicLighting" (a Windows Dynamic Lighting device, driven by the window app).</summary>
    public string Kind { get; set; } = "";

    public List<LightEffect> Effects { get; set; } = [];

    /// <summary>Breathing and Rainbow run at the controller's one speed (Slow / Medium / Fast change nothing).</summary>
    public bool SpeedFixed { get; set; }

    /// <summary>Rainbow always shows at full brightness (the controller has no brightness setting; colors are dimmed by NotOnlyFans).</summary>
    public bool RainbowBrightnessFixed { get; set; }

    /// <summary>The driver was written from a protocol description and not yet tried on this kind of device.</summary>
    public bool Experimental { get; set; }

    /// <summary>
    /// What the light shows now when NotOnlyFans has not set it (read from the device), if the device can tell
    /// and the effect is one of ours; else null.
    /// </summary>
    public LightSetting? Found { get; set; }

    /// <summary>Name of an effect the device shows that NotOnlyFans has no button for (e.g. set by vendor software).</summary>
    public string? FoundOther { get; set; }
}

public sealed class ColorState
{
    public List<LightInfo> Lights { get; set; } = [];

    /// <summary>Settings by light key. Lights not listed are left as they are.</summary>
    public Dictionary<string, LightSetting> Settings { get; set; } = [];

    /// <summary>Sync all: every light shows <see cref="All"/>.</summary>
    public bool Sync { get; set; }

    public LightSetting? All { get; set; }

    /// <summary>Things the user should know, e.g. vendor lighting software running at the same time.</summary>
    public List<string> Warnings { get; set; } = [];

    /// <summary>Last color shown by <see cref="LightEffect.Temperature"/>, by light key ("#RRGGBB").</summary>
    public Dictionary<string, string> Live { get; set; } = [];
}

public static class LightColor
{
    /// <summary>Cool → hot colors of <see cref="LightEffect.Temperature"/> (also drawn in the app).</summary>
    public static readonly (float At, string Color)[] Gradient =
    [
        (0f, "#0A84FF"),
        (0.35f, "#00C7BE"),
        (0.65f, "#FFD60A"),
        (1f, "#FF6A00"),
    ];

    public static bool TryParse(string? hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (hex is not { Length: 7 } || hex[0] != '#'
            || !int.TryParse(hex.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int v))
        {
            return false;
        }
        r = (byte)(v >> 16);
        g = (byte)(v >> 8);
        b = (byte)v;
        return true;
    }

    public static string ToHex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";

    /// <summary>Colors at the ends of the default scale (shown for "cool" and "hot" until the user picks their own).</summary>
    public static string DefaultCool => Gradient[0].Color;

    public static string DefaultHot => Gradient[^1].Color;

    public static bool HasOwnColors(LightSetting s) => s.CoolColor is not null || s.HotColor is not null;

    /// <summary>
    /// The scale from cool (0) to hot (1), for drawing. The default one, or with own colors a fade from the cool
    /// color to the hot one, sampled finely so that a plain gradient brush shows exactly what the lights do.
    /// </summary>
    public static (float At, string Color)[] Scale(LightSetting s)
    {
        if (!HasOwnColors(s))
        {
            return Gradient;
        }
        const int steps = 12;
        return [.. Enumerable.Range(0, steps + 1).Select(i => ((float)i / steps, Blend(s.CoolColor ?? DefaultCool, s.HotColor ?? DefaultHot, (float)i / steps)))];
    }

    /// <summary>Color for a temperature with the setting's cool/hot temperatures and colors.</summary>
    public static string ForTemperature(float temperature, LightSetting s)
    {
        float t = s.Hot > s.Cool ? Math.Clamp((temperature - s.Cool) / (s.Hot - s.Cool), 0, 1) : temperature >= s.Hot ? 1 : 0;
        return HasOwnColors(s) ? Blend(s.CoolColor ?? DefaultCool, s.HotColor ?? DefaultHot, t) : OnDefaultScale(t);
    }

    /// <summary>
    /// From <paramref name="from"/> to <paramref name="to"/> around the color wheel, the short way, so the colors
    /// in between stay as vivid as the ends (blue → red passes violet, not a dull grey). A grey or white end
    /// takes the other end's hue, so white → red goes through pink.
    /// </summary>
    public static string Blend(string from, string to, float k)
    {
        TryParse(from, out var r0, out var g0, out var b0);
        TryParse(to, out var r1, out var g1, out var b1);
        var (h0, s0, v0) = ToHsv(r0, g0, b0);
        var (h1, s1, v1) = ToHsv(r1, g1, b1);
        if (s0 < 0.05f)
        {
            h0 = h1;
        }
        if (s1 < 0.05f)
        {
            h1 = h0;
        }
        float dh = h1 - h0;
        if (dh > 180)
        {
            dh -= 360;
        }
        else if (dh < -180)
        {
            dh += 360;
        }
        float h = (h0 + dh * k + 360) % 360;
        return FromHsv(h, s0 + (s1 - s0) * k, v0 + (v1 - v0) * k);
    }

    private static (float H, float S, float V) ToHsv(byte r, byte g, byte b)
    {
        float rf = r / 255f, gf = g / 255f, bf = b / 255f;
        float max = Math.Max(rf, Math.Max(gf, bf)), min = Math.Min(rf, Math.Min(gf, bf)), d = max - min;
        float h = d == 0 ? 0
            : max == rf ? 60 * (((gf - bf) / d % 6 + 6) % 6)
            : max == gf ? 60 * ((bf - rf) / d + 2)
            : 60 * ((rf - gf) / d + 4);
        return (h, max == 0 ? 0 : d / max, max);
    }

    private static string FromHsv(float h, float s, float v)
    {
        float c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0f),
            1 => (x, c, 0f),
            2 => (0f, c, x),
            3 => (0f, x, c),
            4 => (x, 0f, c),
            _ => (c, 0f, x),
        };
        static byte B(float f) => (byte)Math.Clamp(Math.Round(f * 255), 0, 255);
        return ToHex(B(r + m), B(g + m), B(b + m));
    }

    private static string OnDefaultScale(float t)
    {
        for (int i = 1; i < Gradient.Length; i++)
        {
            var (at1, c1) = Gradient[i];
            if (t <= at1 || i == Gradient.Length - 1)
            {
                var (at0, c0) = Gradient[i - 1];
                float k = at1 > at0 ? Math.Clamp((t - at0) / (at1 - at0), 0, 1) : 1;
                TryParse(c0, out var r0, out var g0, out var b0);
                TryParse(c1, out var r1, out var g1, out var b1);
                return ToHex(Mix(r0, r1, k), Mix(g0, g1, k), Mix(b0, b1, k));
            }
        }
        return Gradient[^1].Color;
    }

    private static byte Mix(byte a, byte b, float k) => (byte)Math.Round(a + (b - a) * k);
}
