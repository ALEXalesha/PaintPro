# Generate the PaintPro app icon (.ico) at multiple resolutions.
# Run with: powershell -ExecutionPolicy Bypass -File tools/generate-icon.ps1
Add-Type -AssemblyName System.Drawing

$sizes = @(256, 128, 64, 48, 32, 16)
$outIco = Join-Path $PSScriptRoot '..\src\PaintPro.Wpf\Assets\AppIcon.ico'
New-Item -ItemType Directory -Force -Path (Split-Path $outIco) | Out-Null

$pngs = @()
foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode    = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode  = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    # Rounded background with the gradient from the spec (purple → blue).
    $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush `
        $rect, ([System.Drawing.Color]::FromArgb(0x5B,0x8D,0xEF)), `
        ([System.Drawing.Color]::FromArgb(0x9D,0x5B,0xEF)), 45.0
    $r = [Math]::Max(2, [int]($size * 0.18))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, $r*2, $r*2, 180, 90)
    $path.AddArc($rect.Right - $r*2, $rect.Y, $r*2, $r*2, 270, 90)
    $path.AddArc($rect.Right - $r*2, $rect.Bottom - $r*2, $r*2, $r*2, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $r*2, $r*2, $r*2, 90, 90)
    $path.CloseAllFigures()
    $g.FillPath($brush, $path)

    # Paint palette circle.
    $cx = $size * 0.42; $cy = $size * 0.58; $rad = $size * 0.30
    $paletteBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0xF4,0xF4,0xF8))
    $g.FillEllipse($paletteBrush, [single]($cx - $rad), [single]($cy - $rad), [single]($rad*2), [single]($rad*2))

    # Thumb hole.
    $holeBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x14,0x14,0x1F))
    $hr = $rad * 0.30
    $g.FillEllipse($holeBrush, [single]($cx + $rad*0.30 - $hr), [single]($cy + $rad*0.05 - $hr), [single]($hr*2), [single]($hr*2))

    # Three colour blobs.
    $blobs = @(
        @{ x = $cx - $rad*0.55; y = $cy - $rad*0.50; c = [System.Drawing.Color]::FromArgb(0xEF,0x5B,0x6E) },
        @{ x = $cx - $rad*0.05; y = $cy - $rad*0.70; c = [System.Drawing.Color]::FromArgb(0xEF,0xC8,0x5B) },
        @{ x = $cx - $rad*0.65; y = $cy + $rad*0.05; c = [System.Drawing.Color]::FromArgb(0x5B,0xEF,0xA3) }
    )
    foreach ($b in $blobs) {
        $br = New-Object System.Drawing.SolidBrush $b.c
        $br2 = $rad * 0.18
        $g.FillEllipse($br, [single]($b.x - $br2), [single]($b.y - $br2), [single]($br2*2), [single]($br2*2))
    }

    # Brush stroke handle (diagonal).
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(0xF4,0xF4,0xF8)), ([single]($size*0.08))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($pen, [single]($size*0.62), [single]($size*0.18), [single]($size*0.88), [single]($size*0.44))
    # Brush tip.
    $tipBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0xEF,0x5B,0x6E))
    $g.FillEllipse($tipBrush, [single]($size*0.55), [single]($size*0.10), [single]($size*0.16), [single]($size*0.16))

    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,@($size, $ms.ToArray())
    $bmp.Dispose()
}

# Pack PNGs into an ICO file (PNG-in-ICO format, supported on Windows Vista+).
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out

# ICONDIR
$bw.Write([uint16]0)            # reserved
$bw.Write([uint16]1)            # type = icon
$bw.Write([uint16]$pngs.Count)  # number of images

# Each ICONDIRENTRY is 16 bytes; image data follows.
$dataOffset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $size = $p[0]
    $data = $p[1]
    $iconSize = if ($size -ge 256) { 0 } else { $size }  # 0 means 256
    $bw.Write([byte]$iconSize)     # width
    $bw.Write([byte]$iconSize)     # height
    $bw.Write([byte]0)             # palette colours
    $bw.Write([byte]0)             # reserved
    $bw.Write([uint16]1)           # planes
    $bw.Write([uint16]32)          # bpp
    $bw.Write([uint32]$data.Length)
    $bw.Write([uint32]$dataOffset)
    $dataOffset += $data.Length
}
foreach ($p in $pngs) {
    $bw.Write($p[1])
}
$bw.Flush()

[System.IO.File]::WriteAllBytes($outIco, $out.ToArray())
Write-Host "Wrote $outIco ($($out.Length) bytes, $($pngs.Count) resolutions)"
