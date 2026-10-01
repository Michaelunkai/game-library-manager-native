using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

internal enum GameRemovalTargetKind
{
    InstallFolder,
    VersionedInstallFolder,
    StagingFolder,
    LockFile,
    LocalFolder,
    InstallationFolder,
    LaunchFile,
    DockerContainer,
    DockerVolume
}

internal enum GameRemovalDecision
{
    Approved,
    Rejected
}

internal enum GameRemovalItemStatus
{
    Deleted,
    Skipped,
    Failed
}

internal sealed record GameRemovalTarget(
    string Path,
    GameRemovalTargetKind Kind,
    bool IsDirectory,
    long SizeBytes,
    GameRemovalDecision Decision,
    string Reason);

// Everything the engine needs is supplied explicitly so it never has to guess
// a root. The manager fills this from UserState; tests fill it directly.
internal sealed record GameRemovalRequest
{
    public string GameId { get; init; } = "";
    public string GameName { get; init; } = "";
    public string MountPath { get; init; } = "";
    public IReadOnlyList<string> LocalFolders { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> InstallationFolders { get; init; } = Array.Empty<string>();
    public string? LaunchPath { get; init; }
    // Every candidate filesystem path must be a strict descendant of one of
    // these roots or it is rejected, never deleted.
    public IReadOnlyList<string> AllowedRoots { get; init; } = Array.Empty<string>();
    public bool IncludeDockerLeftovers { get; init; } = true;
    public string? DockerContext { get; init; }
    public string? DockerHost { get; init; }
}

internal sealed record GameRemovalPlan(
    string GameId,
    string GameName,
    IReadOnlyList<GameRemovalTarget> Targets,
    IReadOnlyList<string> AllowedRoots,
    string? DockerContext = null,
    string? DockerHost = null)
{
    public IReadOnlyList<GameRemovalTarget> ApprovedTargets =>
        Targets.Where(target => target.Decision == GameRemovalDecision.Approved).ToArray();
    public bool HasApprovedTargets => Targets.Any(target => target.Decision == GameRemovalDecision.Approved);
}

internal sealed record GameRemovalProgress(int Index, int Total, string Path, string Action);

internal sealed record GameRemovalOutcome(
    string Path,
    GameRemovalTargetKind Kind,
    GameRemovalItemStatus Status,
    string? Reason);

internal sealed record GameRemovalResult(IReadOnlyList<GameRemovalOutcome> Outcomes)
{
    public int DeletedCount => Outcomes.Count(outcome => outcome.Status == GameRemovalItemStatus.Deleted);
    public int SkippedCount => Outcomes.Count(outcome => outcome.Status == GameRemovalItemStatus.Skipped);
    public int FailedCount => Outcomes.Count(outcome => outcome.Status == GameRemovalItemStatus.Failed);
    public bool Succeeded => FailedCount == 0;
}

/// <summary>
/// Produces a deterministic removal plan for a single game and executes it safely.
/// Safety rules: every filesystem candidate must be a strict descendant of an
/// explicitly supplied root; drive roots, the roots themselves, ancestors of a
/// root and user profile folders are rejected; reparse points are rejected and
/// never traversed; a failure on one item never hides or stops its siblings.
/// </summary>
internal static class GameRemoval
{
    private const int MaxParallelDeletions = 4;
    private const int DockerProbeTimeoutMs = 2500;
    private const int DockerInspectTimeoutMs = 3000;
    private const int DockerDeleteTimeoutMs = 30000;
    private const int MaxMeasuredEntries = 1_000_000;

