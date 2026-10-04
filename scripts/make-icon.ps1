$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$packageRoot = Split-Path -Parent $PSScriptRoot
$imagePath = Join-Path $packageRoot 'assets\PadLock.png'
$iconPath = Join-Path $packageRoot 'assets\PadLock.ico'
$sourceImage = [Drawing.Image]::FromFile($imagePath)
$layers = @()
try {
    foreach ($size in @(16,24,32,48,64,128,256)) {
        $bitmap = New-Object Drawing.Bitmap($size,$size)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $buffer = New-Object IO.MemoryStream
        try {
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.DrawImage($sourceImage,0,0,$size,$size)
            $bitmap.Save($buffer,[Drawing.Imaging.ImageFormat]::Png)
            $layers += [pscustomobject]@{size=$size;data=$buffer.ToArray()}
        } finally { $buffer.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
} finally { $sourceImage.Dispose() }
$stream = [IO.File]::Create($iconPath)
$writer = New-Object IO.BinaryWriter($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$layers.Count)
    $offset = 6 + 16 * $layers.Count
    foreach ($layer in $layers) {
        $dimension = if ($layer.size -eq 256) { 0 } else { $layer.size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$layer.data.Length); $writer.Write([uint32]$offset)
        $offset += $layer.data.Length
    }
    foreach ($layer in $layers) { $writer.Write([byte[]]$layer.data) }
} finally { $writer.Dispose(); $stream.Dispose() }
Get-Item -LiteralPath $iconPath | Select-Object Name,Length
