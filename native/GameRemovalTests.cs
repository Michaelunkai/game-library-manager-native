using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace GameLibrary.Native;

internal static class GameRemovalTests
{
    internal static void Run(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("A scratch root is required.");
        Directory.CreateDirectory(root);
        int created = 0, deleted = 0;

        // 1. A normal multi-file tree is fully deleted.
        {
            const string gameId = "normaltree";
            string installFolder = Path.Combine(root, DockerScripts.InstallFolder(gameId));
            Directory.CreateDirectory(Path.Combine(installFolder, "bin"));
            File.WriteAllText(Path.Combine(installFolder, "game.exe"), "exe");
            File.WriteAllText(Path.Combine(installFolder, "bin", "data.pak"), "data");
            created += 3;
            var request = new GameRemovalRequest
            {
                GameId = gameId,
                GameName = "Normal Tree",
                MountPath = root,
                AllowedRoots = new[] { root },
                IncludeDockerLeftovers = false
            };
            GameRemovalPlan plan = GameRemoval.Plan(request);
            Check(plan.ApprovedTargets.Any(t => t.Kind == GameRemovalTargetKind.InstallFolder && PathsEqual(t.Path, installFolder)),
                "The install folder of a normal tree was not approved for removal.");
            Check(GameRemoval.Describe(plan).Contains(installFolder, StringComparison.OrdinalIgnoreCase),
                "Describe did not name the install folder.");
            GameRemovalResult result = GameRemoval.ExecuteAsync(plan, null, default).GetAwaiter().GetResult();
            Check(!Directory.Exists(installFolder), "A normal multi-file tree was not fully deleted.");
            Check(result.FailedCount == 0, "Deleting a normal tree reported failures: " + FirstFailure(result));
            deleted += 3;
        }

        // 2. A path escaping the allow-listed roots is rejected and survives.
        {
            string outside = root + "-escape";
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
            created += 2;
            var request = new GameRemovalRequest
            {
                GameId = "escape",
                MountPath = "",
                LocalFolders = new[] { outside },
                AllowedRoots = new[] { root },
                IncludeDockerLeftovers = false
            };
            GameRemovalPlan plan = GameRemoval.Plan(request);
            GameRemovalTarget target = plan.Targets.First(t => PathsEqual(t.Path, outside));
            Check(target.Decision == GameRemovalDecision.Rejected, "A path escaping the allow-listed roots was not rejected.");
            GameRemoval.ExecuteAsync(plan, null, default).GetAwaiter().GetResult();
            Check(Directory.Exists(outside) && File.Exists(Path.Combine(outside, "keep.txt")),
                "A path escaping the allow-listed roots was deleted.");
            Directory.Delete(outside, true);
            deleted += 2;
        }

        // 3. The root itself and the drive root are rejected.
        {
            string driveRoot = Path.GetPathRoot(Path.GetFullPath(root)) ?? "";
            var request = new GameRemovalRequest
            {
                GameId = "rootsafety",
                MountPath = "",
                LocalFolders = new[] { root },
                InstallationFolders = new[] { driveRoot },
                AllowedRoots = new[] { root },
                IncludeDockerLeftovers = false
            };
            GameRemovalPlan plan = GameRemoval.Plan(request);
            Check(plan.Targets.Where(t => PathsEqual(t.Path, root)).All(t => t.Decision == GameRemovalDecision.Rejected),
                "The allowed root itself was not rejected.");
            Check(plan.Targets.Where(t => PathsEqual(t.Path, driveRoot)).All(t => t.Decision == GameRemovalDecision.Rejected),
                "The drive root was not rejected.");
            GameRemoval.ExecuteAsync(plan, null, default).GetAwaiter().GetResult();
            Check(Directory.Exists(root), "The scratch root was deleted.");
        }

        // 4. A reparse point (junction) is rejected, not followed.
        {
            string outside = root + "-reparse-target";
            string link = Path.Combine(root, "junction-link");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "sentinel.txt"), "sentinel");
            created += 2;
            if (!TryCreateJunction(link, outside))
                throw new InvalidOperationException("Could not create a junction to exercise reparse-point rejection.");
            try
            {
                var request = new GameRemovalRequest
                {
                    GameId = "reparse",
                    MountPath = "",
                    LocalFolders = new[] { link },
                    AllowedRoots = new[] { root },
                    IncludeDockerLeftovers = false
                };
                GameRemovalPlan plan = GameRemoval.Plan(request);
                GameRemovalTarget target = plan.Targets.First(t => PathsEqual(t.Path, link));
                Check(target.Decision == GameRemovalDecision.Rejected, "A junction target was not rejected.");
                GameRemoval.ExecuteAsync(plan, null, default).GetAwaiter().GetResult();
                Check(File.Exists(Path.Combine(outside, "sentinel.txt")),
                    "A junction target was followed and its destination deleted.");
            }
            finally
            {
                TryDeleteJunction(link);
                Directory.Delete(outside, true);
                deleted += 2;
            }
        }

        // 5. A second run over the same plan reports Skipped(AlreadyAbsent).
        {
            string folder = Path.Combine(root, "resume-local");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "a.txt"), "a");
            created += 2;
            var request = new GameRemovalRequest
            {
                GameId = "resume",
                MountPath = "",
                LocalFolders = new[] { folder },
                AllowedRoots = new[] { root },
                IncludeDockerLeftovers = false
            };
            GameRemovalPlan plan = GameRemoval.Plan(request);
            GameRemovalResult first = GameRemoval.ExecuteAsync(plan, null, default).GetAwaiter().GetResult();
            Check(first.DeletedCount == 1 && !Directory.Exists(folder), "The first run did not delete the folder.");
            GameRemovalResult second = GameRemoval.ExecuteAsync(plan, null, default).GetAwaiter().GetResult();
            Check(second.FailedCount == 0, "The second run reported failures: " + FirstFailure(second));
            Check(second.Outcomes.Count > 0 && second.Outcomes.All(o => o.Status == GameRemovalItemStatus.Skipped && o.Reason == "AlreadyAbsent"),
                "Already-absent items did not report Skipped(AlreadyAbsent) on a second run.");
            deleted += 2;
        }

        // 6. A read-only file still gets deleted.
        {
            string folder = Path.Combine(root, "readonly-local");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "readonly.txt");
            File.WriteAllText(file, "ro");
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
            created += 2;
            var request = new GameRemovalRequest
            {
                GameId = "readonly",
                MountPath = "",
                LocalFolders = new[] { folder },
                AllowedRoots = new[] { root },
                IncludeDockerLeftovers = false
            };
            GameRemovalPlan plan = GameRemoval.Plan(request);
            GameRemovalResult result = GameRemoval.ExecuteAsync(plan, null, default).GetAwaiter().GetResult();
            Check(!Directory.Exists(folder) && result.DeletedCount == 1, "A read-only file prevented deletion.");
            deleted += 2;
        }

        // 7. One undeletable file yields Failed for that item while its sibling still deletes.
        {
            string locked = Path.Combine(root, "locked-tree");
            string free = Path.Combine(root, "free-tree");
            Directory.CreateDirectory(locked);
            Directory.CreateDirectory(free);
            File.WriteAllText(Path.Combine(locked, "busy.txt"), "busy");
            File.WriteAllText(Path.Combine(free, "free.txt"), "free");
            created += 4;
            using (var hold = new FileStream(Path.Combine(locked, "busy.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var request = new GameRemovalRequest
                {
                    GameId = "undeletable",
                    MountPath = "",
                    LocalFolders = new[] { locked, free },
                    AllowedRoots = new[] { root },
                    IncludeDockerLeftovers = false
                };
                GameRemovalPlan plan = GameRemoval.Plan(request);
                GameRemovalResult result = GameRemoval.ExecuteAsync(plan, null, default).GetAwaiter().GetResult();
                GameRemovalOutcome lockedOutcome = result.Outcomes.First(o => PathsEqual(o.Path, locked));
                GameRemovalOutcome freeOutcome = result.Outcomes.First(o => PathsEqual(o.Path, free));
                Check(lockedOutcome.Status == GameRemovalItemStatus.Failed, "A locked file did not yield Failed.");
                Check(freeOutcome.Status == GameRemovalItemStatus.Deleted && !Directory.Exists(free),
                    "A sibling target did not delete after another item failed.");
                Check(Directory.Exists(locked), "The locked tree was removed unexpectedly.");
            }
            if (Directory.Exists(locked)) Directory.Delete(locked, true);
            deleted += 2;
        }

        // 8. Describe() names every target, including the launcher and local folder.
        {
            string folder = Path.Combine(root, "describe-local");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "inside.txt"), "x");
            string launcher = Path.Combine(root, "describe-launcher.exe");
            File.WriteAllText(launcher, "exe");
            created += 3;
            var request = new GameRemovalRequest
            {
                GameId = "describe",
                GameName = "Describe Me",
                MountPath = root,
                LocalFolders = new[] { folder },
                LaunchPath = launcher,
                AllowedRoots = new[] { root },
                IncludeDockerLeftovers = false
            };
            GameRemovalPlan plan = GameRemoval.Plan(request);
            string text = GameRemoval.Describe(plan);
            Check(plan.Targets.Count > 0, "The describe plan had no targets.");
            foreach (GameRemovalTarget target in plan.Targets)
                Check(text.Contains(target.Path, StringComparison.OrdinalIgnoreCase), "Describe omitted target " + target.Path);
            GameRemovalResult result = GameRemoval.ExecuteAsync(plan, null, default).GetAwaiter().GetResult();
            Check(result.FailedCount == 0, "Describe fixture deletion reported failures: " + FirstFailure(result));
            Check(!Directory.Exists(folder) && !File.Exists(launcher), "Describe fixture targets were not deleted.");
            deleted += 3;
        }

        Console.WriteLine("SLOT1 FIXTURES created=" + created + " deleted=" + deleted);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string FirstFailure(GameRemovalResult result)
    {
        GameRemovalOutcome? failed = result.Outcomes.FirstOrDefault(o => o.Status == GameRemovalItemStatus.Failed);
        return failed == null ? "none" : failed.Path + " -> " + failed.Reason;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            string command = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            var start = new ProcessStartInfo(command)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("mklink");
            start.ArgumentList.Add("/J");
            start.ArgumentList.Add(link);
            start.ArgumentList.Add(target);
            using Process? process = Process.Start(start);
            if (process == null) return false;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(10000);
            return Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteJunction(string link)
    {
        try
        {
            if (Directory.Exists(link)) Directory.Delete(link, false);
        }
        catch { }
    }
}
