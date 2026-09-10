param(
    [Parameter(Mandatory=$true)][string]$ScriptPath,
    [string]$ReportPath = ''
)
$ErrorActionPreference = 'Stop'
if (-not $ReportPath) { $ReportPath=Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'evidence\killall-behavior.json' }
$ScriptPath = (Resolve-Path -LiteralPath $ScriptPath).Path
if ([IO.Path]::GetExtension($ScriptPath) -cne '.ps1') { throw 'Supply the actual exported PowerShell script; BAT execution is not allowed in this fixture.' }
$tokens=$null; $parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($ScriptPath,[ref]$tokens,[ref]$parseErrors)
if ($parseErrors.Count) { throw 'The exported script does not parse.' }
# Fail closed if the payload gains an execution route that the isolated fixture cannot intercept.
$allowed=@('Get-Command','Join-Path','Test-Path','Out-String','ForEach-Object','Write-Output','Read-Host','Get-Date','Write-Warning','Write-Error')
foreach($command in $ast.FindAll({param($n) $n -is [Management.Automation.Language.CommandAst]},$true)) {
    $name=$command.GetCommandName()
    if ($name) { if ($allowed -cnotcontains $name) { throw ('Unmocked payload command: '+$name) } }
    elseif ($command.CommandElements[0] -isnot [Management.Automation.Language.VariableExpressionAst] -or $command.CommandElements[0].VariablePath.UserPath -cne 'dockerExecutable') { throw 'Unknown dynamic payload invocation.' }
}
if ($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -or $n -is [Management.Automation.Language.TypeExpressionAst]},$true).Count) { throw 'The payload contains an unreviewed function or type execution route.' }
foreach($call in $ast.FindAll({param($n) $n -is [Management.Automation.Language.InvokeMemberExpressionAst]},$true)) {
    if ($call.Member.Extent.Text -cne 'Trim' -or $call.Arguments.Count -ne 0) { throw 'Unknown payload member invocation.' }
}
$originalHash=(Get-FileHash -LiteralPath $ScriptPath -Algorithm SHA256).Hash
$ReportPath=[IO.Path]::GetFullPath($ReportPath)
$fixture=Join-Path ([IO.Path]::GetDirectoryName($ReportPath)) ('killall-behavior-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
[void][IO.Directory]::CreateDirectory($fixture)
$child=Join-Path $fixture 'isolated-child.ps1'
@'
param([string]$CasePath)
$ErrorActionPreference='Stop'
$case=[IO.File]::ReadAllText($CasePath) | ConvertFrom-Json
$script:NativeTestPath=Get-Command Test-Path -CommandType Cmdlet
$script:Calls=New-Object 'System.Collections.Generic.List[object]'
$script:Violations=New-Object 'System.Collections.Generic.List[string]'
$script:Prompts=New-Object 'System.Collections.Generic.List[string]'
$script:ActiveContext=$case.context
$env:DOCKER_CONTEXT=$case.dockerContext
$env:DOCKER_HOST=$case.dockerHost
$env:DOCKER_TLS_VERIFY=$null
$env:DOCKER_CERT_PATH=$null
function Reject-Fixture([string]$Reason) { $script:Violations.Add($Reason); throw $Reason }
function Get-Command {
    [CmdletBinding()]param([Parameter(Position=0)][string]$Name)
    if ($Name -cne 'docker.exe') { Reject-Fixture ('Unexpected Get-Command: '+$Name) }
    [pscustomobject]@{Source='Invoke-FixtureDocker'}
}
function Test-Path {
    [CmdletBinding()]param([string]$LiteralPath,[string]$PathType)
    if ($LiteralPath -ceq 'Invoke-FixtureDocker' -and $PathType -ceq 'Leaf') { return $true }
    if ($LiteralPath -match '(?i)docker') { Reject-Fixture ('Attempted real Docker path: '+$LiteralPath) }
    & $script:NativeTestPath @PSBoundParameters
}
function Read-Host {
    $prompt=($args -join ' ')
    if ($prompt -cne 'Type DELETE ALL to remove these containers, or anything else to cancel') { Reject-Fixture ('Unexpected confirmation prompt: '+$prompt) }
    $script:Prompts.Add($prompt)
    if ($case.changedContext) { $script:ActiveContext=$case.changedContext }
    return [string]$case.answer
}
function Invoke-FixtureDocker {
    $parts=@($args | ForEach-Object {[string]$_})
    $script:Calls.Add([pscustomobject]@{arguments=$parts;activeContext=$script:ActiveContext})
    $global:LASTEXITCODE=0
    if (($parts -join '|') -ceq 'context|show') {
        if ($case.contextFailure) { $global:LASTEXITCODE=17; return }
        if ($env:DOCKER_CONTEXT) { return $env:DOCKER_CONTEXT }
        return $script:ActiveContext
    }
    if ($parts.Count -lt 2 -or $parts[0] -cne $case.targetKind -or $parts[1] -cne $case.targetValue) { Reject-Fixture ('Docker target was not pinned: '+($parts -join ' ')) }
    $tail=@($parts | Select-Object -Skip 2)
    if (($tail -join '|') -ceq 'container|ls|--all|--no-trunc|--format|{{.ID}} {{.Names}} {{.Status}}') {
        if ($case.listFailure) { $global:LASTEXITCODE=23; return }
        foreach($line in $case.listing) { Write-Output ([string]$line) }
        return
    }
    if ($tail.Count -eq 4 -and $tail[0] -ceq 'container' -and $tail[1] -ceq 'rm' -and $tail[2] -ceq '--force') {
        $id=$tail[3]
        if (@($case.allowedIds) -cnotcontains $id) { Reject-Fixture ('Unlisted container removal: '+$id) }
        if ($id -ceq $case.failedId) { $global:LASTEXITCODE=31; Write-Output ('fixture removal failed: '+$id); return }
        Write-Output ('fixture removed: '+$id); return
    }
    Reject-Fixture ('Unknown Docker invocation: '+($parts -join ' '))
}
try {
    if ($PSVersionTable.PSVersion.Major -ne 5) { throw 'This fixture must run in Windows PowerShell 5.' }
    # Dot-source the unchanged real export, then propagate its exit status to the child.
    . $case.scriptPath
    exit $LASTEXITCODE
} finally {
    $receipt=[pscustomobject]@{powershell=$PSVersionTable.PSVersion.ToString();mocked=$true;calls=@($script:Calls.ToArray());prompts=@($script:Prompts.ToArray());violations=@($script:Violations.ToArray())}
    [IO.File]::WriteAllText($case.receiptPath,($receipt | ConvertTo-Json -Depth 12))
}
'@ | Set-Content -LiteralPath $child -Encoding UTF8
$idA='a'*64; $idB='b'*64
$normal=@(($idA+' fixture-alpha Up 1 minute'),($idB+' fixture-beta Exited (0)'))
$cases=@(
    @{name='Cancel confirmation removes nothing';answer='CANCEL';expectedExit=0;expectedRemoved=@();expectedPrompts=1},
    @{name='Confirmation is case-sensitive';answer='delete all';expectedExit=0;expectedRemoved=@();expectedPrompts=1},
    @{name='Empty Docker target exits without prompting';answer='DELETE ALL';listing=@();expectedExit=0;expectedRemoved=@();expectedPrompts=0},
    @{name='Listing failure is nonzero and removes nothing';answer='DELETE ALL';listFailure=$true;expectedExit=1;expectedRemoved=@();expectedPrompts=0},
    @{name='Malformed container ID rejects the entire listing';answer='DELETE ALL';listing=@(($idA+' fixture-alpha Up'),('not-an-id fixture-bad Up'));expectedExit=1;expectedRemoved=@();expectedPrompts=0},
    @{name='Confirmed removal covers every listed ID with original context pinned';answer='DELETE ALL';changedContext='different-context-after-review';expectedExit=0;expectedRemoved=@($idA,$idB);expectedPrompts=1},
    @{name='DOCKER_HOST overrides configured current context';answer='DELETE ALL';dockerHost='npipe:////./pipe/fixture-only';targetKind='--host';targetValue='npipe:////./pipe/fixture-only';expectedExit=0;expectedRemoved=@($idA,$idB);expectedPrompts=1},
    @{name='Explicit DOCKER_CONTEXT takes priority over DOCKER_HOST';answer='DELETE ALL';dockerContext='explicit-fixture-context';dockerHost='npipe:////./pipe/unused-fixture';targetValue='explicit-fixture-context';expectedExit=0;expectedRemoved=@($idA,$idB);expectedPrompts=1},
    @{name='A removal failure is reported nonzero after attempting remaining listed IDs';answer='DELETE ALL';failedId=$idA;expectedExit=1;expectedRemoved=@($idA,$idB);expectedPrompts=1},
    @{name='Context resolution failure cannot list or remove containers';answer='DELETE ALL';contextFailure=$true;expectedExit=1;expectedRemoved=@();expectedPrompts=0}
)
$results=New-Object 'System.Collections.Generic.List[object]'
$powershell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
function Invoke-FixtureChild([string]$ArgumentText,[string]$Label) {
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=$powershell; $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $start.Arguments=$ArgumentText
    $process=New-Object Diagnostics.Process; $process.StartInfo=$start
    try {
        [void]$process.Start()
        $stdoutTask=$process.StandardOutput.ReadToEndAsync(); $stderrTask=$process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(15000)) { $process.Kill(); throw ('Isolated proof child timed out: '+$Label) }
        return [pscustomobject]@{id=$process.Id;exitCode=$process.ExitCode;stdout=$stdoutTask.GetAwaiter().GetResult();stderr=$stderrTask.GetAwaiter().GetResult()}
    } finally { $process.Dispose() }
}
$calibration=@(foreach($expectedExit in 0,7) {
    $inert=Invoke-FixtureChild ('-NoLogo -NoProfile -NonInteractive -Command "exit '+$expectedExit+'"') 'inert exit calibration'
    if ($null -eq $inert.exitCode -or $inert.exitCode -ne $expectedExit) { throw ('Process exit capture calibration failed for '+$expectedExit) }
    [pscustomobject]@{command=('exit '+$expectedExit);passed=$true;expectedExit=$expectedExit;actualExit=$inert.exitCode;childPid=$inert.id}
})
foreach($definition in $cases) {
    $index=$results.Count+1
    $case=@{name=$definition.name;context='desktop-linux-fixture';dockerContext='';dockerHost='';targetKind='--context';targetValue='desktop-linux-fixture';listing=$normal;allowedIds=@($idA,$idB);failedId='';contextFailure=$false;listFailure=$false;changedContext='';scriptPath=$ScriptPath;receiptPath=(Join-Path $fixture ('case-'+$index+'.json'))}
    foreach($key in $definition.Keys) { $case[$key]=$definition[$key] }
    $inputPath=Join-Path $fixture ('case-'+$index+'-input.json')
    $case | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $inputPath -Encoding UTF8
    $stdout=Join-Path $fixture ('case-'+$index+'.stdout.log'); $stderr=Join-Path $fixture ('case-'+$index+'.stderr.log')
    $process=Invoke-FixtureChild ('-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$child+'" -CasePath "'+$inputPath+'"') $case.name
    $exitCode=$process.ExitCode
    [IO.File]::WriteAllText($stdout,$process.stdout); [IO.File]::WriteAllText($stderr,$process.stderr)
    $receipt=Get-Content -LiteralPath $case.receiptPath -Raw | ConvertFrom-Json
    $removals=@($receipt.calls | Where-Object { $_.arguments.Count -eq 6 -and $_.arguments[2] -ceq 'container' -and $_.arguments[3] -ceq 'rm' })
    $removedIds=@($removals | ForEach-Object { $_.arguments[5] })
    $passed=($exitCode -eq $case.expectedExit -and @($receipt.violations).Count -eq 0 -and ($removedIds -join '|') -ceq (@($case.expectedRemoved) -join '|') -and @($receipt.prompts).Count -eq $case.expectedPrompts -and $receipt.powershell.StartsWith('5.'))
    $result=[pscustomobject]@{name=$case.name;passed=$passed;childPid=$process.Id;exitCode=$exitCode;expectedExit=$case.expectedExit;removalCalls=$removedIds;promptCount=@($receipt.prompts).Count;violations=@($receipt.violations);receipt=$case.receiptPath;stdout=$stdout;stderr=$stderr}
    $results.Add($result)
    Write-Output (($index.ToString()+'/'+$cases.Count)+' '+$case.name+': '+$passed+' (exit '+$exitCode+')')
}
$unchanged=(Get-FileHash -LiteralPath $ScriptPath -Algorithm SHA256).Hash -eq $originalHash
$passed=$unchanged -and @($results | Where-Object {-not $_.passed}).Count -eq 0
$report=[pscustomobject]@{at=[DateTime]::UtcNow.ToString('o');passed=$passed;actualExport=$ScriptPath;scriptSha256=$originalHash;scriptUnchanged=$unchanged;headless=$true;mockDockerOnly=$true;realDockerExecuted=$false;fixture=$fixture;exitCaptureCalibration=$calibration;checks=@($results.ToArray())}
$report | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
if (-not $passed) { throw ('Kill All behavior verification failed; see '+$ReportPath) }
Write-Output ('Verified '+$results.Count+' actual-script mock scenarios. Report: '+$ReportPath)
