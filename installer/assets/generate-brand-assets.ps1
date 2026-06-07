# Generates all optiSYS icon/brand assets from the single source mark
# (installer/assets/icon-source.png — a transparent Fluent icon), preserving alpha throughout:
#
#   src/OptiSYS.App/Assets/AppIcon.png + AppIcon.ico  = app icon (transparent, multi-size PNG-in-ICO)
#   installer/assets/generated/SetupIcon.ico          = installer icon = the mark + a download badge
#   installer/assets/generated/wizard-small.png       = the mark shown in the compact installer window
#
# The source is cropped to its non-transparent bounds and scaled to fill the icon canvas, so the mark
# reads at full size (no shrunken look from the model's transparent margin). Run standalone or via
# build-installer.ps1 (before the build). Deterministic.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSCommandPath          # installer/assets
$gen = Join-Path $root 'generated'
$src = Join-Path $root 'icon-source.png'
$appAssets = Join-Path (Split-Path -Parent (Split-Path -Parent $root)) 'src\OptiSYS.App\Assets'
New-Item -ItemType Directory -Force -Path $gen | Out-Null
New-Item -ItemType Directory -Force -Path $appAssets | Out-Null

if (-not (Test-Path $src)) { throw "icon source not found: $src" }
# Load the source image ONCE (over a MemoryStream kept alive for the whole run) and reuse it for
# every DrawImage — re-decoding per call would leak a stream each time and waste work.
$srcStream = New-Object System.IO.MemoryStream(, [System.IO.File]::ReadAllBytes($src))
$srcImg = [System.Drawing.Image]::FromStream($srcStream)
$srcW = $srcImg.Width
$srcH = $srcImg.Height

# Non-transparent bounding box of the source (in source px), via a fast downscaled alpha scan.
function Get-SourceBounds {
    $S = 128
    $small = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($small)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($srcImg, 0, 0, $S, $S)
    $g.Dispose()
    $bd = $small.LockBits((New-Object System.Drawing.Rectangle(0, 0, $S, $S)),
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $bd.Stride
    $buf = New-Object byte[] ($stride * $S)
    [System.Runtime.InteropServices.Marshal]::Copy($bd.Scan0, $buf, 0, $buf.Length)
    $small.UnlockBits($bd); $small.Dispose()
    $minX = $S; $minY = $S; $maxX = -1; $maxY = -1
    for ($y = 0; $y -lt $S; $y++) {
        $ro = $y * $stride
        for ($x = 0; $x -lt $S; $x++) {
            if ($buf[$ro + $x * 4 + 3] -gt 10) {
                if ($x -lt $minX) { $minX = $x }; if ($x -gt $maxX) { $maxX = $x }
                if ($y -lt $minY) { $minY = $y }; if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }
    if ($maxX -lt 0) { return @(0, 0, $srcW, $srcH) }
    $fx = $srcW / $S; $fy = $srcH / $S
    $bx = [Math]::Max(0, [int]($minX * $fx))
    $by = [Math]::Max(0, [int]($minY * $fy))
    # Clamp so srcX+srcW / srcY+srcH never exceed the source (independent rounding otherwise smears).
    $bw = [Math]::Min($srcW - $bx, [int]((($maxX - $minX + 1) * $fx) + 1))
    $bh = [Math]::Min($srcH - $by, [int]((($maxY - $minY + 1) * $fy) + 1))
    return @($bx, $by, $bw, $bh)
}

$bounds = Get-SourceBounds   # @(x, y, w, h) in source px

function Pt($cx, $cy) { return (New-Object System.Drawing.PointF([single]$cx, [single]$cy)) }

# Minimalist download badge (accent disc + white arrow) on the lower-right, for the installer icon.
function Add-DownloadBadge($g, [int]$s) {
    $r = [single]($s * 0.27)
    $cx = [single]($s - $r - $s * 0.05)
    $cy = [single]($s - $r - $s * 0.05)
    $ring = [single]($s * 0.02)
    $halo = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $g.FillEllipse($halo, ($cx - $r - $ring), ($cy - $r - $ring), (($r + $ring) * 2), (($r + $ring) * 2))
    $accent = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 30, 120, 180))
    $g.FillEllipse($accent, ($cx - $r), ($cy - $r), ($r * 2), ($r * 2))
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [single]($r * 0.18))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawLine($pen, (Pt $cx ($cy - $r * 0.5)), (Pt $cx ($cy + $r * 0.2)))
    $g.DrawLines($pen, [System.Drawing.PointF[]]@((Pt ($cx - $r * 0.42) ($cy - $r * 0.08)), (Pt $cx ($cy + $r * 0.38)), (Pt ($cx + $r * 0.42) ($cy - $r * 0.08))))
    $g.DrawLine($pen, (Pt ($cx - $r * 0.45) ($cy + $r * 0.6)), (Pt ($cx + $r * 0.45) ($cy + $r * 0.6)))
    $halo.Dispose(); $accent.Dispose(); $pen.Dispose()
}

