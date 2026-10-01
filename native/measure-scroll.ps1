param(
    [string]$ExePath = (Join-Path $PSScriptRoot 'dist\GameLibrary.exe'),
    [string]$ReportPath,
    [int]$Rounds = 6,
    [double]$StepPercent = 1,
    [double]$BudgetMs = 16.7,
    [double]$MaxOverBudgetRatio = 0.05,
    [double]$FreezeCeilingMs = 250,
    [int]$MinCatalogItems = 1000,
    [int]$MinMeasuredSteps = 200,
    [int]$ReadyTimeoutSeconds = 240,
    [int]$StepTimeoutMs = 5000
)

# Scroll smoothness harness.
#
# It drives the packaged Game Library window with UI Automation in exactly the
# shape measure-search.ps1 uses (--data-dir <isolated profile under evidence>
# --offline, no user data, no network), then walks the virtualized GameList with
# fixed SetScrollPercent steps and times every step. A "frame drop" becomes a
# measured number instead of an opinion.
#
# One step = the synchronous UI Automation SetScrollPercent call (which marshals
# onto the WPF UI thread and returns when that thread finished the scroll, so it
# contains the realize and layout work) plus a WM_NULL round trip with SMTO_ABORTIFHUNG
# after a 1ms settle, which blocks for exactly as long as the UI thread stays busy.
# The harness' own floor is measured with no-op scroll calls and reported.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ScrollProbe {
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool MoveWindow(IntPtr window, int x, int y, int width, int height, bool repaint);
    [StructLayout(LayoutKind.Sequential)]
    struct Rect { public int Left, Top, Right, Bottom; }
    const uint WmNull = 0x0000;
    const uint SmtoAbortIfHung = 0x0002;
    /// <summary>Blocks until the owning UI thread pumps our WM_NULL. Returns microseconds.</summary>
    public static long Ping(IntPtr window, uint timeoutMilliseconds) {
        IntPtr result;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        SendMessageTimeout(window, WmNull, IntPtr.Zero, IntPtr.Zero, SmtoAbortIfHung, timeoutMilliseconds, out result);
        return (long)((System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
    }
    /// <summary>Grows the window to the given size so the visible card band matches a real session.</summary>
    public static bool Resize(IntPtr window, int width, int height) {
        Rect rect;
        if (!GetWindowRect(window, out rect)) return false;
        return MoveWindow(window, rect.Left, rect.Top, width, height, true);
    }
}
'@

$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$evidenceRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'evidence'))
[IO.Directory]::CreateDirectory($evidenceRoot) | Out-Null
$runId = [guid]::NewGuid().ToString('N')
$dataRoot = Join-Path $evidenceRoot ('scroll-timing-' + (Get-Date -Format yyyyMMdd-HHmmss) + '-' + $runId.Substring(0, 8))
[IO.Directory]::CreateDirectory($dataRoot) | Out-Null
if ([string]::IsNullOrWhiteSpace($ReportPath)) { $ReportPath = Join-Path $dataRoot 'scroll-report.json' }
$ReportPath = [IO.Path]::GetFullPath($ReportPath)
$evidencePrefix = $evidenceRoot.TrimEnd('\') + '\'
if (-not $ReportPath.StartsWith($evidencePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'ReportPath must be under this repository evidence directory.' }
[IO.Directory]::CreateDirectory((Split-Path $ReportPath -Parent)) | Out-Null
$ExePath = [IO.Path]::GetFullPath($ExePath)
if (-not [IO.File]::Exists($ExePath)) { throw "Executable is missing: $ExePath" }

$started = [DateTimeOffset]::UtcNow
$executableSha256 = (Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash
$failures = New-Object 'System.Collections.Generic.List[string]'
$checks = New-Object 'System.Collections.Generic.List[object]'
$warmup = New-Object 'System.Collections.Generic.List[double]'
$rewinds = New-Object 'System.Collections.Generic.List[double]'
$steps = New-Object 'System.Collections.Generic.List[object]'
$app = $null
$catalogItems = 0
$viewSizePercent = 0.0
$realizedPeak = 0
$viewport = ''

function Add-Check([string]$name, [bool]$passed, [string]$detail) {
    $checks.Add([ordered]@{ name = $name; passed = $passed; detail = $detail })
    if (-not $passed) { $failures.Add($name + ': ' + $detail) }
}
function Percentile([double[]]$values, [double]$percentile) {
    if ($null -eq $values -or $values.Count -eq 0) { return 0.0 }
    $sorted = @($values | Sort-Object)
    $rank = [int][Math]::Ceiling(($percentile / 100.0) * $sorted.Count) - 1
    if ($rank -lt 0) { $rank = 0 }
    if ($rank -ge $sorted.Count) { $rank = $sorted.Count - 1 }
    return [double]$sorted[$rank]
}

try {
    # measure-search.ps1 invocation shape: an isolated profile under evidence plus
    # --offline. No user library, no network, no UI interaction required.
    $app = Start-Process -FilePath $ExePath -ArgumentList @('--data-dir', $dataRoot, '--offline') -PassThru -WindowStyle Normal
    $listCondition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, 'GameList')
    $rowCondition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)
    $textCondition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text)

    $window = $null
    $list = $null
    $deadline = [DateTime]::UtcNow.AddSeconds($ReadyTimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline -and -not $list) {
        Start-Sleep -Milliseconds 250
        if ($app.HasExited) { throw "The packaged window exited during startup with exit code $($app.ExitCode)." }
        $app.Refresh()
        if ($app.MainWindowHandle -eq [IntPtr]::Zero) { continue }
        $candidate = [Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
        if (-not $candidate) { continue }
        $candidateList = $candidate.FindFirst([Windows.Automation.TreeScope]::Descendants, $listCondition)
        if (-not $candidateList) { continue }
        if ($candidateList.FindAll([Windows.Automation.TreeScope]::Children, $rowCondition).Count -gt 0) {
            $window = $candidate
            $list = $candidateList
        }
    }
    if (-not $list) { throw "The game list did not become ready within $ReadyTimeoutSeconds seconds." }

    # The window applies its own monitor placement, so wait for the real layout to
    # settle and only then read the viewport. Every step is measured against this
    # same list rectangle, so the realized card band per step is identical per run.
    $list = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $listCondition)
    $scroll = [Windows.Automation.ScrollPattern]$list.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
    $layoutDeadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 400
        $app.Refresh()
        if ($app.MainWindowHandle -ne [IntPtr]::Zero) {
            $list = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $listCondition)
            $viewport = $list.Current.BoundingRectangle.ToString()
        }
        $rect = $list.Current.BoundingRectangle
    } while ([DateTime]::UtcNow -lt $layoutDeadline -and ($rect.Height -lt 120 -or $rect.Width -lt 320))
    if ($rect.Height -lt 120 -or $rect.Width -lt 320) { throw "The game list never settled into a measurable viewport ($viewport)." }

    # The catalog size is read from the app's own status line so the long-list
    # claim comes from the app, not from an estimate.
    foreach ($text in $window.FindAll([Windows.Automation.TreeScope]::Descendants, $textCondition)) {
        $name = $text.Current.Name
        if ($name -match '([\d,]+)\s+identity cards') {
            $parsed = 0
            if ([int]::TryParse($Matches[1].Replace(',', ''), [ref]$parsed)) { $catalogItems = [Math]::Max($catalogItems, $parsed) }
        }
    }
    $viewSizePercent = [double]$scroll.Current.VerticalViewSize
    $realizedPeak = $list.FindAll([Windows.Automation.TreeScope]::Children, $rowCondition).Count
    Write-Output ("READY=YES catalogCards=$catalogItems viewSizePercent=$([Math]::Round($viewSizePercent,4)) realizedPeak=$realizedPeak viewport=$viewport")
    if ($catalogItems -le 0) { throw 'The window did not report an identity-card catalog count, so the long-list requirement cannot be verified.' }

    # Warm-up pass: the first realize of every card pays JIT, the theme pass and
    # the first cover decode. Measured, reported, and excluded from the scored steps.
    $warmPercent = 0.0
    $warmDeadline = [DateTime]::UtcNow.AddSeconds(20)
    while ($warmPercent -lt 99 -and [DateTime]::UtcNow -lt $warmDeadline) {
        $warmPercent = [Math]::Min(100.0, $warmPercent + ($StepPercent * 100.0))
        $warmClock = [Diagnostics.Stopwatch]::StartNew()
        $scroll.SetScrollPercent(-1, $warmPercent)
        $warmup.Add($warmClock.Elapsed.TotalMilliseconds + ([ScrollProbe]::Ping($app.MainWindowHandle, $StepTimeoutMs) / 1000.0))
    }
    $scroll.SetScrollPercent(-1, 0)
    Start-Sleep -Milliseconds 500

    # Harness floor, measured once the window is settled: a scroll request that
    # moves nothing. This is measurement and IPC overhead, not app work, and it is
    # reported next to the results so the step numbers can be read honestly.
    $floor = New-Object 'System.Collections.Generic.List[double]'
    for ($i = 0; $i -lt 12; $i++) {
        $floorClock = [Diagnostics.Stopwatch]::StartNew()
        $scroll.SetScrollPercent(-1, 0)
        $floor.Add($floorClock.Elapsed.TotalMilliseconds + ([ScrollProbe]::Ping($app.MainWindowHandle, $StepTimeoutMs) / 1000.0))
    }
    $floorMs = if ($floor.Count -gt 0) { Percentile $floor.ToArray() 95 } else { 0.0 }
    $floorMax = if ($floor.Count -gt 0) { [double]($floor | Measure-Object -Maximum).Maximum } else { 0.0 }

    $measuredClock = [Diagnostics.Stopwatch]::StartNew()
    $current = 0.0
    $stepIndex = 0
    $itemsTraversed = 0
    $overBudget = 0
    $timeouts = 0
    $worst = [ordered]@{ step = 0; milliseconds = 0.0; targetPercent = 0.0; round = 0; indexInRound = 0; percentScrolled = 0.0 }
    for ($round = 1; $round -le $Rounds; $round++) {
        $current = 0.0
        for ($i = 1; $i -le [int][Math]::Floor(100 / $StepPercent); $i++) {
            $target = [Math]::Min(100.0, $current + $StepPercent)
            if ($target -le $current) { break }
            $clock = [Diagnostics.Stopwatch]::StartNew()
            $scroll.SetScrollPercent(-1, $target)
            $setMs = $clock.Elapsed.TotalMilliseconds
            # Two synchronous waits on the UI thread. The scroll call marshals onto
            # the WPF dispatcher and returns when that thread is done, and the
            # WM_NULL round trip then blocks for whatever render, realize, decode
            # and layout work is still queued, so a stall is wait time, not silence.
            $pingUs = [ScrollProbe]::Ping($app.MainWindowHandle, $StepTimeoutMs)
            $observed = [double]$scroll.Current.VerticalScrollPercent
            $elapsed = $setMs + ($pingUs / 1000.0)
            $timedOut = $pingUs -ge ($StepTimeoutMs * 1000)
            if ($timedOut) { $timeouts++ }
            $stepIndex++
            $scrolledPercent = [Math]::Max(0.0, $observed - $current)
            $itemsThisStep = $catalogItems * ($scrolledPercent / 100.0)
            $itemsTraversed += $itemsThisStep
            $realized = $list.FindAll([Windows.Automation.TreeScope]::Children, $rowCondition).Count
            if ($realized -gt $realizedPeak) { $realizedPeak = $realized }
            if ($elapsed -gt $BudgetMs) { $overBudget++ }
            if ($elapsed -gt $worst.milliseconds) {
                $worst = [ordered]@{ step = $stepIndex; milliseconds = $elapsed; targetPercent = $target; round = $round; indexInRound = $i; percentScrolled = $scrolledPercent }
            }
            $steps.Add([pscustomobject]([ordered]@{
                step = $stepIndex; round = $round; indexInRound = $i
                targetPercent = $target; observedPercent = $observed; percentScrolled = $scrolledPercent
                setCallMs = [Math]::Round($setMs, 3); uiThreadWaitMs = [Math]::Round($pingUs / 1000.0, 3)
                milliseconds = [Math]::Round($elapsed, 3); overBudget = ($elapsed -gt $BudgetMs)
                realizedContainers = $realized; timedOut = $timedOut
            }))
            $current = $observed
            if ($current -ge 99.5) {
                # Wrap to the top so later rounds travel cold covers and a short
                # tail, which is where stalls live. The rewind is timed on its own
                # and reported separately, then the UI thread is drained so the next
                # scored step is not charged for the rewind it never asked for.
                $rewindClock = [Diagnostics.Stopwatch]::StartNew()
                $scroll.SetScrollPercent(-1, 0)
                [void][ScrollProbe]::Ping($app.MainWindowHandle, $StepTimeoutMs)
                $rewinds.Add([double]$rewindClock.Elapsed.TotalMilliseconds)
                Start-Sleep -Milliseconds 200
                $current = 0
            }
        }
    }
    $wallSeconds = $measuredClock.Elapsed.TotalSeconds
    $latencies = @($steps | ForEach-Object { [double]$_.milliseconds })
    $p50 = Percentile $latencies 50
    $p95 = Percentile $latencies 95
    $p99 = Percentile $latencies 99
    $max = if ($latencies.Count -gt 0) { [double]($latencies | Measure-Object -Maximum).Maximum } else { 0.0 }
    $mean = if ($latencies.Count -gt 0) { [double]($latencies | Measure-Object -Average).Average } else { 0.0 }
    $overBudgetRatio = if ($steps.Count -gt 0) { $overBudget / [double]$steps.Count } else { 1.0 }
    $throughput = if ($wallSeconds -gt 0) { $itemsTraversed / $wallSeconds } else { 0.0 }
    $totalPercent = if ($steps.Count -gt 0) { [double]($steps | Measure-Object -Property percentScrolled -Sum).Sum } else { 0.0 }
    $overBudgetMax = if ($overBudget -gt 0) { $max } else { 0.0 }

    Add-Check 'long-catalog' ($catalogItems -ge $MinCatalogItems) ("identityCards=$catalogItems required=$MinCatalogItems viewport=$viewport")
    Add-Check 'scrollable-range' ($viewSizePercent -gt 0 -and $viewSizePercent -lt 5) ("verticalViewSizePercent=$([Math]::Round($viewSizePercent,4)) (a long list shows well under 5% of its content at once)")
    Add-Check 'measured-step-count' ($steps.Count -ge $MinMeasuredSteps) ("steps=$($steps.Count) required=$MinMeasuredSteps rounds=$Rounds stepPercent=$StepPercent")
    Add-Check 'no-scroll-step-timeout' ($timeouts -eq 0) ("timedOutSteps=$timeouts stepTimeoutMs=$StepTimeoutMs")
    Add-Check 'p95-within-frame-budget' ($p95 -le $BudgetMs) ("p95Ms=$([Math]::Round($p95,3)) budgetMs=$BudgetMs harnessFloorP95Ms=$([Math]::Round($floorMs,3))")
    Add-Check 'over-budget-ratio-within-threshold' ($overBudgetRatio -le $MaxOverBudgetRatio) ("overBudget=$overBudget/$($steps.Count) ratio=$([Math]::Round($overBudgetRatio,4)) allowed=$MaxOverBudgetRatio")
    Add-Check 'no-freeze-beyond-ceiling' ($max -le $FreezeCeilingMs) ("maxMs=$([Math]::Round($max,3)) ceilingMs=$FreezeCeilingMs worstStep=$($worst.step)")

    $measurement = [ordered]@{
        stepMethod = 'UI Automation ScrollPattern.SetScrollPercent on GameList, fixed percent steps. One step = the synchronous scroll call (marshals onto the WPF dispatcher and returns when that thread finished the scroll, so it includes realize and layout) plus a WM_NULL/SMTO_ABORTIFHUNG round trip that blocks for any render, cover decode or layout work still queued behind it. Both are waits on the UI thread, so a stall is measured as time, not as silence.'
        harnessFloorP95Ms = [Math]::Round($floorMs, 3)
        harnessFloorMaxMs = [Math]::Round($floorMax, 3)
        budgetMs = $BudgetMs; maxOverBudgetRatio = $MaxOverBudgetRatio; freezeCeilingMs = $FreezeCeilingMs
        rounds = $Rounds; stepPercent = $StepPercent; viewport = $viewport
        catalogIdentityCards = $catalogItems; verticalViewSizePercent = [Math]::Round($viewSizePercent, 4)
        realizedContainersPeak = $realizedPeak
        measuredSteps = $steps.Count; measuredWallSeconds = [Math]::Round($wallSeconds, 3)
        percentScrolledTotal = [Math]::Round($totalPercent, 3)
        itemsTraversed = [Math]::Round($itemsTraversed, 1)
        itemsRealizedPerSecond = [Math]::Round($throughput, 2)
        stepMilliseconds = [ordered]@{
            mean = [Math]::Round($mean, 3); p50 = [Math]::Round($p50, 3); p95 = [Math]::Round($p95, 3); p99 = [Math]::Round($p99, 3); max = [Math]::Round($max, 3)
        }
        worstStep = [ordered]@{
            step = $worst.step; milliseconds = [Math]::Round($worst.milliseconds, 3); targetPercent = $worst.targetPercent
            percentScrolled = [Math]::Round($worst.percentScrolled, 4); round = $worst.round; indexInRound = $worst.indexInRound
        }
        frameDrops = [ordered]@{
            budgetMs = $BudgetMs; overBudgetSteps = $overBudget; measuredSteps = $steps.Count
            overBudgetPercent = [Math]::Round($overBudgetRatio * 100, 3); anyStepOverBudget = ($overBudget -gt 0)
            worstOverBudgetStepMs = [Math]::Round($overBudgetMax, 3)
        }
        rewindToTop = [ordered]@{
            count = $rewinds.Count
            p50Milliseconds = [Math]::Round((Percentile $rewinds.ToArray() 50), 3)
            maxMilliseconds = if ($rewinds.Count -gt 0) { [Math]::Round(([double]($rewinds | Measure-Object -Maximum).Maximum), 3) } else { 0 }
            note = 'each round returns to the top of the list; the rewind is timed separately and is not charged to a scroll step'
        }
        warmup = [ordered]@{
            steps = $warmup.Count
            p95Milliseconds = [Math]::Round((Percentile $warmup.ToArray() 95), 3)
            maxMilliseconds = if ($warmup.Count -gt 0) { [Math]::Round(([double]($warmup | Measure-Object -Maximum).Maximum), 3) } else { 0 }
            note = 'discarded first-realize pass, reported for transparency'
        }
        steps = $steps.ToArray()
    }
}
catch {
    $failures.Add('harness: ' + $_.Exception.Message)
    $checks.Add([ordered]@{ name = 'harness-completed'; passed = $false; detail = $_.Exception.Message })
    $measurement = [ordered]@{ error = $_.Exception.Message; message = $_.Exception.ToString() }
}
finally {
    if ($null -ne $app) {
        # Only the exact process this harness started is ever closed.
        try { if (-not $app.HasExited) { $app.CloseMainWindow() | Out-Null } } catch { }
        Start-Sleep -Seconds 2
        try { if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue } } catch { }
        try { $app.Dispose() } catch { }
    }
}

