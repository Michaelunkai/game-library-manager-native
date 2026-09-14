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
        Check("Default profile resolves beside the distribution on the bundle drive", () =>
        {
            var expected = Path.Combine(root, "data");
            var resolved = LibraryStore.ResolveDefaultRoot(Path.Combine(root, "dist"));
            Require(string.Equals(resolved, expected, StringComparison.OrdinalIgnoreCase), "The default profile did not resolve beside dist.");
            Require(!resolved.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameLibraryManager"), StringComparison.OrdinalIgnoreCase), "The default profile still targets LocalApplicationData.");
        });
        Check("Startup monitor follows the launch pointer unless explicitly overridden", () =>
        {
            var placementType = typeof(MainWindow).Assembly.GetType("GameLibrary.Native.WindowPlacement")
                ?? throw new InvalidOperationException("WindowPlacement is missing.");
            var modeType = typeof(MainWindow).Assembly.GetType("GameLibrary.Native.StartupMonitorMode")
                ?? throw new InvalidOperationException("StartupMonitorMode is missing.");
            var select = placementType.GetMethod("SelectIndex", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("WindowPlacement.SelectIndex is missing.");
            var screens = new[]
            {
                new System.Drawing.Rectangle(0, 0, 1920, 1040),
                new System.Drawing.Rectangle(1920, -240, 2560, 1400)
            };
            object auto = Enum.Parse(modeType, "Auto");
            object primary = Enum.Parse(modeType, "Primary");
            object secondary = Enum.Parse(modeType, "Secondary");
            int Invoke(System.Drawing.Point pointer, System.Drawing.Point? foreground, object mode) =>
                (int)(select.Invoke(null, new object?[] { screens, pointer, foreground, 0, mode }) ?? -1);
            Require(Invoke(new System.Drawing.Point(2500, 400), new System.Drawing.Point(200, 200), auto) == 1,
                "Auto startup ignored the monitor containing the launch pointer.");
            Require(Invoke(new System.Drawing.Point(9000, 9000), new System.Drawing.Point(2500, 400), auto) == 1,
                "Auto startup did not fall back to the foreground window monitor.");
            Require(Invoke(new System.Drawing.Point(9000, 9000), null, auto) == 0,
                "Auto startup did not fall back to the primary monitor.");
            Require(Invoke(new System.Drawing.Point(2500, 400), null, primary) == 0 && Invoke(new System.Drawing.Point(200, 200), null, secondary) == 1,
                "Explicit primary/secondary monitor overrides changed semantics.");
            var constructor = typeof(MainWindow).GetConstructors().Single(candidate => candidate.GetParameters().Length == 3);
            var monitorParameter = constructor.GetParameters()[2];
            Require(monitorParameter.ParameterType == modeType && string.Equals(monitorParameter.DefaultValue?.ToString(), "Auto", StringComparison.Ordinal),
                "The native executable still defaults to a fixed secondary monitor.");
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
            string node = Path.Combine(AppContext.BaseDirectory, "tools", "node", "node.exe");
            Require(File.Exists(launcher) && File.Exists(node), "The bundled Wand CDP recovery runtime is missing.");
            string source = File.ReadAllText(launcher);
            Require(source.Contains("launchNonce", StringComparison.Ordinal) && source.Contains("127.0.0.1:9222", StringComparison.Ordinal), "The Wand CDP recovery route lost its unique loopback-only launch behavior.");
        });
        Check("Exact-case Docker tags remain distinct", () =>
        {
            var games = new List<Game>(); LibraryStore.MergeTags(games, JsonNode.Parse("{\"tags\":[{\"name\":\"AeternaNoctis\"},{\"name\":\"aeternanoctis\"}]}")!, state.Settings);
            Require(games.Count == 2, "Tags were collapsed.");
        });
        Check("Punctuation aliases attach Docker metadata to the canonical game", () =>
        {
            var games = new List<Game> { new() { Id = "007firstlight", Name = "007 First Light" } };
            LibraryStore.MergeTags(games, JsonNode.Parse("{\"tags\":[{\"name\":\"007-first-light\",\"full_size\":123456789}]}")!, state.Settings);
            Require(games.Count == 1 && games[0].Id == "007firstlight" && games[0].SizeGb > 0, "A punctuation-variant Docker tag created a duplicate identity.");
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
        Check("Installed size is measured and persisted separately from Docker download size", () =>
        {
            string folder = Path.Combine(root, "installed-size-proof");
            Directory.CreateDirectory(Path.Combine(folder, "nested"));
            File.WriteAllBytes(Path.Combine(folder, "game.exe"), new byte[1536]);
            File.WriteAllBytes(Path.Combine(folder, "nested", "payload.bin"), new byte[2560]);
            var measureType = typeof(MainWindow).Assembly.GetType("GameLibrary.Native.InstalledSize")
                ?? throw new InvalidOperationException("InstalledSize is missing.");
            var measure = measureType.GetMethod("Measure", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("InstalledSize.Measure is missing.");
            long bytes = (long)(measure.Invoke(null, new object?[] { folder, CancellationToken.None }) ?? -1L);
            Require(bytes == 4096, "Installed byte measurement was not exact.");

            var installedBytesProperty = typeof(UserState).GetProperty("InstalledBytes")
                ?? throw new InvalidOperationException("UserState.InstalledBytes is missing.");
            var measuredUtcProperty = typeof(UserState).GetProperty("InstalledSizeMeasuredUtc")
                ?? throw new InvalidOperationException("UserState.InstalledSizeMeasuredUtc is missing.");
            installedBytesProperty.SetValue(state, new Dictionary<string, long>(StringComparer.Ordinal) { ["size-proof"] = bytes });
            measuredUtcProperty.SetValue(state, new Dictionary<string, DateTime>(StringComparer.Ordinal) { ["size-proof"] = DateTime.UtcNow });
            store.Save(state);
            var restored = store.LoadState();
            var restoredBytes = (Dictionary<string, long>)installedBytesProperty.GetValue(restored)!;
            Require(restoredBytes["size-proof"] == bytes, "Measured installed bytes did not survive restart.");

            var game = new Game { Id = "size-proof", Name = "Size Proof", SizeGb = 1.5, Installed = true };
            var installedBytesGameProperty = typeof(Game).GetProperty("InstalledBytes")
                ?? throw new InvalidOperationException("Game.InstalledBytes is missing.");
            installedBytesGameProperty.SetValue(game, 2_000_000_000L);
            string label = typeof(Game).GetProperty("SizeLabel")?.GetValue(game)?.ToString() ?? "";
            Require(label.Contains("installed", StringComparison.OrdinalIgnoreCase) && !label.Contains("download", StringComparison.OrdinalIgnoreCase),
                "An observed local size was still presented as a Docker download estimate.");
            installedBytesGameProperty.SetValue(game, 0L);
            label = typeof(Game).GetProperty("SizeLabel")?.GetValue(game)?.ToString() ?? "";
            Require(label.Contains("download", StringComparison.OrdinalIgnoreCase), "The uninstalled Docker size was not identified as download size.");
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
            if (!offline) Check("Actual packaged window loads live catalog and production config", Sync.Online && Sync.LastSync != null);
            var playStateGame = Games.FirstOrDefault(game => game.CanPlayWithWand) ?? Games.First();
            GameList.ScrollIntoView(playStateGame);
            await Dispatcher.InvokeAsync(() => GameList.UpdateLayout(), DispatcherPriority.Render);
            var playStateContainer = GameList.ItemContainerGenerator.ContainerFromItem(playStateGame);
            var playButton = FindVisual<Button>(playStateContainer, candidate => System.Windows.Automation.AutomationProperties.GetAutomationId(candidate) == "PlayGame");
            var wandButton = FindVisual<Button>(playStateContainer, candidate => System.Windows.Automation.AutomationProperties.GetAutomationId(candidate) == "PlayWithWand");
            var exitButton = FindVisual<Button>(playStateContainer, candidate => System.Windows.Automation.AutomationProperties.GetAutomationId(candidate) == "ForceExitGameAndWand");
            Check("Idle cards reflect exact Wand eligibility while hiding force-exit controls", playButton?.Content?.ToString()?.Contains("Play", StringComparison.Ordinal) == true
                && wandButton?.Visibility == (playStateGame.CanPlayWithWand ? Visibility.Visible : Visibility.Collapsed)
                && exitButton?.Visibility == Visibility.Collapsed);
            string processFixtureFolder = Path.Combine(Store.Root, "process-fixture");
            Directory.CreateDirectory(processFixtureFolder);
            string processFixture = Path.Combine(processFixtureFolder, "TrackedGame.exe");
            File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe"), processFixture, true);
            double playtimeBeforeFixture = State.PlayTimeSeconds.GetValueOrDefault(playStateGame.Id);
            using var trackedFixture = Process.Start(new ProcessStartInfo(processFixture, "127.0.0.1 -n 30") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })
                ?? throw new InvalidOperationException("Could not start the tracked UI process fixture.");
            try
            {
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
            }
            using (var exitedFixture = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe"), "127.0.0.1 -n 1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })
                ?? throw new InvalidOperationException("Could not start the exited-process UI fixture."))
            {
                exitedFixture.WaitForExit(5000);
                Check("A process that exits during Wand confirmation is ignored without stale tracking", !TrackPlayProcess(playStateGame, exitedFixture, usesWand: true, ownsProcess: false) && !activePlays.ContainsKey(playStateGame.Id));
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
                SearchBox.Text = "Filter"; ApplyFilter(); Check("Global search never exposes protected categories", filtered.Count == 3 && filtered.All(g => g.Id != "filter-hidden"));
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
                else adminToken = "offline-fixture"; // Edit-control fixture only; no auth claim without a password.
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