function New-IconBitmap([int]$size, [bool]$withBadge) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $m = [int]($size * 0.04)
    $dest = New-Object System.Drawing.Rectangle($m, $m, ($size - 2 * $m), ($size - 2 * $m))
    $g.DrawImage($srcImg, $dest, $bounds[0], $bounds[1], $bounds[2], $bounds[3], [System.Drawing.GraphicsUnit]::Pixel)
    if ($withBadge) { Add-DownloadBadge $g $size }
    $g.Dispose()
    return $bmp
}

function Get-PngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray(); $ms.Dispose()
    return , $bytes
}

# Multi-size PNG-in-ICO (alpha preserved -> transparent corners, not black).
function Save-Ico([bool]$withBadge, [string]$icoPath, [string]$previewPath) {
    $sizes = @(256, 128, 64, 48, 32, 16)
    $pngs = @()
    foreach ($s in $sizes) {
        $bmp = New-IconBitmap $s $withBadge
        if ($s -eq 256 -and $previewPath) { $bmp.Save($previewPath, [System.Drawing.Imaging.ImageFormat]::Png) }
        $pngs += , (Get-PngBytes $bmp)
        $bmp.Dispose()
    }
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$pngs.Count)
    $offset = 6 + 16 * $pngs.Count
    for ($i = 0; $i -lt $pngs.Count; $i++) {
        $dim = [Byte]($sizes[$i] -band 0xFF)
        $bw.Write($dim); $bw.Write($dim)
        $bw.Write([Byte]0); $bw.Write([Byte]0)
        $bw.Write([UInt16]1); $bw.Write([UInt16]32)
        $bw.Write([UInt32]$pngs[$i].Length)
        $bw.Write([UInt32]$offset)
        $offset += $pngs[$i].Length
    }
    foreach ($p in $pngs) { $bw.Write($p) }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($icoPath, $ms.ToArray())
    $bw.Dispose(); $ms.Dispose()
}

# App icon (no badge) -> src/Assets.
$app256 = New-IconBitmap 256 $false
$app256.Save((Join-Path $appAssets 'AppIcon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$app256.Dispose()
Save-Ico $false (Join-Path $appAssets 'AppIcon.ico') $null

# Installer icon (with download badge) -> generated.
Save-Ico $true (Join-Path $gen 'SetupIcon.ico') (Join-Path $gen 'SetupIcon-preview.png')

# Compact installer window mark (no badge), larger so it doesn't look small in the window.
$wiz = New-IconBitmap 96 $false
$wiz.Save((Join-Path $gen 'wizard-small.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$wiz.Dispose()

$srcImg.Dispose(); $srcStream.Dispose()
Write-Host "Brand assets regenerated (cropped-to-fill): AppIcon.* + SetupIcon.ico (+badge) + wizard-small.png ($gen)"
