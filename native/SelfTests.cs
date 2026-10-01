using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GameLibrary.Native;

public static class SelfTests
{
    public static int Run(string report)
    {
        var checks = new List<object>();
        var root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!, "test-data-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
        int failures = 0;
        void Check(string name, Action test)
        {
            try { test(); checks.Add(new { name, passed = true, at = DateTime.UtcNow }); }
            catch (Exception ex) { failures++; checks.Add(new { name, passed = false, error = ex.ToString(), at = DateTime.UtcNow }); }
        }
        void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is ArgumentException or FormatException) { return; } throw new Exception("Invalid input was accepted."); }
        var store = new LibraryStore(root); var state = new UserState();
        Check("Atomic state replacement waits for a transiently locked rollback file", () => AtomicWriteTests.Run(root));
        Check("Game removal deletes only contained targets and refuses escapes, roots and reparse points", () => GameRemovalTests.Run(root));
        Check("Search index matches the existing filter exactly while serving keystrokes from cache", () => SearchPerformanceTests.Run(root));
        Check("Non-game tags map only into the seven hidden categories and never into a visible one", () => TagTaxonomyTests.Run(root));
        Check("Completion percent and hours remaining are clamped, confident and never fabricated", () => CompletionProgressTests.Run(root));
        Check("Save-data discovery finds the real per-game path through registry, engine and known-folder layers", () => SaveDataLocatorTests.Run(root));
        Check("Backup snapshots a consistent save while the game runs and restore rolls back on failure", () => SaveRestoreCoordinatorTests.Run(root));
        Check("Speed ladder and F1/F2/F3 hotkeys act only on a positively identified running game", () => GameSpeedControllerTests.Run(root));
        Check("Native speed engine selects the matching architecture and re-bases the scaled clock without a jump", () => GameSpeedNativeTests.Run(root));
        Check("Install-job manifest reads survive concurrent replacement", () => InstallJobConcurrencyTests.Run(root));
        Check("Install progress parses live byte and file percentages", () => InstallProgressTests.Run(root));
        Check("Many games install in parallel with their own folders", () => ParallelInstallTests.Run(root));
        Check("Version-only Docker tags cannot impersonate numeric game titles", () => Require(VersionTagMetadataTests.Run(root) == 7, "Version-tag checks incomplete."));
        Check("Corroborated installed catalog titles reject unrelated metadata", () => InstalledCatalogTitleTests.Run(root));
        Check("Canonical source grouping preserves installed and newer published versions", () => GameIdentityTests.Run(root));
        Check("Duplicate display names collapse to the most recently pushed tag", () => NameDedupTests.Run(root));
        Check("Saved playable installations remain visible when their source registration disappears", () =>
        {
            string folder = Path.Combine(root, "orphan-installed", "Ashen");
            Directory.CreateDirectory(folder);
            string executable = Path.Combine(folder, "Ashen.exe");
            File.WriteAllText(executable, "fixture");
            var saved = new UserState();
            saved.InstalledGames.Add("Win64");
            saved.LaunchPaths["Win64"] = executable;
            saved.InstallationFolders["Win64"] = folder;
            saved.LaunchPaths["catalog-ashen"] = executable;
            saved.InstalledGames.Add("missing-file");
            saved.LaunchPaths["missing-file"] = Path.Combine(folder, "missing.exe");
            var config = new JsonObject { ["gameCategories"] = new JsonObject { ["Win64"] = "finished" } };
            var records = new List<Game> { new() { Id = "catalog-ashen", Name = "Ashen" } };
            Require(InstalledOrphanSources.Add(records, saved, config) == 1 && records.Count == 2,
                "The valid saved installation was lost or the missing executable became a card.");
            var orphan = records.Single(game => game.Id == "Win64");
            Require(orphan.Name == "Ashen" && orphan.Category == "finished" && orphan.IsLocal,
                "Saved identity, installation title, or protected category was lost.");
            Require(new GameIdentityIndex(records, saved).Groups().Count == 1,
                "An orphan and catalog source using the same verified executable became duplicate cards.");
            Require(InstalledOrphanSources.Add(records, saved, config) == 0,
                "Repeating the orphan projection duplicated a source record.");
        });
        Check("2D classification respects protected assignments, evidence, and undo", () => GameClassificationTests.Run(root));
        Check("Metadata evidence preserves scoped accepted values and audits mismatches", () => MetadataEvidenceTests.Run(root));
        Check("Maximum download size excludes unknown and oversized catalog entries", () =>
        {
            Require(MainWindow.WithinMaxDownloadSize(new Game { SizeGb = 2.5 }, 3), "Known in-range download was excluded.");
            Require(!MainWindow.WithinMaxDownloadSize(new Game { SizeGb = 0 }, 3), "Unknown size was treated as zero GB.");
            Require(!MainWindow.WithinMaxDownloadSize(new Game { SizeGb = 4 }, 3), "Oversized download was included.");
        });
        Check("Unknown pause state holds played time until a reliable sample returns", () =>
        {
            var timing = new ActivePlaytime(3600, 1000, 1000);
            timing.Sample(2000, paused: false);
            double known = timing.TotalSeconds;
            timing.Hold(5000);
            Require(timing.TotalSeconds == known, "Unknown pause interval increased played time.");
            timing.Sample(5000, paused: true);
            timing.Sample(8000, paused: true);
            Require(timing.TotalSeconds == known, "Paused interval increased played time.");
        });
        Check("Readable metadata queries preserve identity and retry semantics", () => ReadableMetadataTitleTests.Run(root));
        Check("Backup outcomes distinguish saved data, no saves, and failures", () =>
        {
            Require(GameSaveOperations.InterpretResult(0, "ASS_NO_SAVES Nothing to back up.", false, "proof").NoSaveData, "No-save outcome was lost.");
            Require(!GameSaveOperations.InterpretResult(0, "BACKUP_OK path=fixture", false, "proof").NoSaveData, "Completed backup was mislabeled.");
            Require(!GameSaveOperations.InterpretResult(0, "REASS_OK game=fixture", true, "proof").NoSaveData, "Completed restore was rejected.");
            foreach (var sample in new[] { (1, "ASS_NO_SAVES Nothing", false), (0, "", false),
                (0, "ASS_NO_SAVES Nothing", true), (0, "ASS_NO_SAVES Nothing\nBACKUP_OK path=x", false),
                (0, "Log text mentions BACKUP_OK path=x", false) })
            {
                bool rejected = false;
                try { GameSaveOperations.InterpretResult(sample.Item1, sample.Item2, sample.Item3, "proof"); }
                catch (IOException) { rejected = true; }
                Require(rejected, "Unconfirmed backup/restore outcome was accepted.");
            }
        });
        Check("No-save backup result requires a physical receipt for the selected executable", () =>
        {
            string backupRoot = Path.Combine(root, "receipt-fixture");
            string receiptDir = Path.Combine(backupRoot, "Fixture Game 1");
            Directory.CreateDirectory(receiptDir);
            string executable = Path.Combine(root, "Fixture Game.exe");
            File.WriteAllText(executable, "fixture");
            string receipt = Path.Combine(receiptDir, "backup.json");
            File.WriteAllText(receipt, System.Text.Json.JsonSerializer.Serialize(new
            {
                status = "no-saves", executable, files = 0, bytes = 0,
                fileEntries = Array.Empty<object>(), registryEntries = Array.Empty<object>()
            }));
            var result = GameSaveOperations.VerifyPhysicalReceipt(new GameSaveResult("proof", true),
                "ASS_NO_SAVES Selected game had no saves; record=" + receiptDir, executable, false, backupRoot);
            Require(result.NoSaveData && result.ReceiptPath == receipt, "The physical no-save receipt was not reported.");
            bool rejected = false;
            try { GameSaveOperations.VerifyPhysicalReceipt(new GameSaveResult("proof", true),
                "ASS_NO_SAVES Selected game had no saves; record=" + receiptDir, Path.Combine(root, "Other.exe"), false, backupRoot); }
            catch (IOException) { rejected = true; }
            Require(rejected, "A receipt for a different executable was accepted.");
        });
        Check("Save receipt cannot follow an NTFS link outside the backup root", () =>
        {
            string backupRoot = Path.Combine(root, "linked-receipt-fixture", "backups");
            string outside = Path.Combine(root, "linked-receipt-fixture", "outside");
            Directory.CreateDirectory(backupRoot);
            Directory.CreateDirectory(outside);
            string executable = Path.Combine(root, "Linked Game.exe");
            File.WriteAllText(executable, "fixture");
            File.WriteAllText(Path.Combine(outside, "backup.json"), System.Text.Json.JsonSerializer.Serialize(new
            {
                status = "no-saves", executable, files = 0, bytes = 0,
                fileEntries = Array.Empty<object>(), registryEntries = Array.Empty<object>()
            }));
            string linked = Path.Combine(backupRoot, "Linked Game 1");
            Directory.CreateSymbolicLink(linked, outside);
            bool rejected = false;
            try { GameSaveOperations.VerifyPhysicalReceipt(new GameSaveResult("proof", true),
                "ASS_NO_SAVES Selected game had no saves; record=" + linked, executable, false, backupRoot); }
            catch (IOException) { rejected = true; }
            Require(rejected, "A linked receipt outside the backup root was accepted.");
        });
        Check("Structured save results bind exit, game, operation, and verified outcome", () =>
        {
            string selected = Path.Combine(root, "Selected Game.exe");
            string other = Path.Combine(root, "Other Game.exe");
            string path = Path.Combine(root, "save-result.json");
            void Write(string executable, string outcome, string integrity = "verified") =>
                File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
                {
                    schemaVersion = 1, operation = "backup", selectedExecutable = executable,
                    outcome, integrity, receiptDirectory = root, backupManifest = Path.Combine(root, "backup.json")
                }));
            Write(selected, "no_saves");
            using (var accepted = GameSaveOperations.ReadStructuredResult(path, selected, false, false, 0))
                Require(accepted.RootElement.GetProperty("outcome").GetString() == "no_saves", "Verified no-save result was rejected.");
            foreach (var invalid in new[] { (other, "no_saves", "verified", 0),
                (selected, "failed", "verified", 0), (selected, "backed_up", "failed", 0),
                (selected, "backed_up", "verified", 1) })
            {
                Write(invalid.Item1, invalid.Item2, invalid.Item3);
                bool rejected = false;
                try { using var ignored = GameSaveOperations.ReadStructuredResult(path, selected, false, false, invalid.Item4); }
                catch (IOException) { rejected = true; }
                Require(rejected, "Inconsistent structured result was accepted.");
            }
        });
        Check("Structured save failures preserve helper diagnostics and operation log", () =>
            GameSaveOperationsFailureTests.Run(root));
        Check("Backup manifest payload hashes and selected executable are verified", () =>
        {
            string backupRoot = Path.Combine(root, "payload-receipt-fixture");
            string receiptDir = Path.Combine(backupRoot, "Saved Game 1");
            string payloadDir = Path.Combine(receiptDir, "SaveGames");
            Directory.CreateDirectory(payloadDir);
            string executable = Path.Combine(root, "Saved Game.exe");
            string payload = Path.Combine(payloadDir, "save.dat");
            File.WriteAllText(payload, "good save");
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(payload)));
            File.WriteAllText(Path.Combine(receiptDir, "backup.json"), System.Text.Json.JsonSerializer.Serialize(new
            {
                status = "complete", executable, files = 1, bytes = 9,
                fileEntries = new[] { new { path = @"SaveGames\save.dat", bytes = 9, sha256 = hash } },
                registryEntries = Array.Empty<object>()
            }));
            var result = GameSaveOperations.VerifyPhysicalReceipt(new GameSaveResult("proof"),
                "BACKUP_OK path=" + receiptDir + " files=1 bytes=9", executable, false, backupRoot);
            Require(result.BackupPath == receiptDir, "Verified backup receipt path was lost.");
            File.WriteAllText(payload, "evil save");
            bool rejected = false;
            try { GameSaveOperations.VerifyPhysicalReceipt(new GameSaveResult("proof"),
                "BACKUP_OK path=" + receiptDir + " files=1 bytes=9", executable, false, backupRoot); }
            catch (IOException) { rejected = true; }
            Require(rejected, "Modified save payload passed manifest verification.");
        });
        Check("Installer staging payloads never become installed library entries", () =>
        {
            string library = Path.Combine(root, "staging-scan");
            string stage = Path.Combine(library, DockerScripts.StagingDirectoryName, "unfinished-game");
            Directory.CreateDirectory(stage); File.WriteAllText(Path.Combine(stage, "Game.exe"), "partial payload");
            Require(InstalledScanner.Discover(library, Array.Empty<(string, string)>(), default).Games.Count == 0, "Partial staging payload became a local game.");
            Require(InstalledScanner.Discover(stage, Array.Empty<(string, string)>(), default).Games.Count == 0, "Direct staging scan became a local game.");
            string installed = Path.Combine(library, "Real Game"); Directory.CreateDirectory(installed);
            File.WriteAllText(Path.Combine(installed, "Game.exe"), "installed payload");
            Require(InstalledScanner.Discover(library, Array.Empty<(string, string)>(), default).Games.Single().Folder == installed, "A real sibling installation was lost.");
            var fixture = new UserState();
            string id = LocalGame.Identity(stage), realId = LocalGame.Identity(installed);
            fixture.LocalGames[id] = new LocalGame { Folder = stage }; fixture.InstalledGames.Add(id);
            fixture.LocalGames[realId] = new LocalGame { Folder = installed }; fixture.InstalledGames.Add(realId);
            Require(InstalledFolderMapping.RemoveStagingRegistrations(fixture) && !fixture.LocalGames.ContainsKey(id) && !fixture.InstalledGames.Contains(id)
                && fixture.LocalGames.ContainsKey(realId) && File.Exists(Path.Combine(stage, "Game.exe")), "Staging marker correction removed game files or a real registration.");
        });
        Check("Installed reconciliation removes only absent directories within the available scanned library", () =>
        {
            string library = Path.Combine(root, "reconcile-library"), other = Path.Combine(root, "reconcile-other");
            Directory.CreateDirectory(library); Directory.CreateDirectory(other);
            string existing = Path.Combine(library, "Existing"); Directory.CreateDirectory(existing);
            string externalExe = Path.Combine(other, "external.exe"); File.WriteAllText(externalExe, "fixture");
            var fixture = new UserState();
            foreach (string id in new[] { "removed", "alias", "outside", "existing", "manual", "unmapped", "local:removed" }) fixture.InstalledGames.Add(id);
            fixture.LaunchPaths["removed"] = Path.Combine(library, "Gone", "Game.exe");
            fixture.LaunchPaths["alias"] = Path.Combine(library, "Gone", "Bin", "Game.exe");
            fixture.LaunchPaths["outside"] = Path.Combine(other, "Missing", "Game.exe");
            fixture.LaunchPaths["existing"] = Path.Combine(existing, "Missing.exe");
            fixture.InstallationFolders["manual"] = Path.Combine(library, "GoneManual");
            fixture.LaunchPaths["manual"] = externalExe;
            fixture.LocalGames["local:removed"] = new LocalGame { Name = "Saved title", Folder = Path.Combine(library, "GoneLocal") };
            fixture.LocalCatalog["gameCategories"] = new JsonObject { ["removed"] = "Action" };
            fixture.PlayTimeSeconds["removed"] = 12345;
            var scan = InstalledScanner.Discover(library, Array.Empty<(string, string)>(), default);
            Require(InstalledFolderMapping.ReconcileMissing(fixture, scan), "Absent installations were retained.");
            Require(!fixture.InstalledGames.Contains("removed") && !fixture.InstalledGames.Contains("alias") && !fixture.InstalledGames.Contains("local:removed"), "Removed directory aliases remained installed.");
            Require(new[] { "outside", "existing", "manual", "unmapped" }.All(fixture.InstalledGames.Contains), "Unscanned, inaccessible-candidate or manually selected installations were erased.");
            Require((string?)fixture.LocalCatalog["gameCategories"]?["removed"] == "Action" && fixture.PlayTimeSeconds["removed"] == 12345 && fixture.LocalGames["local:removed"].Name == "Saved title" && fixture.LaunchPaths.ContainsKey("removed"), "Personal records were erased.");
            fixture.InstalledGames.Add("removed");
            Require(!InstalledFolderMapping.ReconcileMissing(fixture, new InstalledScanResult()), "A partial download scan removed installations.");
            Directory.Move(library, library + "-offline");
            Require(!InstalledFolderMapping.ReconcileMissing(fixture, scan) && fixture.InstalledGames.Contains("removed"), "An unavailable root was treated as an uninstall.");
        });
        Check("Exact scanner launcher matches recover alias install roots without guessing", () =>
        {
            string folder = Path.Combine(root, "folder-mapping"), nested = Path.Combine(folder, "Bin"), other = Path.Combine(root, "other-mapping");
            Directory.CreateDirectory(nested); Directory.CreateDirectory(other);
            string exe = Path.Combine(nested, "Game.exe"); File.WriteAllText(exe, "fixture");
            var mappingState = new UserState();
            mappingState.InstalledGames.UnionWith(new[] { "alias", "preserved", "missing" });
            mappingState.LaunchPaths["alias"] = exe; mappingState.LaunchPaths["preserved"] = exe;
            mappingState.LaunchPaths["missing"] = exe + ".missing"; mappingState.InstallationFolders["preserved"] = other;
            var scan = new InstalledScanResult(); scan.Games.Add(new("catalog", "Game", folder, exe, false));
            Require(InstalledFolderMapping.Apply(mappingState, scan) && mappingState.InstallationFolders["alias"] == folder, "Alias lost the full scanner-verified root.");
            Require(mappingState.InstallationFolders["preserved"] == other && !mappingState.InstallationFolders.ContainsKey("missing"), "Known or unavailable paths were overwritten.");
            Require(!InstalledFolderMapping.Apply(mappingState, scan), "Repeated scan changed stable mappings.");
            mappingState.InstallationFolders.Remove("alias"); scan.Games.Add(new("other", "Other", other, exe, false));
            Require(!InstalledFolderMapping.Apply(mappingState, scan) && !mappingState.InstallationFolders.ContainsKey("alias"), "Ambiguous folder match was guessed.");
        });
        Check("Independent-source synchronization survives outages and preserves queues (34 scenarios)", () => Require(SyncResilienceTests.Run(root) == 34, "Incomplete resilience coverage."));
        Check("Custom saved launchers recover exact catalog roots without becoming scan candidates", () =>
        {
            string scanRoot = Path.Combine(root, "custom-launcher-root");
            string folder = Path.Combine(scanRoot, "custom-game"); Directory.CreateDirectory(folder);
            string launcher = Path.Combine(folder, "Launcher.exe"); File.WriteAllText(launcher, "fixture");
            var scan = InstalledScanner.Discover(scanRoot, new[] { ("custom-game", "Custom Game") }, default);
            Require(scan.Games.Count == 0 && scan.ExecutableFolders.Count == 0, "Custom launcher was automatically selected.");
            var known = new UserState(); known.InstalledGames.Add("custom-game"); known.LaunchPaths["custom-game"] = launcher;
            Require(InstalledFolderMapping.Apply(known, scan) && known.InstallationFolders["custom-game"] == folder, "Explicit custom launcher root was lost.");
            known.InstallationFolders.Clear();
            string outside = Path.Combine(scanRoot, "Launcher.exe"); File.WriteAllText(outside, "fixture"); known.LaunchPaths["custom-game"] = outside;
            Require(!InstalledFolderMapping.Apply(known, scan), "Outside custom launcher mapped into unrelated folder.");
            known.LaunchPaths["custom-game"] = launcher;
            scan.ExplicitCatalogFolders.Add(("custom-game", scanRoot));
            Require(!InstalledFolderMapping.Apply(known, scan), "Conflicting custom roots guessed.");
        });
        Check("Ambiguous catalog names retain folder evidence for explicit launchers only", () =>
        {
            string scanRoot = Path.Combine(root, "ambiguous-folder-evidence");
            string folder = Path.Combine(scanRoot, "Same Game");
            string bin = Path.Combine(folder, "Bin"); Directory.CreateDirectory(bin);
            string executable = Path.Combine(bin, "SameGame.exe"); File.WriteAllText(executable, "fixture");
            var scan = InstalledScanner.Discover(scanRoot, new[] { ("samegame", "Same Game"), ("SameGame", "Same Game") }, default);
            Require(scan.Games.Count == 0 && scan.ExecutableFolders.Any(e => e.Launcher == executable && e.Folder == folder), "Ambiguity discarded filesystem evidence or guessed game identity.");
            var known = new UserState(); known.InstalledGames.UnionWith(new[] { "samegame", "SameGame", "unselected" });
            known.LaunchPaths["samegame"] = executable; known.LaunchPaths["SameGame"] = executable;
            Require(InstalledFolderMapping.Apply(known, scan) && known.InstallationFolders["samegame"] == folder &&
                known.InstallationFolders["SameGame"] == folder && !known.InstallationFolders.ContainsKey("unselected"), "Explicit launcher mapping lost or unselected game mapped.");
            known.InstallationFolders.Clear(); scan.ExecutableFolders.Add((executable, scanRoot));
            Require(!InstalledFolderMapping.Apply(known, scan), "Conflicting folder evidence was guessed.");
        });
        Check("Installed file storage measurement (22 scenarios)", () => Require(InstalledStorageTests.Run(root) == 22, "Incomplete storage coverage."));
        Check("Persistent installed-storage receipts (10 scenarios)", () => Require(InstalledStoragePersistenceTests.Run(root) == 10, "Incomplete installed-storage persistence coverage."));
        Check("Storage retries grow within bounds and aliases share measured folders", () =>
        {
            var cache = new InstalledStorageCache(); string folder = Path.Combine(root, "storage-cache");
            var now = DateTime.UtcNow;
            var partial = new InstalledStorageResult(12, false, now, 1, 0, "Time limit reached");
            Require(cache.Budget(folder).TotalSeconds == 30 && cache.Fresh(folder, now) == null, "Initial budget invalid.");
            cache.Record(folder, partial, cache.Budget(folder));
            Require(cache.Budget(folder).TotalSeconds == 60 && cache.Fresh(folder.ToUpperInvariant() + Path.DirectorySeparatorChar, now.AddSeconds(59)) == partial,
                "Alias measured again or timeout retry failed to grow.");
            Require(cache.Fresh(folder, now.AddMinutes(1)) == null && cache.Fresh(folder + "-other", now) == null, "Retry delayed or unrelated folder reused.");
            foreach (int seconds in new[] { 120, 240, 300, 300 })
            { cache.Record(folder, partial, cache.Budget(folder)); Require(cache.Budget(folder).TotalSeconds == seconds, "Retry budget not bounded."); }
            var complete = partial with { Complete = true, Reason = null };
            cache.Record(folder, complete, cache.Budget(folder));
            Require(cache.Budget(folder).TotalSeconds == 300 && cache.Fresh(folder, now.AddMinutes(9)) == complete && cache.Fresh(folder, now.AddMinutes(10)) == null,
                "Complete measurements lost their proven budget or failed to expire.");
            cache.Record(folder, partial with { Reason = "Access denied" }, TimeSpan.FromSeconds(30));
            Require(cache.Budget(folder).TotalSeconds == 30 && cache.Fresh(folder, now.AddMinutes(2)) != null, "Non-timeout errors cause aggressive retries.");
        });
        Check("Independent synchronization retry backoff (23 scenarios)", () => Require(SyncBackoffTests.Run(root) == 23, "Incomplete backoff coverage."));
        Check("Read-only shared fallback and polling cadence (12 scenarios)", () => Require(SharedFallbackTests.Run(root) == 12, "Incomplete shared fallback coverage."));
        Check("Namespace enumeration retains complete caches (29 scenarios)", () => Require(DockerNamespaceTests.Run(root) == 29, "Incomplete namespace coverage."));
        Check("Repository-qualified game identities (9 scenarios)", () => Require(DockerIdentityTests.Run(root) == 9, "Incomplete identity coverage."));
        Check("Metadata outage cooldown is shared across games and clients (8 scenarios)", () => Require(MetadataAvailabilityTests.Run(root) == 8, "Incomplete metadata outage coverage."));
        Check("Windows catalog edits survive restart and storage failure (7 scenarios)", () => Require(LocalCatalogTests.Run(root) == 7, "Incomplete local catalog coverage."));
        Check("Native pause suspends a real process tree, resumes it, rejects stale identity, and recovers on disconnect", () => GamePauseProof.Run(root));
        Check("Save backup queue persists exact executable identity across restart", () =>
        {
            var fixtureStore = new LibraryStore(Path.Combine(root, "save-queue"));
            var fixtureState = new UserState();
            fixtureState.PendingGameBackups["CaseSensitiveTag"] = @"E:\games\fixture\Game.exe";
            fixtureState.InstallationFolders["CaseSensitiveTag"] = @"E:\games\fixture";
            fixtureStore.Save(fixtureState);
            Require(fixtureStore.LoadState().PendingGameBackups["CaseSensitiveTag"] == @"E:\games\fixture\Game.exe", "A pending exact-game backup was lost.");
            Require(fixtureStore.LoadState().InstallationFolders["CaseSensitiveTag"] == @"E:\games\fixture", "A verified installation root was lost.");
            Reject(() => GameSaveOperations.ValidateExecutable("game.exe"));
            Reject(() => GameSaveOperations.ValidateExecutable(@"E:\games\fixture\script.ps1"));
        });
        Check("Game progress preserves unknown values, validates numbers, and displays shared estimates", () =>
        {
            string backupRoot = Path.Combine(root, "progress-backups"), backup = Path.Combine(backupRoot, "fixture");
            Directory.CreateDirectory(backup);
            File.WriteAllText(Path.Combine(backup, "backup.json"), "{\"executable\":\"E:\\\\games\\\\fixture\\\\Game.exe\"}");
            var row = new JsonObject { ["schema_version"] = 2, ["game"] = "Fixture", ["backup"] = new JsonObject { ["path"] = backup },
                ["progress"] = new JsonObject { ["frac"] = null, ["source"] = "unknown" },
                ["styles"] = new JsonObject { ["main"] = new JsonObject { ["left_avg"] = null } } };
            var rows = new JsonArray(row);
            var missing = GameProgressClient.Parse(rows.ToJsonString(), backupRoot).Single();
            Require(missing.Label == "No verified campaign percentage · Remaining hours need campaign data"
                && !missing.Detail.Contains("unknown", StringComparison.OrdinalIgnoreCase), "Missing progress became a number or leaked an unexplained sentinel.");
            row["progress"]!["frac"] = 0.625; row["styles"]!["main"]!["left_avg"] = 7.5;
            Require(GameProgressClient.Parse(rows.ToJsonString(), backupRoot).Single().Label == "~62.500% estimated · ~7.5 h remaining", "Shared numeric estimates changed.");
            row["progress"]!["frac"] = "bad-data";
            Require(GameProgressClient.Parse(rows.ToJsonString(), backupRoot).Single().Label.StartsWith("No verified campaign percentage", StringComparison.Ordinal), "Malformed progress became zero.");
            row["schema_version"] = 3;
            row["scopes"] = new JsonObject
            {
                ["main_story"] = new JsonObject
                {
                    ["status"] = "unavailable", ["fraction"] = null,
                    ["basis"] = "no validated campaign denominator",
                    ["availability_reason"] = new JsonObject
                    {
                        ["code"] = "main_story_progress_unavailable",
                        ["missing"] = new JsonArray(new JsonObject
                        {
                            ["item"] = "validated_main_story_denominator", ["reason"] = "campaign_total_not_verified"
                        })
                    },
                    ["evidence"] = new JsonObject
                    {
                        ["backup"] = new JsonObject { ["files"] = new JsonArray(new JsonObject { ["path"] = "save.sav" }) },
                        ["active_profile"] = new JsonObject { ["status"] = "unresolved" }
                    }
                },
                ["overall_completion"] = new JsonObject
                {
                    ["status"] = "unavailable", ["fraction"] = null,
                    ["basis"] = "no independently verified full-completion criteria",
                    ["availability_reason"] = new JsonObject
                    {
                        ["code"] = "overall_completion_criteria_unverified",
                        ["missing"] = new JsonArray(new JsonObject
                        {
                            ["item"] = "full_game_completion_decoder", ["reason"] = "not_available"
                        })
                    }
                }
            };
            var v3 = GameProgressClient.Parse(rows.ToJsonString(), backupRoot).Single();
            Require(v3.Label == "Main story unavailable · Overall unavailable"
                && v3.Detail.Contains("validated_main_story_denominator: campaign_total_not_verified", StringComparison.Ordinal)
                && v3.Detail.Contains("full_game_completion_decoder: not_available", StringComparison.Ordinal)
                && v3.Detail.Contains("Manifest files: 1; Active profile: unresolved", StringComparison.Ordinal),
                "Version-3 coverage reasons or verified manifest evidence disappeared from native details.");
        });
        Check("Default profile resolves beside the distribution on the bundle drive", () =>
        {
            var expected = Path.Combine(root, "data");
            var resolved = LibraryStore.ResolveDefaultRoot(Path.Combine(root, "dist"));
            Require(string.Equals(resolved, expected, StringComparison.OrdinalIgnoreCase), "The default profile did not resolve beside dist.");
            Require(LibraryStore.ResolveDefaultRoot(Path.Combine(root, "native", "dist")) == expected, "Windows distribution abandoned the original repository profile.");
            Require(LibraryStore.ResolveDefaultRoot(Path.Combine(root, "NATIVE", "DIST")) == expected, "Windows profile resolution depends on path casing.");
            var explicitStore = new LibraryStore(Path.Combine(root, "explicit-profile"));
            Require(explicitStore.Root == Path.Combine(root, "explicit-profile"), "Explicit test profile was redirected.");
            Require(!resolved.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameLibraryManager"), StringComparison.OrdinalIgnoreCase), "The default profile still targets LocalApplicationData.");
        });
        Check("Healthy repository updates survive sibling failure, restart and later complete recovery", () =>
        {
            const string owner = "michadockermisha";
            JsonObject Row(string repo, string tag, int hour, bool stale = false) => new()
            {
                ["repository"] = owner + "/" + repo, ["complete"] = true, ["stale"] = stale, ["count"] = 1,
                ["fetchedAt"] = $"2026-09-22T{hour:00}:00:00Z", ["tags"] = new JsonArray(new JsonObject { ["name"] = tag, ["full_size"] = hour * 1000000000L })
            };
            JsonObject Scan(params JsonObject[] rows) => new() { ["namespace"] = owner, ["complete"] = false,
                ["repositories"] = new JsonArray(rows.Select(x => (JsonNode)x).ToArray()) };
            var baseline = Scan(Row("backup", "old-proof", 1));
            baseline["complete"] = true; baseline["success"] = true; baseline["repositoryCount"] = 1; baseline["fetchedAt"] = "2026-09-22T01:00:00Z";
            var damaged = Row("bad", "partial-proof", 2); damaged["count"] = 2;
            var update = NamespaceUpdates.Combine(owner, baseline, Scan(Row("backup", "old-proof", 1, true), Row("healthy", "new-proof", 2), damaged, Row("old", "stale-proof", 1)));
            Require(DataJson.Number(update["repositoryCount"]) == 1 && update["complete"]!.ToString() == "false", "Incomplete scan was promoted or unverified rows exposed.");
            var games = new List<Game>(); LibraryStore.MergeNamespace(games, baseline); NamespaceUpdates.Apply(games, update);
            Require(games.Any(g => g.Id == "old-proof") && games.Any(g => g.Id == DockerIdentity.Create(owner + "/healthy", "new-proof")), "Healthy additions or failed repository's prior game lost.");
            var repeat = NamespaceUpdates.Combine(owner, baseline, update, Scan(Row("healthy", "older-proof", 1), Row("backup", "old-proof", 1, true)));
            Require(JsonNode.DeepEquals(update, repeat), "Repeated outage regressed verified updates.");
            var other = Scan(Row("healthy", "foreign-proof", 3)); other["namespace"] = "other";
            Require(DataJson.Number(NamespaceUpdates.Combine(owner, baseline, other)["repositoryCount"]) == 0, "Another account's update leaked.");
            var profile = new LibraryStore(Path.Combine(root, "partial-namespace-profile"));
            foreach (string file in new[] { "games.json", "times.json", "image-sizes.json", "dates-added.json", "tabs.json" })
                profile.CacheData(file, file is "games.json" or "tabs.json" ? "[]" : "{}");
            profile.CacheData("docker-namespace-catalog.json", baseline.ToJsonString()); profile.CacheData("docker-namespace-updates.json", update.ToJsonString());
            var preferences = new UserState(); preferences.Settings.DockerUsername = owner;
            var loaded = new LibraryStore(profile.Root).LoadGames(preferences, new JsonObject());
            Require(loaded.Any(g => g.Id == DockerIdentity.Create(owner + "/healthy", "new-proof")), "Restart discarded healthy partial updates.");
            var priorityUpdate = NamespaceUpdates.Combine(owner, baseline, Scan(Row("backup", "old-proof", 4)));
            profile.CacheData("docker-namespace-updates.json", priorityUpdate.ToJsonString());
            var direct = Row("backup", "old-proof", 3); direct["success"] = true;
            profile.CacheData("docker-tags.json", direct.ToJsonString());
            preferences.Settings.RepoName = "backup";
            Require(profile.LoadGames(preferences, new JsonObject()).Single(g => g.Id == "old-proof").SizeGb == 4, "Older direct-priority cache overwrote a fresh repository update.");
            direct = Row("backup", "old-proof", 5); direct["success"] = true;
            profile.CacheData("docker-tags.json", direct.ToJsonString());
            Require(profile.LoadGames(preferences, new JsonObject()).Single(g => g.Id == "old-proof").SizeGb == 5, "Old partial update overwrote newer direct-priority data.");
            baseline["fetchedAt"] = "2026-09-22T03:00:00Z";
            Require(DataJson.Number(NamespaceUpdates.Combine(owner, baseline, update)["repositoryCount"]) == 0, "Old sidecar overrode a later complete scan.");
            Require(DataJson.Number(NamespaceUpdates.Combine(owner, null, update)["repositoryCount"]) == 1, "First incomplete scan hid healthy repositories without a baseline.");
        });
        Check("Docker login reuses encrypted PowerShell credentials and rejects damaged records", () =>
        {
            string directory = Path.Combine(root, "docker-credential-fixture"); Directory.CreateDirectory(directory);
            string bad = Path.Combine(directory, "bad.dpapi"), good = Path.Combine(directory, "good.dpapi");
            File.WriteAllText(bad, "damaged record");
            var encrypted = System.Security.Cryptography.ProtectedData.Protect(Encoding.Unicode.GetBytes("fixture-token"), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            string record = Convert.ToHexString(encrypted);
            File.WriteAllText(good, record);
            var credential = DockerHubAccess.ReadSavedCredential(new[] { bad + ".missing", bad, good });
            Require(credential?.Username == "michadockermisha" && credential?.Secret == "fixture-token", "Encrypted script credential was not recovered after invalid candidates.");
            Require(File.ReadAllText(good) == record && File.ReadAllText(bad) == "damaged record", "Reading credentials modified their storage.");
            File.WriteAllText(good, Convert.ToHexString(System.Security.Cryptography.ProtectedData.Protect(Encoding.Unicode.GetBytes("bad\nrecord"), null, System.Security.Cryptography.DataProtectionScope.CurrentUser)));
            Require(DockerHubAccess.ReadSavedCredential(new[] { bad, good }) == null, "Invalid credential was accepted.");
        });
        Check("Docker token exchange uses the supported API and sanitized authentication failures", () =>
        {
            using var handler = new DockerLoginFixtureHandler();
            using var http = new HttpClient(handler);
            Require(DockerHubAccess.Exchange(http, "fixture-user", "fixture-secret", default).GetAwaiter().GetResult() == "fixture-jwt" && handler.ValidRequest, "Supported token exchange contract was not followed.");
            handler.Reject = true;
            bool rejected = false;
            try { DockerHubAccess.Exchange(http, "fixture-user", "fixture-secret", default).GetAwaiter().GetResult(); }
            catch (DockerAuthenticationException ex) { rejected = ex.StatusCode == HttpStatusCode.Unauthorized && !ex.Message.Contains("fixture-secret", StringComparison.Ordinal); }
            Require(rejected, "Authentication failure lost its status or exposed the response body.");
        });
        Check("Sync distinguishes missing Docker credentials and shared-service quota without leaking credentials", () =>
        {
            var auth = new DockerAuthenticationException("fixture secret must not appear");
            Require(SyncClient.FailureSummary(auth) == "Docker sign-in required", "Missing credentials were reported as a network failure.");
            Require(SyncClient.FailureSummary(new DockerAuthenticationException("temporary login service outage", System.Net.HttpStatusCode.ServiceUnavailable)) == "HTTP 503", "An authentication-service outage was misreported as a missing sign-in.");
            using var http = new System.Net.Http.HttpClient(new OfflineNetworkGuard());
            var client = new DockerNamespaceClient(http, _ => System.Threading.Tasks.Task.FromException<string>(auth), Path.Combine(root, "auth-error-proof"));
            var snapshot = client.ReadAsync("fixture", "backup", default).GetAwaiter().GetResult();
            Require(snapshot["complete"]!.GetValue<bool>() == false && DataJson.Text(snapshot["errors"]?[0]?["code"]) == "authentication-required", "Namespace discarded the authentication failure type.");
            Require(!snapshot.ToJsonString().Contains("fixture secret", StringComparison.Ordinal) && MainWindow.NamespaceFailureStatus(snapshot).Contains("sign-in required", StringComparison.Ordinal), "Namespace diagnostic exposed credentials or hid the required sign-in.");
            using var response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable) { Content = new System.Net.Http.StringContent("{\"error\":\"usage_exceeded\",\"message\":\"untrusted body\"}") };
            bool quota = false;
            try { SyncClient.EnsureSharedResponse(response, default).GetAwaiter().GetResult(); }
            catch (SharedQuotaException ex) { quota = SyncClient.FailureSummary(ex).Contains("quota exceeded", StringComparison.Ordinal) && !ex.Message.Contains("untrusted", StringComparison.Ordinal); }
            Require(quota, "Shared quota failure was hidden or echoed an untrusted response.");
            using var ordinary = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable) { Content = new System.Net.Http.StringContent("service temporarily unavailable") };
            bool rejected = false;
            try { SyncClient.EnsureSharedResponse(ordinary, default).GetAwaiter().GetResult(); }
            catch (System.Net.Http.HttpRequestException ex) { rejected = ex is not SharedQuotaException && ex.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable; }
            Require(rejected, "An unrelated503was classified as quota exhaustion.");
        });
        Check("Original personal history and legacy fields survive release reloads and remote category changes", () =>
        {
            var profile = new LibraryStore(Path.Combine(root, "recovered-profile"));
            var recovered = DataJson.Read<UserState>("{\"schemaVersion\":1,\"installedBytes\":{\"007firstlight\":12345},\"playTimeSeconds\":{\"007firstlight\":43478.833970300046},\"lastPlayedUtc\":{\"007firstlight\":\"2026-09-21T20:11:19.5636041Z\"},\"localCatalog\":{\"gameCategories\":{\"007firstlight\":\"action\"},\"tabs\":[{\"id\":\"action\",\"name\":\"Action\"}]}}");
            using var sync = new SyncClient(profile, new OfflineNetworkGuard());
            recovered.LocalCatalog["gameCategories"]!["AgainsttheStorm"] = "action";
            recovered.LocalCatalog["gameCategories"]!["againstthestorm"] = "strategy";
            for (int release = 0; release < 3; release++)
            {
                profile.Save(recovered);
                recovered = profile.LoadState();
                Require(profile.RecoveryNotice == null && DataJson.Text(recovered.LocalCatalog["gameCategories"]?["AgainsttheStorm"]) == "action"
                    && DataJson.Text(recovered.LocalCatalog["gameCategories"]?["againstthestorm"]) == "strategy", "Case-distinct game IDs rejected a valid personal profile.");
                sync.Remote["gameCategories"] = new JsonObject { ["007firstlight"] = "hyperv" };
                Require(DataJson.Text(sync.Effective(recovered)["gameCategories"]?["007firstlight"]) == "action", "Remote refresh replaced recovered Action category.");
                Require(recovered.PlayTimeSeconds["007firstlight"] == 43478.833970300046 && recovered.LastPlayedUtc["007firstlight"].Year == 2026, "Release reload lost recovered history.");
                Require(recovered.AdditionalData!["installedBytes"].GetProperty("007firstlight").GetInt64() == 12345, "Release erased a legacy profile field.");
            }
        });
        Check("Windowless diagnostic failures return a report without escaping", () =>
        {
            string diagnosticReport = Path.Combine(root, "diagnostic-failure.json");
            int result = Program.RunDiagnostic(diagnosticReport, () => throw new DirectoryNotFoundException("Missing fixture catalog"));
            var failure = JsonNode.Parse(File.ReadAllText(diagnosticReport))!;
            Require(result == 1 && failure["passed"]!.GetValue<bool>() == false && DataJson.Text(failure["error"]).Contains("Missing fixture catalog"), "Diagnostic failure escaped or was reported as successful.");
            Require(Program.RunDiagnostic(root, () => throw new IOException("Unwritable report fixture")) == 1, "An unwritable report escaped the diagnostic boundary.");
        });
        Check("Packaged catalog extraction", () => { store.EnsureAssets(); Require(File.Exists(Path.Combine(store.Assets, "data", "games.json")), "Missing data."); });
        Check("Packaged catalog and every cover remain available offline", () =>
        {
            var games = store.LoadGames(state, store.ReadConfig());
            Require(games.Count >= 1179, "Incomplete catalog.");
            Require(Directory.EnumerateFiles(Path.Combine(store.Assets, "images"), "*", SearchOption.AllDirectories).Count() >= 2028, "Incomplete covers.");
            Require(games.Select(g => g.Id).Distinct(StringComparer.Ordinal).Count() == games.Count, "Duplicate exact identities.");
        });
        Check("Packaged supported Wand registrations remain available without a profile dependency", () =>
        {
            string manifest = Path.Combine(AppContext.BaseDirectory, "tools", "wand-supported-games.json");
            Require(File.Exists(manifest), "The bundled Wand registration manifest is missing.");
            Require(JsonNode.Parse(File.ReadAllText(manifest)) is JsonArray registrations && registrations.Count == 39
                && registrations.All(row => row is JsonObject record
                    && DataJson.Text(record["titleId"]).Length > 0
                    && DataJson.Text(record["gameId"]).Length > 0
                    && DataJson.Text(record["path"]).StartsWith(@"E:\games\", StringComparison.OrdinalIgnoreCase)),
                "The bundled Wand registration manifest is incomplete or invalid.");
            var supported = WandIntegration.LoadSupportedGames(new[] { manifest });
            Require(supported.Count == 39 && supported.Select(game => game.GameId).Distinct(StringComparer.Ordinal).Count() == 39,
                "The runtime Wand manifest loader did not preserve all 39 unique registrations.");
        });
        Check("Wand registration discovery retains cached rows and eventually retries a new database root", () =>
        {
            string roaming = Path.Combine(root, "wand-refresh-discovery-" + Guid.NewGuid().ToString("N"));
            var refresh = new WandRegistrationRefreshState<string>();
            refresh.Publish(new[] { "existing-registration" });
            DateTime now = DateTime.UtcNow;
            int attempts = 0;

            IReadOnlyList<string> TryRefresh(DateTime attemptUtc, bool failRead = false)
            {
                bool loaded = refresh.TryLoad(attemptUtc, () =>
                {
                    attempts++;
                    string[] roots = WandLiveLibrary.DiscoverLevelDbRoots(roaming);
                    if (roots.Length == 0) throw new IOException("The registration root is not available yet.");
                    if (failRead) throw new IOException("The registration snapshot is temporarily unreadable.");
                    return new[] { "existing-registration", "new-registration" };
                }, out var rows, out _);
                if (loaded) refresh.RecordSuccess(rows);
                return loaded ? rows : refresh.Rows;
            }

            Require(WandLiveLibrary.DiscoverLevelDbRoots(roaming).Length == 0,
                "A missing Wand database root was reported as present.");
            Require(TryRefresh(now).SequenceEqual(new[] { "existing-registration" }) && attempts == 1,
                "A missing database root discarded cached registrations or did not schedule retry state.");

            string levelDb = Path.Combine(roaming, "Wand", "Local Storage", "leveldb");
            Directory.CreateDirectory(levelDb);
            File.WriteAllText(Path.Combine(levelDb, "000001.ldb"), "fixture");
            Require(WandLiveLibrary.DiscoverLevelDbRoots(roaming).SequenceEqual(new[] { levelDb }, StringComparer.OrdinalIgnoreCase),
                "A newly created Wand database root was not discovered on the next scan.");
            Require(TryRefresh(now.AddMilliseconds(500), failRead: true).SequenceEqual(new[] { "existing-registration" }) && attempts == 1,
                "A refresh bypassed the bounded retry delay or lost cached registrations.");
            Require(TryRefresh(now.AddSeconds(1), failRead: true).SequenceEqual(new[] { "existing-registration" }) && attempts == 2,
                "A readable root whose snapshot failed did not preserve cached rows.");
            Require(TryRefresh(now.AddSeconds(2), failRead: true).SequenceEqual(new[] { "existing-registration" }) && attempts == 2,
                "A repeated failed read was retried before its backoff elapsed.");
            Require(TryRefresh(now.AddSeconds(3)).SequenceEqual(new[] { "existing-registration", "new-registration" })
                && refresh.ConsecutiveFailures == 0 && attempts == 3,
                "A newly readable snapshot did not eventually replace cached registrations and reset retry state.");

            var capped = new WandRegistrationRefreshState<string>();
            DateTime retryUtc = now;
            TimeSpan longest = TimeSpan.Zero;
            for (int i = 0; i < 10; i++)
            {
                TimeSpan delay = capped.RecordFailure(retryUtc);
                if (delay > longest) longest = delay;
                retryUtc += delay;
            }
            Require(capped.ConsecutiveFailures == 6 && longest == TimeSpan.FromSeconds(30)
                && capped.CanAttempt(retryUtc),
                "Repeated registration failures did not use a capped retry cadence.");
        });
        Check("Command launchers track their exact child executable", () =>
        {
            string launcherRoot = Path.Combine(root, "wand-command-launcher");
            string emulatorRoot = Path.Combine(launcherRoot, "emu");
            Directory.CreateDirectory(emulatorRoot);
            string emulator = Path.Combine(emulatorRoot, "Ryujinx.exe");
            File.WriteAllBytes(emulator, new byte[] { 77, 90 });
            string launcher = Path.Combine(launcherRoot, "Ryujinx.bat");
            File.WriteAllText(launcher, "cd emu" + Environment.NewLine + "Ryujinx.exe -r ..\\Data" + Environment.NewLine + "cd ..");
            Require(string.Equals(WandIntegration.ResolveTrackedExecutable(launcher), emulator, StringComparison.OrdinalIgnoreCase),
                "The registered command launcher was not mapped to its exact runtime executable.");
        });
        Check("Exact Wand registrations resolve catalog ids and nested runtime executables without title guessing", () =>
        {
            var registration = new WandRegisteredInstallation("100", "200", @"C:\Games\Proof\Launcher.exe");
            var catalog = JsonNode.Parse("{\"titles\":{\"100\":{\"id\":\"100\",\"name\":\"Proof Game\",\"gameIds\":[\"200\"]}},\"games\":{\"200\":{\"titleId\":\"100\",\"platformId\":\"steam\",\"versionPath\":\"Proof\\\\Binaries\\\\ProofGame.exe\"}}}")!.AsObject();
            Require(WandIntegration.TryResolveRegisteredTarget(catalog, registration, out var target)
                && target.TitleId == "100" && target.GameId == "200", "The exact saved Wand ids were not accepted from the matching catalog records.");
            string install = Path.Combine(root, "nested-runtime-proof");
            string launcher = Path.Combine(install, "Launcher.exe");
            string runtime = Path.Combine(install, "Proof", "Binaries", "ProofGame.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(runtime)!);
            File.WriteAllBytes(launcher, new byte[] { 77, 90 });
            File.WriteAllBytes(runtime, new byte[] { 77, 90 });
            Require(string.Equals(WandIntegration.ResolveTrackedExecutable(launcher, target.VersionPath), runtime, StringComparison.OrdinalIgnoreCase),
                "A registered root launcher did not track the catalog's exact nested process.");
        });
        Check("Wand library matching rejects same-folder title collisions", () =>
        {
            var registration = new WandSupportedGame("tailsofiron", "53336", "57242", "Tails of Iron", @"E:\games\tailsofiron\TOI.exe");
            var wrongFolderMatch = new Game { Id = "tailsofiron", Name = "Tails of Iron 2: Whiskers of Winter" };
            var correctTitle = new Game { Id = "TailsofIron", Name = "Tails of Iron" };
            Require(MainWindow.WandLibraryMatchScore(wrongFolderMatch, registration, null) == 0
                && MainWindow.WandLibraryMatchScore(correctTitle, registration, null) > 0,
                "A folder-id collision could attach Wand to the wrong game card.");
        });
        Check("Activity logging never blocks the caller on an unavailable log file", () =>
        {
            string logRoot = Path.Combine(root, "unavailable-log-proof");
            var logStore = new LibraryStore(logRoot);
            Directory.CreateDirectory(Path.Combine(logRoot, "activity.log"));
            var timer = Stopwatch.StartNew();
            logStore.Log("This write is expected to fail on the background writer.");
            timer.Stop();
            Require(timer.Elapsed < TimeSpan.FromMilliseconds(500), "Activity logging blocked its caller.");
        });
        Check("Packaged Wand same-route recovery remains self-contained", () =>
        {
            string launcher = Path.Combine(AppContext.BaseDirectory, "tools", "wand_cdp_launch.js");
            string trainerProbe = Path.Combine(AppContext.BaseDirectory, "tools", "wand_trainer_status.js");
            string trainerTrace = Path.Combine(AppContext.BaseDirectory, "tools", "wand_tophat_evidence.js");
            string node = Path.Combine(AppContext.BaseDirectory, "tools", "node", "node.exe");
            Require(File.Exists(launcher) && File.Exists(trainerProbe) && File.Exists(trainerTrace) && File.Exists(node),
                "The bundled Wand CDP recovery or trainer-evidence runtime is missing.");
            string source = File.ReadAllText(launcher);
            Require(source.Contains("launchNonce", StringComparison.Ordinal) && source.Contains("127.0.0.1:9222", StringComparison.Ordinal), "The Wand CDP recovery route lost its unique loopback-only launch behavior.");
            Require(source.Contains("process.argv[2] === '--preflight'", StringComparison.Ordinal)
                && source.Contains("inspectPreNavigationState", StringComparison.Ordinal)
                && source.Contains("trainer-state-unavailable-before-navigation", StringComparison.Ordinal),
                "The packaged Wand protocol preflight lost its fail-closed, non-mutating safety mode.");
            string traceSource = File.ReadAllText(trainerTrace);
            Require(traceSource.Contains("parseLengthDelimitedFrames", StringComparison.Ordinal)
                && traceSource.Contains("trainer_run_mod_json", StringComparison.Ordinal),
                "The packaged Wand trainer evidence adapter is missing its framed Tophat command contract.");
        });
        Check("Wand active trainer and exited Tophat evidence remain distinct", () =>
        {
            const string gameId = "57393";
            const int processId = 19296;
            DateTime now = DateTime.UtcNow;
            DateTime processStarted = now.AddMinutes(-3);
            string creation = processStarted.ToFileTimeUtc().ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
            DateTime attemptStarted = now.AddSeconds(-5);
            var activeJson = new JsonObject
            {
                ["connected"] = true,
                ["reason"] = "trainer-active",
                ["gameId"] = gameId,
                ["processId"] = processId,
                ["observedUtc"] = now.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["trainerTrace"] = new JsonObject { ["trainerObserved"] = false }
            };
            var active = WandTrainerEvidenceAdapter.InterpretProbeOutput(activeJson.ToJsonString(), gameId,
                processId, creation, attemptStarted, "DELTARUNE", true, true);
            Require(active.Confirmed && active.Source == "wand-12.58.0-trainer-vm",
                "Current trainer state did not confirm the exact live PID and creation identity.");
            var staleIdentity = WandTrainerEvidenceAdapter.InterpretProbeOutput(activeJson.ToJsonString(), gameId,
                processId, creation, attemptStarted, "DELTARUNE", false, true);
            Require(!staleIdentity.Confirmed && WandIntegration.StatusForTrainerEvidence(staleIdentity) != WandSessionStatus.Connected,
                "Active sidebar state was accepted without an exact process creation match.");
            Require(WandIntegration.StatusForTrainerEvidence(new WandTrainerEvidence(false, "game-process-changed", now,
                    "The exact game process exited before attachment was verified.")) == WandSessionStatus.Disconnected
                && WandIntegration.StatusForTrainerEvidence(new WandTrainerEvidence(false, "game-pid-reused", now,
                    "The observed PID belongs to a different process creation identity.")) == WandSessionStatus.Disconnected,
                "Exited or replaced game processes were reported as still running after the bounded attachment wait.");

            DateTime sessionStarted = processStarted.AddSeconds(10);
            DateTime pluginLoaded = sessionStarted.AddSeconds(2);
            DateTime commandObserved = sessionStarted.AddSeconds(3);
            var exitedJson = new JsonObject
            {
                ["connected"] = false,
                ["reason"] = "trainer-not-active",
                ["gameId"] = gameId,
                ["processId"] = processId,
                ["observedUtc"] = now.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["trainerTrace"] = new JsonObject
                {
                    ["trainerObserved"] = true,
                    ["processName"] = "DELTARUNE",
                    ["processId"] = processId,
                    ["sessionStartedUtc"] = sessionStarted.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    ["pluginLoadedUtc"] = pluginLoaded.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    ["observedUtc"] = commandObserved.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    ["traceFileName"] = "fixture.traces.otlp"
                }
            };
            var historical = WandTrainerEvidenceAdapter.InterpretProbeOutput(exitedJson.ToJsonString(), gameId,
                processId, creation, processStarted.AddSeconds(5), "DELTARUNE", true, false);
            Require(!historical.Confirmed && historical.Source == "trainer-connected-before-exit"
                && WandIntegration.StatusForTrainerEvidence(historical) == WandSessionStatus.Disconnected,
                "A successful historical Tophat command was reported as a current connection or running game.");
            var state = WandSessionState.Begin("fixture-wand-game", Path.Combine(root, "fixture-wand-game.exe"));
            state.Transition(WandSessionStatus.StartingWand, "fixture");
            state.Transition(WandSessionStatus.Attaching, "fixture");
            state.Transition(WandIntegration.StatusForTrainerEvidence(historical), historical.Detail,
                historical.Source, historical.ObservedUtc);
            Require(state.Read().Status == WandSessionStatus.Disconnected
                && state.Read().EvidenceSource == "trainer-connected-before-exit",
                "Historical Tophat evidence did not preserve an exited session as Disconnected.");
        });
        Check("Exact-case Docker tags remain distinct", () =>
        {
            var games = new List<Game>(); LibraryStore.MergeTags(games, JsonNode.Parse("{\"tags\":[{\"name\":\"AeternaNoctis\"},{\"name\":\"aeternanoctis\"}]}")!, state.Settings);
            Require(games.Count == 2, "Tags were collapsed.");
        });
        Check("Punctuation-distinct Docker tags retain independent image identities and metadata", () =>
        {
            var games = new List<Game> { new() { Id = "007firstlight", Name = "007 First Light" } };
            LibraryStore.MergeTags(games, JsonNode.Parse("{\"tags\":[{\"name\":\"007-first-light\",\"full_size\":123456789}]}")!, state.Settings);
            Require(games.Count == 2 && games[0].Id == "007firstlight" && games[0].SizeGb == 0
                && games[1].Id == "007-first-light" && games[1].SizeGb > 0
                && games[1].DockerImage.EndsWith(":007-first-light", StringComparison.Ordinal), "Distinct exact tags were collapsed or metadata was attached to the wrong image.");
        });
        Check("Docker Hub 403 fallback remains usable without claiming fresh tags", () =>
        {
            Require(SyncClient.HasUsableTags(JsonNode.Parse("{\"success\":false,\"degraded\":true,\"tags\":[{\"name\":\"game\"}],\"error\":\"Docker Hub HTTP 403\"}")!), "Advertised fallback was rejected.");
            Require(!SyncClient.HasUsableTags(JsonNode.Parse("{\"success\":false,\"degraded\":false,\"tags\":[]}")!), "Invalid empty response was accepted.");
        });
        Check("Docker refresh reuses only a recent same-repository unchanged complete snapshot", () =>
        {
            var tag = JsonNode.Parse("{\"name\":\"exactTag\",\"full_size\":10,\"last_updated\":\"2026-09-01\"}")!;
            var page = new JsonObject { ["count"] = 1, ["results"] = new JsonArray(tag.DeepClone()) };
            var cached = new JsonObject { ["source"] = "docker-hub-direct", ["repository"] = "user/repo", ["fetchedAt"] = DateTime.UtcNow.ToString("O"), ["count"] = 1, ["tags"] = new JsonArray(tag.DeepClone()) };
            Require(SyncClient.CanReuseDockerSnapshot(cached, page, "user/repo", DateTime.UtcNow), "Unchanged verified snapshot was not reused.");
            Require(!SyncClient.CanReuseDockerSnapshot(cached, page, "other/repo", DateTime.UtcNow) && !SyncClient.CanReuseDockerSnapshot(cached, page, "user/repo", DateTime.UtcNow.AddMinutes(16)), "Wrong repository or stale snapshot reused.");
            page["count"] = 2; Require(!SyncClient.CanReuseDockerSnapshot(cached, page, "user/repo", DateTime.UtcNow), "New tag count was ignored.");
            page["count"] = 1; page["results"]![0]!["full_size"] = 20;
            Require(!SyncClient.CanReuseDockerSnapshot(cached, page, "user/repo", DateTime.UtcNow), "Updated tag metadata was ignored.");
        });
        Check("New Docker tags retain verified size/date without inventing playtime", () =>
        {
            var games = new List<Game>(); LibraryStore.MergeTags(games, JsonNode.Parse("{\"tags\":[{\"name\":\"NewGame\",\"full_size\":1000000000,\"last_updated\":\"2026-09-08T00:00:00Z\"}]}")!, state.Settings);
            Require(games.Single().Time == 0 && games.Single().Discovered && games.Single().SizeGb == 1 && games.Single().Added.Year == 2026, "Unknown playtime was fabricated or verified metadata lost.");
        });
        Check("Offline transport rejects requests without opening a network connection", () =>
        {
            var guard = new OfflineNetworkGuard(); using var client = new SyncClient(store, guard);
            client.Refresh(state, true, "fixture").GetAwaiter().GetResult();
            Require(guard.Attempts == 1 && !client.Online && client.LastSync == null, "Offline transport did not reject the request.");
        });
        Check("Unavailable automatic artwork is deferred across restart without hiding manual retry", () =>
        {
            var game = new Game { Id = "retry-proof", Name = "Retry proof", Time = 0 };
            var attempts = new JsonObject { [game.Id] = new JsonObject { ["retryAfter"] = DateTime.UtcNow.AddHours(1).ToString("O") } };
            store.CacheData("metadata-attempts.json", attempts.ToJsonString());
            var reloaded = JsonNode.Parse(File.ReadAllText(Path.Combine(new LibraryStore(root).Cache, "metadata-attempts.json")))!.AsObject();
            Require(!MainWindow.MetadataDue(game, reloaded, DateTime.UtcNow) && MainWindow.MetadataDue(game, reloaded, DateTime.UtcNow.AddHours(2)), "Retry schedule lost across restart.");
            Require(!MainWindow.MetadataDue(new Game { Id = "local:x", IsLocal = true }, new(), DateTime.UtcNow), "Local executables triggered Docker metadata lookup.");
        });
        Check("Rejected discovered metadata honors its retry barrier", () =>
        {
            var game = new Game
            {
                Id = "dragon-quest-i-ii-hd-2d-remake",
                Name = "DRAGON QUEST III HD-2D Remake",
                Discovered = true,
                Cover = "cached-cover",
                Time = 10
            };
            var attempts = new JsonObject { [game.Id] = new JsonObject { ["retryAfter"] = DateTime.UtcNow.AddHours(1).ToString("O") } };
            Require(!MainWindow.MetadataDue(game, attempts, DateTime.UtcNow), "A rejected provider title bypassed its persisted retry barrier.");
            Require(MainWindow.MetadataDue(game, attempts, DateTime.UtcNow.AddHours(2)), "A rejected provider title was never released after its retry barrier.");
        });
        Check("Wand delayed startup remains asynchronous and tolerant", () =>
        {
            Require(WandIntegration.WandStartupWindow >= TimeSpan.FromMinutes(3), "The Wand startup window no longer covers a delayed desktop-client start.");
        });
        Check("Missing-cover converter returns a deterministic nonblank image", () =>
        {
            var converter = new CoverConverter();
            var first = converter.Convert(new Game { Id = "cover-fallback-proof", Name = "Cover Fallback Proof", Cover = "" }, typeof(ImageSource), null!, null!);
            var second = converter.Convert(new Game { Id = "cover-fallback-proof", Name = "A different display name", Cover = "" }, typeof(ImageSource), null!, null!);
            var firstBitmap = first as BitmapSource;
            var secondBitmap = second as BitmapSource;
            Require(firstBitmap != null && secondBitmap != null
                && firstBitmap.PixelWidth > 0 && firstBitmap.PixelHeight > 0
                && ReferenceEquals(first, second), "A missing cover did not produce a stable renderable fallback.");
            int stride = firstBitmap!.PixelWidth * Math.Max(1, firstBitmap.Format.BitsPerPixel / 8);
            var pixel = new byte[stride * firstBitmap.PixelHeight];
            firstBitmap.CopyPixels(pixel, stride, 0);
            Require(pixel.Any(value => value != 0), "The missing-cover fallback rendered as a blank pixel buffer.");
        });
        Check("Atomic save and restart preserve preferences", () => { state.Wishlist.Add("AeternaNoctis"); state.Ratings["AeternaNoctis"] = 5; store.Save(state); var loaded = new LibraryStore(root).LoadState(); Require(loaded.Wishlist.Contains("AeternaNoctis") && loaded.Ratings["AeternaNoctis"] == 5, "State lost."); });
        Check("Damaged state recovers a preserved backup", () => { store.Save(state); File.WriteAllText(store.StatePath, "{bad"); var loaded = store.LoadState(); Require(loaded.Wishlist.Contains("AeternaNoctis"), "Backup not recovered."); Require(Directory.GetFiles(root, "*.corrupt-*").Length == 1, "Damaged state not preserved."); store.Save(loaded); });
        Check("Path traversal is rejected", () => Reject(() => LibraryStore.SafeChild(root, "../outside.txt")));
        Check("Invalid rating import is rejected", () => Reject(() => LibraryStore.ValidateState(new UserState { Ratings = new() { ["game"] = 6 } })));
        Check("Unknown state schema is rejected", () => Reject(() => LibraryStore.ValidateState(new UserState { SchemaVersion = 999 })));
        Check("Disjoint shared changes merge without losing website fields", () =>
        {
            var remote = JsonNode.Parse("{\"gameCategories\":{\"a\":\"old\",\"b\":\"website\"},\"custom\":42}")!.AsObject();
            var edit = new PendingEdit { Key = "a", Before = JsonValue.Create("old"), After = JsonValue.Create("native") };
            var merged = Merge.Apply(remote, new[] { edit });
            Require(DataJson.Text(merged["gameCategories"]?["a"]) == "native" && DataJson.Text(merged["gameCategories"]?["b"]) == "website" && merged["custom"]!.GetValue<int>() == 42, "Merge lost fields.");
        });
        Check("Same-field concurrent change becomes a conflict", () =>
        {
            var edit = new PendingEdit { Key = "a", Before = JsonValue.Create("old"), After = JsonValue.Create("native") };
            var merged = Merge.Apply(JsonNode.Parse("{\"gameCategories\":{\"a\":\"website\"}}")!.AsObject(), new[] { edit });
            Require(edit.Conflict != null && DataJson.Text(merged["gameCategories"]?["a"]) == "website", "Concurrent update was overwritten.");
        });
        Check("Interrupted acknowledged writes are idempotent", () =>
        {
            var edit = new PendingEdit { Key = "a", Before = JsonValue.Create("old"), After = JsonValue.Create("native") };
            Merge.Apply(JsonNode.Parse("{\"gameCategories\":{\"a\":\"native\"}}")!.AsObject(), new[] { edit }); Require(edit.Conflict == null, "Own confirmed change conflicted.");
        });
        Check("Offline edits survive restart", () =>
        {
            using var client = new SyncClient(store, new FixtureHandler { Fail = true });
            client.Queue(state, "gameCategories", "fixture", JsonValue.Create("new"));
            client.Refresh(state, false, "test").GetAwaiter().GetResult();
            Require(!client.Online && store.LoadState().Pending.Count == 1, "Offline edit lost."); state.Pending.Clear(); store.Save(state);
        });
        Check("Reconnect publishes and verifies queued changes", () =>
        {
            var handler = new FixtureHandler(); using var client = new SyncClient(store, handler);
            client.Refresh(state, false, null).GetAwaiter().GetResult(); client.Queue(state, "gameCategories", "fixture", JsonValue.Create("new"));
            client.Refresh(state, false, "test").GetAwaiter().GetResult();
            Require(state.Pending.Count == 0 && DataJson.Text(handler.Config["gameCategories"]?["fixture"]) == "new" && handler.Posts == 1, "Reconnect failed.");
        });
        Check("Unauthorized writes retain their queue", () =>
        {
            var handler = new FixtureHandler { RejectWrite = true }; using var client = new SyncClient(store, handler);
            client.Refresh(state, false, null).GetAwaiter().GetResult(); client.Queue(state, "gameCategories", "fixture", JsonValue.Create("edit"));
            client.Refresh(state, false, "test").GetAwaiter().GetResult(); Require(state.Pending.Count == 1 && !client.Online, "Unauthorized save looked successful."); state.Pending.Clear();
        });
        Check("Native defaults match the Windows website contract", () =>
        {
            var defaults = new Preferences();
            Require(defaults.MountPath.Equals(@"E:\games", StringComparison.OrdinalIgnoreCase) && defaults.SortBy == "Recently Added" && defaults.ScriptFormat == "bat" && defaults.ShellTarget == "native-linux", "Native defaults drifted from the requested website contract.");
        });
        Check("Category order can move individually without losing the other tabs", () =>
        {
            var tabs = new List<Category> { new("first", "First"), new("second", "Second"), new("third", "Third") };
            Require(MainWindow.MoveCategory(tabs, "third", -1) && tabs.Select(t => t.Id).SequenceEqual(new[] { "first", "third", "second" }), "Category move-up changed the wrong tab order.");
            Require(MainWindow.MoveCategory(tabs, "first", 1) && tabs.Select(t => t.Id).SequenceEqual(new[] { "third", "first", "second" }), "Category move-down changed the wrong tab order.");
            Require(!MainWindow.MoveCategory(tabs, "third", -1) && !MainWindow.MoveCategory(tabs, "second", 1), "Category edge moves were accepted.");
        });
        Check("Wand protocol integration resolves an exact catalog title", () =>
        {
            var catalog = JsonNode.Parse("{\"titles\":{\"56593\":{\"id\":\"56593\",\"name\":\"Dying Light 2 Stay Human\",\"gameIds\":[\"60921\"]}},\"games\":{\"60921\":{\"id\":\"60921\",\"titleId\":\"56593\",\"platformId\":\"steam\",\"versionPath\":\"DyingLightGame_x64_rwdi.exe\"}}}")!.AsObject();
            Require(WandIntegration.TryResolve(catalog, new Game { Name = "Dying Light 2 Stay Human" }, "DyingLightGame_x64_rwdi.exe", out var target), "Exact Wand title was not resolved.");
            Require(target.TitleId == "56593" && target.GameId == "60921" && WandIntegration.BuildProtocolUri(target.TitleId, target.GameId) == "wemod://play?titleId=56593&gameId=60921", "Wand protocol URI drifted.");
        });
        Check("Wand protocol preflight blocks busy and unknown states without claiming attachment", () =>
        {
            var idle = WandIntegration.InterpretProtocolPreflight(
                "{\"ok\":true,\"state\":\"idle\",\"navigated\":false,\"playDispatched\":false}", 0);
            Require(idle.SafeToDispatch && !idle.TrainerBusy && idle.Reason == "idle-sidebar-state",
                "A verified idle sidebar snapshot did not allow the protocol dispatch gate.");

            var busy = WandIntegration.InterpretProtocolPreflight(
                "{\"ok\":false,\"state\":\"blocked\",\"navigated\":false,\"playDispatched\":false,\"reason\":\"trainer-was-already-launching\",\"activeTrainerGameId\":\"57393\",\"activeTrainerProcessId\":2212}", 3);
            string conflict = WandIntegration.DescribeProtocolPreflightBlock(busy, "116523");
            Require(!busy.SafeToDispatch && busy.TrainerBusy && busy.ActiveTrainerGameId == "57393"
                && busy.ActiveTrainerProcessId == 2212 && conflict.Contains("another game", StringComparison.Ordinal)
                && conflict.Contains("gameId=57393", StringComparison.Ordinal) && conflict.Contains("PID=2212", StringComparison.Ordinal)
                && conflict.Contains("does not confirm trainer attachment", StringComparison.Ordinal)
                && conflict.Contains("no new protocol launch was sent", StringComparison.Ordinal),
                "A different busy Wand trainer was not reported as a specific non-destructive conflict.");

            var unknown = WandIntegration.InterpretProtocolPreflight(
                "{\"ok\":false,\"state\":\"unknown\",\"navigated\":false,\"playDispatched\":false,\"reason\":\"trainer-state-unavailable-before-navigation\"}", 3);
            var unsafeSideEffect = WandIntegration.InterpretProtocolPreflight(
                "{\"ok\":true,\"state\":\"idle\",\"navigated\":true,\"playDispatched\":false}", 0);
            Require(!unknown.SafeToDispatch && !unknown.TrainerBusy && unknown.Reason == "trainer-state-unavailable-before-navigation"
                && !unsafeSideEffect.SafeToDispatch && !unsafeSideEffect.TrainerBusy,
                "Unknown status or a preflight that navigated was allowed to dispatch the protocol URI.");
        });
        Check("Wand launch dispatch is single-shot across CDP, URI fallback, late process, and cold readiness", () =>
            Require(WandLaunchDispatchTests.Run() == 23, "Wand single-dispatch and readiness cases are incomplete."));
        Check("Unity version fingerprints resolve only their paired game executable", () =>
        {
            string folder = Path.Combine(root, "unity-fingerprint");
            string managed = Path.Combine(folder, "wizardwithagun_Data", "Managed");
            Directory.CreateDirectory(managed);
            string executable = Path.Combine(folder, "wizardwithagun.exe");
            string fingerprint = Path.Combine(managed, "Unity.Burst.dll");
            File.WriteAllText(executable, "fixture");
            File.WriteAllText(fingerprint, "fixture");
            var catalog = new JsonObject
            {
                ["titles"] = new JsonObject { ["75407"] = new JsonObject { ["name"] = "Wizard with a Gun", ["gameIds"] = new JsonArray("81975") } },
                ["games"] = new JsonObject { ["81975"] = new JsonObject { ["titleId"] = "75407", ["platformId"] = "steam", ["versionPath"] = @"wizardwithagun_Data\Managed\Unity.Burst.dll" } }
            };
            var game = new Game { Id = "WizardwithaGun", Name = "Wizardwitha Gun" };
            Require(WandIntegration.TryResolve(catalog, game, executable, out var target) && target.GameId == "81975", "The paired Unity fingerprint was rejected.");
            string launcher = Path.Combine(folder, "Launcher.exe");
            File.WriteAllText(launcher, "fixture");
            Require(!WandIntegration.TryResolve(catalog, game, launcher, out _), "An unrelated launcher inherited the game's fingerprint.");
            Require(!WandIntegration.ExactVersionPathMatches(executable, @"wizardwithagun_Data\..\Unity.Burst.dll"), "A traversal fingerprint was accepted.");
            File.Delete(fingerprint);
            Require(!WandIntegration.TryResolve(catalog, game, executable, out _), "A missing version fingerprint was accepted.");
        });
        Check("Wand custom-install request preserves the exact executable location", () =>
        {
            string executable = Path.Combine(root, "wand-custom-install", "bin", "ExactGame.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.WriteAllText(executable, "fixture");

            WandCustomInstallationRequest request = WandIntegration.BuildCustomInstallationRequest("115056", executable);
            string fullPath = Path.GetFullPath(executable);
            string expectedSku = "115056_" + fullPath.ToLowerInvariant();

            Require(request.GameId == "115056"
                && request.ExecutablePath == fullPath
                && request.WorkingDirectory == Path.GetDirectoryName(fullPath)
                && request.Sku == expectedSku
                && request.CorrelationId == "custom:" + expectedSku,
                "The native Wand handoff did not retain the exact executable and working directory.");
        });
        Check("Play with Wand requires an existing exact Wand registration", () =>
        {
            string executable = Path.Combine(root, "wand-existing-registration", "Registered.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.WriteAllText(executable, "fixture");
            string manifest = Path.Combine(root, "wand-supported-games.json");
            File.WriteAllText(manifest, new JsonArray(new JsonObject
            {
                ["titleId"] = "900", ["gameId"] = "901", ["path"] = executable
            }).ToJsonString());
            var catalog = new JsonObject
            {
                ["titles"] = new JsonObject { ["900"] = new JsonObject { ["id"] = "900", ["name"] = "Registered Game", ["gameIds"] = new JsonArray("901") } },
                ["games"] = new JsonObject { ["901"] = new JsonObject { ["id"] = "901", ["titleId"] = "900", ["platformId"] = "steam", ["versionPath"] = "Registered.exe" } }
            };
            store.CacheData("wand-catalog.json", catalog.ToJsonString());
            var game = new Game { Id = "registeredgame", Name = "Registered Game", Installed = true };
            Require(WandIntegration.TryGetExistingWandInstallation(executable, new[] { manifest }, out var registration)
                && registration.TitleId == "900" && registration.GameId == "901", "The saved exact Wand registration was not read.");
            Require(WandIntegration.CanLaunchExistingWandInstall(game, executable, store, new[] { manifest }, out _), "An exact existing Wand registration was hidden.");
            File.WriteAllText(manifest, new JsonArray(new JsonObject
            {
                ["titleId"] = "900", ["gameId"] = "different", ["path"] = executable
            }).ToJsonString());
            Require(!WandIntegration.CanLaunchExistingWandInstall(game, executable, store, new[] { manifest }, out var message)
                && message.Contains("does not match", StringComparison.Ordinal), "A mismatched Wand registration was offered as playable.");
        });
        Check("Wand resolution uses the stable game id and executable aliases", () =>
        {
            var catalog = JsonNode.Parse("{\"titles\":{\"12\":{\"id\":\"12\",\"slug\":\"the-vagrant\",\"name\":\"The Vagrant\",\"gameIds\":[\"34\"]}},\"games\":{\"34\":{\"id\":\"34\",\"titleId\":\"12\",\"platformId\":\"steam\",\"versionPath\":\"TheVagrant.exe\"}}}")!.AsObject();
            Require(WandIntegration.TryResolve(catalog, new Game { Id = "thevagrant", Name = "A stale display title" }, @"E:\games\TheVagrant\TheVagrant.exe", out var target), "Wand did not use the stable id/executable aliases.");
            Require(target.TitleId == "12" && target.GameId == "34", "Alias-based Wand target was not deterministic.");
        });
        Check("Wand cached version paths choose the exact executable from an ambiguous install", () =>
        {
            string folder = Path.Combine(root, "wand-launcher-resolution");
            Directory.CreateDirectory(Path.Combine(folder, "bin"));
            string launcher = Path.Combine(folder, "Launcher.exe");
            string gameExe = Path.Combine(folder, "bin", "ProofGame.exe");
            File.WriteAllText(launcher, "fixture"); File.WriteAllText(gameExe, "fixture");
            var catalog = JsonNode.Parse("{\"titles\":{\"91\":{\"id\":\"91\",\"name\":\"Proof Game\",\"gameIds\":[\"92\"]}},\"games\":{\"92\":{\"id\":\"92\",\"titleId\":\"91\",\"platformId\":\"steam\",\"versionPath\":\"bin\\\\ProofGame.exe\"}}}")!.AsObject();
            store.CacheData("wand-catalog.json", catalog.ToJsonString());
            var resolved = WandIntegration.ResolveInstalledExecutable(new Game { Id = "proofgame", Name = "Proof Game" }, folder, store);
            Require(string.Equals(resolved, gameExe, StringComparison.OrdinalIgnoreCase), "The cached Wand version path did not win over a launcher executable.");
        });
        Check("Wand resolution prefers a nested shipping binary over a tiny root bootstrap", () =>
        {
            string folder = Path.Combine(root, "wand-unreal-bootstrap");
            string nested = Path.Combine(folder, "UTW_Beginnings", "Binaries", "Win64");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(folder, "UTW_Beginnings.exe"), "bootstrap");
            string shipping = Path.Combine(nested, "UTW_Beginnings-Win64-Shipping.exe");
            File.WriteAllText(shipping, "shipping");
            var resolved = WandIntegration.ResolveInstalledExecutable(new Game { Id = "underthewitch", Name = "Underthewitch" }, folder, store);
            Require(string.Equals(resolved, shipping, StringComparison.OrdinalIgnoreCase), "The Unreal shipping executable was displaced by the root bootstrap.");
        });
        Check("Wand launch detects only a safe same-name root bootstrap for a nested catalog binary", () =>
        {
            string folder = Path.Combine(root, "wand-bootstrap-context");
            string nested = Path.Combine(folder, "G1R", "Binaries", "Win64");
            Directory.CreateDirectory(nested);
            string bootstrap = Path.Combine(folder, "G1R-Win64-Shipping.exe");
            string shipping = Path.Combine(nested, "G1R-Win64-Shipping.exe");
            File.WriteAllText(bootstrap, "small root stub");
            File.WriteAllText(shipping, "nested shipping binary");
            Require(string.Equals(WandIntegration.ResolveBootstrapExecutable(shipping, @"G1R\Binaries\Win64\G1R-Win64-Shipping.exe"), bootstrap, StringComparison.OrdinalIgnoreCase), "The safe same-name root bootstrap was not detected.");
            Require(WandIntegration.ResolveBootstrapExecutable(shipping, "Other\\G1R-Win64-Shipping.exe") == null, "A mismatched catalog path produced a bootstrap candidate.");
            Require(WandIntegration.ResolveBootstrapExecutable(shipping, "G1R-Win64-Shipping.exe") == null, "A root-level catalog binary produced a duplicate bootstrap candidate.");
        });
        Check("Wand title matching rejects generic parent and substring collisions", () =>
        {
            var catalog = JsonNode.Parse("{\"titles\":{\"1\":{\"id\":\"1\",\"name\":\"Railbound\",\"gameIds\":[\"11\"]},\"2\":{\"id\":\"2\",\"name\":\"FINAL FANTASY XV WINDOWS EDITION\",\"gameIds\":[\"22\"]},\"3\":{\"id\":\"3\",\"name\":\"SpeedRunners\",\"gameIds\":[\"33\"]},\"4\":{\"id\":\"4\",\"name\":\"SpeedRunners 2: King of Speed\",\"gameIds\":[\"44\"]}},\"games\":{\"11\":{\"id\":\"11\",\"titleId\":\"1\",\"platformId\":\"steam\",\"versionPath\":\"Railbound.exe\"},\"22\":{\"id\":\"22\",\"titleId\":\"2\",\"platformId\":\"steam\",\"versionPath\":\"Windows.exe\"},\"33\":{\"id\":\"33\",\"titleId\":\"3\",\"platformId\":\"steam\",\"versionPath\":\"SpeedRunners.exe\"},\"44\":{\"id\":\"44\",\"titleId\":\"4\",\"platformId\":\"steam\",\"versionPath\":\"SpeedRunners2.exe\"}}}")!.AsObject();
            Require(!WandIntegration.TryResolve(catalog, new Game { Id = "railbound", Name = "Railbound" }, @"E:\\games\\railbound\\Windows\\Windows.exe", out _), "A title match accepted an executable that did not match the catalog version path.");
            Require(WandIntegration.TryResolve(catalog, new Game { Id = "railbound", Name = "Railbound" }, @"E:\\games\\railbound\\Railbound.exe", out var rail) && rail.GameId == "11", "The exact Railbound executable was not selected.");
            Require(WandIntegration.TryResolve(catalog, new Game { Id = "speedrunners", Name = "SpeedRunners" }, @"E:\\games\\speedrunners\\SpeedRunners.exe", out var speed) && speed.GameId == "33", "Exact SpeedRunners matching was displaced by a longer title.");
        });
        Check("Play with Wand fails closed instead of starting an unmodified fallback", () =>
        {
            store.CacheData("wand-catalog.json", "{\"titles\":{\"other\":{\"id\":\"other\",\"name\":\"Other game\",\"gameIds\":[\"other-win\"]}},\"games\":{\"other-win\":{\"id\":\"other-win\",\"titleId\":\"other\",\"platformId\":\"steam\",\"versionPath\":\"Other.exe\"}}}");
            var result = WandIntegration.LaunchAsync(new Game { Id = "wand-fail-closed", Name = "Wand fail closed" }, @"C:\\Games\\WandFailClosed.exe", "", store, default).GetAwaiter().GetResult();
            Require(result.Process == null && !result.UsedProtocol && result.Message.Contains("No unmodified game", StringComparison.Ordinal), "Play with Wand launched or promised an unmodified fallback.");
        });
        Check("Wand overlay diagnostics bind IPC and hook markers to one PID without proving trainer attachment", () =>
        {
            Require(WandIntegration.ContainsConnectionEvidence("[42:7][info] ipc connected\n[42:7][warning] dxgi_hooked: true", 42), "A complete Wand overlay diagnostic log was rejected.");
            Require(!WandIntegration.ContainsConnectionEvidence("[41:7][info] ipc connected\n[warning] dxgi_hooked: true", 42), "Overlay diagnostics from a different game PID were accepted.");
            Require(!WandIntegration.ContainsConnectionEvidence("[41:7][info] ipc connected\n[42:7][warning] dxgi_hooked: true", 42), "Stale overlay IPC from another game PID was combined with a current hook marker.");
            Require(!WandIntegration.ContainsConnectionEvidence("[42:7][info] ipc connected"), "IPC alone was accepted as complete overlay diagnostics.");
            Require(!WandIntegration.ContainsConnectionEvidence("[42:7][warning] dxgi_hooked: true"), "A hook marker without IPC was accepted as complete overlay diagnostics.");
        });
        Check("Play time persists and is exposed on installed games", () =>
        {
            const string id = "local:fixture-play";
            state.PlayTimeSeconds[id] = 7380;
            state.LocalGames[id] = new LocalGame { Name = "Fixture Play", Folder = Path.Combine(root, "fixture-play") };
            state.InstalledGames.Add(id);
            store.Save(state);
            var loaded = store.LoadState();
            var game = store.LoadGames(loaded, store.ReadConfig()).FirstOrDefault(g => g.Id == id);
            var lastPlayed = DateTime.UtcNow.AddMinutes(-3);
            state.LastPlayedUtc[id] = lastPlayed;
            store.Save(state);
            loaded = store.LoadState();
            game = store.LoadGames(loaded, store.ReadConfig()).FirstOrDefault(g => g.Id == id);
                Require(loaded.PlayTimeSeconds[id] == 7380 && loaded.LastPlayedUtc[id] == lastPlayed && game != null && game.Installed && Math.Abs(game.PlayedHours - 2.05) < 0.001 && game.LastPlayedUtc == lastPlayed, "Play time or last-played state was not persisted or displayed.");
        });
        Check("Playing state exposes the visible play and forced-exit controls", () =>
        {
            var game = new Game { IsPlaying = true };
            Require(game.PlayLabel.Contains("Playing", StringComparison.Ordinal) && game.PlayedMeta.StartsWith("Playing", StringComparison.Ordinal), "A running game did not expose its playing state.");
            game.IsPlaying = false;
            Require(game.PlayLabel.Contains("Play", StringComparison.Ordinal) && !game.PlayLabel.Contains("Playing", StringComparison.Ordinal), "A finished game did not restore its Play label.");
        });
        Check("Wand launch cleanup releases handles without terminating a running game", () =>
        {
            var process = Process.Start(new ProcessStartInfo("ping.exe", "127.0.0.1 -n 30") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })
                ?? throw new InvalidOperationException("Could not start the harmless process fixture.");
            int processId = process.Id;
            try
            {
                WandIntegration.ReleaseProcessHandleWithoutTermination(process);
                Thread.Sleep(150);
                using var observed = Process.GetProcessById(processId);
                Require(!observed.HasExited, "Releasing a Wand launch handle terminated the running process.");
            }
            finally
            {
                try { using var cleanup = Process.GetProcessById(processId); if (!cleanup.HasExited) { cleanup.Kill(entireProcessTree: true); cleanup.WaitForExit(5000); } } catch (ArgumentException) { }
            }
        });
        Check("Explicit Exit requests process-tree termination without blocking the UI", () =>
        {
            using var process = Process.Start(new ProcessStartInfo("ping.exe", "127.0.0.1 -n 30") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })
                ?? throw new InvalidOperationException("Could not start the harmless process fixture.");
            var elapsed = Stopwatch.StartNew();
            Require(MainWindow.RequestImmediateProcessTreeExit(process), "The explicit Exit request was not issued.");
            elapsed.Stop();
            Require(elapsed.Elapsed < TimeSpan.FromSeconds(1), "The explicit Exit request blocked instead of returning immediately.");
            Require(process.WaitForExit(5000), "The explicit Exit request did not terminate the process tree.");
        });
        Check("AHK frozen-process state requires counted exact identities", () =>
        {
            string pausedState = "[FrozenProcesses]\r\nCount=1\r\n[FrozenProcess1]\r\nPid=42\r\nCreated=ABC123\r\nMode=game_suspend\r\nState=paused\r\n";
            var paused = FrozenProcessState.Parse(pausedState);
            Require(paused.IsPausedFor(new[] { new FrozenProcessIdentity(42, "abc123") }), "A counted paused process with the exact creation stamp was not recognized.");
            Require(!paused.IsPausedFor(new[] { new FrozenProcessIdentity(42, "different") }) && !paused.IsPausedFor(new[] { new FrozenProcessIdentity(43, "abc123") }), "A mismatched PID or creation stamp was accepted.");
            string staleState = "[FrozenProcesses]\r\nCount=0\r\n[FrozenProcess1]\r\nPid=42\r\nCreated=ABC123\r\nState=paused\r\n";
            Require(!FrozenProcessState.Parse(staleState).IsPausedFor(new[] { new FrozenProcessIdentity(42, "ABC123") }), "An uncounted stale paused record was accepted.");
            string restoringState = pausedState.Replace("State=paused", "State=restoring", StringComparison.Ordinal);
            string pendingState = pausedState.Replace("State=paused", "State=restore_pending", StringComparison.Ordinal);
            Require(!FrozenProcessState.Parse(restoringState).IsPausedFor(new[] { new FrozenProcessIdentity(42, "ABC123") })
                && !FrozenProcessState.Parse(pendingState).IsPausedFor(new[] { new FrozenProcessIdentity(42, "ABC123") }),
                "A running AHK resume transition was incorrectly treated as paused.");
        });
        Check("AHK resume recovers only the exact paused process window", () =>
        {
            string stateFile = Path.Combine(root, "frozen-window-fixture.ini");
            File.WriteAllText(stateFile, "[FrozenProcesses]\r\nCount=1\r\n[FrozenProcess1]\r\nPid=77\r\nCreated=0000000000ABC123\r\nHwnd=4660\r\nState=paused\r\n");
            Require(FrozenProcessState.FindWindowHandle(stateFile, 77, "ABC123") == new IntPtr(4660),
                "The exact hidden AHK window was not recovered.");
            Require(FrozenProcessState.FindWindowHandle(stateFile, 77, "ABC124") == IntPtr.Zero
                && FrozenProcessState.FindWindowHandle(stateFile, 78, "ABC123") == IntPtr.Zero,
                "A different PID or creation stamp inherited the saved window.");
            File.WriteAllText(stateFile, "[FrozenProcesses]\r\nCount=0\r\n[FrozenProcess1]\r\nPid=77\r\nCreated=ABC123\r\nHwnd=4660\r\nState=paused\r\n");
            Require(FrozenProcessState.FindWindowHandle(stateFile, 77, "ABC123") == IntPtr.Zero,
                "An uncounted recovery record was mistaken for a currently paused window.");
        });
        Check("AHK pause reads fail closed and missing files mean running", () =>
        {
            string stateFile = Path.Combine(root, "frozen-processes.ini");
            File.WriteAllText(stateFile, "[FrozenProcesses]\r\nCount=1\r\n[FrozenProcess1]\r\nPid=77\r\nCreated=STAMP\r\nState=paused\r\n");
            var paused = FrozenProcessState.Read(stateFile, new[] { new FrozenProcessIdentity(77, "STAMP") });
            Require(paused.IsAvailable && paused.IsPaused, "A valid AHK state file was not read.");
            File.WriteAllText(stateFile, "not an ini snapshot");
            var malformed = FrozenProcessState.Read(stateFile, new[] { new FrozenProcessIdentity(77, "STAMP") });
            Require(!malformed.IsAvailable && !malformed.IsPaused, "Malformed AHK state was treated as active play.");
            File.Delete(stateFile);
            var missing = FrozenProcessState.Read(stateFile, new[] { new FrozenProcessIdentity(77, "STAMP") });
            Require(missing.IsAvailable && !missing.IsPaused, "A missing optional AHK state file did not mean normal timing.");
        });
        Check("Active playtime excludes a frozen interval and resumes", () =>
        {
            var timing = new ActivePlaytime(100, 0, 1_000_000);
            timing.Sample(10_000_000, paused: false);
            timing.Sample(20_000_000, paused: true);
            timing.Sample(120_000_000, paused: true);
            timing.Sample(130_000_000, paused: false);
            timing.Sample(140_000_000, paused: false);
            Require(Math.Abs(timing.TotalSeconds - 130) < 0.0001 && !timing.IsPaused, "Paused seconds were counted or resumed timing did not continue.");
        });
        Check("Repeated installs deduplicate by exact game and destination", () =>
        {
            string first = DockerScripts.InstallWorkKey(@"E:\\games", "same-game");
            string same = DockerScripts.InstallWorkKey(@"e:\\games\\", "same-game");
            string otherDestination = DockerScripts.InstallWorkKey(@"E:\\other", "same-game");
            string otherGame = DockerScripts.InstallWorkKey(@"E:\\games", "other-game");
            Require(first == same && first != otherDestination && first != otherGame, "Install reservations were not stable and destination/game scoped.");
        });
        Check("Concurrent install reservations allow one same-game writer and independent writers", () =>
        {
            var book = new InstallReservationBook();
            using var start = new ManualResetEventSlim(false);
            var tasks = Enumerable.Range(0, 24).Select(_ => Task.Run(() =>
            {
                start.Wait();
                return book.Reserve(new[] { @"E:\GAMES|same-game" }).Length;
            })).ToArray();
            start.Set();
            Task.WaitAll(tasks);
            Require(tasks.Count(task => task.Result == 1) == 1 && tasks.All(task => task.Result is 0 or 1), "Same-game reservations overlapped.");
            var independent = book.Reserve(new[] { @"E:\GAMES|other-game", @"E:\OTHER|same-game" });
            Require(independent.Length == 2, "Independent game or destination reservations were incorrectly blocked.");
            book.Release(new[] { @"E:\GAMES|same-game" });
            Require(book.Reserve(new[] { @"E:\GAMES|same-game" }).Length == 1, "A released install reservation was not reusable.");
        });
        Check("Native sort modes match all website sort categories and keep unknown values last", () =>
        {
            var games = new[]
            {
                new Game { Id = "alpha", Name = "Alpha", CategoryName = "Zed", Time = 12, SizeGb = 2, Rating = 4, Added = DateTime.UtcNow.AddDays(-2) },
                new Game { Id = "beta", Name = "Beta", CategoryName = "Alpha", Time = 0, SizeGb = 0, Rating = 0, Added = default },
                new Game { Id = "new", Name = "New", CategoryName = "Beta", Category = "new", Time = 2, SizeGb = 1, Rating = 2, Added = default }
            };
            var modes = new[] { "Name A–Z", "Name Z–A", "Time to Beat (Low–High)", "Time to Beat (High–Low)", "Recently Added", "Recently Played", "Oldest First", "Rating (High–Low)", "Rating (Low–High)", "Size (Small–Large)", "Size (Large–Small)", "Category" };
            Require(modes.All(mode => MainWindow.SortGames(games, mode).Count() == games.Length), "A website sort category is missing from native.");
            foreach (var mode in new[] { "Time to Beat (Low–High)", "Time to Beat (High–Low)", "Rating (High–Low)", "Rating (Low–High)", "Size (Small–Large)", "Size (Large–Small)" })
                Require(MainWindow.SortGames(games, mode).Last().Id == "beta", "Unknown values did not stay last for " + mode + ".");
            var played = new Game { Id = "played", Name = "Played", LastPlayedUtc = DateTime.UtcNow, PlayedHours = 0.1 };
            var older = new Game { Id = "older", Name = "Older", LastPlayedUtc = DateTime.UtcNow.AddHours(-1), PlayedHours = 0.1 };
            var never = new Game { Id = "never", Name = "Never" };
            Require(MainWindow.SortInstalledGames(new[] { never, older, played }, "Size (Small–Large)").Select(g => g.Id).SequenceEqual(new[] { "played", "older", "never" }), "Installed games did not put the latest played game first.");
            Require(MainWindow.DefaultSortForTab("installed", "Recently Added") == "Recently Played", "Installed default sort label does not reflect recent play.");
            Require(MainWindow.DefaultSortForTab("all", "Name A–Z") == "Name A–Z", "Installed default changed the saved catalog order.");
        });
        Check("PowerShell, BAT and shell scripts preserve exact tags", () =>
        {
            var game = new Game { Id = "AeternaNoctis", Name = "A title with 'quotes' & %PATH%" };
            foreach (var format in new[] { "ps1", "sh", "bat" })
            {
                var script = DockerScripts.Generate(new[] { game }, state.Settings, format);
                if (format == "bat") script = script.Split("\r\n# GLM_POWERSHELL_START\r\n")[1];
                Require(script.Contains("backup:AeternaNoctis") && !script.Contains("wsl --") && !script.Contains("docker system prune"), "Unsafe or incorrect script.");
                if (format == "ps1")
                {
                    Require(script.Contains("[IO.FileStream]::new", StringComparison.Ordinal) && !script.Contains("[IO.File]::Open(", StringComparison.Ordinal),
                        "The Windows installer lock must use a FileStream constructor supported by Windows PowerShell 5.1.");
                }
                if (format is "ps1" or "bat" or "sh")
                {
                    Require(script.Contains("container create") && script.Contains("cp --follow-link")
                        && script.Contains(DockerScripts.CompletionMarkerName) && script.Contains("GameLibraryManager|")
                        && script.Contains(DockerScripts.StagingDirectoryName) && script.Contains("contains no playable Windows executable")
                        && !script.Contains("cp -rL /home", StringComparison.Ordinal) && !script.Contains("--mount", StringComparison.Ordinal),
                        "Install scripts must use Docker's archive copy, then stage and prove a playable payload before replacing an existing install.");
                    Require(script.Contains("yuzu") && script.Contains("gamebootstrapper") && script.Contains("toolkit"), "Install scripts must not treat emulator or bundled utility executables as native game proof.");
                }
                if (format == "sh") Require(script.Contains("completed_games") && script.Contains("failed_games") && script.Contains("Install batch completed with failures:"), "The shell export still aborts a multi-game batch at the first failure.");
                if (format == "sh") Require(script.Contains("grep -Eiv") && script.Contains("/[^/]*(editor|toolkit|packager)"), "The Bash export does not reject support utilities when proving a native executable.");
                File.WriteAllText(Path.Combine(root, "generated." + format), DockerScripts.Generate(new[] { game }, state.Settings, format));
            }
            var wsl = DockerScripts.Generate(new[] { game }, state.Settings, "sh", shellTarget: "wsl2");
            Require(wsl.Contains("Target: wsl2") && wsl.Contains("/mnt/e/games") && wsl.Contains(DockerScripts.CompletionMarkerName), "WSL2 Bash path conversion or completion proof is missing.");
        });
        Check("Duplicate selections collapse to one install identity", () =>
        {
            var first = new Game { Id = "duplicate-install", Name = "First selection" };
            var second = new Game { Id = "duplicate-install", Name = "Second selection" };
            var distinct = DockerScripts.DistinctGames(new[] { first, second });
            Require(distinct.Length == 1 && ReferenceEquals(distinct[0], first), "Duplicate game selections were not collapsed before install generation.");
        });
        Check("Shell completion markers expand the destination variable", () =>
        {
            var game = new Game { Id = "shell-marker-expansion", Name = "Shell marker expansion" };
            string script = DockerScripts.Generate(new[] { game }, state.Settings, "sh", shellTarget: "native-linux");
            string markerLine = script.Split('\n').Single(line => line.TrimStart().StartsWith("completion_marker=", StringComparison.Ordinal)).Trim();
            string expected = "completion_marker=\"$install_folder/" + DockerScripts.CompletionMarkerName + "\"";
            Require(markerLine == expected, "The POSIX completion marker must expand $destination inside double quotes.");
            Require(!markerLine.Contains("'$destination/", StringComparison.Ordinal), "The POSIX completion marker must not quote the destination variable literally.");
        });
        Check("Multi-game install scripts isolate each completion marker", () =>
        {
            var games = new[] { new Game { Id = "batch-alpha", Name = "Batch Alpha" }, new Game { Id = "batch-beta", Name = "Batch Beta" } };
            foreach (var format in new[] { "ps1", "sh", "bat" })
            {
                var scriptSettings = DataJson.Read<Preferences>(DataJson.Write(state.Settings));
                if (format == "sh") scriptSettings.MountPath = Path.Combine(root, "multi-game-output");
                string script = DockerScripts.Generate(games, scriptSettings, format, shellTarget: format == "sh" ? "wsl2" : null);
                Require(script.Contains("GameLibraryManager|batch-alpha") && script.Contains("GameLibraryManager|batch-beta") && script.Contains(DockerScripts.InstallFolder("batch-alpha")) && script.Contains(DockerScripts.InstallFolder("batch-beta")), "A multi-game " + format + " script lost a game-specific completion marker or folder.");
                if (format == "sh")
                {
                    Require(script.Contains("completed_games+=") && script.Contains("failed_games+=") && script.Contains("later games were still attempted"), "The multi-game shell export does not isolate per-game failures.");
                    File.WriteAllText(Path.Combine(root, "multi-game.sh"), script);
                }
            }
        });
        Check("Windows installs hand off a reviewed BAT job to the visible default terminal", () =>
        {
            string batPath = Path.Combine(root, "install-games.bat");
            string bat = DockerScripts.Generate(new[] { new Game { Id = "terminalproof", Name = "Terminal proof" } }, state.Settings, "bat");
            var start = JobWindow.BuildDefaultTerminalStartInfo(batPath);
            const string operationId = "11111111111111111111111111111111";
            string bound = JobWindow.BindJobEnvironment(bat, "bat", operationId, lockHeld: true);
            Require(bat.StartsWith("@echo off\r\n", StringComparison.Ordinal), "The install export is not a BAT job.");
            Require(bat.Contains("# GLM_POWERSHELL_START") && bat.Contains(DockerScripts.CompletionMarkerName), "The BAT job lost its reviewed PowerShell payload or completion proof.");
            Require(bat.Contains("exit /b %GLM_EXIT%\r\n") && !bat.Contains("\r\npause\r\n"), "The BAT job lost its completion exit contract.");
            string expectedCmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            Require(start.UseShellExecute && !start.CreateNoWindow
                && string.Equals(start.FileName, expectedCmd, StringComparison.OrdinalIgnoreCase)
                && start.Arguments == "/d /c call \"" + Path.GetFullPath(batPath) + "\""
                && string.Equals(start.WorkingDirectory, root, StringComparison.OrdinalIgnoreCase), "The BAT job was not handed to the visible default terminal with its working directory.");
            Require(bound.StartsWith("@set \"GLM_INSTALL_OPERATION_ID=" + operationId + "\"\r\n@set \"GLM_NATIVE_INSTALL_LOCK_HELD=1\"\r\n", StringComparison.Ordinal) && bound.EndsWith(bat, StringComparison.Ordinal), "The BAT job did not retain its bound operation identity and payload.");
            Reject(() => JobWindow.BuildDefaultTerminalStartInfo(Path.Combine(root, "install-games.ps1")));
        });
        Check("Per-game install failures stay bounded and identify the affected game", () =>
        {
            var games = new[]
            {
                new Game { Id = "per-game-failure-alpha", Name = "Per Game Failure Alpha" },
                new Game { Id = "per-game-failure-beta", Name = "Per Game Failure Beta" }
            };
            foreach (var format in new[] { "ps1", "bat", "sh" })
            {
                string script = DockerScripts.Generate(games, state.Settings, format, shellTarget: format == "sh" ? "native-linux" : null);
                string payload = format == "bat" ? script.Split("\r\n# GLM_POWERSHELL_START\r\n")[1] : script;
                foreach (var game in games)
                {
                    string failureIdentity = format == "sh" ? game.Name : game.Id;
                    string pullFailure = "Docker pull failed for " + failureIdentity;
                    string extractionFailure = "Extraction failed for " + failureIdentity;
                    Require(payload.Contains(pullFailure, StringComparison.Ordinal)
                        && payload.Contains(extractionFailure, StringComparison.Ordinal)
                        && payload.Contains("after five attempts", StringComparison.Ordinal)
                        && payload.Contains("after three attempts", StringComparison.Ordinal)
                        && payload.Contains("GameLibraryManager|" + game.Id + "|", StringComparison.Ordinal)
                        && payload.Contains(DockerScripts.InstallFolder(game.Id), StringComparison.Ordinal),
                        "The " + format + " export lost a bounded, game-specific failure or completion scope for " + game.Id + ".");
                }
                if (format == "ps1")
                    Require(payload.Contains("The previous installation was preserved; review the log and retry.", StringComparison.Ordinal)
                        && payload.Contains("contains no playable Windows executable", StringComparison.Ordinal)
                        && payload.Contains(DockerScripts.StagingDirectoryName, StringComparison.Ordinal),
                        "The PowerShell export stopped reporting staged payload validation and preservation for per-game failures.");
                else
                    Require(payload.Contains("run_success=0", StringComparison.Ordinal) || payload.Contains("$runSuccess = $false", StringComparison.Ordinal),
                        "The " + format + " export has no per-game success gate.");
                if (format is "ps1" or "bat")
                    Require(payload.Contains("$failedGames", StringComparison.Ordinal)
                        && payload.Contains("Install batch completed with failures:", StringComparison.Ordinal)
                        && payload.Contains("exit 1", StringComparison.Ordinal),
                        "The Windows export still aborts at the first failed game instead of reporting per-game failures.");
            }
        });
        Check("Concurrent install jobs receive unique script and log paths", () =>
        {
            var timestamp = new DateTime(2026, 9, 9, 10, 0, 0, 123, DateTimeKind.Local);
            string first = JobWindow.BuildJobLogPath(root, timestamp, Guid.Parse("11111111-1111-1111-1111-111111111111"));
            string second = JobWindow.BuildJobLogPath(root, timestamp, Guid.Parse("22222222-2222-2222-2222-222222222222"));
            string powershell = DockerScripts.Generate(new[] { new Game { Id = "concurrent-lock", Name = "Concurrent lock" } }, state.Settings, "ps1");
            string shell = DockerScripts.Generate(new[] { new Game { Id = "concurrent-lock", Name = "Concurrent lock" } }, state.Settings, "sh");
            Require(!string.Equals(first, second, StringComparison.OrdinalIgnoreCase)
                && Path.GetExtension(Path.ChangeExtension(first, ".bat")) == ".bat"
                && !Path.GetFileName(first).Equals(timestamp.ToString("yyyyMMdd-HHmmss-fff") + ".log", StringComparison.Ordinal)
                && powershell.Contains("Enter-NativeInstallLock", StringComparison.Ordinal)
                && powershell.Contains("Exit-NativeInstallLock", StringComparison.Ordinal)
                && shell.Contains("native_install_lock", StringComparison.Ordinal)
                && shell.Contains("native_install_unlock", StringComparison.Ordinal),
                 "Concurrent jobs still share the timestamp-only script/log path.");
        });
        Check("Same-game installs to different destinations receive isolated Docker identities", () =>
        {
            string first = DockerScripts.ContainerNameForDestination("same-game", @"E:\\games\\first");
            string second = DockerScripts.ContainerNameForDestination("same-game", @"E:\\games\\second");
            string firstAgain = DockerScripts.ContainerNameForDestination("same-game", @"e:\\games\\first\\");
            Require(first != second && first == firstAgain && first.Length == 28,
                "Destination-scoped container names did not remain unique and canonical.");
            Require(DockerScripts.InstallFolder("same-game") == DockerScripts.InstallFolder("same-game"),
                "The stable downloaded folder identity changed with the destination namespace.");
        });
        Check("A 1259-game BAT export stays below Windows command-line limits", () =>
        {
            var games = Enumerable.Range(0, 1259).Select(i => new Game { Id = "game" + i, Name = "Game " + i });
            string bat = DockerScripts.Generate(games, state.Settings, "bat");
            var launcher = bat.Split('\n').First(l => l.Contains("WindowsPowerShell\\v1.0\\powershell.exe", StringComparison.OrdinalIgnoreCase));
            Require(launcher.Length < 8191 && bat.Contains("backup:game1258"), "Bulk BAT export is truncated or too long.");
        });
        Check("Case-distinct Docker tags use different Windows directories", () => Require(!DockerScripts.InstallFolder("AeternaNoctis").Equals(DockerScripts.InstallFolder("aeternanoctis"), StringComparison.OrdinalIgnoreCase), "Case-distinct installs collide."));
        Check("Untrusted Docker identities cannot inject shell commands", () => Reject(() => DockerScripts.Generate(new[] { new Game { Id = "bad; Remove-Item C:" } }, state.Settings)));
        Check("Invalid mount paths are rejected", () => Reject(() => DockerScripts.Generate(new[] { new Game { Id = "game" } }, new Preferences { MountPath = "relative/path" })));
        Check("Stop scripts affect only selected owned container names", () =>
        {
            var script = DockerScripts.Generate(new[] { new Game { Id = "game" } }, state.Settings, stop: true);
            Require(script.Contains(DockerScripts.ContainerNameForDestination("game", state.Settings.MountPath)) && !script.Contains("-aq") && !script.Contains("prune"), "Stop scope is excessive.");
        });
        Check("Docker cleanup refuses mismatched ownership metadata", () =>
        {
            var game = new Game { Id = "ownership-proof", Name = "Ownership proof" };
            string expectedMetadata = "native|" + game.Id;
            string installPs = DockerScripts.Generate(new[] { game }, state.Settings, "ps1");
            string stopPs = DockerScripts.Generate(new[] { game }, state.Settings, "ps1", stop: true);
            string installSh = DockerScripts.Generate(new[] { game }, state.Settings, "sh", shellTarget: "native-linux");
            foreach (var script in new[] { installPs, stopPs, installSh })
            {
                Require(script.Contains("com.gamelibrary.owner") && script.Contains("com.gamelibrary.game-id") && script.Contains(expectedMetadata), "Cleanup does not inspect both native ownership and exact game identity.");
                Require(script.Contains("Refusing destructive cleanup for unowned container", StringComparison.Ordinal), "Cleanup has no fail-closed ownership mismatch refusal.");
            }
            Require(DockerScripts.OwnershipMatches(expectedMetadata, game.Id) &&
                DockerScripts.OwnershipMatches("  " + expectedMetadata + "\r\n", game.Id) &&
                !DockerScripts.OwnershipMatches("native|different-game", game.Id) &&
                !DockerScripts.OwnershipMatches("other|" + game.Id, game.Id), "Direct native cancellation does not enforce exact ownership metadata.");
            Require(DockerScripts.OwnershipFromLabelsJson("{\"com.gamelibrary.owner\":\"native\",\"com.gamelibrary.game-id\":\"ownership-proof\"}") == expectedMetadata &&
                DockerScripts.OwnershipFromLabelsJson("{\"com.gamelibrary.owner\":\"other\",\"com.gamelibrary.game-id\":\"ownership-proof\"}") != expectedMetadata &&
                DockerScripts.OwnershipFromLabelsJson("not-json").Length == 0, "Docker label JSON parsing is not fail-closed.");
            Require(installPs.IndexOf("container inspect", StringComparison.Ordinal) < installPs.IndexOf("container rm --force", StringComparison.Ordinal), "PowerShell cleanup can remove before ownership inspection.");
            Require(stopPs.IndexOf("container inspect", StringComparison.Ordinal) < stopPs.IndexOf("& $dockerExecutable stop", StringComparison.Ordinal), "PowerShell stop can stop before ownership inspection.");
            Require(installSh.IndexOf("container inspect", StringComparison.Ordinal) < installSh.IndexOf("docker rm -f", StringComparison.Ordinal), "Shell cleanup can remove before ownership inspection.");
        });
        Check("Kill All export needs no selection and BAT preserves its PowerShell payload", () =>
        {
            var script = DockerScripts.GenerateKillAll("ps1");
            var bat = DockerScripts.GenerateKillAll("bat");
            Require(bat.Split("\r\n# GLM_POWERSHELL_START\r\n")[1] == script, "BAT payload differs from the reviewed PowerShell script.");
            Require(script.Contains("Read-Host") && script.IndexOf("Read-Host", StringComparison.Ordinal) < script.IndexOf("container rm --force", StringComparison.Ordinal), "Missing execution confirmation.");
            Require(script.Contains("--no-trunc") && script.Contains("@targetArguments") && !script.Contains("--volumes") && !script.Contains("prune") && !script.Contains("wsl --"), "Removal scope or Docker backend preservation regressed.");
            File.WriteAllText(Path.Combine(root, "kill-all.ps1"), script, new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(root, "kill-all.bat"), bat);
            Reject(() => DockerScripts.GenerateKillAll("unsupported"));
        });
        Check("Installed scanner avoids installers and ambiguous executables", () =>
        {
            string folder = Path.Combine(root, "installed", "TestGame"); Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "setup.exe"), "fixture"); File.WriteAllText(Path.Combine(folder, "TestGame.exe"), "fixture");
            var found = InstalledScanner.Scan(Path.GetDirectoryName(folder)!, new[] { ("TestGame", "Test Game") }, default); Require(found.Count == 1 && found["TestGame"].EndsWith("TestGame.exe"), "Wrong executable selected.");
        });
        Check("Local folder size sums nested files and leaves missing paths unknown", () =>
        {
            string folder = Path.Combine(root, "local-size-proof");
            string nested = Path.Combine(folder, "content", "data");
            Directory.CreateDirectory(nested);
            using (var file = new FileStream(Path.Combine(folder, "root.bin"), FileMode.Create, FileAccess.Write, FileShare.Read)) file.SetLength(1024);
            using (var file = new FileStream(Path.Combine(nested, "nested.bin"), FileMode.Create, FileAccess.Write, FileShare.Read)) file.SetLength(2048);
            Require(InstalledScanner.MeasureFolders(new[] { folder }, default) == 3072, "Nested local file lengths were not summed exactly.");
            Require(InstalledScanner.MeasureFolders(Array.Empty<string>(), default) == null, "An empty folder selection was reported as zero storage.");
            Require(InstalledScanner.MeasureFolders(new[] { Path.Combine(root, "missing-local-install") }, default) == null, "A missing install folder was reported as zero storage.");
        });
        Check("Card metadata labels registry size as a download estimate", () =>
        {
            var game = new Game { Name = "Proof", CategoryName = "New arrivals", Time = 10, SizeGb = 0.07 };
            Require(game.Meta.Contains("~10 h", StringComparison.Ordinal) && game.Meta.Contains("0.07 GB download", StringComparison.Ordinal), "The card does not distinguish completion hours from Docker download size.");
        });
        Check("Canonical punctuation variants do not hide an installed game", () =>
        {
            string library = Path.Combine(root, "canonical-install");
            string folder = Path.Combine(library, "007 First Light");
            string retail = Path.Combine(folder, "Retail");
            Directory.CreateDirectory(retail);
            string launcher = Path.Combine(retail, "007FirstLight.exe");
            File.WriteAllText(launcher, "fixture");
            File.WriteAllText(Path.Combine(folder, "unins000.exe"), "fixture");
            var found = InstalledScanner.Discover(library, new[] { ("007firstlight", "007 First Light") }, default);
            Require(found.Games.Count == 1 && found.Games[0].Id == "007firstlight" && string.Equals(found.Games[0].Launcher, launcher, StringComparison.OrdinalIgnoreCase), "The canonical 007 folder was not mapped to its playable executable.");
        });
        Check("Catalog scanner chooses real game binaries and rejects emulator payloads", () =>
        {
            string library = Path.Combine(root, "launcher-selection");
            string shippingFolder = Path.Combine(library, "Unreal Game");
            string shippingDirectory = Path.Combine(shippingFolder, "UnrealGame", "Binaries", "Win64");
            Directory.CreateDirectory(shippingDirectory);
            string bootstrap = Path.Combine(shippingFolder, "UnrealGame.exe");
            string shipping = Path.Combine(shippingDirectory, "UnrealGame-Win64-Shipping.exe");
            File.WriteAllText(bootstrap, "bootstrap");
            using (var stream = new FileStream(shipping, FileMode.Create, FileAccess.Write, FileShare.Read)) stream.SetLength(24 * 1024 * 1024);

            string duplicateFolder = Path.Combine(library, "Duplicate Game");
            string duplicateNested = Path.Combine(duplicateFolder, "Duplicate Game");
            Directory.CreateDirectory(duplicateNested);
            string duplicateRoot = Path.Combine(duplicateFolder, "Duplicate Game.exe");
            string duplicateCopy = Path.Combine(duplicateNested, "Duplicate Game.exe");
            File.WriteAllText(duplicateRoot, "root");
            File.WriteAllText(duplicateCopy, "nested");

            string emulatorFolder = Path.Combine(library, "Emulator Payload");
            Directory.CreateDirectory(emulatorFolder);
            File.WriteAllText(Path.Combine(emulatorFolder, "yuzu.exe"), "emulator");
            File.WriteAllText(Path.Combine(emulatorFolder, "Launcher.exe"), "generic launcher");
            File.WriteAllText(Path.Combine(emulatorFolder, "HW2Toolkit.exe"), "toolkit");
            File.WriteAllText(Path.Combine(emulatorFolder, "HW2Editor.exe"), "editor");

            string legacyFolder = Path.Combine(library, "DeusExInvisibleWar");
            string legacySystem = Path.Combine(legacyFolder, "System");
            Directory.CreateDirectory(legacySystem);
            File.WriteAllText(Path.Combine(legacySystem, "dx2.exe"), "bootstrap");
            using (var stream = new FileStream(Path.Combine(legacySystem, "DX2Main.exe"), FileMode.Create, FileAccess.Write, FileShare.Read)) stream.SetLength(6 * 1024 * 1024);
            File.WriteAllText(Path.Combine(legacySystem, "Ion Launcher.exe"), "launcher");

            var found = InstalledScanner.Discover(library, new[]
            {
                ("unrealgame", "Unreal Game"),
                ("duplicategame", "Duplicate Game"),
                ("emulatorpayload", "Emulator Payload"),
                ("DeusExInvisibleWar", "Deus Ex Invisible War")
            }, default);
            var selectedShipping = found.Games.Single(g => g.Id == "unrealgame").Launcher;
            Require(selectedShipping == shipping, "The root bootstrap displaced the real shipping binary.");
            Require(found.Games.Single(g => g.Id == "duplicategame").Launcher == duplicateRoot, "The shallow playable binary was not preferred over a duplicate copy.");
            Require(found.Games.Single(g => g.Id == "DeusExInvisibleWar").Launcher == Path.Combine(legacySystem, "DX2Main.exe"), "The playable DX2Main binary was displaced by the tiny DX2 bootstrap.");
            Require(!found.Games.Any(g => g.Id == "emulatorpayload") && found.CatalogFoldersPresent.Contains("emulatorpayload"), "An emulator or generic launcher was offered as a native game executable.");
        });
        Check("Legacy hashed install folders still resolve their exact game executable", () =>
        {
            string library = Path.Combine(root, "legacy-install");
            string folder = Path.Combine(library, "legacygame-oldsuffix");
            Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "LegacyGame.exe"), "fixture"); File.WriteAllText(Path.Combine(folder, DockerScripts.CompletionMarkerName), "GameLibraryManager|legacygame");
            var folders = InstalledScanner.FindCatalogFolders(library, "legacygame");
            var found = InstalledScanner.ScanDownloads(library, new[] { ("legacygame", "Legacy Game") }, default);
            var explicitScan = InstalledScanner.Discover(library, new[] { ("legacygame", "Legacy Game") }, default);
            Require(folders.Count == 1 && InstalledScanner.HasCompletionMarker(library, "legacygame", "Legacy Game") && found.Games.Count == 1 && explicitScan.Games.Count == 1 && found.Games[0].Launcher == Path.Combine(folder, "LegacyGame.exe") && explicitScan.Games[0].Id == "legacygame" && found.CatalogFoldersPresent.Contains("legacygame"), "A valid legacy install folder or completion marker was not discovered.");
        });
        Check("Completion markers are exact and cannot cross-identify games", () =>
        {
            string markerRoot = Path.Combine(root, "marker-proof-library");
            string marker = Path.Combine(markerRoot, DockerScripts.InstallFolder("marker-proof"), DockerScripts.CompletionMarkerName);
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, "GameLibraryManager|marker-proof");
            Require(InstalledScanner.IsValidCompletionMarker(marker, "marker-proof"), "A valid completion marker was rejected.");
            Require(!InstalledScanner.IsValidCompletionMarker(marker, "other-game"), "A marker for another game was accepted.");
            File.SetLastWriteTimeUtc(marker, DateTime.UtcNow.AddMinutes(-2));
            Require(!InstalledScanner.HasFreshCompletionMarker(markerRoot, "marker-proof", DateTime.UtcNow.AddMinutes(-1)), "A stale completion marker was treated as a new install.");
            File.WriteAllText(marker, "GameLibraryManager|marker-proof");
            File.SetLastWriteTimeUtc(marker, DateTime.UtcNow);
            Require(InstalledScanner.HasFreshCompletionMarker(markerRoot, "marker-proof", DateTime.UtcNow.AddMinutes(-1)), "A new completion marker was not recognized.");
            File.WriteAllText(marker, "GameLibraryManager|marker-proof|operation-a");
            Require(InstalledScanner.HasFreshCompletionMarker(markerRoot, "marker-proof", DateTime.UtcNow.AddMinutes(-1), operationId: "operation-a")
                && !InstalledScanner.HasFreshCompletionMarker(markerRoot, "marker-proof", DateTime.UtcNow.AddMinutes(-1), operationId: "operation-b"),
                "A completion marker was not isolated to its exact install operation.");
            File.WriteAllText(marker, "GameLibraryManager|marker-proof\npartial");
            Require(!InstalledScanner.IsValidCompletionMarker(marker, "marker-proof"), "A malformed completion marker was accepted.");
        });
        Check("Incomplete game folder cannot launch a bundled runtime utility", () =>
        {
            string folder = Path.Combine(root, "scan-incomplete", "Viewfinder", "_Redist"); Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "QuickSFV.exe"), "fixture");
            Require(InstalledScanner.Scan(Path.GetDirectoryName(Path.GetDirectoryName(folder)!)!, new[] { ("viewfinder", "Viewfinder") }, default).Count == 0, "A support utility was offered as a game.");
        });
        Check("Unknown installed games survive local state reload without becoming Docker tags", () =>
        {
            string folder = Path.Combine(root, "local-library", "My Local Game"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "MyLocalGame.exe"), "scanner fixture");
            var entry = InstalledScanner.Discover(Path.GetDirectoryName(folder)!, Array.Empty<(string, string)>(), default).Games.Single();
            Require(entry.IsLocal && entry.Launcher != null && !DockerScripts.ValidTag(entry.Id), "Local executable was dropped or became a Docker identity.");
            state.LocalGames[entry.Id] = new() { Name = entry.Name, Folder = entry.Folder }; state.LaunchPaths[entry.Id] = entry.Launcher!; state.InstalledGames.Add(entry.Id); store.Save(state);
            var restored = store.LoadGames(store.LoadState(), store.ReadConfig()).Single(g => g.Id == entry.Id);
            Require(restored.IsLocal && restored.Installed && restored.DockerImage.Length == 0 && restored.Name == "My Local Game", "Local game state was not restored.");
            Reject(() => DockerScripts.Generate(new[] { restored }, state.Settings));
        });
        Check("Ambiguous local launchers are retained for explicit choice", () =>
        {
            string folder = Path.Combine(root, "ambiguous-local", "Local game"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "client.exe"), "fixture"); File.WriteAllText(Path.Combine(folder, "alternate.exe"), "fixture");
            var found = InstalledScanner.Discover(Path.GetDirectoryName(folder)!, Array.Empty<(string, string)>(), default);
            Require(found.Games.Count == 1 && found.Games[0].Launcher == null && found.Notices.Count > 0, "Ambiguous launcher was silently selected or game discarded.");
        });
        Check("A library-root launcher cannot hide installed child games", () =>
        {
            string library = Path.Combine(root, "library-with-launcher"); string folder = Path.Combine(library, "Child Game"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(library, "launcher.exe"), "fixture"); File.WriteAllText(Path.Combine(folder, "ChildGame.exe"), "fixture");
            var found = InstalledScanner.Discover(library, Array.Empty<(string, string)>(), default);
            Require(found.Games.Count == 1 && found.Games[0].Folder == folder, "Root launcher hid child games.");
        });
        Check("Local game discovery excludes web helpers and repack menus without guessing between games", () =>
        {
            string library = Path.Combine(root, "local-support-helpers");
            string dawn = Path.Combine(library, "The Blood of Dawnwalker");
            string oni = Path.Combine(library, "Onimusha - Way of the Sword");
            string dawnExe = Path.Combine(dawn, "Dawnwalker", "Binaries", "Win64", "Dawnwalker.exe");
            string helper = Path.Combine(dawn, "Engine", "Binaries", "Win64", "EpicWebHelper.exe");
            foreach (string path in new[] { dawnExe, helper, Path.Combine(oni, "OnimushaWotS.exe"), Path.Combine(oni, "FitGirl-Launcher.exe") })
            { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "fixture"); }
            var found = InstalledScanner.Discover(library, Array.Empty<(string, string)>(), default);
            Require(found.Games.Count == 2 && found.Games.Single(g => g.Folder == dawn).Launcher == dawnExe
                && found.Games.Single(g => g.Folder == oni).Launcher == Path.Combine(oni, "OnimushaWotS.exe"), "Support binaries prevented exact executable discovery.");
            var existing = new UserState();
            string localId = LocalGame.Identity(dawn);
            existing.InstalledGames.Add(localId);
            existing.LocalGames[localId] = new LocalGame { Name = "Personal title", Folder = dawn };
            existing.PlayTimeSeconds[localId] = 123;
            var catalogScan = InstalledScanner.Discover(library, new[] { ("dawnwalker", "The Blood of Dawnwalker") }, default);
            Require(InstalledFolderMapping.Apply(existing, catalogScan) && existing.LaunchPaths[localId] == dawnExe
                && existing.LocalGames[localId].Name == "Personal title" && existing.PlayTimeSeconds[localId] == 123,
                "Catalog discovery did not recover the exact-folder local alias without losing personal data.");
            string manual = Path.Combine(oni, "OnimushaWotS.exe"); existing.LaunchPaths[localId] = manual;
            InstalledFolderMapping.Apply(existing, catalogScan);
            Require(existing.LaunchPaths[localId] == manual, "A saved existing launcher was overwritten.");
            File.WriteAllText(Path.Combine(oni, "SecondGame.exe"), "fixture");
            Require(InstalledScanner.Discover(library, Array.Empty<(string, string)>(), default).Games.Single(g => g.Folder == oni).Launcher == null,
                "Two real executable candidates were guessed.");
            string supportOnly = Path.Combine(library, "Only support"); Directory.CreateDirectory(supportOnly);
            File.WriteAllText(Path.Combine(supportOnly, "FitGirl-Launcher.exe"), "fixture");
            Require(!InstalledScanner.Discover(library, Array.Empty<(string, string)>(), default).Games.Any(g => g.Folder == supportOnly), "A repack menu became an installed game.");
        });
        Check("Completed-download scan excludes support-only payloads and unrelated folders", () =>
        {
            string library = Path.Combine(root, "completed-downloads");
            foreach (var id in new[] { "actualgame", "supportonly", "unrelated" }) Directory.CreateDirectory(Path.Combine(library, DockerScripts.InstallFolder(id)));
            File.WriteAllText(Path.Combine(library, DockerScripts.InstallFolder("actualgame"), "actualgame.exe"), "fixture");
            File.WriteAllText(Path.Combine(library, DockerScripts.InstallFolder("supportonly"), "QuickSFV.exe"), "fixture");
            File.WriteAllText(Path.Combine(library, DockerScripts.InstallFolder("unrelated"), "unrelated.exe"), "fixture");
            var found = InstalledScanner.ScanDownloads(library, new[] { ("actualgame", "Actual game"), ("supportonly", "Support only") }, default);
            Require(found.Games.Count == 1 && found.Games[0].Id == "actualgame" && found.CatalogFoldersPresent.Contains("supportonly") && found.Notices.Count > 0, "Completion scan marked incomplete or unselected payloads installed.");
        });
        Check("Downloaded scans recover a legacy folder that omits an initial article", () =>
        {
            string library = Path.Combine(root, "legacy-article-folder");
            string folder = Path.Combine(library, "legendoftianding");
            Directory.CreateDirectory(folder);
            string executable = Path.Combine(folder, "Zebra.exe");
            File.WriteAllText(executable, "fixture");
            var found = InstalledScanner.ScanDownloads(library, new[] { ("thelegendoftianding", "The Legend of Tianding") }, default);
            var folders = InstalledScanner.FindCatalogFolders(library, "thelegendoftianding", "The Legend of Tianding");
            Require(found.Games.Count == 1 && found.Games[0].Id == "thelegendoftianding"
                && string.Equals(found.Games[0].Launcher, executable, StringComparison.OrdinalIgnoreCase)
                && folders.Contains(folder, StringComparer.OrdinalIgnoreCase),
                "The valid legacy Tianding folder was not mapped back to its catalog game.");
        });
        Check("Metadata alias matching accepts curated aliases and rejects generic title collisions", () =>
        {
            Require(MetadataClient.SameTitle("Viewfinder", "VIEWFINDER")
                && MetadataClient.SameTitle("DAVE THE DIVER", "DAVE: THE DIVER")
                && !MetadataClient.SameTitle("INMOST", "INMOST Soundtrack")
                && !MetadataClient.SameTitle("", ""), "Incorrect metadata title alias was accepted.");
            var alias = JsonNode.Parse("{\"id\":\"ofashnsteel\",\"name\":\"Of Ash and Steel\",\"source\":{\"image\":\"steam-known\",\"time\":\"known-override\"}}")!.AsObject();
            var game = new Game { Id = "ofashnsteel", Name = "ofashnsteel" };
            Require(MetadataClient.MatchesGame(game, alias), "Curated backend alias was rejected.");
            alias["source"]!["image"] = "steam";
            Require(!MetadataClient.MatchesGame(game, alias), "A generic different-title search result was accepted.");
            alias["source"]!["image"] = "steam-known";
            alias["id"] = "another-game";
            Require(!MetadataClient.MatchesGame(game, alias), "A curated alias with a different stable identity was accepted.");
        });
        Check("Metadata never treats shared title words as sequel or spinoff identity", () =>
        {
            foreach (var pair in new[] { ("Dark Souls", "Dark Souls III"), ("Elden Ring", "Elden Ring Nightreign"),
                ("Resident Evil 4", "Resident Evil 4 VR"), ("Star Wars Outlaws", "LEGO Star Wars Outlaws"),
                ("Game II", "Game III"), ("Game 2", "Game 20"), ("The Game", "Game"), ("Game", "Game Soundtrack") })
            {
                var game = new Game { Id = "identity-proof", Name = pair.Item1 };
                var response = new JsonObject { ["id"] = game.Id, ["name"] = pair.Item2 };
                Require(!MetadataClient.MatchesGame(game, response), "Unrelated metadata accepted: " + pair.Item2);
            }
            Require(MetadataClient.MatchesGame(new Game { Id = "identity-proof", Name = "Witch's Game" },
                new JsonObject { ["id"] = "identity-proof", ["name"] = "WITCHS: GAME" }), "Harmless punctuation normalization was lost.");
        });
        Check("Explicit non-game catalog entries cannot inherit unrelated completion metadata", () =>
        {
            var proofState = new UserState(); proofState.Ratings["ahk2exe"] = 4; proofState.Wishlist.Add("ahk2exe");
            var config = store.ReadConfig(); config["gameCategories"] ??= new JsonObject(); config["gameCategories"]!["ahk2exe"] = "action";
            var metadata = store.ReadMetadata();
            metadata["ahk2exe"] = new JsonObject { ["name"] = "Mega Man Battle Network", ["time"] = 99, ["diskRequirementGb"] = 77, ["matchedTitle"] = "Mega Man Battle Network" };
            store.CacheData("metadata.json", metadata.ToJsonString());
            var entry = store.LoadGames(proofState, config).Single(g => g.Id == "ahk2exe");
            Require(entry.IsNonGame && entry.Name == "ahk2exe" && entry.Time == 0 && entry.DiskRequirementGb == 0
                && entry.Meta.Contains("Utility / backup", StringComparison.Ordinal), "Utility acquired unrelated game data.");
            Require(entry.Category == "action" && entry.Rating == 4 && entry.Wishlisted && entry.DockerImage == "michadockermisha/backup:ahk2exe", "Classification changed personal state or Docker identity.");
            Require(!MainWindow.MetadataDue(entry, new(), DateTime.UtcNow), "Utility consumes automatic game metadata work.");
            using var transport = new OfflineNetworkGuard(); using var client = new MetadataClient(transport);
            bool rejected = false;
            try { client.Refresh(entry, store, true, true, default).GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected && transport.Attempts == 0, "Manual game metadata queried a utility.");
            Require(!store.LoadGames(proofState, config).Single(g => g.Id == "007firstlight").IsNonGame, "Game classification changed.");
        });
        Check("GMenu versions use publisher repository titles without weakening metadata identity", () =>
        {
            const string tag = "gmenu-20260917003510-f36462cf", repository = "michadockermisha/onimusha---way-of-the-sword";
            string id = DockerIdentity.Create(repository, tag);
            var games = new List<Game>(); LibraryStore.MergeTags(games,
                new JsonObject { ["tags"] = new JsonArray(new JsonObject { ["name"] = tag }) },
                new Preferences { DockerUsername = "michadockermisha", RepoName = "onimusha---way-of-the-sword" });
            var game = games.Single();
            Require(game.Id == id && game.DockerImage == repository + ":" + tag && MetadataClient.SameTitle(game.Name, "Onimusha: Way of the Sword"), "Build title repair changed image identity or missed the title.");
            var response = new JsonObject { ["success"] = true, ["id"] = id, ["name"] = "Onimusha: Way of the Sword", ["image"] = "https://covers.example.test/image.jpg", ["time"] = 12 };
            Require(MetadataClient.MatchesGame(game, response), "Exact repository title rejected.");
            response["name"] = "Onimusha 2: Samurai's Destiny";
            Require(!MetadataClient.MatchesGame(game, response), "Sequel accepted for a build tag.");
            foreach (string value in new[] { "gmenu-20261317003510-f36462cf", "gmenu-20260917003510-notahash", "onimusha-2", "2026" })
                Require(DockerIdentity.MetadataTitle(repository, value) == LibraryStore.FormatName(value), "Arbitrary tag was treated as a generated version.");
            Require(DockerIdentity.MetadataTitle("other/onimusha", tag) == LibraryStore.FormatName(tag)
                && DockerIdentity.MetadataTitle(DockerIdentity.CatalogRepository, tag) == LibraryStore.FormatName(tag), "Naming contract escaped its publisher scope.");
            response["name"] = "Onimusha: Way of the Sword";
            var attempts = new JsonObject { [id] = new JsonObject { ["retryAfter"] = DateTime.UtcNow.AddHours(1).ToString("O") } };
            Require(MainWindow.MetadataDue(game, attempts, DateTime.UtcNow), "Legacy build-tag failure blocked the corrected title.");
            attempts[id]!["queryTitle"] = MetadataClient.ExpectedTitle(game);
            Require(!MainWindow.MetadataDue(game, attempts, DateTime.UtcNow), "Corrected-title failure bypassed backoff repeatedly.");
            attempts[id]!["queryTitle"] = LibraryStore.FormatName(tag);
            Require(MainWindow.MetadataDue(game, attempts, DateTime.UtcNow), "Changed lookup identity retained the wrong-title cooldown.");
            string cover = Directory.EnumerateFiles(Path.Combine(store.Assets, "images"), "*.jpg", SearchOption.AllDirectories).First();
            using var client = new MetadataClient(new MetadataFixtureHandler(File.ReadAllBytes(cover), response.ToJsonString()));
            client.Refresh(game, store, true, true, default).GetAwaiter().GetResult();
            Require(File.Exists(LibraryStore.SafeChild(store.Cache, DataJson.Text(store.ReadMetadata()[id]?["cover"]))), "Corrected build title could not retain its matched artwork.");
        });
        Check("Metadata time and downloaded cover survive offline restart", () =>
        {
            string cover = Directory.EnumerateFiles(Path.Combine(store.Assets, "images"), "*.jpg", SearchOption.AllDirectories).First();
            using var client = new MetadataClient(new MetadataFixtureHandler(File.ReadAllBytes(cover)));
            var game = new Game { Id = "metadata-proof", Name = "Metadata Proof", Category = "new" };
            var result = client.Refresh(game, store, true, true, default).GetAwaiter().GetResult();
            var saved = new LibraryStore(root).ReadMetadata()[game.Id]!;
            Require(DataJson.Number(saved["time"]) == 12 && File.Exists(LibraryStore.SafeChild(store.Cache, DataJson.Text(saved["cover"]))), "Enriched assets lost offline.");
        });
        Check("Verified catalog identities repair cross-title launchers without losing user edits", () => CatalogIdentityTests.Run(root));
        Check("Duration verification requires canonical provider identity and labels unverified estimates", () =>
        {
            var record = new JsonObject { ["time"] = 14, ["matchedTitle"] = "Duration Proof", ["timeUrl"] = "https://howlongtobeat.com/game/42",
                ["source"] = new JsonObject { ["time"] = "howlongtobeat-cache" } };
            Require(!CompletionDuration.HasSource(record), "Legacy URL without title evidence was verified.");
            record["timeTitle"] = "Unrelated Game";
            Require(!CompletionDuration.HasSource(record), "Cross-title duration was verified.");
            record["timeTitle"] = "Duration Proof";
            record["timeSamples"] = 0;
            Require(!CompletionDuration.HasSource(record), "A completion estimate without provider samples was verified.");
            record["timeSamples"] = 7;
            Require(CompletionDuration.HasSource(record), "Matching canonical title was rejected.");
            record["timeTitle"] = "Canonical Edition"; record["timeAlias"] = "Duration Proof";
            Require(CompletionDuration.HasSource(record), "Exact provider alias was rejected.");
            var game = new Game { Time = 14 };
            Require(game.Meta.Contains("unverified estimate", StringComparison.Ordinal), "Unverified catalog hours look verified.");
            game.TimeVerifiedAt = DateTime.UtcNow;
            Require(!game.Meta.Contains("unverified", StringComparison.Ordinal) && game.Meta.Contains("~14 h", StringComparison.Ordinal), "Verified estimate formatting changed.");
        });
        Check("Unsourced and stale completion hours remain eligible for automatic verification", () =>
        {
            var now = DateTime.UtcNow;
            var game = new Game { Id = "duration-age", Name = "Duration Age", Time = 22, Cover = Directory.EnumerateFiles(Path.Combine(store.Assets, "images"), "*.jpg", SearchOption.AllDirectories).First() };
            Require(MainWindow.MetadataDue(game, new(), now), "Bundled positive hours prevented source verification.");
            game.TimeVerifiedAt = now.AddDays(-29);
            Require(!MainWindow.MetadataDue(game, new(), now), "Fresh sourced duration retried.");
            game.TimeVerifiedAt = now.AddDays(-30);
            Require(MainWindow.MetadataDue(game, new(), now), "Stale duration never refreshed.");
            var attempts = new JsonObject { [game.Id] = new JsonObject { ["retryAfter"] = now.AddHours(1).ToString("O") } };
            Require(!MainWindow.MetadataDue(game, attempts, now), "Source verification bypassed outage backoff.");
            game.TimeVerifiedAt = now.AddDays(1);
            Require(MainWindow.TimeNeedsRefresh(game, now), "Future timestamp prevented verification indefinitely.");
        });
        Check("Damaged cover files repair atomically and cached fallbacks recover without restart", () =>
        {
            byte[] bytes = File.ReadAllBytes(Directory.EnumerateFiles(Path.Combine(store.Assets, "images"), "*.jpg", SearchOption.AllDirectories).First());
            var profile = new LibraryStore(Path.Combine(root, "artwork-repair"));
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            string path = LibraryStore.SafeChild(profile.Cache, "covers/" + hash + ".img"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[bytes.Length]);
            var initialWrite = File.GetLastWriteTimeUtc(path); var initialCreation = File.GetCreationTimeUtc(path);
            var game = new Game { Id = "metadata-proof", Name = "Metadata Proof", Cover = path, Time = 12, TimeVerifiedAt = DateTime.UtcNow };
            var converter = new CoverConverter();
            var fallback = converter.Convert(game, typeof(ImageSource), null!, null!);
            Require(!ArtworkFile.IsUsable(path) && MainWindow.MetadataDue(game, new(), DateTime.UtcNow), "Fresh hours hid a damaged cover.");
            var attempts = new JsonObject { [game.Id] = new JsonObject { ["retryAfter"] = DateTime.UtcNow.AddHours(1).ToString("O") } };
            Require(!MainWindow.MetadataDue(game, attempts, DateTime.UtcNow), "Damaged cover bypassed outage backoff.");
            using var client = new MetadataClient(new MetadataFixtureHandler(bytes));
            client.Refresh(game, profile, true, false, default).GetAwaiter().GetResult();
            // NTFS timestamp tunneling/coarse clocks can preserve every stat field
            // during a same-length replacement. Explicit invalidation must win.
            File.SetCreationTimeUtc(path, initialCreation); File.SetLastWriteTimeUtc(path, initialWrite);
            Require(File.ReadAllBytes(path).SequenceEqual(bytes) && ArtworkFile.IsUsable(path), "Existing damaged cache prevented replacement.");
            var repaired = converter.Convert(game, typeof(ImageSource), null!, null!);
            Require(!ReferenceEquals(repaired, fallback) && !MainWindow.MetadataDue(game, new(), DateTime.UtcNow), "Fallback cache survived repair or valid artwork retried.");
            File.Delete(path);
            Require(MainWindow.MetadataDue(game, new(), DateTime.UtcNow) && !ReferenceEquals(repaired, converter.Convert(game, typeof(ImageSource), null!, null!)), "Deleted cover remained cached as valid.");
            string wrongAddress = Path.Combine(profile.Cache, "covers", new string('0', 64) + ".img"); File.WriteAllBytes(wrongAddress, bytes);
            Require(!ArtworkFile.IsUsable(wrongAddress), "A valid image at the wrong content address was accepted.");
        });
        Check("Installer markers resolve local metadata identity without stripping title numbers", () => InstalledMetadataIdentityTests.Run(store));
        Check("GOG primary manifests resolve future local games without changing personal labels", () => GogInstalledIdentityTests.Run(store, Path.Combine(root, "gog-installed-identity")));
        Check("Periodic local scans preserve custom game names across restart", () => LocalScanNameTests.Run(root));
        Check("Local platform identity resolves canonical metadata without changing personal fields", () => {
            string cover = Directory.EnumerateFiles(Path.Combine(store.Assets, "images"), "*.jpg", SearchOption.AllDirectories).First();
            InstalledPlatformIdentityTests.Run(store, Path.Combine(root, "platform-identity"), File.ReadAllBytes(cover));
        });
        Check("Steam fallback survives hosted outage and rejects sequel metadata", () =>
        {
            string cover = Directory.EnumerateFiles(Path.Combine(store.Assets, "images"), "*.jpg", SearchOption.AllDirectories).First();
            SteamMetadataTests.Run(store, File.ReadAllBytes(cover));
        });
        Check("Later curated catalog time supersedes a cached genre estimate", () =>
        {
            var catalogGame = store.LoadGames(state, store.ReadConfig()).First(g => !g.IsLocal && g.Time > 0);
            double expected = catalogGame.Time;
            var all = store.ReadMetadata(); all[catalogGame.Id] = new JsonObject { ["time"] = 999, ["source"] = new JsonObject { ["time"] = "genre-estimate" } };
            store.CacheData("metadata.json", all.ToJsonString());
            Require(store.LoadGames(state, store.ReadConfig()).Single(g => g.Id == catalogGame.Id).Time == expected, "Cached estimate overwrote authoritative playtime.");
        });
        Check("Partial metadata refresh preserves source labels for unchanged fields", () =>
        {
            var game = store.LoadGames(state, store.ReadConfig()).First(g => !g.IsLocal && g.Time > 0);
            double catalogTime = game.Time;
            var all = store.ReadMetadata();
            all[game.Id] = new JsonObject { ["time"] = 12, ["source"] = new JsonObject { ["time"] = "genre-estimate", ["image"] = "old-cover" } };
            store.CacheData("metadata.json", all.ToJsonString());
            var response = new JsonObject { ["success"] = true, ["id"] = game.Id, ["name"] = game.Name,
                ["image"] = "https://covers.example.test/image.jpg", ["time"] = 35,
                ["source"] = new JsonObject { ["time"] = "known-override", ["image"] = "fixture-cover" } };
            string cover = Directory.EnumerateFiles(Path.Combine(store.Assets, "images"), "*.jpg", SearchOption.AllDirectories).First();
            using var client = new MetadataClient(new MetadataFixtureHandler(File.ReadAllBytes(cover), response.ToJsonString()));
            client.Refresh(game, store, true, false, default).GetAwaiter().GetResult();
            var saved = new LibraryStore(root).ReadMetadata()[game.Id]!;
            Require(DataJson.Number(saved["time"]) == 12 && DataJson.Text(saved["source"]?["time"]) == "genre-estimate", "Cover-only refresh relabeled unchanged time.");
            Require(store.LoadGames(state, store.ReadConfig()).Single(g => g.Id == game.Id).Time == catalogTime, "Relabeled estimate replaced curated catalog time.");
            response["source"]!["image"] = "unused-cover-source";
            using var timeClient = new MetadataClient(new MetadataFixtureHandler(File.ReadAllBytes(cover), response.ToJsonString()));
            timeClient.Refresh(game, store, false, true, default).GetAwaiter().GetResult();
            saved = new LibraryStore(root).ReadMetadata()[game.Id]!;
            Require(DataJson.Number(saved["time"]) == 35 && DataJson.Text(saved["source"]?["time"]) == "known-override" && DataJson.Text(saved["source"]?["image"]) == "fixture-cover", "Time-only refresh relabeled the unchanged cover.");
        });
        ImportSyncTests.AddChecks(Check, root);
        LibraryStore.AtomicWrite(Path.GetFullPath(report), DataJson.Write(new { at = DateTime.UtcNow, executable = Environment.ProcessPath, passed = failures == 0, tests = checks.Count, failures, fixtureRoot = root, checks }));
        return failures == 0 ? 0 : 1;
    }
    private sealed class MetadataFixtureHandler(byte[] cover, string? responseJson = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpContent body = request.RequestUri!.AbsolutePath.StartsWith("/api/")
                ? new StringContent(responseJson ?? "{\"success\":true,\"id\":\"metadata-proof\",\"name\":\"Metadata Proof\",\"image\":\"https://covers.example.test/image.jpg\",\"time\":12,\"source\":{\"time\":\"fixture\"}}")
                : new ByteArrayContent(cover);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body });
        }
    }
    private sealed class DockerLoginFixtureHandler : HttpMessageHandler
    {
        public bool ValidRequest, Reject;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            ValidRequest = request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == "https://hub.docker.com/v2/auth/token"
                && DataJson.Text(body["identifier"]) == "fixture-user" && DataJson.Text(body["secret"]) == "fixture-secret";
            return new HttpResponseMessage(Reject ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)
            { Content = new StringContent(Reject ? "fixture-secret untrusted error" : "{\"access_token\":\"fixture-jwt\"}") };
        }
    }
    private sealed class FixtureHandler : HttpMessageHandler
    {
        public bool Fail, RejectWrite;
        public int Posts;
        public JsonObject Config = JsonNode.Parse("{\"gameCategories\":{},\"hiddenTabs\":[],\"tabs\":[]}")!.AsObject();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Fail) throw new HttpRequestException("Simulated network loss");
            if (request.Method == HttpMethod.Post)
            {
                if (RejectWrite) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                Posts++; Config = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["success"] = true, ["config"] = Config.DeepClone(), ["configVersion"] = Posts.ToString() }.ToJsonString()) };
        }
    }
}

