using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

public static class SharedFallbackTests
{
    public static int Run(string root) => Task.Run(() => RunAsync(root)).GetAwaiter().GetResult();
    private static async Task<int> RunAsync(string root)
    {
        int passed = 0;
        async Task Check(string name, Func<LibraryStore, Task> test)
        {
            var store = new LibraryStore(Path.Combine(root, "fallback-" + name + "-" + Guid.NewGuid().ToString("N")));
            store.CacheData("admin-config.json", "{\"gameCategories\":{\"cached\":\"old\"},\"hiddenTabs\":[],\"tabs\":[]}");
            try { await test(store); passed++; }
            catch (Exception ex) { throw new InvalidOperationException(name + ": " + ex.Message, ex); }
        }
        await Check("read-only-queue-and-recovery", async store =>
        {
            var handler = new Fixture(); using var client = new SyncClient(store, handler);
            var state = new UserState(); client.Queue(state, "gameCategories", "fixture", JsonValue.Create("local"));
            await client.Refresh(state, false, "must-not-leak");
            Require(client.Remote["gameCategories"]?["fixture"]?.GetValue<string>() == "github", "Canonical fallback was not loaded");
            Require(!client.Online && client.LastSync == null && !client.SupportsConditionalWrites && client.Version == "", "Fallback falsely confirmed synchronization");
            Require(client.SharedStatus.Contains("read-only") && client.SharedStatus.Contains("GitHub"), "Missing degraded provenance");
            Require(store.LoadState().Pending.Count == 1 && handler.Posts == 0 && client.Effective(state)["gameCategories"]?["fixture"]?.GetValue<string>() == "local", "Fallback lost/published local edit");
            await client.Refresh(state, false, "must-not-leak");
            Require(handler.GitHub == 1 && handler.Reads == 1, "Cooldown polled fallback");
            Require(client.SharedStatus.Contains("read-only"), "Cooldown lost fallback provenance");
            handler.Healthy = true; await client.Refresh(state, false, null, force: true);
            Require(client.Online && client.LastSync != null && !client.SharedStatus.Contains("GitHub") && handler.GitHub == 1, "Primary recovery failed");
            Require(store.LoadState().Pending.Count == 1, "Recovery without write lost queue");
        });
        foreach (string body in new[] { "{}", "[]", "{bad", "{\"gameCategories\":{\"a\":4},\"hiddenTabs\":[],\"tabs\":[]}", new string(' ', 2 * 1024 * 1024 + 1) })
            await Check("reject-invalid-" + passed, async store =>
            {
                string before = store.ReadConfig().ToJsonString(); var handler = new Fixture { Body = body };
                using var client = new SyncClient(store, handler); await client.Refresh(new UserState(), false, null);
                Require(handler.GitHub == 1 && client.Remote.ToJsonString() == before && store.ReadConfig().ToJsonString() == before, "Invalid fallback replaced cache");
            });
        await Check("http-failure-preserves-cache", async store =>
        {
            var handler = new Fixture { GitHubStatus = HttpStatusCode.ServiceUnavailable }; using var client = new SyncClient(store, handler);
            string before = client.Remote.ToJsonString(); await client.Refresh(new UserState(), false, null);
            Require(handler.GitHub == 1 && client.Remote.ToJsonString() == before && !client.Online, "Failed fallback changed cache");
        });
        await Check("write-readback-no-fallback", async store =>
        {
            var handler = new Fixture { Healthy = true, FailReadback = true }; using var client = new SyncClient(store, handler);
            var state = new UserState(); client.Queue(state, "gameCategories", "fixture", JsonValue.Create("local"));
            await client.Refresh(state, false, "fixture");
            Require(handler.Posts == 1 && handler.GitHub == 0 && state.Pending.Count == 1 && !client.Online, "Fallback used as write confirmation");
        });
        await Check("cancel-fallback", async store =>
        {
            using var cancel = new CancellationTokenSource(); var handler = new Fixture { Cancel = cancel }; using var client = new SyncClient(store, handler);
            string before = client.Remote.ToJsonString(); bool cancelled = false;
            try { await client.Refresh(new UserState(), false, null, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled && client.Remote.ToJsonString() == before, "Cancellation hidden or cache changed");
        });
        await Check("offline-no-fallback", async store =>
        {
            var handler = new OfflineNetworkGuard(); using var client = new SyncClient(store, handler);
            await client.Refresh(new UserState(), true, null);
            Require(handler.Attempts <= 1 && !client.Online && !client.SharedStatus.Contains("GitHub"), "Offline attempted fallback");
        });
        await Check("healthy-read-cadence", async store =>
        {
            var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var handler = new Fixture { Healthy = true }; using var client = new SyncClient(store, handler, utcNow: () => now);
            var state = new UserState(); await client.Refresh(state, false, null);
            var last = client.LastSync;
            for (int i = 0; i < 29; i++) { now = now.AddSeconds(2); await client.Refresh(state, false, null); }
            Require(handler.Reads == 1 && client.Online && client.LastSync == last, "Healthy polling flooded shared endpoint");
            now = now.AddSeconds(2); await client.Refresh(state, false, null);
            Require(handler.Reads == 2, "Healthy read not refreshed at deadline");
            await client.Refresh(state, false, null, force: true);
            Require(handler.Reads == 3, "Manual refresh did not bypass cadence");
            client.Queue(state, "gameCategories", "fixture", JsonValue.Create("local"));
            await client.Refresh(state, false, null);
            Require(handler.Reads == 3 && client.SharedStatus == "Shared configuration read" && client.Status.Contains("1 changes queued"),
                "Cached healthy status falsely synchronized queued changes or fetched again");
            state.Pending[0].Conflict = "Review required";
            await client.Refresh(state, false, "fixture");
            Require(handler.Reads == 3 && handler.Posts == 0 && client.Status.Contains("Conflict needs review"),
                "Conflicted queue bypassed healthy cadence");
            client.Queue(state, "gameCategories", "second", JsonValue.Create("local"));
            await client.Refresh(state, false, "fixture");
            Require(handler.Reads == 3 && handler.Posts == 0, "Mixed conflicted queue bypassed healthy cadence");
            state.Pending[0].Conflict = null;
            await client.Refresh(state, false, "fixture");
            Require(handler.Posts == 1, "Queued write suppressed by healthy cadence");
        });
        await Check("fallback-freshness", async store =>
        {
            var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var handler = new Fixture(); using var client = new SyncClient(store, handler, utcNow: () => now);
            var state = new UserState(); await client.Refresh(state, false, null);
            now = now.AddSeconds(10); await client.Refresh(state, false, null);
            Require(handler.Reads == 2 && handler.GitHub == 1 && client.SharedStatus.Contains("read-only"), "Fresh GitHub snapshot unnecessarily reread");
            now = now.AddSeconds(50); await client.Refresh(state, false, null);
            Require(handler.GitHub == 2, "Stale GitHub snapshot did not refresh");
        });
        return passed;
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Fixture : HttpMessageHandler
    {
        public bool Healthy, FailReadback;
        public int GitHub, Reads, Posts;
        public string Body = "{\"gameCategories\":{\"fixture\":\"github\"},\"hiddenTabs\":[],\"tabs\":[]}";
        public HttpStatusCode GitHubStatus = HttpStatusCode.OK;
        public CancellationTokenSource? Cancel;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "raw.githubusercontent.com")
            {
                GitHub++;
                Require(request.Method == HttpMethod.Get && request.Headers.Authorization == null && !request.Headers.Contains("X-Admin-Token"), "Credentials or writes reached GitHub");
                Require(request.RequestUri.AbsolutePath == "/Michaelunkai/game-library-manager-web/main/data/admin-config.json", "Wrong canonical source");
                Cancel?.Cancel(); cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new HttpResponseMessage(GitHubStatus) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
            }
            if (request.Method == HttpMethod.Post) { Posts++; return Response(HttpStatusCode.OK, "{\"success\":true}"); }
            Reads++;
            return Healthy && !(FailReadback && Posts > 0)
                ? Response(HttpStatusCode.OK, "{\"success\":true,\"config\":{\"gameCategories\":{},\"hiddenTabs\":[],\"tabs\":[]},\"configVersion\":\"primary\",\"capabilities\":{\"conditionalWrites\":true}}")
                : Response(HttpStatusCode.ServiceUnavailable, "{\"error\":\"usage_exceeded\"}");
        }
        private static Task<HttpResponseMessage> Response(HttpStatusCode status, string body) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
