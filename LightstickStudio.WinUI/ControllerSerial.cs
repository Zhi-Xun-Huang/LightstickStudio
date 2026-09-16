using System.IO.Ports;
using System.Text;
using Microsoft.Win32;

namespace LightstickStudio;

internal sealed class ControllerSerial : IDisposable
{
    private const string Identity = "ID JFKJ_LIGHTSTICK_TX_V1";
    private readonly SerialPort _port;

    private ControllerSerial(SerialPort port, string detail)
    {
        _port = port;
        Detail = detail;
    }

    public string PortName => _port.PortName;
    public string Detail { get; }

    public static ControllerSerial FindAndOpen(CancellationToken cancellationToken)
    {
        var candidates = FindSeeedSerialPorts();
        if (candidates.Count == 0)
        {
            var connected = string.Join(", ", SerialPort.GetPortNames().OrderBy(ComPortNumber));
            throw new InvalidOperationException(
                $"找不到 XIAO nRF52840 USB 串口（目前串口：{(connected.Length == 0 ? "無" : connected)}）");
        }

        var failures = new List<string>();
        foreach (var candidate in candidates.OrderBy(ComPortNumber))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SerialPort? port = null;
            try
            {
                port = new SerialPort(candidate, 115200, Parity.None, 8, StopBits.One)
                {
                    Encoding = Encoding.ASCII,
                    NewLine = "\n",
                    ReadTimeout = 120,
                    WriteTimeout = 500,
                    DtrEnable = true,
                    RtsEnable = false,
                };
                port.Open();
                Wait(cancellationToken, 120);
                port.DiscardInBuffer();
                port.Write("identify\n");

                var response = new StringBuilder();
                var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(900);
                while (DateTime.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (port.BytesToRead > 0)
                    {
                        response.Append(port.ReadExisting());
                        if (response.ToString().Contains(Identity, StringComparison.Ordinal))
                        {
                            return new ControllerSerial(port, "JFKJ_LIGHTSTICK_TX_V1");
                        }
                    }
                    Wait(cancellationToken, 20);
                }
                failures.Add($"{candidate}: 未收到韌體識別回覆");
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or
                UnauthorizedAccessException or TimeoutException or ArgumentException)
            {
                failures.Add($"{candidate}: {error.Message}");
            }

            port?.Dispose();
        }

        throw new InvalidOperationException(
            $"已找到 XIAO，但未識別到燈棒控制韌體（{string.Join("；", failures)}）。請關閉 Serial Monitor。" );
    }

    public void SendFrame(double red, double green, double blue)
    {
        var r = (byte)Math.Clamp((int)Math.Round(red), 0, 255);
        var g = (byte)Math.Clamp((int)Math.Round(green), 0, 255);
        var b = (byte)Math.Clamp((int)Math.Round(blue), 0, 255);
        _port.Write($"frame {r} {g} {b}\n");
    }

    public void Dispose() => _port.Dispose();

    private static IReadOnlyCollection<string> FindSeeedSerialPorts()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\USB", writable: false);
            if (usb is null) return result;

            foreach (var deviceName in usb.GetSubKeyNames())
            {
                if (!deviceName.StartsWith("VID_2886&PID_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var device = usb.OpenSubKey(deviceName, writable: false);
                if (device is null) continue;
                foreach (var instanceName in device.GetSubKeyNames())
                {
                    using var parameters = device.OpenSubKey(
                        $@"{instanceName}\Device Parameters", writable: false);
                    if (parameters?.GetValue("PortName") is string portName &&
                        portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(portName);
                    }
                }
            }
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            // The normal desktop process can read this key. If policy blocks it,
            // report no candidates rather than probing unrelated serial devices.
        }
        var connected = SerialPort.GetPortNames()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        result.IntersectWith(connected);
        return result;
    }

    private static int ComPortNumber(string name) =>
        int.TryParse(name.AsSpan(3), out var value) ? value : int.MaxValue;

    private static void Wait(CancellationToken cancellationToken, int milliseconds)
    {
        if (cancellationToken.WaitHandle.WaitOne(milliseconds))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
