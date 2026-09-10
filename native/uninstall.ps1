param([string]$InstallRoot = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
$targetRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$receiptPath = Join-Path $targetRoot 'install-receipt.json'
if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) { throw 'No managed installation receipt exists. Nothing was removed.' }
$receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
if ($receipt.app -ne 'GameLibraryManager.Native' -or [IO.Path]::GetFullPath($receipt.root).TrimEnd('\') -ne $targetRoot) { throw 'Installation identity does not match. Nothing was removed.' }
$targetExe = Join-Path $targetRoot 'GameLibrary.exe'
if (Test-Path -LiteralPath $targetExe) {
    if ((Get-FileHash -LiteralPath $targetExe -Algorithm SHA256).Hash -ne $receipt.sha256) { throw 'The installed executable changed outside this installer. Nothing was removed.' }
    Remove-Item -LiteralPath $targetExe
}
$shell = New-Object -ComObject WScript.Shell
foreach ($linkPath in $receipt.shortcuts) {
    if (Test-Path -LiteralPath $linkPath -PathType Leaf) {
        $shortcut = $shell.CreateShortcut($linkPath)
        if ($shortcut.TargetPath -eq $targetExe) { Remove-Item -LiteralPath $linkPath }
    }
}
Remove-Item -LiteralPath $receiptPath
$uninstaller = Join-Path $targetRoot 'uninstall.ps1'
if (Test-Path -LiteralPath $uninstaller) { Remove-Item -LiteralPath $uninstaller }
# No recursive deletion. Backups and any other files remain. Saved libraries and games are never touched.
if (@(Get-ChildItem -LiteralPath $targetRoot -Force).Count -eq 0) { Remove-Item -LiteralPath $targetRoot }
Write-Output 'Game Library removed. Saved preferences, catalog caches, downloaded games, backups, and unrelated files were preserved.'
