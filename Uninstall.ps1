[CmdletBinding(SupportsShouldProcess=$true)]
param()
$ErrorActionPreference = 'Stop'
$controlPath = Join-Path $PSScriptRoot 'build\FC27PadLockControl.exe'
$taskSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value

function Get-RootException([Exception]$Exception) {
    while ($Exception.InnerException) { $Exception = $Exception.InnerException }
    return $Exception
}
function Send-PadLockEvent([string]$Name) {
    $eventHandle = $null
    try { $eventHandle = [Threading.EventWaitHandle]::OpenExisting($Name); [void]$eventHandle.Set() }
    catch { if ((Get-RootException $_.Exception) -isnot [Threading.WaitHandleCannotBeOpenedException]) { throw } }
    finally { if ($eventHandle) { $eventHandle.Dispose() } }
}
function Test-PadLockRunning([string]$Name) {
    $mutexHandle = $null; $acquired = $false
    try {
        $mutexHandle = [Threading.Mutex]::OpenExisting($Name)
        try { $acquired = $mutexHandle.WaitOne(0) }
        catch { if ((Get-RootException $_.Exception) -is [Threading.AbandonedMutexException]) { $acquired = $true } else { throw } }
        return (-not $acquired)
    }
    catch { if ((Get-RootException $_.Exception) -is [Threading.WaitHandleCannotBeOpenedException]) { return $false }; throw }
    finally { if ($mutexHandle) { if ($acquired) { $mutexHandle.ReleaseMutex() }; $mutexHandle.Dispose() } }
}

