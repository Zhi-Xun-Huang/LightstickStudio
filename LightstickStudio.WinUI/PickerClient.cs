using System.Diagnostics;
using System.Drawing;
using System.Text.Json;

namespace LightstickStudio;

internal static class PickerClient
{
    public static async Task<Color?> PickColorAsync()
    {
        var result = await RunAsync("color");
        if (result is null || !result.Value.TryGetProperty("color", out var colorValue))
        {
            return null;
        }
        var text = colorValue.GetString()?.TrimStart('#');
        if (text?.Length != 6 || !int.TryParse(text,
                System.Globalization.NumberStyles.HexNumber, null, out var rgb))
        {
            return null;
        }
        return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
    }

    public static async Task<Rectangle?> PickRegionAsync()
    {
        var result = await RunAsync("region");
        if (result is null || result.Value.TryGetProperty("cancelled", out _))
        {
            return null;
        }
        return new Rectangle(
            result.Value.GetProperty("left").GetInt32(),
            result.Value.GetProperty("top").GetInt32(),
            result.Value.GetProperty("width").GetInt32(),
            result.Value.GetProperty("height").GetInt32());
    }

    private static async Task<JsonElement?> RunAsync(string mode)
    {
        var root = BackendClient.FindProjectRoot();
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "picker", "LightstickStudio.Picker.exe"),
            Path.Combine(root, "LightstickStudio.Picker", "bin", "x64", "Debug", "net8.0-windows", "LightstickStudio.Picker.exe"),
            Path.Combine(root, "LightstickStudio.Picker", "bin", "Debug", "net8.0-windows", "LightstickStudio.Picker.exe"),
            Path.Combine(root, "LightstickStudio.Picker", "bin", "x64", "Release", "net8.0-windows", "LightstickStudio.Picker.exe"),
            Path.Combine(root, "LightstickStudio.Picker", "bin", "Release", "net8.0-windows", "LightstickStudio.Picker.exe"),
            Path.Combine(AppContext.BaseDirectory, "LightstickStudio.Picker.exe"),
        };
        var executable = candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("找不到原生畫面框選工具");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(mode);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("無法啟動畫面框選工具");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? $"取色工具異常結束（代碼 {process.ExitCode}）。"
                    : error.Trim());
        }
        if (string.IsNullOrWhiteSpace(output)) return null;
        using var document = JsonDocument.Parse(output);
        return document.RootElement.Clone();
    }
}
