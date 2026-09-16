using System.Drawing;
using System.Runtime.InteropServices;

namespace LightstickStudio;

internal readonly record struct RgbColor(double Red, double Green, double Blue)
{
    public RgbColor Clamp() => new(
        Math.Clamp(Red, 0, 255),
        Math.Clamp(Green, 0, 255),
        Math.Clamp(Blue, 0, 255));

    public RgbColor Scale(double value) =>
        new(Red * value, Green * value, Blue * value);
}

internal sealed class ScreenColorSampler
{
    private const int SrcCopy = 0x00CC0020;
    private const int Halftone = 4;
    private const uint DibRgbColors = 0;
    private readonly Rectangle _area;
    private RgbColor _current = new(255, 0, 255);
    private long _lastChromaticAt;

    public double Smoothing { get; set; }
    public double ColorBoost { get; set; }
    public double RedBoost { get; set; }
    public double GreenBoost { get; set; }
    public double BlueBoost { get; set; }

    public ScreenColorSampler(StudioSettings settings)
    {
        var monitors = MonitorEnumerator.GetMonitors();
        if (monitors.Count == 0)
        {
            throw new InvalidOperationException("Windows 未回報可擷取的顯示器");
        }

        var virtualDesktop = monitors.Aggregate(Rectangle.Union);
        if (settings.Region is { } region)
        {
            _area = Rectangle.Intersect(
                new Rectangle(region.Left, region.Top, region.Width, region.Height),
                virtualDesktop);
            if (_area.Width <= 0 || _area.Height <= 0)
            {
                throw new InvalidOperationException("選擇的視窗位於可見桌面之外");
            }
        }
        else
        {
            var index = settings.Monitor - 1;
            if (index < 0 || index >= monitors.Count)
            {
                throw new InvalidOperationException($"顯示器必須介於 1 到 {monitors.Count}");
            }
            _area = monitors[index];
        }

        Update(settings);
    }

    public void Update(StudioSettings settings)
    {
        Smoothing = settings.Smoothing;
        ColorBoost = settings.ColorBoost;
        RedBoost = settings.RedBoost;
        GreenBoost = settings.GreenBoost;
        BlueBoost = settings.BlueBoost;
    }

    public RgbColor Sample()
    {
        byte[] bgra;
        int width;
        int height;
        try
        {
            (bgra, width, height) = CaptureDownsampled(_area);
        }
        catch
        {
            return _current;
        }

        var colorful = new List<ColorPoint>(width * height);
        for (var offset = 0; offset < bgra.Length; offset += 4)
        {
            var blue = bgra[offset] / 255.0;
            var green = bgra[offset + 1] / 255.0;
            var red = bgra[offset + 2] / 255.0;
            var maximum = Math.Max(red, Math.Max(green, blue));
            var minimum = Math.Min(red, Math.Min(green, blue));
            var saturation = maximum <= 0 ? 0 : (maximum - minimum) / maximum;
            if (maximum <= 0.10 || saturation <= 0.20) continue;

            var hue = RgbToHsv(red, green, blue).Hue;
            var weight = saturation * saturation * Math.Pow(maximum, 1.5);
            colorful.Add(new ColorPoint(red * 255, green * 255, blue * 255,
                hue, saturation, maximum, weight));
        }

        if (colorful.Count >= 12)
        {
            const int binCount = 24;
            var histogram = new double[binCount];
            foreach (var point in colorful)
            {
                var bin = (int)Math.Floor(point.Hue * binCount) % binCount;
                histogram[bin] += point.Weight;
            }

            var dominantBin = 0;
            var dominantScore = double.MinValue;
            for (var index = 0; index < binCount; index++)
            {
                var score = histogram[index] + 0.5 *
                    (histogram[(index + binCount - 1) % binCount] +
                     histogram[(index + 1) % binCount]);
                if (score > dominantScore)
                {
                    dominantScore = score;
                    dominantBin = index;
                }
            }

            var center = (dominantBin + 0.5) / binCount;
            double redTotal = 0, greenTotal = 0, blueTotal = 0, weightTotal = 0;
            foreach (var point in colorful)
            {
                var distance = Math.Abs(point.Hue - center);
                distance = Math.Min(distance, 1.0 - distance);
                if (distance > 1.5 / binCount) continue;
                redTotal += point.Red * point.Weight;
                greenTotal += point.Green * point.Weight;
                blueTotal += point.Blue * point.Weight;
                weightTotal += point.Weight;
            }

            if (weightTotal > 0)
            {
                var sampled = new RgbColor(
                    redTotal / weightTotal,
                    greenTotal / weightTotal,
                    blueTotal / weightTotal);
                var maxChannel = Math.Max(sampled.Red, Math.Max(sampled.Green, sampled.Blue));
                if (maxChannel > 0) sampled = sampled.Scale(255.0 / maxChannel);
                sampled = EnhanceColor(sampled);
                _current = Lerp(_current, sampled, Math.Clamp(Smoothing, 0, 1));
                _lastChromaticAt = Environment.TickCount64;
            }
        }
        else if (Environment.TickCount64 - _lastChromaticAt > 1200)
        {
            _current = Lerp(_current, new RgbColor(255, 255, 255),
                Math.Clamp(Smoothing * 0.2, 0, 1));
        }

        return _current.Clamp();
    }

