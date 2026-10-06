param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$packageRoot = Split-Path -Parent $PSScriptRoot
$buildRoot = Join-Path $packageRoot 'build'
if ($OutputDirectory) {
    $buildRoot = [IO.Path]::GetFullPath($OutputDirectory)
}
$summary = @()
foreach ($appName in @('FC27PadLock','FC27PadLockControl','FC27PadLockOverlay','FC27PadLockMonitor','FC27PadLockUpdater')) {
    $exePath = Join-Path $buildRoot "$appName.exe"
    $reportPath = Join-Path $buildRoot "$appName-self-test.json"
    $process = Start-Process -FilePath $exePath -ArgumentList @('--self-test',('"{0}"' -f $reportPath)) -WindowStyle Hidden -PassThru
    try {
        if (-not $process.WaitForExit(15000)) { throw "Self-test timed out: $appName" }
        if ($process.ExitCode -ne 0) { throw "Self-test failed: $appName (exit $($process.ExitCode))" }
    } finally { $process.Dispose() }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if ($report.failures -ne 0 -or $report.hooksInstalled -ne $false -or $report.inputSent -ne $false) { throw "Unsafe or failed self-test report: $appName" }
    $summary += [pscustomobject]@{application=$appName;testCount=$report.testCount;failures=$report.failures;hooksInstalled=$false;inputSent=$false}
}
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$settingsTest = Join-Path $buildRoot 'SettingsPersistence.exe'
& $compilerPath /nologo /target:exe /platform:x64 /reference:System.Web.Extensions.dll "/out:$settingsTest" (Join-Path $packageRoot 'tests/SettingsPersistence.cs') (Join-Path $packageRoot 'src/AppSettings.cs') (Join-Path $packageRoot 'src/TextCatalog.cs')
if ($LASTEXITCODE -ne 0) { throw 'Settings persistence test compilation failed.' }
$heartbeatTest = Join-Path $buildRoot 'MonitorHeartbeat.exe'
& $compilerPath /nologo /target:exe /platform:x64 "/out:$heartbeatTest" (Join-Path $packageRoot 'tests/MonitorHeartbeat.cs')
if ($LASTEXITCODE -ne 0) { throw 'Monitor heartbeat test compilation failed.' }
foreach ($test in @(
    @{path=$settingsTest;args=@(('"{0}"' -f $buildRoot))},
    @{path=$heartbeatTest;args=@(('"{0}"' -f (Join-Path $buildRoot 'FC27PadLockMonitor.exe')),('"{0}"' -f $buildRoot))}
)) {
    $process=Start-Process -FilePath $test.path -ArgumentList $test.args -WindowStyle Hidden -PassThru
    try { if (-not $process.WaitForExit(15000) -or $process.ExitCode -ne 0) { throw "Integration check failed: $($test.path)" } }
    finally { $process.Dispose() }
}
$result = [pscustomobject]@{version=(Get-Content -LiteralPath (Join-Path $packageRoot 'VERSION') -Raw).Trim();verifiedUtc=[DateTime]::UtcNow.ToString('o');applications=$summary;settingsPersistenceVerified=$true;monitorHeartbeatVerified=$true;actualMatchVerified=$false}
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $buildRoot 'verification-summary.json') -Encoding UTF8
$result | ConvertTo-Json -Depth 5
