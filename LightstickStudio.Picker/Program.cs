using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.Json;
using System.Windows.Forms;

namespace LightstickStudio.Picker;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // Packaging check: initialize the real WinForms runtime without taking
        // a screenshot, displaying an overlay or changing any hardware state.
        if (args.FirstOrDefault() == "--self-test")
        {
            Console.WriteLine("{\"ready\":true,\"tool\":\"LightstickStudio.Picker\"}");
            return;
        }
        var colorMode = args.FirstOrDefault()?.Equals(
            "color", StringComparison.OrdinalIgnoreCase) == true;
        using var picker = new ScreenPickerForm(colorMode);
        if (picker.ShowDialog() != DialogResult.OK)
        {
            Console.WriteLine("{\"cancelled\":true}");
            return;
        }

        if (picker.SelectedColor is Color color)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                color = $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            }));
        }
        else if (picker.SelectedRegion is Rectangle region)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                left = region.Left,
                top = region.Top,
                width = region.Width,
                height = region.Height,
            }));
        }
    }
}

internal sealed class ScreenPickerForm : Form
{
    private readonly bool _colorMode;
    private readonly Bitmap _desktop;
    private Point _start;
    private Point _current;
    private bool _dragging;

    public Rectangle? SelectedRegion { get; private set; }
    public Color? SelectedColor { get; private set; }

    public ScreenPickerForm(bool colorMode)
    {
        _colorMode = colorMode;
        var virtualScreen = SystemInformation.VirtualScreen;
        _desktop = new Bitmap(virtualScreen.Width, virtualScreen.Height);
        using (var graphics = Graphics.FromImage(_desktop))
        {
            graphics.CopyFromScreen(virtualScreen.Left, virtualScreen.Top, 0, 0,
                virtualScreen.Size, CopyPixelOperation.SourceCopy);
        }

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = virtualScreen;
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;
        KeyPreview = true;
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        eventArgs.Graphics.DrawImageUnscaled(_desktop, 0, 0);
        using var shade = new SolidBrush(Color.FromArgb(92, 5, 10, 20));
        eventArgs.Graphics.FillRectangle(shade, ClientRectangle);

        var selection = CurrentSelection();
        if (!_colorMode && selection.Width > 0 && selection.Height > 0)
        {
            eventArgs.Graphics.DrawImage(_desktop, selection, selection, GraphicsUnit.Pixel);
            using var outline = new Pen(Color.FromArgb(255, 34, 211, 238), 3);
            eventArgs.Graphics.DrawRectangle(outline, selection);
        }

        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var panel = new SolidBrush(Color.FromArgb(225, 17, 24, 39));
        eventArgs.Graphics.FillRoundedRectangle(panel,
            new Rectangle(18, 18, 590, 54), 10);
        using var font = new Font("Segoe UI", 14, FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.White);
        var instruction = _colorMode
            ? "點擊畫面取得固定顏色 · Esc 取消"
            : "拖曳框選畫面採樣區域 · Esc 取消";
        eventArgs.Graphics.DrawString(instruction, font, textBrush, 36, 36);

        if (_colorMode)
        {
            using var crosshair = new Pen(Color.FromArgb(255, 34, 211, 238), 2);
            eventArgs.Graphics.DrawLine(crosshair, _current.X - 18, _current.Y,
                _current.X + 18, _current.Y);
            eventArgs.Graphics.DrawLine(crosshair, _current.X, _current.Y - 18,
                _current.X, _current.Y + 18);
        }
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        base.OnMouseDown(eventArgs);
        if (eventArgs.Button != MouseButtons.Left) return;
        _start = _current = eventArgs.Location;
        _dragging = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        base.OnMouseMove(eventArgs);
        _current = eventArgs.Location;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        base.OnMouseUp(eventArgs);
        if (eventArgs.Button != MouseButtons.Left || !_dragging) return;
        _dragging = false;
        _current = eventArgs.Location;

        if (_colorMode)
        {
            SelectedColor = AverageColor(_current, radius: 3);
            DialogResult = DialogResult.OK;
            Close();
            return;
        }

        var selected = CurrentSelection();
        if (selected.Width < 16 || selected.Height < 16)
        {
            Invalidate();
            return;
        }
        var virtualScreen = SystemInformation.VirtualScreen;
        SelectedRegion = new Rectangle(
            virtualScreen.Left + selected.Left,
            virtualScreen.Top + selected.Top,
            selected.Width,
            selected.Height);
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        if (eventArgs.KeyCode == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            Close();
            return;
        }
        base.OnKeyDown(eventArgs);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _desktop.Dispose();
        base.Dispose(disposing);
    }

    private Rectangle CurrentSelection()
    {
        var left = Math.Min(_start.X, _current.X);
        var top = Math.Min(_start.Y, _current.Y);
        return new Rectangle(left, top,
            Math.Abs(_current.X - _start.X), Math.Abs(_current.Y - _start.Y));
    }

    private Color AverageColor(Point center, int radius)
    {
        long red = 0, green = 0, blue = 0;
        var count = 0;
        for (var y = Math.Max(0, center.Y - radius);
             y <= Math.Min(_desktop.Height - 1, center.Y + radius); y++)
        {
            for (var x = Math.Max(0, center.X - radius);
                 x <= Math.Min(_desktop.Width - 1, center.X + radius); x++)
            {
                var pixel = _desktop.GetPixel(x, y);
                red += pixel.R;
                green += pixel.G;
                blue += pixel.B;
                count++;
            }
        }
        return Color.FromArgb((int)(red / count), (int)(green / count),
            (int)(blue / count));
    }
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush,
        Rectangle bounds, int radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }
}
