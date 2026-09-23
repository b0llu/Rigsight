# Regenerates assets/Rigsight.ico (multi-size, PNG-compressed entries) and assets/Rigsight.png.
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $PSScriptRoot '..\assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'

    # Rounded square with a diagonal blue -> teal gradient.
    $r = [single]($s * 0.23)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, 2*$r, 2*$r, 180, 90)
    $path.AddArc($s - 2*$r - 1, 0, 2*$r, 2*$r, 270, 90)
    $path.AddArc($s - 2*$r - 1, $s - 2*$r - 1, 2*$r, 2*$r, 0, 90)
    $path.AddArc(0, $s - 2*$r - 1, 2*$r, 2*$r, 90, 90)
    $path.CloseFigure()
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0,0), (New-Object System.Drawing.PointF $s,$s), ([System.Drawing.Color]::FromArgb(255,91,140,255)), ([System.Drawing.Color]::FromArgb(255,61,220,151))
    $g.FillPath($grad, $path)

    # Pulse line.
    $w = [single][Math]::Max(1.6, $s * 0.085)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $w
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $pts = @(
        @(0.16, 0.54), @(0.34, 0.54), @(0.43, 0.30), @(0.55, 0.74), @(0.64, 0.46), @(0.70, 0.54), @(0.84, 0.54)
    ) | ForEach-Object { New-Object System.Drawing.PointF ([single]($_[0] * $s)), ([single]($_[1] * $s)) }
    $g.DrawLines($pen, [System.Drawing.PointF[]]$pts)

    # Small "hot" dot at the end of the line.
    $d = [single]($s * 0.13)
    $dot = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 251, 191, 36))
    $g.FillEllipse($dot, [single](0.84 * $s - $d/2), [single](0.54 * $s - $d/2), $d, $d)

    $g.Dispose()
    return $bmp
}

function Get-PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return ,([byte[]]$ms.ToArray())
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = New-Object System.Collections.Generic.List[byte[]]
foreach ($s in $sizes) {
    $b = New-IconBitmap $s
    $pngs.Add([byte[]](Get-PngBytes $b))
    if ($s -eq 256) { $b.Save((Join-Path $assets 'Rigsight.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $b.Dispose()
}

$fs = [System.IO.File]::Create((Join-Path $assets 'Rigsight.ico'))
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
Write-Host "Wrote $assets\Rigsight.ico"
