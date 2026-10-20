# Draws the app icon (a white cloud on the app's blue) and writes Assets\app.ico + Assets\app-256.png.
#   .\tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $PSScriptRoot '..\src\JsyncDrive\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function Draw([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = [single]$size
    # rounded square
    $r = $s * 0.22
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($s - 2 * $r, 0, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($s - 2 * $r, $s - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc(0, $s - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $rect = New-Object System.Drawing.RectangleF 0, 0, $s, $s
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 58, 110, 214)), ([System.Drawing.Color]::FromArgb(255, 36, 82, 182)), 90
    $g.FillPath($grad, $path)
    # cloud: two bumps and a flat base
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $cx = $s * 0.5
    $g.FillEllipse($white, $s * 0.20, $s * 0.42, $s * 0.30, $s * 0.30)
    $g.FillEllipse($white, $s * 0.34, $s * 0.28, $s * 0.36, $s * 0.36)
    $g.FillEllipse($white, $s * 0.52, $s * 0.40, $s * 0.28, $s * 0.28)
    $g.FillRectangle($white, $s * 0.34, $s * 0.50, $s * 0.32, $s * 0.22)
    $g.FillEllipse($white, $s * 0.20, $s * 0.46, $s * 0.28, $s * 0.26)
    $g.FillEllipse($white, $s * 0.52, $s * 0.44, $s * 0.28, $s * 0.28)
    $g.FillRectangle($white, $s * 0.34, $s * 0.46, $s * 0.32, $s * 0.26)
    $g.Dispose()
    return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($n in $sizes) {
    $bmp = Draw $n
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($n -eq 256) { $bmp.Save((Join-Path $assets 'app-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    , @($n, $ms.ToArray())
}
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $n = $p[0]; $data = $p[1]
    $w.Write([byte]($(if ($n -ge 256) { 0 } else { $n }))); $w.Write([byte]($(if ($n -ge 256) { 0 } else { $n })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$data.Length); $w.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($p in $pngs) { $w.Write([byte[]]$p[1]) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'app.ico'), $out.ToArray())
'wrote app.ico and app-256.png'
