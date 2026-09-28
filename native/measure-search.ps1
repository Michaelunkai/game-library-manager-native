param([string]$ExePath = (Join-Path $PSScriptRoot 'dist\GameLibrary.exe'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$evidenceRoot = Join-Path $PSScriptRoot 'evidence'
$dataRoot = Join-Path $evidenceRoot ('search-timing-' + (Get-Date -Format yyyyMMdd-HHmmss))
[IO.Directory]::CreateDirectory($dataRoot) | Out-Null

$warm = Start-Process -FilePath (Resolve-Path $ExePath).Path -ArgumentList @('--data-dir',$dataRoot,'--offline') -PassThru
Start-Sleep -Seconds 4
if (-not $warm.HasExited) { try { $warm.CloseMainWindow() | Out-Null } catch {}; Start-Sleep -Seconds 2; if (-not $warm.HasExited) { Stop-Process -Id $warm.Id -Force -ErrorAction SilentlyContinue } }
Start-Sleep -Seconds 2

$app = Start-Process -FilePath (Resolve-Path $ExePath).Path -ArgumentList @('--data-dir',$dataRoot,'--offline') -PassThru
$window = $null
$deadline = [DateTime]::UtcNow.AddSeconds(40)
while ([DateTime]::UtcNow -lt $deadline -and -not $window) {
    Start-Sleep -Milliseconds 150
    $app.Refresh()
    if ($app.MainWindowHandle -ne [IntPtr]::Zero) {
        $candidate = [Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
        $list = $candidate.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty,'GameList')))
        if ($list) {
            $rows = $list.FindAll([Windows.Automation.TreeScope]::Children, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem)))
            if ($rows.Count -gt 0) { $window = $candidate }
        }
    }
}
if (-not $window) { Write-Output "READY=NO"; exit 1 }
Write-Output "READY=YES"

# Time a search keystroke -> filtered list update.
$search = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty,'SearchBox')))
$list = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty,'GameList')))
$before = $list.FindAll([Windows.Automation.TreeScope]::Children, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem))).Count
$clock = [Diagnostics.Stopwatch]::StartNew()
([Windows.Automation.ValuePattern]$search.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).SetValue('the')
$targetCount = -1
do {
    Start-Sleep -Milliseconds 50
    $targetCount = $list.FindAll([Windows.Automation.TreeScope]::Children, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem))).Count
} while ($targetCount -eq $before -and $clock.ElapsedMilliseconds -lt 10000)
Write-Output ("SEARCH_MS=" + $clock.ElapsedMilliseconds + " from=" + $before + " to=" + $targetCount)

if (-not $app.HasExited) { try { $app.CloseMainWindow() | Out-Null } catch {}; Start-Sleep -Seconds 2; if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue } }