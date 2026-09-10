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
            "$nativeInstallLockHeld = $env:GLM_NATIVE_INSTALL_LOCK_HELD -eq '1'"
        };
        lines.Add("function Test-NativeContainer { param([string]$name, [string]$expectedMetadata, [string]$expectedOperationId); try { $inspection = & $dockerExecutable container inspect $name --format " + PsQuote(OwnershipFormat) + " 2>$null } catch { return $false }; $inspectExitCode = $LASTEXITCODE; if ($inspectExitCode -ne 0) { return $false }; try { $labels = (([string]($inspection | Out-String)).Trim() | ConvertFrom-Json) } catch { throw ('Refusing destructive cleanup for unowned container ' + $name + '.') }; $actualMetadata = [string]$labels.'com.gamelibrary.owner' + '|' + [string]$labels.'com.gamelibrary.game-id'; $actualOperationId = [string]$labels.'" + OperationLabel + "'; if ($actualMetadata -cne $expectedMetadata -or ($expectedOperationId -and $actualOperationId -cne $expectedOperationId)) { throw ('Refusing destructive cleanup for unowned container ' + $name + '.') }; return $true }");
        lines.Add("function Enter-NativeInstallLock { param([string]$path); if ($nativeInstallLockHeld) { return $null }; $deadline = [DateTime]::UtcNow.AddHours(24); while ($true) { try { return [IO.File]::Open($path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None, 1, [IO.FileOptions]::WriteThrough) } catch [IO.IOException] { if ([DateTime]::UtcNow -ge $deadline) { throw ('Timed out waiting for the selected game install lock: ' + $path) }; Start-Sleep -Milliseconds 250 } } }");
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
            lines.Add("$installMutex = Enter-NativeInstallLock (Join-Path $destination " + PsQuote(lockPath) + ")");
            lines.Add("try {");
            if (stop)
            {
                lines.Add("if (Test-NativeContainer " + PsQuote(name) + " " + PsQuote(ownership) + ") { & $dockerExecutable stop " + PsQuote(name) + "; if ($LASTEXITCODE -ne 0) { throw 'Could not stop selected container.' } }");
                lines.Add("} finally { Exit-NativeInstallLock $installMutex }");
                continue;
            }
            lines.Add("$pullSuccess = $false; for ($attempt = 1; $attempt -le 5 -and -not $pullSuccess; $attempt++) {");
            lines.Add("  & $dockerExecutable info --format '{{.OSType}}' *> $null; if ($LASTEXITCODE -ne 0) { Write-Warning ('Docker is not ready; retry ' + $attempt + '/5'); Start-Sleep -Seconds ([Math]::Min($attempt * 2, 10)); continue }");
            lines.Add("  & $dockerExecutable pull " + PsQuote(image) + "; if ($LASTEXITCODE -eq 0) { $pullSuccess = $true } else { Write-Warning ('Pull failed for " + game.Id + " on retry ' + $attempt + '/5'); if ($attempt -lt 5) { Start-Sleep -Seconds ([Math]::Min($attempt * 2, 10)) } }");
            lines.Add("}");
            lines.Add("if (-not $pullSuccess) { throw " + PsQuote("Docker pull failed for " + game.Id + " after five attempts. Existing files were preserved.") + " }");
            string folderName = InstallFolder(game.Id);
            string folder = "/output/" + folderName;
            string marker = folder + "/" + CompletionMarkerName;
            string hostMarker = Path.Combine(folderName, CompletionMarkerName);
            string markerPrefix = "GameLibraryManager|" + game.Id + "|";
            lines.Add("$markerValue = " + PsQuote(markerPrefix) + " + $operationId; $completionMarker = Join-Path $destination " + PsQuote(hostMarker) + "; Remove-Item -LiteralPath $completionMarker -Force -ErrorAction SilentlyContinue");
            // Docker Desktop bind mounts map the Linux files into Windows storage. BusyBox
            // `cp -a` attempts to preserve Unix modes/owners and reports an I/O error for
            // files that copied successfully. Copy recursively without attribute
            // preservation, then require a non-empty destination before reporting success.
            string shell = "set -eu; mkdir -p " + ShQuote(folder) + "; rm -f " + ShQuote(marker) + "; cp -rL /home/. " + ShQuote(folder + "/") + "; rm -f " + ShQuote(marker) + "; find " + ShQuote(folder) + " -mindepth 1 -print -quit | grep -q .; marker_value=" + ShQuote(markerPrefix) + "$GLM_INSTALL_OPERATION_ID; printf '%s\\n' \"$marker_value\" > " + ShQuote(marker) + "; test -s " + ShQuote(marker) + "; du -sh " + ShQuote(folder);
            string cleanup = "if (Test-NativeContainer " + PsQuote(name) + " " + PsQuote(ownership) + " $operationId) { & $dockerExecutable container rm --force " + PsQuote(name) + " *> $null; if ($LASTEXITCODE -ne 0) { throw 'Could not remove the native container.' } }";
            lines.Add("$runSuccess = $false; for ($attempt = 1; $attempt -le 3 -and -not $runSuccess; $attempt++) {");
            lines.Add("  Remove-Item -LiteralPath $completionMarker -Force -ErrorAction SilentlyContinue; " + cleanup + "; & $dockerExecutable run --rm --name " + PsQuote(name) + " --env ('GLM_INSTALL_OPERATION_ID=' + $operationId) --label " + PsQuote("com.gamelibrary.owner=native") + " --label " + PsQuote("com.gamelibrary.game-id=" + game.Id) + " --label (" + PsQuote(OperationLabel + "=") + " + $operationId) --mount (\"type=bind,source=$destination,target=/output\") " + PsQuote(image) + " sh -c " + PsQuote(shell) + "; $markerContent = if (Test-Path -LiteralPath $completionMarker -PathType Leaf) { Get-Content -LiteralPath $completionMarker -Raw -ErrorAction SilentlyContinue } else { '' }; if ($LASTEXITCODE -eq 0 -and ([string]$markerContent).Trim() -eq $markerValue) { $runSuccess = $true } else { Write-Warning ('Extraction failed for " + game.Id + " on attempt ' + $attempt + '/3; the completion marker was not written or was invalid.'); " + cleanup + "; if ($attempt -lt 3) { Start-Sleep -Seconds ([Math]::Min($attempt * 3, 15)) } }");
            lines.Add("}");
            lines.Add("if (-not $runSuccess) { throw " + PsQuote("Extraction failed for " + game.Id + " after three attempts. Partial files were preserved; review the log and retry.") + " }");
            lines.Add("Write-Output ((Get-Date -Format o) + " + PsQuote(" Completed " + game.Id) + ")");
            lines.Add("} finally { Exit-NativeInstallLock $installMutex }");
        }
        lines.Add("Write-Output 'All selected operations completed.'");
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
        lines.Add("native_container_owned() { local name=\"$1\" expected=\"$2\" expected_operation=\"${3:-}\" metadata operation; if ! metadata=$(docker container inspect \"$name\" --format " + ShQuote(ShellOwnershipFormat) + " 2>/dev/null); then return 1; fi; if [ \"$metadata\" != \"$expected\" ]; then echo \"Refusing destructive cleanup for unowned container ${name}.\" >&2; return 2; fi; if [ -n \"$expected_operation\" ]; then if ! operation=$(docker container inspect \"$name\" --format " + ShQuote("{{ index .Config.Labels \"" + OperationLabel + "\" }}") + " 2>/dev/null); then return 2; fi; if [ \"$operation\" != \"$expected_operation\" ]; then echo \"Refusing cleanup for a container owned by another install operation: ${name}.\" >&2; return 2; fi; fi; return 0; }");
        lines.Add("native_install_lock() { local path=\"$1\"; if [ \"$native_install_lock_held\" = 1 ]; then return 0; fi; local deadline=$((SECONDS+86400)); while :; do if exec 9>>\"$path\" 2>/dev/null && flock -n 9 2>/dev/null; then return 0; fi; exec 9>&- 2>/dev/null || true; if [ \"$SECONDS\" -ge \"$deadline\" ]; then echo \"Timed out waiting for the selected game install lock: $path\" >&2; return 1; fi; sleep 0.25; done; }");
        lines.Add("native_install_unlock() { if [ \"$native_install_lock_held\" != 1 ]; then flock -u 9 2>/dev/null || true; exec 9>&- 2>/dev/null || true; fi; }");
        foreach (var game in games)
        {
            string canonicalDestination = Path.GetFullPath(settings.MountPath);
            var name = ShQuote(ContainerNameForDestination(game.Id, canonicalDestination));
            var ownership = ShQuote(OwnershipMetadata(game.Id));
            string lockPath = "$destination/.gamelibrarymanager-locks/" + ContainerNameForDestination(game.Id, canonicalDestination) + ".lock";
            lines.Add("native_install_lock \"" + lockPath + "\"");
            if (stop) { lines.Add("if native_container_owned " + name + " " + ownership + "; then docker stop " + name + "; else ownership_status=$?; if [ $ownership_status -eq 2 ]; then exit 1; fi; fi"); lines.Add("native_install_unlock"); continue; }
            var image = ShQuote(settings.DockerUsername + "/" + settings.RepoName + ":" + game.Id);
            lines.Add("pull_success=0; for attempt in 1 2 3 4 5; do if docker info >/dev/null 2>&1 && docker pull " + image + "; then pull_success=1; break; fi; echo \"[RETRY $attempt/5] Pull failed; waiting before retry...\"; sleep $((attempt * 2)); done");
            lines.Add("if [ $pull_success -ne 1 ]; then echo " + ShQuote("Docker pull failed for " + game.Name + " after five attempts.") + " >&2; exit 1; fi");
            var folder = "/output/" + InstallFolder(game.Id);
            var marker = folder + "/" + CompletionMarkerName;
            var markerPrefix = "GameLibraryManager|" + game.Id + "|";
            lines.Add("completion_marker=\"$destination/" + InstallFolder(game.Id) + "/" + CompletionMarkerName + "\"");
            lines.Add("marker_value=" + ShQuote(markerPrefix) + "\"$operation_id\"");
            var copy = "set -eu; mkdir -p " + ShQuote(folder) + "; rm -f " + ShQuote(marker) + "; cp -rL /home/. " + ShQuote(folder + "/") + "; rm -f " + ShQuote(marker) + "; find " + ShQuote(folder) + " -mindepth 1 -print -quit | grep -q .; marker_value=" + ShQuote(markerPrefix) + "$GLM_INSTALL_OPERATION_ID; printf '%s\\n' \"$marker_value\" > " + ShQuote(marker) + "; test -s " + ShQuote(marker);
            var markerCheck = "test -s \"$completion_marker\" && test \"$(cat \"$completion_marker\")\" = \"$marker_value\"";
            var cleanup = "if native_container_owned " + name + " " + ownership + " \"$operation_id\"; then docker rm -f " + name + " >/dev/null 2>&1 || { echo 'Could not remove the native container.' >&2; exit 1; }; else ownership_status=$?; if [ $ownership_status -eq 2 ]; then exit 1; fi; fi";
            lines.Add("run_success=0; for attempt in 1 2 3; do rm -f -- \"$completion_marker\"; " + cleanup + "; if docker run --rm --name " + name + " --env \"GLM_INSTALL_OPERATION_ID=$operation_id\" --label " + ShQuote("com.gamelibrary.owner=native") + " --label " + ShQuote("com.gamelibrary.game-id=" + game.Id) + " --label \"" + OperationLabel + "=$operation_id\" --mount \"type=bind,source=$destination,target=/output\" " + image + " sh -c " + ShQuote(copy) + " && " + markerCheck + "; then run_success=1; break; fi; " + cleanup + "; echo \"[RETRY $attempt/3] Extraction failed or completion marker missing; waiting before retry...\"; sleep $((attempt * 3)); done");
            lines.Add("if [ $run_success -ne 1 ]; then echo " + ShQuote("Extraction failed for " + game.Name + " after three attempts.") + " >&2; exit 1; fi");
            lines.Add("native_install_unlock");
        }
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
