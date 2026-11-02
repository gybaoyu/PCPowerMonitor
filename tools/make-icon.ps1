# 生成 assets\app.ico —— 圆角渐变底 + 闪电，多尺寸（16~256，经典 DIB 条目，兼容性最好）
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root 'assets'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$icoPath = Join-Path $outDir 'app.ico'

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

function New-Path([System.Drawing.RectangleF]$r, [double]$rad) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [float]($rad * 2)
    if ($d -gt $r.Width) { $d = $r.Width }
    if ($d -gt $r.Height) { $d = $r.Height }
    $p.AddArc($r.X, $r.Y, $d, $d, 180, 90)
    $p.AddArc($r.Right - $d, $r.Y, $d, $d, 270, 90)
    $p.AddArc($r.Right - $d, $r.Bottom - $d, $d, $d, 0, 90)
    $p.AddArc($r.X, $r.Bottom - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [Math]::Max(0.5, $size * 0.015)
    $rect = New-Object System.Drawing.RectangleF($pad, $pad, ($size - 2 * $pad), ($size - 2 * $pad))
    $path = New-Path $rect ($rect.Width * 0.235)

    $c1 = [System.Drawing.Color]::FromArgb(255, 74, 132, 255)
    $c2 = [System.Drawing.Color]::FromArgb(255, 154, 104, 255)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, 55.0)
    $g.FillPath($brush, $path)

    # 顶部高光
    $hlH = [float]($rect.Height * 0.5)
    $hlRect = New-Object System.Drawing.RectangleF([float]$rect.X, [float]$rect.Y, [float]$rect.Width, $hlH)
    $hl = New-Object System.Drawing.Drawing2D.LinearGradientBrush($hlRect, [System.Drawing.Color]::FromArgb(55, 255, 255, 255), [System.Drawing.Color]::FromArgb(0, 255, 255, 255), 90.0)
    $g.FillPath($hl, $path)

    # 闪电
    $s = [float]$size
    $pts = @(
        (New-Object System.Drawing.PointF(($s * 0.585), ($s * 0.135))),
        (New-Object System.Drawing.PointF(($s * 0.285), ($s * 0.555))),
        (New-Object System.Drawing.PointF(($s * 0.465), ($s * 0.555))),
        (New-Object System.Drawing.PointF(($s * 0.415), ($s * 0.875))),
        (New-Object System.Drawing.PointF(($s * 0.725), ($s * 0.455))),
        (New-Object System.Drawing.PointF(($s * 0.535), ($s * 0.455)))
    )
    $bolt = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bolt.AddPolygon([System.Drawing.PointF[]]$pts)
    $bolt.CloseFigure()
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 255, 255))
    $g.FillPath($white, $bolt)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 255, 255, 255), [float]([Math]::Max(0.8, $s * 0.045)))
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPath($pen, $bolt)

    $pen.Dispose(); $white.Dispose(); $bolt.Dispose()
    $brush.Dispose(); $hl.Dispose(); $path.Dispose()
    $g.Dispose()
    return $bmp
}

$entries = New-Object System.Collections.ArrayList

foreach ($size in $sizes) {
    $bmp = New-IconBitmap $size
    $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $bd = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $bd.Stride
    $buf = New-Object byte[] ($stride * $size)
    [System.Runtime.InteropServices.Marshal]::Copy($bd.Scan0, $buf, 0, $buf.Length)
    $bmp.UnlockBits($bd)
    $bmp.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    # BITMAPINFOHEADER (高度翻倍: XOR + AND)
    $bw.Write([int]40)
    $bw.Write([int]$size)
    $bw.Write([int]($size * 2))
    $bw.Write([int16]1)
    $bw.Write([int16]32)
    $bw.Write([int]0)
    $bw.Write([int]($size * $size * 4))
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    # XOR 像素, 自下而上
    for ($y = $size - 1; $y -ge 0; $y--) { $bw.Write($buf, $y * $stride, $size * 4) }
    # AND 掩码全 0 (alpha 决定透明)
    $maskRow = [int]([Math]::Ceiling($size / 32.0) * 4)
    $zero = New-Object byte[] $maskRow
    for ($y = 0; $y -lt $size; $y++) { $bw.Write($zero, 0, $maskRow) }
    $bw.Flush()
    [void]$entries.Add(@{ Size = $size; Data = $ms.ToArray() })
    $bw.Dispose(); $ms.Dispose()
}

$fs = [System.IO.File]::Create($icoPath)
$w = New-Object System.IO.BinaryWriter($fs)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $s = [int]$e.Size
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim)
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$e.Data.Length); $w.Write([uint32]$offset)
    $offset += $e.Data.Length
}
foreach ($e in $entries) { $w.Write([byte[]]$e.Data) }
$w.Flush(); $w.Dispose(); $fs.Dispose()

Write-Host ("ICO 生成: {0}  ({1:N0} 字节, {2} 尺寸)" -f $icoPath, (Get-Item $icoPath).Length, $entries.Count)
