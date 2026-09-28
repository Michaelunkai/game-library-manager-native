param([string]$ExePath = (Join-Path $PSScriptRoot 'dist\GameLibrary.exe'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$evidenceRoot = Join-Path $PSScriptRoot 'evidence'
$dataRoot = Join-Path $evidenceRoot ('startup-timing-' + (Get-Date -Format yyyyMMdd-HHmmss))
[IO.Directory]::CreateDirectory($dataRoot) | Out-Null

# Warm up once so asset extraction is not counted (steady-state startup).
$warm = Start-Process -FilePath (Resolve-Path $ExePath).Path -ArgumentList @('--data-dir',$dataRoot,'--offline') -PassThru
Start-Sleep -Seconds 4
if (-not $warm.HasExited) { try { $warm.CloseMainWindow() | Out-Null } catch {}; Start-Sleep -Seconds 2; if (-not $warm.HasExited) { Stop-Process -Id $warm.Id -Force -ErrorAction SilentlyContinue } }
Start-Sleep -Seconds 2

$clock = [Diagnostics.Stopwatch]::StartNew()
$app = Start-Process -FilePath (Resolve-Path $ExePath).Path -ArgumentList @('--data-dir',$dataRoot,'--offline') -PassThru
$windowMs = -1
$readyMs = -1
$firstWindowSeen = $false
do {
    Start-Sleep -Milliseconds 150
    $app.Refresh()
    if ($app.MainWindowHandle -ne [IntPtr]::Zero -and -not $firstWindowSeen) { $firstWindowSeen = $true; $windowMs = $clock.ElapsedMilliseconds }
    if ($firstWindowSeen) {
        try {
            $candidate = [Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
            $search = $candidate.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty,'SearchBox')))
            $list = $candidate.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty,'GameList')))
            if ($search -and $list) {
                $rows = $list.FindAll([Windows.Automation.TreeScope]::Children, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem)))
                if ($rows.Count -gt 0) { $readyMs = $clock.ElapsedMilliseconds; break }
            }
        } catch { }
    }
} while ($clock.Elapsed.TotalSeconds -lt 60)

Write-Output ("WINDOW_MS=" + $windowMs)
Write-Output ("READY_MS=" + $readyMs)
Write-Output ("DATA_ROOT=" + $dataRoot)

# Clean up
if (-not $app.HasExited) { try { $app.CloseMainWindow() | Out-Null } catch {}; Start-Sleep -Seconds 2; if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue } }