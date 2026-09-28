param(
    [Parameter(Mandatory=$true)][string]$FixtureExecutable,
    [Parameter(Mandatory=$true)][string]$AssemblyPath,
    [Parameter(Mandatory=$true)][string]$DotnetRoot
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$evidence = Join-Path $repo 'evidence'
$suite = Join-Path $evidence ('qualified-installs-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($suite)
$fixture = (Resolve-Path -LiteralPath $FixtureExecutable).Path
$assembly = (Resolve-Path -LiteralPath $AssemblyPath).Path
$checks = [Collections.Generic.List[object]]::new()
function Check([string]$Name, [bool]$Passed) {
    $checks.Add([pscustomobject]@{Name=$Name;Passed=$Passed})
    if (-not $Passed) { throw $Name }
}
function Run-Fixture([string]$Executable, [string[]]$Arguments, [string]$Root, [bool]$InvalidFirst, [string]$LogName) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    if ([IO.Path]::GetFileName($Executable) -ieq 'cmd.exe') {
        # cmd uses its own /s quote removal, not CommandLineToArgvW escaping.
        $start.Arguments = '/d /s /c ""' + (Join-Path $Root 'install.bat') + '""'
    } else {
        foreach($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    }
    $start.Environment['PATH'] = [IO.Path]::GetDirectoryName($fixture) + ';' + $env:PATH
    $start.Environment['DOTNET_ROOT'] = $DotnetRoot
    $start.Environment['GLM_FIXTURE_ROOT'] = $Root
    $start.Environment['GLM_FIXTURE_INVALID_FIRST'] = $(if($InvalidFirst){'1'}else{'0'})
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(55000)) {
            $process.Kill($true)
            throw 'Owned fixture process exceeded its timeout.'
        }
        [IO.File]::WriteAllText((Join-Path $Root ($LogName+'.log')), $output.GetAwaiter().GetResult()+$errorOutput.GetAwaiter().GetResult())
        return $process.ExitCode
    } finally { $process.Dispose() }
}
try {
    foreach($format in @('ps1','bat')) {
        foreach($invalidFirst in @($false,$true)) {
            $root = [IO.Path]::GetFullPath((Join-Path $suite ($format+' space '+$invalidFirst)))
            if (-not $root.StartsWith($suite+[IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture path escaped its suite.' }
            [void][IO.Directory]::CreateDirectory($root)
            $generated = Run-Fixture $fixture @('--generate',$assembly,$root) $root $invalidFirst 'generate'
            Check "$format/$invalidFirst generate" ($generated -eq 0)
            $manifest = Get-Content -Raw -LiteralPath (Join-Path $root 'fixture.json') | ConvertFrom-Json
            $first = Join-Path (Join-Path $root 'installed') $manifest.folders[0]
            $second = Join-Path (Join-Path $root 'installed') $manifest.folders[1]
            [void][IO.Directory]::CreateDirectory($first)
            [IO.File]::WriteAllText((Join-Path $first 'Game.exe'),'previous-game')
            [IO.File]::WriteAllText((Join-Path $root 'untouched.txt'),'outside install destination')
            # Generated recursive staging operations are constrained to the freshly
            # created fixture mount; no real game directory is supplied to this test.
            $scriptPath = Join-Path $root ('install.'+$format)
            if ($format -eq 'ps1') {
                $exitCode = Run-Fixture (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$scriptPath) $root $invalidFirst 'execution'
            } else {
                $exitCode = Run-Fixture (Join-Path $env:SystemRoot 'System32\cmd.exe') @('/d','/c',('"'+$scriptPath+'"')) $root $invalidFirst 'execution'
            }
            Check "$format/$invalidFirst correct exit" ($exitCode -eq $(if($invalidFirst){1}else{0}))
            $calls = @(Get-Content -LiteralPath (Join-Path $root 'calls.jsonl') | ForEach-Object { ,(ConvertFrom-Json -InputObject $_ -NoEnumerate) })
            $pulls = @($calls | Where-Object {$_[0] -eq 'pull'} | ForEach-Object {$_[1]})
            Check "$format/$invalidFirst exact images" (($pulls -join '|') -ceq 'fixture/one:latest|fixture/two:latest')
            Check "$format/$invalidFirst distinct destinations" ($first -cne $second)
            Check "$format/$invalidFirst first payload" ((Get-Content -Raw -LiteralPath (Join-Path $first 'Game.exe')) -ceq $(if($invalidFirst){'previous-game'}else{'fixture/one:latest'}))
            Check "$format/$invalidFirst second payload" ((Get-Content -Raw -LiteralPath (Join-Path $second 'Game.exe')) -ceq 'fixture/two:latest')
            Check "$format/$invalidFirst exact completion identity" ((Get-Content -Raw -LiteralPath (Join-Path $second '.gamelibrarymanager-install-complete')).StartsWith('GameLibraryManager|docker:fixture/two:latest|',[StringComparison]::Ordinal))
            Check "$format/$invalidFirst outside file preserved" ((Get-Content -Raw -LiteralPath (Join-Path $root 'untouched.txt')) -ceq 'outside install destination')
            Check "$format/$invalidFirst scoped container labels" (@($calls | Where-Object {$_[0] -eq 'container' -and $_[1] -eq 'create' -and (($_ -join '|') -match 'com.gamelibrary.game-id=docker:fixture/(one|two):latest')}).Count -eq 2)
        }
    }
    $passed = $true
} catch { $passed = $false; $failure = $_.Exception.Message }
$report = [pscustomobject]@{Passed=$passed;At=[DateTime]::UtcNow;AssemblySha256=(Get-FileHash -LiteralPath $assembly).Hash;FixtureSha256=(Get-FileHash -LiteralPath $fixture).Hash;Suite=$suite;RealDockerInvoked=$false;Checks=$checks;Failure=$failure}
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence 'criteria-qualified-install-execution-20260922.json')
$report | Select-Object Passed,Suite,Failure,@{Name='CheckCount';Expression={$_.Checks.Count}} | ConvertTo-Json
if (-not $passed) { exit 1 }