    private RgbColor EnhanceColor(RgbColor color)
    {
        var hsv = RgbToHsv(color.Red / 255, color.Green / 255, color.Blue / 255);
        var hue = hsv.Hue;
        var saturation = hsv.Saturation + (1 - hsv.Saturation) * ColorBoost;

        var redDistance = Math.Min(hue, 1 - hue);
        var redInfluence = Math.Clamp(1 - redDistance / (50.0 / 360), 0, 1);
        var redStrength = RedBoost * redInfluence;
        saturation += (1 - saturation) * redStrength;
        var signedHue = hue <= 0.5 ? hue : hue - 1;
        signedHue *= 1 - 0.18 * redStrength;
        hue = ModOne(signedHue);

        var greenDelta = CircularDelta(hue, 1.0 / 3);
        var greenStrength = GreenBoost *
            Math.Clamp(1 - Math.Abs(greenDelta) / (55.0 / 360), 0, 1);
        saturation += (1 - saturation) * greenStrength;
        hue = ModOne(hue - greenDelta * 0.22 * greenStrength);

        var blueDelta = CircularDelta(hue, 2.0 / 3);
        var blueStrength = BlueBoost *
            Math.Clamp(1 - Math.Abs(blueDelta) / (50.0 / 360), 0, 1);
        saturation += (1 - saturation) * blueStrength;
        hue = ModOne(hue - blueDelta * 0.18 * blueStrength);

        return HsvToRgb(hue, Math.Clamp(saturation, 0, 1), hsv.Value);
    }

    public static RgbColor HsvToRgb(double hue, double saturation, double value)
    {
        hue = ModOne(hue);
        var scaled = hue * 6;
        var sector = (int)Math.Floor(scaled);
        var fraction = scaled - sector;
        var p = value * (1 - saturation);
        var q = value * (1 - saturation * fraction);
        var t = value * (1 - saturation * (1 - fraction));
        var (red, green, blue) = (sector % 6) switch
        {
            0 => (value, t, p),
            1 => (q, value, p),
            2 => (p, value, t),
            3 => (p, q, value),
            4 => (t, p, value),
            _ => (value, p, q),
        };
        return new RgbColor(red * 255, green * 255, blue * 255);
    }

    private static (double Hue, double Saturation, double Value) RgbToHsv(
        double red, double green, double blue)
    {
        var maximum = Math.Max(red, Math.Max(green, blue));
        var minimum = Math.Min(red, Math.Min(green, blue));
        var delta = maximum - minimum;
        var saturation = maximum <= 0 ? 0 : delta / maximum;
        if (delta <= 0) return (0, saturation, maximum);

        double hue;
        if (maximum == red) hue = ((green - blue) / delta) % 6;
        else if (maximum == green) hue = (blue - red) / delta + 2;
        else hue = (red - green) / delta + 4;
        return (ModOne(hue / 6), saturation, maximum);
    }

    private static double CircularDelta(double value, double center) =>
        ModOne(value - center + 0.5) - 0.5;

    private static double ModOne(double value) => (value % 1 + 1) % 1;

    private static RgbColor Lerp(RgbColor from, RgbColor to, double amount) => new(
        from.Red + (to.Red - from.Red) * amount,
        from.Green + (to.Green - from.Green) * amount,
        from.Blue + (to.Blue - from.Blue) * amount);

    private static (byte[] Pixels, int Width, int Height) CaptureDownsampled(Rectangle area)
    {
        var width = Math.Max(1, Math.Min(160, area.Width));
        var height = Math.Max(1, Math.Min(90, area.Height));
        var sourceDc = GetDC(0);
        if (sourceDc == 0) throw new InvalidOperationException("無法存取桌面畫面");
        var memoryDc = CreateCompatibleDC(sourceDc);
        var bitmap = CreateCompatibleBitmap(sourceDc, width, height);
        var previous = SelectObject(memoryDc, bitmap);
        try
        {
            SetStretchBltMode(memoryDc, Halftone);
            if (!StretchBlt(memoryDc, 0, 0, width, height, sourceDc,
                    area.Left, area.Top, area.Width, area.Height, SrcCopy))
            {
                throw new InvalidOperationException("畫面擷取暫時失敗");
            }

            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0,
                    SizeImage = (uint)(width * height * 4),
                },
            };
            var pixels = new byte[width * height * 4];
            if (GetDIBits(memoryDc, bitmap, 0, (uint)height, pixels,
                    ref info, DibRgbColors) == 0)
            {
                throw new InvalidOperationException("無法讀取畫面像素");
            }
            return (pixels, width, height);
        }
        finally
        {
            SelectObject(memoryDc, previous);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(0, sourceDc);
        }
    }

    private readonly record struct ColorPoint(
        double Red, double Green, double Blue, double Hue,
        double Saturation, double Value, double Weight);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint value);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint value);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(nint dc, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(nint target, int x, int y, int width, int height,
        nint source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, int operation);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines,
        [Out] byte[] bits, ref BitmapInfo info, uint usage);
}
