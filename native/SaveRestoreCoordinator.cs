using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

// What actually happened to one touched path. Partial work is always visible so
// a partially successful operation can never be reported as a clean success.
internal enum SaveItemOutcome { Copied, Skipped, Unstable, Failed, Quarantined, RolledBack, Verified }

// Explicit, testable policy for a live game. The user asked for save operations to
// work while the game is running, so Snapshot is the default everywhere.
internal enum RunningGamePolicy { Refuse, Snapshot }

internal enum SaveOperationStatus { Completed, NoSaveData, Refused, Partial, RolledBack, Cancelled, Failed }

internal sealed record SaveItemReport(string Path, SaveItemOutcome Outcome, long Bytes, string Detail);

internal sealed record SaveOperationOutcome(bool Success, IReadOnlyList<SaveItemReport> Items, string BackupRoot,
    string? QuarantineRoot, string Summary)
{
    internal SaveOperationStatus Status { get; init; } = SaveOperationStatus.Completed;
    internal RunningGamePolicy Policy { get; init; } = RunningGamePolicy.Snapshot;

    // Check this before Success: NoSaveData is a clean informational result and
    // keeps Success true so the UI can keep its old "nothing to save" message.
    internal bool NoSaveData => Status == SaveOperationStatus.NoSaveData;
    internal int CountOf(SaveItemOutcome outcome) => Items.Count(item => item.Outcome == outcome);
    internal IEnumerable<SaveItemReport> Of(SaveItemOutcome outcome) => Items.Where(item => item.Outcome == outcome);
}

internal sealed record SaveOperationProgress(string Phase, string CurrentPath, int Completed, int Total, long Bytes);

internal sealed record ManifestVerificationResult(bool IsIntact, IReadOnlyList<SaveItemReport> Items, string Summary);

internal sealed record RunningProcessProbe(bool Identified, bool Running, string Detail);

internal sealed record SaveOperationRequest(string GameId, string GameName, IReadOnlyList<string> SaveRoots,
    string InstallFolder, string? RunningProcessName, bool AllowWhileRunning);

// The coordinator compiles against this seam only, so the save-data locator is
// wired in later without this file having to know how roots are discovered.
internal interface ISaveRootProvider
{
    public IReadOnlyList<string> GetRoots(SaveOperationRequest request);
}

internal sealed class NullSaveRootProvider : ISaveRootProvider
{
    internal static readonly NullSaveRootProvider Instance = new();

    public IReadOnlyList<string> GetRoots(SaveOperationRequest request) =>
        request?.SaveRoots is { } roots ? roots : Array.Empty<string>();
}

// The injectable filesystem seam. Everything the coordinator mutates goes through
// here, which is what makes the whole flow unit-testable on a temp directory with
// no real game anywhere in sight.
internal interface IBackupWriter
{
    public void EnsureDirectory(string path);
    public Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken);
    public void MovePath(string source, string destination);
    public Task<string> HashFileAsync(string path, CancellationToken cancellationToken);
    public void DeletePath(string path);
}

internal sealed class FileSystemBackupWriter : IBackupWriter
{
    internal const int BufferSize = 128 * 1024;

    public void EnsureDirectory(string path)
    {
        if (!string.IsNullOrWhiteSpace(path)) Directory.CreateDirectory(path);
    }

    public async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        DateTime lastWrite = File.GetLastWriteTimeUtc(source);
        // FileShare.ReadWrite | FileShare.Delete never blocks the running game's own writes.
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, BufferSize, useAsync: true);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write,
            FileShare.Read, BufferSize, useAsync: true);
        await input.CopyToAsync(output, BufferSize, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
        try { File.SetLastWriteTimeUtc(destination, lastWrite); }
        catch (IOException) { } // a live game may re-stamp its own files; content is what matters
        catch (UnauthorizedAccessException) { }
    }

    public void MovePath(string source, string destination)
    {
        if (Directory.Exists(source)) { MoveDirectory(source, destination); return; }
        string? parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        File.Move(source, destination);
    }

    private static void MoveDirectory(string source, string destination)
    {
        string? parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        try { Directory.Move(source, destination); return; }
        catch (IOException) { }
        CopyDirectoryThenRemove(source, destination);
    }

    private static void CopyDirectoryThenRemove(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (string directory in Directory.EnumerateDirectories(source))
            CopyDirectoryThenRemove(directory, Path.Combine(destination, Path.GetFileName(directory)));
        // Only ever reached for a move the coordinator itself authored.
        Directory.Delete(source, recursive: true);
    }

    public Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, BufferSize, useAsync: true);
        return Task.FromResult(Convert.ToHexString(SHA256.HashData(stream)));
    }

    public void DeletePath(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else if (File.Exists(path)) File.Delete(path);
    }
}

internal static class SaveOperationDiagnostics
{
    private static int snapshotRetries;
    private static int volatileSkips;
    private static int inconsistentFiles;
    private static int rollbacks;

    internal static int SnapshotRetries => Volatile.Read(ref snapshotRetries);
    internal static int VolatileSkips => Volatile.Read(ref volatileSkips);
    internal static int InconsistentFiles => Volatile.Read(ref inconsistentFiles);
    internal static int Rollbacks => Volatile.Read(ref rollbacks);

    internal static void RecordSnapshotRetry() => Interlocked.Increment(ref snapshotRetries);
    internal static void RecordVolatileSkip() => Interlocked.Increment(ref volatileSkips);
    internal static void RecordInconsistentFile() => Interlocked.Increment(ref inconsistentFiles);
    internal static void RecordRollback() => Interlocked.Increment(ref rollbacks);

    internal static void Reset()
    {
        Volatile.Write(ref snapshotRetries, 0);
        Volatile.Write(ref volatileSkips, 0);
        Volatile.Write(ref inconsistentFiles, 0);
        Volatile.Write(ref rollbacks, 0);
    }
}

