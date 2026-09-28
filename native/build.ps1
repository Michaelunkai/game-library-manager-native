param(
    [switch]$FrameworkDependent,
    [switch]$SkipAssets,
    [string]$DotnetPath,
    [string]$DistributionRoot,
    [string]$BuildOutputRoot
)
$ErrorActionPreference = 'Stop'
$nativeRoot = $PSScriptRoot
$sourceRoot = Split-Path $nativeRoot -Parent
$buildDrive = [IO.Path]::GetPathRoot($nativeRoot)
$buildRuntime = Join-Path $buildDrive 'study\temp\glm-native-build'
if ([string]::IsNullOrWhiteSpace($BuildOutputRoot)) { $BuildOutputRoot = Join-Path $buildRuntime 'output' }
$BuildOutputRoot = [IO.Path]::GetFullPath($BuildOutputRoot)
if (-not $BuildOutputRoot.EndsWith('\')) { $BuildOutputRoot += '\' }
$buildEnvironment = @{
    TEMP = Join-Path $buildRuntime 'temp'
    TMP = Join-Path $buildRuntime 'tmp'
    DOTNET_CLI_HOME = Join-Path $buildRuntime 'dotnet-home'
    NUGET_PACKAGES = Join-Path $buildRuntime 'nuget'
    NUGET_HTTP_CACHE_PATH = Join-Path $buildRuntime 'nuget-http'
}
$previousEnvironment = @{}
foreach ($entry in $buildEnvironment.GetEnumerator())
{
    $previousEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key)
    [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value)
    [void][IO.Directory]::CreateDirectory($entry.Value)
}
$previousEnvironment['DOTNET_SKIP_FIRST_TIME_EXPERIENCE'] = [Environment]::GetEnvironmentVariable('DOTNET_SKIP_FIRST_TIME_EXPERIENCE')
$previousEnvironment['DOTNET_CLI_TELEMETRY_OPTOUT'] = [Environment]::GetEnvironmentVariable('DOTNET_CLI_TELEMETRY_OPTOUT')
$previousEnvironment['DOTNET_NOLOGO'] = [Environment]::GetEnvironmentVariable('DOTNET_NOLOGO')
[Environment]::SetEnvironmentVariable('DOTNET_SKIP_FIRST_TIME_EXPERIENCE', '1')
[Environment]::SetEnvironmentVariable('DOTNET_CLI_TELEMETRY_OPTOUT', '1')
[Environment]::SetEnvironmentVariable('DOTNET_NOLOGO', '1')
if (-not $DotnetPath) {
    $isolatedSdk = Join-Path $env:USERPROFILE '.codex\toolchains\gamelibrary-dotnet10\dotnet.exe'
    $DotnetPath = 'C:\Program Files\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $isolatedSdk) { $DotnetPath = $isolatedSdk }
}
Push-Location -LiteralPath $nativeRoot
try {
    if (-not (Test-Path -LiteralPath $dotnetPath)) { throw '.NET 10 SDK is required to build. Install it from Microsoft.' }
    if (-not $SkipAssets) {
        Add-Type -AssemblyName System.IO.Compression
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [void][IO.Directory]::CreateDirectory((Join-Path $nativeRoot 'Assets'))
        $assetZip = Join-Path $nativeRoot 'Assets\catalog.zip'
        if (Test-Path -LiteralPath $assetZip) { Remove-Item -LiteralPath $assetZip }
        $archive = [IO.Compression.ZipFile]::Open($assetZip, [IO.Compression.ZipArchiveMode]::Create)
        try {
            $publicRoot = Join-Path $sourceRoot 'public'
            $files = @(Get-ChildItem -LiteralPath (Join-Path $publicRoot 'data'),(Join-Path $publicRoot 'images') -File -Recurse)
            foreach ($file in $files) {
                $entry = $file.FullName.Substring($publicRoot.Length + 1).Replace('\','/')
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, [IO.Compression.CompressionLevel]::Fastest)
            }
            Write-Output ('Bundled {0} catalog and cover files' -f $files.Count)
        } finally { $archive.Dispose() }
    }
    (Get-FileHash -LiteralPath (Join-Path $nativeRoot 'Assets\catalog.zip') -Algorithm SHA256).Hash.ToLowerInvariant() | Set-Content -LiteralPath (Join-Path $nativeRoot 'Assets\catalog.sha256') -Encoding ASCII
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $verifyArchive = [IO.Compression.ZipFile]::OpenRead((Join-Path $nativeRoot 'Assets\catalog.zip'))
    try {
        $verifyCount = @($verifyArchive.Entries).Count
        if ($verifyCount -lt 1000) { throw ('The packaged catalog is incomplete: ' + $verifyCount + ' entries.') }
        Write-Output ('Verified packaged catalog: ' + $verifyCount + ' entries')
    } finally { $verifyArchive.Dispose() }
    $selfContained = 'true'
    if ($FrameworkDependent) { $selfContained = 'false' }
    $usingDefaultDistributionRoot = [string]::IsNullOrWhiteSpace($DistributionRoot)
    if ($usingDefaultDistributionRoot) { $distributionRoot = Join-Path $nativeRoot 'dist' }
    else {
        $distributionRoot = [IO.Path]::GetFullPath($DistributionRoot)
        if (Test-Path -LiteralPath $distributionRoot) { throw 'The requested distribution output already exists. Choose a new empty path so an existing package cannot be overwritten.' }
    }
    if ($usingDefaultDistributionRoot -and (Test-Path -LiteralPath $distributionRoot)) { Remove-Item -LiteralPath $distributionRoot -Recurse -Force }
    [void][IO.Directory]::CreateDirectory($distributionRoot)
    & $dotnetPath publish GameLibrary.Native.csproj -c Release -r win-x64 --self-contained $selfContained -o $distributionRoot "-p:BaseOutputPath=$BuildOutputRoot"
    if ($LASTEXITCODE -ne 0) { throw ('Publish failed: exit ' + $LASTEXITCODE) }
    # The single-file publisher carries the managed payload into the EXE but may
    # omit native Node addons from the publish directory. Copy the exact bundled
    # Wand bridge runtime from the RID-specific build output so the installed EXE
    # remains self-contained and never falls back to a developer profile path.
    $builtTools = Join-Path $BuildOutputRoot 'Release\net10.0-windows\win-x64\tools'
    $publishedTools = Join-Path $distributionRoot 'tools'
    if (-not (Test-Path -LiteralPath $builtTools -PathType Container)) { throw 'The bundled Wand tools were not produced; refusing a stale or incomplete package.' }
    foreach ($file in @(Get-ChildItem -LiteralPath $builtTools -File -Recurse)) {
        $relative = $file.FullName.Substring($builtTools.Length + 1)
        $destination = Join-Path $publishedTools $relative
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    }
    $requiredTools = @(
        (Join-Path $publishedTools 'wemod_add_custom_install.js'),
        (Join-Path $publishedTools 'wand_cdp_launch.js'),
        (Join-Path $publishedTools 'wand_trainer_status.js'),
        (Join-Path $publishedTools 'wand_tophat_evidence.js'),
        (Join-Path $publishedTools 'wand-supported-games.json'),
        (Join-Path $publishedTools 'wand-runtime\classic-level\prebuilds\win32-x64\classic-level.node'),
        (Join-Path $publishedTools 'node\node.exe')
    )
    foreach ($required in $requiredTools) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw ('Required bundled runtime file is missing: ' + $required) }
    }
    $exe = Join-Path $distributionRoot 'GameLibrary.exe'
    Get-Item -LiteralPath $exe | Select-Object FullName,Length,LastWriteTime
    Write-Output ('SHA256: ' + (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash)
} finally
{
    Pop-Location
    foreach ($entry in $previousEnvironment.GetEnumerator())
    {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value)
    }
}
