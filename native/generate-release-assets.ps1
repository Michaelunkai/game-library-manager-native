# Generates the release artwork for the six new library capabilities:
# deletion, zero-lag filtering, tag hygiene, completion accuracy, live
# backup/restore, and the per-game speed bar. Pure vector drawing (gradients,
# rounded cards, glyphs, meters) so the output is crisp and reproducible and
# never a screen capture.
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
function New-Pen([int]$a, [int]$r, [int]$g, [int]$b, [float]$w = 1) { New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb($a,$r,$g,$b), $w) }
function New-Font([float]$s, [System.Drawing.FontStyle]$st = [System.Drawing.FontStyle]::Regular) {
    New-Object System.Drawing.Font('Segoe UI', $s, $st, [System.Drawing.GraphicsUnit]::Pixel)
}
function New-Canvas([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
    return @($bmp, $g)
}
function Fill-VerticalGradient($g, [System.Drawing.Rectangle]$rect, [int]$topA, [int]$topR, [int]$topG, [int]$topB, [int]$botA, [int]$botR, [int]$botG, [int]$botB) {
    $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, [System.Drawing.Color]::FromArgb($topA,$topR,$topG,$topB), [System.Drawing.Color]::FromArgb($botA,$botR,$botG,$botB), 90.0)
    $g.FillRectangle($br, $rect)
    $br.Dispose()
}
function Draw-GlowDot($g, [float]$x, [float]$y, [float]$r, [int]$a, [int]$gr, [int]$gg, [int]$gb) {
    for ($i = 6; $i -ge 1; $i--) {
        $rr = $r * (1 + $i * 0.55)
        $alpha = [int]($a / ($i * 1.7))
        if ($alpha -lt 1) { $alpha = 1 }
        $b = New-Brush $alpha $gr $gg $gb
        $g.FillEllipse($b, $x - $rr, $y - $rr, $rr * 2, $rr * 2)
        $b.Dispose()
    }
}
function Draw-Card($g, [float]$x, [float]$y, [float]$w, [float]$h, [int]$a = 22, [int]$r = 34, [int]$g2 = 40, [int]$b = 52) {
    $p = New-RoundedPath $x $y $w $h 14
    $br = New-Brush $a $r $g2 $b
    $g.FillPath($br, $p)
    $pen = New-Pen 70 120 150 190 1.4
    $g.DrawPath($pen, $p)
    $br.Dispose(); $pen.Dispose(); $p.Dispose()
}

# ---------------------------------------------------------------- hero banner
function Write-Hero([string]$path) {
    $w = 1600; $h = 620
    $c = New-Canvas $w $h
    $bmp = $c[0]; $g = $c[1]
    Fill-VerticalGradient $g (New-Object System.Drawing.Rectangle(0, 0, $w, $h)) 255 14 17 24 255 9 11 16
    for ($i = 0; $i -lt 5; $i++) {
        $cx = 180 + $i * 320; $cy = 120 + ($i % 3) * 70
        Draw-GlowDot $g $cx $cy 130 16 96 165 250
    }
    $f1 = New-Font 62 ([System.Drawing.FontStyle]::Bold)
    $f2 = New-Font 27
    $f3 = New-Font 20
    $w1 = New-Brush 255 236 242 250; $w2 = New-Brush 205 150 165 185; $w3 = New-Brush 255 122 199 255
    $g.DrawString('Game Library Manager', $f1, $w1, 90, 150)
    $g.DrawString('Native Windows build - six new capabilities, every one covered by tests', $f2, $w2, 90, 232)
    $g.DrawString('F:\study\repos\game-library-manager-native\native', $f3, $w3, 90, 274)

    $labels = @(
        'Delete from all drives', 'Zero-lag search', 'Tag hygiene',
        'Accurate completion', 'Live backup + restore', 'Per-game speed bar'
    )
    $x = 90; $y = 336
    $fb = New-Font 19
    foreach ($l in $labels) {
        $tw = [float]$g.MeasureString($l, $fb).Width
        Draw-Card $g $x $y ($tw + 34) 44 30 40 60 84
        $gb = New-Brush 235 225 235 245
        $g.DrawString($l, $fb, $gb, ($x + 17), ($y + 12))
        $x += $tw + 34 + 14
        if ($x -gt 1250) { $x = 90; $y += 58 }
    }

    # accuracy meter motif
    $mx = 90; $my = 476; $mw = 1420; $mh = 22
    Draw-Card $g ($mx - 18) ($my - 30) ($mw + 36) 118 16 28 40 56
    $g.DrawString('Completion accuracy - 0% to 100% with hours remaining', $fb, (New-Brush 220 170 185 205), $mx, $my - 20)
    $track = New-RoundedPath $mx ($my + 14) $mw $mh 11
    $tb = New-Brush 255 48 56 72
    $g.FillPath($tb, $track)
    $done = [float]($mw * 0.86)
    $prog = New-RoundedPath $mx ($my + 14) $done $mh 11
    $pb = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.Rectangle(0, 0, [int]$done, [int]$mh)), [System.Drawing.Color]::FromArgb(255, 122, 199, 255), [System.Drawing.Color]::FromArgb(255, 88, 214, 141), 0.0)
    $g.FillPath($pb, $prog)
    $g.DrawString('86%', $fb, (New-Brush 255 220 245 235), ($mx + $done + 16), ($my + 14))
    $g.DrawString('about 3.1 h left', $fb, (New-Brush 190 255 210 180), ($mx + $done + 78), ($my + 14))

    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose(); $g.Dispose()
}

