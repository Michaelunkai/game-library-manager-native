using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

// Standalone fixture suite; deliberately does not register itself in SelfTests.
public static class SyncBackoffTests
{
    public static int Run(string root) => Task.Run(() => RunAsync(root)).GetAwaiter().GetResult();

    private static async Task<int> RunAsync(string root)
    {
        string suite = Path.Combine(root, "sync-backoff-" + Guid.NewGuid().ToString("N"));
        int passed = 0;
        async Task Check(string name, Func<LibraryStore, Task> test)
        {
            try { await test(new LibraryStore(Path.Combine(suite, name))); passed++; }
            catch (Exception ex) { throw new InvalidOperationException("Sync backoff [" + name + "]: " + ex.Message, ex); }
        }
        await Check("usage-exceeded-not-polled", async store =>
        {
            var handler = new Fixture { Fail = "all" };
            using var client = new SyncClient(store, handler) { SharedReadInterval = TimeSpan.Zero };
            var state = new UserState();
            await client.Refresh(state, true, null);
            await client.Refresh(state, true, null);
            Require(handler.Shared == 1 && handler.Catalog == 5 && handler.Proxy == 1, "HTTP 503 website sources were immediately polled again.");
            Require(handler.Direct >= 3, "Website backoff blocked direct Docker refresh.");
        });
        foreach (string source in new[] { "shared", "catalog", "proxy" })
        {
            await Check(source + "-bounded-exponential-clock", async store =>
            {
                var clock = new Clock();
                var handler = new Fixture { Fail = source };
                using var client = new SyncClient(store, handler, utcNow: () => clock.Now) { SharedReadInterval = TimeSpan.Zero };
                var state = new UserState();
                await client.Refresh(state, true, null);
                int attempts = 1;
                foreach (int seconds in new[] { 10, 20, 40, 80, 160, 300, 300 })
                {
                    clock.Advance(seconds - 1);
                    await client.Refresh(state, true, null);
                    Require(handler.Attempts(source) == attempts, "Source retried before its deadline.");
                    Require(client.Status.Contains("HTTP 503; retry in 1s", StringComparison.Ordinal), "Skipped source lost failure or retry status.");
                    clock.Advance(1);
                    await client.Refresh(state, true, null);
                    Require(handler.Attempts(source) == ++attempts, "Source did not retry at its bounded deadline.");
                }
                Require(source == "shared" ? !client.Online && client.LastSync == null : client.Online && client.LastSync != null,
                    "Source outage corrupted shared connectivity.");
            });
            await Check(source + "-recovery-resets", async store =>
            {
                var clock = new Clock();
                var handler = new Fixture { Fail = source };
                using var client = new SyncClient(store, handler, utcNow: () => clock.Now) { SharedReadInterval = TimeSpan.Zero };
                var state = new UserState();
                await client.Refresh(state, true, null);
                clock.Advance(10);
                await client.Refresh(state, true, null);
                handler.Fail = null;
                clock.Advance(20);
                await client.Refresh(state, true, null);
                Require(!client.Status.Contains("unavailable", StringComparison.Ordinal), "Recovery left stale failure status.");
                handler.Fail = source;
                await client.Refresh(state, true, null);
                Require(handler.Attempts(source) == 4, "Successful source retained cooldown.");
                clock.Advance(10);
                await client.Refresh(state, true, null);
                Require(handler.Attempts(source) == 5, "Recovery did not reset the failure exponent.");
            });
            await Check(source + "-cancel-does-not-start-backoff", async store =>
            {
                var clock = new Clock();
                using var cancellation = new CancellationTokenSource();
                var handler = new Fixture { CancelStage = source, Cancellation = cancellation };
                using var client = new SyncClient(store, handler, utcNow: () => clock.Now) { SharedReadInterval = TimeSpan.Zero };
                var state = new UserState();
                client.Queue(state, "gameCategories", "fixture", JsonValue.Create("retained"));
                bool cancelled = false;
                try { await client.Refresh(state, true, null, cancellation.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                Require(cancelled && client.Status.Contains("cancelled", StringComparison.Ordinal), "Cancellation was hidden.");
                handler.CancelStage = null;
                int before = handler.Attempts(source);
                await client.Refresh(state, true, null);
                Require(handler.Attempts(source) == before + 1, "Caller cancellation introduced failure backoff.");
                Require(store.LoadState().Pending.Count == 1, "Cancellation lost the outbox.");
            });
            await Check(source + "-forced-cancel-keeps-existing-deadline", async store =>
            {
                var clock = new Clock();
                using var cancellation = new CancellationTokenSource();
                var handler = new Fixture { Fail = source, Cancellation = cancellation };
                using var client = new SyncClient(store, handler, utcNow: () => clock.Now) { SharedReadInterval = TimeSpan.Zero };
                var state = new UserState();
                await client.Refresh(state, true, null);
                clock.Advance(2);
                handler.CancelStage = source;
                try { await client.Refresh(state, true, null, cancellation.Token, force: true); }
                catch (OperationCanceledException) { }
                Require(cancellation.IsCancellationRequested, "Fixture did not cancel the forced request.");
                handler.CancelStage = null;
                handler.Fail = null;
                int before = handler.Attempts(source);
                clock.Advance(8);
                await client.Refresh(state, true, null);
                Require(handler.Attempts(source) == before + 1, "Cancelled force attempt extended the original deadline.");
            });
        }
        await Check("staggered-source-deadlines", async store =>
        {
            var clock = new Clock();
            var handler = new Fixture { Fail = "shared" };
            using var client = new SyncClient(store, handler, utcNow: () => clock.Now) { SharedReadInterval = TimeSpan.Zero };
            var state = new UserState();
            await client.Refresh(state, true, null);
            clock.Advance(2); handler.Fail = "catalog";
            await client.Refresh(state, true, null);
            clock.Advance(2); handler.Fail = "proxy";
            await client.Refresh(state, true, null);
            Require(handler.Shared == 1 && handler.Catalog == 10 && handler.Proxy == 3 && handler.Direct == 2, "A failed source blocked another source.");
            clock.Advance(6); handler.Fail = null;
            await client.Refresh(state, true, null);
            Require(handler.Shared == 2 && handler.Catalog == 10 && handler.Proxy == 3 && handler.Direct == 3, "Shared deadline was coupled to catalog/proxy.");
            clock.Advance(2);
            await client.Refresh(state, true, null);
            Require(handler.Shared == 3 && handler.Catalog == 15 && handler.Proxy == 3 && handler.Direct == 4, "Catalog deadline was coupled to proxy.");
            clock.Advance(2);
            await client.Refresh(state, true, null);
            Require(handler.Proxy == 4 && handler.Direct == 4 && client.CatalogNotice == null, "Proxy did not recover independently.");
        });
        await Check("manual-force-and-outbox", async store =>
        {
            var clock = new Clock();
            var handler = new Fixture { Fail = "all" };
            using var client = new SyncClient(store, handler, utcNow: () => clock.Now) { SharedReadInterval = TimeSpan.Zero };
            var state = new UserState();
            client.Queue(state, "gameCategories", "fixture", JsonValue.Create("first"));
            await client.Refresh(state, true, "fixture-admin-token");
            client.Queue(state, "gameCategories", "fixture", JsonValue.Create("latest"));
            state.PendingGameBackups["fixture"] = @"F:\games\fixture\game.exe";
            await client.Refresh(state, true, "fixture-admin-token");
            Require(handler.Posts == 0 && handler.Shared == 1, "Backoff attempted a write or read.");
            handler.Fail = null;
            await client.Refresh(state, true, null, force: true);
            Require(handler.Shared == 2 && handler.Catalog == 10 && handler.Proxy == 2 && client.Online, "Manual force did not bypass all source delays.");
            var saved = store.LoadState();
            Require(saved.Pending.Count == 1 && saved.Pending[0].After?.GetValue<string>() == "latest" && saved.PendingGameBackups.Count == 1,
                "Backoff or recovery lost the latest queued edit/backup.");
            Require(client.Effective(state)["gameCategories"]?["fixture"]?.GetValue<string>() == "latest", "Queued edit is not effective after recovery.");
        });
        await Check("failed-write-backoff-retains-outbox", async store =>
        {
            var clock = new Clock();
            var handler = new Fixture { FailWrites = true };
            using var client = new SyncClient(store, handler, utcNow: () => clock.Now) { SharedReadInterval = TimeSpan.Zero };
            var state = new UserState();
            client.Queue(state, "gameCategories", "fixture", JsonValue.Create("retained"));
            await client.Refresh(state, true, "fixture-admin-token");
            clock.Advance(2);
            await client.Refresh(state, true, "fixture-admin-token");
            Require(handler.Shared == 2 && handler.Posts == 1 && !client.Online && client.LastSync == null, "Failed write was hammered or falsely acknowledged.");
            Require(handler.Catalog == 10 && handler.Proxy == 2 && store.LoadState().Pending.Count == 1, "Write backoff blocked another source or lost the outbox.");
            clock.Advance(8);
            await client.Refresh(state, true, "fixture-admin-token");
            Require(handler.Posts == 2 && store.LoadState().Pending.Count == 1, "Write retry lost the outbox.");
        });
        await Check("shared-success-timestamp-preserved-during-backoff", async store =>
        {
            var clock = new Clock();
            var handler = new Fixture();
            using var client = new SyncClient(store, handler, utcNow: () => clock.Now) { SharedReadInterval = TimeSpan.Zero };
            var state = new UserState();
            await client.Refresh(state, false, null);
            var last = client.LastSync;
            handler.Fail = "shared";
            clock.Advance(2);
            await client.Refresh(state, false, null);
            clock.Advance(2);
            await client.Refresh(state, false, null);
            Require(last == clock.Now.AddSeconds(-4).LocalDateTime && client.LastSync == last && !client.Online && handler.Shared == 2,
                "Backoff advanced the last successful sync timestamp.");
            Require(handler.Catalog == 0 && handler.Proxy == 0 && handler.Direct == 0, "Config-only refresh fetched catalog sources.");
        });
        await Check("proxy-repository-change-retries", async store =>
        {
            var clock = new Clock();
            var handler = new Fixture { Fail = "proxy" };
            using var client = new SyncClient(store, handler, utcNow: () => clock.Now) { SharedReadInterval = TimeSpan.Zero };
            var state = new UserState();
            await client.Refresh(state, true, null);
            state.Settings.RepoName = "different";
            await client.Refresh(state, true, null);
            Require(handler.Proxy == 2, "Old repository failure suppressed a different repository.");
        });
        foreach (string source in new[] { "shared", "catalog", "proxy" })
        {
            await Check(source + "-timeout-is-a-failure", async store =>
            {
                var clock = new Clock();
                var handler = new Fixture { TimeoutStage = source };
                using var client = new SyncClient(store, handler, utcNow: () => clock.Now) { SharedReadInterval = TimeSpan.Zero };
                var state = new UserState();
                await client.Refresh(state, true, null);
                clock.Advance(2);
                await client.Refresh(state, true, null);
                Require(handler.Attempts(source) == 1 && client.Status.Contains("request timed out; retry in 8s", StringComparison.Ordinal),
                    "Transport timeout was treated as caller cancellation or retried early.");
            });
        }
        await Check("deadline-starts-when-failure-finishes", async store =>
        {
            var clock = new Clock();
            var handler = new Fixture { Fail = "shared" };
            handler.OnRequest = stage => { if (stage == "shared") clock.Advance(20); };
            using var client = new SyncClient(store, handler, utcNow: () => clock.Now) { SharedReadInterval = TimeSpan.Zero };
            var state = new UserState();
            await client.Refresh(state, false, null);
            clock.Advance(2);
            await client.Refresh(state, false, null);
            Require(handler.Shared == 1 && client.Status.Contains("retry in 8s", StringComparison.Ordinal), "Slow request consumed its own backoff window.");
        });
        await Check("edit-during-failed-write-retained", async store =>
        {
            var clock = new Clock();
            var handler = new Fixture { FailWrites = true };
            using var client = new SyncClient(store, handler, utcNow: () => clock.Now) { SharedReadInterval = TimeSpan.Zero };
            var state = new UserState();
            client.Queue(state, "gameCategories", "fixture", JsonValue.Create("original"));
            handler.OnRequest = stage => { if (stage == "write") client.Queue(state, "gameCategories", "fixture", JsonValue.Create("newer")); };
            await client.Refresh(state, false, "fixture-admin-token");
            await client.Refresh(state, false, "fixture-admin-token");
            var saved = store.LoadState();
            Require(handler.Posts == 1 && saved.Pending.Count == 1 && saved.Pending[0].After?.GetValue<string>() == "newer", "Failed write/backoff discarded an edit made during the request.");
        });
        return passed;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Clock
    {
        public DateTimeOffset Now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public void Advance(int seconds) => Now = Now.AddSeconds(seconds);
    }

    private sealed class Fixture : HttpMessageHandler
    {
        public string? Fail, CancelStage, TimeoutStage;
        public Action<string>? OnRequest;
        public CancellationTokenSource? Cancellation;
        public int Shared, Catalog, Proxy, Direct, Posts;
        public bool FailWrites;
        public int Attempts(string source) => source == "shared" ? Shared : source == "catalog" ? Catalog / 5 : Proxy;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            string stage;
            string body;
            if (uri.Host == "hub.docker.com")
            {
                stage = "direct"; Direct++;
                body = "{\"count\":1,\"next\":null,\"results\":[{\"name\":\"fixture\",\"full_size\":123}]}";
            }
            else
            {
                Require(uri.Host == new Uri(SyncClient.Production).Host, "Unexpected fixture host.");
                Require(request.Headers.Authorization == null, "Docker token leaked to website.");
                if (request.Method != HttpMethod.Get)
                {
                    stage = "write"; Posts++;
                    body = "{\"success\":true}";
                }
                else if (uri.AbsolutePath == "/api/admin-config")
                {
                    stage = "shared"; Shared++;
                    body = "{\"success\":true,\"config\":{\"gameCategories\":{},\"hiddenTabs\":[],\"tabs\":[]},\"configVersion\":\"fixture\"}";
                }
                else if (uri.AbsolutePath == "/api/docker-tags")
                {
                    stage = "proxy"; Proxy++;
                    body = "{\"success\":true,\"repository\":\"michadockermisha/backup\",\"count\":1,\"tags\":[{\"name\":\"fixture\"}]}";
                }
                else
                {
                    Require(uri.AbsolutePath.StartsWith("/data/", StringComparison.Ordinal), "Unexpected route.");
                    stage = "catalog"; Catalog++;
                    body = uri.AbsolutePath == "/data/games.json" ? "[{\"id\":\"fixture\"}]" : uri.AbsolutePath == "/data/tabs.json" ? "[]" : "{}";
                }
            }
            if (CancelStage == stage) { Cancellation!.Cancel(); cancellationToken.ThrowIfCancellationRequested(); }
            OnRequest?.Invoke(stage);
            if (TimeoutStage == stage) return Task.FromException<HttpResponseMessage>(new TaskCanceledException("Fixture timeout"));
            bool fail = Fail == stage || Fail == "all" && stage != "direct" || FailWrites && stage == "write";
            return Task.FromResult(new HttpResponseMessage(fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent(fail ? "Usage exceeded" : body, Encoding.UTF8, "application/json") });
        }
    }
}
