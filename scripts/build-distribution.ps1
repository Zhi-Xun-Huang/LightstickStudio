param(
    [string]$Version = "0.1.0"
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$artifactsRoot = Join-Path $projectRoot "artifacts"
$sourceName = "LightstickStudio-$Version-source"
$releaseName = "LightstickStudio-v$Version-win-x64"
$sourceDirectory = Join-Path $artifactsRoot $sourceName
$releaseDirectory = Join-Path $artifactsRoot $releaseName
$appDirectory = Join-Path $releaseDirectory "app"
$sourceZip = Join-Path $artifactsRoot "$sourceName.zip"
$releaseZip = Join-Path $artifactsRoot "$releaseName.zip"
$nativeBuildDirectory = Join-Path $artifactsRoot ".zip-launcher-build-$Version"
$checksumsFile = Join-Path $artifactsRoot "SHA256SUMS.txt"

function Reset-ArtifactDirectory([string]$Path) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $safeRoot = [System.IO.Path]::GetFullPath($artifactsRoot) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($safeRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset a directory outside artifacts: $fullPath"
    }
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $fullPath | Out-Null
}

function Copy-ProjectSources([string]$ProjectName) {
    $from = Join-Path $projectRoot $ProjectName
    $to = Join-Path $sourceDirectory $ProjectName
    New-Item -ItemType Directory -Path $to | Out-Null
    Get-ChildItem -LiteralPath $from -File | Where-Object {
        $_.Extension -in ".cs", ".xaml", ".csproj", ".manifest"
    } | Copy-Item -Destination $to
}

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
Reset-ArtifactDirectory $sourceDirectory
Reset-ArtifactDirectory $releaseDirectory
Reset-ArtifactDirectory $nativeBuildDirectory
New-Item -ItemType Directory -Path $appDirectory | Out-Null
foreach ($archive in @($sourceZip, $releaseZip, $checksumsFile)) {
    $fullArchive = [System.IO.Path]::GetFullPath($archive)
    if (-not $fullArchive.StartsWith(
            ([System.IO.Path]::GetFullPath($artifactsRoot) + [System.IO.Path]::DirectorySeparatorChar),
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove an archive outside artifacts: $fullArchive"
    }
    if (Test-Path -LiteralPath $fullArchive) {
        Remove-Item -LiteralPath $fullArchive -Force
    }
}

# Create a repository-ready source tree from an allowlist. Research dumps,
# vendor SDKs, virtual environments and old Python tools are never copied.
Copy-ProjectSources "LightstickStudio.WinUI"
Copy-ProjectSources "LightstickStudio.Picker"

foreach ($file in @(
    ".gitignore",
    "LICENSE",
    "README.md",
    "THIRD_PARTY_NOTICES.md",
    "LightstickStudio.slnx"
)) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $file) -Destination $sourceDirectory
}

$sourceFirmware = Join-Path $sourceDirectory "src"
New-Item -ItemType Directory -Path $sourceFirmware | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot "src\xiao_main.cpp") -Destination $sourceFirmware
$cleanPlatformio = Join-Path $projectRoot "packaging\platformio.clean.ini"
if (-not (Test-Path -LiteralPath $cleanPlatformio)) {
    $cleanPlatformio = Join-Path $projectRoot "platformio.ini"
}
Copy-Item -LiteralPath $cleanPlatformio `
    -Destination (Join-Path $sourceDirectory "platformio.ini")

$iconDestination = Join-Path $sourceDirectory "icon"
New-Item -ItemType Directory -Path $iconDestination | Out-Null
foreach ($file in @("logo.png", "app-icon.png", "app.ico", "README.md")) {
    Copy-Item -LiteralPath (Join-Path $projectRoot "icon\$file") -Destination $iconDestination
}

$firmwareDestination = Join-Path $sourceDirectory "resources\firmware\xiao_nrf52840_plus"
New-Item -ItemType Directory -Path $firmwareDestination -Force | Out-Null
foreach ($file in @("firmware.uf2", "manifest.json")) {
    Copy-Item -LiteralPath (
        Join-Path $projectRoot "resources\firmware\xiao_nrf52840_plus\$file"
    ) -Destination $firmwareDestination
}

$scriptsDestination = Join-Path $sourceDirectory "scripts"
New-Item -ItemType Directory -Path $scriptsDestination | Out-Null
Copy-Item -LiteralPath $PSCommandPath -Destination $scriptsDestination
Copy-Item -LiteralPath (Join-Path $projectRoot "scripts\convert-icon.ps1") -Destination $scriptsDestination
Copy-Item -LiteralPath (Join-Path $projectRoot "scripts\build-zip-launcher.ps1") -Destination $scriptsDestination
$packagingDestination = Join-Path $sourceDirectory "packaging"
New-Item -ItemType Directory -Path $packagingDestination | Out-Null
foreach ($file in @("zip-launcher.c", "zip-launcher.rc")) {
    Copy-Item -LiteralPath (Join-Path $projectRoot "packaging\$file") -Destination $packagingDestination
}

$workflowSource = Join-Path $projectRoot ".github\workflows\release.yml"
if (Test-Path -LiteralPath $workflowSource) {
    $workflowDestination = Join-Path $sourceDirectory ".github\workflows"
    New-Item -ItemType Directory -Path $workflowDestination -Force | Out-Null
    Copy-Item -LiteralPath $workflowSource -Destination $workflowDestination
}

# Publishing WinUI also carries the self-contained Picker project and its
# Windows Forms runtime into the same portable directory.
& dotnet restore (Join-Path $projectRoot "LightstickStudio.WinUI\LightstickStudio.WinUI.csproj") `
    -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }

