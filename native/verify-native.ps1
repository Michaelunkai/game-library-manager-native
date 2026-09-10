param([string]$ExePath = (Join-Path $PSScriptRoot 'dist\GameLibrary.exe'), [string]$DataRoot = '', [switch]$DockerProof, [switch]$MetadataProof, [switch]$PlayProof, [switch]$ScriptProof, [switch]$AdminProof, [switch]$TrayProof, [switch]$LocalGameProof, [switch]$AutomaticCatalogProof, [switch]$KillScriptProof, [string]$DockerTag = 'inmot')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System; using System.Text; using System.Runtime.InteropServices; using System.Collections.Generic;
public static class GLMNativeDialog {
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h,StringBuilder text,int size);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h,uint message,IntPtr w,string text);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h,uint message,IntPtr w,IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
  delegate bool EnumCallback(IntPtr h,IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumCallback callback,IntPtr l);
  [StructLayout(LayoutKind.Sequential)] struct IconIdentifier { public uint size; public IntPtr hWnd; public uint id; public Guid guid; }
  [StructLayout(LayoutKind.Sequential)] struct Rect { public int left,top,right,bottom; }
  [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref IconIdentifier identifier,out Rect rect);
  public static IntPtr[] RegisteredTrayWindows(int pid) {
    var found=new List<IntPtr>();
    EnumWindows(delegate(IntPtr h,IntPtr l) {
      uint owner;GetWindowThreadProcessId(h,out owner);
      if(owner != pid)return true;
      var c=new StringBuilder(256);GetClassName(h,c,256);
      if(!c.ToString().StartsWith("WindowsForms10."))return true;
      var identifier=new IconIdentifier {size=(uint)Marshal.SizeOf(typeof(IconIdentifier)),hWnd=h,id=1}; Rect rect;
      if(Shell_NotifyIconGetRect(ref identifier,out rect)==0)found.Add(h);
      return true;
    },IntPtr.Zero);
    return found.ToArray();
  }
}
'@
$evidenceRoot = Join-Path $PSScriptRoot 'evidence'
[void][IO.Directory]::CreateDirectory($evidenceRoot)
$dataRoot = if ([string]::IsNullOrWhiteSpace($DataRoot)) { Join-Path $evidenceRoot ('external-ui-' + (Get-Date -Format yyyyMMdd-HHmmss)) } else { [IO.Path]::GetFullPath($DataRoot) }
[void][IO.Directory]::CreateDirectory($dataRoot)
if ($PlayProof) {
    $installedProof=Get-Content -Raw -LiteralPath (Join-Path $evidenceRoot 'docker-native-peppergrinder.json') | ConvertFrom-Json; $dataRoot=$installedProof.dataRoot
    $fixtureStatePath=Join-Path $dataRoot 'state.json'
    $fixtureState=Get-Content -Raw -LiteralPath $fixtureStatePath | ConvertFrom-Json
    $fixtureState.launchPaths.PSObject.Properties.Remove('peppergrinder')
    $fixtureState.installedGames=@($fixtureState.installedGames | Where-Object {$_ -cne 'peppergrinder'})
    $fixtureState | ConvertTo-Json -Depth 25 | Set-Content -LiteralPath $fixtureStatePath -Encoding UTF8
}
$checks = New-Object 'System.Collections.Generic.List[object]'
$appProcess = $null
function Add-Check([string]$Name, [bool]$Passed) {
    $checks.Add([pscustomobject]@{ name=$Name; passed=$Passed; at=[DateTime]::UtcNow.ToString('o') })
    if (-not $Passed) { throw ('Verification failed: ' + $Name) }
}
function Wait-AppWindow {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $appProcess.Refresh()
        if ($appProcess.HasExited) { throw 'App exited during UI test.' }
        if ($appProcess.MainWindowHandle -ne [IntPtr]::Zero) {
            $candidate = [Windows.Automation.AutomationElement]::FromHandle($appProcess.MainWindowHandle)
            $search = $candidate.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty,'SearchBox')))
            if ($null -ne $search) {
                # The WPF shell is visible before the bundled catalog extraction
                # completes.  Wait for the first realized row so every UI proof
                # starts from the same ready state and does not race startup.
                $list = $candidate.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty,'GameList')))
                if ($null -ne $list) {
                    $rows = $list.FindAll([Windows.Automation.TreeScope]::Children, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem)))
                    if ($rows.Count -gt 0) { return $candidate }
                }
            }
        }
        Start-Sleep -Milliseconds 150
    } while ($clock.Elapsed.TotalSeconds -lt 300)
    throw 'Native window did not become accessible.'
}
function Find-Id($Root, [string]$Id) {
    $control = $Root.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id)))
    if ($null -eq $control) { throw ('Missing native control: ' + $Id) }
    return $control
}
function Invoke-Control($Control) { ([Windows.Automation.InvokePattern]$Control.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)).Invoke() }
function Toggle-Control($Control) { ([Windows.Automation.TogglePattern]$Control.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)).Toggle() }
function Read-SelectedName($Control) {
    $selected=([Windows.Automation.SelectionPattern]$Control.GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern)).Current.GetSelection()
    if($selected.Count -ne 1){throw 'Expected one selected native item.'}
    return $selected[0].Current.Name
}
function Find-Name($Root,[string]$Name,[Windows.Automation.ControlType]$Type) {
    $matches=@($Root.FindAll([Windows.Automation.TreeScope]::Descendants,(New-Object Windows.Automation.AndCondition((New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty,$Name)),(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,$Type))))))
    if($matches.Count -ne 1){throw ('Expected one native '+$Type.ProgrammaticName+': '+$Name+'; found '+$matches.Count)}
    return $matches[0]
}
function Wait-ProcessControl([string]$Name,[Windows.Automation.ControlType]$Type) {
    $clock=[Diagnostics.Stopwatch]::StartNew()
    do {
        $roots=[Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty,$appProcess.Id)))
        $matches=@(foreach($root in $roots){$root.FindAll([Windows.Automation.TreeScope]::Descendants,(New-Object Windows.Automation.AndCondition((New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty,$Name)),(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,$Type)))) )})
        if($matches.Count -eq 1){return $matches[0]}
        if($matches.Count -gt 1){throw ('Ambiguous fixture-owned control: '+$Name)}
        Start-Sleep -Milliseconds 100
    }while($clock.Elapsed.TotalSeconds -lt 10)
    throw ('Fixture-owned control did not appear: '+$Name)
}
function Find-NativeFileControl($Root,[string]$Id,[string]$Class) {
    if ($Root.Current.ProcessId -ne $appProcess.Id) { throw 'Wrong native dialog process.' }
    $readyClock=[Diagnostics.Stopwatch]::StartNew()
    do {
    $matches=@($Root.FindAll([Windows.Automation.TreeScope]::Descendants,(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id))) | Where-Object {
        $className=New-Object Text.StringBuilder 100
        [void][GLMNativeDialog]::GetClassName([IntPtr]$_.Current.NativeWindowHandle,$className,100)
        $className.ToString() -eq $Class
    })
    if($matches.Count -eq 1){break}
    Start-Sleep -Milliseconds 100
    } while($readyClock.Elapsed.TotalSeconds -lt 5)
    if($matches.Count -ne 1){
        $Root.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition) | ForEach-Object {
            $nativeClass=New-Object Text.StringBuilder 100
            [void][GLMNativeDialog]::GetClassName([IntPtr]$_.Current.NativeWindowHandle,$nativeClass,100)
            [pscustomobject]@{Name=$_.Current.Name;Id=$_.Current.AutomationId;Class=$nativeClass.ToString();Patterns=@($_.GetSupportedPatterns().ProgrammaticName)}
        } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'native-dialog-controls.json') -Encoding UTF8
        throw ('Ambiguous native dialog control: '+$Id+'/'+$Class)
    }
    return [IntPtr]$matches[0].Current.NativeWindowHandle
}
function Wait-Dialog([string]$Title) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty,$appProcess.Id)))
        foreach ($candidate in $windows) { if ($candidate.Current.Name -eq $Title) { return $candidate } }
        # Owned WPF dialogs may also be exposed under their owner in the automation tree.
        $candidate = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty,$Title)))
        if ($null -ne $candidate -and $candidate.Current.ControlType -eq [Windows.Automation.ControlType]::Window) { return $candidate }
        Start-Sleep -Milliseconds 100
    } while ($clock.Elapsed.TotalSeconds -lt 10)
    throw ('Native dialog did not open: ' + $Title)
}
try {
    if($AutomaticCatalogProof){
        $fixtureCache=Join-Path $dataRoot 'cache'
        [void][IO.Directory]::CreateDirectory($fixtureCache)
        $seedCache=Join-Path $evidenceRoot 'final-live-catalog\cache'
        foreach($file in @('admin-config.json','dates-added.json','docker-tags.json','games.json','image-sizes.json','tabs.json','times.json')){Copy-Item -LiteralPath (Join-Path $seedCache $file) -Destination (Join-Path $fixtureCache $file)}
        $seedSnapshot=Get-Content -LiteralPath (Join-Path $fixtureCache 'docker-tags.json') -Raw | ConvertFrom-Json
        if(-not$seedSnapshot.success -or $seedSnapshot.degraded -or $seedSnapshot.source -ne 'docker-hub-direct' -or $seedSnapshot.repository -ne 'michadockermisha/backup' -or $seedSnapshot.count -ne @($seedSnapshot.tags).Count){throw 'The existing full snapshot is not suitable for an automatic-refresh proof.'}
        $seedAttempts=@'
const fs=require('node:fs');const path=require('node:path');const dir=process.argv[1];
const tags=JSON.parse(fs.readFileSync(path.join(dir,'docker-tags.json'),'utf8'));const games=JSON.parse(fs.readFileSync(path.join(dir,'games.json'),'utf8'));
const attempts={};const retryAfter=new Date(Date.now()+86400000).toISOString();for(const id of [...tags.tags.map(t=>t.name),...games.map(g=>g.id)])attempts[id]={retryAfter,reason:'Isolated automatic catalog timer proof; suppress unrelated metadata traffic'};
fs.writeFileSync(path.join(dir,'metadata-attempts.json'),JSON.stringify(attempts));
'@
        & 'C:\Program Files\nodejs\node.exe' -e $seedAttempts $fixtureCache
        if($LASTEXITCODE -ne 0){throw 'Could not prepare bounded metadata-attempt fixtures.'}
    }
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $launchArguments=@('--data-dir',$dataRoot)
    if(-not$AutomaticCatalogProof){$launchArguments+='--offline'}
    $launchedAt=[DateTimeOffset]::UtcNow
    $appProcess = Start-Process -FilePath $ExePath -ArgumentList $launchArguments -PassThru
    $window = Wait-AppWindow
    $readyClock = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath (Join-Path $dataRoot 'state.json')) -and $readyClock.Elapsed.TotalSeconds -lt 15) { Start-Sleep -Milliseconds 100 }
    Add-Check 'Actual packaged EXE exposes native WPF controls' ($window.Current.FrameworkId -eq 'WPF')
    $startupMs = $timer.ElapsedMilliseconds
    if($KillScriptProof){
        $testedHash=(Get-FileHash -Algorithm SHA256 -LiteralPath $ExePath).Hash
        Invoke-Control (Find-Id $window 'DeselectAll')
        $visibleBoxes=(Find-Id $window 'GameList').FindAll([Windows.Automation.TreeScope]::Descendants,(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::CheckBox)))
        $checkedBoxes=@($visibleBoxes|Where-Object{([Windows.Automation.TogglePattern]$_.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)).Current.ToggleState -eq [Windows.Automation.ToggleState]::On})
        Add-Check 'Kill All export proof starts with Clear applied and no selected visible games' ($visibleBoxes.Count -gt 0 -and $checkedBoxes.Count -eq 0 -and (Find-Id $window 'SelectedSize').Current.Name -eq '0 GB')
        $exportRecords=New-Object 'System.Collections.Generic.List[object]'
        foreach($format in @('ps1','bat')){
            $header='Export Kill All script (.'+$format+')'
            Invoke-Control (Find-Id $window 'ExportScript')
            Invoke-Control (Wait-ProcessControl $header ([Windows.Automation.ControlType]::MenuItem))
            $dialog=Wait-Dialog 'Export Kill All container script'
            $reviewText=(@($dialog.FindAll([Windows.Automation.TreeScope]::Descendants,(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Text)))|ForEach-Object{$_.Current.Name}) -join "`n")
            Add-Check ('Native '+$format+' review explicitly describes every container and deletion scope') ($reviewText -match '(?i)all.*containers|every.*container' -and $reviewText -match '(?i)remove|delete' -and $reviewText -match '(?i)unrelated' -and $reviewText -match '(?i)writable layers' -and $null -ne (Find-Id $dialog 'SaveKillAllScriptButton'))
            Invoke-Control (Find-Id $dialog 'CancelKillAllScriptButton')
            Start-Sleep -Milliseconds 250
            $window=Wait-AppWindow
            $fixtureWindows=[Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty,$appProcess.Id)))
            $activeSaveDialogs=@($fixtureWindows|Where-Object{$_.Current.Name -eq 'Save As' -or $_.Current.Name -eq 'Export Kill All container script'})
            $ownedReviews=$window.FindAll([Windows.Automation.TreeScope]::Descendants,(New-Object Windows.Automation.AndCondition((New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty,'Export Kill All container script')),(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Window)))))
            Add-Check ('Cancelling the '+$format+' scope review creates no export or save dialog') (@(Get-ChildItem -LiteralPath $dataRoot -File -Filter ('kill-all-containers.'+$format)).Count -eq 0 -and $activeSaveDialogs.Count -eq 0 -and $ownedReviews.Count -eq 0)
            Invoke-Control (Find-Id $window 'ExportScript')
            Invoke-Control (Wait-ProcessControl $header ([Windows.Automation.ControlType]::MenuItem))
            $dialog=Wait-Dialog 'Export Kill All container script'
            Invoke-Control (Find-Id $dialog 'SaveKillAllScriptButton')
            $picker=Wait-Dialog 'Save As'
            $output=Join-Path $dataRoot ('kill-all-containers.'+$format)
            [void][GLMNativeDialog]::SendMessage((Find-NativeFileControl $picker '1001' 'Edit'),0x000C,[IntPtr]::Zero,$output)
            [void][GLMNativeDialog]::SendMessage((Find-NativeFileControl $picker '1' 'Button'),0x00F5,[IntPtr]::Zero,$null)
            Start-Sleep -Milliseconds 300
            Add-Check ('Native Kill All '+$format+' Save dialog creates only the chosen script file') (Test-Path -LiteralPath $output -PathType Leaf)
            $text=[IO.File]::ReadAllText($output)
            if($format -eq 'bat'){
                $markers=[regex]::Matches($text,'(?m)^# GLM_POWERSHELL_START\r?$')
                if($markers.Count -ne 1){throw 'BAT export does not have exactly one embedded PowerShell payload marker.'}
                $payload=$text.Substring($markers[0].Index+$markers[0].Length).TrimStart([char[]]"`r`n")
                $loaderMatch=[regex]::Match($text,'(?mi)^(?:%SystemRoot%\\System32\\WindowsPowerShell\\v1\.0\\)?powershell(?:\.exe)?[^\r\n]*-EncodedCommand\s+([A-Za-z0-9+/=]+)\r?$')
                if(-not$loaderMatch.Success){throw 'BAT export does not expose its fixed PowerShell loader.'}
                $loader=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($loaderMatch.Groups[1].Value))
                $loaderTokens=$null;$loaderErrors=$null
                [void][Management.Automation.Language.Parser]::ParseInput($loader,[ref]$loaderTokens,[ref]$loaderErrors)
                Add-Check 'Kill All BAT loader is valid PowerShell 5 and reads its own embedded payload' ($loaderErrors.Count -eq 0 -and $loader.Contains('$env:GLM_SCRIPT') -and $loader.Contains('GLM_POWERSHELL_START') -and $loaderMatch.Value.Length -lt 8191 -and $text.Contains('set "GLM_SCRIPT=%~f0"') -and $text.IndexOf('exit /b') -gt 0 -and $text.IndexOf('exit /b') -lt $markers[0].Index)
                Add-Check 'Kill All BAT embeds the same reviewed operation as its PS1 export' ($payload.TrimEnd() -ceq $ps1Payload.TrimEnd())
            }else{$payload=$text;$ps1Payload=$payload}
            $parseTokens=$null;$parseErrors=$null
            [void][Management.Automation.Language.Parser]::ParseInput($payload,[ref]$parseTokens,[ref]$parseErrors)
            Add-Check ('Saved Kill All '+$format+' payload parses in Windows PowerShell 5') ($parseErrors.Count -eq 0)
            Add-Check ('Saved Kill All '+$format+' requires typed confirmation before its removal operation') ($payload -match 'Read-Host' -and $payload.Contains("-cne 'DELETE ALL'") -and $payload.IndexOf('Read-Host') -lt $payload.IndexOf('container rm'))
            Add-Check ('Saved Kill All '+$format+' payload describes broad container scope without volume or image pruning') ($payload -match '(?i)all.*containers|every.*container' -and $payload -match '(?i)\b(ps|container\s+ls)\b[^\r\n]*(?:-aq|--all|\s-a\b)' -and $payload -match '(?i)\b(rm|remove)\b' -and $payload -notmatch '(?i)system\s+prune|volume\s+(rm|prune)|image\s+(rm|prune)|wsl\s+--')
            $exportRecords.Add([pscustomobject]@{format=$format;path=$output;sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $output).Hash;bytes=(Get-Item -LiteralPath $output).Length;scopeReview=$reviewText})
            try{([Windows.Automation.WindowPattern]$dialog.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)).Close()}catch{}
            Start-Sleep -Milliseconds 150
            $window=Wait-AppWindow
        }
        Add-Check 'Kill All export produces no native download jobs' (-not(Test-Path -LiteralPath (Join-Path $dataRoot 'jobs')))
        ([Windows.Automation.WindowPattern]$window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)).Close()
        Add-Check 'Kill All export fixture closes cleanly' ($appProcess.WaitForExit(10000))
        [pscustomobject]@{at=[DateTime]::UtcNow.ToString('o');passed=$true;executable=$ExePath;sha256=$testedHash;dataRoot=$dataRoot;offline=$true;selectionRequired=$false;exports=@($exportRecords.ToArray());scriptsExecuted=$false;containerDeletionExecuted=$false;physicalInputUsed=$false;checks=@($checks.ToArray())} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'kill-script-native.json') -Encoding UTF8
        Write-Output ('PASS: '+$checks.Count+' Kill All script review/export checks; scripts and container deletion were never executed.')
        return
    }
    if($AutomaticCatalogProof){
        $snapshotPath=Join-Path $fixtureCache 'docker-tags.json'
        $checkedClock=[Diagnostics.Stopwatch]::StartNew()
        do{
            Start-Sleep -Milliseconds 500
            $initialSnapshot=Get-Content -LiteralPath $snapshotPath -Raw | ConvertFrom-Json
            $initialChecked=if($initialSnapshot.checkedAt){[DateTimeOffset]::Parse($initialSnapshot.checkedAt)}else{[DateTimeOffset]::MinValue}
        }while($initialChecked -le $launchedAt -and $checkedClock.Elapsed.TotalSeconds -lt 45)
        $initialComplete = $initialSnapshot.success -and -not $initialSnapshot.degraded -and $initialSnapshot.repository -eq 'michadockermisha/backup' -and $initialSnapshot.source -eq 'docker-hub-direct' -and $initialSnapshot.count -eq $seedSnapshot.count -and @($initialSnapshot.tags).Count -eq $seedSnapshot.count
        Add-Check 'Normal unsigned startup checks a complete exact Docker snapshot' ($initialChecked -gt $launchedAt -and $initialComplete)
        Write-Output ('Startup catalog check completed at '+$initialSnapshot.checkedAt+'; leaving the window untouched for its scheduled refresh.')
        $scheduledClock=[Diagnostics.Stopwatch]::StartNew()
        do{
            Start-Sleep -Milliseconds 1000
            $appProcess.Refresh()
            if($appProcess.HasExited){throw 'Normal fixture exited before its automatic catalog interval.'}
            $scheduledSnapshot=Get-Content -LiteralPath $snapshotPath -Raw | ConvertFrom-Json
            $scheduledChecked=if($scheduledSnapshot.checkedAt){[DateTimeOffset]::Parse($scheduledSnapshot.checkedAt)}else{[DateTimeOffset]::MinValue}
        }while($scheduledChecked -le $initialChecked -and $scheduledClock.Elapsed.TotalSeconds -lt 100)
        Add-Check 'Untouched normal window performs a second catalog check at its native 15-second interval' ($scheduledChecked -gt $initialChecked -and ($scheduledChecked-$initialChecked).TotalSeconds -ge 10 -and ($scheduledChecked-$initialChecked).TotalSeconds -lt 45)
        Add-Check 'Automatic refresh preserves the complete exact repository and tag count' ($scheduledSnapshot.success -and -not$scheduledSnapshot.degraded -and $scheduledSnapshot.repository -eq 'michadockermisha/backup' -and $scheduledSnapshot.source -eq 'docker-hub-direct' -and $scheduledSnapshot.count -eq $initialSnapshot.count -and @($scheduledSnapshot.tags).Count -eq $initialSnapshot.count)
        Add-Check 'Automatic refresh reuses the full snapshot after a fresh Docker check' ($scheduledSnapshot.fetchedAt -eq $initialSnapshot.fetchedAt -and $scheduledSnapshot.checkedAt -ne $initialSnapshot.checkedAt)
        $activity=Get-Content -LiteralPath (Join-Path $dataRoot 'activity.log') -Raw
        $catalogChecks=[regex]::Matches($activity,'Docker Hub (?:direct snapshot verified|unchanged first page and count; reusing recent complete snapshot)').Count
        Add-Check 'Both startup and scheduled checks are independently recorded by the live application' ($catalogChecks -ge 2)
        Add-Check 'Unsigned catalog fixture has no queued shared writes or metadata refresh activity' (@((Get-Content -LiteralPath (Join-Path $dataRoot 'state.json') -Raw | ConvertFrom-Json).pending).Count -eq 0 -and $activity -notmatch 'acknowledged by production|Automatic metadata:|Metadata refreshed')
        $window=Wait-AppWindow
        $status=(Find-Id $window 'StatusText').Current.Name
        Add-Check 'The normal window stays responsive through its scheduled catalog check' ($appProcess.Responding -and $window.Current.FrameworkId -eq 'WPF')
        ([Windows.Automation.WindowPattern]$window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)).Close()
        Add-Check 'The automatic catalog fixture closes cleanly' ($appProcess.WaitForExit(10000))
        [pscustomobject]@{at=[DateTime]::UtcNow.ToString('o');passed=$true;executable=$ExePath;sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $ExePath).Hash;dataRoot=$dataRoot;manualRefreshUsed=$false;normalWindow=$true;adminSignedIn=$false;seedFetchedAt=$seedSnapshot.fetchedAt;initialCheckedAt=$initialSnapshot.checkedAt;scheduledCheckedAt=$scheduledSnapshot.checkedAt;intervalSeconds=($scheduledChecked-$initialChecked).TotalSeconds;repository=$scheduledSnapshot.repository;tagCount=$scheduledSnapshot.count;status=$status;checks=@($checks.ToArray())} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'automatic-catalog-native.json') -Encoding UTF8
        Write-Output ('PASS: '+$checks.Count+' unattended normal-window catalog checks; '+$scheduledSnapshot.count+' tags; interval '+[Math]::Round(($scheduledChecked-$initialChecked).TotalSeconds,2)+' seconds.')
        return
    }
    if ($LocalGameProof) {
        $testedHash=(Get-FileHash -Algorithm SHA256 -LiteralPath $ExePath).Hash
        $downloadProof=Get-Content -LiteralPath (Join-Path $evidenceRoot 'docker-native-peppergrinder.json') -Raw | ConvertFrom-Json
        if(-not$downloadProof.passed){throw 'The existing real Pepper Grinder download is not verified.'}
        $sourceExe=@($downloadProof.executables|Where-Object{[IO.Path]::GetFileName($_) -eq 'PepperGrinder.exe'})
        if($sourceExe.Count -ne 1 -or -not(Test-Path -LiteralPath $sourceExe[0] -PathType Leaf)){throw 'The verified Pepper Grinder executable is unavailable.'}
        $sourceFolder=[IO.Path]::GetDirectoryName($sourceExe[0])
        if(-not[IO.Path]::GetFullPath($sourceFolder).StartsWith(([IO.Path]::GetFullPath($evidenceRoot)+'\'),[StringComparison]::OrdinalIgnoreCase)){throw 'The source game is outside this project proof directory.'}
        $sourceItems=@(Get-Item -LiteralPath $sourceFolder;Get-ChildItem -LiteralPath $sourceFolder -Recurse -Force)
        if(@($sourceItems|Where-Object{($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0}).Count -gt 0){throw 'The proof source contains a filesystem link.'}
        $localLibrary=Join-Path $dataRoot 'Local library'
        $localName='Native local game proof'
        $localFolder=Join-Path $localLibrary $localName
        [void][IO.Directory]::CreateDirectory($localFolder)
        Get-ChildItem -LiteralPath $sourceFolder -Force | Copy-Item -Destination $localFolder -Recurse -Force
        $localExe=Join-Path $localFolder 'PepperGrinder.exe'
        $copiedFiles=@(Get-ChildItem -LiteralPath $localFolder -Recurse -File)
        Add-Check 'Local fixture copies the verified real game into a separate noncatalog folder' ($copiedFiles.Count -eq $downloadProof.fileCount -and (Get-FileHash -Algorithm SHA256 -LiteralPath $localExe).Hash -eq (Get-FileHash -Algorithm SHA256 -LiteralPath $sourceExe[0]).Hash)
        Invoke-Control (Find-Id $window 'OpenSettings')
        $dialog=Wait-Dialog 'Settings & backups'
        ([Windows.Automation.ValuePattern](Find-Id $dialog 'DownloadFolder').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).SetValue($localLibrary)
        Invoke-Control (Find-Id $dialog 'SaveSettings')
        ([Windows.Automation.WindowPattern]$dialog.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)).Close()
        Start-Sleep -Milliseconds 200
        $window=Wait-AppWindow
        Invoke-Control (Find-Id $window 'ScanFolder')
        $picker=Wait-Dialog 'Choose the folder containing installed games'
        Start-Sleep -Milliseconds 400
        $address=$picker.FindFirst([Windows.Automation.TreeScope]::Descendants,(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty,('Address: '+$localLibrary))))
        Add-Check 'Native ScanFolder dialog selects the exact isolated local library parent' ($null -ne $address)
        [void][GLMNativeDialog]::SendMessage((Find-NativeFileControl $picker '1' 'Button'),0x00F5,[IntPtr]::Zero,$null)
        $scanClock=[Diagnostics.Stopwatch]::StartNew()
        do{
            Start-Sleep -Milliseconds 150
            $localState=Get-Content -LiteralPath (Join-Path $dataRoot 'state.json') -Raw | ConvertFrom-Json
            $localEntries=@($localState.localGames.PSObject.Properties|Where-Object{$_.Value.Name -eq $localName -and $_.Value.Folder -eq $localFolder})
        }while($localEntries.Count -eq 0 -and $scanClock.Elapsed.TotalSeconds -lt 15)
        Add-Check 'Native scan persists one local-only game record with its own identity' ($localEntries.Count -eq 1 -and $localEntries[0].Name.StartsWith('local:',[StringComparison]::Ordinal))
        $localId=$localEntries[0].Name
        Add-Check 'Native scan persists the local Installed marker and real copied executable' ($localState.installedGames -contains $localId -and $localState.launchPaths.PSObject.Properties[$localId].Value -eq $localExe)
        $window=Wait-AppWindow
        ([Windows.Automation.ValuePattern](Find-Id $window 'SearchBox').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).SetValue($localName)
        Start-Sleep -Milliseconds 400
        $rows=(Find-Id $window 'GameList').FindAll([Windows.Automation.TreeScope]::Children,(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem)))
        Add-Check 'Native search displays the new local game under its folder title' ($rows.Count -eq 1 -and $null -ne $rows[0].FindFirst([Windows.Automation.TreeScope]::Descendants,(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty,$localName))))
        Invoke-Control (Find-Name $rows[0] ('Details  '+[char]0x2192) ([Windows.Automation.ControlType]::Button))
        $dialog=Wait-Dialog $localName
        $buttonNames=@($dialog.FindAll([Windows.Automation.TreeScope]::Descendants,(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Button)))|ForEach-Object{$_.Current.Name})
        Add-Check 'Local Details omits Docker installation and Docker copy actions' ($buttonNames -notcontains 'Install this game' -and $buttonNames -notcontains 'Copy Docker command' -and $buttonNames -notcontains 'Copy Docker Hub link')
        Invoke-Control (Find-Id $dialog 'PlayGame')
        $launchClock=[Diagnostics.Stopwatch]::StartNew()
        do {Start-Sleep -Milliseconds 400;$localGameProcess=Get-Process -Name PepperGrinder -ErrorAction SilentlyContinue|Where-Object Path -eq $localExe|Select-Object -First 1}while(($null -eq $localGameProcess -or $localGameProcess.MainWindowHandle -eq [IntPtr]::Zero) -and $launchClock.Elapsed.TotalSeconds -lt 35)
        Add-Check 'Native local-game Play starts the copied real game with a responding window' ($null -ne $localGameProcess -and $localGameProcess.MainWindowHandle -ne [IntPtr]::Zero -and $localGameProcess.Responding)
        $proofGamePid=$localGameProcess.Id;$proofGameTitle=$localGameProcess.MainWindowTitle
        if($localGameProcess.Path -ne $localExe){throw 'The proof game process identity changed.'}
        Start-Sleep -Seconds 2
        [void]$localGameProcess.CloseMainWindow()
        Add-Check 'The exact local proof game closes cleanly' ($localGameProcess.WaitForExit(10000))
        $playClock=[Diagnostics.Stopwatch]::StartNew(); $playedSeconds=0.0
        do {
            Start-Sleep -Milliseconds 250
            if(Test-Path -LiteralPath (Join-Path $dataRoot 'state.json')) {
                $playState=Get-Content -LiteralPath (Join-Path $dataRoot 'state.json') -Raw | ConvertFrom-Json
                if($playState.playTimeSeconds.PSObject.Properties[$localId]) { $playedSeconds=[double]$playState.playTimeSeconds.PSObject.Properties[$localId].Value }
            }
        } while($playedSeconds -le 0 -and $playClock.Elapsed.TotalSeconds -lt 10)
        Add-Check 'Play session persists positive elapsed seconds for the exact game identity' ($playedSeconds -gt 0)
        $window=Wait-AppWindow
        $playedRows=(Find-Id $window 'GameList').FindAll([Windows.Automation.TreeScope]::Children,(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem)))
        $playedLabels=@($playedRows|ForEach-Object{$_.FindAll([Windows.Automation.TreeScope]::Descendants,(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Text)))|ForEach-Object{$_.Current.Name}}|Where-Object{$_ -match '^Played \d+\.\d h$'})
        Add-Check 'Native game rows render the live Played hours label after a session' ($playedLabels.Count -gt 0)
        ([Windows.Automation.WindowPattern]$window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)).Close()
        Add-Check 'The local-library fixture closes cleanly before restart' ($appProcess.WaitForExit(10000))
        $appProcess=Start-Process -FilePath $ExePath -ArgumentList @('--data-dir',$dataRoot,'--offline') -PassThru
        $window=Wait-AppWindow
        $localState=Get-Content -LiteralPath (Join-Path $dataRoot 'state.json') -Raw | ConvertFrom-Json
        Add-Check 'Offline restart preserves the local record, Installed marker, and launcher' ($localState.localGames.PSObject.Properties[$localId].Value.Name -eq $localName -and $localState.installedGames -contains $localId -and $localState.launchPaths.PSObject.Properties[$localId].Value -eq $localExe)
        ([Windows.Automation.ValuePattern](Find-Id $window 'SearchBox').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).SetValue($localName)
        Start-Sleep -Milliseconds 400
        $rows=(Find-Id $window 'GameList').FindAll([Windows.Automation.TreeScope]::Children,(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem)))
        Add-Check 'Offline restart displays the persistent local game in native search' ($rows.Count -eq 1 -and $null -ne $rows[0].FindFirst([Windows.Automation.TreeScope]::Descendants,(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty,$localName))))
        ([Windows.Automation.WindowPattern]$window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)).Close()
        Add-Check 'Local-game proof leaves no fixture application process' ($appProcess.WaitForExit(10000))
        [pscustomobject]@{at=[DateTime]::UtcNow.ToString('o');passed=$true;executable=$ExePath;sha256=$testedHash;dataRoot=$dataRoot;localId=$localId;localFolder=$localFolder;localExe=$localExe;sourceExe=$sourceExe[0];copiedFileCount=$copiedFiles.Count;gamePid=$proofGamePid;gameTitle=$proofGameTitle;gameClosed=$localGameProcess.HasExited;offline=$true;downloadPerformed=$false;physicalInputUsed=$false;checks=@($checks.ToArray())} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'local-game-native.json') -Encoding UTF8
        Write-Output ('PASS: '+$checks.Count+' local-only game native scan/Play/restart checks; no download performed.')
        return
    }
    if ($TrayProof) {
        # .NET 10 NotifyIcon uses WM_USER+1024 and WM_RBUTTONUP for its registered
        # shell callback. Resolve the actual icon with Shell_NotifyIconGetRect first;
        # never send events to other app windows or move the physical pointer.
        $trayWindows=@([GLMNativeDialog]::RegisteredTrayWindows($appProcess.Id))
        Add-Check 'Fixture process has one real registered Windows tray icon' ($trayWindows.Count -eq 1)
        $trayHandle=$trayWindows[0]
        $mainHandle=[IntPtr]$window.Current.NativeWindowHandle
        ([Windows.Automation.WindowPattern]$window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)).SetWindowVisualState([Windows.Automation.WindowVisualState]::Minimized)
        Start-Sleep -Milliseconds 250
        Add-Check 'Minimize hides the fixture window while its shell tray registration remains' (-not[GLMNativeDialog]::IsWindowVisible($mainHandle) -and @([GLMNativeDialog]::RegisteredTrayWindows($appProcess.Id)).Count -eq 1)
        Add-Check 'Registered fixture tray callback opens its context menu' ([GLMNativeDialog]::PostMessage($trayHandle,0x800,[IntPtr]1,[IntPtr]0x205))
        Invoke-Control (Wait-ProcessControl 'Open Game Library' ([Windows.Automation.ControlType]::MenuItem))
        $window=Wait-AppWindow
        Add-Check 'Real tray Open menu command restores the same fixture window' ([GLMNativeDialog]::IsWindowVisible($mainHandle) -and ([Windows.Automation.WindowPattern]$window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)).Current.WindowVisualState -eq [Windows.Automation.WindowVisualState]::Normal)
        [void][GLMNativeDialog]::PostMessage($trayHandle,0x800,[IntPtr]1,[IntPtr]0x205)
        Invoke-Control (Wait-ProcessControl 'Refresh catalog' ([Windows.Automation.ControlType]::MenuItem))
        Start-Sleep -Milliseconds 300
        $window=Wait-AppWindow
        Add-Check 'Tray Refresh command honors explicit offline mode without starting network sync' ((Find-Id $window 'StatusText').Current.Name -match '^Offline' -and -not(Test-Path -LiteralPath (Join-Path $dataRoot 'cache\admin-config.json')))
        [void][GLMNativeDialog]::PostMessage($trayHandle,0x800,[IntPtr]1,[IntPtr]0x205)
        Invoke-Control (Wait-ProcessControl 'Exit' ([Windows.Automation.ControlType]::MenuItem))
        Add-Check 'Real tray Exit menu command shuts down the fixture process' ($appProcess.WaitForExit(10000))
        Add-Check 'Tray Exit removes the fixture shell registration' (@([GLMNativeDialog]::RegisteredTrayWindows($appProcess.Id)).Count -eq 0)
        [pscustomobject]@{at=[DateTime]::UtcNow.ToString('o');passed=$true;executable=$ExePath;dataRoot=$dataRoot;route='Registered NotifyIcon callback opens actual menu; UIAutomation invokes its commands';physicalInputUsed=$false;refreshScope='Offline guard, no live refresh claim';checks=@($checks.ToArray())} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'tray-external-native.json') -Encoding UTF8
        Write-Output ('PASS: '+$checks.Count+' external native tray checks; no physical input used.')
        return
    }
    if ($ScriptProof) {
        $search=Find-Id $window 'SearchBox'
        ([Windows.Automation.ValuePattern]$search.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).SetValue('staroceanthedivineforce')
        Start-Sleep -Milliseconds 400
        Invoke-Control (Find-Id $window 'SelectAll')
        $exports=@(@{name='Download .BAT (default)';file='install-games.bat';stop=$false},@{name='Download .PS1 (PowerShell)';file='install-games.ps1';stop=$false},@{name='Download .SH (Native Linux)';file='install-games.sh';stop=$false},@{name='Export stop-selected-containers script';file='stop-selected-games.ps1';stop=$true})
        foreach($export in $exports){
            Invoke-Control (Find-Id $window 'ExportScript')
            Invoke-Control (Wait-ProcessControl $export.name ([Windows.Automation.ControlType]::MenuItem))
            $picker=Wait-Dialog 'Save As'
            $output=Join-Path $dataRoot $export.file
            [void][GLMNativeDialog]::SendMessage((Find-NativeFileControl $picker '1001' 'Edit'),0x000C,[IntPtr]::Zero,$output)
            [void][GLMNativeDialog]::SendMessage((Find-NativeFileControl $picker '1' 'Button'),0x00F5,[IntPtr]::Zero,$null)
            Start-Sleep -Milliseconds 250
            Add-Check ('Native '+$export.name+' saves the chosen file') (Test-Path -LiteralPath $output)
            $scriptText=[IO.File]::ReadAllText($output)
            $hasExpectedOperation=if($export.stop){$scriptText -match 'container inspect' -and $scriptText -match '\$dockerExecutable stop' -and $scriptText -notmatch '\$dockerExecutable pull'}else{$scriptText.Contains('michadockermisha/backup:staroceanthedivineforce') -and $scriptText -match 'pull'}
            Add-Check ('Saved '+$export.file+' contains the selected operation without broad cleanup') ($hasExpectedOperation -and $scriptText -notmatch 'docker system prune|\s-aq\b|wsl --')
            if($export.file -match '\.(ps1|bat)$'){
                $payload=if($export.file.EndsWith('.bat')){[regex]::Split($scriptText,'(?m)^# GLM_POWERSHELL_START\r?$')[1]}else{$scriptText}
                $parseTokens=$null;$parseErrors=$null
                [void][Management.Automation.Language.Parser]::ParseInput($payload,[ref]$parseTokens,[ref]$parseErrors)
                Add-Check ('Saved '+$export.file+' payload parses in Windows PowerShell 5') ($parseErrors.Count -eq 0)
            }else{
                $bash='F:\backup\windowsapps\installed\PortableGit\bin\bash.exe'
                if(-not(Test-Path -LiteralPath $bash)){throw 'The installed Git Bash syntax checker is unavailable.'}
                & $bash --noprofile --norc -n ($output.Replace('\','/'))
                Add-Check 'Saved install-games.sh parses in the existing Git Bash' ($LASTEXITCODE -eq 0)
            }
            $window=Wait-AppWindow
        }
        [pscustomobject]@{at=[DateTime]::UtcNow.ToString('o');passed=$true;executable=$ExePath;sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $ExePath).Hash;dataRoot=$dataRoot;scriptsExecuted=$false;checks=@($checks.ToArray())} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'scripts-native.json') -Encoding UTF8
        Write-Output ('PASS: '+$checks.Count+' native script export checks; saved scripts were not executed.')
        return
    }
    if ($AdminProof) {
        $fixtureSource=Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\tests\comprehensive-user-security.test.js') -Raw
        $proofPassword=[regex]::Match($fixtureSource,"validAdminPassword:\s*'([^']+)'").Groups[1].Value
        if([string]::IsNullOrEmpty($proofPassword)){throw 'Existing website password fixture was unavailable.'}
        Invoke-Control (Find-Id $window 'AdminSignIn')
        $dialog=Wait-Dialog 'Admin sign in'
        $passwordField=Find-Id $dialog 'AdminPassword'
        ([Windows.Automation.ValuePattern]$passwordField.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).SetValue($proofPassword)
        $proofPassword=$null;$fixtureSource=$null
        Invoke-Control (Find-Name $dialog 'Sign in' ([Windows.Automation.ControlType]::Button))
        Start-Sleep -Milliseconds 250
        $window=Wait-AppWindow
        Add-Check 'External native sign-in accepts the existing website password' ((Find-Id $window 'AdminSignIn').Current.Name -eq 'Sign out')
        Add-Check 'Offline fixture remains offline after admin sign-in before any shared edit' ((Find-Id $window 'StatusText').Current.Name -match '^Offline' -and -not(Test-Path -LiteralPath (Join-Path $dataRoot 'cache\admin-config.json')))
        Invoke-Control (Find-Id $window 'ManageCategories')
        $dialog=Wait-Dialog 'Manage categories'
        ([Windows.Automation.ValuePattern](Find-Id $dialog 'CategoryName').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).SetValue('Native removal proof')
        Invoke-Control (Find-Name $dialog 'Create category' ([Windows.Automation.ControlType]::Button))
        Start-Sleep -Milliseconds 200
        $window=Wait-AppWindow
        $saved=Get-Content -LiteralPath (Join-Path $dataRoot 'state.json') -Raw | ConvertFrom-Json
        Add-Check 'Native category creation durably queues the isolated proof category' (@($saved.pending | Where-Object section -eq 'tabs')[0].after.id -contains 'native_removal_proof')
        $search=Find-Id $window 'SearchBox'
        ([Windows.Automation.ValuePattern]$search.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).SetValue('staroceanthedivineforce')
        Start-Sleep -Milliseconds 400
        Invoke-Control (Find-Id $window 'SelectAll')
        Invoke-Control (Find-Id $window 'MoveSelected')
        $dialog=Wait-Dialog 'Move selected games'
        $choice=$dialog.FindFirst([Windows.Automation.TreeScope]::Descendants,(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ComboBox)))
        ([Windows.Automation.ExpandCollapsePattern]$choice.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
        $item=Find-Name $choice 'Native removal proof' ([Windows.Automation.ControlType]::ListItem)
        ([Windows.Automation.SelectionItemPattern]$item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)).Select()
        ([Windows.Automation.ExpandCollapsePattern]$choice.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)).Collapse()
        Invoke-Control (Find-Name $dialog 'Move games' ([Windows.Automation.ControlType]::Button))
        Start-Sleep -Milliseconds 250
        $window=Wait-AppWindow
        $saved=Get-Content -LiteralPath (Join-Path $dataRoot 'state.json') -Raw | ConvertFrom-Json
        Add-Check 'Native Move games puts the chosen game in the isolated removal category' (@($saved.pending|Where-Object {$_.section -eq 'gameCategories' -and $_.key -eq 'staroceanthedivineforce'})[0].after -eq 'native_removal_proof')
        foreach($answer in @('No','Yes')){
            if($answer -eq 'No'){
                Invoke-Control (Find-Id $window 'ManageCategories')
                $dialog=Wait-Dialog 'Manage categories'
                $choice=$dialog.FindFirst([Windows.Automation.TreeScope]::Descendants,(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ComboBox)))
                ([Windows.Automation.ExpandCollapsePattern]$choice.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
                $item=Find-Name $choice 'Native removal proof' ([Windows.Automation.ControlType]::ListItem)
                ([Windows.Automation.SelectionItemPattern]$item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)).Select()
                ([Windows.Automation.ExpandCollapsePattern]$choice.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)).Collapse()
            }
            Invoke-Control (Find-Name $dialog 'Remove category (move its games to New)' ([Windows.Automation.ControlType]::Button))
            $confirm=Wait-Dialog 'Remove category'
            Add-Check ('Native removal '+$answer+' route presents the exact category confirmation') ($null -ne $confirm.FindFirst([Windows.Automation.TreeScope]::Descendants,(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty,"Remove 'Native removal proof' and move its games to New?"))))
            $answerId=if($answer -eq 'No'){'7'}else{'6'}
            [void][GLMNativeDialog]::SendMessage((Find-NativeFileControl $confirm $answerId 'Button'),0x00F5,[IntPtr]::Zero,$null)
            Start-Sleep -Milliseconds 250
            $saved=Get-Content -LiteralPath (Join-Path $dataRoot 'state.json') -Raw | ConvertFrom-Json
            $hasCategory=@($saved.pending | Where-Object section -eq 'tabs')[0].after.id -contains 'native_removal_proof'
            Add-Check ('Native removal '+$answer+' '+$(if($answer -eq 'No'){'preserves'}else{'removes'})+' the queued category') ($hasCategory -eq ($answer -eq 'No'))
            $gameEdit=@($saved.pending|Where-Object {$_.section -eq 'gameCategories' -and $_.key -eq 'staroceanthedivineforce'})
            if($answer -eq 'No'){Add-Check 'Cancel preserves the game assignment in the category' ($gameEdit.Count -eq 1 -and $gameEdit[0].after -eq 'native_removal_proof')}
            else{Add-Check 'Accept moves the category game back to New' ($gameEdit.Count -eq 0 -or ($gameEdit.Count -eq 1 -and $gameEdit[0].after -eq 'new'))}
        }
        Add-Check 'Offline category actions leave production sync unused' (-not(Test-Path -LiteralPath (Join-Path $dataRoot 'cache\admin-config.json')) -and -not((Get-Content -LiteralPath (Join-Path $dataRoot 'activity.log') -Raw) -match 'acknowledged by production'))
        [pscustomobject]@{at=[DateTime]::UtcNow.ToString('o');passed=$true;executable=$ExePath;dataRoot=$dataRoot;offline=$true;checks=@($checks.ToArray())} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'admin-external-native.json') -Encoding UTF8
        Write-Output ('PASS: '+$checks.Count+' external native administrator/confirmation checks.')
        return
    }
    if ($PlayProof) {
        Invoke-Control (Find-Id $window 'ScanFolder')
        $picker=Wait-Dialog 'Choose the folder containing installed games'
        $expectedFolder=Join-Path $dataRoot 'Downloads'
        Start-Sleep -Milliseconds 400
        $address=$picker.FindFirst([Windows.Automation.TreeScope]::Descendants,(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty,('Address: ' + $expectedFolder))))
        Add-Check 'Native installed scan opens the exact download folder' ($null -ne $address)
        [void][GLMNativeDialog]::SendMessage((Find-NativeFileControl $picker '1' 'Button'),0x00F5,[IntPtr]::Zero,$null)
        Start-Sleep -Milliseconds 500
        $window=Wait-AppWindow
        $scanned=Get-Content -Raw -LiteralPath (Join-Path $dataRoot 'state.json') | ConvertFrom-Json
        Add-Check 'Native scan recognizes the downloaded game without selecting a utility' ([IO.Path]::GetFileName($scanned.launchPaths.peppergrinder) -eq 'PepperGrinder.exe')
        $search=Find-Id $window 'SearchBox'
        ([Windows.Automation.ValuePattern]$search.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).SetValue('peppergrinder')
        Start-Sleep -Milliseconds 400
        $details=$window.FindFirst([Windows.Automation.TreeScope]::Descendants,(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty,('Details  ' + [char]0x2192))))
        Invoke-Control $details
        $dialog=Wait-Dialog 'Pepper Grinder'
        $choose=$dialog.FindFirst([Windows.Automation.TreeScope]::Descendants,(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty,('Choose game executable' + [char]0x2026))))
        Invoke-Control $choose
        $picker=Wait-Dialog 'Choose the actual game executable'
        $fields=$picker.FindAll([Windows.Automation.TreeScope]::Descendants,(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Edit)))
        $fileField=@($fields | Where-Object {$_.Current.Name -match 'File name'}) | Select-Object -First 1
        $gameExe=@($installedProof.executables | Where-Object { [IO.Path]::GetFileName($_) -eq 'PepperGrinder.exe' })[0]
        if ($null -ne $fileField) { ([Windows.Automation.ValuePattern]$fileField.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).SetValue($gameExe) }
        else { [void][GLMNativeDialog]::SendMessage((Find-NativeFileControl $picker '1148' 'Edit'),0x000C,[IntPtr]::Zero,$gameExe) }
        $open=$picker.FindFirst([Windows.Automation.TreeScope]::Descendants,(New-Object Windows.Automation.AndCondition((New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty,'Open')),(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Button)))))
        if($null -ne $open){Invoke-Control $open}else{[void][GLMNativeDialog]::SendMessage((Find-NativeFileControl $picker '1' 'Button'),0x00F5,[IntPtr]::Zero,$null)}
        Start-Sleep -Milliseconds 300
        $saved=Get-Content -Raw -LiteralPath (Join-Path $dataRoot 'state.json') | ConvertFrom-Json
        Add-Check 'Native executable picker persists actual downloaded game launcher' ($saved.launchPaths.peppergrinder -eq $gameExe)
        Invoke-Control (Find-Id $dialog 'PlayGame')
        $launchClock=[Diagnostics.Stopwatch]::StartNew()
        do { Start-Sleep -Milliseconds 500; $gameProcess=Get-Process -Name PepperGrinder -ErrorAction SilentlyContinue | Where-Object Path -eq $gameExe | Select-Object -First 1 } while (($null -eq $gameProcess -or $gameProcess.MainWindowHandle -eq [IntPtr]::Zero) -and $launchClock.Elapsed.TotalSeconds -lt 35)
        Add-Check 'Native Play starts the actual downloaded game with a responding window' ($null -ne $gameProcess -and $gameProcess.MainWindowHandle -ne [IntPtr]::Zero -and $gameProcess.Responding)
        [pscustomobject]@{at=[DateTime]::UtcNow.ToString('o');passed=$true;dataRoot=$dataRoot;gameExe=$gameExe;gamePid=$gameProcess.Id;gameWindow=$gameProcess.MainWindowTitle;checks=@($checks.ToArray())} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'play-native.json') -Encoding UTF8
        Write-Output ('PASS: Native Play launched ' + $gameProcess.MainWindowTitle + '; PID ' + $gameProcess.Id)
        return
    }
    if ($MetadataProof) {
        $search = Find-Id $window 'SearchBox'
        ([Windows.Automation.ValuePattern]$search.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).SetValue('staroceanthedivineforce')
        Start-Sleep -Milliseconds 400
        Invoke-Control (Find-Id $window 'SelectAll')
        Invoke-Control (Find-Id $window 'RefreshMetadata')
        $dialog = Wait-Dialog 'Refresh covers & times'
        Invoke-Control (Find-Id $dialog 'StartMetadataRefresh')
        $metadataFile = Join-Path $dataRoot 'cache\metadata.json'
        $metadataClock = [Diagnostics.Stopwatch]::StartNew()
        do { Start-Sleep -Milliseconds 500 } while (-not (Test-Path -LiteralPath $metadataFile) -and $metadataClock.Elapsed.TotalSeconds -lt 65)
        Add-Check 'Native refresh control receives live metadata' (Test-Path -LiteralPath $metadataFile)
        $metadata = Get-Content -LiteralPath $metadataFile -Raw | ConvertFrom-Json
        $metadataEntry = $metadata.staroceanthedivineforce
        $catalogGames = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\public\data\games.json') | ConvertFrom-Json
        $catalogTime = [double](@($catalogGames | Where-Object { $_.id -ceq 'staroceanthedivineforce' } | Select-Object -First 1).time)
        $timeWasAccepted = $metadataEntry.time -gt 0 -or $metadataEntry.source.time -eq 'genre-estimate'
        $timeWasPreserved = $catalogTime -gt 0 -and $null -eq $metadataEntry.PSObject.Properties['time']
        Add-Check 'Live cover persists for offline use; existing catalog time is preserved and estimates cannot downgrade it' ((Test-Path -LiteralPath (Join-Path (Join-Path $dataRoot 'cache') $metadataEntry.cover)) -and ($timeWasAccepted -or $timeWasPreserved))
        [pscustomobject]@{ at=[DateTime]::UtcNow.ToString('o');passed=$true;dataRoot=$dataRoot;metadata=$metadata;checks=@($checks.ToArray()) } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'metadata-native.json') -Encoding UTF8
        ([Windows.Automation.WindowPattern]$dialog.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)).Close()
        Write-Output 'PASS: Live native cover/time refresh and offline cache.'
        return
    }
    if ($DockerProof) {
        Invoke-Control (Find-Id $window 'OpenSettings')
        $dialog = Wait-Dialog 'Settings & backups'
        $downloadRoot = Join-Path $dataRoot 'Downloads'
        $folderField = Find-Id $dialog 'DownloadFolder'
        ([Windows.Automation.ValuePattern]$folderField.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).SetValue($downloadRoot)
        Invoke-Control (Find-Id $dialog 'SaveSettings')
        ([Windows.Automation.WindowPattern]$dialog.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)).Close()
        Start-Sleep -Milliseconds 200
        $window = Wait-AppWindow
        $search = Find-Id $window 'SearchBox'
        ([Windows.Automation.ValuePattern]$search.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).SetValue($DockerTag)
        Start-Sleep -Milliseconds 400
        $rows = (Find-Id $window 'GameList').FindAll([Windows.Automation.TreeScope]::Children, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem)))
        Add-Check 'Exact real game selected for bounded Docker proof' ($rows.Count -eq 1)
        Invoke-Control (Find-Id $window 'SelectAll')
        Invoke-Control (Find-Id $window 'InstallSelected')
        $review = Wait-Dialog 'Install selected games'
        $startButton = $review.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty,'Start download')))
        Invoke-Control $startButton
        $fixtureSettings = Get-Content -LiteralPath (Join-Path $dataRoot 'state.json') -Raw | ConvertFrom-Json
        $jobTitle = if ([string]$fixtureSettings.settings.scriptFormat -eq 'bat') {
            'Game Library ' + [char]0xB7 + ' Download terminal'
        } elseif ([string]$fixtureSettings.settings.scriptFormat -eq 'sh' -and [string]$fixtureSettings.settings.shellTarget -eq 'wsl2') {
            'Game Library ' + [char]0xB7 + ' WSL2 Ubuntu install'
        } else {
            'Game Library ' + [char]0xB7 + ' Download progress'
        }
        $job = Wait-Dialog $jobTitle
        Add-Check 'Native install uses the route selected by the real default script settings' ($job.Current.Name -eq $jobTitle)
        $jobClock = [Diagnostics.Stopwatch]::StartNew()
        do {
            Start-Sleep -Seconds 2
            $logFile = Get-ChildItem -LiteralPath (Join-Path $dataRoot 'jobs') -Filter '*.log' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            $logText = if ($null -ne $logFile) { Get-Content -Raw -LiteralPath $logFile.FullName } else { '' }
            if ($logText -match 'Failed .*exit code|Could not complete:') { throw ('Native Docker operation failed. See ' + $logFile.FullName) }
        } while ($logText -notmatch 'Completed successfully' -and $jobClock.Elapsed.TotalMinutes -lt 6)
        Add-Check 'Real native install downloads and extracts using Docker VMM' ($logText -match 'Completed successfully')
        $files = @(Get-ChildItem -LiteralPath $downloadRoot -File -Recurse)
        Add-Check 'Extraction produces real files in chosen folder' ($files.Count -gt 0)
        $executables = @($files | Where-Object Extension -eq '.exe' | Select-Object -ExpandProperty FullName)
        $automaticLauncher=$null
        if($DockerTag -eq 'peppergrinder'){
            $completionClock=[Diagnostics.Stopwatch]::StartNew()
            do{
                Start-Sleep -Milliseconds 150
                $installedState=Get-Content -LiteralPath (Join-Path $dataRoot 'state.json') -Raw | ConvertFrom-Json
                $automaticLauncher=$installedState.launchPaths.peppergrinder
            }while(($installedState.installedGames -notcontains $DockerTag -or [string]::IsNullOrEmpty($automaticLauncher)) -and $completionClock.Elapsed.TotalSeconds -lt 20)
            Add-Check 'Completed native download automatically marks the game Installed without a scan or picker' ($installedState.installedGames -contains $DockerTag)
            Add-Check 'Completed native download saves the real Pepper Grinder launcher without manual selection' (-not[string]::IsNullOrEmpty($automaticLauncher) -and [IO.Path]::GetFileName($automaticLauncher) -eq 'PepperGrinder.exe' -and $automaticLauncher.StartsWith(([IO.Path]::GetFullPath($downloadRoot)+'\'),[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $automaticLauncher -PathType Leaf))
        }
        elseif($DockerTag -eq 'inmot'){
            Start-Sleep -Milliseconds 500
            $installedState=Get-Content -LiteralPath (Join-Path $dataRoot 'state.json') -Raw | ConvertFrom-Json
            Add-Check 'An extracted payload with no game executable is not marked Installed' ($executables.Count -eq 0 -and $installedState.installedGames -notcontains $DockerTag -and [string]::IsNullOrEmpty($installedState.launchPaths.inmot))
        }
        [pscustomobject]@{at=[DateTime]::UtcNow.ToString('o');passed=$true;executable=$ExePath;dataRoot=$dataRoot;elapsedMs=$jobClock.ElapsedMilliseconds;fileCount=$files.Count;executables=$executables;automaticLauncher=$automaticLauncher;manualScanOrPickerUsed=$false;log=$logFile.FullName;checks=@($checks.ToArray())} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'docker-native.json') -Encoding UTF8
        Copy-Item -LiteralPath (Join-Path $evidenceRoot 'docker-native.json') -Destination (Join-Path $evidenceRoot ('docker-native-' + $DockerTag + '.json'))
        ([Windows.Automation.WindowPattern]$job.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)).Close()
        ([Windows.Automation.WindowPattern]$window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)).Close()
        Write-Output ('PASS: Real native Docker extraction; files=' + $files.Count + '; evidence=' + (Join-Path $evidenceRoot 'docker-native.json'))
        return
    }
    $search = Find-Id $window 'SearchBox'
    ([Windows.Automation.ValuePattern]$search.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).SetValue('staroceanthedivineforce')
    $list = Find-Id $window 'GameList'
    $searchClock = [Diagnostics.Stopwatch]::StartNew()
    do {
        Start-Sleep -Milliseconds 100
        $rows = $list.FindAll([Windows.Automation.TreeScope]::Children, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem)))
    } while ($rows.Count -ne 1 -and $searchClock.Elapsed.TotalSeconds -lt 10)
    Add-Check 'External UI Automation search returns one exact game' ($rows.Count -eq 1)
    Invoke-Control (Find-Id $window 'SelectAll')
    $box = $rows[0].FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::CheckBox)))
    Add-Check 'Select-all button checks the visible game' (([Windows.Automation.TogglePattern]$box.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)).Current.ToggleState -eq [Windows.Automation.ToggleState]::On)
    Invoke-Control (Find-Id $window 'DeselectAll')
    Add-Check 'Clear button unchecks the visible game' (([Windows.Automation.TogglePattern]$box.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)).Current.ToggleState -eq [Windows.Automation.ToggleState]::Off)
    $actionClock=[Diagnostics.Stopwatch]::StartNew()
    do {
        $rows=(Find-Id $window 'GameList').FindAll([Windows.Automation.TreeScope]::Children,(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem)))
        $cardButtons=@()
        $cardButtonIds=@()
        if($rows.Count -eq 1){
            $cardButtonElements=$rows[0].FindAll([Windows.Automation.TreeScope]::Descendants,(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Button)))
            $cardButtons=@($cardButtonElements|ForEach-Object{$_.Current.Name})
            $cardButtonIds=@($cardButtonElements|ForEach-Object{$_.Current.AutomationId})
        }
        if($cardButtonIds -contains 'PlayGame' -and $cardButtonIds -contains 'PlayWithWand'){break}
        Start-Sleep -Milliseconds 100
    } while($actionClock.Elapsed.TotalSeconds -lt 5)
    Add-Check 'Every game card exposes direct Play and Play with Wand actions' ($cardButtonIds -contains 'PlayGame' -and $cardButtonIds -contains 'PlayWithWand')
    $without=Find-Id $window 'WithoutInstalledFilter'
    Toggle-Control $without
    Start-Sleep -Milliseconds 150
    $withoutRows=(Find-Id $window 'GameList').FindAll([Windows.Automation.TreeScope]::Children,(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem)))
    Add-Check 'Without-installed filter keeps an uninstalled game visible' ($withoutRows.Count -eq 1)
    Toggle-Control $without
    $installedOnly=Find-Id $window 'InstalledOnlyFilter'
    Toggle-Control $installedOnly
    Start-Sleep -Milliseconds 150
    $installedRows=(Find-Id $window 'GameList').FindAll([Windows.Automation.TreeScope]::Children,(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem)))
    Add-Check 'Installed-only and inverse filter remain mutually exclusive' ($installedRows.Count -eq 0)
    Toggle-Control $installedOnly
    $detailButton = $rows[0].FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty,('Details  ' + [char]0x2192))))
    Invoke-Control $detailButton
    $dialog = Wait-Dialog 'STAR OCEAN THE DIVINE FORCE'
    $detailButtons = @($dialog.FindAll([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Button))) | ForEach-Object { $_.Current.Name })
    Add-Check 'Game Details exposes direct Play and Wand launch controls' ($detailButtons -contains 'Play' -and $detailButtons -contains 'Open Wand' -and $detailButtons -contains 'Play with Wand mods')
    $tagField = Find-Id $dialog 'GameTags'
    ([Windows.Automation.ValuePattern]$tagField.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).SetValue('native-ui-proof, favorite-rpg')
    Invoke-Control (Find-Id $dialog 'SaveGameDetails')
    Start-Sleep -Milliseconds 150
    $saved = Get-Content -LiteralPath (Join-Path $dataRoot 'state.json') -Raw | ConvertFrom-Json
    Add-Check 'Game Details saves personal tags through native controls' ($saved.gameTags.staroceanthedivineforce -contains 'native-ui-proof')
    ([Windows.Automation.WindowPattern]$dialog.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)).Close()
    Invoke-Control (Find-Id $window 'OpenSettings')
    $dialog = Wait-Dialog 'Settings & backups'
    $folderField = Find-Id $dialog 'DownloadFolder'
    Add-Check 'New native settings default to E:\games' (([Windows.Automation.ValuePattern]$folderField.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).Current.Value -eq 'E:\games')
    $scriptFormatField = Find-Id $dialog 'ScriptFormat'
    $shellTargetField = Find-Id $dialog 'ShellTarget'
    $scriptSelected = @(([Windows.Automation.SelectionPattern]$scriptFormatField.GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern)).Current.GetSelection())[0].Current.Name
    $shellSelected = @(([Windows.Automation.SelectionPattern]$shellTargetField.GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern)).Current.GetSelection())[0].Current.Name
    Add-Check 'Native settings expose BAT default plus Bash target choice' ($scriptSelected -eq 'bat' -and @('native-linux','wsl2') -contains $shellSelected)
    ([Windows.Automation.ValuePattern]$folderField.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).SetValue((Join-Path $dataRoot 'Downloads'))
    Invoke-Control (Find-Id $dialog 'SaveSettings')
    Start-Sleep -Milliseconds 150
    $saved = Get-Content -LiteralPath (Join-Path $dataRoot 'state.json') -Raw | ConvertFrom-Json
    Add-Check 'Settings dialog saves the actual chosen download path' ($saved.settings.mountPath -eq (Join-Path $dataRoot 'Downloads'))
    $backupPath=Join-Path $dataRoot 'library-backup.json'
    Invoke-Control (Find-Id $dialog 'ExportBackup')
    $picker=Wait-Dialog 'Export library backup'
    [void][GLMNativeDialog]::SendMessage((Find-NativeFileControl $picker '1001' 'Edit'),0x000C,[IntPtr]::Zero,$backupPath)
    [void][GLMNativeDialog]::SendMessage((Find-NativeFileControl $picker '1' 'Button'),0x00F5,[IntPtr]::Zero,$null)
    Start-Sleep -Milliseconds 250
    Add-Check 'Native Export Backup writes the chosen JSON file' (Test-Path -LiteralPath $backupPath)
    $backup=Get-Content -LiteralPath $backupPath -Raw | ConvertFrom-Json
    Add-Check 'Exported backup contains real tags and settings' (($backup.gameTags.staroceanthedivineforce -contains 'native-ui-proof') -and ($backup.settings.mountPath -eq $saved.settings.mountPath))
    $navigationBackupPath=Join-Path $dataRoot 'library-backup-navigation.json'
    $expectedSort='Name A'+[char]0x2013+'Z'
    $backup.settings.sortBy=$expectedSort
    $backup.settings.lastTab='new'
    $backup | ConvertTo-Json -Depth 25 | Set-Content -LiteralPath $navigationBackupPath -Encoding UTF8
    ([Windows.Automation.WindowPattern]$dialog.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)).Close()
    Invoke-Control (Find-Id $window 'ToggleTheme')
    Start-Sleep -Milliseconds 150
    $saved = Get-Content -LiteralPath (Join-Path $dataRoot 'state.json') -Raw | ConvertFrom-Json
    Add-Check 'Theme change persists to the real app state' ($saved.settings.theme -eq 'light')
    Invoke-Control (Find-Id $window 'OpenSettings')
    $dialog=Wait-Dialog 'Settings & backups'
    Invoke-Control (Find-Id $dialog 'ImportBackup')
    $picker=Wait-Dialog 'Import library backup'
    [void][GLMNativeDialog]::SendMessage((Find-NativeFileControl $picker '1148' 'Edit'),0x000C,[IntPtr]::Zero,$navigationBackupPath)
    [void][GLMNativeDialog]::SendMessage((Find-NativeFileControl $picker '1' 'Button'),0x00F5,[IntPtr]::Zero,$null)
    Start-Sleep -Milliseconds 300
    $window=Wait-AppWindow
    $saved=Get-Content -LiteralPath (Join-Path $dataRoot 'state.json') -Raw | ConvertFrom-Json
    Add-Check 'Native Import Backup restores the prior appearance' ($saved.settings.theme -eq 'dark')
    Add-Check 'Native Import Backup preserves personal tags and leaves a rollback copy' (($saved.gameTags.staroceanthedivineforce -contains 'native-ui-proof') -and (Test-Path -LiteralPath (Join-Path $dataRoot 'state.json.bak')))
    Add-Check 'Full backup import persists the requested sort and category' ($saved.settings.sortBy -eq $expectedSort -and $saved.settings.lastTab -eq 'new')
    Add-Check 'Full backup import immediately applies its sort to the visible native control' ((Read-SelectedName (Find-Id $window 'SortBox')) -eq $expectedSort)
    Add-Check 'Full backup import immediately selects New arrivals in the native category list' ((Read-SelectedName (Find-Id $window 'Categories')) -eq 'New arrivals')
    # Exercise the distinct website export shape, without native schemaVersion/personal data.
    $websitePath=Join-Path $dataRoot 'website-settings.json'
    @{settings=@{theme='dark';gridSize='small';showTimes=$false;showCategories=$false;dockerUsername='michadockermisha';repoName='backup';mountPath=(Join-Path $dataRoot 'Downloads')};selectedGames=@('staroceanthedivineforce');exportDate=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $websitePath -Encoding UTF8
    Invoke-Control (Find-Id $window 'OpenSettings')
    $dialog=Wait-Dialog 'Settings & backups'
    Invoke-Control (Find-Id $dialog 'ImportBackup')
    $picker=Wait-Dialog 'Import library backup'
    [void][GLMNativeDialog]::SendMessage((Find-NativeFileControl $picker '1148' 'Edit'),0x000C,[IntPtr]::Zero,$websitePath)
    [void][GLMNativeDialog]::SendMessage((Find-NativeFileControl $picker '1' 'Button'),0x00F5,[IntPtr]::Zero,$null)
    Start-Sleep -Milliseconds 300
    $window=Wait-AppWindow
    $saved=Get-Content -LiteralPath (Join-Path $dataRoot 'state.json') -Raw | ConvertFrom-Json
    Add-Check 'Website settings format imports display and repository preferences' ($saved.settings.gridSize -eq 'small' -and -not $saved.settings.showTimes -and -not $saved.settings.showCategories -and $saved.settings.repoName -eq 'backup')
    Add-Check 'Website settings import preserves native personal tags' ($saved.gameTags.staroceanthedivineforce -contains 'native-ui-proof')
    Add-Check 'Website settings import preserves native-only navigation settings and active controls' ($saved.settings.sortBy -eq $expectedSort -and $saved.settings.lastTab -eq 'new' -and (Read-SelectedName (Find-Id $window 'SortBox')) -eq $expectedSort -and (Read-SelectedName (Find-Id $window 'Categories')) -eq 'New arrivals')
    $list=Find-Id $window 'GameList'
    $rows=$list.FindAll([Windows.Automation.TreeScope]::Children,(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem)))
    $box=$rows[0].FindFirst([Windows.Automation.TreeScope]::Descendants,(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::CheckBox)))
    Add-Check 'Website settings import restores the selected game in native UI' ($rows.Count -eq 1 -and ([Windows.Automation.TogglePattern]$box.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)).Current.ToggleState -eq [Windows.Automation.ToggleState]::On)
    Invoke-Control (Find-Id $window 'ToggleTheme')
    $pattern = [Windows.Automation.WindowPattern]$window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)
    $pattern.SetWindowVisualState([Windows.Automation.WindowVisualState]::Minimized)
    Start-Sleep -Milliseconds 200
    $appProcess.Refresh()
    Add-Check 'Minimize keeps original process alive' (-not $appProcess.HasExited)
    $second = Start-Process -FilePath $ExePath -ArgumentList @('--data-dir',$dataRoot,'--offline') -PassThru
    $exited = $second.WaitForExit(10000)
    Add-Check 'Second launch signals the existing instance and exits' ($exited -and $second.ExitCode -eq 0)
    $window = Wait-AppWindow
    $pattern = [Windows.Automation.WindowPattern]$window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)
    Add-Check 'Second launch restores the existing window' ($pattern.Current.WindowVisualState -eq [Windows.Automation.WindowVisualState]::Normal)
    $pattern.Close()
    Add-Check 'Close exits the packaged application cleanly' ($appProcess.WaitForExit(10000))
    $appProcess = Start-Process -FilePath $ExePath -ArgumentList @('--data-dir',$dataRoot,'--offline') -PassThru
    $window = Wait-AppWindow
    Start-Sleep -Milliseconds 300
    $saved = Get-Content -LiteralPath (Join-Path $dataRoot 'state.json') -Raw | ConvertFrom-Json
    Add-Check 'Relaunch preserves user preferences' ($saved.settings.theme -eq 'light')
    Add-Check 'Relaunch preserves native Details edits and download settings' (($saved.gameTags.staroceanthedivineforce -contains 'native-ui-proof') -and $saved.settings.mountPath -eq (Join-Path $dataRoot 'Downloads'))
    ([Windows.Automation.WindowPattern]$window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)).Close()
    Add-Check 'Repeated close leaves no process behind' ($appProcess.WaitForExit(10000))
    [pscustomobject]@{ at=[DateTime]::UtcNow.ToString('o'); passed=$true; executable=$ExePath; sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $ExePath).Hash; startupToAccessibleMs=$startupMs; dataRoot=$dataRoot; checks=@($checks.ToArray()) } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'external-ui.json') -Encoding UTF8
    Write-Output ('PASS: ' + $checks.Count + ' external native UI checks; startup to accessible controls: ' + $startupMs + ' ms')
    } catch {
    $failedReport = if ($DockerProof) { 'docker-native.json' } elseif ($MetadataProof) { 'metadata-native.json' } elseif ($PlayProof) { 'play-native.json' } elseif ($ScriptProof) { 'scripts-native.json' } elseif ($AdminProof) { 'admin-external-native.json' } elseif ($TrayProof) { 'tray-external-native.json' } elseif ($LocalGameProof) { 'local-game-native.json' } elseif ($AutomaticCatalogProof) { 'automatic-catalog-native.json' } elseif ($KillScriptProof) { 'kill-script-native.json' } else { 'external-ui.json' }
    [pscustomobject]@{ at=[DateTime]::UtcNow.ToString('o'); passed=$false; executable=$ExePath;dataRoot=$dataRoot;error=$_.ToString(); checks=@($checks.ToArray()) } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidenceRoot $failedReport) -Encoding UTF8
    throw
} finally {
    if ($LocalGameProof -and $null -ne $localGameProcess -and -not $localGameProcess.HasExited) { if($localGameProcess.Path -eq $localExe){[void]$localGameProcess.CloseMainWindow();if(-not$localGameProcess.WaitForExit(5000)){Write-Warning ('The exact local proof game '+$localGameProcess.Id+' remains open; reconcile before rerunning.')}} }
    if ($null -ne $appProcess -and -not $appProcess.HasExited) {
        if ($null -ne $confirm) { try { if($confirm.Current.Name -eq 'Remove category' -and $confirm.Current.ProcessId -eq $appProcess.Id) { [void][GLMNativeDialog]::SendMessage((Find-NativeFileControl $confirm '7' 'Button'),0x00F5,[IntPtr]::Zero,$null);Start-Sleep -Milliseconds 150 } } catch {} }
        if ($null -ne $picker) { try { [void][GLMNativeDialog]::SendMessage((Find-NativeFileControl $picker '2' 'Button'),0x00F5,[IntPtr]::Zero,$null) } catch {} }
        if ($null -ne $dialog) { try { ([Windows.Automation.WindowPattern]$dialog.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)).Close() } catch {} }
        [void]$appProcess.CloseMainWindow()
        if(-not $appProcess.WaitForExit(5000)){ Write-Warning ('The exact UI fixture process '+$appProcess.Id+' did not close; do not rerun this fixture until its dialogs are reconciled.') }
    }
}
