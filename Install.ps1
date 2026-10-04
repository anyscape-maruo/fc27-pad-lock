[CmdletBinding(SupportsShouldProcess=$true)]
param(
    [switch]$NoStartup,
    [string]$DesktopDirectory = [Environment]::GetFolderPath('Desktop'),
    [string]$StartupDirectory = [Environment]::GetFolderPath('Startup')
)
$ErrorActionPreference = 'Stop'
$controlPath = Join-Path $PSScriptRoot 'build\FC27PadLockControl.exe'
if (-not (Test-Path -LiteralPath $controlPath)) { throw 'Control app is missing.' }
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'build\FC27PadLock.exe'))) { throw 'Guard app is missing.' }
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'build\FC27PadLockOverlay.exe'))) { throw 'Overlay app is missing.' }
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'build\FC27PadLockMonitor.exe'))) { throw 'Monitor app is missing.' }
$shell = New-Object -ComObject WScript.Shell
function Set-PadLockShortcut {
    param([string]$Path,[string]$Arguments,[string]$Description)
    if (Test-Path -LiteralPath $Path) {
        $old = $shell.CreateShortcut($Path)
        if ($old.TargetPath -ne $controlPath) { throw "Existing shortcut is not owned by this app: $Path" }
    }
    $shortcut = $shell.CreateShortcut($Path)
    $shortcut.TargetPath = $controlPath
    $shortcut.Arguments = $Arguments
    $shortcut.WorkingDirectory = Split-Path -Parent $controlPath
    $shortcut.WindowStyle = 7
    $shortcut.Description = $Description
    $shortcut.IconLocation = "$controlPath,0"
    $shortcut.Save()
}
$desktop = $DesktopDirectory
$startup = $StartupDirectory
if (-not (Test-Path -LiteralPath $desktop -PathType Container)) { throw 'Desktop directory is missing.' }
if (-not (Test-Path -LiteralPath $startup -PathType Container)) { throw 'Startup directory is missing.' }
$launchShortcut = Join-Path $desktop 'FC27 パッド保護で起動.lnk'
$controlShortcut = Join-Path $desktop 'FC27 パッド保護パネル.lnk'
$startupShortcut = Join-Path $startup 'FC27 Pad Lock.lnk'
foreach ($path in @($launchShortcut,$controlShortcut,$startupShortcut)) {
    if (Test-Path -LiteralPath $path) { if ($shell.CreateShortcut($path).TargetPath -ne $controlPath) { throw "Shortcut conflict detected before installation: $path" } }
}
if (-not $PSCmdlet.ShouldProcess('Pad Lock desktop and login shortcuts','Register shortcuts')) { return }
if ($NoStartup) {
    if (Test-Path -LiteralPath $startupShortcut) { Remove-Item -LiteralPath $startupShortcut }
    $startupShortcut = $null
}
# Write the intended owned paths before creating links, so a partial installation is recoverable.
@{installedUtc=[DateTime]::UtcNow.ToString('o');control=$controlPath;launchShortcut=$launchShortcut;controlShortcut=$controlShortcut;startupShortcut=$startupShortcut;gameFilesChanged=$false;status='installing'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'installation.json') -Encoding UTF8
Set-PadLockShortcut $launchShortcut '--launch' '入力ガードの起動を確認してSteam版FC27を起動します。最初の画面からコントローラーで操作してください。'
Set-PadLockShortcut $controlShortcut '--panel' 'FC27のパッド保護を有効・解除・停止できます。'
if ($startupShortcut) { Set-PadLockShortcut $startupShortcut '--background' 'Windowsログイン時にFC27の入力ガードを起動します。' }
@{installedUtc=[DateTime]::UtcNow.ToString('o');control=$controlPath;launchShortcut=$launchShortcut;controlShortcut=$controlShortcut;startupShortcut=$startupShortcut;gameFilesChanged=$false;status='installed'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'installation.json') -Encoding UTF8
