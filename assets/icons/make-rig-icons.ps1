$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Sortie = à côté du script (versionné dans le repo : RIG-TV\assets\icons — l'ancien
# emplacement C:\Code RIG\Audit\icons a été supprimé le 29/06 avec la purge d'Audit).
$outDir = $PSScriptRoot
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

function New-RoundedPath {
    param([single]$x, [single]$y, [single]$w, [single]$h, [single]$r)
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Get-GearPath {
    param([single]$cx, [single]$cy, [single]$rOut, [single]$rIn, [int]$teeth)
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $frac = @(0.06, 0.31, 0.44, 0.94)
    $outer = @($true, $true, $false, $false)
    $pts = New-Object System.Collections.Generic.List[System.Drawing.PointF]
    for ($t = 0; $t -lt $teeth; $t++) {
        for ($k = 0; $k -lt 4; $k++) {
            $ang = [Math]::PI * 2 * ($t + $frac[$k]) / $teeth
            $r = if ($outer[$k]) { $rOut } else { $rIn }
            $px = $cx + [Math]::Cos($ang) * $r
            $py = $cy + [Math]::Sin($ang) * $r
            $pts.Add((New-Object System.Drawing.PointF([single]$px, [single]$py)))
        }
    }
    $p.AddPolygon($pts.ToArray())
    return $p
}

function Draw-Symbol {
    param($g, [string]$kind, [int]$S)
    $cx = $S * 0.5
    $cy = $S * 0.5
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 255, 255))

    if ($kind -eq 'debug') {
        # Gear (engrenage)
        $rOut = $S * 0.30
        $rIn  = $S * 0.225
        $gear = Get-GearPath $cx $cy $rOut $rIn 8
        $g.FillPath($white, $gear)
        # center hole reveals badge color
        $holeR = $S * 0.115
        $badge = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            (New-Object System.Drawing.RectangleF(0, 0, [single]$S, [single]$S)),
            [System.Drawing.Color]::FromArgb(255, 243, 156, 18),
            [System.Drawing.Color]::FromArgb(255, 211, 84, 0), 90)
        $g.FillEllipse($badge, [single]($cx - $holeR), [single]($cy - $holeR), [single]($holeR * 2), [single]($holeR * 2))
        $gear.Dispose(); $badge.Dispose()
    }
    else {
        # Rocket (fusee)
        $bw = $S * 0.26
        $bh = $S * 0.42
        $top = $cy - $bh * 0.5
        $bot = $cy + $bh * 0.5
        $noseH = $S * 0.15
        $body = New-Object System.Drawing.Drawing2D.GraphicsPath
        $body.AddLine([single]($cx - $bw/2), [single]($top + $noseH), [single]$cx, [single]$top)
        $body.AddLine([single]$cx, [single]$top, [single]($cx + $bw/2), [single]($top + $noseH))
        $body.AddLine([single]($cx + $bw/2), [single]($top + $noseH), [single]($cx + $bw/2), [single]($bot - $S*0.08))
        $body.AddLine([single]($cx + $bw/2), [single]($bot - $S*0.08), [single]($cx + $bw*0.32), [single]$bot)
        $body.AddLine([single]($cx + $bw*0.32), [single]$bot, [single]($cx - $bw*0.32), [single]$bot)
        $body.AddLine([single]($cx - $bw*0.32), [single]$bot, [single]($cx - $bw/2), [single]($bot - $S*0.08))
        $body.CloseFigure()
        $g.FillPath($white, $body)

        # Fins (white)
        $finL = New-Object System.Drawing.Drawing2D.GraphicsPath
        $finL.AddPolygon(@(
            (New-Object System.Drawing.PointF([single]($cx - $bw/2), [single]($cy + $S*0.04))),
            (New-Object System.Drawing.PointF([single]($cx - $bw/2 - $S*0.13), [single]($bot + $S*0.03))),
            (New-Object System.Drawing.PointF([single]($cx - $bw/2), [single]($bot - $S*0.05)))
        ))
        $g.FillPath($white, $finL)
        $finR = New-Object System.Drawing.Drawing2D.GraphicsPath
        $finR.AddPolygon(@(
            (New-Object System.Drawing.PointF([single]($cx + $bw/2), [single]($cy + $S*0.04))),
            (New-Object System.Drawing.PointF([single]($cx + $bw/2 + $S*0.13), [single]($bot + $S*0.03))),
            (New-Object System.Drawing.PointF([single]($cx + $bw/2), [single]($bot - $S*0.05)))
        ))
        $g.FillPath($white, $finR)

        # Window (badge green)
        $winR = $S * 0.075
        $winCx = $cx
        $winCy = $top + $noseH + $S * 0.085
        $badge = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            (New-Object System.Drawing.RectangleF(0, 0, [single]$S, [single]$S)),
            [System.Drawing.Color]::FromArgb(255, 46, 204, 113),
            [System.Drawing.Color]::FromArgb(255, 33, 140, 80), 90)
        $g.FillEllipse($badge, [single]($winCx - $winR), [single]($winCy - $winR), [single]($winR*2), [single]($winR*2))

        # Flame
        if ($S -ge 24) {
            $flame = New-Object System.Drawing.Drawing2D.GraphicsPath
            $flame.AddPolygon(@(
                (New-Object System.Drawing.PointF([single]($cx - $bw*0.26), [single]($bot + $S*0.005))),
                (New-Object System.Drawing.PointF([single]$cx, [single]($bot + $S*0.15))),
                (New-Object System.Drawing.PointF([single]($cx + $bw*0.26), [single]($bot + $S*0.005)))
            ))
            $flameBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 213, 79))
            $g.FillPath($flameBrush, $flame)
            $flame.Dispose(); $flameBrush.Dispose()
        }
        $body.Dispose(); $finL.Dispose(); $finR.Dispose(); $badge.Dispose()
    }
    $white.Dispose()
}

