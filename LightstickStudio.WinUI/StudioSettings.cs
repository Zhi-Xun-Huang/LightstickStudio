using System.Globalization;
using System.Text.Json;

namespace LightstickStudio;

internal sealed record CaptureRegion(int Left, int Top, int Width, int Height);

internal sealed record StudioSettings
{
    public string ColorSource { get; init; } = "screen";
    public string BrightnessSource { get; init; } = "audio";
    public string Effect { get; init; } = "steady";
    public string SolidColor { get; init; } = "#FF2040";
    public double FixedBrightness { get; init; } = 0.75;
    public double RainbowSpeed { get; init; } = 0.10;
    public bool RainbowReverse { get; init; }
    public int Monitor { get; init; } = 1;
    public CaptureRegion? Region { get; init; }
    public int Fps { get; init; } = 25;
    public double Sensitivity { get; init; } = 1.35;
    public double Minimum { get; init; }
    public double Maximum { get; init; } = 1.0;
    public double AudioGate { get; init; } = 0.08;
    public double BrightnessGamma { get; init; } = 1.35;
    public double Smoothing { get; init; } = 0.24;
    public double ColorBoost { get; init; } = 0.38;
    public double RedBoost { get; init; } = 0.72;
    public double GreenBoost { get; init; } = 0.72;
    public double BlueBoost { get; init; }

    public static StudioSettings FromJson(JsonElement value)
    {
        CaptureRegion? region = null;
        if (value.TryGetProperty("region", out var regionValue) &&
            regionValue.ValueKind == JsonValueKind.Object)
        {
            region = new CaptureRegion(
                GetInt(regionValue, "left", 0),
                GetInt(regionValue, "top", 0),
                Math.Max(1, GetInt(regionValue, "width", 1)),
                Math.Max(1, GetInt(regionValue, "height", 1)));
        }

        return new StudioSettings
        {
            ColorSource = GetString(value, "color_source", "screen"),
            BrightnessSource = GetString(value, "brightness_source", "audio"),
            Effect = GetString(value, "effect", "steady"),
            SolidColor = GetString(value, "solid_color", "#FF2040"),
            FixedBrightness = GetDouble(value, "fixed_brightness", 0.75),
            RainbowSpeed = GetDouble(value, "rainbow_speed", 0.10),
            RainbowReverse = GetBool(value, "rainbow_reverse", false),
            Monitor = Math.Max(1, GetInt(value, "monitor", 1)),
            Region = region,
            Fps = Math.Clamp(GetInt(value, "fps", 25), 10, 30),
            Sensitivity = GetDouble(value, "sensitivity", 1.35),
            Minimum = GetDouble(value, "minimum", 0),
            Maximum = GetDouble(value, "maximum", 1),
            AudioGate = GetDouble(value, "audio_gate", 0.08),
            BrightnessGamma = GetDouble(value, "brightness_gamma", 1.35),
            Smoothing = GetDouble(value, "smoothing", 0.24),
            ColorBoost = GetDouble(value, "color_boost", 0.38),
            RedBoost = GetDouble(value, "red_boost", 0.72),
            GreenBoost = GetDouble(value, "green_boost", 0.72),
            BlueBoost = GetDouble(value, "blue_boost", 0),
        };
    }

    public (double Red, double Green, double Blue) SolidRgb()
    {
        var text = SolidColor.Trim().TrimStart('#');
        if (text.Length != 6 ||
            !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return (255, 32, 64);
        }
        return ((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
    }

    private static string GetString(JsonElement value, string name, string fallback) =>
        value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString() ?? fallback : fallback;

    private static double GetDouble(JsonElement value, string name, double fallback) =>
        value.TryGetProperty(name, out var item) && item.TryGetDouble(out var number)
            ? number : fallback;

    private static int GetInt(JsonElement value, string name, int fallback) =>
        value.TryGetProperty(name, out var item) && item.TryGetInt32(out var number)
            ? number : fallback;

    private static bool GetBool(JsonElement value, string name, bool fallback) =>
        value.TryGetProperty(name, out var item) &&
        item.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? item.GetBoolean() : fallback;
}
