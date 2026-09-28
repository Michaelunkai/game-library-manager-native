# Generates the README artwork: hero banner, feature cards, and framed app
# views. Pure vector-ish drawing (gradients, rounded cards, glyphs) so the
# images look crisp and professional without relying on screen captures.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$outDir = Join-Path $PSScriptRoot '..\docs\images'
[IO.Directory]::CreateDirectory($outDir) | Out-Null

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-Brush([int]$a, [int]$r, [int]$g, [int]$b) { New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb($a,$r,$g,$b)) }
function New-Pen([int]$a, [int]$r, [int]$g, [int]$b, [float]$w=1) { New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb($a,$r,$g,$b), $w) }

function Draw-SoftShadow($g, [float]$x, [float]$y, [float]$w, [float]$h, [float]$r, [int]$a) {
    for ($i = 1; $i -le 5; $i++) {
        $alpha = [int]($a * (1 - ($i - 1) / 6.0))
        $off = 3 + $i * 1.2
        $path = New-RoundedPath $x ($y + $off) $w $h $r
        $b = New-Brush $alpha 0 0 0
        $g.FillPath($b, $path)
        $b.Dispose(); $path.Dispose()
    }
}

function Draw-PlayGlyph($g, [float]$cx, [float]$cy, [float]$size, [int]$r, [int]$gr, [int]$gb) {
    $b = New-Brush 255 $r $gr $gb
    $pts = @(
        ([System.Drawing.PointF]::new($cx - $size*0.38, $cy - $size*0.42)),
        ([System.Drawing.PointF]::new($cx - $size*0.38, $cy + $size*0.42)),
        ([System.Drawing.PointF]::new($cx + $size*0.5, $cy))
    )
    $g.FillPolygon($b, $pts)
    $b.Dispose()
}

function Draw-DownloadGlyph($g, $x, $y, $s, $r, $gr, $gb) {
    $x = [float]$x; $y = [float]$y; $s = [float]$s
    $pen = New-Pen 255 $r $gr $gb 4
    $g.DrawLine($pen, $x + $s/2, $y, $x + $s/2, $y + $s*0.62)
    $g.DrawLine($pen, $x + $s*0.22, $y + $s*0.36, $x + $s/2, $y + $s*0.66)
    $g.DrawLine($pen, $x + $s*0.78, $y + $s*0.36, $x + $s/2, $y + $s*0.66)
    $g.DrawLine($pen, $x + $s*0.15, $y + $s*0.8, $x + $s*0.85, $y + $s*0.8)
    $pen.Dispose()
}

function Draw-BoltGlyph($g, $cx, $cy, $s, $r, $gr, $gb) {
    $cx = [float]$cx; $cy = [float]$cy; $s = [float]$s
    $b = New-Brush 255 $r $gr $gb
    $pts = New-Object 'System.Drawing.PointF[]' 6
    $pts[0] = [System.Drawing.PointF]::new($cx + $s*0.14, $cy - $s*0.5)
    $pts[1] = [System.Drawing.PointF]::new($cx - $s*0.28, $cy + $s*0.12)
    $pts[2] = [System.Drawing.PointF]::new($cx - $s*0.02, $cy + $s*0.14)
    $pts[3] = [System.Drawing.PointF]::new($cx - $s*0.14, $cy + $s*0.5)
    $pts[4] = [System.Drawing.PointF]::new($cx + $s*0.28, $cy - $s*0.12)
    $pts[5] = [System.Drawing.PointF]::new($cx + $s*0.02, $cy - $s*0.14)
    $g.FillPolygon($b, $pts)
    $b.Dispose()
}

