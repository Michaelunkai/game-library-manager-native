# Regenerable acceptance report for the density / smoothness / completion claims.
#
# Everything in the output is measured:
#   * self-test and ui-test totals come from the real JSON receipts the packaged
#     executable writes, invoked exactly as verify-reliability.ps1 invokes them.
#   * WCAG contrast comes from the token values native\Theme.xaml actually ships.
#   * The two DESIGN.md rules are checked against the real native\MainWindow.xaml
#     text, line by line, and every violation is reported rather than hidden.
#   * The PNG plate is drawn programmatically with System.Drawing. Never a capture.
#
# Unattended: no prompts, owned processes are always stopped, and the only paths
# written are under <repo>\evidence and <repo>\docs\images.

[CmdletBinding()]
param(
    [string]$ExePath = 'native\dist\GameLibrary.exe',
    [string]$EvidenceRoot = 'evidence\acceptance-density',
    [string]$PlatePath = 'docs\images\acceptance-density-2026-10.png',
    [int]$SelfTestTimeoutSeconds = 300,
    [int]$UiTestTimeoutSeconds = 480
)

$ErrorActionPreference = 'Stop'
$schemaVersion = 1

# ------------------------------------------------------------------ path safety
$repo = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$evidenceBase = [IO.Path]::GetFullPath((Join-Path $repo 'evidence')).TrimEnd('\') + '\'

function Resolve-AgainstRepo([string]$value) {
    if ([IO.Path]::IsPathRooted($value)) { return [IO.Path]::GetFullPath($value) }
    return [IO.Path]::GetFullPath((Join-Path $repo $value))
}

$ExePath = Resolve-AgainstRepo $ExePath
$EvidenceRoot = (Resolve-AgainstRepo $EvidenceRoot).TrimEnd('\')
$PlatePath = Resolve-AgainstRepo $PlatePath
if (-not ($EvidenceRoot + '\').StartsWith($evidenceBase, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'EvidenceRoot must be under this repository evidence directory.'
}
if (-not $PlatePath.StartsWith(([IO.Path]::GetFullPath((Join-Path $repo 'docs\images'))).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'PlatePath must be under this repository docs\images directory.'
}
if (-not [IO.File]::Exists($ExePath)) { throw "Executable is missing: $ExePath" }

$xamlPath = Join-Path (Join-Path $repo 'native') 'MainWindow.xaml'
$themePath = Join-Path (Join-Path $repo 'native') 'Theme.xaml'
$designPath = Join-Path $repo 'DESIGN.md'
foreach ($required in @($xamlPath, $themePath, $designPath)) {
    if (-not [IO.File]::Exists($required)) { throw "Required source is missing: $required" }
}

$runId = [guid]::NewGuid().ToString('N')
$runRoot = Join-Path (Join-Path $EvidenceRoot 'runs') $runId
$profilePath = Join-Path $runRoot 'isolated-profile'
[IO.Directory]::CreateDirectory($runRoot) | Out-Null
[IO.Directory]::CreateDirectory($profilePath) | Out-Null

$started = [DateTimeOffset]::UtcNow

# ------------------------------------------------------------------- WCAG math
function Get-Linear([double]$c) { if ($c -le 0.03928) { return $c / 12.92 } return [Math]::Pow(($c + 0.055) / 1.055, 2.4) }
function Get-Luminance([string]$hex) {
    $r = [Convert]::ToInt32($hex.Substring(1, 2), 16)
    $g = [Convert]::ToInt32($hex.Substring(3, 2), 16)
    $b = [Convert]::ToInt32($hex.Substring(5, 2), 16)
    return 0.2126 * (Get-Linear ($r / 255.0)) + 0.7152 * (Get-Linear ($g / 255.0)) + 0.0722 * (Get-Linear ($b / 255.0))
}
function Get-Contrast([string]$a, [string]$b) {
    $l1 = Get-Luminance $a; $l2 = Get-Luminance $b
    if ($l1 -lt $l2) { $t = $l1; $l1 = $l2; $l2 = $t }
    return [Math]::Round(($l1 + 0.05) / ($l2 + 0.05), 2)
}

# ------------------------------------------- live tokens from the shipped Theme
$themeRaw = [IO.File]::ReadAllText($themePath)
$themeBrushes = [ordered]@{}
foreach ($m in [regex]::Matches($themeRaw, 'x:Key="(?<key>\w+)"\s+Color="(?<hex>#[0-9A-Fa-f]{6})"')) {
    $themeBrushes[$m.Groups['key'].Value] = $m.Groups['hex'].Value.ToUpperInvariant()
}
$themeAccentInk = $null
$accentInkMatch = [regex]::Match($themeRaw, 'x:Key="PrimaryButton".*?Property="Foreground"\s+Value="(?<hex>#[0-9A-Fa-f]{6})"')
if ($accentInkMatch.Success) { $themeAccentInk = $accentInkMatch.Groups['hex'].Value.ToUpperInvariant() }
if (-not $themeAccentInk) {
    $accentInkMatch = [regex]::Match($themeRaw, 'x:Key="PrimaryButton".*?Foreground="(?<hex>#[0-9A-Fa-f]{6})"')
    if ($accentInkMatch.Success) { $themeAccentInk = $accentInkMatch.Groups['hex'].Value.ToUpperInvariant() }
}

# ------------------------------------------------- design rules from DESIGN.md
$designRaw = [IO.File]::ReadAllText($designPath)
$designTokens = [ordered]@{}
foreach ($line in ($designRaw -split "`r?`n")) {
    $tm = [regex]::Match($line, '\|\s*(?<role>[A-Za-z]+)\s*\|\s*`(?<hex>#[0-9A-Fa-f]{6})`\s*\|')
    if ($tm.Success) { $designTokens[$tm.Groups['role'].Value] = $tm.Groups['hex'].Value.ToUpperInvariant() }
}
$spacingBase = 4
$spacingBaseMatch = [regex]::Match($designRaw, 'Base\s*\*\*(\d+)px\*\*')
if ($spacingBaseMatch.Success) { $spacingBase = [int]$spacingBaseMatch.Groups[1].Value }
$spacingScale = @()
$spacingScaleMatch = [regex]::Match($designRaw, 'Scale:\s*([0-9,\s]+)\.')
if ($spacingScaleMatch.Success) {
    $spacingScale = @($spacingScaleMatch.Groups[1].Value -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^\d+$' } | ForEach-Object { [int]$_ })
}

function Get-DesignHex([string]$role, $fallback) {
    if ($designTokens.Contains($role)) { return [string]$designTokens[$role] }
    return $fallback
}

$danger = Get-DesignHex 'danger' '#7A2B2B'
$stop = Get-DesignHex 'stop' '#A83232'
$white = '#FFFFFF'

$tokens = [ordered]@{
    bg         = [string]$themeBrushes['CanvasBrush']
    surface    = [string]$themeBrushes['PanelBrush']
    panel      = [string]$themeBrushes['CardBrush']
    stroke     = [string]$themeBrushes['StrokeBrush']
    foreground = [string]$themeBrushes['TextBrush']
    secondary  = [string]$themeBrushes['MutedBrush']
    accent     = [string]$themeBrushes['AccentBrush']
    accentInk  = if ($themeAccentInk) { $themeAccentInk } else { Get-DesignHex 'accentInk' '#0E1A12' }
    danger     = $danger
    stop       = $stop
}
foreach ($key in $tokens.Keys) {
    if (-not $tokens[$key]) { throw "Theme.xaml did not provide the $key token." }
}

$pairSpecs = @(
    @{ pair = 'foreground on bg';      fg = $tokens.foreground; bg = $tokens.bg;        need = 4.5; size = 'body' }
    @{ pair = 'foreground on surface'; fg = $tokens.foreground; bg = $tokens.surface;   need = 4.5; size = 'body' }
    @{ pair = 'foreground on panel';   fg = $tokens.foreground; bg = $tokens.panel;     need = 4.5; size = 'body' }
    @{ pair = 'secondary on bg';       fg = $tokens.secondary;  bg = $tokens.bg;        need = 4.5; size = 'body' }
    @{ pair = 'secondary on surface';  fg = $tokens.secondary;  bg = $tokens.surface;   need = 4.5; size = 'body' }
    @{ pair = 'accent on bg';          fg = $tokens.accent;     bg = $tokens.bg;        need = 3.0; size = 'ui' }
    @{ pair = 'accent on surface';     fg = $tokens.accent;     bg = $tokens.surface;   need = 3.0; size = 'ui' }
    @{ pair = 'accent on panel';       fg = $tokens.accent;     bg = $tokens.panel;     need = 3.0; size = 'ui' }
    @{ pair = 'accentInk on accent';   fg = $tokens.accentInk;  bg = $tokens.accent;    need = 4.5; size = 'button' }
    @{ pair = 'dangerInk on danger';   fg = $white;            bg = $tokens.danger;    need = 4.5; size = 'button' }
    @{ pair = 'stopInk on stop';       fg = $white;            bg = $tokens.stop;      need = 4.5; size = 'button' }
    @{ pair = 'stroke on bg';          fg = $tokens.stroke;     bg = $tokens.bg;        need = 1.5; size = 'border' }
)
$contrast = New-Object 'System.Collections.Generic.List[object]'
foreach ($spec in $pairSpecs) {
    $ratio = Get-Contrast $spec.fg $spec.bg
    $contrast.Add([ordered]@{
        pair = $spec.pair; foreground = $spec.fg; background = $spec.bg
        ratio = $ratio; need = $spec.need; sizeClass = $spec.size
        result = if ($ratio -ge $spec.need) { 'PASS' } else { 'FAIL' }
    })
}
$contrastFailures = New-Object 'System.Collections.Generic.List[object]'
foreach ($row in $contrast) { if ($row.result -eq 'FAIL') { $contrastFailures.Add($row) } }

# ------------------------------------ rule 2: literal hex must be an allowed token
# Allowed = every Theme.xaml resource colour the app ships, plus every role hex
# documented in the DESIGN.md palette table (which names danger and stop as the
# only destructive literals DESIGN.md rule 2 tolerates).
$allowedPalette = New-Object 'System.Collections.Generic.List[string]'
foreach ($hex in $themeBrushes.Values) { if (-not $allowedPalette.Contains($hex)) { $allowedPalette.Add($hex) } }
if ($themeAccentInk -and -not $allowedPalette.Contains($themeAccentInk)) { $allowedPalette.Add($themeAccentInk) }
foreach ($hex in $designTokens.Values) { if (-not $allowedPalette.Contains($hex)) { $allowedPalette.Add($hex) } }

$xamlLines = [IO.File]::ReadAllLines($xamlPath)
$hexPattern = [regex]'(?<![0-9A-Za-z])#(?<hex>[0-9A-Fa-f]{6})(?![0-9A-Fa-f])'
$paletteViolations = New-Object 'System.Collections.Generic.List[object]'
$hexOccurrences = 0
for ($i = 0; $i -lt $xamlLines.Length; $i++) {
    $text = $xamlLines[$i]
    foreach ($m in $hexPattern.Matches($text)) {
        $hexOccurrences++
        $value = '#' + $m.Groups['hex'].Value.ToUpperInvariant()
        if ($allowedPalette.Contains($value)) { continue }
        $before = if ($m.Index -gt 0) { $text.Substring(0, $m.Index) } else { '' }
        $attrMatch = [regex]::Match($before, '(?:Property\s*=\s*")?(?<attr>Margin|Padding|Background|Foreground|BorderBrush|Color|Fill|Stroke)\s*=\s*"[^"]*$')
        if (-not $attrMatch.Success) { $attrMatch = [regex]::Match($before, 'Property="(?<attr>\w+)"\s+Value="[^"]*$') }
        $paletteViolations.Add([ordered]@{
            line = ($i + 1); value = $value
            attribute = if ($attrMatch.Success) { $attrMatch.Groups['attr'].Value } else { $null }
            rule = 'DESIGN.md rule 2'
        })
    }
}

# ------------------------- spacing: every Margin/Padding literal is a 4px multiple
$spacingViolations = New-Object 'System.Collections.Generic.List[object]'
$spacingLiterals = 0
$spacingPattern = [regex]'(?<attr>\bMargin|\bPadding)\s*=\s*"(?<literal>[^"]*)"'
for ($i = 0; $i -lt $xamlLines.Length; $i++) {
    $text = $xamlLines[$i]
    foreach ($m in $spacingPattern.Matches($text)) {
        $literal = $m.Groups['literal'].Value
        $spacingLiterals++
        $components = @($literal -split '[,;]')
        for ($c = 0; $c -lt $components.Count; $c++) {
            $token = $components[$c].Trim()
            if ($token -notmatch '^-?\d+(\.\d+)?$') { continue }
            $number = [double]::Parse($token, [Globalization.CultureInfo]::InvariantCulture)
            if ($number -eq 0) { continue }
            if ([Math]::Abs($number / $spacingBase - [Math]::Round($number / $spacingBase)) -lt 0.0001) { continue }
            $spacingViolations.Add([ordered]@{
                line = ($i + 1); value = $token
                attribute = $m.Groups['attr'].Value
                literal = $literal; component = ($c + 1); base = $spacingBase
                rule = 'DESIGN.md spacing base'
            })
        }
    }
}

# ------------------------------------------------ owned diagnostic invocations
function Quote-Argument([string]$value) {
    if ($value.Length -gt 0 -and $value -notmatch '[\s"]') { return $value }
    $output = New-Object System.Text.StringBuilder
    [void]$output.Append('"')
    $slashes = 0
    foreach ($character in $value.ToCharArray()) {
        if ($character -eq '\') { $slashes++; continue }
        if ($character -eq '"') {
            [void]$output.Append(('\' * (2 * $slashes + 1)))
            [void]$output.Append('"')
            $slashes = 0
            continue
        }
        if ($slashes -gt 0) { [void]$output.Append(('\' * $slashes)); $slashes = 0 }
        [void]$output.Append($character)
    }
    if ($slashes -gt 0) { [void]$output.Append(('\' * (2 * $slashes))) }
    [void]$output.Append('"')
    return $output.ToString()
}

function Invoke-OwnedDiagnostic([string]$name, [string]$file, [string[]]$arguments,
    [int]$timeoutSeconds, [string]$receiptPath) {
    $caseStart = [DateTimeOffset]::UtcNow
    $stdoutPath = Join-Path $runRoot ($name + '.stdout.txt')
    $stderrPath = Join-Path $runRoot ($name + '.stderr.txt')
    $failures = New-Object 'System.Collections.Generic.List[string]'
    $exitCode = $null
    $processId = $null
    $processExited = $false
    $timedOut = $false
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo.FileName = $file
    $process.StartInfo.Arguments = (($arguments | ForEach-Object { Quote-Argument $_ }) -join ' ')
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.CreateNoWindow = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    try {
        if (-not $process.Start()) { throw 'Process did not start.' }
        $processId = $process.Id
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($timeoutSeconds * 1000)) {
            $timedOut = $true
            $failures.Add("Timed out after $timeoutSeconds seconds.")
            try {
                if (-not $process.HasExited) { $process.Kill() }
                $processExited = $process.WaitForExit(10000)
                if (-not $processExited) {
                    $failures.Add("Owned diagnostic process $processId remained active after the exact-process stop attempt.")
                }
            } catch {
                $failures.Add("Could not stop owned diagnostic process $processId after timeout: $($_.Exception.Message)")
            }
        } else {
            $exitCode = $process.ExitCode
            $processExited = $true
        }
        if ($stdoutTask.Wait(5000)) { [IO.File]::WriteAllText($stdoutPath, $stdoutTask.GetAwaiter().GetResult()) }
        else { $failures.Add('Owned diagnostic stdout did not close within five seconds.'); [IO.File]::WriteAllText($stdoutPath, '') }
        if ($stderrTask.Wait(5000)) { [IO.File]::WriteAllText($stderrPath, $stderrTask.GetAwaiter().GetResult()) }
        else { $failures.Add('Owned diagnostic stderr did not close within five seconds.'); [IO.File]::WriteAllText($stderrPath, '') }
    } catch {
        $failures.Add($_.Exception.Message)
    } finally { $process.Dispose() }
    if ($null -ne $exitCode -and $exitCode -ne 0) { $failures.Add("Exit code $exitCode.") }

    $receipt = $null
    if ($processId -and $processExited) {
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
        do {
            if ([IO.File]::Exists($receiptPath)) {
                try {
                    $candidate = [IO.File]::ReadAllText($receiptPath) | ConvertFrom-Json
                    if ($null -ne $candidate) { $receipt = $candidate; break }
                } catch { }
            }
            if ([DateTimeOffset]::UtcNow -ge $deadline) { break }
            Start-Sleep -Milliseconds 200
        } while ($true)
        if ($null -eq $receipt) { $failures.Add("Result receipt $receiptPath did not become valid JSON.") }
    } else {
        $failures.Add('Result receipt wait skipped because the owned diagnostic did not exit.')
    }

    return [ordered]@{
        name = $name; startedUtc = $caseStart.ToString('o'); finishedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        arguments = $arguments; processId = $processId; processExited = $processExited; timedOut = $timedOut
        exitCode = $exitCode; receiptPath = $receiptPath; receipt = $receipt; invocationFailures = $failures.ToArray()
        artifacts = @($stdoutPath, $stderrPath, $receiptPath)
    }
}

function Measure-Receipt($receipt) {
    $out = [ordered]@{
        loaded = $false; total = 0; failures = 0; failedChecks = @()
        declaredTests = $null; declaredFailures = $null; receiptPassed = $null; note = $null
    }
    if ($null -eq $receipt) { $out.note = 'Receipt was not loaded.'; return $out }
    $out.loaded = $true
    $names = @($receipt.PSObject.Properties.Name)
    $checks = @()
    if ($names -contains 'checks') { $checks = @($receipt.checks) }
    elseif ($names -contains 'tests' -and $receipt.tests -is [System.Array]) { $checks = @($receipt.tests) }
    if ($checks.Count -eq 0) { $out.note = 'Receipt contained no check entries, so no total could be measured.' }
    $out.total = $checks.Count
    $failed = @($checks | Where-Object { $_.passed -ne $true })
    $out.failures = $failed.Count
    $out.failedChecks = @($failed | ForEach-Object { [string]$_.name })
    if ($names -contains 'tests' -and $receipt.tests -isnot [System.Array]) { $out.declaredTests = [int]$receipt.tests }
    if ($names -contains 'failures') {
        if ($receipt.failures -is [System.Array]) { $out.declaredFailures = @($receipt.failures).Count }
        else { $out.declaredFailures = [int]$receipt.failures }
    }
    if ($names -contains 'passed') { $out.receiptPassed = ($receipt.passed -eq $true) }
    return $out
}

$selfTestReceipt = Join-Path $runRoot 'self-test.json'
$uiTestReceipt = Join-Path $runRoot 'ui-test.json'

$selfInvocation = $null
$uiInvocation = $null
try {
    Write-Output ("[1/3] running --self-test against {0}" -f $ExePath)
    $selfInvocation = Invoke-OwnedDiagnostic 'self-test' $ExePath @('--self-test', $selfTestReceipt) $SelfTestTimeoutSeconds $selfTestReceipt
    Write-Output ("[2/3] running --ui-test (offline, isolated profile) against {0}" -f $ExePath)
    $uiInvocation = Invoke-OwnedDiagnostic 'ui-test' $ExePath @('--data-dir', $profilePath, '--offline', '--ui-test', $uiTestReceipt) $UiTestTimeoutSeconds $uiTestReceipt
} finally {
    Get-Process -Name 'GameLibrary' -ErrorAction SilentlyContinue | ForEach-Object {
        if ($selfInvocation -and $_.Id -eq $selfInvocation.processId) { try { $_.Kill() } catch { } }
        if ($uiInvocation -and $_.Id -eq $uiInvocation.processId) { try { $_.Kill() } catch { } }
    }
}

# Both owned processes are confirmed exited, so the scratch they created is safe
# to drop: the self-test fixture tree and the isolated offline profile are
# together ~650 MB per run, and the receipts are the only durable evidence.
function Remove-OwnedTransientTree([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return $false }
    try {
        if (-not [IO.Directory]::Exists($path)) { return $false }
        Remove-Item -LiteralPath $path -Recurse
        return (-not [IO.Directory]::Exists($path))
    } catch { return $false }
}
$transientRemoved = New-Object 'System.Collections.Generic.List[string]'
foreach ($dir in (Get-ChildItem -LiteralPath $runRoot -Directory -Force)) {
    if ($dir.Name -ne 'isolated-profile' -and $dir.Name -notlike 'test-data-*') { continue }
    if (Remove-OwnedTransientTree $dir.FullName) { $transientRemoved.Add($dir.Name) }
}
Write-Output ("[3/3] cleaned {0} transient scratch tree(s); receipts retained in {1}" -f $transientRemoved.Count, $runRoot)

$selfTest = Measure-Receipt $selfInvocation.receipt
$uiTest = Measure-Receipt $uiInvocation.receipt

$gates = [ordered]@{
    selfTestReceiptLoaded      = ($selfTest.loaded -and $selfTest.total -gt 0)
    selfTestNoFailures         = ($selfTest.loaded -and $selfTest.failures -eq 0)
    uiTestReceiptLoaded        = ($uiTest.loaded -and $uiTest.total -gt 0)
    uiTestNoFailures           = ($uiTest.loaded -and $uiTest.failures -eq 0)
    contrastAllPass            = ($contrastFailures.Count -eq 0)
    paletteRuleClean           = ($paletteViolations.Count -eq 0)
    spacingRuleClean           = ($spacingViolations.Count -eq 0)
}
$failedGates = New-Object 'System.Collections.Generic.List[string]'
foreach ($gate in $gates.GetEnumerator()) { if ($gate.Value -ne $true) { $failedGates.Add($gate.Key) } }
$passed = $failedGates.Count -eq 0

$spacingDistinctLiterals = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($v in $spacingViolations) { [void]$spacingDistinctLiterals.Add(('L{0} {1}' -f $v.line, $v.literal)) }
$paletteDistinctValues = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($v in $paletteViolations) { [void]$paletteDistinctValues.Add($v.value) }
$paletteCount = $paletteViolations.Count
$spacingCount = $spacingViolations.Count
$paletteDistinctCount = $paletteDistinctValues.Count
$spacingDistinctCount = $spacingDistinctLiterals.Count
$measuredChecks = ([int]$selfTest.total) + ([int]$uiTest.total)
$measuredFailures = ([int]$selfTest.failures) + ([int]$uiTest.failures)

if ($passed) {
    $summary = ("PASS - {0} measured checks, {1} failures, {2}/{2} contrast pairs pass, 0 palette and 0 spacing rule violations." -f $measuredChecks, $measuredFailures, $contrast.Count)
} else {
    $summary = ("FAIL - {0} measured checks, {1} failures, {2}/{3} contrast pairs pass, {4} palette ({5} distinct hex) and {6} spacing ({7} distinct literals) rule violations in native\MainWindow.xaml. Gates: {8}." -f `
        $measuredChecks, $measuredFailures, ($contrast.Count - $contrastFailures.Count), $contrast.Count, `
        $paletteCount, $paletteDistinctCount, $spacingCount, $spacingDistinctCount, ($failedGates -join ', '))
}

$report = [ordered]@{
    schemaVersion = $schemaVersion
    runId = $runId
    generatedUtc = $started.ToString('o')
    finishedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    passed = $passed
    failedGates = $failedGates.ToArray()
    summary = $summary
    generator = [ordered]@{
        script = 'native\generate-acceptance-density.ps1'
        repoRoot = $repo
        executablePath = $ExePath
        executableSha256 = (Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash
        evidenceRoot = $EvidenceRoot
        runRoot = $runRoot
        isolatedProfile = $profilePath
        platePath = $PlatePath
        transientTreesRemoved = $transientRemoved.ToArray()
    }
    selfTest = [ordered]@{
        total = [int]$selfTest.total
        failures = [int]$selfTest.failures
        failedChecks = $selfTest.failedChecks
        declaredTests = $selfTest.declaredTests
        declaredFailures = $selfTest.declaredFailures
        receiptPassed = $selfTest.receiptPassed
        receiptPath = $selfTestReceipt
        receiptLoaded = [bool]$selfTest.loaded
        exitCode = $selfInvocation.exitCode
        timedOut = [bool]$selfInvocation.timedOut
        invocationFailures = $selfInvocation.invocationFailures
    }
    uiTest = [ordered]@{
        total = [int]$uiTest.total
        failures = [int]$uiTest.failures
        failedChecks = $uiTest.failedChecks
        declaredTests = $uiTest.declaredTests
        declaredFailures = $uiTest.declaredFailures
        receiptPassed = $uiTest.receiptPassed
        receiptPath = $uiTestReceipt
        receiptLoaded = [bool]$uiTest.loaded
        exitCode = $uiInvocation.exitCode
        timedOut = [bool]$uiInvocation.timedOut
        invocationFailures = $uiInvocation.invocationFailures
    }
    contrast = $contrast.ToArray()
    paletteViolations = $paletteViolations.ToArray()
    spacingViolations = $spacingViolations.ToArray()
    designRules = [ordered]@{
        source = 'DESIGN.md'
        xaml = 'native\MainWindow.xaml'
        theme = 'native\Theme.xaml'
        spacingBasePx = $spacingBase
        documentedSpacingScale = $spacingScale
        allowedPalette = $allowedPalette.ToArray()
        documentedTokens = $designTokens
        shippedTokens = $tokens
        accentInkDrift = if ($designTokens.Contains('accentInk') -and $designTokens['accentInk'] -ne $tokens.accentInk) {
            "DESIGN.md documents accentInk $($designTokens['accentInk']) but Theme.xaml ships $($tokens.accentInk); contrast is measured against the shipped value."
        } else { $null }
        hexOccurrencesChecked = $hexOccurrences
        spacingLiteralsChecked = $spacingLiterals
        paletteDistinctValues = $paletteDistinctCount
        spacingDistinctLiterals = $spacingDistinctCount
    }
    gates = $gates
}

$reportPath = Join-Path $EvidenceRoot 'acceptance-density.json'
[IO.Directory]::CreateDirectory($EvidenceRoot) | Out-Null
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $reportPath -Encoding UTF8

# ------------------------------------------------------------- rendered plate
Add-Type -AssemblyName System.Drawing

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Brush([string]$hex) { return New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($hex)) }
function PxFont([float]$size, [System.Drawing.FontStyle]$style = [System.Drawing.FontStyle]::Regular, [string]$family = 'Segoe UI') {
    return New-Object System.Drawing.Font($family, $size, $style, [System.Drawing.GraphicsUnit]::Pixel)
}

$canvasW = 1500
$canvasH = 1000
$bmp = New-Object System.Drawing.Bitmap($canvasW, $canvasH)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
$g.Clear([System.Drawing.ColorTranslator]::FromHtml($tokens.bg))

$fTitle = PxFont 34 ([System.Drawing.FontStyle]::Bold)
$fSection = PxFont 16 ([System.Drawing.FontStyle]::Bold)
$fLabel = PxFont 13 ([System.Drawing.FontStyle]::Bold)
$fBody = PxFont 16
$fSmall = PxFont 14
$fTiny = PxFont 13
$fFigure = PxFont 54 ([System.Drawing.FontStyle]::Bold) 'Bahnschrift'
$fMono = PxFont 15 ([System.Drawing.FontStyle]::Regular) 'Consolas'
$fMonoSmall = PxFont 13 ([System.Drawing.FontStyle]::Regular) 'Consolas'
$fVerdict = PxFont 22 ([System.Drawing.FontStyle]::Bold)

$bFore = Brush $tokens.foreground
$bSec = Brush $tokens.secondary
$bAcc = Brush $tokens.accent
$bDanger = Brush $tokens.danger
$bPanel = Brush $tokens.panel
$bSurface = Brush $tokens.surface
$pStroke = New-Object System.Drawing.Pen([System.Drawing.ColorTranslator]::FromHtml($tokens.stroke), 1.2)

function Draw-Text([string]$text, $font, $brush, [float]$x, [float]$y) {
    $g.DrawString($text, $font, $brush, $x, $y)
}
function Draw-TextBox([string]$text, $font, $brush, [float]$x, [float]$y, [float]$w, [float]$h) {
    $g.DrawString($text, $font, $brush, (New-Object System.Drawing.RectangleF($x, $y, $w, $h)))
}
function Draw-Card([float]$x, [float]$y, [float]$w, [float]$h) {
    $path = New-RoundedPath $x $y $w $h 12
    $g.FillPath($bPanel, $path)
    $g.DrawPath($pStroke, $path)
    $path.Dispose()
}
function Draw-Stat([float]$x, [float]$y, [float]$w, [string]$label, [string]$figure, [string]$detail, [string]$accentHex) {
    Draw-Card $x $y $w 150
    Draw-Text $label $fLabel $bSec ($x + 22) ($y + 14)
    Draw-Text $figure $fFigure (Brush $accentHex) ($x + 18) ($y + 32)
    Draw-Text $detail $fSmall $bSec ($x + 22) ($y + 108)
}

Draw-Text 'Game Library Manager - acceptance: density, smoothness, completion' $fTitle $bFore 48 34
Draw-Text ('Measured from the packaged executable.  runId ' + $runId + '  generated ' + $started.ToString('yyyy-MM-dd HH:mm:ss') + 'Z  |  plate drawn programmatically, never captured') $fSmall $bSec 48 82

Draw-Text 'DIAGNOSTIC RECEIPTS  (--self-test / --ui-test, real JSON)' $fSection $bAcc 48 116
Draw-Stat 48 142 440 'SELF-TEST CHECKS' ([string]$selfTest.total) ("failures: " + $selfTest.failures) $tokens.accent
Draw-Stat 508 142 440 'UI-TEST CHECKS' ([string]$uiTest.total) ("failures: " + $uiTest.failures) $tokens.accent
$findingAccent = if ($paletteCount + $spacingCount -eq 0) { $tokens.accent } else { $tokens.stop }
Draw-Stat 968 142 484 'DESIGN RULE VIOLATIONS' ([string]($paletteCount + $spacingCount)) ("palette " + $paletteCount + "   spacing " + $spacingCount + "   check failures " + $measuredFailures) $findingAccent

Draw-Text ('WCAG CONTRAST - live Theme.xaml tokens (' + $tokens.Count + ' semantic roles, ' + $contrast.Count + ' measured pairs)') $fSection $bAcc 48 306
$ty = 338
foreach ($head in @(@('PAIR', 48), @('RATIO', 470), @('NEED', 590), @('SIZE', 700), @('RESULT', 818), @('SWATCH', 1000), @('VALUES', 1270))) {
    Draw-Text $head[0] $fMono $bSec $head[1] $ty
}
$ty += 10
$g.DrawLine((New-Object System.Drawing.Pen([System.Drawing.ColorTranslator]::FromHtml($tokens.stroke), 1)), 48, $ty, 1452, $ty)
$ty += 12
foreach ($row in $contrast) {
    $mark = if ($row.result -eq 'PASS') { $tokens.accent } else { $tokens.danger }
    $pill = New-RoundedPath 818 ($ty - 4) 92 24 12
    $g.FillPath((Brush $mark), $pill)
    $pill.Dispose()
    Draw-Text $row.pair $fBody $bFore 48 $ty
    Draw-Text ('{0:N2}' -f $row.ratio) $fBody $bFore 470 $ty
    Draw-Text ('{0:N1}' -f $row.need) $fBody $bSec 590 $ty
    Draw-Text $row.sizeClass $fBody $bSec 700 $ty
    Draw-Text $row.result $fLabel $bFore 818 ($ty + 3)
    $sw = New-RoundedPath 1000 ($ty - 2) 120 22 6
    $g.FillPath((Brush $row.background), $sw)
    $g.DrawPath($pStroke, $sw)
    $sw.Dispose()
    $sw2 = New-RoundedPath 1128 ($ty - 2) 120 22 6
    $g.FillPath((Brush $row.foreground), $sw2)
    $g.DrawPath($pStroke, $sw2)
    $sw2.Dispose()
    Draw-Text ($row.foreground + ' on ' + $row.background) $fMonoSmall $bSec 1270 $ty
    $ty += 26
}

$ry = [Math]::Max($ty + 28, 700)
Draw-Text ('DESIGN.md RULES CHECKED AGAINST native\MainWindow.xaml  (' + $hexOccurrences + ' hex literals, ' + $spacingLiterals + ' Margin/Padding literals)') $fSection $bAcc 48 $ry
$ry += 32
$paletteAccent = if ($paletteCount -eq 0) { $tokens.accent } else { $tokens.stop }
$spacingAccent = if ($spacingCount -eq 0) { $tokens.accent } else { $tokens.stop }
Draw-Card 48 $ry 690 116
Draw-Text 'Rule 2 - every literal hex must be a Theme.xaml / DESIGN.md palette value' $fLabel $bSec 70 ($ry + 14)
Draw-Text ([string]$paletteCount) $fFigure (Brush $paletteAccent) 70 ($ry + 32)
Draw-Text 'violations' $fSmall $bSec 152 ($ry + 76)
if ($paletteCount -gt 0) {
    $shown = @($paletteViolations | Select-Object -First 4 | ForEach-Object { 'L' + $_.line + ' ' + $_.value })
    Draw-Text (($shown -join '   ')) $fMonoSmall $bSec 240 ($ry + 40)
    Draw-Text (('+ ' + ($paletteCount - $shown.Count) + ' more   /   ' + $paletteDistinctCount + ' distinct hex values')) $fMonoSmall $bSec 240 ($ry + 66)
} else {
    Draw-Text 'no literal hex outside the allowed palette' $fMonoSmall $bSec 240 ($ry + 40)
}

Draw-Card 762 $ry 690 116
Draw-Text ('Spacing - every Margin/Padding literal is a multiple of ' + $spacingBase + 'px') $fLabel $bSec 784 ($ry + 14)
Draw-Text ([string]$spacingCount) $fFigure (Brush $spacingAccent) 784 ($ry + 32)
Draw-Text 'violations' $fSmall $bSec 866 ($ry + 76)
if ($spacingCount -gt 0) {
    $shown = @($spacingDistinctLiterals | Select-Object -First 4)
    Draw-Text (($shown -join '   ')) $fMonoSmall $bSec 954 ($ry + 40)
    Draw-Text (('+ ' + ($spacingDistinctCount - $shown.Count) + ' more literals   /   ' + $spacingCount + ' offending components')) $fMonoSmall $bSec 954 ($ry + 66)
} else {
    Draw-Text ('every literal is on the ' + $spacingBase + 'px base') $fMonoSmall $bSec 954 ($ry + 40)
}

$vy = $ry + 126
$verdictAccent = if ($passed) { $tokens.accent } else { $tokens.stop }
Draw-Card 48 $vy 1404 136
$g.FillRectangle((Brush $verdictAccent), 48, $vy, 12, 136)
Draw-Text $(if ($passed) { 'VERDICT: PASS' } else { 'VERDICT: FAIL' }) $fVerdict (Brush $verdictAccent) 78 ($vy + 14)
Draw-TextBox $summary $fBody $bFore 78 ($vy + 50) 1348 52
Draw-Text ('report ' + $reportPath + '   |   plate docs\images\acceptance-density-2026-10.png') $fMonoSmall $bSec 78 ($vy + 110)

[IO.Directory]::CreateDirectory((Split-Path -Parent $PlatePath)) | Out-Null
$bmp.Save($PlatePath, [System.Drawing.Imaging.ImageFormat]::Png)

$g.Dispose()
$bmp.Dispose()
foreach ($o in @($fTitle, $fSection, $fLabel, $fBody, $fSmall, $fTiny, $fFigure, $fMono, $fMonoSmall, $fVerdict,
        $bFore, $bSec, $bAcc, $bDanger, $bPanel, $bSurface, $pStroke)) { try { $o.Dispose() } catch { } }
[GC]::Collect(); [GC]::WaitForPendingFinalizers()

# ------------------------------------------------------------- stdout summary
Write-Output ''
Write-Output ('Game Library Manager - acceptance density report')
Write-Output ('  run id            : ' + $runId)
Write-Output ('  executable        : ' + $ExePath)
Write-Output ('  report            : ' + $reportPath)
Write-Output ('  plate             : ' + $PlatePath + '  (' + $canvasW + 'x' + $canvasH + ')')
Write-Output ('  run root          : ' + $runRoot + '  (transient scratch removed: ' + $transientRemoved.Count + ')')
Write-Output ''
Write-Output ('  self-test         : total ' + $selfTest.total + ', failures ' + $selfTest.failures + ', exit ' + $selfInvocation.exitCode)
Write-Output ('  ui-test           : total ' + $uiTest.total + ', failures ' + $uiTest.failures + ', exit ' + $uiInvocation.exitCode)
Write-Output ('  measured checks   : ' + $measuredChecks + ' total, ' + $measuredFailures + ' failures')
Write-Output ''
Write-Output '  WCAG contrast (live Theme.xaml tokens):'
Write-Output ('    ' + ('{0,-22} {1,7} {2,6} {3,-8} {4}' -f 'PAIR', 'RATIO', 'NEED', 'SIZE', 'RESULT'))
foreach ($row in $contrast) {
    Write-Output ('    ' + ('{0,-22} {1,7:N2} {2,6:N1} {3,-8} {4}' -f $row.pair, $row.ratio, $row.need, $row.sizeClass, $row.result))
}
Write-Output ('    contrast pairs failing: ' + $contrastFailures.Count)
Write-Output ''
Write-Output ('  DESIGN.md rule 2 (palette) violations  : ' + $paletteCount + ' of ' + $hexOccurrences + ' hex literals (' + $paletteDistinctCount + ' distinct hex values)')
foreach ($v in ($paletteViolations | Select-Object -First 12)) { Write-Output ('    line ' + $v.line + '  ' + $v.value + '  (' + $v.attribute + ')') }
if ($paletteCount -gt 12) { Write-Output ('    ... + ' + ($paletteCount - 12) + ' more') }
Write-Output ('  DESIGN.md spacing violations            : ' + $spacingCount + ' offending components in ' + $spacingDistinctCount + ' of ' + $spacingLiterals + ' Margin/Padding literals')
foreach ($v in ($spacingViolations | Select-Object -First 12)) { Write-Output ('    line ' + $v.line + '  ' + $v.attribute + '="' + $v.literal + '"  offending ' + $v.value + 'px') }
if ($spacingCount -gt 12) { Write-Output ('    ... + ' + ($spacingCount - 12) + ' more') }
Write-Output ''
Write-Output ('  failed gates   : ' + $(if ($failedGates.Count -eq 0) { '(none)' } else { $failedGates -join ', ' }))
Write-Output ('  passed         : ' + $passed)
Write-Output ('  summary        : ' + $summary)
Write-Output ''

if (-not $passed) { exit 1 }
exit 0
