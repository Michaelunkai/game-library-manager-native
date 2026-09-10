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
using System.Windows;
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
        Check("Default profile resolves beside the distribution on the bundle drive", () =>
        {
            var expected = Path.Combine(root, "data");
            var resolved = LibraryStore.ResolveDefaultRoot(Path.Combine(root, "dist"));
            Require(string.Equals(resolved, expected, StringComparison.OrdinalIgnoreCase), "The default profile did not resolve beside dist.");
            Require(!resolved.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameLibraryManager"), StringComparison.OrdinalIgnoreCase), "The default profile still targets LocalApplicationData.");
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
        Check("Exact-case Docker tags remain distinct", () =>
        {
            var games = new List<Game>(); LibraryStore.MergeTags(games, JsonNode.Parse("{\"tags\":[{\"name\":\"AeternaNoctis\"},{\"name\":\"aeternanoctis\"}]}")!, state.Settings);
            Require(games.Count == 2, "Tags were collapsed.");
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
        Check("Wand connection evidence requires IPC and a hook marker", () =>
        {
            Require(WandIntegration.ContainsConnectionEvidence("[42:7][info] ipc connected\n[42:7][warning] dxgi_hooked: true", 42), "A complete Wand overlay connection log was rejected.");
            Require(!WandIntegration.ContainsConnectionEvidence("[41:7][info] ipc connected\n[warning] dxgi_hooked: true", 42), "Connection evidence from a different game PID was accepted.");
            Require(!WandIntegration.ContainsConnectionEvidence("[41:7][info] ipc connected\n[42:7][warning] dxgi_hooked: true", 42), "Stale IPC from another game PID was combined with a current hook marker.");
            Require(!WandIntegration.ContainsConnectionEvidence("[42:7][info] ipc connected"), "IPC alone was accepted as a Wand connection.");
            Require(!WandIntegration.ContainsConnectionEvidence("[42:7][warning] dxgi_hooked: true"), "A hook marker without IPC was accepted as a Wand connection.");
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
            Require(loaded.PlayTimeSeconds[id] == 7380 && game != null && game.Installed && Math.Abs(game.PlayedHours - 2.05) < 0.001, "Play time was not persisted or displayed.");
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
            var modes = new[] { "Name A–Z", "Name Z–A", "Time to Beat (Low–High)", "Time to Beat (High–Low)", "Recently Added", "Oldest First", "Rating (High–Low)", "Rating (Low–High)", "Size (Small–Large)", "Size (Large–Small)", "Category" };
            Require(modes.All(mode => MainWindow.SortGames(games, mode).Count() == games.Length), "A website sort category is missing from native.");
            foreach (var mode in new[] { "Time to Beat (Low–High)", "Time to Beat (High–Low)", "Rating (High–Low)", "Rating (Low–High)", "Size (Small–Large)", "Size (Large–Small)" })
                Require(MainWindow.SortGames(games, mode).Last().Id == "beta", "Unknown values did not stay last for " + mode + ".");
        });
        Check("PowerShell, BAT and shell scripts preserve exact tags", () =>
        {
            var game = new Game { Id = "AeternaNoctis", Name = "A title with 'quotes' & %PATH%" };
            foreach (var format in new[] { "ps1", "sh", "bat" })
            {
                var script = DockerScripts.Generate(new[] { game }, state.Settings, format);
                if (format == "bat") script = script.Split("\r\n# GLM_POWERSHELL_START\r\n")[1];
                Require(script.Contains("backup:AeternaNoctis") && !script.Contains("wsl --") && !script.Contains("docker system prune"), "Unsafe or incorrect script.");
                if (format is "bat" or "sh") Require(script.Contains("cp -rL") && script.Contains(DockerScripts.CompletionMarkerName) && script.Contains("GameLibraryManager|") && !script.Contains("cp -av"), "Install scripts must copy without Unix permission preservation and require an id-bound completion marker.");
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
            string markerLine = script.Split('\n').Single(line => line.StartsWith("completion_marker=", StringComparison.Ordinal)).TrimEnd('\r');
            string expected = "completion_marker=\"$destination/" + DockerScripts.InstallFolder(game.Id) + "/" + DockerScripts.CompletionMarkerName + "\"";
            Require(markerLine == expected, "The POSIX completion marker must expand $destination inside double quotes.");
            Require(!markerLine.Contains("'$destination/", StringComparison.Ordinal), "The POSIX completion marker must not quote the destination variable literally.");
        });
        Check("Multi-game install scripts isolate each completion marker", () =>
        {
            var games = new[] { new Game { Id = "batch-alpha", Name = "Batch Alpha" }, new Game { Id = "batch-beta", Name = "Batch Beta" } };
            foreach (var format in new[] { "ps1", "sh", "bat" })
            {
                string script = DockerScripts.Generate(games, state.Settings, format, shellTarget: format == "sh" ? "wsl2" : null);
                Require(script.Contains("GameLibraryManager|batch-alpha") && script.Contains("GameLibraryManager|batch-beta") && script.Contains(DockerScripts.InstallFolder("batch-alpha")) && script.Contains(DockerScripts.InstallFolder("batch-beta")), "A multi-game " + format + " script lost a game-specific completion marker or folder.");
            }
        });
        Check("Windows installs use the default terminal BAT contract", () =>
        {
            string bat = DockerScripts.Generate(new[] { new Game { Id = "terminalproof", Name = "Terminal proof" } }, state.Settings, "bat");
            var start = JobWindow.BuildDefaultTerminalStartInfo(Path.Combine(root, "install-games.bat"));
            Require(bat.StartsWith("@echo off\r\n", StringComparison.Ordinal) && bat.Contains("# GLM_POWERSHELL_START") && bat.Contains(DockerScripts.CompletionMarkerName) && !bat.Contains("\r\npause\r\n") && start.UseShellExecute && start.FileName.EndsWith("install-games.bat", StringComparison.OrdinalIgnoreCase), "Install did not preserve the visible BAT/default-terminal route, completion proof, or completion exit.");
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
        Check("Metadata title matching rejects soundtracks and preserves exact game names", () =>
        {
            Require(MetadataClient.SameTitle("Viewfinder", "VIEWFINDER") && !MetadataClient.SameTitle("INMOST", "INMOST Soundtrack") && !MetadataClient.SameTitle("", ""), "Incorrect metadata title accepted.");
            var alias = JsonNode.Parse("{\"id\":\"ofashnsteel\",\"name\":\"Of Ash and Steel\",\"source\":{\"image\":\"steam-known\",\"time\":\"known-override\"}}")!.AsObject();
            Require(MetadataClient.MatchesGame(new Game { Id = "ofashnsteel", Name = "ofashnsteel" }, alias), "Curated backend alias was rejected.");
            alias["source"]!["image"] = "steam";
            Require(!MetadataClient.MatchesGame(new Game { Id = "ofashnsteel", Name = "ofashnsteel" }, alias), "A generic different-title search result was accepted.");
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
        try
        {
            Check("Packaged WPF window visible with taskbar identity", IsVisible && ShowInTaskbar && Icon != null && Games.Count >= 1179);
            Check("Tray icon and useful menu created", tray is { Visible: true } && tray.ContextMenuStrip?.Items.Count == 5);
            if (!offline) Check("Actual packaged window loads live catalog and production config", Sync.Online && Sync.LastSync != null);
            SearchBox.Text = "STAR OCEAN"; ApplyFilter();
            Check("Search filters native list", filtered.Count > 0 && filtered.All(g => g.Name.Contains("STAR OCEAN", StringComparison.OrdinalIgnoreCase) || g.Id.Contains("STAR OCEAN", StringComparison.OrdinalIgnoreCase)));
            SelectAll(this, new()); Check("Select all operates on filtered items", filtered.All(g => g.Selected));
            DeselectAll(this, new()); Check("Clear selection works", Games.All(g => !g.Selected));
            SearchBox.Text = "no-such-game-" + Guid.NewGuid(); ApplyFilter(); Check("Empty view is recoverable", EmptyState.Visibility == Visibility.Visible);
            ResetFilters(this, new()); Check("Reset restores results", filtered.Count > 0);
            var originalState = DataJson.Read<UserState>(DataJson.Write(State));
            try
            {
                Games = new()
                {
                    new() { Id = "filter-alpha", Name = "Filter Alpha", Category = "new", CategoryName = "New", Rating = 5, Installed = true, Time = 10, DockerImageUrl = "https://hub.docker.com/r/proof/repo/tags?name=filter-alpha" },
                    new() { Id = "filter-beta", Name = "Filter Beta", Category = "rpg", CategoryName = "RPG", Rating = 1, Time = 20 },
                    new() { Id = "filter-zero", Name = "Filter Zero", Category = "new", CategoryName = "New", Rating = 0 },
                    new() { Id = "filter-hidden", Name = "Filter Hidden", Category = "not_for_me", CategoryName = "Private", Rating = 3 }
                };
                catalogStatsDirty = true; tab = "rpg"; SearchBox.Text = "Filter Alpha"; ApplyFilter();
                Check("Search crosses categories without changing catalog privacy", filtered.Count == 1 && filtered[0].Id == "filter-alpha");
                SearchBox.Text = "Filter"; ApplyFilter(); Check("Global search never exposes protected categories", filtered.Count == 3 && filtered.All(g => g.Id != "filter-hidden"));
                string average = AverageTime.Text, covers = CoverCount.Text, percent = InstalledPercent.Text;
                SearchBox.Text = "hub.docker.com/r/proof"; ApplyFilter(); Check("Docker URL is searchable", filtered.Count == 1 && filtered[0].Id == "filter-alpha");
                Check("Catalog statistics stay constant while filtering", AverageTime.Text == average && CoverCount.Text == covers && InstalledPercent.Text == percent);
                State.LaunchPaths["filter-beta"] = @"F:\NativeProof\UniqueLauncher.exe"; SearchBox.Text = "UniqueLauncher"; ApplyFilter(); Check("Installed launcher path is searchable", filtered.Count == 1 && filtered[0].Id == "filter-beta");
                SearchBox.Text = "Filter"; tab = "installed"; ApplyFilter(); Check("Installed view remains limited during global search", filtered.Count == 1 && filtered[0].Installed);
                tab = "all"; InstalledOnlyFilter.IsChecked = true; ApplyFilter(); Check("Installed-only control composes with global search", filtered.Count == 1 && filtered[0].Installed);
                WithoutInstalledFilter.IsChecked = true; ApplyFilter(); Check("Without-installed control excludes installed games", filtered.Count == 2 && filtered.All(g => !g.Installed));
                ResetFilters(this, new()); Check("Reset clears installed filters", InstalledOnlyFilter.IsChecked == false && WithoutInstalledFilter.IsChecked == false);
                SortBox.SelectedItem = "Rating (Low–High)"; ApplyFilter(); Check("Lowest rating sort uses ascending scores with unrated games last", filtered.Select(g => g.Rating).SequenceEqual(new[] { 1, 5, 0 }));
            }
            finally { SearchBox.Text = ""; InstalledOnlyFilter.IsChecked = false; WithoutInstalledFilter.IsChecked = false; RestoreImportedState(originalState); }
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
                else adminToken = "offline-fixture"; // Edit-control fixture only; no auth claim without a password.
                async Task<EditorWindow> CategoriesDialog()
                {
                    _ = Dispatcher.BeginInvoke(new Action(() => ManageCategories(this, new())));
                    await Task.Delay(120);
                    return System.Windows.Application.Current.Windows.OfType<EditorWindow>().Single(w => w.Title == "Manage categories");
                }
                void Click(EditorWindow dialog, string name) => dialog.Fields.Children.OfType<System.Windows.Controls.Button>().Single(b => (string)b.Content == name).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                var categoryDialog = await CategoriesDialog();
                categoryDialog.Fields.Children.OfType<System.Windows.Controls.TextBox>().Single().Text = "Native proof category";
                Click(categoryDialog, "Create category");
                Check("Offline native category creation queues shared tabs", Store.LoadCategories(Sync.Effective(State)).Any(c => c.Id == "native_proof_category"));
                categoryDialog = await CategoriesDialog();
                var choices = categoryDialog.Fields.Children.OfType<System.Windows.Controls.ComboBox>().Single();
                choices.SelectedItem = choices.Items.Cast<Category>().Single(c => c.Id == "native_proof_category");
                categoryDialog.Fields.Children.OfType<System.Windows.Controls.TextBox>().Single().Text = "Renamed proof category";
                categoryDialog.Fields.Children.OfType<System.Windows.Controls.CheckBox>().Single().IsChecked = true;
                Click(categoryDialog, "Save name and visibility");
                Check("Offline native rename and visibility controls preserve queued edits", Store.LoadCategories(Sync.Effective(State)).Any(c => c.Id == "native_proof_category" && c.Name == "Renamed proof category") && Hidden(Sync.Effective(State)).Contains("native_proof_category"));
                Check("Category edits survive a state reload", Store.LoadState().Pending.Count >= 2);
                await Refresh(false); await Refresh(true);
                Check("Offline admin actions and refresh never reach the rejecting network fixture", offlineNetwork is { Attempts: 0 } && !Sync.Online && Sync.LastSync == null && Store.LoadState().Pending.Count >= 2);
                adminToken = null;
            }
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
