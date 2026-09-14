param([switch]$DesktopShortcut, [string]$InstallRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'installed'), [switch]$NoShortcuts)
$ErrorActionPreference = 'Stop'
$sourceExe = Join-Path $PSScriptRoot 'dist\GameLibrary.exe'
if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) { throw 'Build the EXE with build.ps1 first.' }
$targetRoot = [IO.Path]::GetFullPath($InstallRoot)
if ([IO.Path]::GetPathRoot($targetRoot).TrimEnd('\') -eq $targetRoot.TrimEnd('\')) { throw 'The installation directory cannot be a drive root.' }
[void][IO.Directory]::CreateDirectory($targetRoot)
$targetExe = Join-Path $targetRoot 'GameLibrary.exe'
$sourceIcon = Join-Path $PSScriptRoot 'Assets\GameLibrary.ico'
if (-not (Test-Path -LiteralPath $sourceIcon -PathType Leaf)) { throw 'The package is missing the multi-resolution GameLibrary.ico.' }
$targetIcon = Join-Path $targetRoot 'GameLibrary.ico'
$sourceTools = Join-Path $PSScriptRoot 'dist\tools'
if (-not (Test-Path -LiteralPath $sourceTools -PathType Container)) { throw 'The build output is missing the bundled Wand bridge runtime.' }
if (Test-Path -LiteralPath $targetExe) {
    if (-not (Test-Path -LiteralPath (Join-Path $targetRoot 'install-receipt.json'))) { throw 'An unmanaged GameLibrary.exe already exists here; choose another folder.' }
    Copy-Item -LiteralPath $targetExe -Destination ($targetExe + '.backup-' + (Get-Date -Format yyyyMMdd-HHmmssfff))
}
Copy-Item -LiteralPath $sourceExe -Destination $targetExe -Force
$expected = (Get-FileHash -LiteralPath $sourceExe -Algorithm SHA256).Hash
if ((Get-FileHash -LiteralPath $targetExe -Algorithm SHA256).Hash -ne $expected) { throw 'Installed EXE checksum does not match the package.' }
Copy-Item -LiteralPath $sourceIcon -Destination $targetIcon -Force
$expectedIcon = (Get-FileHash -LiteralPath $sourceIcon -Algorithm SHA256).Hash
if ((Get-FileHash -LiteralPath $targetIcon -Algorithm SHA256).Hash -ne $expectedIcon) { throw 'Installed icon checksum does not match the package.' }
$nativeRuntimeFiles = @(
    'D3DCompiler_47_cor3.dll',
    'PenImc_cor3.dll',
    'PresentationNative_cor3.dll',
    'vcruntime140_cor3.dll',
    'wpfgfx_cor3.dll'
)
foreach ($runtimeName in $nativeRuntimeFiles) {
    $sourceRuntime = Join-Path (Split-Path $sourceExe -Parent) $runtimeName
    if (-not (Test-Path -LiteralPath $sourceRuntime -PathType Leaf)) { throw ('The build output is missing a required native WPF runtime: ' + $runtimeName) }
    $targetRuntime = Join-Path $targetRoot $runtimeName
    Copy-Item -LiteralPath $sourceRuntime -Destination $targetRuntime -Force
    if ((Get-FileHash -LiteralPath $targetRuntime -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $sourceRuntime -Algorithm SHA256).Hash) {
        throw ('Installed native WPF runtime checksum does not match: ' + $runtimeName)
    }
}
$targetTools = Join-Path $targetRoot 'tools'
foreach ($file in @(Get-ChildItem -LiteralPath $sourceTools -File -Recurse)) {
    $relative = $file.FullName.Substring($sourceTools.Length + 1)
    $destination = Join-Path $targetTools $relative
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
    Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
}
$installedBridge = Join-Path $targetTools 'wemod_add_custom_install.js'
$installedCdpLauncher = Join-Path $targetTools 'wand_cdp_launch.js'
$installedManifest = Join-Path $targetTools 'wand-supported-games.json'
$installedAddon = Join-Path $targetTools 'wand-runtime\classic-level\prebuilds\win32-x64\classic-level.node'
$installedNode = Join-Path $targetTools 'node\node.exe'
if (-not (Test-Path -LiteralPath $installedBridge -PathType Leaf) -or -not (Test-Path -LiteralPath $installedCdpLauncher -PathType Leaf) -or -not (Test-Path -LiteralPath $installedManifest -PathType Leaf) -or -not (Test-Path -LiteralPath $installedAddon -PathType Leaf) -or -not (Test-Path -LiteralPath $installedNode -PathType Leaf)) { throw 'Installed Wand bridge runtime or registration manifest is incomplete.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'uninstall.ps1') -Destination (Join-Path $targetRoot 'uninstall.ps1') -Force
$links = @()
$shell = New-Object -ComObject WScript.Shell
if (-not $NoShortcuts) {
    $links += Join-Path ([Environment]::GetFolderPath('Programs')) 'Game Library.lnk'
    if ($DesktopShortcut) { $links += Join-Path ([Environment]::GetFolderPath('Desktop')) 'Game Library.lnk' }
    foreach ($linkPath in $links) {
        if (Test-Path -LiteralPath $linkPath) {
            $old = $shell.CreateShortcut($linkPath)
            if ($old.TargetPath -ne $targetExe) { throw ('An unrelated shortcut already exists: ' + $linkPath) }
        }
        $shortcut = $shell.CreateShortcut($linkPath)
        $shortcut.TargetPath = $targetExe; $shortcut.WorkingDirectory = $targetRoot; $shortcut.IconLocation = $targetIcon + ',0'; $shortcut.Description = 'Native Windows Game Library'; $shortcut.Save()
    }
}
$repair = Start-Process -FilePath $targetExe -ArgumentList @('--repair-shortcuts') -WorkingDirectory $targetRoot -WindowStyle Hidden -Wait -PassThru
if ($repair.ExitCode -ne 0) { throw ('Installed shell links could not be repaired; exit code ' + $repair.ExitCode) }
$verifiedLinks = @($links)
$taskbarRoot = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'
foreach ($name in @('Game Library.lnk', 'GameLibrary.lnk')) {
    $taskbarLink = Join-Path $taskbarRoot $name
    if (Test-Path -LiteralPath $taskbarLink -PathType Leaf) {
        $taskbarShortcut = $shell.CreateShortcut($taskbarLink)
        if ($taskbarShortcut.TargetPath -eq $targetExe) { $verifiedLinks += $taskbarLink }
    }
}
$shellApplication = New-Object -ComObject Shell.Application
foreach ($linkPath in $verifiedLinks) {
    $shortcut = $shell.CreateShortcut($linkPath)
    if ($shortcut.TargetPath -ne $targetExe -or $shortcut.IconLocation -ne ($targetIcon + ',0')) { throw ('Shortcut target or icon verification failed: ' + $linkPath) }
    $folder = $shellApplication.Namespace([IO.Path]::GetDirectoryName($linkPath))
    $item = if ($folder) { $folder.ParseName([IO.Path]::GetFileName($linkPath)) } else { $null }
    if (-not $item -or [string]$item.ExtendedProperty('System.AppUserModel.ID') -ne 'GameLibraryManager.Native') { throw ('Shortcut AppUserModelID verification failed: ' + $linkPath) }
}
[pscustomobject]@{ app='GameLibraryManager.Native'; at=[DateTime]::UtcNow.ToString('o'); root=$targetRoot; executable=$targetExe; sha256=$expected; icon=$targetIcon; iconSha256=$expectedIcon; appUserModelId='GameLibraryManager.Native'; shortcutRepairExitCode=$repair.ExitCode; bundledWandBridge=$true; bundledNativeWpfRuntime=$true; shortcuts=$links } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $targetRoot 'install-receipt.json') -Encoding UTF8
Write-Output ('Installed and checksum-verified: ' + $targetExe)
