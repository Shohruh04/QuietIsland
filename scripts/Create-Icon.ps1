$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetRoot = Join-Path (Split-Path $PSScriptRoot -Parent) 'Assets'
New-Item -ItemType Directory -Path $assetRoot -Force | Out-Null
$frames = [System.Collections.Generic.List[byte[]]]::new()
$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.ScaleTransform($size / 32.0, $size / 32.0)
    $shape = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $shape.AddArc(2, 6, 20, 20, 90, 180)
    $shape.AddArc(10, 6, 20, 20, 270, 180)
    $shape.CloseFigure()
    $fill = [System.Drawing.Drawing2D.LinearGradientBrush]::new([System.Drawing.PointF]::new(5, 6), [System.Drawing.PointF]::new(25, 26), [System.Drawing.Color]::FromArgb(255, 61, 75, 100), [System.Drawing.Color]::FromArgb(255, 11, 16, 28))
    $edge = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 219, 238, 255), 1.8)
    $graphics.FillPath($fill, $shape)
    $graphics.DrawPath($edge, $shape)
    $wave = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 246, 252, 255), 2.1)
    $wave.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $wave.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $graphics.DrawLine($wave, 11, 14, 11, 18)
    $graphics.DrawLine($wave, 16, 11, 16, 21)
    $graphics.DrawLine($wave, 21, 13, 21, 19)
    $shine = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(180, 148, 213, 255), 1)
    $graphics.DrawArc($shine, 5, 9, 14, 14, 190, 75)
    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames.Add($stream.ToArray())
    if ($size -eq 256) { $bitmap.Save((Join-Path $assetRoot 'icon.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $stream.Dispose(); $shine.Dispose(); $wave.Dispose(); $edge.Dispose(); $fill.Dispose(); $shape.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$file = [System.IO.File]::Create((Join-Path $assetRoot 'QuietIsland.ico'))
$writer = [System.IO.BinaryWriter]::new($file)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
for ($i = 0; $i -lt $frames.Count; $i++) {
    $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $writer.Write($frame) }
$writer.Dispose(); $file.Dispose()
