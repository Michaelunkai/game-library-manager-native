param([string]$ExePath = (Join-Path $PSScriptRoot 'dist\GameLibrary.exe'))
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
$job = $null
$terminalProcessIds = @()
$terminalHostProcessIds = @()
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
    } while ($clock.Elapsed.TotalSeconds -lt 25)
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
    } while ($clock.Elapsed.TotalSeconds -lt 15)
    throw ('Native dialog did not open: ' + $Title)
}
function Find-Button($Root, [string]$Name) {
    $condition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Button)
    foreach ($button in $Root.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)) {
        if ($button.Current.Name -eq $Name -or $button.Current.Name.StartsWith($Name, [StringComparison]::Ordinal)) { return $button }
    }
    throw ('Missing native button: ' + $Name)
}
function Text-Descendants($Root) {
    return (($Root.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name }) -join "`n")
}
function Get-ProcessSnapshot([string]$Name) {
    @(Get-Process -Name $Name -ErrorAction SilentlyContinue | ForEach-Object {
        try { [pscustomobject]@{ Id=$_.Id; StartTime=$_.StartTime; MainWindowHandle=$_.MainWindowHandle; MainWindowTitle=$_.MainWindowTitle } } catch { }
    })
}
try {
    Get-Process -Name GameLibrary -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    [IO.Directory]::CreateDirectory($dataRoot) | Out-Null
    $app = Start-Process -FilePath (Resolve-Path $ExePath).Path -ArgumentList @('--data-dir',$dataRoot,'--offline') -PassThru
    $window = Wait-AppWindow
    Add-Check 'Rebuilt WPF EXE exposes the install controls' ($window.Current.ProcessId -eq $app.Id -and $null -ne (Find-Id $window 'InstallSelected'))
    $search = Find-Id $window 'SearchBox'
    ([Windows.Automation.ValuePattern]$search.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).SetValue('staroceanthedivineforce')
    Start-Sleep -Milliseconds 600
    Invoke-Control (Find-Id $window 'SelectAll')
    Invoke-Control (Find-Id $window 'InstallSelected')
    $review = Wait-Dialog 'Install selected games' $window
    $reviewText = Text-Descendants $review
    Add-Check 'Install review names the Windows BAT and WSL2 install routes' ($reviewText -match '(?i)Windows BAT' -and $reviewText -match '(?i)default terminal' -and $reviewText -match '(?i)WSL2 Ubuntu' -and $reviewText -match '(?i)\.SH')
    $beforeCmd = @(Get-ProcessSnapshot 'cmd')
    $beforeHost = @(Get-ProcessSnapshot 'conhost')
    $actionAt = [DateTime]::Now
    Invoke-Control (Find-Button $review 'Start download')
    $job = Wait-Dialog ('Game Library ' + [char]0xB7 + ' Download terminal') $window
    $deadline = [DateTime]::UtcNow.AddSeconds(12)
    $bat = $null
    $cmd = $null
    do {
        $bat = Get-ChildItem -LiteralPath (Join-Path $dataRoot 'jobs') -Filter '*.bat' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        $after = @(Get-ProcessSnapshot 'cmd' | Where-Object { $beforeCmd.Id -notcontains $_.Id -and $_.StartTime -ge $actionAt.AddSeconds(-2) })
        $cmd = $after | Select-Object -First 1
        if ($bat -and $cmd) { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    Add-Check 'Native install writes a BAT job and hands it to cmd.exe' ($null -ne $bat -and $null -ne $cmd)
    $terminalProcessIds = @($cmd.Id)
    $terminalHosts = @(Get-ProcessSnapshot 'conhost' | Where-Object { $beforeHost.Id -notcontains $_.Id -and $_.StartTime -ge $actionAt.AddSeconds(-2) })
    $terminalHostProcessIds = @($terminalHosts | Select-Object -ExpandProperty Id)
    $jobClock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $jobText = Text-Descendants $job
        if ($jobText -match '(?i)Running in your default terminal') { break }
        Start-Sleep -Milliseconds 100
    } while ($jobClock.Elapsed.TotalSeconds -lt 5)
    Add-Check 'Native job window reports the user default terminal route' ($jobText -match '(?i)Running in your default terminal')
    $batText = if ($bat) { Get-Content -LiteralPath $bat.FullName -Raw } else { '' }
    Add-Check 'Generated BAT contains the reviewed PowerShell payload' ($batText.StartsWith('@echo off') -and $batText.Contains('# GLM_POWERSHELL_START'))
    $terminalVisible = ($cmd.MainWindowHandle -ne [IntPtr]::Zero) -or (@($terminalHosts | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero -or $_.MainWindowTitle.Length -gt 0 }).Count -gt 0)
    Add-Check 'Default terminal handoff creates an external console host' ($terminalProcessIds.Count -gt 0 -and $terminalHostProcessIds.Count -gt 0)
    $payload = [pscustomobject]@{ at=[DateTime]::UtcNow.ToString('o'); passed=$true; executable=(Resolve-Path $ExePath).Path; sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $ExePath).Hash; dataRoot=$dataRoot; batPath=if($bat){$bat.FullName}else{$null}; terminalProcessId=if($cmd){$cmd.Id}else{$null}; terminalProcessName='cmd.exe'; terminalHostProcessIds=$terminalHostProcessIds; terminalVisible=$terminalVisible; checks=@($checks.ToArray()) }
    $payload | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $report -Encoding UTF8
    Write-Output ('PASS: Native install opened the default terminal route; evidence=' + $report)
}
catch {
    [pscustomobject]@{ at=[DateTime]::UtcNow.ToString('o'); passed=$false; executable=$ExePath; dataRoot=$dataRoot; error=$_.ToString(); checks=@($checks.ToArray()) } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $report -Encoding UTF8
    throw
}
finally {
    foreach ($id in @($terminalProcessIds + $terminalHostProcessIds)) { if ($id) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue } }
    if ($null -ne $job) { try { ([Windows.Automation.WindowPattern]$job.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)).Close() } catch {} }
    if ($null -ne $app -and -not $app.HasExited) {
        try { $app.CloseMainWindow() | Out-Null; $app.WaitForExit(5000) | Out-Null } catch {}
        if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    }
}
