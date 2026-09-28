param([string]$ExePath = (Join-Path $PSScriptRoot '..\native\dist\GameLibrary.exe'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WinCap2 {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
'@
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$outDir = Join-Path $root 'docs\images'
[IO.Directory]::CreateDirectory($outDir) | Out-Null
$dataRoot = Join-Path $root 'evidence\screenshot-profile'
if (-not (Test-Path -LiteralPath $dataRoot)) { [IO.Directory]::CreateDirectory($dataRoot) | Out-Null }

function Get-AppWindow {
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while ([DateTime]::UtcNow -lt $deadline) {
        $p = Get-Process GameLibrary -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero -and $_.MainWindowTitle -match 'Game' } | Select-Object -First 1
        if ($p) { try { [WinCap2]::SetForegroundWindow($p.MainWindowHandle) | Out-Null } catch {}; return $p }
        Start-Sleep -Milliseconds 300
    }
    return $null
}
function Capture-Window($Proc, [string]$Path) {
    if (-not $Proc) { throw 'No app window.' }
    Start-Sleep -Milliseconds 900
    $rect = New-Object WinCap2+RECT
    [WinCap2]::GetWindowRect($Proc.MainWindowHandle, [ref]$rect) | Out-Null
    $w = $rect.Right - $rect.Left; $h = $rect.Bottom - $rect.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose(); $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    Write-Output ("CAPTURED " + $Path)
}

$warm = Start-Process -FilePath (Resolve-Path $ExePath).Path -ArgumentList @('--data-dir',$dataRoot,'--offline','--main-monitor') -PassThru
Start-Sleep -Seconds 6
if (-not $warm.HasExited) { try { $warm.CloseMainWindow() | Out-Null } catch {}; Start-Sleep -Seconds 2; if (-not $warm.HasExited) { Stop-Process -Id $warm.Id -Force -ErrorAction SilentlyContinue } }
Start-Sleep -Seconds 2

$app = Start-Process -FilePath (Resolve-Path $ExePath).Path -ArgumentList @('--data-dir',$dataRoot,'--offline','--main-monitor') -PassThru
Start-Sleep -Seconds 6
$w = Get-AppWindow
Capture-Window $w (Join-Path $outDir 'library.png')

# Filtered view: type a query so the list shows a manageable subset.
$candidate = [Windows.Automation.AutomationElement]::FromHandle($w.MainWindowHandle)
$search = $candidate.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty,'SearchBox')))
if ($search) {
    ([Windows.Automation.ValuePattern]$search.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).SetValue('star ocean')
    Start-Sleep -Seconds 2
    Capture-Window $w (Join-Path $outDir 'search.png')
    ([Windows.Automation.ValuePattern]$search.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).SetValue('')
    Start-Sleep -Seconds 1
}
try { if (-not $app.HasExited) { $app.CloseMainWindow() | Out-Null } } catch {}
Start-Sleep -Seconds 2
if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
Write-Output "DONE"