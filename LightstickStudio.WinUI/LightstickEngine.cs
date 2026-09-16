namespace LightstickStudio;

internal sealed class LightstickEngine
{
    private readonly Action<object> _emit;
    private readonly object _settingsLock = new();
    private StudioSettings _settings = new();
    private CancellationTokenSource? _cancellation;
    private Task? _worker;

    public LightstickEngine(Action<object> emit) => _emit = emit;

    public bool Running => _worker is { IsCompleted: false };

    public void Update(StudioSettings settings)
    {
        lock (_settingsLock) _settings = settings;
    }

    private StudioSettings Snapshot()
    {
        lock (_settingsLock) return _settings;
    }

    public async Task RestartAsync(StudioSettings settings)
    {
        await StopAsync(TimeSpan.FromSeconds(5));
        Update(settings);
        _cancellation = new CancellationTokenSource();
        _worker = Task.Run(() => RunAsync(_cancellation.Token));
    }

    public async Task StopAsync(TimeSpan timeout)
    {
        var worker = _worker;
        if (worker is null) return;
        _cancellation?.Cancel();
        if (await Task.WhenAny(worker, Task.Delay(timeout)) != worker)
        {
            throw new TimeoutException("上一個控制工作未能及時停止");
        }
        try { await worker; } catch (OperationCanceledException) { }
        _worker = null;
        _cancellation?.Dispose();
        _cancellation = null;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        ControllerSerial? controller = null;
        ScreenColorSampler? screenSampler = null;
        AudioLoopbackAnalyzer? audio = null;
        string? screenKey = null;
        var currentAudioLevel = 0.0;
        var startedAt = Environment.TickCount64 / 1000.0;
        var effectStartedAt = startedAt;
        var activeEffect = "steady";
        var lastStatusAt = 0.0;

        try
        {
            _emit(new { kind = "engine", state = "connecting", message = "正在尋找 XIAO…" });
            controller = await Task.Run(
                () => ControllerSerial.FindAndOpen(cancellationToken), cancellationToken);
            _emit(new
            {
                kind = "device",
                connected = true,
                mode = "runtime",
                port = controller.PortName,
                detail = controller.Detail,
            });
            _emit(new { kind = "engine", state = "running", message = "場控執行中" });

            var nextFrame = Environment.TickCount64 / 1000.0;
            while (!cancellationToken.IsCancellationRequested)
            {
                var settings = Snapshot();
                var frameSeconds = 1.0 / Math.Clamp(settings.Fps, 10, 30);
                var now = Environment.TickCount64 / 1000.0;
                if (!string.Equals(settings.Effect, activeEffect, StringComparison.Ordinal))
                {
                    activeEffect = settings.Effect;
                    effectStartedAt = now;
                }

                RgbColor baseColor;
                if (settings.ColorSource == "screen")
                {
                    var newScreenKey = ScreenKey(settings);
                    if (screenSampler is null || newScreenKey != screenKey)
                    {
                        screenSampler = new ScreenColorSampler(settings);
                        screenKey = newScreenKey;
                    }
                    else
                    {
                        screenSampler.Update(settings);
                    }
                    baseColor = screenSampler.Sample();
                }
                else if (settings.ColorSource == "rainbow")
                {
                    var direction = settings.RainbowReverse ? -1.0 : 1.0;
                    var hue = ModOne((now - startedAt) * settings.RainbowSpeed * direction);
                    baseColor = ScreenColorSampler.HsvToRgb(hue, 1, 1);
                }
                else
                {
                    var solid = settings.SolidRgb();
                    baseColor = new RgbColor(solid.Red, solid.Green, solid.Blue);
                }

                double rawAudioLevel;
                double brightness;
                if (settings.BrightnessSource == "audio")
                {
                    if (audio is null)
                    {
                        audio = new AudioLoopbackAnalyzer();
                        audio.Start();
                        _emit(new { kind = "audio", name = audio.DeviceName });
                    }

                    var reading = audio.Read();
                    if (!double.IsPositiveInfinity(reading.AgeSeconds))
                    {
                        currentAudioLevel = reading.Level;
                    }
                    if (reading.AgeSeconds > 0.12)
                    {
                        currentAudioLevel *= 0.45;
                        if (currentAudioLevel < 0.005) currentAudioLevel = 0;
                    }

                    rawAudioLevel = Math.Clamp(
                        currentAudioLevel * settings.Sensitivity, 0, 1);
                    var gated = Math.Max(0,
                        (rawAudioLevel - settings.AudioGate) /
                        Math.Max(1 - settings.AudioGate, 0.05));
                    var reactive = Math.Pow(gated, settings.BrightnessGamma);
                    brightness = settings.Minimum +
                        (settings.Maximum - settings.Minimum) * reactive;
                }
                else
                {
                    rawAudioLevel = 0;
                    brightness = settings.FixedBrightness;
                    audio?.Dispose();
                    audio = null;
                    currentAudioLevel = 0;
                }

                var scaledElapsed = (now - effectStartedAt) *
                    Math.Clamp(settings.RainbowSpeed, 0.02, 0.5) / 0.10;
                brightness *= EffectEnvelope(settings.Effect, scaledElapsed);
                brightness = Math.Clamp(brightness, 0, 1);
                var output = baseColor.Scale(brightness).Clamp();
                controller.SendFrame(output.Red, output.Green, output.Blue);

                if (now - lastStatusAt >= 0.08)
                {
                    _emit(new
                    {
                        kind = "frame",
                        color = new[]
                        {
                            (int)output.Red, (int)output.Green, (int)output.Blue,
                        },
                        base_color = new[]
                        {
                            (int)baseColor.Red, (int)baseColor.Green, (int)baseColor.Blue,
                        },
                        brightness,
                        audio_level = rawAudioLevel,
                    });
                    lastStatusAt = now;
                }

                nextFrame += frameSeconds;
                var remaining = nextFrame - Environment.TickCount64 / 1000.0;
                if (remaining > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(remaining), cancellationToken);
                }
                else
                {
                    nextFrame = Environment.TickCount64 / 1000.0;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            _emit(new { kind = "engine", state = "error", message = error.Message });
        }
        finally
        {
            audio?.Dispose();
            if (controller is not null)
            {
                try
                {
                    controller.SendFrame(0, 0, 0);
                    await Task.Delay(80);
                }
                catch { }
                controller.Dispose();
            }
            _emit(new { kind = "engine", state = "stopped", message = "已停止並熄燈" });
        }
    }

    private static string ScreenKey(StudioSettings settings) => settings.Region is { } region
        ? $"region:{region.Left}:{region.Top}:{region.Width}:{region.Height}"
        : $"monitor:{settings.Monitor}";

    private static double EffectEnvelope(string effect, double elapsed) => effect switch
    {
        "breathing" => 0.06 + 0.94 *
            (0.5 - 0.5 * Math.Cos(ModOne(elapsed / 3.0) * Math.PI * 2)),
        "blink" => ModOne(elapsed / 0.7) < 0.5 ? 1.0 : 0.0,
        _ => 1.0,
    };

    private static double ModOne(double value) => (value % 1 + 1) % 1;
}
