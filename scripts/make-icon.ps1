# Draws the Trayvora icon (rounded indigo-to-cyan tile with white voice-wave bars)
# and writes Assets/trayvora.ico (16-256 px) and Assets/trayvora.png (256 px).
# Small sizes use three thicker bars so the shape stays readable in the tray.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $PSScriptRoot '../src/VoiceAgent.Desktop/Assets'

function New-IconBitmap([int]$size) {
    $scale = 4
    $s = $size * $scale
    $big = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($big)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Tile: rounded square with a diagonal gradient.
    $pad = [Math]::Max(0, [int]($s * 0.03))
    $w = $s - 2 * $pad
    $r = $w * 0.24
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tile.AddArc($pad, $pad, 2 * $r, 2 * $r, 180, 90)
    $tile.AddArc($pad + $w - 2 * $r, $pad, 2 * $r, 2 * $r, 270, 90)
    $tile.AddArc($pad + $w - 2 * $r, $pad + $w - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $tile.AddArc($pad, $pad + $w - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $tile.CloseFigure()
    $rect = New-Object System.Drawing.RectangleF $pad, $pad, $w, $w
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 79, 70, 229)), ([System.Drawing.Color]::FromArgb(255, 6, 182, 212)), 45.0
    $g.FillPath($fill, $tile)

    # Soft highlight on the upper half for depth (large sizes only).
    if ($size -ge 48) {
        $g.SetClip($tile)
        $shine = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(28, 255, 255, 255))
        $g.FillEllipse($shine, -$s * 0.3, -$s * 0.75, $s * 1.6, $s * 1.15)
        $g.ResetClip()
    }

    # Voice-wave bars.
    $heights = if ($size -le 24) { @(0.36, 0.62, 0.36) } else { @(0.26, 0.50, 0.70, 0.44, 0.22) }
    $count = $heights.Count
    $barW = if ($size -le 24) { $w * 0.15 } else { $w * 0.09 }
    $gap = if ($size -le 24) { $w * 0.09 } else { $w * 0.065 }
    $total = $count * $barW + ($count - 1) * $gap
    $x = $pad + ($w - $total) / 2
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    foreach ($h in $heights) {
        $bh = $w * $h
        $y = $pad + ($w - $bh) / 2
        $bar = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = $barW
        $bar.AddArc($x, $y, $d, $d, 180, 180)
        $bar.AddArc($x, $y + $bh - $d, $d, $d, 0, 180)
        $bar.CloseFigure()
        $g.FillPath($white, $bar)
        $x += $barW + $gap
    }
    $g.Dispose()

    $small = New-Object System.Drawing.Bitmap $size, $size
    $g2 = [System.Drawing.Graphics]::FromImage($small)
    $g2.InterpolationMode = 'HighQualityBicubic'
    $g2.PixelOffsetMode = 'HighQuality'
    $g2.DrawImage($big, 0, 0, $size, $size)
    $g2.Dispose()
    $big.Dispose()
    return $small
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($size in $sizes) {
    $bitmap = New-IconBitmap $size
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($size -eq 256) { $bitmap.Save((Join-Path $assets 'trayvora.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    if ($size -eq 32) { $bitmap.Save((Join-Path $assets 'tray.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bitmap.Dispose()
    , $stream.ToArray()
}

# ICO container with PNG-compressed entries (supported since Windows Vista).
$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $out
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dim); $writer.Write([byte]$dim); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($bytes in $images) { $writer.Write($bytes) }
$writer.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'trayvora.ico'), $out.ToArray())
Write-Output "Wrote trayvora.ico ($($sizes -join ', ') px), trayvora.png and tray.png"
