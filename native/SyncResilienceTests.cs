using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

// Register with Check("Independent sync sources", () => { SyncResilienceTests.Run(root); });
// Uses a unique directory beneath the supplied test root and fixture HTTP only.
public static class SyncResilienceTests
{
    public static int Run(string root) => Task.Run(() => RunAsync(root)).GetAwaiter().GetResult();

    private static async Task<int> RunAsync(string root)
    {
        string suite = Path.Combine(root, "sync-resilience-" + Guid.NewGuid().ToString("N"));
        int passed = 0;
        async Task Check(string name, Func<LibraryStore, Task> test)
        {
            try { await test(new LibraryStore(Path.Combine(suite, name))); passed++; }
            catch (Exception ex) { throw new InvalidOperationException("Sync resilience [" + name + "]: " + ex.Message, ex); }
        }
        foreach (var failures in new[] { (admin: true, files: false), (admin: false, files: true), (admin: true, files: true), (admin: false, files: false) })
        {
            await Check($"outages-admin-{failures.admin}-static-{failures.files}", async store =>
            {
                var state = new UserState();
                var handler = new FixtureHandler { AdminUnavailable = failures.admin, StaticUnavailable = failures.files, ProxyUnavailable = true };
                SeedCatalog(store);
                string before = store.ReadData("games.json");
                using var client = new SyncClient(store, handler) { SharedReadInterval = TimeSpan.Zero };
                client.Queue(state, "gameCategories", "fixture", JsonValue.Create("local-edit"));
                await client.Refresh(state, true, null);
                Require(handler.DockerRequests == 2, "An independent source failure prevented direct Docker refresh.");
                Require(client.Online == !failures.admin && (client.LastSync != null) == !failures.admin, "Wrong shared sync success/last-sync state.");
                Require(store.LoadState().Pending.Count == 1 && handler.Posts == 0, "Outage lost or published a pending edit.");
                Require(ReadTags(store)["count"]?.GetValue<int>() == 1, "Direct snapshot was not cached.");
                Require(client.DockerStatus.Contains("direct", StringComparison.Ordinal) && client.Status.Contains(client.DockerStatus, StringComparison.Ordinal), "Direct source missing from status.");
                if (failures.admin) Require(client.Status.Contains("Shared sync unavailable", StringComparison.Ordinal), "Queued edits masked shared failure.");
                if (failures.files)
                {
                    Require(store.ReadData("games.json") == before, "Partial static download replaced a cached file.");
                    Require(client.Status.Contains("Static catalog unavailable", StringComparison.Ordinal), "Static failure falsely reported success.");
                }
            });
        }
        await Check("admin503-no-post", async store =>
        {
            var handler = new FixtureHandler { AdminUnavailable = true };
            var state = new UserState();
            using var client = new SyncClient(store, handler) { SharedReadInterval = TimeSpan.Zero };
            client.Queue(state, "gameCategories", "fixture", JsonValue.Create("local-edit"));
            await client.Refresh(state, true, "fixture-token");
            Require(handler.Posts == 0 && store.LoadState().Pending.Count == 1 && !client.Online, "Unavailable shared source attempted publication.");
        });
        await Check("write503-does-not-block-docker", async store =>
        {
            var handler = new FixtureHandler { ProxyUnavailable = true };
            var state = new UserState();
            using var client = new SyncClient(store, handler) { SharedReadInterval = TimeSpan.Zero };
            client.Queue(state, "gameCategories", "fixture", JsonValue.Create("local-edit"));
            await client.Refresh(state, true, "fixture-token");
            Require(handler.Posts == 1 && !client.Online && client.LastSync == null && store.LoadState().Pending.Count == 1,
                "Failed write falsely acknowledged shared synchronization.");
            Require(handler.DockerRequests == 2, "Failed shared write blocked Docker.");
        });
        await Check("previous-shared-success-is-not-advanced-by-failure", async store =>
        {
            var handler = new FixtureHandler();
            using var client = new SyncClient(store, handler) { SharedReadInterval = TimeSpan.Zero };
            var state = new UserState();
            await client.Refresh(state, false, null);
            var last = client.LastSync;
            var config = client.Remote.ToJsonString();
            handler.AdminUnavailable = true;
            await client.Refresh(state, true, null);
            Require(!client.Online && last != null && client.LastSync == last && client.Remote.ToJsonString() == config,
                "Failed shared read changed last success or remote config.");
        });
        foreach (string proxy in new[]
        {
            "{bad-json", "null", "[]",
            "{\"success\":false,\"degraded\":true,\"count\":1,\"tags\":[{\"name\":\"old\"}]}",
            "{\"success\":true,\"repository\":\"other/repo\",\"count\":1,\"tags\":[{\"name\":\"wrong\"}]}",
            "{\"success\":true,\"repository\":\"michadockermisha/backup\",\"count\":2,\"tags\":[{\"name\":\"partial\"}]}",
            "{\"success\":true,\"repository\":\"michadockermisha/backup\",\"count\":2,\"tags\":[{\"name\":\"same\"},{\"name\":\"same\"}]}",
            "{\"success\":true,\"count\":1,\"tags\":[{\"name\":\"unscoped\"}]}"
        })
        {
            await Check("invalid-proxy-" + passed, async store =>
            {
                var handler = new FixtureHandler { ProxyBody = proxy };
                using var client = new SyncClient(store, handler) { SharedReadInterval = TimeSpan.Zero };
                await client.Refresh(new UserState(), true, null);
                Require(handler.DockerRequests == 2 && DataJson.Text(ReadTags(store)["source"]) == "docker-hub-direct", "Invalid proxy snapshot bypassed direct fallback.");
            });
        }
        foreach (string direct in new[]
        {
            "{\"count\":2,\"next\":null,\"results\":[{\"name\":\"partial\"}]}",
            "{\"count\":2,\"next\":null,\"results\":[{\"name\":\"same\"},{\"name\":\"same\"}]}",
            "{\"count\":1,\"next\":\"https://hub.docker.com/v2/repositories/michadockermisha/backup/tags-evil?page=2\",\"results\":[{\"name\":\"partial\"}]}",
            "{\"count\":1,\"next\":7,\"results\":[{\"name\":\"partial\"}]}"
        })
        {
            await Check("incomplete-direct-" + passed, async store =>
            {
                string before = SeedTags(store);
                var handler = new FixtureHandler { ProxyUnavailable = true, DockerBody = direct };
                using var client = new SyncClient(store, handler) { SharedReadInterval = TimeSpan.Zero };
                await client.Refresh(new UserState(), true, null);
                Require(File.ReadAllText(Path.Combine(store.Cache, "docker-tags.json")) == before, "Incomplete direct response replaced a complete cache.");
                Require(client.DockerStatus.Contains("Docker unavailable", StringComparison.Ordinal), "Incomplete tags falsely reported success.");
            });
        }
        await Check("both-docker-sources-fail", async store =>
        {
            string before = SeedTags(store);
            var handler = new FixtureHandler { ProxyUnavailable = true, DockerUnavailable = true };
            using var client = new SyncClient(store, handler) { SharedReadInterval = TimeSpan.Zero };
            await client.Refresh(new UserState(), true, null);
            Require(client.Online && client.LastSync != null, "Docker outage incorrectly invalidated a successful shared sync.");
            Require(File.ReadAllText(Path.Combine(store.Cache, "docker-tags.json")) == before && client.Status.Contains("Docker unavailable", StringComparison.Ordinal), "Docker failure lost cache or status.");
        });
        foreach (bool direct in new[] { false, true })
        {
            await Check("empty-repository-direct-" + direct, async store =>
            {
                SeedTags(store);
                var handler = new FixtureHandler
                {
                    ProxyUnavailable = direct,
                    ProxyBody = "{\"success\":true,\"repository\":\"michadockermisha/backup\",\"count\":0,\"tags\":[]}",
                    DockerBody = "{\"count\":0,\"next\":null,\"results\":[]}"
                };
                using var client = new SyncClient(store, handler) { SharedReadInterval = TimeSpan.Zero };
                await client.Refresh(new UserState(), true, null);
                Require(ReadTags(store)["tags"] is JsonArray { Count: 0 } && handler.DockerRequests == (direct ? 2 : 0), "Valid empty repository was rejected.");
            });
        }
        await Check("authentication-retry-is-injected-and-scoped", async store =>
        {
            var handler = new FixtureHandler { ProxyUnavailable = true, RequireDockerAuthentication = true };
            int logins = 0;
            using var client = new SyncClient(store, handler, dockerTokenProvider: _ => { logins++; return Task.FromResult("fixture-only-token"); });
            await client.Refresh(new UserState(), true, null);
            Require(logins == 1 && handler.DockerRequests == 3 && handler.AuthenticatedDockerRequests == 2, "Pagination authentication was not retried and reused for final validation.");
            Require(handler.NonDockerAuthorization == 0 && ReadTags(store)["count"]?.GetValue<int>() == 1, "Docker authorization escaped its host or snapshot was lost.");
        });
        foreach (string stage in new[] { "shared", "static", "proxy", "docker" })
        {
            await Check("cancel-" + stage, async store =>
            {
                string before = SeedTags(store);
                using var cancel = new CancellationTokenSource();
                var handler = new FixtureHandler { ProxyUnavailable = true, CancelStage = stage, Cancellation = cancel };
                var state = new UserState();
                using var client = new SyncClient(store, handler) { SharedReadInterval = TimeSpan.Zero };
                client.Queue(state, "gameCategories", "fixture", JsonValue.Create("retained"));
                bool cancelled = false;
                try { await client.Refresh(state, true, null, cancel.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                Require(cancelled && client.Status.Contains("cancelled", StringComparison.Ordinal), "Caller cancellation was swallowed or reported as success.");
                Require(store.LoadState().Pending.Count == 1 && File.ReadAllText(Path.Combine(store.Cache, "docker-tags.json")) == before, "Cancellation lost queued edits or replaced tags.");
                Require(handler.DockerRequests == (stage == "docker" ? 1 : 0), "Cancellation started an unnecessary fallback.");
                handler.CancelStage = null;
                await client.Refresh(state, false, null);
                Require(client.Online, "Cancellation did not release the synchronization gate.");
            });
        }
        await Check("offline-transport-never-reaches-fallback", async store =>
        {
            var guard = new OfflineNetworkGuard();
            var docker = new FixtureHandler();
            using var client = new SyncClient(store, guard, docker);
            await client.Refresh(new UserState(), true, null);
            Require(guard.Attempts == 1 && docker.DockerRequests == 0 && !client.Online, "Offline transport attempted a direct fallback.");
        });
        await Check("pre-cancelled-refresh-has-no-side-effects", async store =>
        {
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            var handler = new FixtureHandler();
            using var client = new SyncClient(store, handler) { SharedReadInterval = TimeSpan.Zero };
            bool cancelled = false;
            try { await client.Refresh(new UserState(), true, null, cancel.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled && handler.TotalRequests == 0 && !File.Exists(store.StatePath), "Pre-cancelled refresh performed I/O.");
        });
        await Check("proxy-timeout-allows-direct-fallback", async store =>
        {
            var handler = new FixtureHandler { ProxyTimeout = true };
            using var client = new SyncClient(store, handler) { SharedReadInterval = TimeSpan.Zero };
            await client.Refresh(new UserState(), true, null);
            Require(handler.DockerRequests == 2 && client.DockerStatus.Contains("timed out", StringComparison.Ordinal), "Proxy timeout did not fall back with accurate status.");
        });
        await Check("full-direct-pagination-and-recent-cache-reuse", async store =>
        {
            var handler = new FixtureHandler { ProxyUnavailable = true, MultiplePages = true };
            using var client = new SyncClient(store, handler) { SharedReadInterval = TimeSpan.Zero };
            var state = new UserState();
            await client.Refresh(state, true, null);
            Require(handler.DockerRequests == 3 && ReadTags(store)["tags"] is JsonArray { Count: 2 }, "All tag pages and final first-page verification were not fetched.");
            await client.Refresh(state, true, null);
            Require(handler.DockerRequests == 4 && ReadTags(store)["checkedAt"] != null && ReadTags(store)["tags"] is JsonArray { Count: 2 }, "Recent complete snapshot was not safely reused.");
        });
        await Check("final-direct-page-changed", async store =>
        {
            string before = SeedTags(store);
            var handler = new FixtureHandler { ProxyUnavailable = true, ChangeFinalPage = true };
            using var client = new SyncClient(store, handler) { SharedReadInterval = TimeSpan.Zero };
            await client.Refresh(new UserState(), true, null);
            Require(handler.DockerRequests == 2 && File.ReadAllText(Path.Combine(store.Cache, "docker-tags.json")) == before, "A changing registry replaced a complete snapshot.");
        });
        await Check("unsafe-cache-is-never-reused", store =>
        {
            var cached = JsonNode.Parse("{\"success\":true,\"source\":\"docker-hub-direct\",\"repository\":\"user/repo\",\"count\":1,\"tags\":[{\"name\":\"fixture\",\"full_size\":123,\"last_updated\":null}]}")!.AsObject();
            cached["fetchedAt"] = DateTime.UtcNow.ToString("O");
            var first = JsonNode.Parse("{\"count\":1,\"next\":null,\"results\":[{\"name\":\"fixture\",\"full_size\":123}]}")!.AsObject();
            Require(SyncClient.CanReuseDockerSnapshot(cached, first, "user/repo", DateTime.UtcNow), "Fixture is not a reusable snapshot.");
            foreach (var invalid in new[] { ("success", (JsonNode)JsonValue.Create(false)!), ("degraded", (JsonNode)JsonValue.Create("false")!), ("next", (JsonNode)JsonValue.Create("https://hub.docker.com/more")!), ("fetched", (JsonNode)JsonValue.Create(0)!) })
            {
                var changed = (JsonObject)cached.DeepClone();
                changed[invalid.Item1] = invalid.Item2.DeepClone();
                Require(!SyncClient.CanReuseDockerSnapshot(changed, first, "user/repo", DateTime.UtcNow), "Unsafe cached envelope was reused: " + invalid.Item1);
                Require(!SyncClient.CompleteTagSnapshot(changed, "user/repo"), "Unsafe proxy envelope was accepted: " + invalid.Item1);
            }
            return Task.CompletedTask;
        });
        await Check("pending-game-backups-import-guard", async store =>
        {
            var state = new UserState();
            state.PendingGameBackups["fixture"] = @"F:\games\fixture\game.exe";
            store.Save(state);
            string before = File.ReadAllText(store.StatePath);
            using var client = new SyncClient(store, new FixtureHandler());
            foreach (string? replacement in new string?[] { null, @"F:\games\other\game.exe" })
            {
                var imported = new UserState();
                if (replacement != null) imported.PendingGameBackups["fixture"] = replacement;
                bool applied = false, rejected = false;
                try { await client.ImportStateAsync(() => state, _ => imported, _ => applied = true); }
                catch (InvalidOperationException) { rejected = true; }
                Require(rejected && !applied && File.ReadAllText(store.StatePath) == before, "Import discarded or replaced a live pending game backup.");
            }
            var matching = new UserState();
            matching.PendingGameBackups["fixture"] = state.PendingGameBackups["fixture"];
            bool accepted = false;
            await client.ImportStateAsync(() => state, _ => matching, _ => accepted = true);
            Require(accepted && store.LoadState().PendingGameBackups["fixture"] == state.PendingGameBackups["fixture"], "Import rejected or lost an unchanged backup queue.");
        });
        return passed;
    }

    private static JsonObject ReadTags(LibraryStore store) => JsonNode.Parse(File.ReadAllText(Path.Combine(store.Cache, "docker-tags.json")))!.AsObject();
    private static string SeedTags(LibraryStore store)
    {
        const string json = "{\"success\":true,\"source\":\"docker-hub-direct\",\"repository\":\"michadockermisha/backup\",\"fetchedAt\":\"2020-01-01T00:00:00Z\",\"count\":1,\"tags\":[{\"name\":\"known-complete\",\"full_size\":42}]}";
        store.CacheData("docker-tags.json", json);
        return json;
    }
    private static void SeedCatalog(LibraryStore store)
    {
        store.CacheData("games.json", "[{\"id\":\"previous\"}]");
        store.CacheData("tabs.json", "[]");
        foreach (string file in new[] { "times.json", "image-sizes.json", "dates-added.json" }) store.CacheData(file, "{}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        public bool AdminUnavailable, StaticUnavailable, ProxyUnavailable, DockerUnavailable, RequireDockerAuthentication, ProxyTimeout, MultiplePages, ChangeFinalPage;
        public int DockerRequests, Posts, AuthenticatedDockerRequests, NonDockerAuthorization, TotalRequests;
        public string? ProxyBody, DockerBody, CancelStage;
        public CancellationTokenSource? Cancellation;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TotalRequests++;
            var uri = request.RequestUri!;
            void Cancel(string stage)
            {
                if (CancelStage == stage) { Cancellation!.Cancel(); cancellationToken.ThrowIfCancellationRequested(); }
            }
            if (request.Method != HttpMethod.Get)
            {
                Posts++;
                return Task.FromResult(Json("{}", HttpStatusCode.ServiceUnavailable));
            }
            if (uri.Host == "hub.docker.com")
            {
                DockerRequests++;
                Cancel("docker");
                if (request.Headers.Authorization != null) AuthenticatedDockerRequests++;
                if (RequireDockerAuthentication && request.Headers.Authorization == null)
                    return Task.FromResult(Json("{\"message\":\"pagination offset too large for anonymous requests\"}", HttpStatusCode.Forbidden));
                if (MultiplePages)
                    return Task.FromResult(Json(uri.Query.Contains("page=2", StringComparison.Ordinal)
                        ? "{\"count\":2,\"next\":null,\"results\":[{\"name\":\"second\",\"full_size\":456}]}"
                        : "{\"count\":2,\"next\":\"https://hub.docker.com/v2/repositories/michadockermisha/backup/tags?page=2&page_size=100\",\"results\":[{\"name\":\"fixture\",\"full_size\":123}]}"));
                if (ChangeFinalPage && DockerRequests == 2)
                    return Task.FromResult(Json("{\"count\":1,\"next\":null,\"results\":[{\"name\":\"changed\",\"full_size\":123}]}"));
                return Task.FromResult(DockerUnavailable ? Json("{}", HttpStatusCode.ServiceUnavailable) :
                    Json(DockerBody ?? "{\"count\":1,\"next\":null,\"results\":[{\"name\":\"fixture\",\"full_size\":123,\"last_updated\":\"2026-09-22T00:00:00Z\"}]}"));
            }
            if (request.Headers.Authorization != null) NonDockerAuthorization++;
            if (uri.Host != new Uri(SyncClient.Production).Host) throw new InvalidOperationException("Unexpected fixture host.");
            if (uri.AbsolutePath == "/api/admin-config")
            {
                Cancel("shared");
                return Task.FromResult(AdminUnavailable ? Json("{}", HttpStatusCode.ServiceUnavailable) :
                    Json("{\"success\":true,\"config\":{\"gameCategories\":{},\"hiddenTabs\":[],\"tabs\":[]},\"configVersion\":\"fixture-v1\"}"));
            }
            if (uri.AbsolutePath == "/api/docker-tags")
            {
                Cancel("proxy");
                if (ProxyTimeout) return Task.FromException<HttpResponseMessage>(new TaskCanceledException("Fixture timeout"));
                return Task.FromResult(ProxyUnavailable ? Json("{}", HttpStatusCode.ServiceUnavailable) :
                    Json(ProxyBody ?? "{\"success\":true,\"repository\":\"michadockermisha/backup\",\"count\":1,\"tags\":[{\"name\":\"fixture\",\"full_size\":123}]}"));
            }
            if (uri.AbsolutePath.StartsWith("/data/", StringComparison.Ordinal))
            {
                Cancel("static");
                if (StaticUnavailable && uri.AbsolutePath == "/data/times.json") return Task.FromResult(Json("{}", HttpStatusCode.ServiceUnavailable));
            }
            if (uri.AbsolutePath == "/data/games.json") return Task.FromResult(Json("[{\"id\":\"fixture\",\"name\":\"Fixture\"}]"));
            if (uri.AbsolutePath == "/data/tabs.json") return Task.FromResult(Json("[]"));
            if (uri.AbsolutePath.StartsWith("/data/", StringComparison.Ordinal)) return Task.FromResult(Json("{}"));
            throw new InvalidOperationException("Unexpected fixture route.");
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