# ------------------------------------------------------------- feature strip
function Write-Features([string]$path) {
    $w = 1600; $h = 940
    $c = New-Canvas $w $h
    $bmp = $c[0]; $g = $c[1]
    Fill-VerticalGradient $g (New-Object System.Drawing.Rectangle(0, 0, $w, $h)) 255 13 16 23 255 8 10 15
    $fh = New-Font 40 ([System.Drawing.FontStyle]::Bold)
    $fsub = New-Font 20
    $g.DrawString('Six capabilities, eight parallel slots, one integrated build', $fh, (New-Brush 255 235 241 250), 80, 66)
    $g.DrawString('Each capability is a dedicated engine with its own self-test file.', $fsub, (New-Brush 170 145 160 180), 80, 118)

    $cards = @(
        @{ t = 'Delete from all drives'; d = 'Containment-checked plan over every drive and leftover; refuses roots, reparse points and escapes.'; k = 'slot 1' },
        @{ t = 'Zero-lag filtering'; d = 'Prebuilt inverted index, cached queries, list-swap categories - no per-keystroke disk I/O.'; k = 'slot 2' },
        @{ t = 'Tag hygiene'; d = 'Curated non-game tags moved only into the seven hidden categories, never a visible one.'; k = 'slot 3' },
        @{ t = 'Accurate completion'; d = 'Percent out of 100 plus hours left, with explicit confidence and no fabricated totals.'; k = 'slot 4' },
        @{ t = 'Live backup + restore'; d = 'Quantised snapshot while the game runs; verify-then-quarantine restore with rollback.'; k = 'slots 5-6' },
        @{ t = 'Per-game speed bar'; d = 'Scaled-clock hook with F1 +0.5, F2 -0.5, F3 normal, gated on a running game.'; k = 'slots 7-8' }
    )
    $cw = 460; $ch = 250; $gap = 30
    foreach ($card in $cards) {
        $i = [array]::IndexOf($cards, $card)
        $col = $i % 3; $row = [math]::Floor($i / 3)
        $x = 80 + $col * ($cw + $gap); $y = 180 + $row * ($ch + $gap)
        Draw-Card $g $x $y $cw $ch 24 32 42 56
        $acc = New-Brush 255 122 199 255
        $g.FillRectangle($acc, $x, $y, 5, $ch)
        $ft = New-Font 25 ([System.Drawing.FontStyle]::Bold)
        $fd = New-Font 17
        $fk = New-Font 15
        $g.DrawString($card.t, $ft, (New-Brush 255 236 242 250), ($x + 26), ($y + 24))
        $g.DrawString($card.k, $fk, (New-Brush 255 122 199 255), ($x + 26), ($y + 60))
        $words = $card.d -split ' '
        $line = ''; $ly = $y + 96
        foreach ($wd in $words) {
            $try = if ($line) { "$line $wd" } else { $wd }
            if ([float]$g.MeasureString($try, $fd).Width -gt ($cw - 52)) {
                $g.DrawString($line, $fd, (New-Brush 195 160 172 190), ($x + 26), $ly); $ly += 24; $line = $wd
            } else { $line = $try }
        }
        if ($line) { $g.DrawString($line, $fd, (New-Brush 195 160 172 190), ($x + 26), $ly) }
    }
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose(); $g.Dispose()
}