    public static GameRemovalPlan Plan(GameRemovalRequest request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        string gameId = (request.GameId ?? "").Trim();
        if (gameId.Length == 0) throw new ArgumentException("A game identity is required.", nameof(request));
        string gameName = string.IsNullOrWhiteSpace(request.GameName) ? gameId : request.GameName.Trim();
        IReadOnlyList<string> roots = NormalizeRoots(request.AllowedRoots);
        var targets = new List<GameRemovalTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddPath(string? candidate, GameRemovalTargetKind kind, bool isDirectory)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;
            string trimmed = candidate!.Trim();
            string full;
            try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed)); }
            catch (Exception ex) when (IsPathError(ex))
            {
                targets.Add(new GameRemovalTarget(trimmed, kind, isDirectory, 0, GameRemovalDecision.Rejected, "Not a valid filesystem path: " + ex.Message));
                return;
            }
            if (!seen.Add(full)) return;
            (GameRemovalDecision decision, string reason) = EvaluateSafety(full, roots);
            long size = 0;
            if (decision == GameRemovalDecision.Approved)
            {
                if (isDirectory && Directory.Exists(full)) size = MeasureDirectory(full);
                else if (!isDirectory && File.Exists(full)) size = SafeFileLength(full);
            }
            targets.Add(new GameRemovalTarget(full, kind, isDirectory, size, decision, reason));
        }

        if (!string.IsNullOrWhiteSpace(request.MountPath))
        {
            string mount = "";
            try { mount = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.MountPath.Trim())); }
            catch (Exception ex) when (IsPathError(ex)) { mount = ""; }
            if (mount.Length > 0)
            {
                bool dockerIdentity = !gameId.StartsWith("local:", StringComparison.Ordinal);
                string installFolderName = "";
                if (dockerIdentity)
                {
                    try { installFolderName = DockerScripts.InstallFolder(gameId); }
                    catch (ArgumentException) { installFolderName = ""; }
                }
                if (installFolderName.Length > 0)
                {
                    AddPath(Path.Combine(mount, installFolderName), GameRemovalTargetKind.InstallFolder, true);
                    foreach (string child in EnumerateDirectoriesSafe(mount))
                    {
                        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(child));
                        bool versioned = false;
                        try { versioned = DockerScripts.IsVersionedInstallFolder(gameId, name); }
                        catch (ArgumentException) { }
                        if (versioned) AddPath(child, GameRemovalTargetKind.VersionedInstallFolder, true);
                    }
                    // The install work key canonicalizes the destination that owns the
                    // per-operation staging folders.
                    string stagingRoot = Path.Combine(mount, DockerScripts.StagingDirectoryName);
                    string stagingPrefix = installFolderName + "-";
                    foreach (string child in EnumerateDirectoriesSafe(stagingRoot))
                    {
                        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(child));
                        if (name.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
                            AddPath(child, GameRemovalTargetKind.StagingFolder, true);
                    }
                    try
                    {
                        string workKey = DockerScripts.InstallWorkKey(mount, gameId);
                        string lockPath = DockerScripts.InstallLockPath(mount, gameId);
                        if (workKey.Length > 0 && File.Exists(lockPath))
                            AddPath(lockPath, GameRemovalTargetKind.LockFile, false);
                    }
                    catch (ArgumentException) { }
                }
            }
        }

        foreach (string folder in request.LocalFolders ?? Array.Empty<string>())
            AddPath(folder, GameRemovalTargetKind.LocalFolder, true);
        foreach (string folder in request.InstallationFolders ?? Array.Empty<string>())
            AddPath(folder, GameRemovalTargetKind.InstallationFolder, true);
        AddPath(request.LaunchPath, GameRemovalTargetKind.LaunchFile, false);

        if (request.IncludeDockerLeftovers && !gameId.StartsWith("local:", StringComparison.Ordinal))
            AddDockerTargets(request, gameId, targets, seen);

        // A file or folder already covered by an approved parent directory is
        // redundant and would race the parent deletion; drop it from the plan.
        var approvedDirectories = targets
            .Where(target => target.Decision == GameRemovalDecision.Approved && target.IsDirectory)
            .Select(target => target.Path)
            .ToList();
        if (approvedDirectories.Count > 0)
            targets.RemoveAll(target => target.Decision == GameRemovalDecision.Approved
                && approvedDirectories.Any(directory => IsStrictDescendant(target.Path, directory)));

        return new GameRemovalPlan(gameId, gameName, targets, roots, request.DockerContext, request.DockerHost);
    }

    public static string Describe(GameRemovalPlan plan)
    {
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        var text = new StringBuilder();
        text.Append("Permanently delete ").Append(plan.GameName).Append(" and all leftovers?");
        GameRemovalTarget[] approved = plan.Targets.Where(target => target.Decision == GameRemovalDecision.Approved).ToArray();
        GameRemovalTarget[] rejected = plan.Targets.Where(target => target.Decision == GameRemovalDecision.Rejected).ToArray();
        if (approved.Length == 0)
            text.Append(Environment.NewLine).Append("Nothing is eligible for deletion.");
        foreach (GameRemovalTarget target in approved)
        {
            text.Append(Environment.NewLine).Append("  - ").Append(Label(target.Kind)).Append(": ").Append(target.Path);
            if (target.SizeBytes > 0) text.Append(" (").Append(FormatSize(target.SizeBytes)).Append(')');
        }
        foreach (GameRemovalTarget target in rejected)
            text.Append(Environment.NewLine).Append("  - NOT DELETED (").Append(target.Reason).Append("): ").Append(target.Path);
        return text.ToString();
    }

    public static async Task<GameRemovalResult> ExecuteAsync(GameRemovalPlan plan, IProgress<GameRemovalProgress>? progress, CancellationToken ct)
    {
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        GameRemovalTarget[] approved = plan.ApprovedTargets.ToArray();
        if (approved.Length == 0) return new GameRemovalResult(Array.Empty<GameRemovalOutcome>());
        var outcomes = new GameRemovalOutcome[approved.Length];
        using var gate = new SemaphoreSlim(MaxParallelDeletions);
        int completed = 0;
        var workers = new List<Task>(approved.Length);
        for (int index = 0; index < approved.Length; index++)
        {
            int slot = index;
            GameRemovalTarget target = approved[slot];
            workers.Add(Task.Run(async () =>
            {
                GameRemovalOutcome outcome;
                try
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        outcome = DeleteTarget(plan, target);
                    }
                    finally { gate.Release(); }
                }
                catch (OperationCanceledException)
                {
                    outcome = new GameRemovalOutcome(target.Path, target.Kind, GameRemovalItemStatus.Skipped, "Cancelled");
                }
                catch (Exception ex)
                {
                    outcome = new GameRemovalOutcome(target.Path, target.Kind, GameRemovalItemStatus.Failed, ex.GetType().Name + ": " + ex.Message);
                }
                outcomes[slot] = outcome;
                int done = Interlocked.Increment(ref completed);
                try { progress?.Report(new GameRemovalProgress(done, approved.Length, target.Path, outcome.Status.ToString())); }
                catch { }
            }));
        }
        try { await Task.WhenAll(workers).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        return new GameRemovalResult(outcomes.ToArray());
    }

    private static GameRemovalOutcome DeleteTarget(GameRemovalPlan plan, GameRemovalTarget target)
    {
        if (target.Kind == GameRemovalTargetKind.DockerContainer || target.Kind == GameRemovalTargetKind.DockerVolume)
            return DeleteDockerTarget(plan, target);

        (GameRemovalDecision decision, string reason) = EvaluateSafety(target.Path, plan.AllowedRoots);
        if (decision != GameRemovalDecision.Approved)
            return new GameRemovalOutcome(target.Path, target.Kind, GameRemovalItemStatus.Skipped, "Refused: " + reason);
        if (IsReparsePoint(target.Path))
            return new GameRemovalOutcome(target.Path, target.Kind, GameRemovalItemStatus.Skipped, "Reparse point is never followed");

        bool directory = Directory.Exists(target.Path);
        bool file = File.Exists(target.Path);
        if (!directory && !file)
            return new GameRemovalOutcome(target.Path, target.Kind, GameRemovalItemStatus.Skipped, "AlreadyAbsent");
        if (directory)
        {
            if (TryDeleteDirectory(target.Path, out string? directoryError))
                return new GameRemovalOutcome(target.Path, target.Kind, GameRemovalItemStatus.Deleted, null);
            return new GameRemovalOutcome(target.Path, target.Kind, GameRemovalItemStatus.Failed, directoryError);
        }
        if (TryDeleteFile(target.Path, out string? fileError))
            return new GameRemovalOutcome(target.Path, target.Kind, GameRemovalItemStatus.Deleted, null);
        return new GameRemovalOutcome(target.Path, target.Kind, GameRemovalItemStatus.Failed, fileError);
    }

    private static GameRemovalOutcome DeleteDockerTarget(GameRemovalPlan plan, GameRemovalTarget target)
    {
        string[] arguments = target.Kind == GameRemovalTargetKind.DockerContainer
            ? new[] { "container", "rm", "--force", target.Path }
            : new[] { "volume", "rm", target.Path };
        DockerRun run = RunDocker(plan.DockerContext, plan.DockerHost, arguments, DockerDeleteTimeoutMs);
        if (!run.Ran)
            return new GameRemovalOutcome(target.Path, target.Kind, GameRemovalItemStatus.Skipped, "Docker unavailable: " + run.Error);
        if (run.ExitCode == 0)
            return new GameRemovalOutcome(target.Path, target.Kind, GameRemovalItemStatus.Deleted, null);
        string message = (run.StdErr + " " + run.StdOut).Trim();
        if (message.IndexOf("No such container", StringComparison.OrdinalIgnoreCase) >= 0
            || message.IndexOf("No such volume", StringComparison.OrdinalIgnoreCase) >= 0
            || message.IndexOf("no such", StringComparison.OrdinalIgnoreCase) >= 0)
            return new GameRemovalOutcome(target.Path, target.Kind, GameRemovalItemStatus.Skipped, "AlreadyAbsent");
        return new GameRemovalOutcome(target.Path, target.Kind, GameRemovalItemStatus.Failed, "Docker removal failed: " + message);
    }

    private static bool TryDeleteDirectory(string path, out string? error)
    {
        error = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (DirectoryNotFoundException) { return true; }
            catch (FileNotFoundException) { return true; }
            catch (IOException ex) when (IsMissingRace(ex)) { return true; }
            catch (Exception ex) when (attempt == 0 && IsRetryable(ex)) { ClearReadOnlyRecursive(path); }
            catch (Exception ex) when (IsFilesystemError(ex)) { error = DescribeError(ex); return false; }
            catch (Exception ex) { error = DescribeError(ex); return false; }
        }
        error ??= "Deletion failed after clearing read-only attributes";
        return false;
    }

    private static bool TryDeleteFile(string path, out string? error)
    {
        error = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            catch (IOException ex) when (IsMissingRace(ex)) { return true; }
            catch (Exception ex) when (attempt == 0 && IsRetryable(ex)) { ClearReadOnly(path); }
            catch (Exception ex) when (IsFilesystemError(ex)) { error = DescribeError(ex); return false; }
            catch (Exception ex) { error = DescribeError(ex); return false; }
        }
        error ??= "Deletion failed after clearing read-only attributes";
        return false;
    }

    private static void AddDockerTargets(GameRemovalRequest request, string gameId, List<GameRemovalTarget> targets, HashSet<string> seen)
    {
        var candidates = new List<(GameRemovalTargetKind Kind, string Name)>();
        void AddCandidate(GameRemovalTargetKind kind, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (candidates.Any(candidate => candidate.Kind == kind && string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))) return;
            candidates.Add((kind, name));
        }
        AddCandidate(GameRemovalTargetKind.DockerContainer, DockerScripts.ContainerName(gameId));
        if (!string.IsNullOrWhiteSpace(request.MountPath))
        {
            try { AddCandidate(GameRemovalTargetKind.DockerContainer, DockerScripts.ContainerNameForDestination(gameId, request.MountPath)); }
            catch (ArgumentException) { }
        }
        AddCandidate(GameRemovalTargetKind.DockerVolume, DockerScripts.ContainerName(gameId));

        DockerRun probe = RunDocker(request.DockerContext, request.DockerHost, new[] { "version", "--format", "{{.Server.Version}}" }, DockerProbeTimeoutMs);
        if (!probe.Ran || probe.ExitCode != 0) return;

        foreach ((GameRemovalTargetKind kind, string name) in candidates)
        {
            if (kind == GameRemovalTargetKind.DockerContainer)
            {
                DockerRun inspect = RunDocker(request.DockerContext, request.DockerHost,
                    new[] { "container", "inspect", name, "--format", DockerScripts.OwnedContainerInspectFormat }, DockerInspectTimeoutMs);
                if (!inspect.Ran || inspect.ExitCode != 0) continue;
                string line = LastLine(inspect.StdOut);
                int delimiter = line.IndexOf('|');
                string labels = delimiter >= 0 && delimiter < line.Length - 1 ? line[(delimiter + 1)..].Trim() : "";
                if (!DockerScripts.OwnershipMatches(DockerScripts.OwnershipFromLabelsJson(labels), gameId)) continue;
                if (!seen.Add("docker:container:" + name)) continue;
                targets.Add(new GameRemovalTarget(name, kind, false, 0, GameRemovalDecision.Approved, "Owned Docker container leftover"));
            }
            else
            {
                DockerRun inspect = RunDocker(request.DockerContext, request.DockerHost,
                    new[] { "volume", "inspect", name, "--format", "{{.Name}}" }, DockerInspectTimeoutMs);
                if (!inspect.Ran || inspect.ExitCode != 0 || string.IsNullOrWhiteSpace(inspect.StdOut)) continue;
                if (!seen.Add("docker:volume:" + name)) continue;
                targets.Add(new GameRemovalTarget(name, kind, false, 0, GameRemovalDecision.Approved, "Deterministic Docker volume leftover"));
            }
        }
    }

    private static (GameRemovalDecision Decision, string Reason) EvaluateSafety(string full, IReadOnlyList<string> roots)
    {
        if (full.Length == 0) return (GameRemovalDecision.Rejected, "Empty path");
        if (IsDevicePath(full)) return (GameRemovalDecision.Rejected, "Device namespace paths are not supported");
        if (HasWildcard(full)) return (GameRemovalDecision.Rejected, "Path contains wildcard characters");
        if (roots.Count == 0) return (GameRemovalDecision.Rejected, "No allowed deletion roots were supplied");
        if (IsDriveRoot(full)) return (GameRemovalDecision.Rejected, "The drive root is never deleted");
        if (IsUserProfileFolder(full)) return (GameRemovalDecision.Rejected, "A user profile folder is never deleted");
        if (!TryMatchRoot(full, roots, out string matchedRoot))
            return (GameRemovalDecision.Rejected, "Path is outside every allowed deletion root");
        if (CrossesReparsePoint(full, matchedRoot))
            return (GameRemovalDecision.Rejected, "Path crosses a reparse point (symlink or junction)");
        return (GameRemovalDecision.Approved, "Inside allowed root " + matchedRoot);
    }

    private static IReadOnlyList<string> NormalizeRoots(IReadOnlyList<string>? roots)
    {
        var list = new List<string>();
        if (roots == null) return list;
        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            try
            {
                string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Trim()));
                if (full.Length > 0 && !list.Contains(full, StringComparer.OrdinalIgnoreCase)) list.Add(full);
            }
            catch (Exception ex) when (IsPathError(ex)) { }
        }
        return list;
    }

    private static bool TryMatchRoot(string path, IReadOnlyList<string> roots, out string matchedRoot)
    {
        foreach (string root in roots)
        {
            if (path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                matchedRoot = root;
                return true;
            }
        }
        matchedRoot = "";
        return false;
    }

    private static bool IsStrictDescendant(string child, string parent)
    {
        if (string.Equals(child, parent, StringComparison.OrdinalIgnoreCase)) return false;
        return child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool CrossesReparsePoint(string path, string root)
    {
        string current = path;
        while (current.Length > root.Length)
        {
            if (IsReparsePoint(current)) return true;
            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent)) break;
            string trimmed = Path.TrimEndingDirectorySeparator(parent);
            if (string.Equals(trimmed, current, StringComparison.OrdinalIgnoreCase)) break;
            current = trimmed;
        }
        return false;
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (IsFilesystemError(ex) || IsPathError(ex)) { return false; }
    }

    private static bool IsDriveRoot(string path)
    {
        string? root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) return false;
        return string.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUserProfileFolder(string path)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile));
            if (string.Equals(path, normalized, StringComparison.OrdinalIgnoreCase)) return true;
            if (normalized.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        }
        Environment.SpecialFolder[] folders =
        {
            Environment.SpecialFolder.Desktop,
            Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolder.MyMusic,
            Environment.SpecialFolder.MyVideos,
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.Favorites,
            Environment.SpecialFolder.StartMenu,
            Environment.SpecialFolder.Programs
        };
        foreach (Environment.SpecialFolder folder in folders)
        {
            try
            {
                string value = Environment.GetFolderPath(folder);
                if (!string.IsNullOrWhiteSpace(value)
                    && string.Equals(path, Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch (Exception ex) when (IsPathError(ex)) { }
        }
        return false;
    }

    private static bool HasWildcard(string path) => path.IndexOfAny(new[] { '*', '?' }) >= 0;

    private static bool IsDevicePath(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal);

    private static long MeasureDirectory(string path)
    {
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(path);
        int visited = 0;
        while (pending.Count > 0 && visited < MaxMeasuredEntries)
        {
            string current = pending.Pop();
            visited++;
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(current); }
            catch (Exception ex) when (IsFilesystemError(ex) || IsPathError(ex)) { continue; }
            foreach (string entry in entries)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception ex) when (IsFilesystemError(ex) || IsPathError(ex)) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else total = SafeAdd(total, SafeFileLength(entry));
            }
        }
        return total;
    }

    private static long SafeFileLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception ex) when (IsFilesystemError(ex) || IsPathError(ex)) { return 0; }
    }

    private static long SafeAdd(long left, long right)
    {
        try { return checked(left + right); }
        catch (OverflowException) { return long.MaxValue; }
    }

    private static IReadOnlyList<string> EnumerateDirectoriesSafe(string path)
    {
        try { return Directory.GetDirectories(path); }
        catch (Exception ex) when (IsFilesystemError(ex) || IsPathError(ex)) { return Array.Empty<string>(); }
    }

    private static void ClearReadOnly(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
        catch (Exception ex) when (IsFilesystemError(ex) || IsPathError(ex)) { }
    }

    private static void ClearReadOnlyRecursive(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            // Never descend through a reparse point; only clear its own flag.
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0) return;
        }
        catch (Exception ex) when (IsFilesystemError(ex) || IsPathError(ex)) { return; }
        string[] entries;
        try { entries = Directory.GetFileSystemEntries(path); }
        catch (Exception ex) when (IsFilesystemError(ex) || IsPathError(ex)) { return; }
        foreach (string entry in entries)
        {
            FileAttributes entryAttributes;
            try { entryAttributes = File.GetAttributes(entry); }
            catch (Exception ex) when (IsFilesystemError(ex) || IsPathError(ex)) { continue; }
            if ((entryAttributes & FileAttributes.ReparsePoint) != 0)
            {
                if ((entryAttributes & FileAttributes.ReadOnly) != 0)
                {
                    try { File.SetAttributes(entry, entryAttributes & ~FileAttributes.ReadOnly); } catch { }
                }
                continue;
            }
            if ((entryAttributes & FileAttributes.Directory) != 0) ClearReadOnlyRecursive(entry);
            else if ((entryAttributes & FileAttributes.ReadOnly) != 0)
            {
                try { File.SetAttributes(entry, entryAttributes & ~FileAttributes.ReadOnly); } catch { }
            }
        }
    }

    private static string Label(GameRemovalTargetKind kind) => kind switch
    {
        GameRemovalTargetKind.InstallFolder => "Install folder",
        GameRemovalTargetKind.VersionedInstallFolder => "Versioned install folder",
        GameRemovalTargetKind.StagingFolder => "Install staging folder",
        GameRemovalTargetKind.LockFile => "Install lock file",
        GameRemovalTargetKind.LocalFolder => "Local game folder",
        GameRemovalTargetKind.InstallationFolder => "Registered installation folder",
        GameRemovalTargetKind.LaunchFile => "Saved launcher file",
        GameRemovalTargetKind.DockerContainer => "Docker container",
        GameRemovalTargetKind.DockerVolume => "Docker volume",
        _ => "Item"
    };

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value.ToString(unit == 0 ? "0" : "0.##", System.Globalization.CultureInfo.InvariantCulture) + " " + units[unit];
    }

    private static bool IsMissingRace(IOException ex)
    {
        int code = ex.HResult & 0xFFFF;
        return code == 2 || code == 3; // ERROR_FILE_NOT_FOUND / ERROR_PATH_NOT_FOUND
    }

    private static bool IsRetryable(Exception ex) => ex is UnauthorizedAccessException || ex is IOException;

    private static bool IsFilesystemError(Exception ex) =>
        ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is System.ComponentModel.Win32Exception;

    private static bool IsPathError(Exception ex) =>
        ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException;

    private static string DescribeError(Exception ex) => ex.GetType().Name + ": " + ex.Message;

    private static string LastLine(string text)
    {
        string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? "" : lines[^1].Trim();
    }

    private sealed record DockerRun(bool Ran, int ExitCode, string StdOut, string StdErr, string Error);

    private static DockerRun RunDocker(string? context, string? host, IReadOnlyList<string> arguments, int timeoutMs)
    {
        string executable;
        try { executable = DockerScripts.Executable; }
        catch (Exception ex) { return new DockerRun(false, -1, "", "", ex.Message); }
        if (string.IsNullOrWhiteSpace(executable)) return new DockerRun(false, -1, "", "", "Docker executable was not found");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (!string.IsNullOrWhiteSpace(host)) { start.ArgumentList.Add("--host"); start.ArgumentList.Add(host); }
        else if (!string.IsNullOrWhiteSpace(context)) { start.ArgumentList.Add("--context"); start.ArgumentList.Add(context); }
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using Process? process = Process.Start(start);
            if (process == null) return new DockerRun(false, -1, "", "", "Docker process could not be started");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(true); } catch { }
                return new DockerRun(false, -1, SafeResult(stdout), SafeResult(stderr), "Docker command timed out");
            }
            return new DockerRun(true, process.ExitCode, SafeResult(stdout), SafeResult(stderr), "");
        }
        catch (Exception ex)
        {
            return new DockerRun(false, -1, "", "", ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static string SafeResult(Task<string> task)
    {
        try { return task.GetAwaiter().GetResult() ?? ""; }
        catch { return ""; }
    }
}
