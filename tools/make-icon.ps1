Add-Type -AssemblyName System.Drawing

# Draws the Nimbus cloud glyph (matches the runtime tray icon) and writes a
# multi-resolution app.ico containing PNG-compressed frames.

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $d = $r * 2
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-CloudBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = $size / 32.0
    $bgColor = [System.Drawing.ColorTranslator]::FromHtml('#2D6CDF')
    $bg = New-Object System.Drawing.SolidBrush($bgColor)
    $radius = [single](6 * $s)
    $bgPath = New-RoundedPath 0 0 $size $size $radius
    $g.FillPath($bg, $bgPath)

    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $g.FillEllipse($white, [single](5 * $s), [single](13 * $s), [single](12 * $s), [single](12 * $s))
    $g.FillEllipse($white, [single](12 * $s), [single](8 * $s), [single](15 * $s), [single](15 * $s))
    $g.FillEllipse($white, [single](18 * $s), [single](14 * $s), [single](10 * $s), [single](10 * $s))
    $basePath = New-RoundedPath ([single](6 * $s)) ([single](17 * $s)) ([single](20 * $s)) ([single](8 * $s)) ([single](4 * $s))
    $g.FillPath($white, $basePath)

    $g.Dispose()
    return $bmp
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngs = @()
foreach ($size in $sizes) {
    $bmp = New-CloudBitmap $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    $bmp.Dispose()
    $ms.Dispose()
}

$outPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'app.ico'
$fs = [System.IO.File]::Create($outPath)
$bw = New-Object System.IO.BinaryWriter($fs)

# ICONDIR
$bw.Write([uint16]0)            # reserved
$bw.Write([uint16]1)            # type = icon
$bw.Write([uint16]$sizes.Count) # image count

$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]
    $len = $pngs[$i].Length
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $bw.Write([byte]$dim)   # width
    $bw.Write([byte]$dim)   # height
    $bw.Write([byte]0)      # color count
    $bw.Write([byte]0)      # reserved
    $bw.Write([uint16]1)    # color planes
    $bw.Write([uint16]32)   # bits per pixel
    $bw.Write([uint32]$len) # bytes in resource
    $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($png in $pngs) { $bw.Write($png) }

$bw.Flush(); $bw.Close(); $fs.Close()
Write-Host "Wrote $outPath"
