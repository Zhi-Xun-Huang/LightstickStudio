namespace LightstickStudio;

internal sealed record Uf2Volume(string Root, string Model);

internal sealed class FirmwareFlasher : IDisposable
{
    private readonly Action<object> _emit;
    private CancellationTokenSource? _cancellation;
    private Task? _worker;

    public FirmwareFlasher(Action<object> emit) => _emit = emit;

    public void Start()
    {
        if (_worker is { IsCompleted: false })
        {
            Publish("error", "韌體更新程序已在執行中");
            return;
        }

        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        _worker = Task.Run(() => FlashAsync(_cancellation.Token));
    }

    public void Cancel() => _cancellation?.Cancel();

    public static IReadOnlyList<Uf2Volume> FindXiaoUf2Volumes()
    {
        var result = new List<Uf2Volume>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                var infoPath = Path.Combine(drive.RootDirectory.FullName, "INFO_UF2.TXT");
                if (!File.Exists(infoPath)) continue;
                var info = File.ReadAllText(infoPath);
                if (!info.Contains("Board-ID: nRF52840-SeeedXiao-v1", StringComparison.Ordinal) &&
                    !info.Contains("Model: Seeed XIAO nRF52840 Plus", StringComparison.Ordinal))
                {
                    continue;
                }

                var model = info.Split('\n')
                    .Select(line => line.Trim())
                    .FirstOrDefault(line => line.StartsWith("Model:", StringComparison.Ordinal))?
                    .Split(':', 2)[1].Trim() ?? "Seeed XIAO nRF52840 Plus";
                result.Add(new Uf2Volume(drive.RootDirectory.FullName, model));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
        return result;
    }

    private async Task FlashAsync(CancellationToken cancellationToken)
    {
        var firmware = FindFirmware();
        if (firmware is null)
        {
            Publish("error", "找不到內附的 firmware.uf2");
            return;
        }

        try
        {
            Publish("waiting", "等待 XIAO-BOOT…請快速雙擊 XIAO 的 Reset 按鈕",
                progress: 5, cancelable: true);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var volumes = FindXiaoUf2Volumes();
                if (volumes.Count > 1)
                {
                    throw new InvalidOperationException("請只連接一塊處於 XIAO-BOOT 的開發板");
                }
                if (volumes.Count == 1)
                {
                    var volume = volumes[0];
                    Publish("working", $"已偵測到 {volume.Model}：{volume.Root}",
                        progress: 20, cancelable: false);
                    await CopyAndVerifyAsync(firmware, volume, cancellationToken);
                    return;
                }
                await Task.Delay(200, cancellationToken);
            }
            throw new TimeoutException(
                "60 秒內未偵測到 XIAO-BOOT。請確認 USB 已連接，再快速雙擊 Reset 後重試。");
        }
        catch (OperationCanceledException)
        {
            Publish("cancelled", "已取消等待 XIAO-BOOT", progress: 0);
        }
        catch (Exception error)
        {
            Publish("error", error.Message);
        }
    }

    private async Task CopyAndVerifyAsync(string firmware, Uf2Volume volume,
        CancellationToken cancellationToken)
    {
        var destination = Path.Combine(volume.Root, "LIGHTSTICK.UF2");
        Publish("working", $"正在將 UF2 韌體寫入 {volume.Root}…",
            progress: 35, cancelable: false);
        var sourceSize = new FileInfo(firmware).Length;
        long bytesWritten = 0;
        try
        {
            await using var source = new FileStream(firmware, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write,
                FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            var buffer = new byte[4096];
            int count;
            while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                bytesWritten += count;
            }
            await target.FlushAsync(cancellationToken);
        }
        catch (IOException) when (bytesWritten == sourceSize &&
            !FindXiaoUf2Volumes().Any(item =>
                item.Root.Equals(volume.Root, StringComparison.OrdinalIgnoreCase)))
        {
            // The UF2 drive disconnects immediately after accepting the image.
        }

        Publish("working", "UF2 已送出，正在等待控制韌體重新連線…",
            progress: 90, cancelable: false);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var controller = ControllerSerial.FindAndOpen(cancellationToken);
                Publish("success", $"完成：{controller.PortName} · {controller.Detail}",
                    progress: 100);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            await Task.Delay(400, cancellationToken);
        }

        if (FindXiaoUf2Volumes().Count > 0)
        {
            throw new InvalidOperationException(
                "UF2 已複製，但 XIAO 仍停留在 XIAO-BOOT。請按一次 Reset 或重新插拔 USB 後再試。");
        }

        _emit(new
        {
            kind = "firmware",
            state = "success",
            progress = 100,
            verified = false,
            message = "UF2 已成功寫入；XIAO 已離開 Bootloader，但暫時無法驗證通訊 COM。請重新插拔 USB。",
        });
    }

    private static string? FindFirmware()
    {
        var relative = Path.Combine("resources", "firmware",
            "xiao_nrf52840_plus", "firmware.uf2");
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, relative),
            Path.Combine(BackendClient.FindProjectRoot(), relative),
            Path.Combine(BackendClient.FindProjectRoot(), ".pio", "build",
                "xiao_nrf52840_plus", "firmware.uf2"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private void Publish(string state, string message, int? progress = null,
        bool? cancelable = null)
    {
        var values = new Dictionary<string, object?>
        {
            ["kind"] = "firmware",
            ["state"] = state,
            ["message"] = message,
        };
        if (progress is not null) values["progress"] = progress.Value;
        if (cancelable is not null) values["cancelable"] = cancelable.Value;
        _emit(values);
    }

    public void Dispose()
    {
        Cancel();
        _cancellation?.Dispose();
    }
}