& dotnet publish (Join-Path $projectRoot "LightstickStudio.WinUI\LightstickStudio.WinUI.csproj") `
    -c Release -r win-x64 --self-contained true -p:Platform=x64 `
    -p:Version=$Version --no-restore -o $appDirectory
if ($LASTEXITCODE -ne 0) { throw "WinUI publish failed" }

# Unpackaged WinUI publish can omit the application's compiled XAML resources
# when a custom output directory is used. Without them Microsoft.UI.Xaml exits
# before the first window is shown, so copy them from the verified build output.
$winuiBuildDirectory = Join-Path $projectRoot `
    "LightstickStudio.WinUI\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64"
foreach ($file in @("App.xbf", "MainWindow.xbf", "LightstickStudio.WinUI.pri")) {
    $compiledResource = Join-Path $winuiBuildDirectory $file
    if (-not (Test-Path -LiteralPath $compiledResource)) {
        throw "WinUI compiled resource is missing: $compiledResource"
    }
    Copy-Item -LiteralPath $compiledResource -Destination $appDirectory
}

foreach ($pickerFile in @(
    "LightstickStudio.Picker.exe",
    "LightstickStudio.Picker.dll",
    "LightstickStudio.Picker.deps.json",
    "LightstickStudio.Picker.runtimeconfig.json"
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $appDirectory $pickerFile))) {
        throw "Published Picker component is missing: $pickerFile"
    }
}

$docsDirectory = Join-Path $appDirectory "docs"
New-Item -ItemType Directory -Path $docsDirectory | Out-Null
foreach ($file in @("LICENSE", "README.md", "THIRD_PARTY_NOTICES.md")) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $file) -Destination $docsDirectory
}
Get-ChildItem -LiteralPath $releaseDirectory -Filter "*.pdb" -File -Recurse |
    Remove-Item -Force

& (Join-Path $projectRoot "scripts\build-zip-launcher.ps1") `
    -OutputDirectory $nativeBuildDirectory -Version $Version
Copy-Item -LiteralPath (Join-Path $nativeBuildDirectory "LightstickStudio.WinUI.exe") -Destination $releaseDirectory

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $sourceDirectory, $sourceZip,
    [System.IO.Compression.CompressionLevel]::Optimal, $true)
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $releaseDirectory, $releaseZip,
    [System.IO.Compression.CompressionLevel]::Optimal, $true)
$checksumLines = @($sourceZip, $releaseZip) | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([System.IO.Path]::GetFileName($_))"
}
[System.IO.File]::WriteAllLines($checksumsFile, $checksumLines)

$sourceSize = [Math]::Round((Get-Item -LiteralPath $sourceZip).Length / 1MB, 2)
$releaseSize = [Math]::Round((Get-Item -LiteralPath $releaseZip).Length / 1MB, 2)
Write-Host "Source:  $sourceZip ($sourceSize MiB)"
Write-Host "Release: $releaseZip ($releaseSize MiB)"
Write-Host "SHA-256: $checksumsFile"