function Draw-ShieldGlyph($g, $cx, $cy, $s, $r, $gr, $gb) {
    $cx = [float]$cx; $cy = [float]$cy; $s = [float]$s
    $b = New-Brush 255 $r $gr $gb
    $pts = New-Object 'System.Drawing.PointF[]' 6
    $pts[0] = [System.Drawing.PointF]::new($cx, $cy - $s*0.5)
    $pts[1] = [System.Drawing.PointF]::new($cx + $s*0.42, $cy - $s*0.3)
    $pts[2] = [System.Drawing.PointF]::new($cx + $s*0.42, $cy + $s*0.02)
    $pts[3] = [System.Drawing.PointF]::new($cx, $cy + $s*0.42)
    $pts[4] = [System.Drawing.PointF]::new($cx - $s*0.42, $cy + $s*0.02)
    $pts[5] = [System.Drawing.PointF]::new($cx - $s*0.42, $cy - $s*0.3)
    $g.FillPolygon($b, $pts)
    $b.Dispose()
}

function Save-Png($bmp, [string]$name) {
    $path = Join-Path $outDir $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output ("wrote " + $path)
}

function New-Bmp([int]$w, [int]$h) {
    $b = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    return @($b, $g)
}

# ---------------------------------------------------------------------------
# App icon (rounded square with a controller mark)
# ---------------------------------------------------------------------------
$art = New-Bmp 512 512
$bmp = $art[0]; $g = $art[1]
$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point(0,0)), (New-Object System.Drawing.Point(512,512)),
    [System.Drawing.Color]::FromArgb(255,16,36,50), [System.Drawing.Color]::FromArgb(255,6,22,30))
$g.FillRectangle($bg, 0, 0, 512, 512)
$corner = New-RoundedPath 24 24 464 464 96
$g.FillPath($bg, $corner)
# controller body
$cbody = New-Brush 255 45 212 167
$body = New-RoundedPath 96 208 320 116 34
$g.FillPath($cbody, $body)
$cbody.Dispose()
# grips
foreach ($side in @(96, 336)) {
    $grip = New-Brush 255 45 212 167
    $gp = New-RoundedPath $side 178 80 108 48 22
    $g.FillPath($grip, $gp); $grip.Dispose()
}
# dpad + buttons
$dark = New-Brush 255 6 22 30
$g.FillRectangle($dark, 180, 252, 16, 40)
$g.FillRectangle($dark, 172, 260, 32, 16)
$g.FillEllipse($dark, 306, 244, 20, 20)
$g.FillEllipse($dark, 342, 266, 20, 20)
$g.FillEllipse($dark, 306, 292, 20, 20)
$g.FillEllipse($dark, 272, 266, 20, 20)
$dark.Dispose()
$g.Dispose()
Save-Png $bmp 'icon.png'

# ---------------------------------------------------------------------------
# Hero banner
# ---------------------------------------------------------------------------
$W = 1280; $H = 420
$art = New-Bmp $W $H
$bmp = $art[0]; $g = $art[1]
# background gradient
$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point(0,0)), (New-Object System.Drawing.Point($W,$H)),
    [System.Drawing.Color]::FromArgb(255,15,28,42), [System.Drawing.Color]::FromArgb(255,8,20,30))