/// <summary>
/// Quantised consistent-snapshot backup plus verify-then-quarantine-swap restore.
/// </summary>
/// <remarks>
/// Backup succeeds while the game is running. Every file is copied, then re-read
/// and compared (size, last-write time, and a full content hash below
/// FullHashThresholdBytes). A file the game is mutating mid-read is retried a
/// bounded number of times and, if it never settles, is reported as Unstable
/// rather than captured torn. Restore never touches a live save until the backup
/// manifest has been re-verified, and every write that can fail is wrapped in a
/// quarantine-then-swap whose rollback lives in a finally block and is itself
/// verified. Nothing is ever deleted unless the coordinator created it.
/// </remarks>
internal static class SaveRestoreCoordinator
{
    internal const int MaxParallelFileOperations = 4;
    internal const int MaxSnapshotAttempts = 6;
    internal const int SnapshotRetryDelayMilliseconds = 25;
    internal const int MaxVerifiedRollbackFiles = 4096;
    internal const long FullHashThresholdBytes = 8L * 1024L * 1024L;
    internal const string TimestampFormat = "yyyyMMdd-HHmmss-fff";
    internal const string QuarantinePrefix = ".pre-restore-";
    internal const string ManifestFileName = "save-manifest.v1.txt";
    internal const string RootsMapFileName = "save-roots.v1.txt";
    internal const string SummaryFileName = "save-summary.v1.txt";
    internal const string ManifestHeader = "GAME_LIBRARY_SAVE_MANIFEST\t1";
    internal const string RootsMapHeader = "GAME_LIBRARY_SAVE_ROOTS\t1";
    internal const string PayloadFolder = "payload";
    private const string StagingFolder = ".staging";
    private const string UnstableFolder = ".unstable";
    private const string AbandonedFolder = "abandoned";
    private const string LiveFolder = "live";
    private const string RestoreStagingFolder = "staging";
    private const string DetailSeparator = ";";
    private static readonly string[] VolatileSuffixes = { ".tmp", ".temp", ".new", ".bak", ".~", "~" };

    internal static RunningGamePolicy DefaultRunningGamePolicy => RunningGamePolicy.Snapshot;

    internal static RunningGamePolicy ResolvePolicy(SaveOperationRequest request) =>
        request.AllowWhileRunning ? RunningGamePolicy.Snapshot : RunningGamePolicy.Refuse;

    internal static Task<bool> CanRestoreWhileRunningAsync(SaveOperationRequest request,
        CancellationToken cancellationToken = default) =>
        CanRestoreWhileRunningAsync(request, ResolvePolicy(request), cancellationToken);

