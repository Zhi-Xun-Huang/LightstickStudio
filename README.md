# Lightstick Studio

Lightstick Studio 是一套為鳴潮演唱會燈棒打造的非官方 Windows 控制工具。它透過 Seeed Studio XIAO nRF52840 Plus 發射器，提供固定顏色、呼吸燈、閃爍、七彩循環、音樂律動、畫面同步與演唱會同步。

目前已在標示 `HE-750G24`、分區標籤 `05` 的燈棒與 XIAO nRF52840 Plus 上實機驗證。其他場次、分區或外觀相近的燈棒不保證使用相同協定。

## 下載與使用

一般使用者可從 GitHub Releases 下載 `LightstickStudio-v0.1.0-win-x64.zip` 免安裝版：

1. 解壓縮全部檔案，不要只取出 EXE。
2. 以 USB 連接 XIAO nRF52840 Plus。
3. 執行 `LightstickStudio.WinUI.exe`，閱讀並接受使用前聲明。
4. 若 XIAO 尚未安裝控制韌體，前往「發射器韌體」，按畫面提示快速雙擊 Reset。
5. 選擇燈效後按「開始控制」。

最外層只有 `LightstickStudio.WinUI.exe`（小型原生啟動器）與 `app` 資料夾。真正的 WinUI 程式、DLL、Logo、滴管及韌體全部保留在 `app`；說明與授權文件放在 `app/docs`。請勿只移動外層 EXE 或刪除 `app`。程式直接使用解壓後的檔案，不會將執行檔展開到暫存目錄。

Release 已包含 .NET、Windows App SDK、C# 音訊／串口元件與 UF2，不需要安裝 Python、PlatformIO 或 Visual Studio。支援 Windows 10 1809 以上的 x64 電腦。

燈棒進入場控模式後，實體按鍵會維持鎖定。若要恢復手動燈光，必須重新安裝電池，或重新插拔電池絕緣片。

## 從原始碼建置桌面程式

需要 .NET 8 SDK，以及能建置 WinUI 3 / Windows App SDK 專案的 Visual Studio 工作負載。

```powershell
dotnet restore LightstickStudio.WinUI\LightstickStudio.WinUI.csproj -p:Platform=x64
dotnet build LightstickStudio.WinUI\LightstickStudio.WinUI.csproj -c Debug -p:Platform=x64
```

也可以使用 Visual Studio 開啟 `LightstickStudio.slnx`，將 `LightstickStudio.WinUI` 設為啟始專案並選擇 `x64`。

桌面控制核心已完全使用 C# 實作，包括：

- XIAO USB／COM 自動識別
- 串口畫面傳輸
- Win32 畫面擷取與主色分析
- WASAPI Loopback 音訊與節拍分析
- UF2 磁碟偵測及韌體安裝

## 建置 XIAO 韌體

韌體原始碼位於 `src/xiao_main.cpp`，使用 PlatformIO 與 Seeed Studio Platform 建置：

```powershell
pio run -e xiao_nrf52840_plus
```

預編譯的 application-only UF2 位於 `resources/firmware/xiao_nrf52840_plus/firmware.uf2`。它不會修改 Bootloader、SoftDevice 或 UICR。

## 建立發行套件

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-distribution.ps1
```

完成後會在 `artifacts` 產生乾淨原始碼資料夾、Source ZIP、Windows x64 免安裝資料夾、Release ZIP 與 `SHA256SUMS.txt` 校驗碼。ZIP 外層啟動器的原始碼在 `packaging/zip-launcher.c`。完整封裝另外需要 Visual Studio「使用 C++ 的桌面開發」工作負載及 Windows SDK；一般使用者不需要安裝這些工具。

## 使用聲明

本專案為非官方社群研究成果，與遊戲發行商、演唱會主辦單位及燈棒製造商均無隸屬或合作關係。相關名稱、商標及作品之權利歸各自權利人所有。

請勿在演唱會、公共活動或他人設備附近擅自發射控制訊號。使用者應自行確認並遵守所在地法規，並自行承擔操作、改裝及無線發射所造成的風險。

## License 與致謝

Copyright © 2026 Zhi-Xun-Huang。本專案以 [MIT License](LICENSE) 授權；第三方元件適用各自的授權條款，詳見 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

特別感謝 OpenAI Codex，在通訊協定逆向分析、發射器韌體、Lightstick Studio 介面設計、C# 後端移植與除錯過程中提供協作支援。