$g.FillRectangle($bg, 0, 0, $W, $H)
# subtle radial glow behind title area
$glowR = 460
$glow = New-Object System.Drawing.Drawing2D.PathGradientBrush((New-RoundedPath ($W/2-$glowR/2-200) -120 $glowR $glowR 200))
$glow.CenterColor = [System.Drawing.Color]::FromArgb(70,45,212,167)
$glow.SurroundColors = @([System.Drawing.Color]::FromArgb(0,8,20,30))
$g.FillRectangle($glow, 0, 0, $W, $H)
$glow.Dispose(); $bg.Dispose()
# faint grid dots
$dot = New-Brush 22 255 255 255
for ($x = 40; $x -lt $W; $x += 40) { for ($y = 40; $y -lt $H; $y += 40) { $g.FillEllipse($dot, $x, $y, 2, 2) } }
$dot.Dispose()
# tilted cover-card stack on the right
$covers = @(
    @(950, 30, 0, 255,210,110, 'A'),
    @(1030, 120, -6, 255,255,255, 'S'),
    @(1110, 60, 7, 80,200,255, 'W')
)
foreach ($c in $covers) {
    $cx = [float]$c[0]; $cy = [float]$c[1]; $rot = [int]$c[2]
    $r1 = [int]$c[3]; $g1 = [int]$c[4]; $b1 = [int]$c[5]; $letter = $c[6]
    $cw = 132; $ch = 176
    Draw-SoftShadow $g ($cx-8) $cy $cw $ch 14 90
    $save = $g.Save()
    $g.TranslateTransform($cx + $cw/2, $cy + $ch/2)
    $g.RotateTransform($rot)
    $g.TranslateTransform(-($cx + $cw/2), -($cy + $ch/2))
    $card = New-RoundedPath $cx $cy $cw $ch 14
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0,0)), (New-Object System.Drawing.Point($cw,$ch)),
        [System.Drawing.Color]::FromArgb(255,$r1,$g1,$b1), [System.Drawing.Color]::FromArgb(255,[int]($r1*0.45),[int]($g1*0.45),[int]($b1*0.45)))
    $g.FillPath($grad, $card)
    $grad.Dispose()
    $font = New-Object System.Drawing.Font('Segoe UI', 46, [System.Drawing.FontStyle]::Bold)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $tb = New-Brush 90 0 0 0
    $rect = New-Object System.Drawing.RectangleF($cx, $cy, $cw, $ch)
    $g.DrawString($letter, $font, $tb, $rect, $sf)
    $tb.Dispose(); $font.Dispose(); $sf.Dispose()
    $card.Dispose()
    $g.Restore($save)
}
# title block
$titleFont = New-Object System.Drawing.Font('Segoe UI', 46, [System.Drawing.FontStyle]::Bold)
$titleBrush = New-Brush 255 232 240 246
$sf = New-Object System.Drawing.StringFormat
$g.DrawString('Game Library Manager', $titleFont, $titleBrush, 64, 118, $sf)
$titleFont.Dispose(); $titleBrush.Dispose()
$subFont = New-Object System.Drawing.Font('Segoe UI', 18)
$subBrush = New-Brush 255 150 175 195
$g.DrawString('Install, organize, and play your Docker-backed game library on Windows.', $subFont, $subBrush, 64, 188, $sf)
$subFont.Dispose(); $subBrush.Dispose()
# accent line
$accent = New-Brush 255 45 212 167
$g.FillRectangle($accent, 64, 252, 96, 6)
$accent.Dispose()
# tag pills
$tags = @('Windows 10/11', '.NET 10', 'Docker', 'Self-contained')
$x = 64
foreach ($t in $tags) {
    $pf = New-Object System.Drawing.Font('Segoe UI', 13, [System.Drawing.FontStyle]::Bold)
    $sz = $g.MeasureString($t, $pf)
    $pw = $sz.Width + 26; $ph = $sz.Height + 10
    $pill = New-RoundedPath $x 276 $pw $ph 14
    $pb = New-Brush 26 255 255 255
    $g.FillPath($pb, $pill)
    $pb.Dispose()
    $ptb = New-Brush 255 200 220 235
    $pf2 = New-Object System.Drawing.Font('Segoe UI', 12)
    $pf2rect = New-Object System.Drawing.RectangleF($x, 280, $pw, $ph)
    $sf2 = New-Object System.Drawing.StringFormat
    $sf2.Alignment = [System.Drawing.StringAlignment]::Center
    $g.DrawString($t, $pf2, $ptb, $pf2rect, $sf2)
    $x += $pw + 12
    $pf.Dispose(); $pf2.Dispose(); $sf2.Dispose(); $ptb.Dispose()
}
$sf.Dispose()
$g.Dispose()
Save-Png $bmp 'hero.png'

# ---------------------------------------------------------------------------
# Feature cards
# ---------------------------------------------------------------------------
$W = 1280; $H = 360
$art = New-Bmp $W $H
$bmp = $art[0]; $g = $art[1]
$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point(0,0)), (New-Object System.Drawing.Point($W,$H)),
    [System.Drawing.Color]::FromArgb(255,14,26,40), [System.Drawing.Color]::FromArgb(255,8,18,28))
