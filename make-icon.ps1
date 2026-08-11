# Regenerates logo.ico for the .exe. Draws the same geometry as the LogoIcon DrawingImage in
# App.xaml (100x100 design grid) - change the shape there and here together, or the window icon
# and the file icon drift apart.
# Run:  powershell -ExecutionPolicy Bypass -File make-icon.ps1
#
# ASCII only. Windows PowerShell 5.1 reads .ps1 as ANSI, so a UTF-8 em-dash decodes to a byte that
# CP1252 maps to a smart closing quote, which silently terminates a string mid-line.

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$sizes = 16, 32, 48, 64, 128, 256
$out   = Join-Path $PSScriptRoot 'logo.ico'

# The M, as filled quads on a 100x100 grid, y down. Overlapping quads union under Winding fill,
# so the joints need no boolean geometry. The inner-left diagonal is three separate parallel bands
# rather than one stroke with holes punched in it - same look, no path arithmetic, and it
# antialiases properly at 16px where a cut-out would not.
$mark = @(
    , @(20.0, 8.0,  33.0, 8.0,  50.0, 72.0, 37.0, 60.0)          # left diagonal, down to the valley
    , @(67.0, 8.0,  80.0, 8.0,  63.0, 60.0, 50.0, 72.0)          # right diagonal, up from the valley
    , @(70.0, 8.0,  80.0, 8.0,  98.0, 94.0, 81.0, 94.0)          # right stem, flared out at the foot
    , @(20.0, 8.0,  22.6, 8.0,   6.42, 94.0,  2.0, 94.0)         # left stem, band 1  - the sliced leg
    , @(23.7, 8.0,  26.3, 8.0,  12.71, 94.0,  8.29, 94.0)        # left stem, band 2
    , @(27.4, 8.0,  30.0, 8.0,  19.0, 94.0,  14.58, 94.0)        # left stem, band 3
)

function New-LogoPng([int]$px) {
    $s   = $px / 100.0
    $bmp = New-Object System.Drawing.Bitmap($px, $px, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.FillMode = 'Winding'
    foreach ($poly in $mark) {
        $pts = New-Object 'System.Collections.Generic.List[System.Drawing.PointF]'
        for ($i = 0; $i -lt $poly.Count; $i += 2) {
            $pts.Add((New-Object System.Drawing.PointF([float]($poly[$i] * $s), [float]($poly[$i+1] * $s))))
        }
        $path.AddPolygon($pts.ToArray())
    }
    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0xF9, 0x73, 0x16))
    $g.FillPath($brush, $path)

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $brush.Dispose(); $path.Dispose(); $g.Dispose(); $bmp.Dispose()
    return , [byte[]]$ms.ToArray()
}

# ICO container: 6-byte header, one 16-byte directory entry per image, then the payloads.
# PNG-compressed entries are the Vista+ form and keep the 256px frame from bloating the file.
# Everything goes through typed collections - a byte[] handed to the pipeline comes back as
# Object[], and BinaryWriter then picks an overload that writes garbage.
$pngs = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($sz in $sizes) { $pngs.Add([byte[]](New-LogoPng $sz)) }

$ico = New-Object 'System.Collections.Generic.List[byte]'
$ico.AddRange([byte[]]@(0, 0, 1, 0))                                  # reserved, type = icon
$ico.AddRange([BitConverter]::GetBytes([uint16]$pngs.Count))

$offset = 6 + 16 * $pngs.Count
for ($i = 0; $i -lt $pngs.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }          # 0 means 256 in the ICO format
    $ico.AddRange([byte[]]@($dim, $dim, 0, 0))                        # w, h, palette count, reserved
    $ico.AddRange([BitConverter]::GetBytes([uint16]1))                # colour planes
    $ico.AddRange([BitConverter]::GetBytes([uint16]32))               # bits per pixel
    $ico.AddRange([BitConverter]::GetBytes([uint32]$pngs[$i].Length))
    $ico.AddRange([BitConverter]::GetBytes([uint32]$offset))
    $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $ico.AddRange($png) }
[System.IO.File]::WriteAllBytes($out, $ico.ToArray())

# read it back - an ICO that Windows cannot parse is the failure mode worth catching here
$check = New-Object System.Drawing.Icon($out)
$frames = ($pngs | ForEach-Object { $_.Length } | Measure-Object -Sum)
if ($frames.Sum -lt 1000) { throw "payloads look empty ($($frames.Sum) bytes) - the icon did not render" }
$check.Dispose()
"wrote $out ($([math]::Round((Get-Item $out).Length / 1KB, 1)) KB, $($pngs.Count) frames: $($sizes -join ', '))"
