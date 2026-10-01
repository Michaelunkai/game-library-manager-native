using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

internal static class SaveRestoreCoordinatorTests
{
    internal static void Run(string root)
    {
        Directory.CreateDirectory(root);
        SaveOperationDiagnostics.Reset();
        Require(SaveRestoreCoordinator.DefaultRunningGamePolicy == RunningGamePolicy.Snapshot,
            "The default running-game policy must be Snapshot so saves work while a game is running.");
        TestMultiRootBackupIsByteForByte(Path.Combine(root, "01-multi-root"));
        TestBackupWhileRunningStaysConsistent(Path.Combine(root, "02-while-running"));
        TestVolatileSiblingIsSkipped(Path.Combine(root, "03-volatile"));
        TestRestoreRoundTripsExactly(Path.Combine(root, "04-round-trip"));
        TestCorruptBackupIsRefused(Path.Combine(root, "05-corrupt"));
        TestFailedRestoreRollsBack(Path.Combine(root, "06-rollback"));
        TestQuarantineFolderIsTimestamped(Path.Combine(root, "07-quarantine"));
        TestManifestIsStable(Path.Combine(root, "08-manifest-stable"));
        TestVerifyDetectsFlippedByte(Path.Combine(root, "09-flipped-byte"));
        TestPermissionDeniedFailsOnlyThatItem(Path.Combine(root, "10-permission"));
        TestCancellationLeavesNoPartialLiveFile(Path.Combine(root, "11-cancel"));
        TestEmptyRootsReportNoSaveData(Path.Combine(root, "12-no-save-data"));
        TestRefusePolicyBlocksWhileRunning(Path.Combine(root, "13-refuse"));
        TestConcurrencyIsBounded(Path.Combine(root, "14-bounded"));
    }

    // 1. A multi-file, multi-root save tree is copied byte for byte and every
    //    payload file reports Copied and then Verified.
    private static void TestMultiRootBackupIsByteForByte(string root)
    {
        string savesA = Path.Combine(root, "saves-a");
        string savesB = Path.Combine(root, "saves-b");
        string destination = Path.Combine(root, "backups");
        var expected = new List<(string Source, string Hash, int RootIndex)>();
        var folders = new[] { (savesA, "a", 0), (savesB, "b", 1) };
        foreach ((string folder, string name, int index) in folders)
        {
            Directory.CreateDirectory(Path.Combine(folder, "nested"));
            expected.Add((Path.Combine(folder, "slot1.sav"),
                WriteFile(Path.Combine(folder, "slot1.sav"), name + "-slot1", 4096), index));
            expected.Add((Path.Combine(folder, "nested", "slot2.sav"),
                WriteFile(Path.Combine(folder, "nested", "slot2.sav"), name + "-slot2", 8192), index));
            expected.Add((Path.Combine(folder, "settings.ini"),
                WriteFile(Path.Combine(folder, "settings.ini"), name + "-settings", 128), index));
        }

        SaveOperationOutcome outcome = SaveRestoreCoordinator.BackupAsync(
            Request(new[] { savesA, savesB }), NullSaveRootProvider.Instance, new FileSystemBackupWriter(),
            destination, null, CancellationToken.None).GetAwaiter().GetResult();

        Require(outcome.Success, "A clean multi-root backup reported failure: " + outcome.Summary);
        Require(outcome.Status == SaveOperationStatus.Completed, "A clean backup did not report Completed: " + outcome.Summary);
        Require(outcome.CountOf(SaveItemOutcome.Copied) == expected.Count,
            "Expected " + expected.Count + " Copied items but saw " + outcome.CountOf(SaveItemOutcome.Copied) + ".");
        Require(outcome.CountOf(SaveItemOutcome.Verified) == expected.Count,
            "Expected " + expected.Count + " Verified items but saw " + outcome.CountOf(SaveItemOutcome.Verified) + ".");
        Require(outcome.CountOf(SaveItemOutcome.Failed) == 0 && outcome.CountOf(SaveItemOutcome.Unstable) == 0,
            "A clean backup reported failed or unstable items: " + outcome.Summary);
        Require(File.Exists(Path.Combine(outcome.BackupRoot, SaveRestoreCoordinator.ManifestFileName)),
            "The backup did not write a save manifest.");

        foreach ((string source, string hash, int index) in expected)
        {
            string copy = PayloadFor(outcome, source, index);
            Require(File.Exists(copy), "The backup is missing the payload for " + source + ".");
            Require(HashOf(copy) == hash, "The backup copy of " + source + " is not byte for byte identical.");
        }
    }