$g.FillRectangle($bg, 0, 0, $W, $H)
$bg.Dispose()
$features = @(
    @('Durable installs', 'Live byte-for-byte progress as games download and extract, with automatic resume.', 'download', 45,212,167),
    @('Play with Wand', 'One click opens Wand and attaches the right trainer, even while another game runs.', 'bolt', 96,170,255),
    @('Trusted metadata', 'Artwork, completion hours, and disk requirements from verified sources.', 'shield', 245,190,90)
)
$cardW = 396; $cardH = 252; $gap = 28; $left = 30
foreach ($f in $features) {
    $title = $f[0]; $desc = $f[1]; $icon = $f[2]; $icR = $f[3]; $icG = $f[4]; $icB = $f[5]
    Draw-SoftShadow $g $left 40 $cardW $cardH 22 70
    $card = New-RoundedPath $left 40 $cardW $cardH 22
    $cg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0,0)), (New-Object System.Drawing.Point(0,$cardH)),
        [System.Drawing.Color]::FromArgb(255,23,40,58), [System.Drawing.Color]::FromArgb(255,15,29,44))
    $g.FillPath($cg, $card)
    $cg.Dispose()
    # icon circle
    $icx = $left + 36; $icy = 76
    $ic = New-RoundedPath $icx $icy 76 76 38
    $ig = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0,0)), (New-Object System.Drawing.Point(76,76)),
        [System.Drawing.Color]::FromArgb(255,$icR,$icG,$icB), [System.Drawing.Color]::FromArgb(255,[int]($icR*0.5),[int]($icG*0.5),[int]($icB*0.5)))
    $g.FillPath($ig, $ic)
    $ig.Dispose(); $ic.Dispose()
    if ($icon -eq 'download') { Draw-DownloadGlyph $g ($icx+24) ($icy+24) 28 6 22 30 }
    elseif ($icon -eq 'bolt') { Draw-BoltGlyph $g ($icx+38) ($icy+38) 34 6 22 30 }
    else { Draw-ShieldGlyph $g ($icx+38) ($icy+38) 36 6 22 30 }
    # title + description
    $tf = New-Object System.Drawing.Font('Segoe UI', 20, [System.Drawing.FontStyle]::Bold)
    $tbr = New-Brush 255 226 236 244
    $tr = New-Object System.Drawing.RectangleF(($left+128), 78, ($cardW-150), 40)
    $g.DrawString($title, $tf, $tbr, $tr)
    $tf.Dispose(); $tbr.Dispose()
    $df = New-Object System.Drawing.Font('Segoe UI', 13)
    $dbr = New-Brush 255 158 180 198
    $dr = New-Object System.Drawing.RectangleF(($left+36), 176, ($cardW-72), 100)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Near
    $g.DrawString($desc, $df, $dbr, $dr, $sf)
    $df.Dispose(); $dbr.Dispose(); $sf.Dispose()
    $card.Dispose()
    $left += $cardW + $gap
}
$g.Dispose()
Save-Png $bmp 'features.png'

