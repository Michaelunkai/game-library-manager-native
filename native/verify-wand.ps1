param(
    [string]$ExePath = (Join-Path $PSScriptRoot 'dist\GameLibrary.exe'),
    [string]$GameSearch = 'WizardwithaGun',
    [string]$GameTitle = 'Wizardwitha Gun',
    [string]$InstalledGame = 'E:\games\WizardwithaGun\wizardwithagun.exe',
    [string]$DataRoot = ''
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$evidenceRoot = Join-Path $PSScriptRoot 'evidence'
[IO.Directory]::CreateDirectory($evidenceRoot) | Out-Null
$report = Join-Path $evidenceRoot 'wand-native-current.json'
$checks = New-Object 'System.Collections.Generic.List[object]'
$app = $null
$game = $null
$wand = @()
function Add-Check([string]$Name, [bool]$Passed) {
    $checks.Add([pscustomobject]@{ name=$Name; passed=$Passed; at=[DateTime]::UtcNow.ToString('o') })
    if (-not $Passed) { throw ('Verification failed: ' + $Name) }
}
function Find-ById($Root, [string]$Id) {
    $condition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    return $Root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}
function Find-Button($Root, [string]$Name) {
    $condition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button)
    $buttons = $Root.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)
    foreach ($button in $buttons) {
        if ($button.Current.Name -eq $Name -or $button.Current.Name.StartsWith($Name, [StringComparison]::Ordinal)) { return $button }
    }
    return $null
}
function Invoke-Control($Control) { ([Windows.Automation.InvokePattern]$Control.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)).Invoke() }
function Wait-AppWindow {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $app.Refresh()
        if ($app.HasExited) { throw 'Native app exited during Wand proof.' }
        if ($app.MainWindowHandle -ne [IntPtr]::Zero) {
            $candidate = [Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
            $status = Find-ById $candidate 'StatusText'
            # The WPF shell is exposed before InitializeAsync has loaded the
            # catalog. Do not start the search race until the same window says
            # the offline/online catalog is ready.
            if ((Find-ById $candidate 'SearchBox') -and (Find-ById $candidate 'GameList') -and
                $status -and $status.Current.Name -match '(?i)catalog ready') { return $candidate }
        }
        Start-Sleep -Milliseconds 150
    } while ($clock.Elapsed.TotalSeconds -lt 120)
    throw 'Native window did not become accessible.'
}
function Wait-GameRow($Window) {
    $list = Find-ById $Window 'GameList'
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $rows = $list.FindAll([Windows.Automation.TreeScope]::Children, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem)))
        if ($rows.Count -eq 1) { return $rows[0] }
        Start-Sleep -Milliseconds 150
    } while ($clock.Elapsed.TotalSeconds -lt 15)
    throw ($GameSearch + ' did not resolve to one native row.')
}
function Wait-DetailsDialog([int]$ProcessId, [string]$Title) {
    $root = [Windows.Automation.AutomationElement]::RootElement
    $windowCondition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Window)
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        # WPF owned modal windows are not consistently exposed as direct RootElement
        # children or as descendants of the owner window. Search the process's full
        # UIA window tree so the proof follows the same dialog a user sees.
        $windows = $root.FindAll([Windows.Automation.TreeScope]::Descendants, $windowCondition) | Where-Object { $_.Current.ProcessId -eq $ProcessId }
        foreach ($window in $windows) {
            if ($window.Current.Name -eq $Title) { return $window }
        }
        $candidate = $script:proofOwner.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $Title)))
        if ($null -ne $candidate -and $candidate.Current.ControlType -eq [Windows.Automation.ControlType]::Window) { return $candidate }
        Start-Sleep -Milliseconds 120
    } while ($clock.Elapsed.TotalSeconds -lt 10)
    throw ($GameSearch + ' details dialog did not open.')
}
function Find-ExactProcess([string]$Path) {
    $name = [IO.Path]::GetFileNameWithoutExtension($Path)
    foreach ($candidate in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
        try { if ($candidate.MainModule.FileName -ieq $Path) { return $candidate } } catch {}
        $candidate.Dispose()
    }
    return $null
}
function Find-FreshConnectionLog([string]$Executable, [int]$ExpectedPid, [DateTime]$SinceUtc) {
    $overlayRoot = Join-Path $env:LOCALAPPDATA 'Wand\logs\overlay'
    if (-not (Test-Path -LiteralPath $overlayRoot -PathType Container)) { return $null }
    $gameStem = [IO.Path]::GetFileNameWithoutExtension($Executable) -replace '[^a-z0-9]', ''
    foreach ($candidate in @(Get-ChildItem -LiteralPath $overlayRoot -Filter '*.log' -File -ErrorAction SilentlyContinue)) {
        $candidateStem = [IO.Path]::GetFileNameWithoutExtension($candidate.Name) -replace '[^a-z0-9]', ''
        if ($candidateStem -ne $gameStem -or $candidate.LastWriteTimeUtc -lt $SinceUtc.AddSeconds(-1)) { continue }
        try {
            $tail = Get-Content -LiteralPath $candidate.FullName -Raw -ErrorAction Stop
            $pidLines = @($tail -split "`r?`n" | Where-Object { $_.Contains('[' + $ExpectedPid + ':') })
            $hasIpc = @($pidLines | Where-Object { $_ -match '(?i)ipc connected' }).Count -gt 0
            $hasHook = @($pidLines | Where-Object { $_ -match '(?i)(hooked: true|hook res: true)' }).Count -gt 0
            if ($hasIpc -and $hasHook) { return $candidate.FullName }
        } catch {}
    }
    return $null
}
try {
    $installedGame = [IO.Path]::GetFullPath($InstalledGame)
    if (-not (Test-Path -LiteralPath $installedGame -PathType Leaf)) { throw 'The installed Wand proof game executable is missing.' }
    $gameProcessName = [IO.Path]::GetFileNameWithoutExtension($installedGame)
    foreach ($name in @($gameProcessName,'Wand','WeMod')) { Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue }
    $appArguments = @('--offline')
    if (-not [string]::IsNullOrWhiteSpace($DataRoot)) { $appArguments += @('--data-dir', [IO.Path]::GetFullPath($DataRoot)) }
    $app = Start-Process -FilePath (Resolve-Path $ExePath).Path -ArgumentList $appArguments -PassThru
    $window = Wait-AppWindow
    Add-Check 'Rebuilt native WPF app exposes a ready game list' ($window.Current.ProcessId -eq $app.Id)
    $search = Find-ById $window 'SearchBox'
    ([Windows.Automation.ValuePattern]$search.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).SetValue($GameSearch)
    $row = Wait-GameRow $window
    $detail = Find-Button $row 'Details'
    if ($null -eq $detail) { throw 'Wizard details action was not exposed.' }
    Start-Sleep -Milliseconds 750
    Invoke-Control $detail
    $script:proofOwner = $window
    $dialog = Wait-DetailsDialog $app.Id $GameTitle
    $wandButton = Find-Button $dialog 'Play with Wand mods'
    if ($null -eq $wandButton) { throw 'Play with Wand mods action was not exposed.' }
    Add-Check 'Wizard details exposes the Wand launch action' $true
    $wandActionAt = [DateTime]::UtcNow
    Invoke-Control $wandButton
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        $game = Find-ExactProcess $installedGame
        $wand = @(Get-Process -Name Wand,WeMod -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero })
        if ($game -and $wand.Count -gt 0) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    Add-Check 'Wand and the exact installed game start from one native action' ($null -ne $game -and $wand.Count -gt 0)
    Add-Check 'Native Wand handoff leaves a visible Wand window' ($wand.Count -gt 0 -and ($wand | Where-Object { $_.MainWindowTitle.Length -gt 0 }).Count -gt 0)
    $connectionLog = $null
    $connectionDeadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        $connectionLog = Find-FreshConnectionLog $installedGame $game.Id $wandActionAt
        if ($null -ne $connectionLog) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $connectionDeadline)
    Add-Check 'Wand overlay log confirms a fresh IPC and hook connection for the exact game' ($null -ne $connectionLog)
    $payload = [pscustomobject]@{ at=[DateTime]::UtcNow.ToString('o'); passed=$true; executable=(Resolve-Path $ExePath).Path; sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $ExePath).Hash; gameSearch=$GameSearch; gameTitle=$GameTitle; gamePath=$installedGame; gamePid=$game.Id; wandPids=@($wand | Select-Object -ExpandProperty Id); connectionLog=$connectionLog; checks=@($checks.ToArray()) }
    $payload | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $report -Encoding UTF8
    Write-Output ('PASS: Wand native proof; game PID ' + $game.Id + '; Wand processes ' + (($wand | Select-Object -ExpandProperty Id) -join ',') + '; evidence=' + $report)
}
catch {
    [pscustomobject]@{ at=[DateTime]::UtcNow.ToString('o'); passed=$false; executable=$ExePath; error=$_.ToString(); checks=@($checks.ToArray()) } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $report -Encoding UTF8
    throw
}
finally {
    if ($null -ne $game -and -not $game.HasExited) { Stop-Process -Id $game.Id -Force -ErrorAction SilentlyContinue }
    foreach ($process in @(Get-Process -Name Wand,WeMod -ErrorAction SilentlyContinue)) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    if ($null -ne $app -and -not $app.HasExited) {
        try { $app.CloseMainWindow() | Out-Null; $app.WaitForExit(5000) | Out-Null } catch {}
        if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    }
}
