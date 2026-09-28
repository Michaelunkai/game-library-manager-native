param([Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference='Stop'
$output=[IO.Path]::GetFullPath($OutputPath)
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if(-not (Test-Path -LiteralPath $compiler)){throw 'The Windows .NET Framework compiler is unavailable.'}
$icon=Join-Path (Split-Path $PSScriptRoot -Parent) 'native\Assets\GameLibrary.ico'
& $compiler /nologo /target:winexe /optimize+ /reference:System.Windows.Forms.dll "/win32icon:$icon" "/out:$output" (Join-Path $PSScriptRoot 'LegacyLauncher.cs')
if($LASTEXITCODE -ne 0){throw 'Compatibility launcher build failed.'}
Get-FileHash -LiteralPath $output -Algorithm SHA256