# ---------------------------------------------------------------------------
# Framed app views: wrap the captured screens in a professional window chrome
# ---------------------------------------------------------------------------
function Frame-View([string]$srcName, [string]$outName, [string]$title) {
    $src = Join-Path $outDir $srcName
    if (-not (Test-Path -LiteralPath $src)) { Write-Output ("skip " + $srcName); return }
    $shot = [System.Drawing.Bitmap]::FromFile($src)
    $pad = 42; $bar = 62
    $w = $shot.Width + $pad * 2; $h = $shot.Height + $pad * 2 + $bar
    $art = New-Bmp $w $h
    $bmp = $art[0]; $g = $art[1]
    # backdrop
    $back = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0,0)), (New-Object System.Drawing.Point($w,$h)),
        [System.Drawing.Color]::FromArgb(255,10,20,30), [System.Drawing.Color]::FromArgb(255,16,32,48))
    $g.FillRectangle($back, 0, 0, $w, $h)
    $back.Dispose()
    # frame shadow
    Draw-SoftShadow $g 26 30 ($w-52) ($h-60) 20 110
    # window frame
    $frame = New-RoundedPath 26 30 ($w-52) ($h-60) 20
    $fg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0,0)), (New-Object System.Drawing.Point(0,$h)),
        [System.Drawing.Color]::FromArgb(255,32,52,72), [System.Drawing.Color]::FromArgb(255,20,36,54))
    $g.FillPath($fg, $frame)
    $fg.Dispose()
    # title bar
    $tbTop = New-RoundedPath 26 30 ($w-52) 62 20
    $tbgrad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0,0)), (New-Object System.Drawing.Point($w,0)),
        [System.Drawing.Color]::FromArgb(255,24,42,60), [System.Drawing.Color]::FromArgb(255,18,34,50))
    $g.FillPath($tbgrad, $tbTop)
    $tbgrad.Dispose()
    foreach ($dot in @(52, 74, 96)) {
        $d = New-Brush 255 90 110 130
        $g.FillEllipse($d, $dot, 50, 14, 14)
        $d.Dispose()
    }
    $tf = New-Object System.Drawing.Font('Segoe UI', 15)
    $tb = New-Brush 255 170 190 210
    $g.DrawString($title, $tf, $tb, 124, 44)
    $tf.Dispose(); $tb.Dispose()
    # screenshot inset
    $g.DrawImage($shot, $pad, 30 + $bar, $shot.Width, $shot.Height)
    $frame.Dispose(); $tbTop.Dispose()
    $g.Dispose(); $bmp.Dispose()
    $path = Join-Path $outDir $outName
    $bmp2 = New-Object System.Drawing.Bitmap($w, $h)
    $g2 = [System.Drawing.Graphics]::FromImage($bmp2)
    $back2 = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0,0)), (New-Object System.Drawing.Point($w,$h)),
        [System.Drawing.Color]::FromArgb(255,10,20,30), [System.Drawing.Color]::FromArgb(255,16,32,48))
    $g2.FillRectangle($back2, 0, 0, $w, $h)
    $back2.Dispose()
    Draw-SoftShadow $g2 26 30 ($w-52) ($h-60) 20 110
    $frame2 = New-RoundedPath 26 30 ($w-52) ($h-60) 20
    $fg2 = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0,0)), (New-Object System.Drawing.Point(0,$h)),
        [System.Drawing.Color]::FromArgb(255,32,52,72), [System.Drawing.Color]::FromArgb(255,20,36,54))
    $g2.FillPath($fg2, $frame2)
    $fg2.Dispose()
    $tbTop2 = New-RoundedPath 26 30 ($w-52) 62 20
    $tbgrad2 = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0,0)), (New-Object System.Drawing.Point($w,0)),
        [System.Drawing.Color]::FromArgb(255,24,42,60), [System.Drawing.Color]::FromArgb(255,18,34,50))
    $g2.FillPath($tbgrad2, $tbTop2)
    $tbgrad2.Dispose()
    foreach ($dot in @(52, 74, 96)) { $d2 = New-Brush 255 90 110 130; $g2.FillEllipse($d2, $dot, 50, 14, 14); $d2.Dispose() }
    $tf2 = New-Object System.Drawing.Font('Segoe UI', 15); $tb2 = New-Brush 255 170 190 210
    $g2.DrawString($title, $tf2, $tb2, 124, 44); $tf2.Dispose(); $tb2.Dispose()
    $g2.DrawImage($shot, $pad, 30 + $bar, $shot.Width, $shot.Height)
    $frame2.Dispose(); $tbTop2.Dispose(); $g2.Dispose()
    $bmp2.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp2.Dispose(); $shot.Dispose()
    Write-Output ("wrote " + $path)
}

Frame-View 'library.png' 'library-view.png' 'Game Library Manager'
Frame-View 'search.png' 'search-view.png' 'Search results'

Write-Output 'ALL ASSETS WRITTEN'


