param([string]$OutputDirectory,[string]$BuildDirectory)
$ErrorActionPreference = 'Stop'
$packageRoot = Split-Path -Parent $PSScriptRoot
if (-not $BuildDirectory) { $BuildDirectory = Join-Path $packageRoot 'build' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $packageRoot 'artifacts' }
$version = (Get-Content -LiteralPath (Join-Path $packageRoot 'VERSION') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+(-[a-zA-Z0-9.]+)?$') { throw 'Invalid package version.' }
$verification = Get-Content -LiteralPath (Join-Path $BuildDirectory 'verification-summary.json') -Raw | ConvertFrom-Json
if ($verification.version -ne $version -or $verification.applications.Count -ne 5 -or @($verification.applications | Where-Object { $_.failures -ne 0 -or $_.hooksInstalled -ne $false -or $_.inputSent -ne $false }).Count -gt 0) { throw 'Run scripts/verify.ps1 successfully before packaging.' }
$staging = Join-Path $OutputDirectory "FC27PadLock-v$version-win-x64"
if (Test-Path -LiteralPath $staging) { throw 'Staging directory already exists; choose a new output directory.' }
New-Item -ItemType Directory -Path $staging -Force | Out-Null
$files = @('Manual.html','Start.cmd','Install.ps1','Uninstall.ps1','README.md','LICENSE','VERSION','CHANGELOG.md','SECURITY.md','build/FC27PadLock.exe','build/FC27PadLockControl.exe','build/FC27PadLockOverlay.exe','build/FC27PadLockMonitor.exe','build/FC27PadLockUpdater.exe','assets/PadLock.ico','assets/PadLock.png','docs/MANUAL.ja.html','docs/MANUAL.en.html','docs/TROUBLESHOOTING.ja.html','docs/COMPATIBILITY.ja.html','docs/PRIVACY.ja.html','docs/OVERLAY.ja.html','docs/overlay-preview.png','docs/SETTINGS.ja.html','docs/SETTINGS.en.html','docs/MONITOR.ja.html','docs/REPORTING.ja.html','config/app-settings.example.json','config/app-settings.schema.json')
foreach ($relative in $files) {
    $source = Join-Path $packageRoot $relative
    if ($relative.StartsWith('build/')) { $source = Join-Path $BuildDirectory $relative.Substring(6) }
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Package file missing: $relative" }
    $destination = Join-Path $staging $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination
}
$zipName = "FC27PadLock-v$version-win-x64.zip"
$zipPath = Join-Path $OutputDirectory $zipName
if (Test-Path -LiteralPath $zipPath) { throw 'ZIP already exists.' }
# Windows PowerShell 5.1 Compress-Archive writes backslash entry names.
# Use explicit ZIP paths so the payload is canonical on every supported host.
Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zipPath,[IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($relative in $files) {
        $entryName = "$(Split-Path -Leaf $staging)/$relative"
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,(Join-Path $staging $relative),$entryName,[IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }
$hashLines = @("$((Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant())  $zipName")
foreach ($relative in @('build/FC27PadLock.exe','build/FC27PadLockControl.exe','build/FC27PadLockOverlay.exe','build/FC27PadLockMonitor.exe','build/FC27PadLockUpdater.exe')) {
    $hashLines += "$((Get-FileHash -LiteralPath (Join-Path $staging $relative) -Algorithm SHA256).Hash.ToLowerInvariant())  $relative"
}
$hashLines | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ASCII
Get-Item -LiteralPath $zipPath | Select-Object Name,Length
