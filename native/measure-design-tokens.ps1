# Computes WCAG contrast for the live Theme.xaml token set and renders a
# palette/contrast plate. Not a screenshot: every pixel is drawn from the
# measured values.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Get-Linear([double]$c) { if ($c -le 0.03928) { return $c / 12.92 } return [Math]::Pow(($c + 0.055) / 1.055, 2.4) }
function Get-Luminance([int]$r, [int]$g, [int]$b) {
    0.2126 * (Get-Linear ($r / 255.0)) + 0.7152 * (Get-Linear ($g / 255.0)) + 0.0722 * (Get-Linear ($b / 255.0))
}
function Get-Contrast([string]$a, [string]$b) {
    $ar = [Convert]::ToInt32($a.Substring(1, 2), 16); $ag = [Convert]::ToInt32($a.Substring(3, 2), 16); $ab = [Convert]::ToInt32($a.Substring(5, 2), 16)
    $br = [Convert]::ToInt32($b.Substring(1, 2), 16); $bg = [Convert]::ToInt32($b.Substring(3, 2), 16); $bb = [Convert]::ToInt32($b.Substring(5, 2), 16)
    $l1 = Get-Luminance $ar $ag $ab; $l2 = Get-Luminance $br $bg $bb
    if ($l1 -lt $l2) { $t = $l1; $l1 = $l2; $l2 = $t }
    return [Math]::Round(($l1 + 0.05) / ($l2 + 0.05), 2)
}
function To-Hex([int]$r, [int]$g, [int]$b) { return '#{0:X2}{1:X2}{2:X2}' -f $r, $g, $b }

# The live tokens, read from native\Theme.xaml.
$tokens = [ordered]@{
    bg          = '#101217'
    surface     = '#171B23'
    panel       = '#1D222C'
    stroke      = '#303847'
    secondary   = '#A9B5C9'
    foreground  = '#F2F5FA'
    accent      = '#9CE7BB'
}
$accentInk = '#0E1A12'   # text placed on the accent fill
# The danger fills are the ones the app already uses on card buttons, not invented
# shades: #7A2B2B for Delete from all drives, #A83232 for Exit game + Wand.
$danger    = '#7A2B2B'
$dangerInk = '#FFFFFF'
$stopInk   = '#FFFFFF'

$pairs = @(
    @{ label = 'foreground on bg';        fg = $tokens.foreground; bg = $tokens.bg;        need = 4.5; size = 'body' }
    @{ label = 'foreground on surface';   fg = $tokens.foreground; bg = $tokens.surface;   need = 4.5; size = 'body' }
    @{ label = 'foreground on panel';     fg = $tokens.foreground; bg = $tokens.panel;     need = 4.5; size = 'body' }
    @{ label = 'secondary on bg';         fg = $tokens.secondary;  bg = $tokens.bg;        need = 4.5; size = 'body' }
    @{ label = 'secondary on surface';    fg = $tokens.secondary;  bg = $tokens.surface;   need = 4.5; size = 'body' }
    @{ label = 'accent on bg';            fg = $tokens.accent;     bg = $tokens.bg;        need = 3.0; size = 'ui' }
    @{ label = 'accent on surface';       fg = $tokens.accent;     bg = $tokens.surface;   need = 3.0; size = 'ui' }
    @{ label = 'accent on panel';         fg = $tokens.accent;     bg = $tokens.panel;     need = 3.0; size = 'ui' }
    @{ label = 'accentInk on accent';     fg = $accentInk;         bg = $tokens.accent;    need = 4.5; size = 'button' }
    @{ label = 'dangerInk on danger';     fg = $dangerInk;         bg = $danger;           need = 4.5; size = 'button' }
    @{ label = 'stopInk on stop';         fg = $stopInk;           bg = '#A83232';         need = 4.5; size = 'button' }
    @{ label = 'stroke on bg';            fg = $tokens.stroke;     bg = $tokens.bg;        need = 1.5; size = 'border' }
)

