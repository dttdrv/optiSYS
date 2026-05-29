# Generates the installer brand assets so they match the CURRENT app icon
# (the gradient-pulse mark). Unlike the old generate_brand_assets.py, this never
# touches src/OptiSYS.App/Assets/AppIcon.* — the app icon is the single source of
# truth and this script consumes it.
#
#   SetupIcon.ico   = a copy of the app icon
#   wizard-main.png = tall left banner (Welcome/Finished pages): gradient + mark + wordmark
#   wizard-small.png= small top-right mark for the inner pages

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSCommandPath          # installer/assets
$gen = Join-Path $root 'generated'
$appAssets = Join-Path (Split-Path -Parent (Split-Path -Parent $root)) 'src\OptiSYS.App\Assets'
New-Item -ItemType Directory -Force -Path $gen | Out-Null

# 1) Installer icon = the app mark + a download-arrow badge on the lower-right, so the setup
#    file is recognizably "install optiSYS". Written as a proper MULTI-SIZE PNG-in-ICO (like
#    AppIcon.ico) — a single-image .ico renders as a broken/fallback glyph in Explorer.
function New-SetupBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    $appPng = [System.Drawing.Image]::FromFile((Join-Path $appAssets 'AppIcon.png'))
    $g.DrawImage($appPng, 0, 0, $size, $size)
    $appPng.Dispose()

    # Download badge: a white circle on the lower-right with an accent download arrow.
    $r = [single]($size * 0.34)
    $cx = [single]($size - $r - $size * 0.04)
    $cy = [single]($size - $r - $size * 0.04)
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $accentBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 36, 130, 150))
    $g.FillEllipse($white, ($cx - $r), ($cy - $r), ($r * 2), ($r * 2))

    $aw = [single]($r * 0.30)               # stem half-width
    $ah = [single]($r * 0.70)               # arrow total half-height
    $top = [single]($cy - $ah)
    $headY = [single]($cy + $ah * 0.15)
    $g.FillRectangle($accentBrush, ($cx - $aw), $top, ($aw * 2), ($headY - $top))
    $head = @(
        (New-Object System.Drawing.PointF(($cx - $aw * 2.1), $headY)),
        (New-Object System.Drawing.PointF(($cx + $aw * 2.1), $headY)),
        (New-Object System.Drawing.PointF($cx, ($cy + $ah)))
    )
    $g.FillPolygon($accentBrush, $head)
    $g.FillRectangle($accentBrush, ($cx - $r * 0.55), ($cy + $ah * 1.15), ($r * 1.10), [single]($r * 0.20))

    $white.Dispose(); $accentBrush.Dispose(); $g.Dispose()
    return $bmp
}

function Get-PngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return ,$bytes
}

# Assemble a multi-size ICO whose images are PNG-compressed (standard for modern icons).
$sizes = @(256, 128, 64, 48, 32, 16)
$pngs = @()
foreach ($s in $sizes) {
    $b = New-SetupBitmap $s
    if ($s -eq 256) { $b.Save((Join-Path $gen 'SetupIcon-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $pngs += , (Get-PngBytes $b)
    $b.Dispose()
}

$icoStream = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($icoStream)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$pngs.Count)   # ICONDIR
$offset = 6 + 16 * $pngs.Count
for ($i = 0; $i -lt $pngs.Count; $i++) {
    $dim = [Byte]($sizes[$i] -band 0xFF)   # 256 -> 0
    $bw.Write($dim); $bw.Write($dim)       # width, height
    $bw.Write([Byte]0); $bw.Write([Byte]0) # colors, reserved
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)  # planes, bpp
    $bw.Write([UInt32]$pngs[$i].Length)    # bytesInRes
    $bw.Write([UInt32]$offset)             # imageOffset
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $gen 'SetupIcon.ico'), $icoStream.ToArray())
$bw.Dispose(); $icoStream.Dispose()

# Palette matched to the gradient-pulse icon (#163A4F -> #4FA5B7 on a near-black field).
$bgTop    = [System.Drawing.Color]::FromArgb(255, 18, 26, 32)
$bgBottom = [System.Drawing.Color]::FromArgb(255, 9, 12, 15)
$accent   = [System.Drawing.Color]::FromArgb(70, 79, 165, 183)
$textCol  = [System.Drawing.Color]::FromArgb(255, 244, 247, 245)
$mutedCol = [System.Drawing.Color]::FromArgb(255, 150, 162, 168)

$mark = [System.Drawing.Image]::FromFile((Join-Path $appAssets 'AppIcon.png'))

function New-Banner([int]$w, [int]$h, [int]$markSize, [int]$markX, [int]$markY, [bool]$withText) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit

    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $bgTop, $bgBottom, 90.0)
    $g.FillRectangle($grad, $rect)

    # soft accent glow behind the mark
    $glow = New-Object System.Drawing.Drawing2D.GraphicsPath
    $glow.AddEllipse(($markX - 60), ($markY - 60), ($markSize + 120), ($markSize + 120))
    $pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush($glow)
    $pgb.CenterColor = $accent
    $pgb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 79, 165, 183))
    $g.FillPath($pgb, $glow)

    $g.DrawImage($mark, $markX, $markY, $markSize, $markSize)

    if ($withText) {
        $fTitle = New-Object System.Drawing.Font('Segoe UI', 22, [System.Drawing.FontStyle]::Bold)
        $fSub = New-Object System.Drawing.Font('Segoe UI', 11, [System.Drawing.FontStyle]::Regular)
        $g.DrawString('optiSYS', $fTitle, (New-Object System.Drawing.SolidBrush($textCol)), [single]$markX, [single]($markY + $markSize + 6))
        $g.DrawString('Safe system optimization.', $fSub, (New-Object System.Drawing.SolidBrush($mutedCol)), [single]$markX, [single]($markY + $markSize + 44))
        $fTitle.Dispose(); $fSub.Dispose()
    }

    $grad.Dispose(); $pgb.Dispose(); $glow.Dispose(); $g.Dispose()
    return $bmp
}

$main = New-Banner 240 459 104 42 64 $true
$main.Save((Join-Path $gen 'wizard-main.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$main.Dispose()

$small = New-Banner 147 147 108 20 20 $false
$small.Save((Join-Path $gen 'wizard-small.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$small.Dispose()

$mark.Dispose()
Write-Host "Installer brand assets regenerated in $gen"