    // 2. A "running" game keeps rewriting one save file for the whole duration of
    //    the backup. The copy must still end up consistent and the unstable-file
    //    retry must actually engage.
    private static void TestBackupWhileRunningStaysConsistent(string root)
    {
        string saves = Path.Combine(root, "live-saves");
        Directory.CreateDirectory(saves);
        string chunk = Path.Combine(saves, "world.chunk");
        WriteFile(chunk, new string('A', 1024 * 1024), 1024 * 1024);
        WriteFile(Path.Combine(saves, "slot1.sav"), "stable-slot", 512);

        var started = new ManualResetEventSlim(false);
        var stop = new ManualResetEventSlim(false);
        int generation = 0;
        string settledHash = string.Empty;
        var mutator = Task.Run(() =>
        {
            while (!stop.IsSet)
            {
                // A different length every generation makes a torn copy impossible
                // to mistake for a settled one.
                generation++;
                byte[] payload = Encoding.UTF8.GetBytes("MUT|" + generation.ToString(CultureInfo.InvariantCulture) + "|"
                    + new string((char)('a' + generation % 26), 512 * 1024));
                File.WriteAllBytes(chunk, payload);
                settledHash = Convert.ToHexString(SHA256.HashData(payload));
                started.Set();
                Thread.Sleep(1);
            }
        });
        Require(started.Wait(TimeSpan.FromSeconds(10)), "The fixture game never started writing its save.");

        int retriesBefore = SaveOperationDiagnostics.SnapshotRetries;
        // The mutator keeps writing until the snapshot has actually had to retry,
        // so the retry path is exercised for real rather than by luck.
        var watcher = Task.Run(() =>
        {
            var spin = new SpinWait();
            while (!stop.IsSet)
            {
                if (SaveOperationDiagnostics.SnapshotRetries > retriesBefore) { stop.Set(); break; }
                spin.SpinOnce();
                Thread.Sleep(1);
            }
        });
        SaveOperationOutcome outcome = default!;
        try
        {
            var progress = new List<SaveOperationProgress>();
            var tracker = new Progress<SaveOperationProgress>(progress.Add);
            // Reading the 1 MiB chunk takes long enough that the game's rewrite
            // reliably lands inside the copy window, so the retry path is exercised
            // for real rather than by luck.
            var writer = new ScriptedWriter
            {
                BeforeCopy = async (source, destination) =>
                {
                    if (source.EndsWith("world.chunk", StringComparison.OrdinalIgnoreCase))
                        await Task.Delay(60).ConfigureAwait(false);
                }
            };
            outcome = SaveRestoreCoordinator.BackupAsync(
                Request(new[] { saves }, "fixturegame.exe"), NullSaveRootProvider.Instance, writer,
                Path.Combine(root, "backups"), tracker, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally { stop.Set(); }
        watcher.GetAwaiter().GetResult();
        mutator.GetAwaiter().GetResult();

        Require(outcome.Success, "A backup taken while the game was running did not succeed: " + outcome.Summary);
        Require(SaveOperationDiagnostics.SnapshotRetries > retriesBefore,
            "The unstable-file retry never engaged while the game was rewriting its save.");
        Require(outcome.Summary.Contains("fixturegame.exe", StringComparison.OrdinalIgnoreCase),
            "The summary did not disclose that the snapshot was taken while the game was running: " + outcome.Summary);

        string copy = PayloadFor(outcome, chunk);
        Require(File.Exists(copy), "The running-game backup is missing the mutated save file.");
        Require(HashOf(copy) == settledHash,
            "The backup captured a torn copy of a file the game was writing.");
        Require(outcome.Items.Any(item => item.Outcome == SaveItemOutcome.Verified
            && item.Path.EndsWith("world.chunk", StringComparison.OrdinalIgnoreCase)),
            "The mutated save file did not report Verified.");
    }

    // 3. A .tmp sibling is skipped in favour of the settled file beside it.
    private static void TestVolatileSiblingIsSkipped(string root)
    {
        string saves = Path.Combine(root, "saves");
        Directory.CreateDirectory(saves);
        string settled = Path.Combine(saves, "slot1.sav");
        WriteFile(settled, "settled-progress", 64);
        WriteFile(settled + ".tmp", "mid-write-garbage", 64);
        WriteFile(Path.Combine(saves, "keep.dat.tmp"), "orphan-volatile-kept", 64);

        SaveOperationOutcome outcome = SaveRestoreCoordinator.BackupAsync(
            Request(new[] { saves }), NullSaveRootProvider.Instance, new FileSystemBackupWriter(),
            Path.Combine(root, "backups"), null, CancellationToken.None).GetAwaiter().GetResult();

        Require(outcome.Success, "A backup with a volatile sibling did not succeed: " + outcome.Summary);
        Require(SaveOperationDiagnostics.VolatileSkips >= 1, "The volatile-sibling strategy never engaged.");
        var skipped = outcome.Items.Where(item => item.Outcome == SaveItemOutcome.Skipped).ToList();
        Require(skipped.Any(item => item.Path.EndsWith("slot1.sav.tmp", StringComparison.OrdinalIgnoreCase)),
            "The in-progress .tmp sibling was not skipped in favour of the settled file.");
        Require(skipped.Any(item => item.Detail.Contains("settled=", StringComparison.Ordinal)
                && item.Detail.TrimEnd().EndsWith("slot1.sav", StringComparison.OrdinalIgnoreCase)),
            "The skip did not name the settled file it preferred.");
        Require(HashOf(PayloadFor(outcome, settled)) == HashOf(settled),
            "The settled file was not copied.");
        Require(File.Exists(PayloadFor(outcome, Path.Combine(saves, "keep.dat.tmp"))),
            "A volatile file with no settled counterpart was dropped, which would lose data.");
    }

    // 4. A restore puts the original content back exactly.
    private static void TestRestoreRoundTripsExactly(string root)
    {
        string saves = Path.Combine(root, "saves");
        string destination = Path.Combine(root, "backups");
        Directory.CreateDirectory(Path.Combine(saves, "nested"));
        WriteFile(Path.Combine(saves, "slot1.sav"), "original-slot1", 2048);
        WriteFile(Path.Combine(saves, "nested", "slot2.sav"), "original-slot2", 4096);
        var original = SnapshotOf(saves);

        SaveOperationOutcome backup = SaveRestoreCoordinator.BackupAsync(Request(new[] { saves }),
            NullSaveRootProvider.Instance, new FileSystemBackupWriter(), destination, null, CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(backup.Success, "The backup for the round trip failed: " + backup.Summary);

        WriteFile(Path.Combine(saves, "slot1.sav"), "corrupted-progress", 2048);
        File.Delete(Path.Combine(saves, "nested", "slot2.sav"));

        SaveOperationOutcome restored = SaveRestoreCoordinator.RestoreAsync(Request(new[] { saves }),
            NullSaveRootProvider.Instance, new FileSystemBackupWriter(), backup.BackupRoot, null, CancellationToken.None)
            .GetAwaiter().GetResult();

        Require(restored.Success, "The round-trip restore did not succeed: " + restored.Summary);
        Require(restored.Status == SaveOperationStatus.Completed, "The round-trip restore did not report Completed.");
        Require(SameSnapshot(saves, original), "The restore did not put the original save content back exactly.");
    }

    // 5. A corrupt backup is refused and the live save is left untouched.
    private static void TestCorruptBackupIsRefused(string root)
    {
        string saves = Path.Combine(root, "saves");
        Directory.CreateDirectory(saves);
        WriteFile(Path.Combine(saves, "slot1.sav"), "pristine-progress", 512);
        SaveOperationOutcome backup = SaveRestoreCoordinator.BackupAsync(Request(new[] { saves }),
            NullSaveRootProvider.Instance, new FileSystemBackupWriter(), Path.Combine(root, "backups"), null, CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(backup.Success, "The setup backup failed: " + backup.Summary);

        WriteFile(Path.Combine(saves, "slot1.sav"), "live-progress-must-survive", 512);
        var liveBefore = SnapshotOf(saves);
        string payload = PayloadFor(backup, Path.Combine(saves, "slot1.sav"));
        FlipByte(payload);

        SaveOperationOutcome refused = SaveRestoreCoordinator.RestoreAsync(Request(new[] { saves }),
            NullSaveRootProvider.Instance, new FileSystemBackupWriter(), backup.BackupRoot, null, CancellationToken.None)
            .GetAwaiter().GetResult();

        Require(!refused.Success, "A corrupt backup was accepted for restore.");
        Require(refused.Status == SaveOperationStatus.Refused, "A corrupt backup was not refused: " + refused.Summary);
        Require(refused.Summary.Contains("not intact", StringComparison.OrdinalIgnoreCase),
            "The refusal did not explain that the backup is not intact: " + refused.Summary);
        Require(refused.Items.Any(item => item.Outcome == SaveItemOutcome.Failed
            && item.Detail.Contains("hash-mismatch", StringComparison.OrdinalIgnoreCase)),
            "The corrupt backup did not report a hash mismatch.");
        Require(refused.QuarantineRoot is null, "A refused restore created a quarantine folder.");
        Require(SameSnapshot(saves, liveBefore), "A refused restore modified the live save.");
    }

    // 6. A restore whose write fails rolls back from quarantine and the
    //    pre-attempt content is intact afterwards.
    private static void TestFailedRestoreRollsBack(string root)
    {
        string saves = Path.Combine(root, "saves");
        Directory.CreateDirectory(saves);
        WriteFile(Path.Combine(saves, "slot1.sav"), "backup-slot1", 1024);
        WriteFile(Path.Combine(saves, "slot2.sav"), "backup-slot2", 1024);
        SaveOperationOutcome backup = SaveRestoreCoordinator.BackupAsync(Request(new[] { saves }),
            NullSaveRootProvider.Instance, new FileSystemBackupWriter(), Path.Combine(root, "backups"), null, CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(backup.Success, "The setup backup failed: " + backup.Summary);

        WriteFile(Path.Combine(saves, "slot1.sav"), "live-slot1-must-survive", 1024);
        WriteFile(Path.Combine(saves, "slot2.sav"), "live-slot2-must-survive", 1024);
        var liveBefore = SnapshotOf(saves);

        var writer = new ScriptedWriter
        {
            CopyFault = (source, destination) =>
                destination.EndsWith("slot2.sav", StringComparison.OrdinalIgnoreCase) &&
                destination.Contains(SaveRestoreCoordinator.QuarantinePrefix, StringComparison.OrdinalIgnoreCase)
                    ? new IOException("The fixture refused to write slot2.sav.")
                    : null
        };
        SaveOperationOutcome rolled = SaveRestoreCoordinator.RestoreAsync(Request(new[] { saves }),
            NullSaveRootProvider.Instance, writer, backup.BackupRoot, null, CancellationToken.None)
            .GetAwaiter().GetResult();

        Require(!rolled.Success, "A restore whose write failed reported success: " + rolled.Summary);
        Require(rolled.Status == SaveOperationStatus.RolledBack, "The failed restore did not report RolledBack: " + rolled.Summary);
        Require(SameSnapshot(saves, liveBefore), "The rollback did not restore the pre-attempt live save content.");
        Require(rolled.Items.Any(item => item.Outcome == SaveItemOutcome.RolledBack && item.Detail.Contains("verified=true", StringComparison.Ordinal)),
            "The rollback was not itself verified.");
        Require(NoPartialFiles(saves), "The rollback left a partial file in the live save folder.");
        Require(rolled.QuarantineRoot is not null && Directory.Exists(rolled.QuarantineRoot),
            "The rollback did not keep the quarantined previous save for the user.");
    }

    // 7. The quarantine folder is created inside the backup root and is timestamped.
    private static void TestQuarantineFolderIsTimestamped(string root)
    {
        string saves = Path.Combine(root, "saves");
        Directory.CreateDirectory(saves);
        WriteFile(Path.Combine(saves, "slot1.sav"), "content-a", 256);
        SaveOperationOutcome backup = SaveRestoreCoordinator.BackupAsync(Request(new[] { saves }),
            NullSaveRootProvider.Instance, new FileSystemBackupWriter(), Path.Combine(root, "backups"), null, CancellationToken.None)
            .GetAwaiter().GetResult();
        WriteFile(Path.Combine(saves, "slot1.sav"), "content-b", 256);

        SaveOperationOutcome restored = SaveRestoreCoordinator.RestoreAsync(Request(new[] { saves }),
            NullSaveRootProvider.Instance, new FileSystemBackupWriter(), backup.BackupRoot, null, CancellationToken.None)
            .GetAwaiter().GetResult();

        string quarantine = restored.QuarantineRoot
            ?? throw new InvalidOperationException("A successful restore did not create a quarantine folder.");
        string leaf = Path.GetFileName(quarantine.TrimEnd(Path.DirectorySeparatorChar));
        Require(leaf.StartsWith(SaveRestoreCoordinator.QuarantinePrefix, StringComparison.Ordinal),
            "The quarantine folder is not named with the .pre-restore- prefix: " + leaf);
        Require(DateTime.TryParseExact(leaf.Substring(SaveRestoreCoordinator.QuarantinePrefix.Length),
            SaveRestoreCoordinator.TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            "The quarantine folder does not carry a parseable timestamp: " + leaf);
        Require(Path.GetFullPath(quarantine).StartsWith(Path.GetFullPath(backup.BackupRoot) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase), "The quarantine folder is not inside the backup root: " + quarantine);
        Require(Directory.Exists(quarantine), "The quarantine folder was not created on disk.");
        Require(restored.Items.Any(item => item.Outcome == SaveItemOutcome.Quarantined && item.Detail.Contains("movedTo=", StringComparison.Ordinal)),
            "The restore did not report the quarantine move.");
    }

    // 8. The manifest is byte-for-byte stable across two runs over identical input.
    private static void TestManifestIsStable(string root)
    {
        string saves = Path.Combine(root, "saves");
        Directory.CreateDirectory(Path.Combine(saves, "nested"));
        WriteFile(Path.Combine(saves, "zeta.sav"), "zeta", 128);
        WriteFile(Path.Combine(saves, "alpha.sav"), "alpha", 128);
        WriteFile(Path.Combine(saves, Path.Combine("nested", "middle.sav")), "middle", 128);

        SaveOperationOutcome first = SaveRestoreCoordinator.BackupAsync(Request(new[] { saves }),
            NullSaveRootProvider.Instance, new FileSystemBackupWriter(), Path.Combine(root, "backups"), null, CancellationToken.None)
            .GetAwaiter().GetResult();
        Thread.Sleep(25);
        SaveOperationOutcome second = SaveRestoreCoordinator.BackupAsync(Request(new[] { saves }),
            NullSaveRootProvider.Instance, new FileSystemBackupWriter(), Path.Combine(root, "backups"), null, CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(first.Success && second.Success, "A stability setup backup failed.");
        Require(first.BackupRoot != second.BackupRoot, "Two backups shared one folder, so stability cannot be measured.");

        string manifestA = File.ReadAllText(Path.Combine(first.BackupRoot, SaveRestoreCoordinator.ManifestFileName));
        string manifestB = File.ReadAllText(Path.Combine(second.BackupRoot, SaveRestoreCoordinator.ManifestFileName));
        Require(manifestA == manifestB, "The save manifest was not stable across two identical backups.");
        Require(manifestA == SaveRestoreCoordinator.BuildManifest(first.Items),
            "The manifest on disk does not match BuildManifest over the same items.");

        string[] fileLines = manifestA.Split('\n').Where(line => line.StartsWith("FILE\t", StringComparison.Ordinal)).ToArray();
        Require(fileLines.Length == 3, "The manifest listed " + fileLines.Length + " files instead of 3.");
        var order = fileLines.Select(line => line.Split('\t')[1]).ToList();
        var expectedOrder = order.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        Require(order.SequenceEqual(expectedOrder, StringComparer.Ordinal),
            "The manifest is not in a stable ordinal-ignore-case path order.");
        Require(fileLines.All(line => line.Split('\t')[3].Length == 64),
            "The manifest does not carry a per-file SHA256.");
        Require(SaveRestoreCoordinator.BuildManifest(first.Items) == SaveRestoreCoordinator.BuildManifest(first.Items.Reverse().ToList()),
            "BuildManifest is not independent of input order.");
    }

    // 9. VerifyAsync detects a single flipped byte.
    private static void TestVerifyDetectsFlippedByte(string root)
    {
        string saves = Path.Combine(root, "saves");
        Directory.CreateDirectory(saves);
        WriteFile(Path.Combine(saves, "slot1.sav"), "aaaabbbbccccdddd", 256);
        WriteFile(Path.Combine(saves, "slot2.sav"), "eeeeffffgggghhhh", 256);
        SaveOperationOutcome backup = SaveRestoreCoordinator.BackupAsync(Request(new[] { saves }),
            NullSaveRootProvider.Instance, new FileSystemBackupWriter(), Path.Combine(root, "backups"), null, CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(backup.Success, "The setup backup failed: " + backup.Summary);
        string manifest = Path.Combine(backup.BackupRoot, SaveRestoreCoordinator.ManifestFileName);

        ManifestVerificationResult clean = SaveRestoreCoordinator.VerifyAsync(manifest).GetAwaiter().GetResult();
        Require(clean.IsIntact, "An untouched backup did not verify: " + clean.Summary);

        string tampered = PayloadFor(backup, Path.Combine(saves, "slot1.sav"));
        byte[] bytes = File.ReadAllBytes(tampered);
        bytes[bytes.Length / 2] ^= 0x01;
        File.WriteAllBytes(tampered, bytes);

        ManifestVerificationResult damaged = SaveRestoreCoordinator.VerifyAsync(manifest).GetAwaiter().GetResult();
        Require(!damaged.IsIntact, "A single flipped byte was not detected.");
        var bad = damaged.Items.Where(item => item.Outcome == SaveItemOutcome.Failed).ToList();
        Require(bad.Count == 1, "A single flipped byte produced " + bad.Count + " failures instead of 1.");
        Require(bad[0].Path == tampered, "The flipped byte was attributed to the wrong file: " + bad[0].Path);
        Require(bad[0].Detail.Contains("hash-mismatch", StringComparison.OrdinalIgnoreCase),
            "The flipped byte was not reported as a hash mismatch: " + bad[0].Detail);
    }

    // 10. One denied file fails on its own; its siblings still copy, and the
    //     overall result is honestly not a success.
    private static void TestPermissionDeniedFailsOnlyThatItem(string root)
    {
        string saves = Path.Combine(root, "saves");
        Directory.CreateDirectory(saves);
        WriteFile(Path.Combine(saves, "aaa.sav"), "aaa", 512);
        WriteFile(Path.Combine(saves, "locked.sav"), "locked", 512);
        WriteFile(Path.Combine(saves, "zzz.sav"), "zzz", 512);
        var writer = new ScriptedWriter
        {
            CopyFault = (source, destination) => source.EndsWith("locked.sav", StringComparison.OrdinalIgnoreCase)
                ? new UnauthorizedAccessException("Access to the path is denied.")
                : null
        };
        SaveOperationOutcome outcome = SaveRestoreCoordinator.BackupAsync(Request(new[] { saves }),
            NullSaveRootProvider.Instance, writer, Path.Combine(root, "backups"), null, CancellationToken.None)
            .GetAwaiter().GetResult();

        Require(!outcome.Success, "A backup with a denied file reported success: " + outcome.Summary);
        Require(outcome.Status == SaveOperationStatus.Failed, "The denied file did not make the result Failed.");
        Require(outcome.CountOf(SaveItemOutcome.Failed) == 1,
            "Expected exactly one Failed item but saw " + outcome.CountOf(SaveItemOutcome.Failed) + ".");
        SaveItemReport failure = outcome.Items.First(item => item.Outcome == SaveItemOutcome.Failed);
        Require(failure.Detail.Contains("locked.sav", StringComparison.OrdinalIgnoreCase),
            "The failure did not name the denied file: " + failure.Detail);
        Require(failure.Detail.Contains("UnauthorizedAccessException", StringComparison.Ordinal),
            "The failure did not surface the denial reason: " + failure.Detail);
        Require(outcome.CountOf(SaveItemOutcome.Verified) == 2,
            "The siblings of a denied file did not still copy: " + outcome.Summary);
        Require(!File.Exists(Path.Combine(Path.GetFullPath(outcome.BackupRoot), "payload", "root-000", "locked.sav")),
            "The denied file was written to the backup anyway.");
        Require(HashOf(PayloadFor(outcome, Path.Combine(saves, "aaa.sav"))) == HashOf(Path.Combine(saves, "aaa.sav")) &&
            HashOf(PayloadFor(outcome, Path.Combine(saves, "zzz.sav"))) == HashOf(Path.Combine(saves, "zzz.sav")),
            "A sibling of the denied file was not copied byte for byte.");
        Require(outcome.Summary.Contains("failed", StringComparison.OrdinalIgnoreCase),
            "The summary did not disclose the failure: " + outcome.Summary);
    }

    // 11. Cancelling mid-operation never leaves a partial file in the live save
    //     folder, and the pre-attempt content survives.
    private static void TestCancellationLeavesNoPartialLiveFile(string root)
    {
        string saves = Path.Combine(root, "saves");
        Directory.CreateDirectory(Path.Combine(root, "backups"));
        WriteFile(Path.Combine(saves, "slot1.sav"), "one", 256);
        WriteFile(Path.Combine(saves, "slot2.sav"), "two", 256);
        WriteFile(Path.Combine(saves, "slot3.sav"), "three", 256);
        var before = SnapshotOf(saves);

        using (var backupCancellation = new CancellationTokenSource())
        {
            var writer = new ScriptedWriter
            {
                AfterCopy = (destination, token) =>
                {
                    if (destination.Contains(".staging", StringComparison.OrdinalIgnoreCase))
                        backupCancellation.Cancel();
                    return Task.CompletedTask;
                }
            };
            SaveOperationOutcome cancelled = SaveRestoreCoordinator.BackupAsync(Request(new[] { saves }),
                NullSaveRootProvider.Instance, writer, Path.Combine(root, "backups"), null, backupCancellation.Token)
                .GetAwaiter().GetResult();
            Require(cancelled.Status == SaveOperationStatus.Cancelled,
                "A cancelled backup did not report Cancelled: " + cancelled.Status + " / " + cancelled.Summary);
            Require(NoPartialFiles(saves), "A cancelled backup left a partial file in the live save folder.");
            Require(SameSnapshot(saves, before), "A cancelled backup modified the live save.");
            Require(!Directory.Exists(Path.Combine(root, "backups", ".staging")),
                "A cancelled backup left its staging scratch folder behind.");
        }

        SaveOperationOutcome backup = SaveRestoreCoordinator.BackupAsync(Request(new[] { saves }),
            NullSaveRootProvider.Instance, new FileSystemBackupWriter(), Path.Combine(root, "backups"), null, CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(backup.Success, "The setup backup failed: " + backup.Summary);

        WriteFile(Path.Combine(saves, "slot1.sav"), "changed-one", 256);
        WriteFile(Path.Combine(saves, "slot2.sav"), "changed-two", 256);
        WriteFile(Path.Combine(saves, "slot3.sav"), "changed-three", 256);
        var liveBefore = SnapshotOf(saves);
        using (var restoreCancellation = new CancellationTokenSource())
        {
            var writer = new ScriptedWriter
            {
                AfterCopy = (destination, token) =>
                {
                    if (destination.Contains(SaveRestoreCoordinator.QuarantinePrefix, StringComparison.OrdinalIgnoreCase) &&
                        destination.EndsWith("slot2.sav", StringComparison.OrdinalIgnoreCase))
                        restoreCancellation.Cancel();
                    return Task.CompletedTask;
                }
            };
            SaveOperationOutcome cancelled = SaveRestoreCoordinator.RestoreAsync(Request(new[] { saves }),
                NullSaveRootProvider.Instance, writer, backup.BackupRoot, null, restoreCancellation.Token)
                .GetAwaiter().GetResult();
            Require(cancelled.Status == SaveOperationStatus.Cancelled,
                "A cancelled restore did not report Cancelled: " + cancelled.Status + " / " + cancelled.Summary);
            Require(NoPartialFiles(saves), "A cancelled restore left a partial file in the live save folder.");
            Require(SameSnapshot(saves, liveBefore), "A cancelled restore did not put the pre-attempt live save back.");
            Require(cancelled.Items.Any(item => item.Outcome == SaveItemOutcome.RolledBack),
                "A cancelled restore did not roll back from quarantine.");
        }
    }

    // 12. No save roots is a clear NoSaveData result, not an exception.
    private static void TestEmptyRootsReportNoSaveData(string root)
    {
        string destination = Path.Combine(root, "backups");
        Directory.CreateDirectory(destination);
        SaveOperationOutcome empty = SaveRestoreCoordinator.BackupAsync(Request(Array.Empty<string>()),
            NullSaveRootProvider.Instance, new FileSystemBackupWriter(), destination, null, CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(empty.Status == SaveOperationStatus.NoSaveData, "An empty save-root list did not report NoSaveData.");
        Require(empty.NoSaveData, "NoSaveData was not readable from the outcome.");
        Require(empty.Summary.Contains("NoSaveData", StringComparison.Ordinal),
            "The NoSaveData summary was not explicit: " + empty.Summary);
        Require(!Directory.Exists(Path.Combine(destination, "empty-roots", "payload")),
            "An empty save-root list created a backup payload folder.");

        string saves = Path.Combine(root, "saves");
        Directory.CreateDirectory(saves);
        WriteFile(Path.Combine(saves, "slot1.sav"), "content", 128);
        SaveOperationOutcome backup = SaveRestoreCoordinator.BackupAsync(Request(new[] { saves }),
            NullSaveRootProvider.Instance, new FileSystemBackupWriter(), destination, null, CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(backup.Success, "The setup backup failed: " + backup.Summary);

        string liveBefore = File.ReadAllText(Path.Combine(saves, "slot1.sav"));
        SaveOperationOutcome restored = SaveRestoreCoordinator.RestoreAsync(Request(Array.Empty<string>()),
            NullSaveRootProvider.Instance, new FileSystemBackupWriter(), backup.BackupRoot, null, CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(restored.Status == SaveOperationStatus.NoSaveData,
            "Restoring with no save roots did not report NoSaveData: " + restored.Status);
        Require(restored.Summary.Contains("NoSaveData", StringComparison.Ordinal),
            "The no-save-data restore summary was not explicit: " + restored.Summary);
        Require(File.ReadAllText(Path.Combine(saves, "slot1.sav")) == liveBefore,
            "The no-save-data restore modified the live save.");
    }

    // 13. Refuse is an explicit, testable policy; Snapshot is the default.
    private static void TestRefusePolicyBlocksWhileRunning(string root)
    {
        string saves = Path.Combine(root, "saves");
        string destination = Path.Combine(root, "backups");
        Directory.CreateDirectory(saves);
        WriteFile(Path.Combine(saves, "slot1.sav"), "content", 128);
        var request = Request(new[] { saves }, "fixturegame.exe", allowWhileRunning: false);

        Require(SaveRestoreCoordinator.ResolvePolicy(request) == RunningGamePolicy.Refuse,
            "A request that does not allow running games did not resolve to the Refuse policy.");
        Require(!SaveRestoreCoordinator.CanRestoreWhileRunningAsync(request).GetAwaiter().GetResult(),
            "CanRestoreWhileRunningAsync allowed a Refuse request.");
        Require(SaveRestoreCoordinator.CanRestoreWhileRunningAsync(request, RunningGamePolicy.Snapshot)
            .GetAwaiter().GetResult(), "CanRestoreWhileRunningAsync refused an explicit Snapshot policy.");

        SaveOperationOutcome refused = SaveRestoreCoordinator.BackupAsync(request, NullSaveRootProvider.Instance,
            new FileSystemBackupWriter(), destination, null, CancellationToken.None).GetAwaiter().GetResult();
        Require(refused.Status == SaveOperationStatus.Refused, "The Refuse policy did not refuse a running game.");
        Require(!Directory.Exists(Path.Combine(destination, "fixture-game", "payload")),
            "The Refuse policy still wrote backup payload.");

        SaveOperationOutcome snapshot = SaveRestoreCoordinator.BackupAsync(request, NullSaveRootProvider.Instance,
            new FileSystemBackupWriter(), destination, null, CancellationToken.None, RunningGamePolicy.Snapshot)
            .GetAwaiter().GetResult();
        Require(snapshot.Success, "The Snapshot policy refused a running game: " + snapshot.Summary);
    }

    // 14. File work is bounded to four concurrent operations.
    private static void TestConcurrencyIsBounded(string root)
    {
        string saves = Path.Combine(root, "saves");
        Directory.CreateDirectory(saves);
        for (int index = 0; index < 8; index++)
            WriteFile(Path.Combine(saves, "chunk" + index.ToString(CultureInfo.InvariantCulture) + ".bin"),
                new string((char)('a' + index), 512 * 1024), 512 * 1024);
        var writer = new ScriptedWriter();
        SaveOperationOutcome outcome = SaveRestoreCoordinator.BackupAsync(Request(new[] { saves }),
            NullSaveRootProvider.Instance, writer, Path.Combine(root, "backups"), null, CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(outcome.Success, "The bounded-concurrency backup failed: " + outcome.Summary);
        Require(writer.PeakConcurrency <= SaveRestoreCoordinator.MaxParallelFileOperations,
            "The backup ran " + writer.PeakConcurrency + " file operations at once, above the limit of "
            + SaveRestoreCoordinator.MaxParallelFileOperations + ".");
    }

    private static SaveOperationRequest Request(IReadOnlyList<string> roots, string? runningProcess = null,
        bool allowWhileRunning = true) => new SaveOperationRequest(
            "game-1", "Fixture Game", roots, Path.Combine("C:", "Games", "Fixture"),
            runningProcess, allowWhileRunning);

    private static string PayloadFor(SaveOperationOutcome outcome, string source, int rootIndex = 0)
    {
        string name = Path.GetFileName(source);
        string marker = "payload" + Path.DirectorySeparatorChar + SaveRestoreCoordinator.RootKey(rootIndex)
            + Path.DirectorySeparatorChar;
        var match = outcome.Items.FirstOrDefault(item =>
            item.Outcome == SaveItemOutcome.Verified &&
            item.Path.EndsWith(name, StringComparison.OrdinalIgnoreCase) &&
            item.Path.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0);
        if (match is null) throw new InvalidOperationException("The backup has no payload for " + source + ".");
        return match.Path;
    }

    private static string WriteFile(string path, string text, int size)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var builder = new StringBuilder(size);
        while (builder.Length < size) builder.Append(text);
        File.WriteAllText(path, builder.ToString(0, size));
        return HashOf(path);
    }

    private static string HashOf(string path)
    {
        using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static Dictionary<string, string> SnapshotOf(string root) =>
        SaveRestoreCoordinator.ListFiles(root)
            .ToDictionary(file => file[(Path.GetFullPath(root).Length + 1)..], HashOf, StringComparer.OrdinalIgnoreCase);

    private static bool SameSnapshot(string root, IReadOnlyDictionary<string, string> expected)
    {
        Dictionary<string, string> now = SnapshotOf(root);
        return expected.Count == now.Count && expected.All(pair =>
            now.TryGetValue(pair.Key, out string? hash) && hash == pair.Value);
    }

    private static bool NoPartialFiles(string root)
    {
        if (!Directory.Exists(root)) return true;
        return SaveRestoreCoordinator.ListFiles(root)
            .All(file => !Path.GetFileName(file).Contains(".partial", StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileName(file).Contains(".restore", StringComparison.OrdinalIgnoreCase));
    }

    private static void FlipByte(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Require(bytes.Length > 0, "Cannot flip a byte in an empty file: " + path);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // Injectable seam used to simulate a locked file, a failing write and a
    // mid-operation cancellation without touching any real game.
    private sealed class ScriptedWriter : IBackupWriter
    {
        private readonly IBackupWriter inner = new FileSystemBackupWriter();
        private int active;
        private int peak;

        internal Func<string, string, Exception?>? CopyFault { get; init; }
        internal Func<string, string, Task>? BeforeCopy { get; init; }
        internal Func<string, CancellationToken, Task>? AfterCopy { get; init; }
        internal int PeakConcurrency => Volatile.Read(ref peak);

        public void EnsureDirectory(string path) => inner.EnsureDirectory(path);
        public Task<string> HashFileAsync(string path, CancellationToken cancellationToken) =>
            inner.HashFileAsync(path, cancellationToken);
        public void MovePath(string source, string destination) => inner.MovePath(source, destination);
        public void DeletePath(string path) => inner.DeletePath(path);

        public async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
        {
            int now = Interlocked.Increment(ref active);
            int seen = Volatile.Read(ref peak);
            while (now > seen && Interlocked.CompareExchange(ref peak, now, seen) != seen) seen = Volatile.Read(ref peak);
            try
            {
                if (BeforeCopy is not null) await BeforeCopy(source, destination).ConfigureAwait(false);
                Exception? fault = CopyFault?.Invoke(source, destination);
                if (fault is not null) throw fault;
                await inner.CopyFileAsync(source, destination, cancellationToken).ConfigureAwait(false);
                if (AfterCopy is not null) await AfterCopy(destination, cancellationToken).ConfigureAwait(false);
            }
            finally { Interlocked.Decrement(ref active); }
        }
    }
}
