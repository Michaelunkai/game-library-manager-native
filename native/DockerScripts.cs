using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameLibrary.Native;

public static class DockerScripts
{
    internal const string CompletionMarkerName = ".gamelibrarymanager-install-complete";
    internal const string OperationLabel = "com.gamelibrary.operation-id";
    internal const string StagingDirectoryName = ".gamelibrarymanager-staging";
    // JSON avoids Windows PowerShell 5 native-argument stripping of the inner
    // quotes required by a Go-template `index` expression. Bash keeps the
    // dedicated template below because its quoting is stable there.
    internal const string OwnershipFormat = "{{json .Config.Labels}}";
    private const string ShellOwnershipFormat = "{{ index .Config.Labels \"com.gamelibrary.owner\" }}|{{ index .Config.Labels \"com.gamelibrary.game-id\" }}";
    public static string Executable => File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "resources", "bin", "docker.exe"))
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "resources", "bin", "docker.exe") : "docker.exe";
    public static bool ValidTag(string tag) => Regex.IsMatch(tag, @"\A[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}\z");
    public static string PsQuote(string text) => "'" + text.Replace("'", "''") + "'";
    public static string ShQuote(string text) => "'" + text.Replace("'", "'\"'\"'") + "'";
    public static string ContainerName(string id) => "glm-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant()[..16];
    // Docker container names are global to the active daemon, while a library
    // destination is only local to one install script. Keep the legacy
    // game-only name for existing folder identities, but namespace live
    // containers by their canonical destination so two same-game installs to
    // different folders cannot collide on the daemon.
    internal static string ContainerNameForDestination(string id, string destination)
    {
        string canonicalDestination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination.Trim())).ToUpperInvariant();
        string identity = id + "\0" + canonicalDestination;
        return "glm-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..24];
    }
    public static string InstallFolder(string id) => id + "-" + ContainerName(id)[4..12];
    internal static string InstallLockPath(string destination, string gameId) => Path.Combine(Path.GetFullPath(destination), ".gamelibrarymanager-locks", ContainerNameForDestination(gameId, destination) + ".lock");
    internal static string InstallWorkKey(string destination, string gameId)
    {
        string canonicalDestination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination.Trim())).ToUpperInvariant();
        return canonicalDestination + "\0" + gameId;
    }
    internal static string OwnershipMetadata(string gameId) => "native|" + gameId;
    internal static bool OwnershipMatches(string metadata, string gameId) => string.Equals(metadata.Trim(), OwnershipMetadata(gameId), StringComparison.Ordinal);
    internal static string OwnershipFromLabelsJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var labels = document.RootElement;
            string owner = labels.TryGetProperty("com.gamelibrary.owner", out var ownerValue) ? ownerValue.GetString() ?? string.Empty : string.Empty;
            string gameId = labels.TryGetProperty("com.gamelibrary.game-id", out var gameValue) ? gameValue.GetString() ?? string.Empty : string.Empty;
            return owner + "|" + gameId;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
    internal static bool OperationMatches(string json, string operationId)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var labels = document.RootElement;
            return labels.TryGetProperty(OperationLabel, out var operationValue)
                && string.Equals(operationValue.GetString(), operationId, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }
    public static void Validate(Preferences settings, IEnumerable<Game> games)
    {
        if (!Regex.IsMatch(settings.DockerUsername, @"\A[a-z0-9][a-z0-9_-]*\z") || !Regex.IsMatch(settings.RepoName, @"\A[a-z0-9][a-z0-9_.-]*\z")) throw new ArgumentException("Use a valid Docker Hub username and repository.");
        if (!Path.IsPathFullyQualified(settings.MountPath) || settings.MountPath.IndexOfAny(new[] { '\r', '\n', '\0', ',' }) >= 0) throw new ArgumentException("Choose an absolute Windows folder without commas or line breaks.");
        foreach (var game in games)
        {
            if (game.IsLocal || game.Id.StartsWith("local:", StringComparison.Ordinal)) throw new ArgumentException(game.Name + " was found on this PC and has no Docker image. Open Details to play it; select catalog games for Docker scripts.");
            if (!ValidTag(game.Id)) throw new ArgumentException("Invalid Docker tag: " + game.Id);
        }
    }
    internal static Game[] DistinctGames(IEnumerable<Game> selected)
    {
        if (selected == null) throw new ArgumentNullException(nameof(selected));
        var games = selected.ToArray();
        if (games.Any(game => game == null)) throw new ArgumentException("The selected game list contains an empty entry.", nameof(selected));
        return games.GroupBy(game => game.Id, StringComparer.Ordinal).Select(group => group.First()).ToArray();
    }
    public static string Generate(IEnumerable<Game> selected, Preferences settings, string format = "ps1", bool stop = false, string? shellTarget = null)
    {
        var games = DistinctGames(selected);
        if (games.Length == 0) throw new ArgumentException("Select at least one game.");
        Validate(settings, games);
        if (format == "sh") return Shell(games, settings, stop, shellTarget ?? settings.ShellTarget);
        var lines = new List<string> {
            "# Game Library Manager · only the selected game containers are affected.",
            "$ErrorActionPreference = 'Stop'", "$ProgressPreference = 'Continue'",
            "$dockerCommand = Get-Command docker.exe -ErrorAction SilentlyContinue; $dockerExecutable = if ($dockerCommand) { $dockerCommand.Source } else { Join-Path $env:ProgramFiles 'Docker\\Docker\\resources\\bin\\docker.exe' }",
            "if (-not (Test-Path -LiteralPath $dockerExecutable -PathType Leaf)) { throw 'Docker Desktop is not installed. Install it, start Docker Desktop, then retry.' }",
            "$env:PATH = (Split-Path -Parent $dockerExecutable) + [IO.Path]::PathSeparator + $env:PATH # Also resolves Docker Desktop's credential helper in this process only.",
            "& $dockerExecutable info --format '{{.OSType}}'", "if ($LASTEXITCODE -ne 0) { throw 'Start Docker Desktop, then retry. Your Docker backend settings have not been changed.' }",
            "$operationId = if ([string]::IsNullOrWhiteSpace($env:GLM_INSTALL_OPERATION_ID)) { [guid]::NewGuid().ToString('N') } else { $env:GLM_INSTALL_OPERATION_ID }; if ($operationId -notmatch '^[A-Za-z0-9-]{8,64}$') { throw 'The install operation identity is invalid.' }",
            "$destination = " + PsQuote(Path.GetFullPath(settings.MountPath)) + "; [void][IO.Directory]::CreateDirectory($destination); [void][IO.Directory]::CreateDirectory((Join-Path $destination '.gamelibrarymanager-locks'))",
            "$nativeInstallLockHeld = $env:GLM_NATIVE_INSTALL_LOCK_HELD -eq '1'",
            "$completedGames = New-Object 'System.Collections.Generic.List[string]'",
            "$failedGames = New-Object 'System.Collections.Generic.List[string]'"
        };
        lines.Add("$nativeUtilityRegex = '(?i)^(unins|uninstall|setup|install|launcher|gamebootstrapper|eaclauncher|redist|crashreport|crashhandler|crashpad|reporter|helper|quicksfv|dxsetup|vcredist|ue4prereq|ueprereq|dotnet|unitycrashhandler|qtwebengineprocess|yuzu|ryujinx|citron|sudachi|eden|yuzucmd|edencli|edenroom|enbhost|skse|squirrel|inklecate|ffmpeg|languageselector|workshop|unrealcefsubprocess|.*editor|.*toolkit|.*packager)'");
        lines.Add("$nativeSupportRegex = '(?i)(^|\\\\)(support|redist|redistributables|redistributable|commonredist|prerequisites|directx|vcredist|dotnet|installers|installer|crashreporter|crashreportclient|trainer|wemod|fling|flingtrainer|thirdpartylibs|imageioffmpeg|emulators|modding)(\\\\|$)'");
        lines.Add("function Test-NativePlayableExecutable { param([string]$folder); if (-not (Test-Path -LiteralPath $folder -PathType Container)) { return $false }; try { $root = (Get-Item -LiteralPath $folder -ErrorAction Stop).FullName.TrimEnd('\\') + '\\'; foreach ($file in @(Get-ChildItem -LiteralPath $folder -Filter '*.exe' -File -Recurse -ErrorAction SilentlyContinue)) { $relative = $file.FullName.Substring($root.Length); if ($relative -match $nativeSupportRegex -or $file.BaseName -match $nativeUtilityRegex) { continue }; return $true } } catch { return $false }; return $false }");
        lines.Add("function Test-NativeContainer { param([string]$name, [string]$expectedMetadata, [string]$expectedOperationId); try { $inspection = & $dockerExecutable container inspect $name --format " + PsQuote(OwnershipFormat) + " 2>$null } catch { return $false }; $inspectExitCode = $LASTEXITCODE; if ($inspectExitCode -ne 0) { return $false }; try { $labels = (([string]($inspection | Out-String)).Trim() | ConvertFrom-Json) } catch { throw ('Refusing destructive cleanup for unowned container ' + $name + '.') }; $actualMetadata = [string]$labels.'com.gamelibrary.owner' + '|' + [string]$labels.'com.gamelibrary.game-id'; $actualOperationId = [string]$labels.'" + OperationLabel + "'; if ($actualMetadata -cne $expectedMetadata -or ($expectedOperationId -and $actualOperationId -cne $expectedOperationId)) { throw ('Refusing destructive cleanup for unowned container ' + $name + '.') }; return $true }");
        // File.Open has no six-argument overload in Windows PowerShell 5.1's
        // .NET Framework. Construct the FileStream directly so generated jobs
        // work in both Windows PowerShell and current PowerShell releases.
        lines.Add("function Enter-NativeInstallLock { param([string]$path); if ($nativeInstallLockHeld) { return $null }; $deadline = [DateTime]::UtcNow.AddHours(24); while ($true) { try { return [IO.FileStream]::new($path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None, 1, [IO.FileOptions]::WriteThrough) } catch [IO.IOException] { if ([DateTime]::UtcNow -ge $deadline) { throw ('Timed out waiting for the selected game install lock: ' + $path) }; Start-Sleep -Milliseconds 250 } } }");
        lines.Add("function Exit-NativeInstallLock { param([IO.FileStream]$lock); if ($null -ne $lock) { $lock.Dispose() } }");
        int index = 0;
        foreach (var game in games)
        {
            index++;
            string destinationPath = Path.GetFullPath(settings.MountPath);
            string name = ContainerNameForDestination(game.Id, destinationPath), image = settings.DockerUsername + "/" + settings.RepoName + ":" + game.Id;
            string ownership = OwnershipMetadata(game.Id);
            lines.Add("Write-Output ((Get-Date -Format o) + " + PsQuote(" GAME " + index + "/" + games.Length + " · " + (stop ? "Stopping " : "Downloading ") + game.Name) + ")");
            string lockPath = ".gamelibrarymanager-locks\\" + name + ".lock";
            lines.Add("$installMutex = $null; $stagingFolder = $null; try {");
            lines.Add("$installMutex = Enter-NativeInstallLock (Join-Path $destination " + PsQuote(lockPath) + ")");
            if (stop)
            {
                lines.Add("if (Test-NativeContainer " + PsQuote(name) + " " + PsQuote(ownership) + ") { & $dockerExecutable stop " + PsQuote(name) + "; if ($LASTEXITCODE -ne 0) { throw 'Could not stop selected container.' } }");
                lines.Add("[void]$completedGames.Add(" + PsQuote(game.Id) + ")");
                lines.Add("} catch { $failureText = if ($null -ne $_.Exception) { $_.Exception.GetType().Name + ': ' + $_.Exception.Message } else { [string]$_ }; [void]$failedGames.Add(" + PsQuote(game.Id) + "); Write-Output ((Get-Date -Format o) + " + PsQuote(" FAILED " + game.Id + ": ") + " + $failureText) } finally { Exit-NativeInstallLock $installMutex }");
                continue;
            }
            lines.Add("$pullSuccess = $false; for ($attempt = 1; $attempt -le 5 -and -not $pullSuccess; $attempt++) {");
            lines.Add("  & $dockerExecutable info --format '{{.OSType}}' *> $null; if ($LASTEXITCODE -ne 0) { Write-Warning ('Docker is not ready; retry ' + $attempt + '/5'); Start-Sleep -Seconds ([Math]::Min($attempt * 2, 10)); continue }");
            lines.Add("  & $dockerExecutable pull " + PsQuote(image) + "; if ($LASTEXITCODE -eq 0) { $pullSuccess = $true } else { Write-Warning ('Pull failed for " + game.Id + " on retry ' + $attempt + '/5'); if ($attempt -lt 5) { Start-Sleep -Seconds ([Math]::Min($attempt * 2, 10)) } }");
            lines.Add("}");
            lines.Add("if (-not $pullSuccess) { throw " + PsQuote("Docker pull failed for " + game.Id + " after five attempts. Existing files were preserved.") + " }");
            string folderName = InstallFolder(game.Id);
            string markerPrefix = "GameLibraryManager|" + game.Id + "|";
            lines.Add("$markerValue = " + PsQuote(markerPrefix) + " + $operationId; $installFolder = Join-Path $destination " + PsQuote(folderName) + "; $completionMarker = Join-Path $installFolder " + PsQuote(CompletionMarkerName) + "; $stagingRoot = Join-Path $destination " + PsQuote(StagingDirectoryName) + "; $stagingFolder = Join-Path $stagingRoot (" + PsQuote(folderName + "-") + " + $operationId); $stagingMarker = Join-Path $stagingFolder " + PsQuote(CompletionMarkerName) + "; $backupFolder = Join-Path $stagingRoot (" + PsQuote(folderName + "-previous-") + " + $operationId); New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null");
            // Never copy from Linux directly into a Windows bind mount. Even
            // non-archive BusyBox cp attempts metadata operations that can fail
            // on NTFS after the bytes were copied. A stopped container plus
            // `docker cp --follow-link` lets Docker translate the archive onto
            // the host, after which the native verifier owns marker creation.
            string containerSource = name + ":/home/.";
            string cleanup = "if (Test-NativeContainer " + PsQuote(name) + " " + PsQuote(ownership) + " $operationId) { & $dockerExecutable container rm --force " + PsQuote(name) + " *> $null; if ($LASTEXITCODE -ne 0) { throw 'Could not remove the native container.' } }";
            lines.Add("$runSuccess = $false; for ($attempt = 1; $attempt -le 3 -and -not $runSuccess; $attempt++) {");
            lines.Add("  Remove-Item -LiteralPath $stagingFolder -Recurse -Force -ErrorAction SilentlyContinue; if (Test-Path -LiteralPath $stagingFolder) { throw 'Could not clear the operation staging folder.' }; " + cleanup + "; New-Item -ItemType Directory -Force -Path $stagingFolder | Out-Null; & $dockerExecutable container create --name " + PsQuote(name) + " --env ('GLM_INSTALL_OPERATION_ID=' + $operationId) --label " + PsQuote("com.gamelibrary.owner=native") + " --label " + PsQuote("com.gamelibrary.game-id=" + game.Id) + " --label (" + PsQuote(OperationLabel + "=") + " + $operationId) " + PsQuote(image) + " *> $null; $createExitCode = $LASTEXITCODE; $copyExitCode = -1; if ($createExitCode -eq 0) { & $dockerExecutable cp --follow-link " + PsQuote(containerSource) + " $stagingFolder; $copyExitCode = $LASTEXITCODE }; " + cleanup + "; $hasPlayableExecutable = Test-NativePlayableExecutable $stagingFolder; if ($createExitCode -eq 0 -and $copyExitCode -eq 0 -and -not $hasPlayableExecutable) { Remove-Item -LiteralPath $stagingFolder -Recurse -Force -ErrorAction SilentlyContinue; throw " + PsQuote("Backup image for " + game.Name + " contains no playable Windows executable. It was not retried and the existing installation was left untouched. Rebuild or replace that backup image with the actual game payload.") + " }; if ($createExitCode -eq 0 -and $copyExitCode -eq 0 -and $hasPlayableExecutable) { [IO.File]::WriteAllText($stagingMarker, $markerValue + [Environment]::NewLine); $markerContent = Get-Content -LiteralPath $stagingMarker -Raw -ErrorAction SilentlyContinue; if (([string]$markerContent).Trim() -eq $markerValue) { $backupMoved = $false; $promoted = $false; try { if (Test-Path -LiteralPath $installFolder) { Move-Item -LiteralPath $installFolder -Destination $backupFolder -ErrorAction Stop; $backupMoved = $true }; Move-Item -LiteralPath $stagingFolder -Destination $installFolder -ErrorAction Stop; $promoted = $true; $installedPlayable = Test-NativePlayableExecutable $installFolder; $installedMarker = if (Test-Path -LiteralPath $completionMarker -PathType Leaf) { Get-Content -LiteralPath $completionMarker -Raw -ErrorAction SilentlyContinue } else { '' }; if (-not $installedPlayable -or ([string]$installedMarker).Trim() -ne $markerValue) { throw 'Staged payload did not remain valid after promotion.' }; if ($backupMoved) { Remove-Item -LiteralPath $backupFolder -Recurse -Force -ErrorAction Stop }; $runSuccess = $true } catch { if ($promoted -and (Test-Path -LiteralPath $installFolder) -and -not (Test-Path -LiteralPath $stagingFolder)) { try { Move-Item -LiteralPath $installFolder -Destination $stagingFolder -ErrorAction Stop } catch { } }; if ($backupMoved -and (Test-Path -LiteralPath $backupFolder) -and -not (Test-Path -LiteralPath $installFolder)) { try { Move-Item -LiteralPath $backupFolder -Destination $installFolder -ErrorAction Stop } catch { } }; throw } } }; if (-not $runSuccess) { Remove-Item -LiteralPath $stagingFolder -Recurse -Force -ErrorAction SilentlyContinue; Write-Warning ('Extraction failed for " + game.Id + " on attempt ' + $attempt + '/3; Docker create/cp or completion proof failed.'); if ($attempt -lt 3) { Start-Sleep -Seconds ([Math]::Min($attempt * 3, 15)) } }");
            lines.Add("}");
            lines.Add("if (-not $runSuccess) { throw " + PsQuote("Extraction failed for " + game.Id + " after three attempts. The previous installation was preserved; review the log and retry.") + " }");
            lines.Add("[void]$completedGames.Add(" + PsQuote(game.Id) + ")");
            lines.Add("Write-Output ((Get-Date -Format o) + " + PsQuote(" Completed " + game.Id) + ")");
            lines.Add("} catch { $failureText = if ($null -ne $_.Exception) { $_.Exception.GetType().Name + ': ' + $_.Exception.Message } else { [string]$_ }; [void]$failedGames.Add(" + PsQuote(game.Id) + "); Write-Output ((Get-Date -Format o) + " + PsQuote(" FAILED " + game.Id + ": ") + " + $failureText) } finally { if ($null -ne $stagingFolder -and (Test-Path -LiteralPath $stagingFolder)) { Remove-Item -LiteralPath $stagingFolder -Recurse -Force -ErrorAction SilentlyContinue }; Exit-NativeInstallLock $installMutex }");
        }
        lines.Add("if ($failedGames.Count -eq 0 -and $completedGames.Count -eq " + games.Length + ") { Write-Output 'All selected operations completed.'; Write-Output ('Verified ' + $completedGames.Count + '/' + " + games.Length + " + ' selected operation(s).'); exit 0 }");
        lines.Add("Write-Output ('Install batch completed with failures: ' + $completedGames.Count + '/' + " + games.Length + " + ' selected operation(s) completed; failed game(s): ' + ($failedGames -join ', ') + '. Existing files were preserved where possible.'); exit 1");
        var script = string.Join("\r\n", lines) + "\r\n";
        return format == "bat" ? Batch(script, pauseAtEnd: false) : script;
    }
    public static string GenerateKillAll(string format)
    {
        if (format != "ps1" && format != "bat") throw new ArgumentException("Choose PowerShell or BAT for Windows Kill All scripts.");
        // Export only: callers save this text. All Docker access and confirmation happen
        // later, if the user explicitly runs the saved script in a terminal.
        var lines = new[] {
            "# Game Library Manager - Kill All containers in the active Docker target.",
            "# This includes unrelated containers and deletes their writable layers.",
            "# Images, volumes and downloaded game folders are preserved.",
            "$ErrorActionPreference = 'Stop'",
            "try {",
            "    $dockerCommand = Get-Command docker.exe -ErrorAction SilentlyContinue",
            "    $dockerExecutable = if ($dockerCommand) { $dockerCommand.Source } else { Join-Path $env:ProgramFiles 'Docker\\Docker\\resources\\bin\\docker.exe' }",
            "    if (-not (Test-Path -LiteralPath $dockerExecutable -PathType Leaf)) { throw 'Docker Desktop is not installed.' }",
            "    $context = (& $dockerExecutable context show | Out-String).Trim()",
            "    if ($LASTEXITCODE -ne 0 -or -not $context) { throw 'Could not resolve the active Docker context.' }",
            "    $targetArguments = @('--context', $context)",
            "    $scope = 'Docker context: ' + $context",
            "    if (-not $env:DOCKER_CONTEXT -and $env:DOCKER_HOST) { $targetArguments = @('--host', $env:DOCKER_HOST); $scope = 'DOCKER_HOST: ' + $env:DOCKER_HOST }",
            "    $containers = @(& $dockerExecutable @targetArguments container ls --all --no-trunc --format '{{.ID}} {{.Names}} {{.Status}}')",
            "    if ($LASTEXITCODE -ne 0) { throw 'Could not list containers. Start Docker Desktop and retry; backend settings were not changed.' }",
            "    if ($containers.Count -eq 0) { Write-Output ($scope + ': no containers to remove.'); exit 0 }",
            "    $ids = @($containers | ForEach-Object { if ($_ -notmatch '^([0-9a-f]{64})\\s+.+$') { throw 'Unexpected container listing; nothing was removed.' }; $Matches[1] })",
            "    Write-Output $scope",
            "    Write-Output 'ALL listed containers, including unrelated containers, will be force-stopped and removed with their writable layers.'",
            "    Write-Output 'Images, volumes and downloaded game folders are preserved. New containers created after this listing are excluded.'",
            "    $containers | ForEach-Object { Write-Output $_ }",
            "    $confirmation = Read-Host 'Type DELETE ALL to remove these containers, or anything else to cancel'",
            "    if ($confirmation -cne 'DELETE ALL') { Write-Output 'Cancelled; nothing was removed.'; exit 0 }",
            "    $failed = @()",
            "    foreach ($id in $ids) {",
            "        Write-Output ((Get-Date -Format o) + ' Removing ' + $id)",
            "        & $dockerExecutable @targetArguments container rm --force $id",
            "        if ($LASTEXITCODE -ne 0) { $failed += $id; Write-Warning ('Could not remove ' + $id) }",
            "    }",
            "    if ($failed.Count -gt 0) { throw ('Removal failed for ' + $failed.Count + ' container(s). Review the output before retrying.') }",
            "    Write-Output 'All listed containers were removed.'",
            "    exit 0",
            "} catch { Write-Error $_ -ErrorAction Continue; exit 1 }"
        };
        var script = string.Join("\r\n", lines) + "\r\n";
        return format == "bat" ? Batch(script, pauseAtEnd: true) : script;
    }
    private static string Batch(string script, bool pauseAtEnd)
    {
        // Only the fixed loader goes on the command line. The full payload stays below
        // exit /b so bulk exports cannot exceed cmd.exe's 8191-character line limit.
        // The filename is passed through the environment, never interpolated as code.
        const string loader = "$raw=[IO.File]::ReadAllText($env:GLM_SCRIPT); $marker=[regex]::Match($raw,'(?m)^# GLM_POWERSHELL_START\\r?$'); if(-not $marker.Success){throw 'Script payload is missing'}; & ([scriptblock]::Create($raw.Substring($marker.Index+$marker.Length)))";
        string tail = pauseAtEnd ? "echo Exit code: %GLM_EXIT%\r\npause\r\nexit /b %GLM_EXIT%\r\n" : "echo Exit code: %GLM_EXIT%\r\nexit /b %GLM_EXIT%\r\n";
        const string powershell = "%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe";
        return "@echo off\r\nsetlocal\r\nset \"GLM_SCRIPT=%~f0\"\r\n" + powershell + " -NoLogo -NoProfile -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(loader)) + "\r\nset \"GLM_EXIT=%ERRORLEVEL%\"\r\n" + tail + "# GLM_POWERSHELL_START\r\n" + script;
    }
    private static string Shell(Game[] games, Preferences settings, bool stop, string shellTarget)
    {
        if (shellTarget is not ("native-linux" or "wsl2")) throw new ArgumentException("Choose Native Linux or WSL2 for Bash exports.");
        var lines = new List<string> { "#!/usr/bin/env bash", "set -euo pipefail", "command -v docker >/dev/null || { echo 'Docker is required'; exit 1; }", "command -v flock >/dev/null || { echo 'flock is required for safe concurrent installs'; exit 1; }" };
        string destination = ShellDestination(settings.MountPath, shellTarget);
        lines.Insert(3, "# Target: " + shellTarget + " · operations match the PowerShell/BAT exports.");
        lines.Add("destination=" + ShQuote(destination)); lines.Add("mkdir -p -- \"$destination/.gamelibrarymanager-locks\"");
        lines.Add("operation_id=\"${GLM_INSTALL_OPERATION_ID:-$(date +%s%N)-$$}\"");
        lines.Add("native_install_lock_held=\"${GLM_NATIVE_INSTALL_LOCK_HELD:-0}\"");
        lines.Add("completed_games=(); failed_games=()");
        lines.Add("native_container_owned() { local name=\"$1\" expected=\"$2\" expected_operation=\"${3:-}\" metadata operation; if ! metadata=$(docker container inspect \"$name\" --format " + ShQuote(ShellOwnershipFormat) + " 2>/dev/null); then return 1; fi; if [ \"$metadata\" != \"$expected\" ]; then echo \"Refusing destructive cleanup for unowned container ${name}.\" >&2; return 2; fi; if [ -n \"$expected_operation\" ]; then if ! operation=$(docker container inspect \"$name\" --format " + ShQuote("{{ index .Config.Labels \"" + OperationLabel + "\" }}") + " 2>/dev/null); then return 2; fi; if [ \"$operation\" != \"$expected_operation\" ]; then echo \"Refusing cleanup for a container owned by another install operation: ${name}.\" >&2; return 2; fi; fi; return 0; }");
        lines.Add("native_install_lock() { local path=\"$1\"; if [ \"$native_install_lock_held\" = 1 ]; then return 0; fi; local deadline=$((SECONDS+86400)); while :; do if exec 9>>\"$path\" 2>/dev/null && flock -n 9 2>/dev/null; then return 0; fi; exec 9>&- 2>/dev/null || true; if [ \"$SECONDS\" -ge \"$deadline\" ]; then echo \"Timed out waiting for the selected game install lock: $path\" >&2; return 1; fi; sleep 0.25; done; }");
        lines.Add("native_install_unlock() { if [ \"$native_install_lock_held\" != 1 ]; then flock -u 9 2>/dev/null || true; exec 9>&- 2>/dev/null || true; fi; }");
        foreach (var game in games)
        {
            string canonicalDestination = Path.GetFullPath(settings.MountPath);
            string containerName = ContainerNameForDestination(game.Id, canonicalDestination);
            var name = ShQuote(containerName);
            var ownership = ShQuote(OwnershipMetadata(game.Id));
            string lockPath = "$destination/.gamelibrarymanager-locks/" + ContainerNameForDestination(game.Id, canonicalDestination) + ".lock";
            lines.Add("if (");
            lines.Add("  native_install_lock \"" + lockPath + "\"");
            if (stop)
            {
                lines.Add("  if native_container_owned " + name + " " + ownership + "; then docker stop " + name + "; else ownership_status=$?; if [ $ownership_status -eq 2 ]; then exit 1; fi; fi");
            }
            else
            {
            var image = ShQuote(settings.DockerUsername + "/" + settings.RepoName + ":" + game.Id);
            lines.Add("  pull_success=0; for attempt in 1 2 3 4 5; do if docker info >/dev/null 2>&1 && docker pull " + image + "; then pull_success=1; break; fi; echo \"[RETRY $attempt/5] Pull failed; waiting before retry...\"; sleep $((attempt * 2)); done");
            lines.Add("  if [ $pull_success -ne 1 ]; then echo " + ShQuote("Docker pull failed for " + game.Name + " after five attempts.") + " >&2; exit 1; fi");
            var folderName = InstallFolder(game.Id);
            var markerPrefix = "GameLibraryManager|" + game.Id + "|";
            lines.Add("  install_folder=\"$destination/" + folderName + "\"");
            lines.Add("  completion_marker=\"$install_folder/" + CompletionMarkerName + "\"");
            lines.Add("  staging_root=\"$destination/" + StagingDirectoryName + "\"; staging_folder=\"$staging_root/" + folderName + "-$operation_id\"; staging_marker=\"$staging_folder/" + CompletionMarkerName + "\"; backup_folder=\"$staging_root/" + folderName + "-previous-$operation_id\"; mkdir -p -- \"$staging_root\"");
            lines.Add("  marker_value=" + ShQuote(markerPrefix) + "\"$operation_id\"");
            const string shellPlayableRegex = "(^|/)(support|_*redist|redistributables?|commonredist|prerequisites|directx|vcredist|dotnet|installers?|crashreporter|crashreportclient|trainer|wemod|fling|flingtrainer|thirdpartylibs|imageioffmpeg|emulators|modding)(/|$)|/(unins|uninstall|setup|install|launcher|gamebootstrapper|eaclauncher|redist|crashreport|crashhandler|crashpad|reporter|helper|quicksfv|dxsetup|vcredist|ue4prereq|ueprereq|dotnet|unitycrashhandler|qtwebengineprocess|yuzu|ryujinx|citron|sudachi|eden|yuzucmd|edencli|edenroom|enbhost|skse|squirrel|inklecate|ffmpeg|languageselector|workshop|unrealcefsubprocess)[^/]*\\.exe$|/[^/]*(editor|toolkit|packager)[^/]*\\.exe$";
            string hostPlayableExecutableCheck = "find \"$staging_folder\" -type f -iname '*.exe' -print | grep -Eiv " + ShQuote(shellPlayableRegex) + " | grep -q .";
            string installedPlayableExecutableCheck = "find \"$install_folder\" -type f -iname '*.exe' -print | grep -Eiv " + ShQuote(shellPlayableRegex) + " | grep -q .";
            var stagingMarkerCheck = "test -s \"$staging_marker\" && test \"$(cat \"$staging_marker\")\" = \"$marker_value\" && " + hostPlayableExecutableCheck;
            var installedMarkerCheck = "test -s \"$completion_marker\" && test \"$(cat \"$completion_marker\")\" = \"$marker_value\" && " + installedPlayableExecutableCheck;
            var cleanup = "if native_container_owned " + name + " " + ownership + " \"$operation_id\"; then docker rm -f " + name + " >/dev/null 2>&1 || { echo 'Could not remove the native container.' >&2; exit 1; }; else ownership_status=$?; if [ $ownership_status -eq 2 ]; then exit 1; fi; fi";
            lines.Add("  run_success=0; payload_invalid=0");
            lines.Add("  for attempt in 1 2 3; do");
            lines.Add("    rm -rf -- \"$staging_folder\"; " + cleanup + "; mkdir -p -- \"$staging_folder\"");
            lines.Add("    if docker container create --name " + name + " --env \"GLM_INSTALL_OPERATION_ID=$operation_id\" --label " + ShQuote("com.gamelibrary.owner=native") + " --label " + ShQuote("com.gamelibrary.game-id=" + game.Id) + " --label \"" + OperationLabel + "=$operation_id\" " + image + " >/dev/null; then create_exit=0; else create_exit=$?; fi");
            lines.Add("    copy_exit=1; if [ \"$create_exit\" -eq 0 ]; then if docker cp --follow-link " + ShQuote(containerName + ":/home/.") + " \"$staging_folder\"; then copy_exit=0; else copy_exit=$?; fi; fi; " + cleanup);
            lines.Add("    if [ \"$create_exit\" -eq 0 ] && [ \"$copy_exit\" -eq 0 ] && ! " + hostPlayableExecutableCheck + "; then payload_invalid=1; rm -rf -- \"$staging_folder\"; echo " + ShQuote("Backup image for " + game.Name + " contains no playable Windows executable. It was not retried and the existing installation was left untouched.") + " >&2; break; fi");
            lines.Add("    if [ \"$create_exit\" -eq 0 ] && [ \"$copy_exit\" -eq 0 ]; then printf '%s\\n' \"$marker_value\" > \"$staging_marker\"; fi");
            lines.Add("    if [ \"$create_exit\" -eq 0 ] && [ \"$copy_exit\" -eq 0 ] && " + stagingMarkerCheck + "; then");
            lines.Add("      backup_moved=0; if [ -e \"$install_folder\" ]; then if ! mv -- \"$install_folder\" \"$backup_folder\"; then echo 'Could not preserve the previous installation before promotion.' >&2; break; fi; backup_moved=1; fi");
            lines.Add("      if mv -- \"$staging_folder\" \"$install_folder\" && " + installedMarkerCheck + "; then if [ \"$backup_moved\" -eq 1 ]; then rm -rf -- \"$backup_folder\"; fi; run_success=1; break; fi");
            lines.Add("      if [ -e \"$install_folder\" ] && [ ! -e \"$staging_folder\" ]; then mv -- \"$install_folder\" \"$staging_folder\" || true; fi; if [ \"$backup_moved\" -eq 1 ] && [ -e \"$backup_folder\" ] && [ ! -e \"$install_folder\" ]; then mv -- \"$backup_folder\" \"$install_folder\" || true; fi");
            lines.Add("    fi");
            lines.Add("    rm -rf -- \"$staging_folder\"; echo \"[RETRY $attempt/3] Docker create/cp failed or completion proof was invalid; waiting before retry...\"; sleep $((attempt * 3))");
            lines.Add("  done");
            lines.Add("  rm -rf -- \"$staging_folder\"; native_install_unlock");
            lines.Add("  if [ $run_success -ne 1 ]; then if [ $payload_invalid -eq 1 ]; then exit 1; fi; echo " + ShQuote("Extraction failed for " + game.Name + " after three attempts. The previous installation was preserved.") + " >&2; exit 1; fi");
            }
            lines.Add("  native_install_unlock");
            lines.Add("); then");
            lines.Add("  completed_games+=(" + ShQuote(game.Id) + ")");
            lines.Add("else");
            lines.Add("  failed_games+=(" + ShQuote(game.Id) + ")");
            lines.Add("  echo " + ShQuote("FAILED " + game.Name + ": selected operation failed; later games were still attempted.") + " >&2");
            lines.Add("fi");
        }
        lines.Add("if [ ${#failed_games[@]} -eq 0 ]; then");
        lines.Add("  echo 'All selected operations completed.'");
        lines.Add("  echo \"Verified ${#completed_games[@]}/" + games.Length + " selected operation(s).\"");
        lines.Add("else");
        lines.Add("  echo \"Install batch completed with failures: ${#completed_games[@]}/" + games.Length + " selected operation(s) completed; failed game(s): ${failed_games[*]}. Existing files were preserved where possible.\" >&2");
        lines.Add("  exit 1");
        lines.Add("fi");
        return string.Join("\n", lines) + "\n";
    }
    private static string ShellDestination(string path, string shellTarget)
    {
        path = path.Trim();
        if (shellTarget == "wsl2") return ToWslPath(path);
        return path.Replace('\\', '/');
    }
    internal static string ToWslPath(string path)
    {
        path = Path.GetFullPath(path.Trim());
        if (Regex.IsMatch(path, @"\A[A-Za-z]:[\\/]"))
            return "/mnt/" + char.ToLowerInvariant(path[0]) + "/" + path[3..].Replace('\\', '/');
        return path.Replace('\\', '/');
    }
}