function New-IconBitmap {
    param([int]$S, [string]$kind)
    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [single]([Math]::Max(1, [int]($S * 0.06)))
    $w = [single]($S - 2 * $pad)
    $radius = [single]($S * 0.22)
    $rectF = New-Object System.Drawing.RectangleF($pad, $pad, $w, $w)

    # Shadow (large sizes)
    if ($S -ge 48) {
        $shOff = [single]($S * 0.02)
        $shPath = New-RoundedPath ([single]($pad)) ([single]($pad + $shOff)) $w $w $radius
        $shBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(70, 0, 0, 0))
        $g.FillPath($shBrush, $shPath)
        $shPath.Dispose(); $shBrush.Dispose()
    }

    # Badge gradient
    if ($kind -eq 'debug') {
        $c1 = [System.Drawing.Color]::FromArgb(255, 245, 176, 65)
        $c2 = [System.Drawing.Color]::FromArgb(255, 211, 84, 0)
    } else {
        $c1 = [System.Drawing.Color]::FromArgb(255, 88, 214, 141)
        $c2 = [System.Drawing.Color]::FromArgb(255, 30, 132, 73)
    }
    $path = New-RoundedPath $pad $pad $w $w $radius
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rectF, $c1, $c2, 90)
    $g.FillPath($brush, $path)

    # Top glossy highlight
    $oldClip = $g.Clip
    $g.SetClip($path)
    $hi = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.RectangleF($pad, $pad, $w, [single]($w * 0.5))),
        [System.Drawing.Color]::FromArgb(70, 255, 255, 255),
        [System.Drawing.Color]::FromArgb(0, 255, 255, 255), 90)
    $g.FillRectangle($hi, $pad, $pad, $w, [single]($w * 0.5))
    $hi.Dispose()
    $g.Clip = $oldClip

    # Border (medium+ sizes)
    if ($S -ge 32) {
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(60, 0, 0, 0)), ([single]([Math]::Max(1, $S * 0.012)))
        $g.DrawPath($pen, $path)
        $pen.Dispose()
    }

    Draw-Symbol $g $kind $S

    $brush.Dispose(); $path.Dispose(); $g.Dispose()
    return $bmp
}

function Save-Ico {
    param([System.Drawing.Bitmap[]]$Bitmaps, [string]$Path)
    $pngs = @()
    foreach ($b in $Bitmaps) {
        $ms = New-Object System.IO.MemoryStream
        $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs += , $ms.ToArray()
        $ms.Dispose()
    }
    $fs = [System.IO.File]::Create($Path)
    $bw = New-Object System.IO.BinaryWriter($fs)
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$Bitmaps.Count)
    $offset = 6 + 16 * $Bitmaps.Count
    for ($i = 0; $i -lt $Bitmaps.Count; $i++) {
        $wv = $Bitmaps[$i].Width; $hv = $Bitmaps[$i].Height
        $bw.Write([Byte]$(if ($wv -ge 256) { 0 } else { $wv }))
        $bw.Write([Byte]$(if ($hv -ge 256) { 0 } else { $hv }))
        $bw.Write([Byte]0); $bw.Write([Byte]0)
        $bw.Write([UInt16]1); $bw.Write([UInt16]32)
        $bw.Write([UInt32]$pngs[$i].Length)
        $bw.Write([UInt32]$offset)
        $offset += $pngs[$i].Length
    }
    foreach ($p in $pngs) { $bw.Write($p) }
    $bw.Flush(); $bw.Close(); $fs.Close()
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
foreach ($kind in @('debug', 'release')) {
    $bmps = @()
    foreach ($s in $sizes) { $bmps += New-IconBitmap $s $kind }
    $icoPath = Join-Path $outDir "rig-testing-$kind.ico"
    Save-Ico $bmps $icoPath
    # 256 preview
    ($bmps | Where-Object { $_.Width -eq 256 })[0].Save((Join-Path $outDir "preview-$kind.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    foreach ($b in $bmps) { $b.Dispose() }
    Write-Host "Cree: $icoPath"
}

# Combined preview strip (256 + 64 + 32) for both
$strip = New-Object System.Drawing.Bitmap(720, 320, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$sg = [System.Drawing.Graphics]::FromImage($strip)
$sg.SmoothingMode = 'AntiAlias'; $sg.InterpolationMode = 'HighQualityBicubic'
$sg.Clear([System.Drawing.Color]::FromArgb(255, 240, 240, 245))
$y = 20
foreach ($kind in @('debug', 'release')) {
    $b256 = New-IconBitmap 256 $kind
    $b64 = New-IconBitmap 64 $kind
    $b32 = New-IconBitmap 32 $kind
    $x = 20
    $sg.DrawImage($b256, $x, $y, 128, 128); $x += 150
    $sg.DrawImage($b64, $x, ($y + 32), 64, 64); $x += 90
    $sg.DrawImage($b32, $x, ($y + 48), 32, 32)
    $b256.Dispose(); $b64.Dispose(); $b32.Dispose()
    $y += 150
}
$sg.Dispose()
$strip.Save((Join-Path $outDir "preview-both.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$strip.Dispose()
Write-Host "Preview: $(Join-Path $outDir 'preview-both.png')"
