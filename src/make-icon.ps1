# Draws VramMeter.ico: the meter's bar on a dark tile (teal for the game, white for the rest), with VRAM written
# above it from 24 px up,
# at 16, 20, 24, 32, 40, 48, 64 and 256 px. The tray draws the same tile live (src\VramMeter.cs).
Add-Type -AssemblyName System.Drawing
$out = Join-Path (Split-Path $PSScriptRoot) 'VramMeter.ico'

function Draw([int]$size) {
    $bmp = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $s = $size / 32.0
    $r = 7 * $s; $x = 1 * $s; $w = 30 * $s
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x, $x, 2 * $r, 2 * $r, 180, 90); $path.AddArc($x + $w - 2 * $r, $x, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($x + $w - 2 * $r, $x + $w - 2 * $r, 2 * $r, 2 * $r, 0, 90); $path.AddArc($x, $x + $w - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 4, 16, 14))), $path)
    $g.DrawPath((New-Object Drawing.Pen ([Drawing.Color]::FromArgb(255, 44, 66, 62)), ([math]::Max(1, $s))), $path)

    # With the label (24 px and up) the bar sits lower: frame y 13 to 25, ticks y 15 to 23.
    $label = $size -ge 24
    if ($label) { $ft = 13; $fb = 25; $tt = 15; $th = 8 } else { $ft = 8; $fb = 23; $tt = 10; $th = 10 }
    # The bar: ticks from x 7 to 25, 60% teal then 20% white; the frame under them.
    $step = [math]::Max(2, [math]::Round(2 * $s))
    $tick = [math]::Max(1, [math]::Round($s))
    $left = [math]::Round(7 * $s); $right = [math]::Round(25 * $s)
    for ($tx = $left; $tx -lt $right; $tx += $step) {
        $f = ($tx - $left) / ($right - $left)
        if ($f -ge 0.8) { break }
        $c = if ($f -lt 0.6) { [Drawing.Color]::FromArgb(255, 4, 212, 174) } else { [Drawing.Color]::FromArgb(255, 227, 232, 232) }
        $g.FillRectangle((New-Object Drawing.SolidBrush $c), $tx, [math]::Round($tt * $s), $tick, [math]::Round($th * $s))
    }
    $frame = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(255, 227, 232, 232)), ([math]::Max(1, 1.6 * $s))
    $frame.LineJoin = 'Miter'
    $pts = @(@(5, $ft), @(5, $fb), @(27, $fb), @(27, $ft)) | ForEach-Object { New-Object Drawing.PointF ($_[0] * $s), ($_[1] * $s) }
    $g.DrawLines($frame, [Drawing.PointF[]]$pts)
    if ($label) {
        # VRAM in the HUD face, bold, centred above the bar.
        $g.TextRenderingHint = 'AntiAliasGridFit'
        $font = New-Object Drawing.Font 'Bahnschrift SemiBold SemiConden', (8.2 * $s), ([Drawing.FontStyle]::Bold), ([Drawing.GraphicsUnit]::Pixel)
        $fmt = New-Object Drawing.StringFormat; $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Far'
        $g.DrawString('VRAM', $font, (New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 227, 232, 232))), (New-Object Drawing.RectangleF 0, 0, $size, (11.2 * $s)), $fmt)
    }
    $g.Dispose()
    $bmp
}

# ICO: 32-bit DIB entries for the small sizes, PNG for 256
$images = foreach ($size in 16, 20, 24, 32, 40, 48, 64, 256) {
    $bmp = Draw $size
    $ms = New-Object IO.MemoryStream
    if ($size -eq 256) {
        $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    } else {
        $bw = New-Object IO.BinaryWriter $ms
        $mask = [int][math]::Floor(($size + 31) / 32) * 4 * $size
        $bw.Write([int]40); $bw.Write([int]$size); $bw.Write([int]($size * 2)); $bw.Write([int16]1); $bw.Write([int16]32)
        $bw.Write([int]0); $bw.Write([int]($size * $size * 4 + $mask)); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
        for ($y = $size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $size; $x++) { $c = $bmp.GetPixel($x, $y); $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A) }
        }
        $bw.Write((New-Object byte[] $mask))
        $bw.Flush()
    }
    [pscustomobject]@{ Size = $size; Bytes = $ms.ToArray() }
}

$fs = [IO.File]::Create($out); $bw = New-Object IO.BinaryWriter $fs
$bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($i in $images) {
    $d = if ($i.Size -ge 256) { 0 } else { $i.Size }
    $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]$i.Bytes.Length); $bw.Write([int]$offset); $offset += $i.Bytes.Length
}
foreach ($i in $images) { $bw.Write($i.Bytes) }
$bw.Close()
"Wrote $out"
