param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$EvidenceRoot,
    [ValidateSet('Baseline', 'Stage', 'Target')][string]$Phase = 'Stage',
    [switch]$NonUiOnly,
    [switch]$IncludeLiveGames,
    [switch]$IncludeRealDocker
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$evidenceBase = [IO.Path]::GetFullPath((Join-Path $repo 'evidence')).TrimEnd('\') + '\'
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot).TrimEnd('\')
if (-not ($EvidenceRoot + '\').StartsWith($evidenceBase, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'EvidenceRoot must be under this repository evidence directory.'
}
$ExePath = [IO.Path]::GetFullPath($ExePath)
if (-not [IO.File]::Exists($ExePath)) { throw "Executable is missing: $ExePath" }
$runId = [guid]::NewGuid().ToString('N')
$phaseRoot = Join-Path (Join-Path $EvidenceRoot $Phase.ToLowerInvariant()) $runId
[IO.Directory]::CreateDirectory($phaseRoot) | Out-Null
$profile = Join-Path $phaseRoot 'isolated-profile'
[IO.Directory]::CreateDirectory($profile) | Out-Null
$started = [DateTimeOffset]::UtcNow
$hashBefore = (Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash
$results = New-Object 'System.Collections.Generic.List[object]'
$packageRoot = Split-Path $ExePath -Parent
$packageManifest = Join-Path $phaseRoot 'package-manifest.json'
$packageFiles = @(Get-ChildItem -LiteralPath $packageRoot -File -Recurse | ForEach-Object {
    [ordered]@{
        path = $_.FullName.Substring($packageRoot.Length + 1).Replace('\', '/')
        length = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
})
$packageFiles | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $packageManifest -Encoding UTF8
$requiredPackageFiles = @(
    'GameLibrary.exe',
    'D3DCompiler_47_cor3.dll',
    'PenImc_cor3.dll',
    'PresentationNative_cor3.dll',
    'vcruntime140_cor3.dll',
    'wpfgfx_cor3.dll',
    'tools/node/node.exe',
    'tools/wemod_add_custom_install.js',
    'tools/wand_cdp_launch.js',
    'tools/wand_trainer_status.js',
    'tools/wand_tophat_evidence.js',
    'tools/wand_live_library.js',
    'tools/wand-supported-games.json',
    'tools/wand-runtime/classic-level/prebuilds/win32-x64/classic-level.node'
)
$missingPackageFiles = @($requiredPackageFiles | Where-Object { $_ -notin @($packageFiles | ForEach-Object { $_.path }) })
$packageManifestPassed = $missingPackageFiles.Count -eq 0
$packageManifestStatus = if ($packageManifestPassed) { 'Passed' } else { 'Failed' }
$packageManifestBlocker = if ($packageManifestPassed) { $null } else { 'Required package files missing: ' + ($missingPackageFiles -join ', ') }
$results.Add([ordered]@{
    testName = 'package-manifest'; passed = $packageManifestPassed; status = $packageManifestStatus
    blocker = $packageManifestBlocker; fileCount = $packageFiles.Count
    requiredFiles = $requiredPackageFiles; missingRequiredFiles = $missingPackageFiles
    artifacts = @($packageManifest)
})

function Quote-Argument([string]$value) {
    if ($value.Length -gt 0 -and $value -notmatch '[\s"]') { return $value }
    $output = New-Object System.Text.StringBuilder
    [void]$output.Append('"')
    $slashes = 0
    foreach ($character in $value.ToCharArray()) {
        if ($character -eq '\') { $slashes++; continue }
        if ($character -eq '"') {
            [void]$output.Append(('\' * (2 * $slashes + 1)))
            [void]$output.Append('"')
            $slashes = 0
            continue
        }
        if ($slashes -gt 0) { [void]$output.Append(('\' * $slashes)); $slashes = 0 }
        [void]$output.Append($character)
    }
    if ($slashes -gt 0) { [void]$output.Append(('\' * (2 * $slashes))) }
    [void]$output.Append('"')
    return $output.ToString()
}

function Invoke-ProcessCase([string]$name, [string]$file, [string[]]$arguments,
    [int]$timeoutSeconds, [string]$expectedReceipt) {
    $caseStart = [DateTimeOffset]::UtcNow
    $stdoutPath = Join-Path $phaseRoot ($name + '.stdout.txt')
    $stderrPath = Join-Path $phaseRoot ($name + '.stderr.txt')
    $failures = New-Object 'System.Collections.Generic.List[string]'
    $receiptChecks = @()
    $exitCode = $null
    $processId = $null
    $processExited = $false
    $timedOut = $false
    $receiptWaitDeadlineSeconds = 0
    $receiptWaitElapsedMilliseconds = 0
    $receiptLoaded = $false
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo.FileName = $file
    $process.StartInfo.Arguments = (($arguments | ForEach-Object { Quote-Argument $_ }) -join ' ')
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.CreateNoWindow = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    try {
        if (-not $process.Start()) { throw 'Process did not start.' }
        $processId = $process.Id
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($timeoutSeconds * 1000)) {
            # Only this exact process was created by this verification run.
            $timedOut = $true
            $failures.Add("Timed out after $timeoutSeconds seconds.")
            try {
                if (-not $process.HasExited) { $process.Kill() }
                $processExited = $process.WaitForExit(10000)
                if (-not $processExited) {
                    $failures.Add("Owned diagnostic process $processId remained active after the exact-process stop attempt.")
                }
            } catch {
                $failures.Add("Could not stop owned diagnostic process $processId after timeout: $($_.Exception.Message)")
            }
        } else {
            $exitCode = $process.ExitCode
            $processExited = $true
        }
        if ($stdoutTask.Wait(5000)) { [IO.File]::WriteAllText($stdoutPath, $stdoutTask.GetAwaiter().GetResult()) }
        else {
            $failures.Add('Owned diagnostic stdout did not close within five seconds.')
            [IO.File]::WriteAllText($stdoutPath, '')
        }
        if ($stderrTask.Wait(5000)) { [IO.File]::WriteAllText($stderrPath, $stderrTask.GetAwaiter().GetResult()) }
        else {
            $failures.Add('Owned diagnostic stderr did not close within five seconds.')
            [IO.File]::WriteAllText($stderrPath, '')
        }
    } catch {
        $failures.Add($_.Exception.Message)
    } finally { $process.Dispose() }
    if ($null -ne $exitCode -and $exitCode -ne 0) { $failures.Add("Exit code $exitCode.") }
    if ($expectedReceipt) {
        $receipt = $null
        $receiptReadError = $null
        if (-not $processId) {
            $failures.Add('Result receipt wait skipped because the diagnostic process did not start.')
        } elseif (-not $processExited) {
            $failures.Add("Result receipt wait skipped because owned diagnostic process $processId was not confirmed exited.")
        } else {
            $receiptWaitDeadlineSeconds = if ($timedOut) { 5 } else { 30 }
            $receiptWaitStarted = [DateTimeOffset]::UtcNow
            $receiptDeadline = $receiptWaitStarted.AddSeconds($receiptWaitDeadlineSeconds)
            $receiptLoaded = $false
            do {
                if ([IO.File]::Exists($expectedReceipt)) {
                    try {
                        $receipt = Get-Content -LiteralPath $expectedReceipt -Raw | ConvertFrom-Json
                        $receiptLoaded = $true
                        break
                    } catch { $receiptReadError = $_.Exception.Message }
                }
                if ([DateTimeOffset]::UtcNow -ge $receiptDeadline) { break }
                Start-Sleep -Milliseconds 200
            } while ($true)
            $receiptWaitElapsedMilliseconds = [int][Math]::Round(([DateTimeOffset]::UtcNow - $receiptWaitStarted).TotalMilliseconds)
            if (-not $receiptLoaded) {
                if ([IO.File]::Exists($expectedReceipt)) {
                    $failures.Add("Result receipt remained invalid JSON for $receiptWaitDeadlineSeconds seconds after process exit. $receiptReadError")
                } else {
                    $failures.Add("Result receipt did not appear within $receiptWaitDeadlineSeconds seconds after process exit.")
                }
            } else {
                try {
                    if ($receipt.passed -ne $true) { $failures.Add('Result receipt does not report passed=true.') }
                    if ($null -ne $receipt.executable -and -not [string]::Equals([string]$receipt.executable, $ExePath, [StringComparison]::OrdinalIgnoreCase)) {
                        $failures.Add('Result receipt executable path does not match the verified executable.')
                    }
                    if ($null -ne $receipt.checks) { $receiptChecks = @($receipt.checks) }
                    elseif ($null -ne $receipt.tests) { $receiptChecks = @($receipt.tests) }
                } catch { $failures.Add('Result receipt could not be inspected.') }
            }
        }
    }
    $result = [ordered]@{
        schemaVersion = 1; runId = $runId; testName = $name
        startedUtc = $caseStart.ToString('o'); finishedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        executablePath = $ExePath; executableSha256 = $hashBefore; profilePath = $profile
        processId = $processId; processExited = $processExited; timedOut = $timedOut
        receiptWaitDeadlineSeconds = $receiptWaitDeadlineSeconds
        receiptWaitElapsedMilliseconds = $receiptWaitElapsedMilliseconds; receiptLoaded = $receiptLoaded
        passed = ($failures.Count -eq 0); checks = $receiptChecks; failures = @($failures)
        externalDependencies = @(); artifacts = @($stdoutPath, $stderrPath, $expectedReceipt) | Where-Object { $_ }
        exitCode = $exitCode
    }
    $resultPath = Join-Path $phaseRoot ($name + '.result.json')
    $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $resultPath -Encoding UTF8
    $results.Add($result)
}

Invoke-ProcessCase 'self-test' $ExePath @('--self-test', (Join-Path $phaseRoot 'self-test.json')) 180 (Join-Path $phaseRoot 'self-test.json')
Invoke-ProcessCase 'storage-sync-proof' $ExePath @('--storage-sync-proof', (Join-Path $phaseRoot 'storage-sync-proof.json')) 120 (Join-Path $phaseRoot 'storage-sync-proof.json')
$installFixtures = Join-Path $phaseRoot 'install-fixtures'
[IO.Directory]::CreateDirectory($installFixtures) | Out-Null
Invoke-ProcessCase 'install-job-proof' $ExePath @('--install-job-proof', $installFixtures, (Join-Path $phaseRoot 'install-job-proof.json')) 120 (Join-Path $phaseRoot 'install-job-proof.json')

if (-not $NonUiOnly) {
    Invoke-ProcessCase 'ui-test' $ExePath @('--data-dir', $profile, '--offline', '--ui-test', (Join-Path $phaseRoot 'ui-test.json')) 240 (Join-Path $phaseRoot 'ui-test.json')
    Invoke-ProcessCase 'native-pause-proof' $ExePath @('--native-pause-proof', (Join-Path $phaseRoot 'native-pause-proof.json')) 120 (Join-Path $phaseRoot 'native-pause-proof.json')
}

$windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $reassFixture = 'F:\study\Platforms\windows\functions\Test-ReassRestore.ps1'
    if ([IO.File]::Exists($windowsPowerShell) -and [IO.File]::Exists($reassFixture)) {
        Invoke-ProcessCase 'reass-fixture' $windowsPowerShell @('-NoProfile', '-File', $reassFixture) 120 ''
    } else { $results.Add([ordered]@{ testName = 'reass-fixture'; passed = $false; status = 'Blocked'; blocker = 'Windows PowerShell 5.1 or Reass fixture is missing.' }) }

    $python = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'Python\pythoncore-3.14-64\python.exe'
    $progressTests = 'F:\study\projects\games\tools\gameprogress\tests'
    if ([IO.File]::Exists($python) -and [IO.Directory]::Exists($progressTests)) {
        Invoke-ProcessCase 'gameprogress-python' $python @('-m', 'pytest', '-q', $progressTests) 90 ''
    } else { $results.Add([ordered]@{ testName = 'gameprogress-python'; passed = $false; status = 'Blocked'; blocker = 'Python runtime or progress tests missing.' }) }

    $node = Join-Path $packageRoot 'tools\node\node.exe'
    $wandFixtureRoot = Join-Path $phaseRoot 'packaged-wand-fixtures'
    $wandFixtureTools = Join-Path $wandFixtureRoot 'tools'
    $wandFixtureTests = Join-Path $wandFixtureRoot 'tests'
    $wandFixtureData = Join-Path $wandFixtureTests 'fixtures'
    [IO.Directory]::CreateDirectory($wandFixtureTools) | Out-Null
    [IO.Directory]::CreateDirectory($wandFixtureTests) | Out-Null
    [IO.Directory]::CreateDirectory($wandFixtureData) | Out-Null
    $wandHelperNames = @('wand_live_library.js', 'wand_trainer_status.js', 'wand_cdp_launch.js', 'wand_tophat_evidence.js')
    $wandTestNames = @('wand-live-library.test.cjs', 'wand-trainer-status.test.cjs', 'wand-cdp-target.test.cjs', 'wand-tophat-evidence.test.cjs')
    $missingWandFixtures = New-Object 'System.Collections.Generic.List[string]'
    foreach ($helperName in $wandHelperNames) {
        $source = Join-Path (Join-Path $packageRoot 'tools') $helperName
        if (-not [IO.File]::Exists($source)) { $missingWandFixtures.Add($source); continue }
        [IO.File]::Copy($source, (Join-Path $wandFixtureTools $helperName))
    }
    foreach ($testName in $wandTestNames) {
        $source = Join-Path (Join-Path $PSScriptRoot 'tests') $testName
        if (-not [IO.File]::Exists($source)) { $missingWandFixtures.Add($source); continue }
        [IO.File]::Copy($source, (Join-Path $wandFixtureTests $testName))
    }
    $goldenTrace = Join-Path (Join-Path (Join-Path $PSScriptRoot 'tests') 'fixtures') 'wand-tophat-deltarune-success.traces.otlp'
    if ([IO.File]::Exists($goldenTrace)) {
        [IO.File]::Copy($goldenTrace, (Join-Path $wandFixtureData 'wand-tophat-deltarune-success.traces.otlp'))
    } else { $missingWandFixtures.Add($goldenTrace) }
    if (-not [IO.File]::Exists($node)) { $missingWandFixtures.Add($node) }
    if ($missingWandFixtures.Count -eq 0) {
        Invoke-ProcessCase 'wand-js' $node @('--test', (Join-Path $wandFixtureTests 'wand-live-library.test.cjs')) 90 ''
        Invoke-ProcessCase 'wand-trainer-js' $node @('--test', (Join-Path $wandFixtureTests 'wand-trainer-status.test.cjs')) 90 ''
        Invoke-ProcessCase 'wand-cdp-js' $node @('--test', (Join-Path $wandFixtureTests 'wand-cdp-target.test.cjs')) 90 ''
        Invoke-ProcessCase 'wand-tophat-js' $node @('--test', (Join-Path $wandFixtureTests 'wand-tophat-evidence.test.cjs')) 90 ''
    } else {
        $results.Add([ordered]@{ testName = 'packaged-wand-fixtures'; passed = $false; status = 'Blocked'; blocker = 'Missing packaged Wand fixtures: ' + ($missingWandFixtures -join ', ') })
    }
if ($IncludeLiveGames -and -not $NonUiOnly) {
    Invoke-ProcessCase 'ahk-pipe-proof' $ExePath @('--ahk-pipe-proof', (Join-Path $phaseRoot 'ahk-pipe-proof.json')) 20 (Join-Path $phaseRoot 'ahk-pipe-proof.json')
}

$liveStatus = if ($NonUiOnly) { 'Not tested: NonUiOnly was selected.' }
    elseif ($IncludeLiveGames) { 'Not tested: live scenarios require individual evidence and safe game selection.' }
    else { 'Not tested: IncludeLiveGames was not selected.' }
$dockerStatus = if ($IncludeRealDocker) { 'Not tested: real Docker scenarios require an isolated destination and job-level evidence.' }
    else { 'Not tested: IncludeRealDocker was not selected.' }
$hashAfter = (Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash
if ($hashAfter -ne $hashBefore) { $results.Add([ordered]@{ testName = 'binary-unchanged'; passed = $false; status = 'Failed'; blocker = 'Executable hash changed during verification.' }) }
$passed = @($results | Where-Object { $_.passed -ne $true }).Count -eq 0
$summary = [ordered]@{
    schemaVersion = 1; runId = $runId; phase = $Phase; startedUtc = $started.ToString('o')
    finishedUtc = [DateTimeOffset]::UtcNow.ToString('o'); executablePath = $ExePath
    executableSha256 = $hashBefore; executableSha256After = $hashAfter
    evidencePath = $phaseRoot; scope = if ($NonUiOnly) { 'non-ui-core' } else { 'full-reliability' }
    profilePath = $profile; passed = $passed; tests = $results.ToArray()
    liveGames = $liveStatus; realDocker = $dockerStatus
}
$summaryPath = Join-Path $phaseRoot 'verification-summary.json'
$summary | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
$failedTests = @($results | Where-Object { $_.passed -ne $true } | ForEach-Object { $_.testName })
$consoleSummary = [ordered]@{
    schemaVersion = 1; runId = $runId; phase = $Phase; scope = $summary.scope
    passed = $passed; testCount = $results.Count; failedTests = $failedTests
    executablePath = $ExePath; executableSha256 = $hashBefore
    evidencePath = $phaseRoot; summaryPath = $summaryPath
}
Write-Output ($consoleSummary | ConvertTo-Json -Depth 5 -Compress)
if (-not $passed) { exit 1 }