    internal static Task<bool> CanRestoreWhileRunningAsync(SaveOperationRequest request, RunningGamePolicy policy,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null) return Task.FromResult(false);
        return Task.FromResult(policy == RunningGamePolicy.Snapshot || request.AllowWhileRunning);
    }

    // Best-effort process observation only. It never gates an operation by itself:
    // an unidentifiable process must not silently turn into a refusal.
    internal static Task<RunningProcessProbe> ProbeRunningProcessAsync(string? runningProcessName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(runningProcessName))
            return Task.FromResult(new RunningProcessProbe(true, false, "No running process was reported for this game."));
        string name = runningProcessName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = Path.GetFileNameWithoutExtension(name);
        try
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        if (process.HasExited) continue;
                        return Task.FromResult(new RunningProcessProbe(true, true, "The game is running (pid " + process.Id + ")."));
                    }
                    catch (InvalidOperationException) { continue; }
                }
            }
            return Task.FromResult(new RunningProcessProbe(true, false, "The game is not currently running."));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Task.FromResult(new RunningProcessProbe(false, false,
                "The running state could not be observed: " + ex.Message));
        }
    }

    internal static Task<SaveOperationOutcome> BackupAsync(SaveOperationRequest request, ISaveRootProvider roots,
        IBackupWriter writer, IProgress<SaveOperationProgress>? progress, CancellationToken ct) =>
        BackupAsync(request, roots, writer, GameSaveOperations.BackupRoot, progress, ct, null);

    internal static async Task<SaveOperationOutcome> BackupAsync(SaveOperationRequest request, ISaveRootProvider roots,
        IBackupWriter writer, string destinationRoot, IProgress<SaveOperationProgress>? progress, CancellationToken ct,
        RunningGamePolicy? policy = null)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (writer is null) throw new ArgumentNullException(nameof(writer));
        roots ??= NullSaveRootProvider.Instance;

        var reports = new List<SaveItemReport>();
        var gate = new SemaphoreSlim(MaxParallelFileOperations, MaxParallelFileOperations);
        try
        {
            string destination = SafeFullPath(destinationRoot);
            RunningGamePolicy effective = policy ?? ResolvePolicy(request);
            if (effective == RunningGamePolicy.Refuse && !string.IsNullOrWhiteSpace(request.RunningProcessName))
            {
                reports.Add(new SaveItemReport(request.RunningProcessName, SaveItemOutcome.Failed, 0,
                    "reason=policy-refuse;the running-game policy is Refuse, so nothing was backed up."));
                return new SaveOperationOutcome(false, reports, destination, null,
                    "Refused: this game's running process is " + request.RunningProcessName +
                    " and the running-game policy is Refuse. No data was touched.")
                { Status = SaveOperationStatus.Refused, Policy = effective };
            }

            IReadOnlyList<string> candidateRoots;
            try { candidateRoots = NormalizeRoots(roots.GetRoots(request)); }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                reports.Add(new SaveItemReport(destination, SaveItemOutcome.Failed, 0, "reason=root-enumeration;" + Flatten(ex)));
                return new SaveOperationOutcome(false, reports, destination, null,
                    "Backup failed: the save locations could not be read. " + Flatten(ex))
                { Status = SaveOperationStatus.Failed, Policy = effective };
            }

            if (candidateRoots.Count == 0)
                return NoSaveData(reports, destination, effective, DisplayName(request));

            var plans = new List<SnapshotPlan>();
            int rootIndex = 0;
            foreach (string root in candidateRoots)
            {
                IReadOnlyList<string> files;
                try { files = ListFiles(root); }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    reports.Add(new SaveItemReport(root, SaveItemOutcome.Failed, 0, "reason=root-unreadable;" + Flatten(ex)));
                    rootIndex++;
                    continue;
                }
                var present = new HashSet<string>(files.Select(file => RelativeKey(root, file)), StringComparer.OrdinalIgnoreCase);
                string rootKey = RootKey(rootIndex);
                rootIndex++;
                foreach (string file in files.OrderBy(item => RelativeKey(root, item), StringComparer.OrdinalIgnoreCase))
                {
                    string relative = RelativeKey(root, file);
                    string? settled = SettledCounterpart(relative, present);
                    if (settled is not null)
                    {
                        SaveOperationDiagnostics.RecordVolatileSkip();
                        reports.Add(new SaveItemReport(file, SaveItemOutcome.Skipped, 0,
                            "rel=" + ManifestRelative(rootKey, relative) + DetailSeparator + "reason=volatile-sibling" +
                            DetailSeparator + "settled=" + ManifestRelative(rootKey, settled)));
                        continue;
                    }
                    plans.Add(new SnapshotPlan(file, rootKey, relative));
                }
            }

            if (plans.Count == 0 && reports.All(item => item.Outcome != SaveItemOutcome.Failed))
                return NoSaveData(reports, destination, effective, DisplayName(request));

            string stamp = DateTime.Now.ToString(TimestampFormat, CultureInfo.InvariantCulture);
            string backupRoot = Path.Combine(destination, SanitizeSegment(DisplayName(request)) + "-" + stamp);
            string payloadRoot = Path.Combine(backupRoot, PayloadFolder);
            string staging = Path.Combine(backupRoot, StagingFolder);
            string unstable = Path.Combine(backupRoot, UnstableFolder);
            writer.EnsureDirectory(backupRoot);
            writer.EnsureDirectory(payloadRoot);
            writer.EnsureDirectory(staging);

            int total = plans.Count;
            int completed = 0;
            long totalBytes = 0;
            var tasks = new List<Task<SaveItemReport[]>>(total);
            foreach (SnapshotPlan plan in plans)
                tasks.Add(SnapshotWithProgressAsync(plan, staging, unstable, writer, gate, progress, total,
                    () => Interlocked.Increment(ref completed), counter => Interlocked.Add(ref totalBytes, counter), ct));

            SaveItemReport[][] results;
            try { results = await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                AbandonStaging(writer, staging, unstable);
                return Cancelled(reports, backupRoot, effective);
            }
            foreach (SaveItemReport[] group in results) reports.AddRange(group);
            reports.Sort(CompareReports);

            string manifest = Path.Combine(backupRoot, ManifestFileName);
            await File.WriteAllTextAsync(manifest, BuildManifest(reports), new UTF8Encoding(false), CancellationToken.None)
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(backupRoot, RootsMapFileName),
                BuildRootsMap(candidateRoots), new UTF8Encoding(false), CancellationToken.None).ConfigureAwait(false);

            int verified = reports.Count(item => item.Outcome == SaveItemOutcome.Verified);
            int skipped = reports.Count(item => item.Outcome == SaveItemOutcome.Skipped);
            int unstableCount = reports.Count(item => item.Outcome == SaveItemOutcome.Unstable);
            int failed = reports.Count(item => item.Outcome == SaveItemOutcome.Failed);
            long payloadBytes = reports.Where(item => item.Outcome == SaveItemOutcome.Verified).Sum(item => item.Bytes);
            bool success = unstableCount == 0 && failed == 0;
            string runningNote = string.IsNullOrWhiteSpace(request.RunningProcessName)
                ? string.Empty : " The snapshot was taken while the game was running (" + request.RunningProcessName + ").";
            string summary = (success ? "Backed up " : "Backup finished with problems: ")
                + verified + " file(s), " + payloadBytes + " byte(s) to " + backupRoot + "."
                + (skipped > 0 ? " " + skipped + " volatile file(s) were skipped in favour of their settled files." : string.Empty)
                + (unstableCount > 0 ? " " + unstableCount + " file(s) never settled and were NOT backed up." : string.Empty)
                + (failed > 0 ? " " + failed + " file(s) failed." : string.Empty)
                + runningNote;
            await File.WriteAllTextAsync(Path.Combine(backupRoot, SummaryFileName), summary, new UTF8Encoding(false),
                CancellationToken.None).ConfigureAwait(false);

            return new SaveOperationOutcome(success, reports, backupRoot, null, summary)
            {
                Status = success ? SaveOperationStatus.Completed
                    : failed > 0 ? SaveOperationStatus.Failed : SaveOperationStatus.Partial,
                Policy = effective
            };
        }
        finally { gate.Dispose(); }
    }

    internal static Task<SaveOperationOutcome> RestoreAsync(SaveOperationRequest request, ISaveRootProvider roots,
        IBackupWriter writer, string backupRoot, IProgress<SaveOperationProgress>? progress, CancellationToken ct,
        RunningGamePolicy? policy = null)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (writer is null) throw new ArgumentNullException(nameof(writer));
        return RestoreCoreAsync(request, roots ?? NullSaveRootProvider.Instance, writer, backupRoot, progress,
            policy ?? ResolvePolicy(request), ct);
    }

    private static async Task<SaveOperationOutcome> RestoreCoreAsync(SaveOperationRequest request, ISaveRootProvider roots,
        IBackupWriter writer, string backupRoot, IProgress<SaveOperationProgress>? progress, RunningGamePolicy policy,
        CancellationToken ct)
    {
        var reports = new List<SaveItemReport>();
        if (ct.IsCancellationRequested) return Cancelled(reports, backupRoot ?? string.Empty, policy);
        string root = SafeFullPath(backupRoot);
        if (!Directory.Exists(root))
        {
            reports.Add(new SaveItemReport(root, SaveItemOutcome.Failed, 0, "reason=backup-missing;the backup folder does not exist."));
            return new SaveOperationOutcome(false, reports, root, null, "Restore refused: the backup folder is missing.")
            { Status = SaveOperationStatus.Refused, Policy = policy };
        }
        if (policy == RunningGamePolicy.Refuse && !string.IsNullOrWhiteSpace(request.RunningProcessName))
        {
            reports.Add(new SaveItemReport(request.RunningProcessName, SaveItemOutcome.Failed, 0,
                "reason=policy-refuse;the running-game policy is Refuse, so nothing was restored."));
            return new SaveOperationOutcome(false, reports, root, null,
                "Refused: this game's running process is " + request.RunningProcessName +
                " and the running-game policy is Refuse. No live save was touched.")
            { Status = SaveOperationStatus.Refused, Policy = policy };
        }

        // Step 1: the backup must prove itself before any live save is touched.
        string manifestPath = Path.Combine(root, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            reports.Add(new SaveItemReport(manifestPath, SaveItemOutcome.Failed, 0,
                "reason=manifest-missing;this backup has no save manifest, so it cannot be verified."));
            return new SaveOperationOutcome(false, reports, root, null,
                "Restore refused: the backup has no verifiable manifest. The live saves were not touched.")
            { Status = SaveOperationStatus.Refused, Policy = policy };
        }
        ManifestVerificationResult verification = await VerifyCoreAsync(manifestPath, ct).ConfigureAwait(false);
        reports.AddRange(verification.Items);
        if (!verification.IsIntact)
            return new SaveOperationOutcome(false, reports, root, null,
                "Restore refused: the backup is not intact (" + verification.Summary + ") The live saves were not touched.")
            { Status = SaveOperationStatus.Refused, Policy = policy };

        // Step 2: prove this backup belongs to these save folders.
        IReadOnlyList<string> candidateRoots;
        try { candidateRoots = NormalizeRoots(roots.GetRoots(request)); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            reports.Add(new SaveItemReport(root, SaveItemOutcome.Failed, 0, "reason=root-enumeration;" + Flatten(ex)));
            return new SaveOperationOutcome(false, reports, root, null,
                "Restore refused: the save locations could not be read. " + Flatten(ex))
            { Status = SaveOperationStatus.Failed, Policy = policy };
        }
        if (candidateRoots.Count == 0)
        {
            reports.Add(new SaveItemReport(root, SaveItemOutcome.Skipped, 0,
                "reason=no-save-locations;no save folder is known for this game, so nothing was restored."));
            return new SaveOperationOutcome(true, reports, root, null,
                "NoSaveData: no save locations are known for " + DisplayName(request) + ", so nothing was restored.")
            { Status = SaveOperationStatus.NoSaveData, Policy = policy };
        }
        string? mapProblem = RootsMapProblem(root, candidateRoots);
        if (mapProblem is not null)
        {
            reports.Add(new SaveItemReport(Path.Combine(root, RootsMapFileName), SaveItemOutcome.Failed, 0,
                "reason=roots-map;" + mapProblem));
            return new SaveOperationOutcome(false, reports, root, null,
                "Restore refused: " + mapProblem + " The live saves were not touched.")
            { Status = SaveOperationStatus.Refused, Policy = policy };
        }

        // Step 3: quarantine, write, verify, and roll back on any failure.
        var payload = verification.Items.Where(item => item.Outcome == SaveItemOutcome.Verified).ToList();
        string stamp = DateTime.Now.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        string quarantineRoot = Path.Combine(root, QuarantinePrefix + stamp);
        string? usedQuarantine = null;
        int completed = 0;
        int totalFiles = payload.Count;
        for (int index = 0; index < candidateRoots.Count; index++)
        {
            if (ct.IsCancellationRequested) return Cancelled(reports, root, policy, usedQuarantine);
            string live = candidateRoots[index];
            string key = RootKey(index);
            var files = payload.Where(item => RootKeyOf(RelativeOf(item.Path)) == key).ToList();
            // Never quarantine a live save the backup has nothing to replace it with.
            if (files.Count == 0) continue;
            RestoreRootOutcome outcome = await RestoreRootAsync(live, key, files, root, quarantineRoot, writer, progress,
                totalFiles, () => Interlocked.Increment(ref completed), ct).ConfigureAwait(false);
            reports.AddRange(outcome.Reports);
            if (outcome.UsedQuarantine) usedQuarantine = quarantineRoot;
            if (outcome.Cancelled) return Cancelled(reports, root, policy, usedQuarantine);
        }

        reports.Sort(CompareReports);
        int quarantined = reports.Count(item => item.Outcome == SaveItemOutcome.Quarantined);
        int rolledBack = reports.Count(item => item.Outcome == SaveItemOutcome.RolledBack);
        int restored = reports.Count(item => item.Outcome == SaveItemOutcome.Verified);
        int failed = reports.Count(item => item.Outcome == SaveItemOutcome.Failed);
        bool success = failed == 0 && rolledBack == 0;
        string summary = (success ? "Restored " : "Restore finished with problems: ")
            + restored + " file(s) verified back into " + string.Join("; ", candidateRoots) + "."
            + (quarantined > 0 ? " Your previous saves were moved aside to " + quarantineRoot + "." : string.Empty)
            + (rolledBack > 0 ? " " + rolledBack + " file(s) were rolled back from quarantine." : string.Empty)
            + (failed > 0 ? " " + failed + " file(s) failed." : string.Empty);
        return new SaveOperationOutcome(success, reports, root, usedQuarantine, summary)
        {
            Status = success ? SaveOperationStatus.Completed
                : rolledBack > 0 ? SaveOperationStatus.RolledBack : SaveOperationStatus.Failed,
            Policy = policy
        };
    }

    // An aborted snapshot must not leave half-written scratch behind. The torn
    // attempts are parked as evidence rather than deleted, and the scratch folder
    // itself is removed because the coordinator created it.
    private static void AbandonStaging(IBackupWriter writer, string staging, string unstable)
    {
        try
        {
            if (!Directory.Exists(staging)) return;
            string[] leftovers = Directory.GetFileSystemEntries(staging);
            if (leftovers.Length == 0) { writer.DeletePath(staging); return; }
            string parked = Path.Combine(unstable, "cancelled-" + Guid.NewGuid().ToString("N"));
            writer.EnsureDirectory(unstable);
            writer.EnsureDirectory(parked);
            foreach (string leftover in leftovers)
                writer.MovePath(leftover, Path.Combine(parked, Path.GetFileName(leftover)));
            writer.DeletePath(staging);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed record RestoreRootOutcome(IReadOnlyList<SaveItemReport> Reports, bool UsedQuarantine, bool Cancelled);

    private static async Task<RestoreRootOutcome> RestoreRootAsync(string live, string rootKey,
        IReadOnlyList<SaveItemReport> payloadFiles, string backupRoot, string quarantineRoot, IBackupWriter writer,
        IProgress<SaveOperationProgress>? progress, int totalFiles, Action bump, CancellationToken ct)
    {
        var reports = new List<SaveItemReport>();
        string quarantined = Path.Combine(quarantineRoot, LiveFolder, rootKey);
        string staging = Path.Combine(quarantineRoot, RestoreStagingFolder, rootKey);
        string abandoned = Path.Combine(quarantineRoot, AbandonedFolder, rootKey + "-" + Guid.NewGuid().ToString("N"));
        bool usedQuarantine = false;
        bool hadLive = false;
        IReadOnlyDictionary<string, string> previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(live))
            {
                hadLive = true;
                previous = await SnapshotStateAsync(live, writer, ct).ConfigureAwait(false);
                writer.EnsureDirectory(Path.Combine(quarantineRoot, LiveFolder));
                writer.MovePath(live, quarantined);
                usedQuarantine = true;
                reports.Add(new SaveItemReport(live, SaveItemOutcome.Quarantined, previous.Count,
                    "movedTo=" + quarantined + DetailSeparator + "reason=restore-swap"));
            }
            writer.EnsureDirectory(staging);

            // Write into staging and verify it there. The live folder can only
            // ever receive a fully verified tree, in one move at the very end.
            foreach (SaveItemReport item in payloadFiles)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                string manifestRelative = RelativeOf(item.Path);
                string source = Path.Combine(backupRoot, manifestRelative);
                string target = Path.Combine(staging, WithinRoot(manifestRelative));
                string? targetParent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(targetParent)) writer.EnsureDirectory(targetParent);
                await writer.CopyFileAsync(source, target, ct).ConfigureAwait(false);
                string hash = await writer.HashFileAsync(target, ct).ConfigureAwait(false);
                string expected = HashOf(item) ?? string.Empty;
                if (expected.Length == 0 || !hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    SaveOperationDiagnostics.RecordInconsistentFile();
                    throw new IOException("A restored file does not match its manifest hash: " + target);
                }
                reports.Add(new SaveItemReport(Path.Combine(live, WithinRoot(manifestRelative)),
                    SaveItemOutcome.Verified, item.Bytes, "rel=" + manifestRelative + DetailSeparator + "sha256=" + hash));
                bump();
                progress?.Report(new SaveOperationProgress("restore", target, totalFiles, totalFiles, item.Bytes));
            }

            string? parent = Path.GetDirectoryName(live);
            if (!string.IsNullOrEmpty(parent)) writer.EnsureDirectory(parent);
            if (Directory.Exists(live)) writer.MovePath(live, abandoned);
            writer.MovePath(staging, live);
            if (Directory.Exists(quarantineRoot) && !Directory.EnumerateFileSystemEntries(quarantineRoot).Any())
                writer.DeletePath(quarantineRoot); // only ever our own empty scratch folder
            return new RestoreRootOutcome(reports, usedQuarantine, false);
        }
        catch (OperationCanceledException)
        {
            await RollbackAsync(live, quarantined, staging, abandoned, hadLive, usedQuarantine, previous, writer, reports, "cancelled")
                .ConfigureAwait(false);
            return new RestoreRootOutcome(reports, usedQuarantine, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
            await RollbackAsync(live, quarantined, staging, abandoned, hadLive, usedQuarantine, previous, writer, reports,
                Flatten(ex)).ConfigureAwait(false);
            return new RestoreRootOutcome(reports, usedQuarantine, false);
        }
    }

    private static async Task RollbackAsync(string live, string quarantined, string staging, string abandoned,
        bool hadLive, bool usedQuarantine, IReadOnlyDictionary<string, string> previous, IBackupWriter writer,
        List<SaveItemReport> reports, string reason)
    {
        // Nothing the user owns is ever deleted. Our own half-written tree is
        // parked aside as evidence and the previous saves are moved back.
        if (Directory.Exists(live)) writer.MovePath(live, abandoned);
        if (Directory.Exists(staging)) writer.MovePath(staging, abandoned + "-staging");
        if (usedQuarantine && Directory.Exists(quarantined))
        {
            writer.MovePath(quarantined, live);
            IReadOnlyDictionary<string, string> now = await SnapshotStateAsync(live, writer, CancellationToken.None)
                .ConfigureAwait(false);
            bool match = previous.Count == now.Count && previous.All(pair => now.TryGetValue(pair.Key, out string? hash)
                && string.Equals(hash, pair.Value, StringComparison.OrdinalIgnoreCase));
            reports.Add(new SaveItemReport(live, match ? SaveItemOutcome.RolledBack : SaveItemOutcome.Failed, now.Count,
                "reason=" + reason + DetailSeparator + "restoredFrom=" + quarantined + DetailSeparator
                + (match ? "verified=true" : "verified=false")));
        }
        else if (hadLive)
        {
            reports.Add(new SaveItemReport(live, SaveItemOutcome.Failed, 0,
                "reason=" + reason + DetailSeparator
                + "rollback=unavailable;the previous saves could not be moved back into place."));
        }
        else
        {
            reports.Add(new SaveItemReport(live, SaveItemOutcome.RolledBack, 0,
                "reason=" + reason + DetailSeparator + "rollback=verified;there was no previous save to move back."));
        }
        if (Directory.Exists(abandoned))
            reports.Add(new SaveItemReport(abandoned, SaveItemOutcome.Skipped, 0,
                "reason=preserved-incomplete-attempt;nothing was deleted; the incomplete attempt was kept here."));
    }

    private static async Task<IReadOnlyDictionary<string, string>> SnapshotStateAsync(string directory, IBackupWriter writer,
        CancellationToken ct)
    {
        var state = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directory)) return state;
        IReadOnlyList<string> files;
        try { files = ListFiles(directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return state; }
        foreach (string file in files.Take(MaxVerifiedRollbackFiles))
        {
            string key = RelativeKey(directory, file);
            try { state[key] = await writer.HashFileAsync(file, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { state[key] = "unreadable:" + ex.GetType().Name; }
        }
        return state;
    }

    private sealed record SnapshotPlan(string Path, string RootKey, string Relative);

    private static async Task<SaveItemReport[]> SnapshotWithProgressAsync(SnapshotPlan plan, string staging, string unstable,
        IBackupWriter writer, SemaphoreSlim gate, IProgress<SaveOperationProgress>? progress, int total, Action bump,
        Action<long> addBytes, CancellationToken ct)
    {
        var reports = new List<SaveItemReport>();
        string manifestRelative = ManifestRelative(plan.RootKey, plan.Relative);
        string payload = Path.Combine(staging, "..", PayloadFolder, plan.RootKey, plan.Relative);
        payload = SafeFullPath(payload);
        string reason = string.Empty;
        for (int attempt = 1; attempt <= MaxSnapshotAttempts; attempt++)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            string token = Guid.NewGuid().ToString("N");
            string partial = Path.Combine(staging, token + ".partial");
            long beforeLength;
            DateTime beforeWrite;
            try
            {
                var info = new FileInfo(plan.Path);
                if (!info.Exists) return new[] { Unstable(payload, manifestRelative, attempt, "the file vanished during the snapshot") };
                beforeLength = info.Length;
                beforeWrite = info.LastWriteTimeUtc;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new[] { Failed(payload, manifestRelative, attempt, Flatten(ex)) };
            }

            string hash;
            try
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await writer.CopyFileAsync(plan.Path, partial, ct).ConfigureAwait(false);
                    hash = await writer.HashFileAsync(partial, ct).ConfigureAwait(false);
                }
                finally { gate.Release(); }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                or NotSupportedException or System.Security.SecurityException)
            {
                return new[] { Failed(payload, manifestRelative, attempt, Flatten(ex)) };
            }

            bool stable = true;
            bool fullHash = beforeLength <= FullHashThresholdBytes;
            try
            {
                var after = new FileInfo(plan.Path);
                if (!after.Exists) { stable = false; reason = "the file vanished while it was being read"; }
                else if (after.Length != beforeLength)
                { stable = false; reason = "the size changed from " + beforeLength + " to " + after.Length + " bytes"; }
                else if (after.LastWriteTimeUtc != beforeWrite)
                { stable = false; reason = "the last-write time changed while it was being read"; }
                else if (fullHash)
                {
                    string second;
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try { second = await writer.HashFileAsync(plan.Path, ct).ConfigureAwait(false); }
                    finally { gate.Release(); }
                    if (!second.Equals(hash, StringComparison.OrdinalIgnoreCase))
                    { stable = false; reason = "the content changed while it was being read"; }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                stable = false;
                reason = "the file could not be re-read: " + Flatten(ex);
            }

            if (stable)
            {
                try
                {
                    string? parent = Path.GetDirectoryName(payload);
                    if (!string.IsNullOrEmpty(parent)) writer.EnsureDirectory(parent);
                    writer.MovePath(partial, payload);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                    or NotSupportedException or System.Security.SecurityException)
                {
                    return new[] { Failed(payload, manifestRelative, attempt, Flatten(ex)) };
                }
                string detail = "rel=" + manifestRelative + DetailSeparator + "sha256=" + hash + DetailSeparator
                    + "attempts=" + attempt + DetailSeparator + "stable=true" + DetailSeparator
                    + "verified=" + (fullHash ? "sha256" : "size+mtime");
                var copy = new SaveItemReport(payload, SaveItemOutcome.Copied, beforeLength, detail);
                reports.Add(copy);
                reports.Add(copy with { Outcome = SaveItemOutcome.Verified });
                addBytes(beforeLength);
                bump();
                progress?.Report(new SaveOperationProgress("backup", payload, total, total, beforeLength));
                return reports.ToArray();
            }

            // Park the torn attempt as evidence instead of deleting or trusting it.
            try
            {
                writer.EnsureDirectory(unstable);
                writer.MovePath(partial, Path.Combine(unstable, token + ".partial"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            SaveOperationDiagnostics.RecordSnapshotRetry();
            try { await Task.Delay(SnapshotRetryDelayMilliseconds, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            if (attempt == MaxSnapshotAttempts)
                return new[] { Unstable(payload, manifestRelative, attempt, reason) };
        }
        return reports.ToArray();
    }

    private static SaveItemReport Unstable(string payload, string manifestRelative, int attempts, string reason)
    {
        SaveOperationDiagnostics.RecordInconsistentFile();
        return new SaveItemReport(payload, SaveItemOutcome.Unstable, 0,
            "rel=" + manifestRelative + DetailSeparator + "attempts=" + attempts + DetailSeparator + "reason=" + reason
            + DetailSeparator + "stable=false;this file was NOT backed up because the game kept changing it.");
    }

    private static SaveItemReport Failed(string payload, string manifestRelative, int attempts, string reason) =>
        new SaveItemReport(payload, SaveItemOutcome.Failed, 0,
            "rel=" + manifestRelative + DetailSeparator + "attempts=" + attempts + DetailSeparator + "reason=" + reason);

    internal static string BuildManifest(IEnumerable<SaveItemReport> items)
    {
        var entries = new Dictionary<string, (long Bytes, string Hash)>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (SaveItemReport item in items ?? Enumerable.Empty<SaveItemReport>())
        {
            if (item.Outcome != SaveItemOutcome.Verified && item.Outcome != SaveItemOutcome.Copied) continue;
            string? relative = Value(item.Detail, "rel");
            string? hash = HashOf(item);
            if (string.IsNullOrWhiteSpace(relative) || string.IsNullOrWhiteSpace(hash)) continue;
            if (entries.ContainsKey(relative!)) continue; // Copied and Verified describe one payload file
            entries[relative!] = (item.Bytes, hash!.ToUpperInvariant());
            total += item.Bytes;
        }
        var lines = new List<string> { ManifestHeader };
        // Deterministic ordering: ordinal-ignore-case on the manifest path, with a
        // plain ordinal tie-break so case-only duplicates still order stably.
        foreach (KeyValuePair<string, (long Bytes, string Hash)> entry in entries
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Key, StringComparer.Ordinal))
            lines.Add("FILE\t" + entry.Key + "\t" + entry.Value.Bytes.ToString(CultureInfo.InvariantCulture)
                + "\t" + entry.Value.Hash);
        lines.Add("END\t" + entries.Count.ToString(CultureInfo.InvariantCulture) + "\t" + total.ToString(CultureInfo.InvariantCulture));
        return string.Join("\n", lines) + "\n";
    }

    internal static Task<ManifestVerificationResult> VerifyAsync(string manifestPath, CancellationToken ct = default) =>
        VerifyCoreAsync(manifestPath, ct);

    private static async Task<ManifestVerificationResult> VerifyCoreAsync(string manifestPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var reports = new List<SaveItemReport>();
        if (!File.Exists(manifestPath))
        {
            reports.Add(new SaveItemReport(manifestPath, SaveItemOutcome.Failed, 0, "reason=manifest-missing"));
            return new ManifestVerificationResult(false, reports, "the manifest is missing.");
        }
        string backupRoot;
        try { backupRoot = SafeFullPath(Path.GetDirectoryName(manifestPath) ?? string.Empty); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            reports.Add(new SaveItemReport(manifestPath, SaveItemOutcome.Failed, 0, "reason=bad-manifest-path;" + Flatten(ex)));
            return new ManifestVerificationResult(false, reports, "the manifest path is unusable.");
        }
        string prefix = backupRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string[] lines;
        try { lines = await File.ReadAllLinesAsync(manifestPath, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reports.Add(new SaveItemReport(manifestPath, SaveItemOutcome.Failed, 0, "reason=manifest-unreadable;" + Flatten(ex)));
            return new ManifestVerificationResult(false, reports, "the manifest could not be read.");
        }
        if (lines.Length == 0 || !lines[0].StartsWith(ManifestHeader, StringComparison.Ordinal))
        {
            reports.Add(new SaveItemReport(manifestPath, SaveItemOutcome.Failed, 0, "reason=manifest-format"));
            return new ManifestVerificationResult(false, reports, "the manifest is not a recognised save manifest.");
        }

        int matched = 0;
        int bad = 0;
        foreach (string raw in lines)
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || !line.StartsWith("FILE\t", StringComparison.Ordinal)) continue;
            string[] parts = line.Split('\t');
            if (parts.Length != 4)
            {
                bad++;
                reports.Add(new SaveItemReport(manifestPath, SaveItemOutcome.Failed, 0, "reason=manifest-line;a manifest entry is malformed."));
                continue;
            }
            string relative = parts[1];
            long bytes = long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : -1;
            string expected = parts[3];
            string full;
            try { full = SafeFullPath(Path.Combine(backupRoot, relative)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                bad++;
                reports.Add(new SaveItemReport(backupRoot, SaveItemOutcome.Failed, 0,
                    "reason=manifest-path;" + relative + " is not a usable manifest path."));
                continue;
            }
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                bad++;
                reports.Add(new SaveItemReport(full, SaveItemOutcome.Failed, 0,
                    "reason=manifest-escape;the manifest points outside the backup folder."));
                continue;
            }
            if (bytes < 0 || !File.Exists(full))
            {
                bad++;
                reports.Add(new SaveItemReport(full, SaveItemOutcome.Failed, 0,
                    "reason=manifest-missing-file;the file named by the manifest is missing."));
                continue;
            }
            if (new FileInfo(full).Length != bytes)
            {
                bad++;
                SaveOperationDiagnostics.RecordInconsistentFile();
                reports.Add(new SaveItemReport(full, SaveItemOutcome.Failed, bytes,
                    "reason=size-mismatch;expected " + bytes + " bytes."));
                continue;
            }
            string actual;
            try
            {
                using var stream = new FileStream(full, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, FileSystemBackupWriter.BufferSize, useAsync: false);
                actual = Convert.ToHexString(SHA256.HashData(stream));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                bad++;
                reports.Add(new SaveItemReport(full, SaveItemOutcome.Failed, bytes, "reason=unreadable;" + Flatten(ex)));
                continue;
            }
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                bad++;
                SaveOperationDiagnostics.RecordInconsistentFile();
                reports.Add(new SaveItemReport(full, SaveItemOutcome.Failed, bytes,
                    "reason=hash-mismatch;the file no longer matches its manifest hash."));
                continue;
            }
            matched++;
            reports.Add(new SaveItemReport(full, SaveItemOutcome.Verified, bytes,
                "rel=" + relative + DetailSeparator + "sha256=" + actual));
        }
        return bad == 0
            ? new ManifestVerificationResult(true, reports, matched + " file(s) match the manifest.")
            : new ManifestVerificationResult(false, reports, bad + " manifest entries do not match the backup.");
    }

    internal static string BuildRootsMap(IReadOnlyList<string> roots)
    {
        var lines = new List<string> { RootsMapHeader };
        for (int index = 0; index < roots.Count; index++)
            lines.Add("ROOT\t" + RootKey(index) + "\t" + SafeFullPath(roots[index]));
        return string.Join("\n", lines) + "\n";
    }

    private static string? RootsMapProblem(string backupRoot, IReadOnlyList<string> roots)
    {
        string mapPath = Path.Combine(backupRoot, RootsMapFileName);
        if (!File.Exists(mapPath)) return "This backup does not record which save folders it came from.";
        string[] lines;
        try { lines = File.ReadAllLines(mapPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return "The backup's save-folder record could not be read: " + ex.Message; }
        if (lines.Length == 0 || !lines[0].StartsWith(RootsMapHeader, StringComparison.Ordinal))
            return "The backup's save-folder record is not recognisable.";
        var recorded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in lines)
        {
            string[] parts = raw.TrimEnd('\r').Split('\t');
            if (parts.Length == 3 && parts[0] == "ROOT") recorded[parts[1]] = parts[2];
        }
        for (int index = 0; index < roots.Count; index++)
        {
            string key = RootKey(index);
            if (!recorded.TryGetValue(key, out string? value))
                return "The backup does not describe save folder " + (index + 1) + ", so it cannot be matched to this game's saves.";
            string actual = SafeFullPath(roots[index]);
            string expected;
            try { expected = SafeFullPath(value); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            { return "The backup records an unusable save folder."; }
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                return "The backup was taken from " + expected + ", but this game's save folder is now " + actual + ".";
        }
        return null;
    }

    internal static IReadOnlyList<string> NormalizeRoots(IReadOnlyList<string>? roots)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? candidate in roots ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            string full;
            try { full = SafeFullPath(candidate); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { continue; }
            if (!Directory.Exists(full)) continue;
            if (seen.Add(full)) result.Add(full);
        }
        return result;
    }

    internal static IReadOnlyList<string> ListFiles(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.CaseInsensitive
        };
        return Directory.EnumerateFiles(root, "*", options).ToList();
    }

    // A .tmp/.bak/.new/~ sibling the game is mid-write into is skipped in favour of
    // the settled file beside it. With no settled counterpart it is kept, because
    // skipping it would lose data.
    internal static string? SettledCounterpart(string relative, IReadOnlySet<string> present)
    {
        string directory = Path.GetDirectoryName(relative) ?? string.Empty;
        string name = Path.GetFileName(relative);
        string? trimmed = null;
        foreach (string suffix in VolatileSuffixes)
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            { trimmed = name[..^suffix.Length]; break; }
        }
        trimmed ??= name.StartsWith('~') && name.Length > 1 ? name[1..] : null;
        if (string.IsNullOrEmpty(trimmed)) return null;
        string candidate = directory.Length == 0 ? trimmed : Path.Combine(directory, trimmed);
        return present.Contains(candidate) ? candidate : null;
    }

    internal static string RootKey(int index) => "root-" + index.ToString("000", CultureInfo.InvariantCulture);

    internal static string ManifestRelative(string rootKey, string relative) =>
        PayloadFolder + "/" + rootKey + "/" + relative.Replace('\\', '/');

    private static string RelativeOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        string marker = PayloadFolder + Path.DirectorySeparatorChar;
        int at = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return at < 0 ? string.Empty : path[at..].Replace('\\', '/');
    }

    private static string RootKeyOf(string manifestRelative)
    {
        string prefix = PayloadFolder + "/";
        if (!manifestRelative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return string.Empty;
        string rest = manifestRelative[prefix.Length..];
        int slash = rest.IndexOf('/');
        return slash < 0 ? rest : rest[..slash];
    }

    private static string WithinRoot(string manifestRelative)
    {
        string key = RootKeyOf(manifestRelative);
        if (key.Length == 0) return manifestRelative;
        string prefix = PayloadFolder + "/" + key + "/";
        return manifestRelative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? manifestRelative[prefix.Length..].Replace('/', Path.DirectorySeparatorChar)
            : manifestRelative;
    }

    private static string? HashOf(SaveItemReport item) => Value(item.Detail, "sha256");

    private static string? Value(string? detail, string key)
    {
        if (string.IsNullOrEmpty(detail)) return null;
        foreach (string part in detail.Split(DetailSeparator, StringSplitOptions.None))
        {
            int at = part.IndexOf('=');
            if (at <= 0) continue;
            if (part.AsSpan(0, at).SequenceEqual(key.AsSpan())) return part[(at + 1)..].Trim();
        }
        return null;
    }

    private static int CompareReports(SaveItemReport left, SaveItemReport right)
    {
        int byPath = string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
        return byPath != 0 ? byPath : left.Outcome.CompareTo(right.Outcome);
    }

    private static string RelativeKey(string root, string file)
    {
        string relative = Path.GetRelativePath(root, file);
        return relative.Length == 0 ? Path.GetFileName(file) : relative;
    }

    private static SaveOperationOutcome NoSaveData(IReadOnlyList<SaveItemReport> reports, string destination,
        RunningGamePolicy policy, string game) =>
        new SaveOperationOutcome(true, reports, destination, null,
            "NoSaveData: no save data was found for " + game + ".")
        { Status = SaveOperationStatus.NoSaveData, Policy = policy };

    private static SaveOperationOutcome Cancelled(IReadOnlyList<SaveItemReport> reports, string backupRoot,
        RunningGamePolicy policy, string? quarantine = null) =>
        new SaveOperationOutcome(false, reports, backupRoot, quarantine,
            "Cancelled: the save operation was cancelled. No half-written file was left in a live save folder.")
        { Status = SaveOperationStatus.Cancelled, Policy = policy };

    private static string DisplayName(SaveOperationRequest request) =>
        string.IsNullOrWhiteSpace(request.GameName) ? request.GameId : request.GameName;

    private static string Flatten(Exception exception)
    {
        var text = new StringBuilder();
        for (Exception? current = exception; current is not null && text.Length < 400; current = current.InnerException)
        {
            if (text.Length > 0) text.Append(" <- ");
            text.Append(current.GetType().Name).Append(": ").Append(
                (current.Message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim());
        }
        return text.ToString();
    }

    internal static string SafeFullPath(string? path)
    {
        string trimmed = (path ?? string.Empty).Trim().Trim('"');
        if (trimmed.Length == 0) throw new ArgumentException("A folder path is required.", nameof(path));
        return Path.GetFullPath(trimmed);
    }

    private static string SanitizeSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char character in value)
            builder.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), character) >= 0 ? '_' : character);
        string result = builder.ToString().Trim().Trim('.');
        if (result.Length == 0) result = "game";
        return result.Length > 48 ? result[..48] : result;
    }
}