public partial class MainWindow
{
    private async Task RunUiProof(string report)
    {
        var checks = new List<object>();
        void Check(string name, bool passed) { checks.Add(new { name, passed, at = DateTime.UtcNow }); if (!passed) throw new InvalidOperationException("UI proof failed: " + name); }
        static T? FindVisual<T>(DependencyObject? root, Func<T, bool> predicate) where T : DependencyObject
        {
            if (root == null) return null;
            if (root is T candidate && predicate(candidate)) return candidate;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var found = FindVisual(VisualTreeHelper.GetChild(root, i), predicate);
                if (found != null) return found;
            }
            return null;
        }
        try
        {
            Check("Packaged WPF window visible with taskbar identity", IsVisible && ShowInTaskbar && Icon != null && Games.Count >= 1179);
            Check("Tray icon and useful menu created", tray is { Visible: true } && tray.ContextMenuStrip?.Items.Count == 5);
            Save();
            string persistenceId = "storage-failure-proof";
            int? previousRating = State.Ratings.TryGetValue(persistenceId, out int savedRating) ? savedRating : null;
            try
            {
                State.Ratings[persistenceId] = 4;
                using (var locked = new FileStream(Store.StatePath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    bool reportedFailure = false;
                    try { Save(); } catch (IOException) { reportedFailure = true; }
                    Check("A locked profile prevents a false successful-save result", reportedFailure);
                    var closeAttempt = new System.ComponentModel.CancelEventArgs();
                    OnClosing(this, closeAttempt);
                    Check("Unsaved changes prevent shutdown without cancelling the running window", closeAttempt.Cancel && !closing && !lifetime.IsCancellationRequested);
                }
                Save();
                Check("Saving recovers after storage becomes available and survives readback", Store.LoadState().Ratings.GetValueOrDefault(persistenceId) == 4);
            }
            finally
            {
                if (previousRating.HasValue) State.Ratings[persistenceId] = previousRating.Value;
                else State.Ratings.Remove(persistenceId);
                Save();
            }
            VerifyStartupEditingProtection(Check);
            await VerifyPersonalEditingPersistence(Check);
            if (!offline) Check("Actual packaged window loads live catalog and production config", Sync.Online && Sync.LastSync != null);
            ResetFilters(this, new());
            var playStateGame = filtered.FirstOrDefault(game => game.CanPlayWithWand) ?? filtered.First();
            GameList.ScrollIntoView(playStateGame);
            await Dispatcher.InvokeAsync(() => GameList.UpdateLayout(), DispatcherPriority.Render);
            var playStateContainer = GameList.ItemContainerGenerator.ContainerFromItem(playStateGame);
            var playButton = FindVisual<Button>(playStateContainer, candidate => System.Windows.Automation.AutomationProperties.GetAutomationId(candidate) == "PlayGame");
            var wandButton = FindVisual<Button>(playStateContainer, candidate => System.Windows.Automation.AutomationProperties.GetAutomationId(candidate) == "PlayWithWand");
            var exitButton = FindVisual<Button>(playStateContainer, candidate => System.Windows.Automation.AutomationProperties.GetAutomationId(candidate) == "ForceExitGameAndWand");
            var pauseButton = FindVisual<Button>(playStateContainer, candidate => System.Windows.Automation.AutomationProperties.GetAutomationId(candidate) == "PauseGame");
            string storageFixture = Path.Combine(Store.Root, "storage-ui-fixture");
            Directory.CreateDirectory(storageFixture);
            File.WriteAllBytes(Path.Combine(storageFixture, "data.bin"), new byte[4096]);
            bool previousInstalled = playStateGame.Installed;
            string? previousFolder = State.InstallationFolders.GetValueOrDefault(playStateGame.Id);
            try
            {
                playStateGame.Installed = false;
                playStateGame.Notify(null);
                await Dispatcher.InvokeAsync(() => GameList.UpdateLayout(), DispatcherPriority.Render);
                Check("Uninstalled cards hide all play controls", playButton is { IsVisible: false } && pauseButton is { IsVisible: false }
                    && wandButton is { IsVisible: false } && exitButton is { IsVisible: false });
                bool launchRejected = false;
                try { await PlayGame(playStateGame); } catch (InvalidOperationException) { launchRejected = true; }
                Check("Uninstalled games cannot launch through a direct play action", launchRejected);
                playStateGame.Installed = true;
                playStateGame.Notify(null);
                installedStorage.Remove(playStateGame.Id);
                ApplyInstalledStorage();
                Check("Missing installed measurements explain availability without inventing a size", playStateGame.InstalledStorageLabel == "Installed files not yet measured");
                State.InstallationFolders[playStateGame.Id] = storageFixture;
                installedStorage[playStateGame.Id] = (storageFixture, await InstalledStorage.MeasureAsync(storageFixture, lifetime.Token));
                ApplyInstalledStorage();
                await Dispatcher.InvokeAsync(() => GameList.UpdateLayout(), DispatcherPriority.Render);
                Check("Installed cards expose play and pause controls", playButton is { IsVisible: true } && pauseButton is { IsVisible: true });
                var storageText = FindVisual<TextBlock>(playStateContainer, candidate => System.Windows.Automation.AutomationProperties.GetAutomationId(candidate) == "InstalledFileSize");
                Check("Installed file size is bound to a measured installation folder", storageText?.Text.Contains("GB installed files", StringComparison.Ordinal) == true
                    && installedStorage[playStateGame.Id].Result is { Bytes: 4096, Complete: true }
                    && playStateGame.InstalledStorageDetail.Contains(storageFixture, StringComparison.Ordinal));
                installedStorage[playStateGame.Id] = (storageFixture, new InstalledStorageResult(4096, false, DateTime.UtcNow, 1, 1, "Fixture unreadable file"));
                ApplyInstalledStorage();
                Check("Incomplete installed size is explicitly labeled", playStateGame.InstalledStorageLabel.StartsWith("At least ", StringComparison.Ordinal)
                    && playStateGame.InstalledStorageDetail.Contains("Incomplete:", StringComparison.Ordinal));
            }
            finally
            {
                playStateGame.Installed = previousInstalled;
                if (previousFolder == null) State.InstallationFolders.Remove(playStateGame.Id);
                else State.InstallationFolders[playStateGame.Id] = previousFolder;
                installedStorage.Remove(playStateGame.Id);
                ApplyInstalledStorage();
            }
            Check("Idle pause is disabled; cards retain backup and restore", pauseButton is { IsEnabled: false }
                && FindVisual<Button>(playStateContainer, b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "BackupGame") != null
                && FindVisual<Button>(playStateContainer, b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "RestoreGame") != null);
            Check("Idle cards reflect exact Wand eligibility while hiding force-exit controls", playButton?.Content?.ToString()?.Contains("Play", StringComparison.Ordinal) == true
                && wandButton?.Visibility == (playStateGame.CanPlayWithWand ? Visibility.Visible : Visibility.Collapsed)
                && exitButton?.Visibility == Visibility.Collapsed);
            string processFixtureFolder = Path.Combine(Store.Root, "process-fixture");
            Directory.CreateDirectory(processFixtureFolder);
            string processFixture = Path.Combine(processFixtureFolder, "TrackedGame.exe");
            File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe"), processFixture, true);
            double playtimeBeforeFixture = State.PlayTimeSeconds.GetValueOrDefault(playStateGame.Id);
            using var trackedFixture = Process.Start(new ProcessStartInfo(processFixture, "127.0.0.1 -n 120") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })
                ?? throw new InvalidOperationException("Could not start the tracked UI process fixture.");
            try
            {
                playStateGame.Installed = true;
                playStateGame.Notify(null);
                await ObserveWandGameProcess(playStateGame, processFixture, lifetime.Token);
                await Task.Delay(350);
                if (activePlays.TryGetValue(playStateGame.Id, out var observedSession)) SamplePlaySession(playStateGame.Id, observedSession, announceTransition: false);
                await Dispatcher.InvokeAsync(() => { GameList.ScrollIntoView(playStateGame); GameList.UpdateLayout(); }, DispatcherPriority.Render);
                playStateContainer = GameList.ItemContainerGenerator.ContainerFromItem(playStateGame);
                playButton = FindVisual<Button>(playStateContainer, candidate => System.Windows.Automation.AutomationProperties.GetAutomationId(candidate) == "PlayGame");
                exitButton = FindVisual<Button>(playStateContainer, candidate => System.Windows.Automation.AutomationProperties.GetAutomationId(candidate) == "ForceExitGameAndWand");
                Check("A real running process changes Play to Playing, exposes Exit, and accrues playtime", playButton?.Content?.ToString()?.Contains("Playing", StringComparison.Ordinal) == true
                    && exitButton?.Visibility == Visibility.Visible && exitButton.IsVisible
                    && State.PlayTimeSeconds.GetValueOrDefault(playStateGame.Id) > playtimeBeforeFixture);
                pauseButton = FindVisual<Button>(playStateContainer, b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "PauseGame");
                var beforePauseColor = pauseButton?.Background.ToString();
                pauseButton?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var pauseWait = Stopwatch.StartNew();
                while (activePlays[playStateGame.Id].PauseChanging && pauseWait.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(50);
                await Dispatcher.InvokeAsync(() => GameList.UpdateLayout(), DispatcherPriority.Render);
                Check("Pause click confirms suspension and changes text and color", playStateGame.IsPlayPaused
                    && pauseButton?.Content?.ToString() == "Resume" && pauseButton.Background.ToString() != beforePauseColor);
                double heldTime = State.PlayTimeSeconds[playStateGame.Id];
                await Task.Delay(1100);
                SamplePlaySession(playStateGame.Id, activePlays[playStateGame.Id], false);
                Check("Native pause holds the playtime ledger", Math.Abs(State.PlayTimeSeconds[playStateGame.Id] - heldTime) < 0.1);
                pauseButton?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                pauseWait.Restart();
                while (activePlays[playStateGame.Id].PauseChanging && pauseWait.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(50);
                await Task.Delay(1100);
                SamplePlaySession(playStateGame.Id, activePlays[playStateGame.Id], false);
                Check("Resume click restores color and active timing", !playStateGame.IsPlayPaused
                    && pauseButton?.Content?.ToString() == "Pause" && pauseButton.Background.ToString() == beforePauseColor
                    && State.PlayTimeSeconds[playStateGame.Id] > heldTime + 0.5);
                RequestImmediateProcessTreeExit(trackedFixture);
                trackedFixture.WaitForExit(5000);
                if (activePlays.TryGetValue(playStateGame.Id, out var completedSession)) { CommitPlaySession(playStateGame.Id, completedSession); Save(); }
                await Dispatcher.InvokeAsync(() => { GameList.ScrollIntoView(playStateGame); GameList.UpdateLayout(); }, DispatcherPriority.Render);
                playStateContainer = GameList.ItemContainerGenerator.ContainerFromItem(playStateGame);
                playButton = FindVisual<Button>(playStateContainer, candidate => System.Windows.Automation.AutomationProperties.GetAutomationId(candidate) == "PlayGame");
                exitButton = FindVisual<Button>(playStateContainer, candidate => System.Windows.Automation.AutomationProperties.GetAutomationId(candidate) == "ForceExitGameAndWand");
                Check("A finished real process restores Play and hides the force-exit control", playButton?.Content?.ToString()?.Contains("Playing", StringComparison.Ordinal) != true && exitButton?.Visibility == Visibility.Collapsed);
            }
            finally
            {
                try
                {
                    if (!trackedFixture.HasExited) RequestImmediateProcessTreeExit(trackedFixture);
                    trackedFixture.WaitForExit(5000);
                }
                catch (InvalidOperationException) { }
                playStateGame.Installed = previousInstalled;
                playStateGame.Notify(null);
            }
            using (var exitedFixture = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe"), "127.0.0.1 -n 1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })
                ?? throw new InvalidOperationException("Could not start the exited-process UI fixture."))
            {
                exitedFixture.WaitForExit(5000);
                Check("A process that exits during Wand confirmation is ignored without stale tracking", !TrackPlayProcess(playStateGame, exitedFixture, usesWand: true, ownsProcess: false) && !activePlays.ContainsKey(playStateGame.Id));
            }
            int saveCalls = 0;
            bool persistedBeforeSave = false;
            string observedSavePath = "";
            string? originalProofLauncher = State.LaunchPaths.GetValueOrDefault(playStateGame.Id);
            // Match the actual launch contract: a just-started process may not
            // expose MainModule yet, so tracking uses the selected launcher.
            State.LaunchPaths[playStateGame.Id] = processFixture;
            saveOperationProof = (path, restore) =>
            {
                saveCalls++;
                observedSavePath = path;
                persistedBeforeSave = !restore && string.Equals(path, processFixture, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Store.LoadState().PendingGameBackups.GetValueOrDefault(playStateGame.Id), path, StringComparison.OrdinalIgnoreCase);
                return Task.FromResult(new GameSaveResult("isolated-save-proof"));
            };
            try
            {
                using var normal = Process.Start(new ProcessStartInfo(processFixture, "127.0.0.1 -n 2") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
                _ = normal.Handle;
                TrackPlayProcess(playStateGame, Process.GetProcessById(normal.Id));
                await RunGameSave(playStateGame.Id, playStateGame.Name, processFixture, true);
                Check("Restore refuses a running game without invoking the helper", saveCalls == 0);
                var exitWait = Stopwatch.StartNew();
                while (activePlays.ContainsKey(playStateGame.Id) && exitWait.Elapsed < TimeSpan.FromSeconds(8))
                { await Task.Delay(100); UpdatePlaySessions(); }
                Check($"Normal process exit dispatches exactly one durable exact-game backup (calls={saveCalls}, durable={persistedBeforeSave}, path={observedSavePath})", saveCalls == 1 && persistedBeforeSave
                    && !State.PendingGameBackups.ContainsKey(playStateGame.Id));
                saveOperationProof = (_, _) => throw new IOException("Expected isolated backup failure");
                await RunGameSave(playStateGame.Id, playStateGame.Name, processFixture, false);
                Check("Failed backup remains queued across restart", Store.LoadState().PendingGameBackups.GetValueOrDefault(playStateGame.Id) == processFixture);
                saveOperationProof = (_, _) => Task.FromResult(new GameSaveResult("isolated-retry-proof"));
                await RunGameSave(playStateGame.Id, playStateGame.Name, processFixture, false);
                Check("Successful backup retry clears the durable queue", !Store.LoadState().PendingGameBackups.ContainsKey(playStateGame.Id));
                string noSaveReceiptPath = Path.Combine(Store.Root, "isolated-no-save-receipt");
                Directory.CreateDirectory(noSaveReceiptPath);
                saveOperationProof = (_, _) => Task.FromResult(new GameSaveResult("isolated-no-save-proof", true, noSaveReceiptPath));
                await RunGameSave(playStateGame.Id, playStateGame.Name, processFixture, false);
                string receiptMarker = "receipt: ";
                int receiptStart = StatusText.Text.IndexOf(receiptMarker, StringComparison.Ordinal);
                string visibleReceipt = receiptStart >= 0 ? StatusText.Text[(receiptStart + receiptMarker.Length)..].Trim() : "";
                Check("Confirmed no-save outcome displays its physical receipt and clears retry queue", !Store.LoadState().PendingGameBackups.ContainsKey(playStateGame.Id)
                    && StatusText.Text.Contains("No save data found", StringComparison.Ordinal)
                    && receiptStart >= 0 && Directory.Exists(visibleReceipt)
                    && !StatusText.Text.Contains("completed", StringComparison.Ordinal));
            }
            finally
            {
                saveOperationProof = null;
                if (originalProofLauncher == null) State.LaunchPaths.Remove(playStateGame.Id);
                else State.LaunchPaths[playStateGame.Id] = originalProofLauncher;
            }
            int ownedWindowsBeforeFailure = OwnedWindows.Count;
            ReportUiFailure("Non-modal failure proof", new InvalidOperationException("fixture process exited"));
            Check("Recoverable operation failures stay non-modal and leave the library interactive", OwnedWindows.Count == ownedWindowsBeforeFailure && StatusText.Text.Contains("fixture process exited", StringComparison.Ordinal));
            SearchBox.Text = "STAR OCEAN"; ApplyFilter();
            Check("Search filters native list", filtered.Count > 0 && filtered.All(g => g.Name.Contains("STAR OCEAN", StringComparison.OrdinalIgnoreCase) || g.Id.Contains("STAR OCEAN", StringComparison.OrdinalIgnoreCase)));
            SelectAll(this, new()); Check("Select all operates on filtered items", filtered.All(g => g.Selected));
            DeselectAll(this, new()); Check("Clear selection works", Games.All(g => !g.Selected));
            SearchBox.Text = "no-such-game-" + Guid.NewGuid(); ApplyFilter(); Check("Empty view is recoverable", EmptyState.Visibility == Visibility.Visible);
            ResetFilters(this, new()); Check("Reset restores results", filtered.Count > 0);
            Check("Website parity controls exist in the native action bar", MaxSizeFilter != null
                && FindVisual<Button>(this, button => AutomationProperties.GetAutomationId(button) == "SelectCurrentCategory") != null
                && FindVisual<Button>(this, button => AutomationProperties.GetAutomationId(button) == "RandomVisibleGame") != null
                && FindVisual<Button>(this, button => AutomationProperties.GetAutomationId(button) == "CopySelectedCommands") != null);
            SelectCurrentCategory(this, new());
            Check("Select category chooses visible entries without changing assignments", filtered.All(game => game.Selected));
            DeselectAll(this, new());
            RandomVisibleGame(this, new());
            Check("Random selection comes from the current eligible view", GameList.SelectedItem is Game randomChoice
                && filtered.Contains(randomChoice) && !randomChoice.IsNonGame && !randomChoice.RequiresGameIdentity);
            MaxSizeFilter!.Text = "0.5"; ApplyFilter();
            Check("Maximum download filter excludes unknown sizes", filtered.All(game => MainWindow.WithinMaxDownloadSize(game, 0.5)));
            ResetFilters(this, new());
            var originalState = DataJson.Read<UserState>(DataJson.Write(State));
            try
            {
                Games = new()
                {
                    new() { Id = "filter-alpha", Name = "Filter Alpha", Category = "new", CategoryName = "New", Rating = 5, Installed = true, CanPlayWithWand = true, Time = 10, DockerImageUrl = "https://hub.docker.com/r/proof/repo/tags?name=filter-alpha" },
                    new() { Id = "filter-beta", Name = "Filter Beta", Category = "rpg", CategoryName = "RPG", Rating = 1, Time = 20 },
                    new() { Id = "filter-zero", Name = "Filter Zero", Category = "new", CategoryName = "New", Rating = 0 },
                    new() { Id = "filter-hidden", Name = "Filter Hidden", Category = "not_for_me", CategoryName = "Private", Rating = 3 }
                };
                catalogStatsDirty = true; tab = "rpg"; SearchBox.Text = "Filter Alpha"; ApplyFilter();
                Check("Search crosses categories without changing catalog privacy", filtered.Count == 1 && filtered[0].Id == "filter-alpha");
                var priorReliability = State.LocalCatalog["reliability"]?.DeepClone();
                try
                {
                    var reliability = State.LocalCatalog["reliability"] is JsonObject existingReliability ? (JsonObject)existingReliability.DeepClone() : new JsonObject();
                    var visibility = reliability["categoryVisibility"] is JsonObject existingVisibility ? (JsonObject)existingVisibility.DeepClone() : new JsonObject();
                    var rules = visibility["categories"] is JsonObject existingRules ? (JsonObject)existingRules.DeepClone() : new JsonObject();
                    rules["not_for_me"] = new JsonObject { ["hideTab"] = false, ["hideGamesFromAll"] = true };
                    visibility["schemaVersion"] = CategoryVisibility.CurrentSchemaVersion; visibility["categories"] = rules;
                    reliability["schemaVersion"] = CategoryVisibility.CurrentSchemaVersion; reliability["categoryVisibility"] = visibility;
                    State.LocalCatalog["reliability"] = reliability;
                    tab = "all"; SearchBox.Text = "Filter"; ApplyFilter();
                    Check("All games search respects Hide games from All games", filtered.Count == 3 && filtered.All(g => g.Id != "filter-hidden"));
                }
                finally
                {
                    if (priorReliability == null) State.LocalCatalog.Remove("reliability");
                    else State.LocalCatalog["reliability"] = priorReliability;
                }
                string average = AverageTime.Text, covers = CoverCount.Text, percent = InstalledPercent.Text;
                SearchBox.Text = "hub.docker.com/r/proof"; ApplyFilter(); Check("Docker URL is searchable", filtered.Count == 1 && filtered[0].Id == "filter-alpha");
                Check("Catalog statistics stay constant while filtering", AverageTime.Text == average && CoverCount.Text == covers && InstalledPercent.Text == percent);
                State.LaunchPaths["filter-beta"] = @"F:\NativeProof\UniqueLauncher.exe"; SearchBox.Text = "UniqueLauncher"; ApplyFilter(); Check("Installed launcher path is searchable", filtered.Count == 1 && filtered[0].Id == "filter-beta");
                SearchBox.Text = "Filter"; tab = "installed"; ApplyFilter(); Check("Installed view remains limited during global search", filtered.Count == 1 && filtered[0].Installed);
                tab = "all"; InstalledOnlyFilter.IsChecked = true; ApplyFilter(); Check("Installed-only control composes with global search", filtered.Count == 1 && filtered[0].Installed);
                WithoutInstalledFilter.IsChecked = true; ApplyFilter(); Check("Without-installed control excludes installed games", filtered.Count == 2 && filtered.All(g => !g.Installed));
                Games.Single(g => g.Id == "filter-hidden").Installed = true;
                Games.Single(g => g.Id == "filter-hidden").CanPlayWithWand = true;
                WandIncludedFilter.IsChecked = true; ApplyFilter(); Check("Wand-included control shows the complete registered set across category privacy", filtered.Count == 2 && filtered.All(g => g.Installed && g.CanPlayWithWand) && filtered.Any(g => g.Id == "filter-alpha") && filtered.Any(g => g.Id == "filter-hidden"));
                ResetFilters(this, new()); Check("Reset clears installed filters", InstalledOnlyFilter.IsChecked == false && WithoutInstalledFilter.IsChecked == false && WandIncludedFilter.IsChecked == false);
                SortBox.SelectedItem = "Rating (Low–High)"; ApplyFilter(); Check("Lowest rating sort uses ascending scores with unrated games last", filtered.Select(g => g.Rating).SequenceEqual(new[] { 1, 5, 0 }));
            }
            finally { SearchBox.Text = ""; InstalledOnlyFilter.IsChecked = false; WithoutInstalledFilter.IsChecked = false; WandIncludedFilter.IsChecked = false; RestoreImportedState(originalState); }
            var gamesBeforeCoverProof = Games;
            var tabBeforeCoverProof = tab;
            string searchBeforeCoverProof = SearchBox.Text;
            object? sortBeforeCoverProof = SortBox.SelectedItem;
            object? tagBeforeCoverProof = TagBox.SelectedItem;
            int ratingBeforeCoverProof = RatingBox.SelectedIndex;
            bool? installedOnlyBeforeCoverProof = InstalledOnlyFilter.IsChecked;
            bool? withoutInstalledBeforeCoverProof = WithoutInstalledFilter.IsChecked;
            bool? wandIncludedBeforeCoverProof = WandIncludedFilter.IsChecked;
            bool statsDirtyBeforeCoverProof = catalogStatsDirty;
            try
            {
                string cachedCover = Directory.EnumerateFiles(Path.Combine(Store.Assets, "images"), "*.jpg", SearchOption.AllDirectories).First();
                var noCover = new Game { Id = "no-cover-proof", Name = "No Cover Proof", Category = "new", CategoryName = "New", Cover = "" };
                var covered = new Game { Id = "covered-proof", Name = "Covered Proof", Category = "new", CategoryName = "New", Cover = cachedCover };
                Games = new() { noCover, covered };
                tab = "all"; SearchBox.Text = ""; SortBox.SelectedItem = "Name A–Z"; TagBox.SelectedItem = "All tags"; RatingBox.SelectedIndex = 0;
                InstalledOnlyFilter.IsChecked = false; WithoutInstalledFilter.IsChecked = false; WandIncludedFilter.IsChecked = false;
                catalogStatsDirty = true; ApplyFilter(); GameList.ScrollIntoView(noCover);
                await Dispatcher.InvokeAsync(() => GameList.UpdateLayout(), DispatcherPriority.Render);
                var noCoverContainer = GameList.ItemContainerGenerator.ContainerFromItem(noCover);
                var fallback = FindVisual<TextBlock>(noCoverContainer, candidate => candidate.Text == noCover.Initial);
                Check("No-cover cards render a deterministic nonblank initial fallback", noCoverContainer != null && fallback != null && fallback.Text == noCover.Initial && !string.IsNullOrWhiteSpace(fallback.Text) && fallback.IsVisible && fallback.ActualWidth > 0 && fallback.ActualHeight > 0);
                Check("Cover statistics count cached artwork but exclude fallback cards", CoverCount.Text == "1");
            }
            finally
            {
                Games = gamesBeforeCoverProof; tab = tabBeforeCoverProof; SearchBox.Text = searchBeforeCoverProof; SortBox.SelectedItem = sortBeforeCoverProof; TagBox.SelectedItem = tagBeforeCoverProof; RatingBox.SelectedIndex = ratingBeforeCoverProof;
                InstalledOnlyFilter.IsChecked = installedOnlyBeforeCoverProof; WithoutInstalledFilter.IsChecked = withoutInstalledBeforeCoverProof; WandIncludedFilter.IsChecked = wandIncludedBeforeCoverProof;
                catalogStatsDirty = true; ApplyFilter(); catalogStatsDirty = statsDirtyBeforeCoverProof;
            }
            var before = State.Settings.Theme; ToggleTheme(this, new()); Check("Theme switches", State.Settings.Theme != before); ToggleTheme(this, new());
            HideToTray(); Check("Minimize / hide retains tray", !IsVisible && tray!.Visible); RestoreWindow(); Check("Restore returns window", IsVisible && WindowState == WindowState.Normal);
            var job = new JobWindow(Store, "Write-Output 'native-progress-proof'; exit 7", Array.Empty<string>());
            int completedEvents = 0; bool? completionSuccess = null; bool completionAfterStopped = false;
            job.Completed += success => { completedEvents++; completionSuccess = success; completionAfterStopped = !job.Running; };
            job.Show();
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (job.LastExitCode == null && DateTime.UtcNow < deadline) await Task.Delay(100);
            Check("Progress window captures real process output and nonzero exit", job.LastExitCode == 7 && job.DisplayedOutput.Contains("native-progress-proof") && job.DisplayedOutput.Contains("Failed"));
            Check("Failed process completion fires once after stopping without marking installation successful", completedEvents == 1 && completionSuccess == false && completionAfterStopped);
            job.Close(); RestoreWindow();
            string markerRoot = Path.Combine(Store.Root, "immediate-marker-proof");
            string markerId = "immediate-proof";
            string markerFolder = Path.Combine(markerRoot, DockerScripts.InstallFolder(markerId));
            Directory.CreateDirectory(markerFolder);
            var markerJobScript = "$folder = " + DockerScripts.PsQuote(markerFolder) + "; New-Item -ItemType Directory -Force -Path $folder | Out-Null; Set-Content -LiteralPath (Join-Path $folder 'immediate-proof.exe') -Value 'fixture'; Set-Content -LiteralPath (Join-Path $folder '" + DockerScripts.CompletionMarkerName + "') -Value ('GameLibraryManager|" + markerId + "|' + $env:GLM_INSTALL_OPERATION_ID); Start-Sleep -Seconds 3";
            var markerJob = new JobWindow(Store, markerJobScript, Array.Empty<string>(), completionDestination: markerRoot, completionGameIds: new[] { markerId });
            int markerEvents = 0; bool markerObservedWhileRunning = false;
            markerJob.GameCompleted += id => { markerEvents++; markerObservedWhileRunning = markerJob.Running; return Task.CompletedTask; };
            markerJob.Show();
            var markerDeadline = DateTime.UtcNow.AddSeconds(10);
            while (markerEvents == 0 && DateTime.UtcNow < markerDeadline) await Task.Delay(100);
            Check("Install marker updates are delivered before the batch process exits", markerEvents == 1 && markerObservedWhileRunning);
            while (markerJob.LastExitCode == null && DateTime.UtcNow < markerDeadline) await Task.Delay(100);
            markerJob.Close(); RestoreWindow();
            if (offline)
            {
                var proofPassword = Environment.GetEnvironmentVariable("GLM_PROOF_ADMIN_PASSWORD");
                if (proofPassword != null)
                {
                    _ = Dispatcher.BeginInvoke(new Action(() => AdminSignIn(this, new())));
                    await Task.Delay(120);
                    var signIn = System.Windows.Application.Current.Windows.OfType<EditorWindow>().Single(w => w.Title == "Admin sign in");
                    signIn.Fields.Children.OfType<System.Windows.Controls.PasswordBox>().Single().Password = proofPassword;
                    signIn.Fields.Children.OfType<System.Windows.Controls.Button>().Single(b => (string)b.Content == "Sign in").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    Check("Native admin sign-in validates the existing website password", IsAdmin);
                }
                adminToken = null; // Windows library organization must work without shared admin sign-in.
                string pendingBeforeLocalEdits = DataJson.Write(State.Pending);
                string localProofName = "Native proof " + Guid.NewGuid().ToString("N")[..8];
                string localProofId = localProofName.ToLowerInvariant().Replace(' ', '_');
                async Task<EditorWindow> CategoriesDialog()
                {
                    _ = Dispatcher.BeginInvoke(new Action(() => ManageCategories(this, new())));
                    var deadline = DateTime.UtcNow.AddSeconds(3);
                    while (DateTime.UtcNow < deadline)
                    {
                        var visible = System.Windows.Application.Current.Windows.OfType<EditorWindow>()
                            .Where(w => w.Title == "Manage categories" && w.IsVisible).ToArray();
                        if (visible.Length == 1) return visible[0];
                        await Task.Delay(50);
                    }
                    throw new InvalidOperationException("The Manage categories dialog did not settle to one visible window.");
                }
                void Click(EditorWindow dialog, string name) => dialog.Fields.Children.OfType<System.Windows.Controls.Button>().Single(b => (string)b.Content == name).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                var categoryDialog = await CategoriesDialog();
                categoryDialog.Fields.Children.OfType<System.Windows.Controls.TextBox>().Single().Text = localProofName;
                Click(categoryDialog, "Create category");
                Check("Offline native category creation saves locally without admin sign-in", !IsAdmin && Store.LoadCategories(Sync.Effective(State)).Any(c => c.Id == localProofId));
                categoryDialog = await CategoriesDialog();
                var choices = categoryDialog.Fields.Children.OfType<System.Windows.Controls.ComboBox>().Single();
                choices.SelectedItem = choices.Items.Cast<Category>().Single(c => c.Id == localProofId);
                categoryDialog.Fields.Children.OfType<System.Windows.Controls.TextBox>().Single().Text = "Renamed proof category";
                var categoryToggles = categoryDialog.Fields.Children.OfType<System.Windows.Controls.CheckBox>().ToArray();
                categoryToggles.Single(toggle => AutomationProperties.GetAutomationId(toggle) == "CategoryHideTab").IsChecked = true;
                categoryToggles.Single(toggle => AutomationProperties.GetAutomationId(toggle) == "CategoryHideGamesFromAll").IsChecked = false;
                Click(categoryDialog, "Save name and visibility");
                var localProofVisibility = State.LocalCatalog["reliability"]?["categoryVisibility"]?["categories"]?[localProofId];
                Check("Offline native rename and independent visibility controls save local edits", Store.LoadCategories(Sync.Effective(State)).Any(c => c.Id == localProofId && c.Name == "Renamed proof category")
                    && Hidden(Sync.Effective(State)).Contains(localProofId)
                    && localProofVisibility?["hideTab"]?.ToString() == "true"
                    && localProofVisibility?["hideGamesFromAll"]?.ToString() == "false");
                Check("Category edits survive a state reload", Store.LoadState().LocalCatalog["tabs"]!.AsArray().Any(t => DataJson.Text(t?["id"]) == localProofId));
                await Refresh(false); await Refresh(true);
                Check("Offline local edits never request network access or change the shared queue", offlineNetwork is { Attempts: 0 } && !Sync.Online && Sync.LastSync == null && DataJson.Write(Store.LoadState().Pending) == pendingBeforeLocalEdits);
                adminToken = null;
            }
            var wandProofState = DataJson.Read<UserState>(DataJson.Write(State));
            var previousWandIds = wandIncludedGameIds.ToArray();
            try
            {
                ResetFilters(this, new());
                string firstWandPath = Path.Combine(Store.Root, "wand-fixture-a.exe"), secondWandPath = Path.Combine(Store.Root, "wand-fixture-b.exe");
                File.Copy(processFixture, firstWandPath, true); File.Copy(processFixture, secondWandPath, true);
                var first = new WandSupportedGame("live-wand-fixture-a", "proof-a", "proof-a", "Live Wand fixture A", firstWandPath);
                var second = new WandSupportedGame("live-wand-fixture-b", "proof-b", "proof-b", "Live Wand fixture B", secondWandPath);
                wandRegistrationProof = () => new[] { first };
                await SynchronizeWandSupportedGamesAsync();
                WandIncludedFilter.IsChecked = true;
                while (wandRegistrationSyncing) await Task.Delay(20);
                ApplyFilter();
                Check("Wand checkbox refreshes current registrations and displays their count", filtered.Count == 1 && CatalogCaption.Text.StartsWith("1 games", StringComparison.Ordinal));
                wandRegistrationProof = () => new[] { first, second };
                await SynchronizeWandSupportedGamesAsync();
                Check("Wand membership additions update visible count", filtered.Count == 2 && CatalogCaption.Text.StartsWith("2 games", StringComparison.Ordinal));
                var wandMetadata = Store.ReadMetadata();
                wandMetadata["wand:proof-a"] = new JsonObject { ["time"] = 17.5, ["diskRequirementGb"] = 25 };
                Store.CacheData("metadata.json", wandMetadata.ToJsonString());
                State.Ratings["wand:proof-a"] = 4; Save(); Reload();
                var restoredWand = Games.Single(g => g.Id == "wand:proof-a");
                Check("Wand-only rows and enriched metadata survive library reload", restoredWand.IsLocal && restoredWand.Time == 17.5 && restoredWand.DiskRequirementGb == 25 && restoredWand.Rating == 4 && Store.LoadState().WandGames.Count == 2);
                Check("New Wand identities remain eligible for automatic metadata", MainWindow.MetadataDue(Games.Single(g => g.Id == "wand:proof-b"), new(), DateTime.UtcNow));
                wandRegistrationProof = () => Array.Empty<WandSupportedGame>();
                await SynchronizeWandSupportedGamesAsync();
                Check("An empty current Wand library clears the previous count", filtered.Count == 0 && CatalogCaption.Text.StartsWith("0 games", StringComparison.Ordinal));
                Check("Removing Wand membership preserves installed library data", Games.Any(g => g.Id == "wand:proof-a" && g.Time == 17.5 && g.Installed));
            }
            finally
            {
                wandRegistrationProof = null;
                wandIncludedGameIds.Clear(); wandIncludedGameIds.UnionWith(previousWandIds);
                WandIncludedFilter.IsChecked = false;
                RestoreImportedState(wandProofState);
            }
            VerifyAutomaticMetadataSorting(Check);
            await VerifyAutomaticMetadataEntryPaths(Check, processFixture);
            if (Program.PauseProof)
            {
                var pauseProof = await RunPauseProof();
                checks.Add(new { name = "AHK Ctrl+H pause/resume timing", passed = pauseProof.Passed, detail = pauseProof.Detail, at = DateTime.UtcNow });
                if (!pauseProof.Passed) throw new InvalidOperationException("AHK pause proof failed: " + pauseProof.Detail);
            }
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Width = MinWidth; Height = Math.Max(MinHeight, 800);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var content = (FrameworkElement)Content;
            Check("Search, combined filter and statistics fit the minimum native window", new FrameworkElement[] { SearchBox, SortBox, InstalledOnlyFilter, WithoutInstalledFilter, AverageTime, CoverCount, GameList }.All(control =>
            {
                var bounds = control.TransformToAncestor(content).TransformBounds(new Rect(0, 0, control.ActualWidth, control.ActualHeight));
                return control.ActualWidth > 0 && bounds.Left >= -1 && bounds.Right <= content.ActualWidth + 1 && bounds.Top >= -1 && bounds.Bottom <= content.ActualHeight + 1;
            }));
            var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.ChangeExtension(report, ".png"))) png.Save(file);
            LibraryStore.AtomicWrite(report, DataJson.Write(new { at = DateTime.UtcNow, passed = true, executable = Environment.ProcessPath, catalog = Games.Count, checks }));
        }
        catch (Exception ex) { LibraryStore.AtomicWrite(report, DataJson.Write(new { at = DateTime.UtcNow, passed = false, error = ex.ToString(), checks })); }
        finally { Close(); }
    }

    private async Task<(bool Passed, string Detail)> RunPauseProof()
    {
        string originalPath = State.Settings.FrozenProcessesPath;
        string proofRoot = Path.Combine(Store.Root, "pause-proof-" + Guid.NewGuid().ToString("N"));
        string proofState = Path.Combine(proofRoot, "frozen-processes.ini");
        string id = "local:ahk-pause-proof-" + Guid.NewGuid().ToString("N");
        var game = new Game { Id = id, Name = "AHK pause proof" };
        Process? owned = null;
        try
        {
            Directory.CreateDirectory(proofRoot);
            State.Settings.FrozenProcessesPath = proofState;
            Games.Add(game);
            State.PlayTimeSeconds[id] = 0;
            string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            var start = new ProcessStartInfo(powershell)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("-NoLogo"); start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command"); start.ArgumentList.Add("Start-Sleep -Seconds 20");
            owned = Process.Start(start) ?? throw new InvalidOperationException("The pause-proof fixture process did not start.");
            TrackPlayProcess(game, owned, ownsProcess: true);
            owned = null;
            var session = activePlays[id];
            if (session.ProcessCreationStamps.Count == 0) throw new InvalidOperationException("The fixture process creation stamp could not be captured.");
            int pid = session.Process.Id;
            string created = session.ProcessCreationStamps[pid].PadLeft(16, '0');
            async Task<bool> WaitFor(Func<bool> condition)
            {
                var deadline = DateTime.UtcNow.AddSeconds(6);
                while (DateTime.UtcNow < deadline)
                {
                    if (condition()) return true;
                    await Task.Delay(100);
                }
                return condition();
            }
            void WriteState(int count, string state)
            {
                string stale = "[FrozenProcess1]\r\nPid=" + pid + "\r\nCreated=" + created + "\r\nMode=game_suspend\r\nState=" + state + "\r\n";
                File.WriteAllText(proofState, "[FrozenProcesses]\r\nCount=" + count + "\r\n" + stale);
            }

            await Task.Delay(1400);
            UpdatePlaySessions();
            double beforePause = State.PlayTimeSeconds[id];
            WriteState(1, "paused");
            bool pausedSeen = await WaitFor(() => game.IsPlayPaused);
            double pauseStart = State.PlayTimeSeconds[id];
            await Task.Delay(1600);
            UpdatePlaySessions();
            double pauseEnd = State.PlayTimeSeconds[id];
            WriteState(0, "paused");
            bool resumedSeen = await WaitFor(() => !game.IsPlayPaused);
            double resumeStart = State.PlayTimeSeconds[id];
            await Task.Delay(1600);
            UpdatePlaySessions();
            double resumeEnd = State.PlayTimeSeconds[id];
            double pausedDelta = pauseEnd - pauseStart;
            double resumedDelta = resumeEnd - resumeStart;
            bool passed = beforePause > 0.5 && pausedSeen && resumedSeen && pausedDelta < 0.5 && resumedDelta > 0.8;
            return (passed, $"configured={Preferences.DefaultFrozenProcessesPath}; fixturePid={pid}; activeBeforePause={beforePause:0.00}; pausedDelta={pausedDelta:0.00}; resumedDelta={resumedDelta:0.00}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            try
            {
                if (activePlays.TryGetValue(id, out var active))
                {
                    try { if (!active.Process.HasExited) active.Process.Kill(entireProcessTree: true); } catch { }
                    try { await active.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
                    try { CommitPlaySession(id, active); } catch { }
                }
                else if (owned != null)
                {
                    try { if (!owned.HasExited) owned.Kill(entireProcessTree: true); } catch { }
                    try { await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
                    owned.Dispose();
                }
            }
            catch { }
            Games.RemoveAll(candidate => ReferenceEquals(candidate, game));
            State.PlayTimeSeconds.Remove(id);
            State.Settings.FrozenProcessesPath = originalPath;
            try { if (File.Exists(proofState)) File.Delete(proofState); } catch { }
            try { if (Directory.Exists(proofRoot)) Directory.Delete(proofRoot, recursive: true); } catch { }
        }
    }
}