if (-not $PSCmdlet.ShouldProcess('Current user/session Pad Lock and owned shortcuts','Stop and unregister')) { return }
Send-PadLockEvent "Local\FC27PadLockUI-$taskSid-Quit"
Send-PadLockEvent "Local\FC27PadLock-$taskSid-Stop"
Send-PadLockEvent "Local\FC27PadLockOverlay-$taskSid-Quit"
Send-PadLockEvent "Local\FC27PadLockMonitor-$taskSid-Quit"
$stopDeadline = [DateTime]::UtcNow.AddSeconds(5)
do {
    $running = (Test-PadLockRunning "Local\FC27PadLockUI-$taskSid-Instance") -or (Test-PadLockRunning "Local\FC27PadLock-$taskSid-Instance") -or (Test-PadLockRunning "Local\FC27PadLockOverlay-$taskSid-Instance") -or (Test-PadLockRunning "Local\FC27PadLockMonitor-$taskSid-Instance")
    if (-not $running) { break }
    Start-Sleep -Milliseconds 100
} while ([DateTime]::UtcNow -lt $stopDeadline)
if ($running) { throw 'Stop was requested, but Pad Lock is still running in this session. Use Ctrl+Alt+F12 and close the app before retrying.' }
$receiptPath = Join-Path $PSScriptRoot 'installation.json'
$knownStartup = Join-Path ([Environment]::GetFolderPath('Startup')) 'FC27 Pad Lock.lnk'
if (Test-Path -LiteralPath $knownStartup) {
    $startupShell=New-Object -ComObject WScript.Shell
    if ($startupShell.CreateShortcut($knownStartup).TargetPath -eq $controlPath) { Remove-Item -LiteralPath $knownStartup }
}
if (Test-Path -LiteralPath $receiptPath) {
    $installed = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    $shell = New-Object -ComObject WScript.Shell
    $allowedTargets=@($controlPath,(Join-Path $PSScriptRoot 'build\FC27PadLockOverlay.exe'),(Join-Path $PSScriptRoot 'build\FC27PadLockMonitor.exe'))
    foreach ($path in @($installed.launchShortcut,$installed.controlShortcut,$installed.startupShortcut,$installed.overlayShortcut,$installed.overlayStartupShortcut,$installed.monitorShortcut,$installed.monitorStartupShortcut)) {
        if ($path -and (Test-Path -LiteralPath $path)) {
            if ($allowedTargets -contains $shell.CreateShortcut($path).TargetPath) { Remove-Item -LiteralPath $path }
        }
    }
    $installed | Add-Member -NotePropertyName status -NotePropertyValue 'uninstalled' -Force
    $installed | ConvertTo-Json | Set-Content -LiteralPath $receiptPath -Encoding UTF8
}
$setupReceipt = Join-Path $PSScriptRoot 'setup-installation.json'
if (Test-Path -LiteralPath $setupReceipt) {
    $setup = Get-Content -LiteralPath $setupReceipt -Raw | ConvertFrom-Json
    $expectedAppRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'FC27PadLock\App'))
    $resolvedRoot = [IO.Path]::GetFullPath($PSScriptRoot)
    if ($setup.product -ne 'FC27PadLock' -or $setup.version -notmatch '^\d+\.\d+\.\d+(-[a-zA-Z0-9.]+)?$' -or
        $resolvedRoot -ne [IO.Path]::GetFullPath((Join-Path $expectedAppRoot $setup.version)) -or
        [IO.Path]::GetFullPath($setup.installRoot) -ne $resolvedRoot -or [IO.Path]::GetFullPath($setup.appRoot) -ne $expectedAppRoot -or
        [IO.Path]::GetFullPath($setup.controlPath) -ne [IO.Path]::GetFullPath($controlPath)) { throw 'Installation receipt does not match this installation.' }
    for ($checkPath=$resolvedRoot; $checkPath; $checkPath=Split-Path -Parent $checkPath) {
        if ((Get-Item -LiteralPath $checkPath).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing cleanup through a reparse point.' }
    }
    $shell = New-Object -ComObject WScript.Shell
    foreach ($path in @($setup.desktopShortcut,$setup.startupShortcut,$setup.startMenuShortcut)) {
        if ($path -and (Test-Path -LiteralPath $path) -and $shell.CreateShortcut($path).TargetPath -eq $controlPath) { Remove-Item -LiteralPath $path }
    }
    $registryPath='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\FC27PadLock'
    if (Test-Path -LiteralPath $registryPath) {
        $registration=Get-ItemProperty -LiteralPath $registryPath
        if ($registration.InstallLocation -eq $resolvedRoot) { Remove-Item -LiteralPath $registryPath }
    }
    $packageNames=@('Start.cmd','Install.ps1','Uninstall.ps1','README.md','LICENSE','VERSION','CHANGELOG.md','SECURITY.md',
        'build/FC27PadLock.exe','build/FC27PadLockControl.exe','build/FC27PadLockOverlay.exe','build/FC27PadLockMonitor.exe',
        'assets/PadLock.ico','assets/PadLock.png','config/app-settings.example.json','config/app-settings.schema.json',
        'docs/MANUAL.ja.md','docs/MANUAL.en.md','docs/TROUBLESHOOTING.ja.md','docs/COMPATIBILITY.ja.md','docs/PRIVACY.ja.md',
        'docs/OVERLAY.ja.md','docs/overlay-preview.png','docs/SETTINGS.ja.md','docs/SETTINGS.en.md','docs/MONITOR.ja.md','docs/REPORTING.ja.md')
    foreach ($relative in $setup.files) {
        if ($packageNames -notcontains $relative) { throw 'Installation receipt contains an unexpected file.' }
        $target = [IO.Path]::GetFullPath((Join-Path $resolvedRoot $relative))
        if (-not $target.StartsWith($resolvedRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup path escaped installation root.' }
        if (Test-Path -LiteralPath $target) {
            $item=Get-Item -LiteralPath $target
            if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Unexpected cleanup target.' }
            for($parentPath=$item.Directory.FullName; $parentPath -and $parentPath -ne $resolvedRoot; $parentPath=Split-Path -Parent $parentPath) {
                if ((Get-Item -LiteralPath $parentPath).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing cleanup through a reparse point.' }
            }
            if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $setup.sha256.$relative) { Remove-Item -LiteralPath $target }
        }
    }
    Remove-Item -LiteralPath $setupReceipt
    foreach ($relative in @('build','assets','docs','config','')) {
        $directory=if($relative){Join-Path $resolvedRoot $relative}else{$resolvedRoot}
        if ((Test-Path -LiteralPath $directory -PathType Container) -and @(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0) { Remove-Item -LiteralPath $directory }
    }
}
Write-Output 'Pad Lock stopped; owned installation removed. User preferences and game settings were preserved.'