$passed = $failures.Count -eq 0
$report = [ordered]@{
    schemaVersion = 1; runId = $runId; testName = 'scroll-smoothness'
    startedUtc = $started.ToString('o'); finishedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    executablePath = $ExePath; executableSha256 = $executableSha256; profilePath = $dataRoot
    driveMode = 'UI Automation against the packaged WPF window in --offline mode with an isolated profile under evidence, the same invocation shape as measure-search.ps1'
    uiTestNote = 'The scroll phase deliberately does not pass --ui-test: Program.TestReport makes MainWindow run its own self-driven proof and then Close(), so the window no longer exists to scroll. The bundled offline catalog is the long list under test and its size is read from the app status line.'
    passed = $passed; checks = $checks.ToArray(); failures = $failures.ToArray()
    measurement = $measurement
    externalDependencies = @('an interactive Windows desktop session for UI Automation')
    artifacts = @($ReportPath, $dataRoot)
}
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $ReportPath -Encoding UTF8

$stepCount = $steps.Count
$overSteps = @($steps | Where-Object { $_.overBudget })
Write-Output ''
Write-Output 'SCROLL SMOOTHNESS SUMMARY'
Write-Output '======================='
'{0,-28} {1}' -f 'runId', $runId
'{0,-28} {1}' -f 'executable', $ExePath
'{0,-28} {1}' -f 'executableSha256', $executableSha256
'{0,-28} {1}' -f 'catalogIdentityCards', $catalogItems
'{0:-28} {1}' -f 'verticalViewSizePercent', ([Math]::Round($viewSizePercent, 4))
'{0,-28} {1}' -f 'realizedContainersPeak', $realizedPeak
'{0,-28} {1}' -f 'measuredSteps', $stepCount
if ($stepCount -gt 0) {
    '{0,-28} {1}' -f 'itemsRealizedPerSecond', $measurement.itemsRealizedPerSecond
    '{0,-28} {1}' -f 'stepP50Ms', $measurement.stepMilliseconds.p50
    '{0:-28} {1}' -f 'stepP95Ms', $measurement.stepMilliseconds.p95
    '{0,-28} {1}' -f 'stepP99Ms', $measurement.stepMilliseconds.p99
    '{0,-28} {1}' -f 'stepMaxMs', $measurement.stepMilliseconds.max
    '{0,-28} {1}' -f 'worstStepMs', $measurement.worstStep.milliseconds
    '{0,-28} {1}' -f 'stepsOver16_7ms', $overSteps.Count
    '{0,-28} {1}' -f 'stepsOverBudgetPercent', $measurement.frameDrops.overBudgetPercent
    '{0,-28} {1}' -f 'budgetMs', $BudgetMs
    '{0,-28} {1}' -f 'harnessFloorP95Ms', $measurement.harnessFloorP95Ms
}
'{0,-28} {1}' -f 'passed', $passed
'{0,-28} {1}' -f 'report', $ReportPath
Write-Output ''
foreach ($check in $checks) { '{0,-5} {1,-34} {2}' -f ($(if ($check.passed) { 'PASS' } else { 'FAIL' })), $check.name, $check.detail }
Write-Output ''
Write-Output 'SLOWEST STEPS'
Write-Output '-------------'
if ($overSteps.Count -eq 0) { Write-Output 'no step exceeded the frame budget' }
else { @($steps | Sort-Object -Property milliseconds -Descending | Select-Object -First 15) | Format-Table step, round, indexInRound, targetPercent, percentScrolled, setCallMs, uiThreadWaitMs, milliseconds, realizedContainers -AutoSize | Out-String -Width 220 | Write-Output }
Write-Output ('REPORT=' + $ReportPath)
if (-not $passed) { exit 1 }
exit 0