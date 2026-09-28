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

/// <summary>Deterministic, offline fixtures. No native application dependencies.</summary>
public static class DockerNamespaceTests
{
    public static int Run(string root) => RunAsync(root).GetAwaiter().GetResult();

    private static async Task<int> RunAsync(string root)
    {
        string suite = Path.Combine(root, "docker-namespace-" + Guid.NewGuid().ToString("N"));
        int checks = 0;
        async Task Check(string name, Func<Fixture, DockerNamespaceClient, string, Task> body)
        {
            var fixture = new Fixture();
            using var http = new HttpClient(fixture);
            string directory = Path.Combine(suite, name);
            var client = new DockerNamespaceClient(http, fixture.Token, directory, () => fixture.Now, fixture.Delay);
            try { await body(fixture, client, directory).ConfigureAwait(false); checks++; }
            catch (Exception ex) { throw new InvalidOperationException("Docker namespace [" + name + "] failed.", ex); }
        }
        Task<JsonObject> Read(DockerNamespaceClient client, CancellationToken ct = default) => client.ReadAsync("michadockermisha", "backup", ct);

        await Check("multipage-auth-case-metadata-empty", async (f, c, path) =>
        {
            var result = await Read(c).ConfigureAwait(false);
            Require(Complete(result) && (int)result["repositoryCount"]! == 3, "Namespace incomplete.");
            Require(f.TokenCalls == 1 && f.Authenticated == f.Calls.Count, "Authentication not reused.");
            Require(f.Calls.First(x => x.Contains("/tags/", StringComparison.Ordinal)).Contains("/backup/", StringComparison.Ordinal), "Priority not first.");
            var backup = Repository(result, "backup");
            Require((int)backup["count"]! == 2 && (string)backup["tags"]![0]!["name"]! == "Game" &&
                (string)backup["tags"]![1]!["name"]! == "game", "Tag case collapsed.");
            Require((int)Repository(result, "other")["count"]! == 1 && (int)Repository(result, "empty")["count"]! == 0, "Cross-repo collision or empty repo lost.");
            Require((long)backup["tags"]![0]!["full_size"]! == 42 && (string)backup["tags"]![0]!["digest"]! == "sha256:tag" &&
                (string)backup["tags"]![0]!["images"]![0]!["architecture"]! == "amd64", "Metadata lost.");
            Require(Directory.GetFiles(path, "*.json").Length == 4 && Directory.GetFiles(path, "*.tmp").Length == 0, "Cache writes missing.");
        }).ConfigureAwait(false);

        await Check("incremental-and-forced", async (f, c, _) =>
        {
            Require(Complete(await Read(c).ConfigureAwait(false)), "Initial failed.");
            f.Calls.Clear();
            var unchanged = await Read(c).ConfigureAwait(false);
            Require(Complete(unchanged) && (int)unchanged["cachedRepositoryCount"]! == 3 && !f.Calls.Any(x => x.Contains("/tags/")), "Unchanged repos fetched.");
            f.OtherFingerprint = "changed";
            f.Calls.Clear();
            var changed = await Read(c).ConfigureAwait(false);
            Require(Complete(changed) && (int)changed["refreshedRepositoryCount"]! == 1 &&
                f.Calls.Count(x => x.Contains("/tags/")) == 1 && f.Calls.Any(x => x.Contains("/other/tags/")), "Changed repo not refreshed alone.");
            f.Now = f.Now.AddHours(25);
            f.Calls.Clear();
            var forced = await Read(c).ConfigureAwait(false);
            Require(Complete(forced) && (int)forced["refreshedRepositoryCount"]! == 3, "Periodic refresh skipped.");
        }).ConfigureAwait(false);

        foreach (string next in new[] { "https://evil.example/steal", "http://hub.docker.com/v2/namespaces/michadockermisha/repositories/",
            "https://hub.docker.com:444/v2/namespaces/michadockermisha/repositories/",
            "https://user@hub.docker.com/v2/namespaces/michadockermisha/repositories/",
            "/v2/namespaces/another/repositories/", "?page_size=100&ordering=name", "?page=2#bad", "" })
        {
            await Check("invalid-pagination-" + checks, async (f, c, _) =>
            {
                f.ListNextOverride = next;
                Require(!Complete(await Read(c).ConfigureAwait(false)), "Unsafe pagination accepted.");
                Require(f.Calls.All(x => x.StartsWith("https://hub.docker.com/v2/namespaces/michadockermisha/repositories/", StringComparison.Ordinal)), "Untrusted request sent.");
            }).ConfigureAwait(false);
        }

        foreach (string mode in new[] { "count-change", "missing-tail", "duplicate-repo", "duplicate-tag", "empty-page", "missing-next", "changed-closing", "redirect", "malformed" })
        {
            await Check(mode, async (f, c, path) =>
            {
                f.Mode = mode;
                Require(!Complete(await Read(c).ConfigureAwait(false)), "Invalid scan accepted.");
                Require(!Directory.Exists(path) || Directory.GetFiles(path, "namespace-*.json").Length == 0, "Partial namespace persisted.");
            }).ConfigureAwait(false);
        }

        await Check("partial-failure-preserves-caches", async (f, c, path) =>
        {
            Require(Complete(await Read(c).ConfigureAwait(false)), "Initial failed.");
            var before = Directory.GetFiles(path, "*.json").ToDictionary(p => p, File.ReadAllText);
            f.Now = f.Now.AddDays(2);
            f.Mode = "forbidden-backup";
            var partial = await Read(c).ConfigureAwait(false);
            Require(!Complete(partial) && (bool)Repository(partial, "backup")["stale"]! && (int)Repository(partial, "backup")["count"]! == 2, "Failed cache not retained.");
            Require(before.Where(x => Path.GetFileName(x.Key).StartsWith("namespace-", StringComparison.Ordinal) || JsonNode.Parse(x.Value)?["repository"]?.GetValue<string>() == "michadockermisha/backup")
                .All(x => File.ReadAllText(x.Key) == x.Value), "Complete cache overwritten.");
            Require(!partial.ToJsonString().Contains(Fixture.Secret, StringComparison.Ordinal), "Token leaked.");
            var baseline = JsonNode.Parse(before.Single(x => Path.GetFileName(x.Key).StartsWith("namespace-", StringComparison.Ordinal)).Value)!.AsObject();
            var updates = NamespaceUpdates.Combine("michadockermisha", baseline, partial);
            Require((int)updates["repositoryCount"]! == 2 && updates["repositories"]!.AsArray().All(row => (string)row!["repository"]! != "michadockermisha/backup"), "Healthy HTTP results were blocked by the failed backup repository.");
            f.Mode = "";
            var recovered = await Read(c).ConfigureAwait(false);
            Require(Complete(recovered) && (int)NamespaceUpdates.Combine("michadockermisha", recovered, updates)["repositoryCount"]! == 0, "Recovery retained obsolete partial overrides.");
        }).ConfigureAwait(false);

        await Check("no-incremental-before-complete", async (f, c, _) =>
        {
            f.Mode = "forbidden-backup";
            Require(!Complete(await Read(c).ConfigureAwait(false)), "Failure not reported.");
            f.Mode = "";
            var result = await Read(c).ConfigureAwait(false);
            Require(Complete(result) && (int)result["cachedRepositoryCount"]! == 0, "Partial baseline reused.");
        }).ConfigureAwait(false);

        await Check("backoff", async (f, c, _) =>
        {
            f.Throttle = 2;
            Require(Complete(await Read(c).ConfigureAwait(false)) && f.Delays.Count == 2 && f.Delays.All(x => x == TimeSpan.FromSeconds(3)), "Retry-After ignored.");
        }).ConfigureAwait(false);
        await Check("bounded-retries", async (f, c, _) =>
        {
            f.Throttle = 100;
            Require(!Complete(await Read(c).ConfigureAwait(false)) && f.Calls.Count == 5, "Retries unbounded.");
        }).ConfigureAwait(false);
        await Check("provider-failure-redacted", async (f, c, _) =>
        {
            f.FailToken = true;
            var result = await Read(c).ConfigureAwait(false);
            Require(!Complete(result) && f.Calls.Count == 0 && !result.ToJsonString().Contains(Fixture.Secret), "Provider error leaked.");
        }).ConfigureAwait(false);
        await Check("cancellation", async (f, c, path) =>
        {
            Require(Complete(await Read(c).ConfigureAwait(false)), "Initial failed.");
            string full = Directory.GetFiles(path, "namespace-*.json").Single();
            string before = File.ReadAllText(full);
            using var source = new CancellationTokenSource();
            f.Cancel = source;
            f.Now = f.Now.AddDays(2);
            bool canceled = false;
            try { await Read(c, source.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { canceled = true; }
            Require(canceled && File.ReadAllText(full) == before, "Cancellation lost or cache replaced.");
            f.Cancel = null;
            Require(Complete(await Read(c).ConfigureAwait(false)), "Cancellation did not release scan gate.");
        }).ConfigureAwait(false);
        await Check("deep-pagination", async (f, c, _) =>
        {
            f.Deep = true;
            var result = await Read(c).ConfigureAwait(false);
            Require(Complete(result) && (int)Repository(result, "backup")["count"]! == 1002, "Deep pagination truncated.");
        }).ConfigureAwait(false);
        await Check("bounded-concurrency", async (f, c, _) =>
        {
            f.Concurrent = true;
            Require(Complete(await Read(c).ConfigureAwait(false)) && f.MaximumActive == 4, "Expected four bounded workers.");
        }).ConfigureAwait(false);
        await Check("unauthorized", async (f, c, _) =>
        {
            f.Mode = "unauthorized";
            var result = await Read(c).ConfigureAwait(false);
            Require(!Complete(result) && f.TokenCalls == 1 && f.Calls.Count == 1, "Unauthorized listing accepted or retried.");
        }).ConfigureAwait(false);
        await Check("empty-namespace", async (f, c, _) =>
        {
            f.Mode = "empty-namespace";
            var result = await Read(c).ConfigureAwait(false);
            Require(Complete(result) && (int)result["repositoryCount"]! == 0 && ((JsonArray)result["repositories"]!).Count == 0,
                "Empty namespace treated as failure.");
        }).ConfigureAwait(false);
        return checks;
    }

    private static bool Complete(JsonObject result) => (bool)result["success"]! && (bool)result["complete"]!;
    private static JsonObject Repository(JsonObject result, string name) => (JsonObject)((JsonArray)result["repositories"]!).Single(r =>
        (string)r!["repository"]! == "michadockermisha/" + name)!;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Fixture : HttpMessageHandler
    {
        public const string Secret = "offline-secret-never-report";
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public readonly List<string> Calls = new();
        public readonly List<TimeSpan> Delays = new();
        public int TokenCalls, Authenticated, Throttle;
        public string Mode = "", OtherFingerprint = "initial";
        public string? ListNextOverride;
        public bool FailToken, Deep, Concurrent;
        public int MaximumActive;
        private int active;
        private readonly TaskCompletionSource<bool> fourActive = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource? Cancel;
        private int listStarts;
        public Task<string> Token(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); TokenCalls++;
            if (FailToken) throw new InvalidOperationException(Secret);
            return Task.FromResult(Secret);
        }
        public Task Delay(TimeSpan duration, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Delays.Add(duration); return Task.CompletedTask;
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            bool observed = Concurrent && request.RequestUri!.AbsolutePath.Contains("/tags/", StringComparison.Ordinal) &&
                !request.RequestUri.AbsolutePath.Contains("/backup/", StringComparison.Ordinal);
            if (observed)
            {
                int current = Interlocked.Increment(ref active);
                lock (Calls) MaximumActive = Math.Max(MaximumActive, current);
                if (current >= 4) fourActive.TrySetResult(true);
                await fourActive.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            try { return await SendCore(request, ct).ConfigureAwait(false); }
            finally { if (observed) Interlocked.Decrement(ref active); }
        }
        private Task<HttpResponseMessage> SendCore(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Require(request.Method == HttpMethod.Get, "Remote mutation attempted.");
            string url = request.RequestUri!.AbsoluteUri;
            lock (Calls) Calls.Add(url);
            if (request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == Secret) Interlocked.Increment(ref Authenticated);
            else return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            if (Throttle-- > 0)
            {
                var response = new HttpResponseMessage(Throttle % 2 == 0 ? HttpStatusCode.TooManyRequests : HttpStatusCode.ServiceUnavailable);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
                return Task.FromResult(response);
            }
            if (Mode == "redirect") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect));
            if (Mode == "unauthorized") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            if (Mode == "malformed") return Reply("{bad");
            bool tags = request.RequestUri.AbsolutePath.Contains("/tags/", StringComparison.Ordinal);
            if (!tags)
            {
                if (Mode == "empty-namespace") return Reply(Page(0, null, new JsonArray()).ToJsonString());
                if (Concurrent) return Reply(Page(9, null, new JsonArray(Enumerable.Range(0, 9).Select(i => (JsonNode)Repo(i == 0 ? "backup" : "repo-" + i)).ToArray())).ToJsonString());
                bool second = request.RequestUri.Query.Contains("page=2", StringComparison.Ordinal);
                if (!second) listStarts++;
                var rows = second ? new JsonArray(Repo(Mode == "duplicate-repo" ? "other" : "backup"), Repo("empty")) : new JsonArray(Repo("other"));
                var page = Page(Mode == "count-change" && second ? 4 : 3, second ? null : ListNextOverride ?? "?page=2", rows);
                if (Mode == "missing-tail" && !second) page["next"] = null;
                if (Mode == "missing-next") page.Remove("next");
                if (Mode == "empty-page" && !second) page["results"] = new JsonArray();
                return Reply(page.ToJsonString());
            }
            if (Cancel != null) { Cancel.Cancel(); ct.ThrowIfCancellationRequested(); }
            if (request.RequestUri.AbsolutePath.Contains("/backup/"))
            {
                if (Mode == "forbidden-backup") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
                if (Deep)
                {
                    int p = request.RequestUri.Query.StartsWith("?page=", StringComparison.Ordinal) ? int.Parse(request.RequestUri.Query.Substring(6)) : 1;
                    return Reply(Page(1002, p < 1002 ? "?page=" + (p + 1) : null, new JsonArray(Tag("tag-" + p))).ToJsonString());
                }
                bool second = request.RequestUri.Query.Contains("page=2", StringComparison.Ordinal);
                return Reply(Page(2, second ? null : "?page=2", new JsonArray(Tag(second && Mode != "duplicate-tag" ? "game" : "Game"))).ToJsonString());
            }
            bool empty = request.RequestUri.AbsolutePath.Contains("/empty/");
            return Reply(Page(empty ? 0 : 1, null, empty ? new JsonArray() : new JsonArray(Tag("Game"))).ToJsonString());
        }
        private JsonObject Repo(string name) => new() { ["name"] = name, ["namespace"] = "michadockermisha",
            ["last_updated"] = Mode == "changed-closing" && listStarts > 1 ? "changed" : name == "other" ? OtherFingerprint : "initial" };
        private static JsonObject Page(int count, string? next, JsonArray results) => new() { ["count"] = count, ["next"] = next, ["results"] = results };
        private static JsonObject Tag(string name) => new() { ["name"] = name, ["full_size"] = 42L, ["last_updated"] = "2026-01-01T00:00:00Z",
            ["digest"] = "sha256:tag", ["images"] = new JsonArray(new JsonObject { ["architecture"] = "amd64", ["os"] = "linux", ["digest"] = "sha256:image", ["size"] = 42L }) };
        private static Task<HttpResponseMessage> Reply(string body) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
