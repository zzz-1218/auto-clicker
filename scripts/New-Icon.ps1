$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'src\DianXu.App\Assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
$pngs = @()
$sizes = @(16, 24, 32, 48, 64, 128, 256)
foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($size / 256.0, $size / 256.0)
    $round = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $round.AddArc(3, 3, 76, 76, 180, 90); $round.AddArc(177, 3, 76, 76, 270, 90)
    $round.AddArc(177, 177, 76, 76, 0, 90); $round.AddArc(3, 177, 76, 76, 90, 90); $round.CloseFigure()
    $brush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#3869EF'))
    $g.FillPath($brush, $round)
    $cursor = [System.Drawing.PointF[]]@([System.Drawing.PointF]::new(67,48),[System.Drawing.PointF]::new(67,202),[System.Drawing.PointF]::new(108,161),[System.Drawing.PointF]::new(141,220),[System.Drawing.PointF]::new(173,201),[System.Drawing.PointF]::new(140,144),[System.Drawing.PointF]::new(201,144))
    $g.FillPolygon([System.Drawing.Brushes]::White, $cursor)
    $dot = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#FFD893'))
    $g.FillEllipse($dot, 187, 43, 35, 35)
    $stream = [System.IO.MemoryStream]::new(); $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,$stream.ToArray()
    $stream.Dispose(); $dot.Dispose(); $brush.Dispose(); $round.Dispose(); $g.Dispose(); $bitmap.Dispose()
}
$file = [System.IO.File]::Create((Join-Path $assets 'DianXu.ico'))
$writer = [System.IO.BinaryWriter]::new($file)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Length)
$offset = 6 + 16 * $sizes.Length
for ($i = 0; $i -lt $sizes.Length; $i++) {
    $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$pngs[$i].Length); $writer.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $writer.Write([byte[]]$png) }
$writer.Dispose(); $file.Dispose()
Write-Output 'Application icon created.'
