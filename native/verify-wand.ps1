param(
    [string]$ExePath = (Join-Path $PSScriptRoot 'dist\GameLibrary.exe'),
    [string]$AuditPath = '',
    [string]$ReportPath = (Join-Path $PSScriptRoot 'evidence\wand-native-current.json'),
    [string]$GameSearch = '',
    [string]$GameTitle = '',
    [string]$InstalledGame = '',
    [string]$DataRoot = ''
)

# This legacy entrypoint used to start the UI and a real game, stop every Wand
# and same-named game process, then report overlay IPC/hook markers as trainer
# connection. Those markers cannot prove that the trainer attached. Keep the
# entrypoint compatible but fail closed until a game-bound trainer receipt is
# available from a separately authorized live test.
$ErrorActionPreference = 'Stop'
$exe = [IO.Path]::GetFullPath($ExePath)
$report = [IO.Path]::GetFullPath($ReportPath)
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Native executable is missing: $exe" }
$audit = $null
if (-not [string]::IsNullOrWhiteSpace($AuditPath)) {
    $auditFull = [IO.Path]::GetFullPath($AuditPath)
    if (-not (Test-Path -LiteralPath $auditFull -PathType Leaf)) { throw "Wand audit is missing: $auditFull" }
    $audit = Get-Content -LiteralPath $auditFull -Raw | ConvertFrom-Json
}
$receipt = [ordered]@{
    schemaVersion = 2
    atUtc = [DateTime]::UtcNow.ToString('o')
    executable = $exe
    sha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    registrationAuditPath = $AuditPath
    registrationAuditPassed = ($null -ne $audit -and $audit.passed -eq $true)
    registrationCount = if ($null -ne $audit) { $audit.supportedRegistrationCount } else { 0 }
    trainerAttachmentVerified = $false
    status = 'Not tested'
    blocker = 'An overlay IPC/hook log is not trainer-session evidence for the exact game process. No live game was launched by this read-only entrypoint.'
    gameSearch = $GameSearch
    gameTitle = $GameTitle
    installedGame = $InstalledGame
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($report)) | Out-Null
$receipt | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $report -Encoding UTF8
Write-Output "Wand trainer attachment remains Not tested; registration audit passed=$($receipt.registrationAuditPassed); report=$report"
exit 2