$rows = foreach ($p in $pairs) {
    $c = Get-Contrast $p.fg $p.bg
    [PSCustomObject]@{
        Pair = $p.label; Ratio = $c; Need = $p.need; Size = $p.size
        Result = if ($c -ge $p.need) { 'PASS' } else { 'FAIL' }
    }
}
$rows | Format-Table -AutoSize | Out-String | Write-Output
$fails = @($rows | Where-Object { $_.Result -eq 'FAIL' })
"FAIL count: $($fails.Count)"

# ---------------------------------------------------------------- render plate
$w = 1500; $h = 900
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
$g.Clear([System.Drawing.ColorTranslator]::FromHtml($tokens.bg))

function Font([float]$s, [System.Drawing.FontStyle]$st = [System.Drawing.FontStyle]::Regular) {
    New-Object System.Drawing.Font('Segoe UI', $s, $st, [System.Drawing.GraphicsUnit]::Pixel)
}
$title = Font 34 ([System.Drawing.FontStyle]::Bold)
$sub = Font 17
$rowF = Font 17
$mono = New-Object System.Drawing.Font('Consolas', 16, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)

$g.DrawString('Game Library Manager - design tokens', $title, (New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($tokens.foreground))), 48, 40)
$g.DrawString('Precision & Density - dark default. Every value below is measured, not asserted.', $sub, (New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($tokens.secondary))), 48, 84)

# swatch row
$x = 48; $y = 130
foreach ($kv in $tokens.GetEnumerator()) {
    $fill = [System.Drawing.ColorTranslator]::FromHtml($kv.Value)
    $r = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 16
    $r.AddArc($x, $y, $d, $d, 180, 90); $r.AddArc($x + 110, $y, $d, $d, 270, 90)
    $r.AddArc($x + 110, $y + 74, $d, $d, 0, 90); $r.AddArc($x, $y + 74, $d, $d, 90, 90); $r.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush($fill)), $r)
    $g.DrawPath((New-Object System.Drawing.Pen([System.Drawing.ColorTranslator]::FromHtml($tokens.stroke), 1.2)), $r)
    $g.DrawString($kv.Key, $rowF, (New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($tokens.foreground))), $x, ($y + 84))
    $g.DrawString($kv.Value, $mono, (New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($tokens.secondary))), $x, ($y + 106))
    $x += 230
}

# contrast table
$ty = 290
$g.DrawString('Measured contrast (WCAG 2.1)', $title, (New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($tokens.foreground))), 48, $ty)
$ty += 46
$cols = @(48, 470, 600, 700, 800)
$g.DrawString('PAIR', $mono, (New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($tokens.secondary))), $cols[0], $ty)
$g.DrawString('RATIO', $mono, (New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($tokens.secondary))), $cols[1], $ty)
$g.DrawString('NEED', $mono, (New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($tokens.secondary))), $cols[2], $ty)
$g.DrawString('SIZE', $mono, (New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($tokens.secondary))), $cols[3], $ty)
$g.DrawString('RESULT', $mono, (New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($tokens.secondary))), $cols[4], $ty)
$ty += 26
foreach ($r in $rows) {
    $colour = if ($r.Result -eq 'PASS') { $tokens.accent } else { $danger }
    $g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($colour))), $cols[4] - 14, ($ty + 3), 8, 14)
    $g.DrawString($r.Pair, $rowF, (New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($tokens.foreground))), $cols[0], $ty)
    $g.DrawString(('{0:N2}' -f $r.Ratio), $rowF, (New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($tokens.foreground))), $cols[1], $ty)
    $g.DrawString(('{0:N1}' -f $r.Need), $rowF, (New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($tokens.secondary))), $cols[2], $ty)
    $g.DrawString($r.Size, $rowF, (New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($tokens.secondary))), $cols[3], $ty)
    $g.DrawString($r.Result, $rowF, (New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($colour))), $cols[4], $ty)
    $ty += 28
}

$outDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'docs\images'
[IO.Directory]::CreateDirectory($outDir) | Out-Null
$out = Join-Path $outDir 'design-tokens-2026-10.png'
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
"wrote $out"
