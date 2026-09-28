param([string]$ExePath = (Join-Path $PSScriptRoot 'dist\GameLibrary.exe'), [int]$ProgressSeconds = 30, [switch]$IncludeControlAck, [string]$Game = 'staroceanthedivineforce', [int]$WatchSeconds = 0)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$evidenceRoot = Join-Path $PSScriptRoot 'evidence'
[IO.Directory]::CreateDirectory($evidenceRoot) | Out-Null
$stamp = Get-Date -Format yyyyMMdd-HHmmss
$dataRoot = Join-Path $evidenceRoot ('terminal-native-' + $stamp)
$report = Join-Path $evidenceRoot 'terminal-native-current.json'
$checks = New-Object 'System.Collections.Generic.List[object]'
$app = $null
function Add-Check([string]$Name, [bool]$Passed) {
    $checks.Add([pscustomobject]@{ name=$Name; passed=$Passed; at=[DateTime]::UtcNow.ToString('o') })
    if (-not $Passed) { throw ('Verification failed: ' + $Name) }
}
function Find-Id($Root, [string]$Id) {
    $condition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $control = $Root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $control) { throw ('Missing native control: ' + $Id) }
    return $control
}
function Invoke-Control($Control) { ([Windows.Automation.InvokePattern]$Control.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)).Invoke() }
function Text-Descendants($Root) {
    return (($Root.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name }) -join "`n")
}
function Wait-AppWindow {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $app.Refresh()
        if ($app.HasExited) { throw 'Native app exited during terminal proof.' }
        if ($app.MainWindowHandle -ne [IntPtr]::Zero) {
            $candidate = [Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
            $search = $candidate.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty,'SearchBox')))
            $list = $candidate.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty,'GameList')))
            if ($search -and $list) {
                $rows = $list.FindAll([Windows.Automation.TreeScope]::Children, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem)))
                if ($rows.Count -gt 0) { return $candidate }
            }
        }
        Start-Sleep -Milliseconds 150
    } while ($clock.Elapsed.TotalSeconds -lt 40)
    throw 'Native window did not become accessible.'
}
function Wait-Dialog([string]$Title, [Windows.Automation.AutomationElement]$Owner) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $roots = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id)))
        foreach ($candidate in $roots) { if ($candidate.Current.Name -eq $Title) { return $candidate } }
        if ($Owner) {
            $candidate = $Owner.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty,$Title)))
            if ($candidate -and $candidate.Current.ControlType -eq [Windows.Automation.ControlType]::Window) { return $candidate }
        }
        Start-Sleep -Milliseconds 100
    } while ($clock.Elapsed.TotalSeconds -lt 20)
    throw ('Native dialog did not open: ' + $Title)
}
function Find-Button($Root, [string]$Name) {
    $condition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Button)
    foreach ($button in $Root.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)) {
        if ($button.Current.Name -eq $Name -or $button.Current.Name.StartsWith($Name, [StringComparison]::Ordinal)) { return $button }
    }
    throw ('Missing native button: ' + $Name)
}
function Find-TerminalWindow {
    $roots = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)
    foreach ($candidate in $roots) {
        $name = $candidate.Current.Name
        if ($name -and $name -match '(?i)Game Library' -and $name -match '(?i)Install terminal') { return $candidate }
    }
    return $null
}
try {
    Get-Process -Name GameLibrary -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    [IO.Directory]::CreateDirectory($dataRoot) | Out-Null
    $app = Start-Process -FilePath (Resolve-Path $ExePath).Path -ArgumentList @('--data-dir',$dataRoot,'--offline') -PassThru
    $window = Wait-AppWindow
    Add-Check 'Rebuilt WPF EXE exposes the install controls' ($window.Current.ProcessId -eq $app.Id -and $null -ne (Find-Id $window 'InstallSelected'))
    $search = Find-Id $window 'SearchBox'
    ([Windows.Automation.ValuePattern]$search.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).SetValue($Game)
    $list = Find-Id $window 'GameList'
    $filterClock = [Diagnostics.Stopwatch]::StartNew()
    do {
        Start-Sleep -Milliseconds 100
        $rows = $list.FindAll([Windows.Automation.TreeScope]::Children, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem)))
    } while ($rows.Count -ne 1 -and $filterClock.Elapsed.TotalSeconds -lt 15)
    Add-Check 'Search returns the exact install candidate' ($rows.Count -eq 1)
    Invoke-Control (Find-Id $window 'SelectAll')
    Invoke-Control (Find-Id $window 'InstallSelected')
    $review = Wait-Dialog 'Install selected games' $window
    $reviewText = Text-Descendants $review
    Add-Check 'Install review names the Windows BAT and WSL2 install routes' ($reviewText -match '(?i)Windows BAT' -and $reviewText -match '(?i)default terminal' -and $reviewText -match '(?i)WSL2 Ubuntu' -and $reviewText -match '(?i)\.SH')
    Invoke-Control (Find-Button $review 'Start download')

    # The durable terminal and worker are separate processes launched from the
    # same executable. Wait for the manifest and the console window.
    $jobsRoot = Join-Path $dataRoot 'jobs'
    $manifestClock = [Diagnostics.Stopwatch]::StartNew()
    $jobDirectory = $null
    do {
        if (Test-Path -LiteralPath $jobsRoot) {
            $jobDirectory = Get-ChildItem -LiteralPath $jobsRoot -Directory -ErrorAction SilentlyContinue | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'job.json') } | Select-Object -First 1
        }
        if ($null -eq $jobDirectory) { Start-Sleep -Milliseconds 200 }
    } while ($null -eq $jobDirectory -and $manifestClock.Elapsed.TotalSeconds -lt 30)
    Add-Check 'Durable install manifest was created' ($null -ne $jobDirectory)

    $eventsPath = Join-Path $jobDirectory.FullName 'events.jsonl'
    $terminalWindow = $null
    $windowClock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $terminalWindow = Find-TerminalWindow
        if ($null -eq $terminalWindow) { Start-Sleep -Milliseconds 250 }
    } while ($null -eq $terminalWindow -and $windowClock.Elapsed.TotalSeconds -lt 30)
    Add-Check 'Durable install terminal window is visible' ($null -ne $terminalWindow -and -not $terminalWindow.Current.IsOffscreen)

    # Real-time progress: the event log must keep growing while the job runs.
    function Read-Events {
        if (-not (Test-Path -LiteralPath $eventsPath)) { return @() }
        return @(Get-Content -LiteralPath $eventsPath -ErrorAction SilentlyContinue)
    }
    $before = @(Read-Events)
    $progressClock = [Diagnostics.Stopwatch]::StartNew()
    do {
        Start-Sleep -Milliseconds 500
        $after = @(Read-Events)
    } while ($after.Count -le $before.Count -and $progressClock.Elapsed.TotalSeconds -lt $ProgressSeconds)
    Add-Check 'Durable install streams real-time progress events' ($after.Count -gt $before.Count)

    function Get-Stages([object[]]$Events) {
        return @($Events | ForEach-Object { try { (ConvertFrom-Json $_).stage } catch { } } | Where-Object { $_ } | Select-Object -Unique)
    }
    $stages = @(Get-Stages $after)
    $stageClock = [Diagnostics.Stopwatch]::StartNew()
    while ($stages.Count -lt 2 -and $stageClock.Elapsed.TotalSeconds -lt 120) {
        Start-Sleep -Milliseconds 750
        $after = @(Read-Events)
        $stages = @(Get-Stages $after)
    }
    Add-Check 'Progress advances through real install stages' ($stages.Count -ge 2)

    # A real Docker pull reports byte ratios ("4.0MB/8.0MB"). Wait until at
    # least one command event carries a positive byte total so the terminal can
    # render a live percentage.
    $byteClock = [Diagnostics.Stopwatch]::StartNew()
    $byteEvent = $null
    do {
        $after = @(Read-Events)
        $byteEvent = $after | Where-Object { $_ -match '"completedBytes":[1-9]' -and $_ -match '"totalBytes":[1-9]' } | Select-Object -Last 1
        if ($byteEvent) { break }
        Start-Sleep -Milliseconds 750
    } while ($byteClock.Elapsed.TotalSeconds -lt 150)
    # Recorded, not fatal: a fully cached image reports "Already exists" with no
    # byte ratio. Byte/percent parsing is asserted deterministically in the
    # self-test suite.
    $byteProgressObserved = $null -ne $byteEvent

    if ($WatchSeconds -gt 0) {
        $watchStart = [DateTime]::UtcNow
        while ([DateTime]::UtcNow.Subtract($watchStart).TotalSeconds -lt $WatchSeconds) {
            Start-Sleep -Seconds 3
            $latest = @(Read-Events) | ForEach-Object { try { ($_ | ConvertFrom-Json) } catch {} } |
                Where-Object { $_.stage -eq 'Pull' -and $_.kind -eq 'progress' -and $_.completedBytes -gt 0 -and $_.totalBytes -gt 0 } | Select-Object -Last 1
            if ($latest) {
                $p = [math]::Round(100.0 * $latest.completedBytes / $latest.totalBytes, 3)
                Write-Output ("WATCH " + $p.ToString('0.000') + "% " + $latest.completedBytes + "/" + $latest.totalBytes + " B " + $latest.message)
            } else {
                Write-Output "WATCH waiting for byte progress..."
            }
        }
    }

    # Live pause/resume acknowledgement: write the same control request the UI
    # sends and require the durable worker to answer with a matching identity
    # (the terminal reported "mismatched job identity" before this fix).
    $jobManifest = Get-Content -LiteralPath (Join-Path $jobDirectory.FullName 'job.json') -Raw | ConvertFrom-Json
    if ($IncludeControlAck) {
    $controlDir = Join-Path $jobDirectory.FullName 'controls'
    [IO.Directory]::CreateDirectory($controlDir) | Out-Null
    function Send-Control([string]$Action) {
        $rid = [guid]::NewGuid().ToString('N')
        $body = [pscustomobject]@{ schemaVersion=1; operationId=$jobManifest.operationId; requestId=$rid; action=$Action; requestedUtc=[DateTime]::UtcNow.ToString('o') } | ConvertTo-Json -Compress
        Set-Content -LiteralPath (Join-Path $controlDir ($rid + '.json')) -Value $body -Encoding UTF8
        $responsePath = Join-Path $controlDir ($rid + '.response.json')
        $clock = [Diagnostics.Stopwatch]::StartNew()
        while (-not (Test-Path -LiteralPath $responsePath) -and $clock.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 250 }
        if (-not (Test-Path -LiteralPath $responsePath)) { return $null }
        return Get-Content -LiteralPath $responsePath -Raw | ConvertFrom-Json
    }
    $pauseAck = Send-Control 'pause'
    Add-Check 'Durable worker acknowledges a live pause request' ($null -ne $pauseAck)
    Add-Check 'Pause acknowledgement carries the exact job identity' ($null -ne $pauseAck -and $pauseAck.operationId -eq $jobManifest.operationId -and $pauseAck.accepted -eq $true)
    Start-Sleep -Milliseconds 500
    $resumeAck = Send-Control 'resume'
    Add-Check 'Durable worker acknowledges a live resume request' ($null -ne $resumeAck -and $resumeAck.operationId -eq $jobManifest.operationId -and $resumeAck.accepted -eq $true)
    }

    $job = Get-Content -LiteralPath (Join-Path $jobDirectory.FullName 'job.json') -Raw | ConvertFrom-Json
    Add-Check 'Durable install has not entered a failed state' ($job.status -notin @('failed','retryableFailure'))

    $payload = [pscustomobject]@{ at=[DateTime]::UtcNow.ToString('o'); passed=$true; executable=(Resolve-Path $ExePath).Path; sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $ExePath).Hash; dataRoot=$dataRoot; jobDirectory=$jobDirectory.FullName; status=$job.status; stages=$stages; eventCount=$after.Count; byteProgressObserved=$byteProgressObserved; terminalWindowName=$terminalWindow.Current.Name; checks=@($checks.ToArray()) }
    $payload | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $report -Encoding UTF8
    Write-Output ('PASS: Native durable install terminal opened and streamed progress; evidence=' + $report)
}
catch {
    [pscustomobject]@{ at=[DateTime]::UtcNow.ToString('o'); passed=$false; executable=$ExePath; dataRoot=$dataRoot; error=$_.ToString(); checks=@($checks.ToArray()) } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $report -Encoding UTF8
    throw
}
finally {
    if ($null -ne $jobDirectory) {
        $jobPath = Join-Path $jobDirectory.FullName 'job.json'
        if (Test-Path -LiteralPath $jobPath) {
            try {
                $job = Get-Content -LiteralPath $jobPath -Raw | ConvertFrom-Json
                foreach ($id in @($job.ownedProcess.processId, $job.workerProcess.processId)) {
                    if ($id) { Stop-Process -Id ([int]$id) -Force -ErrorAction SilentlyContinue }
                }
                # A force-killed worker cannot reconcile its Docker child, so stop
                # any docker pull that still references this job's pinned digest.
                if ($job.pinnedDigest) {
                    Get-CimInstance Win32_Process -Filter "Name='docker.exe'" -ErrorAction SilentlyContinue |
                        Where-Object { $_.CommandLine -like ('*' + $job.pinnedDigest + '*') } |
                        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
                }
            } catch { }
        }
        try { ([Windows.Automation.WindowPattern]$terminalWindow.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)).Close() } catch { }
    }
    if ($null -ne $app -and -not $app.HasExited) {
        try { $app.CloseMainWindow() | Out-Null; $app.WaitForExit(5000) | Out-Null } catch {}
        if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    }
}
