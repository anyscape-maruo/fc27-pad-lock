param([Parameter(Mandatory=$true)][string]$PayloadZip,[string]$OutputPath)
$ErrorActionPreference='Stop'
$packageRoot=Split-Path -Parent $PSScriptRoot
$payloadPath=(Resolve-Path -LiteralPath $PayloadZip).Path
if (-not $OutputPath) { $OutputPath=Join-Path $packageRoot 'build\FC27PadLockSetup.exe' }
$OutputPath=[IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force | Out-Null
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4 compiler missing.' }
$compilerArgs=@('/nologo','/target:winexe','/platform:x64',"/out:$OutputPath",'/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll','/reference:System.Web.Extensions.dll','/reference:System.IO.Compression.dll','/reference:System.IO.Compression.FileSystem.dll','/reference:Microsoft.CSharp.dll',"/resource:$payloadPath,FC27PadLock.Payload.zip",(Join-Path $packageRoot 'src\Setup.cs'),(Join-Path $packageRoot 'src\AssemblyInfo.cs'))
$icon=Join-Path $packageRoot 'assets\PadLock.ico'
if (Test-Path -LiteralPath $icon) {$compilerArgs+= "/win32icon:$icon"}
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Setup compilation failed.' }
Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256
