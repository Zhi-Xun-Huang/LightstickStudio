using System.Drawing;
using System.Runtime.InteropServices;

namespace LightstickStudio;

internal static class MonitorEnumerator
{
    private delegate bool MonitorEnumProc(
        nint monitor, nint hdc, nint rect, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        nint hdc, nint clip, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    public static IReadOnlyList<Rectangle> GetMonitors()
    {
        var result = new List<Rectangle>();
        EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo
            {
                Size = (uint)Marshal.SizeOf<MonitorInfo>(),
                Device = string.Empty,
            };
            if (GetMonitorInfo(monitor, ref info))
            {
                result.Add(Rectangle.FromLTRB(
                    info.Monitor.Left, info.Monitor.Top,
                    info.Monitor.Right, info.Monitor.Bottom));
            }
            return true;
        }, 0);
        return result;
    }
}
