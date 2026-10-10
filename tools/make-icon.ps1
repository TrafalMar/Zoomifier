param([string]$OutIco, [string]$PreviewPng)
Add-Type -AssemblyName System.Drawing

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.ScaleTransform($size / 256.0, $size / 256.0)
    $small = $size -le 24

    $dark = [System.Drawing.Color]::FromArgb(255, 38, 43, 52)
    $orange = [System.Drawing.Color]::FromArgb(255, 255, 106, 43)

    # Monitor
    $body = RoundRect 8 28 196 140 18
    $g.FillPath((New-Object System.Drawing.SolidBrush $dark), $body)
    $screenRect = New-Object System.Drawing.RectangleF 22, 42, 168, 112
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $screenRect, ([System.Drawing.Color]::FromArgb(255, 77, 166, 255)), ([System.Drawing.Color]::FromArgb(255, 26, 95, 180)), 60.0
    $g.FillPath($grad, (RoundRect 22 42 168 112 8))
    if (-not $small) {
        # Selection rectangle on the screen (the region being zoomed)
        $sel = New-Object System.Drawing.Pen $orange, 7
        $sel.DashStyle = 'Dash'
        $g.DrawRectangle($sel, 44, 62, 64, 44)
        # Stand
        $g.FillRectangle((New-Object System.Drawing.SolidBrush $dark), 92, 166, 28, 22)
        $g.FillPath((New-Object System.Drawing.SolidBrush $dark), (RoundRect 62 184 88 14 7))
    }

    # Magnifier
    $cx = 168; $cy = 150; $r = 56
    $handle = New-Object System.Drawing.Pen $dark, 30
    $handle.StartCap = 'Round'; $handle.EndCap = 'Round'
    $g.DrawLine($handle, 208, 190, 238, 220)
    $handleHi = New-Object System.Drawing.Pen $orange, 18
    $handleHi.StartCap = 'Round'; $handleHi.EndCap = 'Round'
    $g.DrawLine($handleHi, 214, 196, 236, 218)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 245, 250, 255))), $cx - $r, $cy - $r, 2 * $r, 2 * $r)
    $ring = New-Object System.Drawing.Pen $dark, ($(if ($small) { 22 } else { 16 }))
    $g.DrawEllipse($ring, $cx - $r, $cy - $r, 2 * $r, 2 * $r)
    # Plus sign
    $plus = New-Object System.Drawing.Pen $orange, ($(if ($small) { 22 } else { 16 }))
    $plus.StartCap = 'Round'; $plus.EndCap = 'Round'
    $g.DrawLine($plus, $cx - 26, $cy, $cx + 26, $cy)
    $g.DrawLine($plus, $cx, $cy - 26, $cx, $cy + 26)

    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $b = Draw $s
    $ms = New-Object System.IO.MemoryStream
    if ($s -ge 256) {
        $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        # Classic DIB entry: BITMAPINFOHEADER + bottom-up BGRA pixels + empty AND mask
        $bw = New-Object System.IO.BinaryWriter $ms
        $bw.Write([uint32]40); $bw.Write([int32]$s); $bw.Write([int32]($s * 2))
        $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
        $bw.Write([uint32]($s * $s * 4)); $bw.Write([int32]0); $bw.Write([int32]0)
        $bw.Write([uint32]0); $bw.Write([uint32]0)
        for ($y = $s - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $s; $x++) {
                $c = $b.GetPixel($x, $y)
                $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
            }
        }
        $maskRow = [int]([math]::Ceiling($s / 32.0) * 4)
        $bw.Write((New-Object byte[] ($maskRow * $s)))
        $bw.Flush()
    }
    if ($s -eq 256 -and $PreviewPng) { $b.Save($PreviewPng) }
    $b.Dispose()
    , $ms.ToArray()
}

# ICO container with PNG-compressed entries
$fs = [System.IO.File]::Create($OutIco)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
