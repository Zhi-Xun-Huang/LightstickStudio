using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace LightstickStudio;

internal sealed record CaptureTarget(
    string Label, int Monitor, Rectangle? Region, nint WindowHandle = 0);

internal static class WindowEnumerator
{
    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const uint DwmExtendedFrameBounds = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int length);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        nint window, uint attribute, out NativeRect value, int size);

    public static IReadOnlyList<CaptureTarget> GetTargets()
    {
        var result = new List<CaptureTarget>();
        var monitors = MonitorEnumerator.GetMonitors();
        for (var index = 0; index < monitors.Count; index++)
        {
            var bounds = monitors[index];
            result.Add(new CaptureTarget(
                $"顯示器 {index + 1}（完整畫面）· {bounds.Width}×{bounds.Height}",
                index + 1, null));
        }

        var windows = new List<CaptureTarget>();
        var virtualBounds = monitors.Count == 0
            ? Rectangle.Empty
            : monitors.Aggregate(Rectangle.Union);
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            var titleLength = GetWindowTextLength(window);
            if (titleLength <= 0) return true;
            var title = new StringBuilder(titleLength + 1);
            GetWindowText(window, title, title.Capacity);
            if (title.ToString().Equals("Lightstick Studio",
                    StringComparison.OrdinalIgnoreCase)) return true;

            NativeRect nativeBounds;
            if (DwmGetWindowAttribute(window, DwmExtendedFrameBounds,
                    out nativeBounds, Marshal.SizeOf<NativeRect>()) != 0 &&
                !GetWindowRect(window, out nativeBounds))
            {
                return true;
            }
            var bounds = Rectangle.FromLTRB(nativeBounds.Left, nativeBounds.Top,
                nativeBounds.Right, nativeBounds.Bottom);
            if (bounds.Width < 80 || bounds.Height < 60 ||
                !bounds.IntersectsWith(virtualBounds)) return true;
            windows.Add(new CaptureTarget(
                $"視窗 · {title} · {bounds.Width}×{bounds.Height}",
                1, bounds, window));
            return true;
        }, 0);

        result.AddRange(windows
            .GroupBy(target => (target.Label, target.Region))
            .Select(group => group.First())
            .OrderBy(target => target.Label, StringComparer.CurrentCultureIgnoreCase));
        return result;
    }

    public static bool TryGetBounds(nint window, out Rectangle bounds)
    {
        NativeRect nativeBounds;
        if (window == 0 || !IsWindowVisible(window) ||
            (DwmGetWindowAttribute(window, DwmExtendedFrameBounds,
                 out nativeBounds, Marshal.SizeOf<NativeRect>()) != 0 &&
             !GetWindowRect(window, out nativeBounds)))
        {
            bounds = Rectangle.Empty;
            return false;
        }
        bounds = Rectangle.FromLTRB(nativeBounds.Left, nativeBounds.Top,
            nativeBounds.Right, nativeBounds.Bottom);
        return bounds.Width >= 80 && bounds.Height >= 60;
    }
}
