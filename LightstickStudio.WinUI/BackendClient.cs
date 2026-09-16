using System.Text.Json;

namespace LightstickStudio;

/// <summary>
/// In-process C# backend for the WinUI application. The public message shape is
/// intentionally unchanged so the UI remains decoupled from the controller.
/// </summary>
internal sealed class BackendClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly LightstickEngine _engine;
    private readonly FirmwareFlasher _flasher;
    private bool _started;

    public event Action<JsonElement>? MessageReceived;
    public event Action<string>? Failed;

    public BackendClient()
    {
        _engine = new LightstickEngine(Emit);
        _flasher = new FirmwareFlasher(Emit);
    }

    public static string FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LightstickStudio.slnx")) ||
                Directory.Exists(Path.Combine(directory.FullName, "resources", "firmware")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        return AppContext.BaseDirectory;
    }

    public Task StartAsync()
    {
        _started = true;
        Emit(new { kind = "backend", state = "ready" });
        return Task.CompletedTask;
    }

    public async Task SendAsync(object message)
    {
        if (!_started)
        {
            throw new InvalidOperationException("C# 背景引擎尚未啟動");
        }

        var payload = JsonSerializer.SerializeToElement(message);
        var command = payload.TryGetProperty("command", out var commandValue)
            ? commandValue.GetString() : null;

        await _commandLock.WaitAsync();
        try
        {
            switch (command)
            {
                case "scan":
                    await ScanAsync();
                    break;
                case "start":
                case "restart":
                    await _engine.RestartAsync(ReadSettings(payload));
                    break;
                case "update":
                    _engine.Update(ReadSettings(payload));
                    break;
                case "stop":
                    await _engine.StopAsync(TimeSpan.FromSeconds(5));
                    break;
                case "flash":
                    if (_engine.Running)
                    {
                        Emit(new { kind = "firmware", state = "error", message = "請先停止即時控制" });
                    }
                    else
                    {
                        _flasher.Start();
                    }
                    break;
                case "cancel_flash":
                    _flasher.Cancel();
                    break;
                case "shutdown":
                    await _engine.StopAsync(TimeSpan.FromSeconds(3));
                    break;
                default:
                    Emit(new { kind = "backend", state = "error", message = $"未知指令：{command}" });
                    break;
            }
        }
        catch (Exception error)
        {
            Emit(new { kind = "backend", state = "error", message = error.Message });
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private static StudioSettings ReadSettings(JsonElement payload)
    {
        return payload.TryGetProperty("settings", out var settings)
            ? StudioSettings.FromJson(settings)
            : new StudioSettings();
    }

    private async Task ScanAsync()
    {
        try
        {
            using var controller = await Task.Run(
                () => ControllerSerial.FindAndOpen(CancellationToken.None));
            Emit(new
            {
                kind = "device",
                connected = true,
                mode = "runtime",
                port = controller.PortName,
                detail = controller.Detail,
            });
        }
        catch (Exception error)
        {
            var volumes = FirmwareFlasher.FindXiaoUf2Volumes();
            if (volumes.Count == 1)
            {
                Emit(new
                {
                    kind = "device",
                    connected = true,
                    mode = "uf2",
                    port = volumes[0].Root,
                    detail = volumes[0].Model,
                });
                return;
            }

            Emit(new
            {
                kind = "device",
                connected = false,
                message = error.Message,
            });
        }
    }

    private void Emit(object message)
    {
        try
        {
            MessageReceived?.Invoke(JsonSerializer.SerializeToElement(message));
        }
        catch (Exception error)
        {
            Failed?.Invoke(error.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _started = false;
        _flasher.Cancel();
        await _engine.StopAsync(TimeSpan.FromSeconds(3));
        _flasher.Dispose();
        _commandLock.Dispose();
    }
}