# ------------------------------------------------------------ speed bar card
function Write-Speed([string]$path) {
    $w = 1400; $h = 620
    $c = New-Canvas $w $h
    $bmp = $c[0]; $g = $c[1]
    Fill-VerticalGradient $g (New-Object System.Drawing.Rectangle(0, 0, $w, $h)) 255 15 18 26 255 9 11 16
    $fh = New-Font 38 ([System.Drawing.FontStyle]::Bold)
    $fl = New-Font 19
    $g.DrawString('Per-game speed bar', $fh, (New-Brush 255 236 242 250), 70, 58)
    $g.DrawString('Applies only while a game is actually running. F3 restores exact normal speed.', $fl, (New-Brush 175 150 165 185), 70, 108)

    $steps = @(0.1, 0.25, 0.5, 0.75, 1.0, 1.5, 2.0, 3.0, 4.0, 6.0, 8.0, 10.0, 16.0)
    $tx = 90; $ty = 200; $tw = 1220; $th = 26
    $track = New-RoundedPath $tx $ty $tw $th 13
    $g.FillPath((New-Brush 255 46 54 70), $track)
    $idx = 7
    for ($i = 0; $i -lt $steps.Count; $i++) {
        $px = $tx + ($tw * $i / ($steps.Count - 1))
        $norm = ($i -eq 4)
        $col = if ($i -lt 4) { @(120, 200, 255) } elseif ($i -gt 4) { @(255, 168, 120) } else { @(120, 255, 180) }
        $b = New-Brush ($(if ($i -eq $idx) { 255 } else { 110 })) $col[0] $col[1] $col[2]
        $g.FillEllipse($b, ($px - $(if ($i -eq $idx) { 13 } else { 9 })), ($ty + $th / 2 - $(if ($i -eq $idx) { 13 } else { 9 })), $(if ($i -eq $idx) { 26 } else { 18 }), $(if ($i -eq $idx) { 26 } else { 18 }))
        $fb = New-Font 15
        $lb = New-Brush $(if ($i -eq $idx) { 255 } else { 150 }) $col[0] $col[1] $col[2]
        $txt = "{0}x" -f $steps[$i]
        $tw2 = [float]$g.MeasureString($txt, $fb).Width
        $g.DrawString($txt, $fb, $lb, ($px - $tw2 / 2), ($ty + $th + 18))
    }
    $sel = $tx + ($tw * $idx / ($steps.Count - 1))
    Draw-GlowDot $g $sel ($ty + $th / 2) 26 60 255 168 120

    # hotkey legend
    $keys = @(
        @{ k = 'F1'; v = '+0.5x'; d = '1.0x becomes exactly 1.5x' },
        @{ k = 'F2'; v = '-0.5x'; d = '1.0x becomes exactly 0.5x' },
        @{ k = 'F3'; v = 'normal'; d = 'always exactly 1.0x' }
    )
    $x = 90
    foreach ($kd in $keys) {
        Draw-Card $g $x 330 380 130 28 36 50 66
        $fk = New-Font 34 ([System.Drawing.FontStyle]::Bold)
        $fv = New-Font 24
        $fd = New-Font 16
        $g.DrawString($kd.k, $fk, (New-Brush 255 122 199 255), ($x + 26), 352)
        $g.DrawString($kd.v, $fv, (New-Brush 255 235 241 250), ($x + 118), 360)
        $g.DrawString($kd.d, $fd, (New-Brush 180 155 170 190), ($x + 26), 412)
        $x += 400
    }
    $fnote = New-Font 16
    $g.DrawString('Offline single-player titles launched from this library. The engine refuses any process it cannot positively identify.', $fnote, (New-Brush 150 130 145 165), 90, 500)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose(); $g.Dispose()
}

$targets = @(
    @{ f = 'hero-2026-10.png'; s = 'hero' },
    @{ f = 'features-2026-10.png'; s = 'features' },
    @{ f = 'speed-bar-2026-10.png'; s = 'speed' }
)
foreach ($t in $targets) {
    $p = Join-Path $outDir $t.f
    switch ($t.s) {
        'hero' { Write-Hero $p }
        'features' { Write-Features $p }
        'speed' { Write-Speed $p }
    }
    "wrote $p"
}
