using System.Globalization;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;
using DrawingColor = System.Drawing.Color;
using DrawingRectangle = System.Drawing.Rectangle;
using UiColor = Windows.UI.Color;

namespace LightstickStudio;

public sealed partial class MainWindow : Window
{
    private readonly BackendClient _backend = new();
    private readonly DispatcherTimer _settingsTimer = new();
    private readonly DispatcherTimer _windowTrackingTimer = new();
    private readonly DispatcherTimer _deviceScanTimer = new();
    private DrawingRectangle? _region;
    private bool _uiReady;
    private bool _running;
    private bool _flashing;
    private bool _scanInProgress;
    private bool _closeConfirmed;
    private bool _closeDialogOpen;
    private string _lastDeviceState = string.Empty;
    private string _colorSource = "screen";
    private string _brightnessSource = "audio";
    private string _effect = "steady";
    private AppWindow? _appWindow;

    public MainWindow()
    {
        InitializeComponent();
        var version = typeof(MainWindow).Assembly.GetName().Version;
        AboutVersionText.Text = version is null
            ? "版本資訊無法取得"
            : $"版本 {version.Major}.{version.Minor}.{version.Build}";
        ConfigureWindow();
        PopulateCaptureTargets();
        LoadSettings();

        _settingsTimer.Interval = TimeSpan.FromMilliseconds(90);
        _settingsTimer.Tick += SettingsTimer_Tick;
        _windowTrackingTimer.Interval = TimeSpan.FromMilliseconds(500);
        _windowTrackingTimer.Tick += WindowTrackingTimer_Tick;
        _windowTrackingTimer.Start();
        _deviceScanTimer.Interval = TimeSpan.FromSeconds(2);
        _deviceScanTimer.Tick += DeviceScanTimer_Tick;
        _backend.MessageReceived += Backend_MessageReceived;
        _backend.Failed += Backend_Failed;
        Closed += MainWindow_Closed;
        RootGrid.Loaded += RootGrid_Loaded;
        _uiReady = true;
        UpdateVisualSettings();
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs args)
    {
        RootGrid.Loaded -= RootGrid_Loaded;
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "使用前免責聲明",
            Content = "Lightstick Studio 是非官方社群研究工具，與遊戲發行商、"
                    + "演唱會主辦單位及燈棒製造商均無隸屬或合作關係。\n\n"
                    + "本程式僅供合法持有的燈棒進行個人研究與控制。請勿在演唱會、"
                    + "公共活動或他人設備附近擅自發射控制訊號，以免干擾官方場控或他人設備。\n\n"
                    + "使用者應自行確認並遵守所在地法規，並自行承擔操作、改裝及無線發射所造成的風險。"
                    + "本程式依現況提供，開發者不對設備損壞、資料損失或其他衍生損害負責。",
            PrimaryButtonText = "同意並繼續",
            CloseButtonText = "退出程式",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            _closeConfirmed = true;
            Close();
            return;
        }
        await StartBackendAsync();
    }

    private void ConfigureWindow()
    {
        var handle = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(handle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "icon", "app.ico"));
        _appWindow.Resize(new SizeInt32(1120, 850));
        _appWindow.Closing += AppWindow_Closing;
    }

    private void PopulateCaptureTargets()
    {
        var previousLabel = (MonitorCombo.SelectedItem as ComboBoxItem)?.Content?.ToString();
        MonitorCombo.Items.Clear();
        var targets = WindowEnumerator.GetTargets();
        foreach (var target in targets)
        {
            MonitorCombo.Items.Add(new ComboBoxItem
            {
                Content = target.Label,
                Tag = target,
            });
        }
        var previousIndex = targets
            .Select((target, index) => (target, index))
            .FirstOrDefault(item => item.target.Label == previousLabel).index;
        MonitorCombo.SelectedIndex = targets.Count > 0
            ? Math.Clamp(previousIndex, 0, targets.Count - 1) : -1;
    }

    private async Task StartBackendAsync()
    {
        try
        {
            await _backend.StartAsync();
        }
        catch (Exception error)
        {
            ShowStatus("背景引擎無法啟動", error.Message, InfoBarSeverity.Error);
        }
    }

    private void Backend_MessageReceived(JsonElement message)
    {
        DispatcherQueue.TryEnqueue(() => HandleBackendMessage(message));
    }

    private void Backend_Failed(string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _running = false;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            ShowStatus("背景引擎已停止", message, InfoBarSeverity.Error);
        });
    }

    private void HandleBackendMessage(JsonElement message)
    {
        var kind = GetString(message, "kind");
        switch (kind)
        {
            case "backend":
                if (GetString(message, "state") == "ready")
                {
                    ShowStatus("背景引擎已就緒", "正在尋找 XIAO…", InfoBarSeverity.Informational);
                    _deviceScanTimer.Start();
                    _ = RequestDeviceScanAsync();
                }
                else if (GetString(message, "state") == "error")
                {
                    ShowStatus("背景引擎錯誤", GetString(message, "message"), InfoBarSeverity.Error);
                }
                break;
            case "device":
                if (GetBool(message, "connected"))
                {
                    var port = GetString(message, "port");
                    var detail = GetString(message, "detail");
                    var mode = GetString(message, "mode");
                    var signature = $"connected:{mode}:{port}:{detail}";
                    if (signature == _lastDeviceState)
                    {
                        break;
                    }
                    _lastDeviceState = signature;
                    if (mode is "bootloader" or "uf2")
                    {
                        var transport = mode == "uf2" ? "UF2 Bootloader" : "Bootloader";
                        ShowStatus($"XIAO {transport} 已連線", $"{port} · 可直接安裝韌體",
                            InfoBarSeverity.Informational);
                        FirmwareStatusText.Text = $"已偵測到 {transport} · {port} · 等待安裝韌體";
                    }
                    else
                    {
                        ShowStatus("XIAO 已連線", $"{port} · {detail}", InfoBarSeverity.Success);
                        FirmwareStatusText.Text = $"已安裝相容韌體 · {port} · JFKJ_LIGHTSTICK_TX_V1";
                    }
                }
                else
                {
                    if (_lastDeviceState == "disconnected")
                    {
                        break;
                    }
                    _lastDeviceState = "disconnected";
                    ShowStatus("尚未找到相容的 XIAO", "請連接控制器，或關閉占用串口的程式。", InfoBarSeverity.Warning);
                    FirmwareStatusText.Text = "未識別到控制韌體；可連接 XIAO 後安裝";
                }
                break;
            case "engine":
                HandleEngineEvent(message);
                break;
            case "frame":
                HandleFrameEvent(message);
                break;
            case "audio":
                AudioDeviceText.Text = $"音訊：{GetString(message, "name")}";
                break;
            case "firmware":
                HandleFirmwareEvent(message);
                break;
        }
    }

    private void HandleEngineEvent(JsonElement message)
    {
        var state = GetString(message, "state");
        var detail = GetString(message, "message");
        switch (state)
        {
            case "connecting":
                ShowStatus("正在連接控制器", detail, InfoBarSeverity.Informational);
                break;
            case "running":
                _running = true;
                StartButton.IsEnabled = false;
                StopButton.IsEnabled = true;
                ShowStatus("場控執行中", $"模式：{CurrentModeLabel()}",
                    InfoBarSeverity.Success);
                break;
            case "error":
                _running = false;
                StartButton.IsEnabled = true;
                StopButton.IsEnabled = false;
                ShowStatus("控制器錯誤", detail, InfoBarSeverity.Error);
                break;
            case "stopped":
                _running = false;
                StartButton.IsEnabled = true;
                StopButton.IsEnabled = false;
                ResetOutputPreview();
                ShowStatus("控制已停止", detail, InfoBarSeverity.Informational);
                break;
        }
    }

    private void HandleFrameEvent(JsonElement message)
    {
        if (!message.TryGetProperty("color", out var colorArray) ||
            colorArray.GetArrayLength() < 3)
        {
            return;
        }
        var red = colorArray[0].GetInt32();
        var green = colorArray[1].GetInt32();
        var blue = colorArray[2].GetInt32();
        var color = ColorHelper.FromArgb(255, (byte)red, (byte)green, (byte)blue);
        OutputSwatch.Background = new SolidColorBrush(color);
        OutputHexText.Text = $"#{red:X2}{green:X2}{blue:X2}";
        var brightness = GetDouble(message, "brightness");
        OutputDetailText.Text = $"RGB {red}, {green}, {blue} · 亮度 {Math.Round(brightness * 100)}%";
        AudioMeter.Value = Math.Round(GetDouble(message, "audio_level") * 100);
    }

    private void ResetOutputPreview()
    {
        OutputSwatch.Background = new SolidColorBrush(Colors.Black);
        OutputHexText.Text = "#000000";
        OutputDetailText.Text = "RGB 0, 0, 0 · 亮度 0%";
        AudioMeter.Value = 0;
    }

    private void HandleFirmwareEvent(JsonElement message)
    {
        var state = GetString(message, "state");
        _flashing = state is not ("success" or "error" or "cancelled");
        FirmwareStatusText.Text = GetString(message, "message");
        if (message.TryGetProperty("progress", out var progress))
        {
            FirmwareProgress.Value = progress.GetDouble();
        }
        if (state == "waiting" && GetBool(message, "cancelable"))
        {
            FlashButton.Content = "取消等待";
            FlashButton.IsEnabled = true;
        }
        else if (_flashing)
        {
            FlashButton.Content = "正在安裝…";
            FlashButton.IsEnabled = false;
        }
        else
        {
            FlashButton.Content = "安裝／更新燈棒控制韌體";
            FlashButton.IsEnabled = true;
        }
        if (state == "error")
        {
            ShowStatus("韌體更新失敗", FirmwareStatusText.Text, InfoBarSeverity.Error);
        }
        else if (state == "success")
        {
            ShowStatus("韌體更新完成", FirmwareStatusText.Text, InfoBarSeverity.Success);
        }
        else if (state == "cancelled")
        {
            ShowStatus("已取消韌體更新", FirmwareStatusText.Text, InfoBarSeverity.Informational);
        }
    }

    private async void PresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string preset })
        {
            return;
        }
        switch (preset)
        {
            case "steady":
                SetSources("solid", "fixed", "steady");
                break;
            case "breathing":
                SetSources("solid", "fixed", "breathing");
                break;
            case "blink":
                SetSources("solid", "fixed", "blink");
                break;
            case "rainbow":
                SetSources("rainbow", "fixed", "steady");
                break;
            case "audio":
                SetSources("solid", "audio", "steady");
                break;
            case "screen":
                SetSources("screen", "fixed", "steady");
                break;
            case "concert":
                SetSources("screen", "audio", "steady");
                break;
        }
        await SendSettingsIfRunningAsync();
    }

    private void SetSources(string color, string brightness, string effect = "steady")
    {
        _colorSource = color;
        _brightnessSource = brightness;
        _effect = effect;
        SolidColorRadio.IsChecked = color == "solid";
        ScreenColorRadio.IsChecked = color == "screen";
        RainbowColorRadio.IsChecked = color == "rainbow";
        FixedBrightnessRadio.IsChecked = brightness == "fixed";
        AudioBrightnessRadio.IsChecked = brightness == "audio";
        UpdateVisualSettings();
    }

    private void SourceChanged(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || sender is not RadioButton { IsChecked: true } radio ||
            radio.Tag is not string value)
        {
            return;
        }
        if (radio.GroupName == "ColorSource")
        {
            _colorSource = value;
        }
        else
        {
            _brightnessSource = value;
        }
        _effect = "steady";
        ScheduleSettingsUpdate();
    }

    private async void TestColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string color })
        {
            return;
        }
        SolidColorBox.Text = color;
        await SendSettingsIfRunningAsync();
    }

    private async void ChooseColorButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new ColorPicker
        {
            Color = ParseColor(SolidColorBox.Text) ?? ColorHelper.FromArgb(255, 255, 32, 64),
            IsAlphaEnabled = false,
            IsAlphaSliderVisible = false,
            IsAlphaTextInputVisible = false,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "選擇固定顏色",
            Content = picker,
            PrimaryButtonText = "套用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            SetSolidColor(picker.Color);
            await SendSettingsIfRunningAsync();
        }
    }

    private async void EyedropperButton_Click(object sender, RoutedEventArgs e)
    {
        EyedropperButton.IsEnabled = false;
        ShowStatus("滴管取色", "請在螢幕上點選要採樣的顏色；按 Esc 可取消。",
            InfoBarSeverity.Informational);
        try
        {
            var picked = await PickerClient.PickColorAsync();
            if (picked is not DrawingColor drawingColor)
            {
                return;
            }
            SetSolidColor(ColorHelper.FromArgb(255, drawingColor.R, drawingColor.G, drawingColor.B));
            await SendSettingsIfRunningAsync();
        }
        catch (Exception error)
        {
            ShowStatus("滴管無法啟動", error.Message, InfoBarSeverity.Error);
        }
        finally
        {
            EyedropperButton.IsEnabled = true;
        }
    }

    private void RefreshWindowsButton_Click(object sender, RoutedEventArgs e)
    {
        PopulateCaptureTargets();
        ApplySelectedCaptureTarget();
        ScheduleSettingsUpdate();
    }

    private async void ClearRegionButton_Click(object sender, RoutedEventArgs e)
    {
        var monitorIndex = MonitorCombo.Items
            .OfType<ComboBoxItem>()
            .Select((item, index) => (item, index))
            .FirstOrDefault(value => value.item.Tag is CaptureTarget { Region: null })
            .index;
        MonitorCombo.SelectedIndex = Math.Max(0, monitorIndex);
        _region = null;
        RegionText.Text = "整個顯示器";
        await SendSettingsIfRunningAsync();
    }

    private void MonitorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }
        ApplySelectedCaptureTarget();
        SetSources("screen", _brightnessSource);
        ScheduleSettingsUpdate();
    }

    private void ApplySelectedCaptureTarget()
    {
        if (MonitorCombo.SelectedItem is not ComboBoxItem { Tag: CaptureTarget target })
        {
            return;
        }
        _region = target.Region;
        RegionText.Text = target.Region is DrawingRectangle region
            ? $"視窗範圍 · {region.Width}×{region.Height} @ {region.Left},{region.Top}"
            : $"使用完整顯示器 {target.Monitor}";
    }

    private void SolidColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ScheduleSettingsUpdate();
    }

    private void Slider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        ScheduleSettingsUpdate();
    }

    private void FixedBrightnessSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (FixedBrightnessText is not null)
        {
            FixedBrightnessText.Text = $"{Math.Round(e.NewValue * 100)}%";
        }
        ScheduleSettingsUpdate();
    }

    private void ScheduleSettingsUpdate()
    {
        if (!_uiReady)
        {
            return;
        }
        UpdateVisualSettings();
        _settingsTimer.Stop();
        _settingsTimer.Start();
    }

    private async void SettingsTimer_Tick(object? sender, object e)
    {
        _settingsTimer.Stop();
        await SendSettingsIfRunningAsync();
    }

    private async void WindowTrackingTimer_Tick(object? sender, object e)
    {
        if (MonitorCombo.SelectedItem is not ComboBoxItem
            {
                Tag: CaptureTarget { WindowHandle: not 0 } target
            } || !WindowEnumerator.TryGetBounds(target.WindowHandle, out var current) ||
            current == _region)
        {
            return;
        }
        _region = current;
        RegionText.Text = $"視窗範圍 · {current.Width}×{current.Height} @ {current.Left},{current.Top}";
        await SendSettingsIfRunningAsync();
    }

    private void UpdateVisualSettings()
    {
        var color = ParseColor(SolidColorBox.Text);
        if (color is UiColor validColor)
        {
            SolidColorSwatch.Background = new SolidColorBrush(validColor);
        }
        var usesSolidColor = _colorSource == "solid";
        var usesRainbow = _colorSource == "rainbow";
        var usesEffectSpeed = _effect is "breathing" or "blink";
        SetControlGroupEnabled(SolidColorControls, usesSolidColor);
        SetControlGroupEnabled(TestColorControls, usesSolidColor);
        SetControlGroupEnabled(
            RainbowSpeedControls, usesRainbow || usesEffectSpeed);
        SetControlGroupEnabled(
            FixedBrightnessControls, _brightnessSource == "fixed");
        UpdatePresetButtons();
        if (_running)
        {
            ShowStatus("場控執行中", $"模式：{CurrentModeLabel()}",
                InfoBarSeverity.Success);
        }
    }

    private static void SetControlGroupEnabled(Panel group, bool enabled)
    {
        group.IsHitTestVisible = enabled;
        group.Opacity = enabled ? 1.0 : 0.42;
        SetDescendantControlsEnabled(group, enabled);
    }

    private static void SetDescendantControlsEnabled(Panel panel, bool enabled)
    {
        foreach (var child in panel.Children)
        {
            if (child is Control control)
            {
                control.IsEnabled = enabled;
            }
            else if (child is Panel nestedPanel)
            {
                SetDescendantControlsEnabled(nestedPanel, enabled);
            }
        }
    }

    private string CurrentModeLabel()
    {
        if (_effect == "breathing")
        {
            return "呼吸燈";
        }
        if (_effect == "blink")
        {
            return "閃爍";
        }
        return (_colorSource, _brightnessSource) switch
        {
            ("solid", "fixed") => "恆亮",
            ("rainbow", "fixed") => "七彩循環",
            ("solid", "audio") => "音樂律動",
            ("screen", "fixed") => "畫面同步",
            ("screen", "audio") => "演唱會同步",
            ("rainbow", "audio") => "音樂律動・七彩",
            _ => "自訂組合",
        };
    }

    private string? CurrentPresetTag()
    {
        if (_effect is "breathing" or "blink")
        {
            return _effect;
        }
        return (_colorSource, _brightnessSource) switch
        {
            ("solid", "fixed") => "steady",
            ("rainbow", "fixed") => "rainbow",
            ("solid", "audio") => "audio",
            ("screen", "fixed") => "screen",
            ("screen", "audio") => "concert",
            _ => null,
        };
    }

    private void UpdatePresetButtons()
    {
        var activeTag = CurrentPresetTag();
        var inactiveStyle = (Style)RootGrid.Resources["PresetButtonStyle"];
        var activeStyle = (Style)RootGrid.Resources["ActivePresetButtonStyle"];
        Button[] buttons =
        [
            SteadyPresetButton,
            BreathingPresetButton,
            BlinkPresetButton,
            RainbowPresetButton,
            AudioPresetButton,
            ScreenPresetButton,
            ConcertPresetButton,
        ];
        foreach (var button in buttons)
        {
            button.Style = button.Tag as string == activeTag
                ? activeStyle : inactiveStyle;
        }
    }

    private object CollectSettings()
    {
        var color = ParseColor(SolidColorBox.Text) ?? ColorHelper.FromArgb(255, 255, 32, 64);
        var monitor = MonitorCombo.SelectedItem is ComboBoxItem { Tag: CaptureTarget target }
            ? target.Monitor : 1;
        object? region = _region is DrawingRectangle selected
            ? new { left = selected.Left, top = selected.Top, width = selected.Width, height = selected.Height }
            : null;
        return new
        {
            color_source = _colorSource,
            brightness_source = _brightnessSource,
            effect = _effect,
            solid_color = $"#{color.R:X2}{color.G:X2}{color.B:X2}",
            fixed_brightness = FixedBrightnessSlider.Value,
            rainbow_speed = RainbowSpeedSlider.Value,
            rainbow_reverse = false,
            monitor,
            region,
            fps = (int)Math.Round(FpsSlider.Value),
            sensitivity = SensitivitySlider.Value,
            minimum = Math.Min(MinimumSlider.Value, MaximumSlider.Value),
            maximum = Math.Max(MinimumSlider.Value, MaximumSlider.Value),
            audio_gate = AudioGateSlider.Value,
            brightness_gamma = GammaSlider.Value,
            smoothing = SmoothingSlider.Value,
            color_boost = ColorBoostSlider.Value,
            red_boost = RedBoostSlider.Value,
            green_boost = GreenBoostSlider.Value,
            blue_boost = BlueBoostSlider.Value,
        };
    }

    private async Task SendSettingsIfRunningAsync()
    {
        if (_running)
        {
            await SendSafeAsync(new { command = "update", settings = CollectSettings() });
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        ShowStatus("正在啟動場控", "正在連接 XIAO…", InfoBarSeverity.Informational);
        await SendSafeAsync(new { command = "restart", settings = CollectSettings() });
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _running = false;
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        ResetOutputPreview();
        ShowStatus("正在停止", "即將送出熄燈畫面…", InfoBarSeverity.Informational);
        await SendSafeAsync(new { command = "stop" });
    }

    private async void DeviceScanTimer_Tick(object? sender, object e)
    {
        await RequestDeviceScanAsync();
    }

    private async Task RequestDeviceScanAsync()
    {
        if (_running || _flashing || _scanInProgress)
        {
            return;
        }
        _scanInProgress = true;
        try
        {
            await SendSafeAsync(new { command = "scan" });
        }
        finally
        {
            _scanInProgress = false;
        }
    }

    private async void FlashButton_Click(object sender, RoutedEventArgs e)
    {
        if (_flashing)
        {
            FlashButton.IsEnabled = false;
            FirmwareStatusText.Text = "正在取消等待…";
            await SendSafeAsync(new { command = "cancel_flash" });
            return;
        }
        if (_running)
        {
            await ShowDialogAsync("請先停止控制", "韌體更新前必須釋放 XIAO 串口。");
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "安裝燈棒控制韌體",
            Content = "按下「開始等待」後，請快速雙擊 XIAO 的 Reset 按鈕。\n\n"
                    + "程式會等待 XIAO-BOOT 磁碟；偵測到正確的開發板後便自動開始安裝。"
                    + "只會更新 application，不會修改 Bootloader、SoftDevice 或 UICR。",
            PrimaryButtonText = "開始等待",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        FlashButton.IsEnabled = false;
        _flashing = true;
        FirmwareProgress.Value = 0;
        FirmwareStatusText.Text = "準備等待 XIAO-BOOT…";
        await SendSafeAsync(new { command = "flash" });
    }

    private async Task SendSafeAsync(object message)
    {
        try
        {
            await _backend.SendAsync(message);
        }
        catch (Exception error)
        {
            ShowStatus("無法傳送指令", error.Message, InfoBarSeverity.Error);
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
        }
    }

    private void SetSolidColor(UiColor color)
    {
        SolidColorBox.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        SolidColorSwatch.Background = new SolidColorBrush(color);
    }

    private static UiColor? ParseColor(string text)
    {
        var value = text.Trim().TrimStart('#');
        if (value.Length != 6 || !int.TryParse(value, NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var rgb))
        {
            return null;
        }
        return ColorHelper.FromArgb(255, (byte)(rgb >> 16),
            (byte)(rgb >> 8), (byte)rgb);
    }

    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        DeviceInfoBar.Title = title;
        DeviceInfoBar.Message = message;
        DeviceInfoBar.Severity = severity;
        DeviceInfoBar.IsOpen = true;
    }

    private async Task ShowDialogAsync(string title, string content)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = content,
            CloseButtonText = "確定",
        };
        await dialog.ShowAsync();
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed)
        {
            return;
        }

        args.Cancel = true;
        if (_closeDialogOpen)
        {
            return;
        }

        _closeDialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "關閉 Lightstick Studio？",
                Content = "燈棒進入場控模式後，實體按鍵會保持鎖定。\n\n"
                        + "若要恢復按鍵手動燈光，請重新安裝電池，或重新插拔電池絕緣片。",
                PrimaryButtonText = "仍要關閉",
                CloseButtonText = "返回",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                _closeConfirmed = true;
                Close();
            }
        }
        finally
        {
            _closeDialogOpen = false;
        }
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _windowTrackingTimer.Stop();
        _deviceScanTimer.Stop();
        SaveSettings();
        await _backend.DisposeAsync();
    }

    private string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Lightstick Studio", "settings-winui.json");

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var saved = JsonSerializer.Deserialize<PersistedSettings>(
                File.ReadAllText(SettingsPath));
            if (saved is null) return;
            _colorSource = saved.ColorSource;
            _brightnessSource = saved.BrightnessSource;
            _effect = saved.Effect;
            SolidColorBox.Text = saved.SolidColor;
            FixedBrightnessSlider.Value = saved.FixedBrightness;
            RainbowSpeedSlider.Value = saved.RainbowSpeed;
            FpsSlider.Value = saved.Fps;
            SensitivitySlider.Value = saved.Sensitivity;
            MinimumSlider.Value = saved.Minimum;
            MaximumSlider.Value = saved.Maximum;
            AudioGateSlider.Value = saved.AudioGate;
            GammaSlider.Value = saved.BrightnessGamma;
            SmoothingSlider.Value = saved.Smoothing;
            ColorBoostSlider.Value = saved.ColorBoost;
            RedBoostSlider.Value = saved.RedBoost;
            GreenBoostSlider.Value = saved.GreenBoost;
            BlueBoostSlider.Value = saved.BlueBoost;
            MonitorCombo.SelectedIndex = Math.Clamp(
                saved.Monitor - 1, 0, Math.Max(0, MonitorCombo.Items.Count - 1));
            if (saved.Region is not null)
            {
                _region = new DrawingRectangle(saved.Region.Left, saved.Region.Top,
                    saved.Region.Width, saved.Region.Height);
                RegionText.Text = $"自訂區域 · {_region.Value.Width}×{_region.Value.Height} @ {_region.Value.Left},{_region.Value.Top}";
            }
            SetSources(_colorSource, _brightnessSource, _effect);
        }
        catch
        {
            // A bad settings file should never prevent the controller from opening.
        }
    }

    private void SaveSettings()
    {
        try
        {
            var monitor = MonitorCombo.SelectedItem is ComboBoxItem { Tag: CaptureTarget target }
                ? target.Monitor : 1;
            var saved = new PersistedSettings
            {
                ColorSource = _colorSource,
                BrightnessSource = _brightnessSource,
                Effect = _effect,
                SolidColor = SolidColorBox.Text,
                FixedBrightness = FixedBrightnessSlider.Value,
                RainbowSpeed = RainbowSpeedSlider.Value,
                RainbowReverse = false,
                Monitor = Math.Max(1, monitor),
                Region = _region is DrawingRectangle region
                    ? new RegionSettings(region.Left, region.Top, region.Width, region.Height)
                    : null,
                Fps = FpsSlider.Value,
                Sensitivity = SensitivitySlider.Value,
                Minimum = MinimumSlider.Value,
                Maximum = MaximumSlider.Value,
                AudioGate = AudioGateSlider.Value,
                BrightnessGamma = GammaSlider.Value,
                Smoothing = SmoothingSlider.Value,
                ColorBoost = ColorBoostSlider.Value,
                RedBoost = RedBoostSlider.Value,
                GreenBoost = GreenBoostSlider.Value,
                BlueBoost = BlueBoostSlider.Value,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Settings persistence is best-effort.
        }
    }

    private static string GetString(JsonElement message, string name) =>
        message.TryGetProperty(name, out var value) ? value.ToString() : string.Empty;

    private static bool GetBool(JsonElement message, string name) =>
        message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static double GetDouble(JsonElement message, string name) =>
        message.TryGetProperty(name, out var value) && value.TryGetDouble(out var number)
            ? number : 0.0;

    private sealed class PersistedSettings
    {
        public string ColorSource { get; set; } = "screen";
        public string BrightnessSource { get; set; } = "audio";
        public string Effect { get; set; } = "steady";
        public string SolidColor { get; set; } = "#FF2040";
        public double FixedBrightness { get; set; } = 0.75;
        public double RainbowSpeed { get; set; } = 0.10;
        public bool RainbowReverse { get; set; }
        public int Monitor { get; set; } = 1;
        public RegionSettings? Region { get; set; }
        public double Fps { get; set; } = 25;
        public double Sensitivity { get; set; } = 1.35;
        public double Minimum { get; set; }
        public double Maximum { get; set; } = 1;
        public double AudioGate { get; set; } = 0.08;
        public double BrightnessGamma { get; set; } = 1.35;
        public double Smoothing { get; set; } = 0.24;
        public double ColorBoost { get; set; } = 0.38;
        public double RedBoost { get; set; } = 0.72;
        public double GreenBoost { get; set; } = 0.72;
        public double BlueBoost { get; set; }
    }

    private sealed record RegionSettings(int Left, int Top, int Width, int Height);
}
