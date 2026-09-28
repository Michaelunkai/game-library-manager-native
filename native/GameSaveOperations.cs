using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

internal sealed record GameSaveResult(string LogPath, bool NoSaveData = false, string ReceiptPath = "", string BackupPath = "", string BackupManifestSha256 = "");

internal static class GameSaveOperations
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RestoreGates = new(StringComparer.OrdinalIgnoreCase);
    internal const string BackupHelper = @"F:\study\projects\SystemMonitor\PSProcLasso\Windows\Applications\Gaming\SaveData\AssLatestGameBackup\dist\AssLatestGameBackup.exe";
    internal const string RestoreHelper = @"F:\study\Platforms\windows\functions\Reass.ps1";
    internal const string BackupRoot = @"F:\backup\gamesaves";

    private static string ResolveBackupHelper()
    {
        string[] candidates =
        {
            BackupHelper,
            @"F:\study\Windows\Applications\Gaming\SaveData\AssLatestGameBackup\dist\AssLatestGameBackup.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                @"WindowsPowerShell\legacy-safe-functions\AssLatestGameBackup\AssLatestGameBackup.exe")
        };
        return candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("The game backup engine is missing.", BackupHelper);
    }

    private static string ResolveRestoreHelper()
    {
        string[] candidates =
        {
            RestoreHelper,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                @"WindowsPowerShell\legacy-safe-functions\Invoke-Reass.ps1")
        };
        return candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("The verified restore engine is missing.", RestoreHelper);
    }

    internal static string ValidateExecutable(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || path.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            throw new ArgumentException("Choose this game's executable in Details before using save operations.");
        return Path.GetFullPath(path);
    }

    internal static ProcessStartInfo CreateStart(string executable, bool restore, bool verifyOnly = false)
    {
        executable = ValidateExecutable(executable);
        ProcessStartInfo start;
        if (restore)
        {
            string helper = ResolveRestoreHelper();
            start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"));
            foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", helper,
                "-GameExecutable", executable }) start.ArgumentList.Add(arg);
            if (verifyOnly) start.ArgumentList.Add("-VerifyOnly");
        }
        else
        {
            if (!File.Exists(executable)) throw new FileNotFoundException("This game's executable is missing.", executable);
            string helper = ResolveBackupHelper();
            // Legacy ass ignores unknown arguments and selects the latest game.
            // Never probe it by execution, even with --help or --capabilities.
            var version = FileVersionInfo.GetVersionInfo(helper);
            if (version.FileMajorPart < 1 || (version.FileMajorPart == 1 && version.FileMinorPart < 1))
                throw new InvalidOperationException("The backup engine needs the verified exact-game update (version 1.1 or later).");
            start = new ProcessStartInfo(helper);
            start.ArgumentList.Add("--game-executable"); start.ArgumentList.Add(executable);
        }
        start.UseShellExecute = false; start.CreateNoWindow = true;
        start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        return start;
    }

    internal static GameSaveResult InterpretResult(int exitCode, string text, bool restore, string log)
    {
        if (exitCode != 0) throw new IOException((restore ? "Restore" : "Backup") + " failed (exit " + exitCode + "). Details: " + log);
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        bool noSaves = lines.Any(line => line.StartsWith("ASS_NO_SAVES ", StringComparison.Ordinal));
        bool completed = lines.Any(line => line.StartsWith(restore ? "REASS_OK game=" : "BACKUP_OK path=", StringComparison.Ordinal));
        if (!restore && noSaves && !completed) return new GameSaveResult(log, true);
        if (!completed || noSaves) throw new IOException("The save engine did not confirm a consistent completion result. Details: " + log);
        return new GameSaveResult(log);
    }

    internal static async Task<GameSaveResult> RunAsync(LibraryStore store, string executable, bool restore)
    {
        executable = ValidateExecutable(executable);
        if (!Directory.Exists(BackupRoot)) throw new DirectoryNotFoundException("The game-save backup destination is unavailable: " + BackupRoot);
        if (!restore) return await RunHelperAsync(store, executable, restore: false, verifyOnly: false);

        string gateKey = Path.GetFullPath(executable);
        SemaphoreSlim gate = RestoreGates.GetOrAdd(gateKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            EnsureGameNotRunning(executable);
            // The helper verifies the selected backup without touching live save targets.
            GameSaveResult preflight = await RunHelperAsync(store, executable, restore: true, verifyOnly: true);
            EnsureGameNotRunning(executable);
            return await RunHelperAsync(store, executable, restore: true, verifyOnly: false,
                expectedBackupManifest: preflight.ReceiptPath, expectedBackupManifestSha256: preflight.BackupManifestSha256);
        }
        finally { gate.Release(); }
    }

    internal static void EnsureGameNotRunning(string executable)
    {
        string fullPath = Path.GetFullPath(executable);
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(fullPath)))
        {
            using (process)
            {
                try
                {
                    if (process.HasExited) continue;
                    string? runningPath = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(runningPath))
                        throw new InvalidOperationException("A same-named process could not be identified; restore remains blocked for safety.");
                    if (Path.GetFullPath(runningPath).Equals(fullPath, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Exit this game before restoring its saves: " + fullPath);
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    throw new InvalidOperationException("A same-named process could not be identified; restore remains blocked for safety.");
                }
                catch (InvalidOperationException) { throw; }
            }
        }
    }

    private static async Task<GameSaveResult> RunHelperAsync(LibraryStore store, string executable, bool restore, bool verifyOnly,
        string? expectedBackupManifest = null, string? expectedBackupManifestSha256 = null)
    {
        var start = CreateStart(executable, restore, verifyOnly);
        string log = JobWindow.BuildJobLogPath(store.Root, DateTime.Now, Guid.NewGuid());
        string resultPath = Path.ChangeExtension(log, ".result.json");
        if (restore)
        {
            string script = start.ArgumentList[5];
            string source = File.ReadAllText(script);
            if (!source.Contains("[string]$ResultPath", StringComparison.Ordinal) ||
                !source.Contains("[string]$GameExecutable", StringComparison.Ordinal) ||
                !source.Contains("[string]$ExpectedBackupManifest", StringComparison.Ordinal) ||
                !source.Contains("[string]$ExpectedBackupManifestSha256", StringComparison.Ordinal))
                throw new InvalidOperationException("The restore helper does not support exact-game pinned structured results: " + script);
            if (!verifyOnly)
            {
                if (string.IsNullOrWhiteSpace(expectedBackupManifest) || string.IsNullOrWhiteSpace(expectedBackupManifestSha256))
                    throw new InvalidOperationException("Restore requires the exact manifest verified by its preflight.");
                start.ArgumentList.Add("-ExpectedBackupManifest");
                start.ArgumentList.Add(expectedBackupManifest);
                start.ArgumentList.Add("-ExpectedBackupManifestSha256");
                start.ArgumentList.Add(expectedBackupManifestSha256);
            }
            start.ArgumentList.Add("-ResultPath");
        }
        else
        {
            await VerifyBackupCapabilitiesAsync(start.FileName);
            start.ArgumentList.Add("--result-json");
        }
        start.ArgumentList.Add(resultPath);
        if (restore && !verifyOnly) EnsureGameNotRunning(executable);
        using var process = Process.Start(start) ?? throw new IOException("The save operation could not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string text = await output + Environment.NewLine + await error;
        LibraryStore.AtomicWrite(log, text);
        using var structured = ReadStructuredResult(resultPath, executable, restore, verifyOnly, process.ExitCode, log);
        if (verifyOnly)
        {
            if (process.ExitCode != 0 || !text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Any(line => line.StartsWith("REASS_VERIFY_OK game=", StringComparison.Ordinal)))
                throw new IOException("Restore preflight did not verify a matching backup. Details: " + log);
            return VerifyRestorePreflight(structured.RootElement, text, executable, log, BackupRoot);
        }
        var result = InterpretResult(process.ExitCode, text, restore, log);
        result = VerifyPhysicalReceipt(result, text, executable, restore);
        var record = structured.RootElement;
        string receiptDirectory = RequireFullPath(record, "receiptDirectory");
        if (!receiptDirectory.Equals(Path.GetDirectoryName(result.ReceiptPath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The structured and physical save receipts disagree. Details: " + log);
        string manifest = RequireFullPath(record, "backupManifest");
        if (!Path.GetFullPath(manifest).Equals(Path.Combine(result.BackupPath, "backup.json"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The structured save result identifies another backup manifest. Details: " + log);
        VerifyStructuredPhysicalResult(record, result, restore, log);
        return result;
    }

    private static GameSaveResult VerifyRestorePreflight(JsonElement record, string text, string executable, string log, string backupRoot)
    {
        string directory = EnsureBackupChild(RequireFullPath(record, "receiptDirectory"), backupRoot, log);
        string manifest = EnsureBackupChild(RequireFullPath(record, "backupManifest"), backupRoot, log);
        if (!Path.Combine(directory, "backup.json").Equals(manifest, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Restore preflight sidecar does not identify its selected backup manifest. Details: " + log);
        string outputDirectory = ReadBracketedPath(text, "REASS_VERIFY_OK game=", "path=[", log);
        if (!Path.GetFullPath(outputDirectory).Equals(directory, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Restore preflight structured and physical backup paths disagree. Details: " + log);

        using var document = JsonDocument.Parse(File.ReadAllText(manifest));
        JsonElement audit = document.RootElement;
        if (!string.Equals(audit.GetProperty("status").GetString(), "complete", StringComparison.Ordinal))
            throw new IOException("Restore preflight selected a backup without a complete physical manifest. Details: " + log);
        string recordedExecutable = audit.GetProperty("executable").GetString() ?? "";
        if (!Path.IsPathFullyQualified(recordedExecutable) ||
            !Path.GetFullPath(recordedExecutable).Equals(Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Restore preflight selected a backup for another executable. Details: " + log);
        VerifyBackupPayload(directory, audit);
        VerifyStructuredCounts(record, audit, log);
        string manifestHash = HashFile(manifest);
        string structuredHash = record.GetProperty("backupManifestSha256").GetString() ?? "";
        if (!manifestHash.Equals(structuredHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Restore preflight manifest hash disagrees with its physical file. Details: " + log);
        return new GameSaveResult(log, ReceiptPath: manifest, BackupPath: directory, BackupManifestSha256: manifestHash);
    }

    private static void VerifyStructuredPhysicalResult(JsonElement record, GameSaveResult result, bool restore, string log)
    {
        string expectedOutcome = restore ? "restored" : result.NoSaveData ? "no_saves" : "backed_up";
        if (!string.Equals(record.GetProperty("outcome").GetString(), expectedOutcome, StringComparison.Ordinal))
            throw new IOException("Structured save outcome disagrees with the physical receipt. Details: " + log);

        string manifest = Path.Combine(result.BackupPath, "backup.json");
        using var document = JsonDocument.Parse(File.ReadAllText(manifest));
        VerifyStructuredCounts(record, document.RootElement, log);
        if (restore)
        {
            if (!record.TryGetProperty("backupManifestSha256", out JsonElement restoreHash) ||
                restoreHash.ValueKind != JsonValueKind.String ||
                !string.Equals(restoreHash.GetString(), result.BackupManifestSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Restore structured manifest hash disagrees with the physical receipt. Details: " + log);
        }
        else if (record.TryGetProperty("backupManifestSha256", out JsonElement backupHash) &&
            (backupHash.ValueKind != JsonValueKind.String ||
             !string.Equals(backupHash.GetString(), result.BackupManifestSha256, StringComparison.OrdinalIgnoreCase)))
        {
            throw new IOException("Structured save manifest hash disagrees with the physical receipt. Details: " + log);
        }
    }

    private static void VerifyStructuredCounts(JsonElement record, JsonElement audit, string log)
    {
        int fileCount = record.GetProperty("fileCount").GetInt32();
        int registryCount = record.GetProperty("registryCount").GetInt32();
        long verifiedBytes = record.GetProperty("verifiedBytes").GetInt64();
        int physicalFiles = audit.GetProperty("files").GetInt32();
        int physicalFileEntries = audit.GetProperty("fileEntries").GetArrayLength();
        int physicalRegistryEntries = audit.GetProperty("registryEntries").GetArrayLength();
        long physicalBytes = audit.GetProperty("bytes").GetInt64();
        if (fileCount != physicalFiles || fileCount != physicalFileEntries ||
            registryCount != physicalRegistryEntries || verifiedBytes != physicalBytes)
            throw new IOException("Structured save counts or bytes disagree with the physical manifest. Details: " + log);
    }

    private static string RequireFullPath(JsonElement record, string property)
    {
        if (!record.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw new IOException("The structured save result omitted " + property + ".");
        string path = value.GetString() ?? "";
        if (!Path.IsPathFullyQualified(path)) throw new IOException("The structured save result contains a non-absolute " + property + ".");
        return Path.GetFullPath(path);
    }

    private static string EnsureBackupChild(string path, string backupRoot, string log)
    {
        string full = Path.GetFullPath(path);
        string parent = Path.GetFullPath(backupRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string root = parent + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("A structured or physical save path is outside the backup destination. Details: " + log);
        // A manifest path can be lexically inside the backup root while an NTFS
        // junction or symbolic link redirects the payload to another directory.
        // Refuse such receipts before reading or restoring their contents.
        string current = parent;
        if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("A save receipt path traverses a reparse point. Details: " + log);
        foreach (string component in Path.GetRelativePath(parent, full).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, component);
            if ((File.Exists(current) || Directory.Exists(current))
                && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("A save receipt path traverses a reparse point. Details: " + log);
        }
        return full;
    }

    private static string ReadBracketedPath(string text, string linePrefix, string pathMarker, string log)
    {
        string line = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(item => item.StartsWith(linePrefix, StringComparison.Ordinal))
            ?? throw new IOException("The save helper did not return the selected backup path. Details: " + log);
        int start = line.IndexOf(pathMarker, StringComparison.Ordinal);
        int end = line.LastIndexOf(']');
        if (start < 0 || end <= start + pathMarker.Length)
            throw new IOException("The save helper returned a malformed selected backup path. Details: " + log);
        return line.Substring(start + pathMarker.Length, end - start - pathMarker.Length);
    }

    private static async Task VerifyBackupCapabilitiesAsync(string helper)
    {
        var probe = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        probe.ArgumentList.Add("--capabilities");
        using var process = Process.Start(probe) ?? throw new IOException("The backup helper capability check could not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string response = await output;
        _ = await error;
        if (process.ExitCode != 0) throw new InvalidOperationException("The backup helper capability check failed.");
        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        if (!root.TryGetProperty("exactGameExecutable", out var exact) || exact.ValueKind != JsonValueKind.True ||
            !root.TryGetProperty("resultJson", out var structured) || structured.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("The backup helper needs exact-game structured-result support.");
    }

    internal static JsonDocument ReadStructuredResult(string resultPath, string executable, bool restore, bool verifyOnly,
        int exitCode, string? operationLogPath = null)
    {
        if (!File.Exists(resultPath))
            throw new IOException(WithOperationLog("The save helper did not create its structured result: " + resultPath, operationLogPath));
        var document = JsonDocument.Parse(File.ReadAllText(resultPath));
        try
        {
            var root = document.RootElement;
            string selectedExecutable = RequireFullPath(root, "selectedExecutable");
            if (root.GetProperty("schemaVersion").GetInt32() != 1 ||
                !string.Equals(root.GetProperty("operation").GetString(), restore ? "restore" : "backup", StringComparison.Ordinal) ||
                !string.Equals(selectedExecutable, Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase))
                throw new IOException(WithOperationLog("The structured save result does not match the selected game.", operationLogPath));
            string outcome = root.GetProperty("outcome").GetString() ?? "";
            string integrity = root.GetProperty("integrity").GetString() ?? "";
            if (exitCode != 0)
            {
                if (outcome == "failed" && integrity == "failed")
                {
                    string errorCode = StructuredText(root, "errorCode");
                    string errorMessage = StructuredText(root, "errorMessage").Replace('\r', ' ').Replace('\n', ' ').Trim();
                    string details = string.Join(": ", new[] { errorCode, errorMessage }.Where(value => !string.IsNullOrWhiteSpace(value)));
                    string failure = (restore ? "Restore" : "Backup") + " failed (exit " + exitCode + ")." +
                        (details.Length == 0 ? " The helper returned no structured error details." : " " + details);
                    throw new IOException(WithOperationLog(failure, operationLogPath));
                }
                throw new IOException(WithOperationLog(
                    "The save helper failed (exit " + exitCode + ") without a matching structured failure result.", operationLogPath));
            }
            bool valid = restore ? outcome == (verifyOnly ? "verified" : "restored")
                : outcome is "backed_up" or "no_saves";
            if (!valid || integrity != "verified")
                throw new IOException(WithOperationLog("The save helper did not report a verified structured outcome.", operationLogPath));
            return document;
        }
        catch { document.Dispose(); throw; }
    }

    private static string StructuredText(JsonElement record, string property) =>
        record.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private static string WithOperationLog(string message, string? operationLogPath) =>
        string.IsNullOrWhiteSpace(operationLogPath) ? message : message + " Details: " + operationLogPath;

    internal static GameSaveResult VerifyPhysicalReceipt(GameSaveResult result, string text, string executable, bool restore,
        string backupRoot = BackupRoot)
    {
        var line = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(item => item.StartsWith(restore ? "REASS_OK game=" : result.NoSaveData ? "ASS_NO_SAVES " : "BACKUP_OK path=", StringComparison.Ordinal))
            ?? throw new IOException("The save helper did not return a receipt location.");
        string marker = restore ? "pre_restore=[" : result.NoSaveData ? "record=" : "path=";
        int at = line.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) throw new IOException("The save helper did not return a receipt location.");
        string tail = line[(at + marker.Length)..];
        string directory = restore ? tail.Split(']')[0]
            : result.NoSaveData ? tail.Trim()
            : tail.Split(new[] { " files=" }, StringSplitOptions.None)[0];
        directory = directory.Trim();
        if (!Path.IsPathFullyQualified(directory))
            throw new IOException("The save helper returned a non-absolute receipt path.");
        directory = Path.GetFullPath(directory);
        directory = EnsureBackupChild(directory, backupRoot, "physical receipt");
        string root = Path.GetFullPath(backupRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string receipt = Path.Combine(directory, restore ? "pre-restore.json" : "backup.json");
        using var document = JsonDocument.Parse(File.ReadAllText(receipt));
        var json = document.RootElement;
        string status = json.GetProperty("status").GetString() ?? "";
        if (!status.Equals(restore ? "complete" : result.NoSaveData ? "no-saves" : "complete", StringComparison.Ordinal))
            throw new IOException("The physical save receipt has an inconsistent outcome: " + receipt);
        string selected = Path.GetFullPath(executable);
        string backupPath = restore ? json.GetProperty("sourceBackup").GetString() ?? "" : directory;
        if (!Path.IsPathFullyQualified(backupPath))
            throw new IOException("The physical save receipt contains a non-absolute backup path: " + receipt);
        backupPath = Path.GetFullPath(backupPath);
        backupPath = EnsureBackupChild(backupPath, backupRoot, "physical backup");
        string backupReceipt = Path.Combine(backupPath, "backup.json");
        using var backupDocument = restore ? JsonDocument.Parse(File.ReadAllText(backupReceipt)) : null;
        var backupJson = restore ? backupDocument!.RootElement : json;
        string recordedExecutable = backupJson.GetProperty("executable").GetString() ?? "";
        if (!Path.IsPathFullyQualified(recordedExecutable) ||
            !Path.GetFullPath(recordedExecutable).Equals(selected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The physical save receipt belongs to another executable: " + receipt);
        VerifyBackupPayload(backupPath, backupJson);
        string manifestHash = HashFile(backupReceipt);
        return result with { ReceiptPath = receipt, BackupPath = backupPath, BackupManifestSha256 = manifestHash };
    }

    private static void VerifyBackupPayload(string directory, JsonElement audit)
    {
        int fileCount = 0;
        int registryCount = 0;
        long fileBytes = 0;
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (string section in new[] { "fileEntries", "registryEntries" })
        {
            if (!audit.TryGetProperty(section, out var entries) || entries.ValueKind != JsonValueKind.Array)
                throw new IOException("The save manifest is missing " + section + ".");
            foreach (var entry in entries.EnumerateArray())
            {
                string relative = entry.GetProperty("path").GetString() ?? "";
                string path = Path.GetFullPath(Path.Combine(directory, relative));
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                    throw new IOException("The save manifest points outside its receipt or to a missing file.");
                path = EnsureBackupChild(path, directory, "manifest payload");
                long expectedBytes = entry.GetProperty("bytes").GetInt64();
                if (new FileInfo(path).Length != expectedBytes) throw new IOException("Save payload size differs from its manifest: " + path);
                using var stream = File.OpenRead(path);
                string actualHash = Convert.ToHexString(SHA256.HashData(stream));
                string expectedHash = entry.GetProperty("sha256").GetString() ?? "";
                if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Save payload hash differs from its manifest: " + path);
                if (section == "fileEntries")
                {
                    fileCount++;
                    fileBytes = checked(fileBytes + expectedBytes);
                }
                else registryCount++;
            }
        }
        if (!audit.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Number || files.GetInt32() != fileCount)
            throw new IOException("Save receipt file count is inconsistent.");
        if (!audit.TryGetProperty("bytes", out var totalBytes) || totalBytes.ValueKind != JsonValueKind.Number || totalBytes.GetInt64() != fileBytes)
            throw new IOException("Save receipt byte count is inconsistent.");
        if (audit.TryGetProperty("registryKeys", out var registryKeys) &&
            (registryKeys.ValueKind != JsonValueKind.Array || registryKeys.GetArrayLength() != registryCount))
            throw new IOException("Save receipt registry count is inconsistent.");
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
