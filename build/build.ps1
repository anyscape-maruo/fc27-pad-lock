param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$packageRoot = Split-Path -Parent $PSScriptRoot
$buildOutput = $PSScriptRoot
if ($OutputDirectory) {
    $buildOutput = [IO.Path]::GetFullPath($OutputDirectory)
    New-Item -ItemType Directory -Path $buildOutput -Force | Out-Null
}
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw 'Windows x64 with the .NET Framework 4.x compiler is required.' }
$assemblyInfo = Join-Path $packageRoot 'src/AssemblyInfo.cs'
$settingsSource = Join-Path $packageRoot 'src/AppSettings.cs'
$shortcutSource = Join-Path $packageRoot 'src/ToggleShortcut.cs'
$textSource = Join-Path $packageRoot 'src/TextCatalog.cs'
$diagnosticSource = Join-Path $packageRoot 'src/DiagnosticReport.cs'
$iconPath = Join-Path $packageRoot 'assets/PadLock.ico'
if (-not (Test-Path -LiteralPath $iconPath)) { throw 'App icon is missing; run scripts/make-icon.ps1.' }
$sourcePath = Join-Path $packageRoot 'src/FC27PadLock.cs'
$outputPath = Join-Path $buildOutput 'FC27PadLock.exe'
& $compilerPath /nologo /target:winexe /platform:x64 /optimize+ "/out:$outputPath" "/win32icon:$iconPath" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll $sourcePath $assemblyInfo $settingsSource $shortcutSource
if ($LASTEXITCODE -ne 0) { throw "Compilation failed: $LASTEXITCODE" }
Get-FileHash -Algorithm SHA256 -LiteralPath $outputPath
$controlSource = Join-Path $packageRoot 'src/FC27PadLockControl.cs'
$controlOutput = Join-Path $buildOutput 'FC27PadLockControl.exe'
$desktopUiSource = Join-Path $packageRoot 'src/DesktopUi.cs'
& $compilerPath /nologo /target:winexe /platform:x64 /optimize+ "/out:$controlOutput" "/win32icon:$iconPath" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /reference:Microsoft.CSharp.dll $controlSource $assemblyInfo $settingsSource $shortcutSource $textSource $diagnosticSource $desktopUiSource
if ($LASTEXITCODE -ne 0) { throw "Control compilation failed: $LASTEXITCODE" }
Get-FileHash -Algorithm SHA256 -LiteralPath $controlOutput
$overlaySource = Join-Path $packageRoot 'src/FC27PadLockOverlay.cs'
$overlayOutput = Join-Path $buildOutput 'FC27PadLockOverlay.exe'
& $compilerPath /nologo /target:winexe /platform:x64 /optimize+ "/out:$overlayOutput" "/win32icon:$iconPath" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll $overlaySource $assemblyInfo $textSource
if ($LASTEXITCODE -ne 0) { throw "Overlay compilation failed: $LASTEXITCODE" }
Get-FileHash -Algorithm SHA256 -LiteralPath $overlayOutput
$monitorSource = Join-Path $packageRoot 'src/FC27PadLockMonitor.cs'
$monitorOutput = Join-Path $buildOutput 'FC27PadLockMonitor.exe'
& $compilerPath /nologo /target:winexe /platform:x64 /optimize+ "/out:$monitorOutput" "/win32icon:$iconPath" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll $monitorSource $assemblyInfo $textSource $settingsSource
if ($LASTEXITCODE -ne 0) { throw "Monitor compilation failed: $LASTEXITCODE" }
Get-FileHash -Algorithm SHA256 -LiteralPath $monitorOutput
