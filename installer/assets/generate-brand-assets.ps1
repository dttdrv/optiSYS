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
#    file is recognizably "install optiSYS". Composited from AppIcon.png and saved as an .ico.
function New-SetupIcon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    $appPng = [System.Drawing.Image]::FromFile((Join-Path $appAssets 'AppIcon.png'))
    $g.DrawImage($appPng, 0, 0, $size, $size)
    $appPng.Dispose()

    # Download badge: a white circle on the lower-right with an accent download arrow.
    $r = [int]($size * 0.34)
    $cx = [int]($size - $r - $size * 0.04)
    $cy = [int]($size - $r - $size * 0.04)
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $accentBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 36, 130, 150))
    $g.FillEllipse($white, ($cx - $r), ($cy - $r), ($r * 2), ($r * 2))

    # Download arrow (accent): vertical stem + downward head + a base bar.
    $aw = [single]($r * 0.30)               # stem half-width
    $ah = [single]($r * 0.70)               # arrow total half-height
    $top = [single]($cy - $ah)
    $headY = [single]($cy + $ah * 0.15)
    $stem = New-Object System.Drawing.Drawing2D.GraphicsPath
    $stem.AddRectangle((New-Object System.Drawing.RectangleF(($cx - $aw), $top, ($aw * 2), ($headY - $top))))
    $g.FillPath($accentBrush, $stem)
    $head = @(
        (New-Object System.Drawing.PointF(($cx - $aw * 2.1), $headY)),
        (New-Object System.Drawing.PointF(($cx + $aw * 2.1), $headY)),
        (New-Object System.Drawing.PointF($cx, ($cy + $ah)))
    )
    $g.FillPolygon($accentBrush, $head)
    $baseBar = New-Object System.Drawing.RectangleF(($cx - $r * 0.55), ($cy + $ah * 1.15), ($r * 1.10), [single]($r * 0.20))
    $g.FillRectangle($accentBrush, $baseBar)

    $white.Dispose(); $accentBrush.Dispose(); $stem.Dispose(); $g.Dispose()
    return $bmp
}

$setupBmp = New-SetupIcon 256
# PNG preview (for eyeballing) + the .ico the installer actually uses.
$setupBmp.Save((Join-Path $gen 'SetupIcon-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$hicon = $setupBmp.GetHicon()
$icon = [System.Drawing.Icon]::FromHandle($hicon)
$fs = [System.IO.File]::Create((Join-Path $gen 'SetupIcon.ico'))
$icon.Save($fs)
$fs.Close()
$icon.Dispose()
$setupBmp.Dispose()

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
