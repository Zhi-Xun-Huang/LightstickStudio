param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$Version = "0.1.0"
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path -LiteralPath $vswhere)) { throw "Visual Studio C++ Build Tools are required to build the native ZIP launcher." }
$installation = & $vswhere -latest -products '*' `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw "Install the Visual Studio Desktop development with C++ workload." }
$msvc = Get-ChildItem -LiteralPath (Join-Path $installation "VC\Tools\MSVC") -Directory |
    Sort-Object Name -Descending | Select-Object -First 1
$tools = Join-Path $msvc.FullName "bin\Hostx64\x64"
$sdkRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10"
$sdk = Get-ChildItem -LiteralPath (Join-Path $sdkRoot "Lib") -Directory |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "um\x64\kernel32.lib") } |
    Sort-Object Name -Descending | Select-Object -First 1
if (-not $sdk) { throw "Windows 10/11 SDK is required." }
$include = Join-Path $sdkRoot "Include\$($sdk.Name)"
$rc = Join-Path $sdkRoot "bin\$($sdk.Name)\x64\rc.exe"
$resource = Join-Path $output "zip-launcher.res"
$object = Join-Path $output "zip-launcher.obj"
$exe = Join-Path $output "LightstickStudio.WinUI.exe"
$numbers = $Version.Split('-')[0].Split('.')
if ($numbers.Count -ne 3 -or @($numbers | Where-Object { $_ -notmatch '^\d+$' }).Count -ne 0) {
    throw "Version must start with major.minor.patch."
}

Push-Location $projectRoot
try {
    & $rc /nologo "/fo$resource" "/dSTUDIO_MAJOR=$($numbers[0])" `
        "/dSTUDIO_MINOR=$($numbers[1])" "/dSTUDIO_PATCH=$($numbers[2])" "packaging\zip-launcher.rc"
    if ($LASTEXITCODE -ne 0) { throw "ZIP launcher resource compilation failed." }
    & (Join-Path $tools "cl.exe") /nologo /c /O1 /GS- /Zl /utf-8 /W4 /WX `
        "/I$($msvc.FullName)\include" "/I$include\um" "/I$include\shared" "/I$include\ucrt" `
        "/Fo$object" "packaging\zip-launcher.c"
    if ($LASTEXITCODE -ne 0) { throw "ZIP launcher compilation failed." }
    & (Join-Path $tools "link.exe") /nologo /NODEFAULTLIB /ENTRY:StudioEntry `
        /SUBSYSTEM:WINDOWS /MACHINE:X64 /OPT:REF /OPT:ICF "/OUT:$exe" `
        $object $resource "/LIBPATH:$($sdk.FullName)\um\x64" kernel32.lib user32.lib
    if ($LASTEXITCODE -ne 0) { throw "ZIP launcher linking failed." }
} finally {
    Pop-Location
}
Write-Host "Native ZIP launcher: $exe"
