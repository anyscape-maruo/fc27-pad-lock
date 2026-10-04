param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$packageRoot = Split-Path -Parent $PSScriptRoot
$buildRoot = Join-Path $packageRoot 'build'
if ($OutputDirectory) {
    $buildRoot = [IO.Path]::GetFullPath($OutputDirectory)
}
$summary = @()
foreach ($appName in @('FC27PadLock','FC27PadLockControl','FC27PadLockOverlay','FC27PadLockMonitor')) {
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
$result = [pscustomobject]@{version=(Get-Content -LiteralPath (Join-Path $packageRoot 'VERSION') -Raw).Trim();verifiedUtc=[DateTime]::UtcNow.ToString('o');applications=$summary;actualMatchVerified=$false}
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $buildRoot 'verification-summary.json') -Encoding UTF8
$result | ConvertTo-Json -Depth 5
