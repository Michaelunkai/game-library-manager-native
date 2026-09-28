using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

/// <summary>
/// Reads all repositories visible to the supplied Docker Hub identity. The caller owns
/// HttpClient and must disable automatic redirects on its handler. Use a namespace-owner
/// token to include private repositories; Hub cannot report repositories hidden from it.
/// No threads are created. Call from the application's background refresh workflow.
/// </summary>
public sealed class DockerNamespaceClient
{
    private readonly HttpClient http;
    private readonly Func<CancellationToken, Task<string>> tokenProvider;
    private readonly string cacheDirectory;
    private readonly SemaphoreSlim scanGate = new(1, 1);
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);

    public DockerNamespaceClient(HttpClient http, Func<CancellationToken, Task<string>> tokenProvider, string cacheDirectory)
        : this(http, tokenProvider, cacheDirectory, () => DateTimeOffset.UtcNow, Task.Delay) { }

    internal DockerNamespaceClient(HttpClient http, Func<CancellationToken, Task<string>> tokenProvider,
        string cacheDirectory, Func<DateTimeOffset> utcNow, Func<TimeSpan, CancellationToken, Task> delay)
    {
        this.http = http ?? throw new ArgumentNullException(nameof(http));
        this.tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        this.cacheDirectory = Path.GetFullPath(cacheDirectory);
        this.utcNow = utcNow;
        this.delay = delay;
    }

    /// <summary>
    /// Returns success/complete, namespace, repositoryCount (discovered count), fetchedAt,
    /// repositories, status, errors, refreshedRepositoryCount and cachedRepositoryCount.
    /// Each repository has repository, count, tags, complete, stale, fetchedAt and fingerprint.
    /// Failures return complete=false, with last complete per-repository data marked stale.
    /// Cancellation throws OperationCanceledException. Only complete scans replace namespace
    /// cache; only complete tag enumerations replace repository caches. Reuse begins after
    /// a complete initial scan, and each repository is fetched at least every 24 hours.
    /// Complete means count-checked pages and an unchanged closing repository listing,
    /// not a transactional snapshot (Docker Hub provides no snapshot transaction).
    /// </summary>
    public async Task<JsonObject> ReadAsync(string namespaceName, string priorityRepository, CancellationToken cancellationToken)
    {
        ValidateSegment(namespaceName);
        await scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var repositories = new List<JsonObject>();
            var errors = new List<JsonObject>();
            int discovered = 0, refreshed = 0, reused = 0;
            string stage = "authentication";
            try
            {
                // Fetch once per scan; never persist, log, or include provider exceptions.
                string token = await tokenProvider(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(token) || token.Contains('\r') || token.Contains('\n'))
                    throw new InvalidDataException();
                var auth = new AuthenticationHeaderValue("Bearer", token);
                stage = "listing";
                var listing = await ListRepositories(namespaceName, auth, cancellationToken).ConfigureAwait(false);
                discovered = listing.Count;
                var baseline = await ReadCache(CachePath("namespace", namespaceName), cancellationToken).ConfigureAwait(false);
                bool incremental = baseline?["complete"]?.GetValue<bool>() == true &&
                    baseline?["namespace"]?.GetValue<string>() == namespaceName;
                string priority = priorityRepository.StartsWith(namespaceName + "/", StringComparison.Ordinal)
                    ? priorityRepository.Substring(namespaceName.Length + 1) : priorityRepository;
                var ordered = listing.OrderBy(x => x.Key == priority ? 0 : 1).ThenBy(x => x.Key, StringComparer.Ordinal).ToArray();
                int cursor = -1;
                object resultLock = new();
                async Task Worker(bool oneRepository = false)
                {
                    while (true)
                    {
                        int index = Interlocked.Increment(ref cursor);
                        if (index >= ordered.Length) return;
                        cancellationToken.ThrowIfCancellationRequested();
                        var entry = ordered[index];
                        string repository = namespaceName + "/" + entry.Key;
                        JsonObject? cached = null;
                        try
                        {
                            cached = await ReadCache(CachePath("repository", repository), cancellationToken).ConfigureAwait(false);
                            if (!ValidRepositoryCache(cached, repository)) cached = null;
                            if (incremental && cached != null && entry.Value.Length > 0 &&
                                cached["fingerprint"]?.GetValue<string>() == entry.Value &&
                                DateTimeOffset.TryParse(cached["fetchedAt"]?.GetValue<string>(), CultureInfo.InvariantCulture,
                                    DateTimeStyles.RoundtripKind, out var fetched) && utcNow() >= fetched && utcNow() - fetched < RefreshInterval)
                            {
                                cached["stale"] = false;
                                lock (resultLock) { repositories.Add(cached); reused++; }
                                if (oneRepository) return;
                                continue;
                            }
                            var tags = await ReadPages(Route(namespaceName, entry.Key), auth, cancellationToken).ConfigureAwait(false);
                            var snapshot = new JsonObject
                            {
                                ["repository"] = repository, ["count"] = tags.Count,
                                ["tags"] = new JsonArray(tags.Select(t => (JsonNode)t.DeepClone()).ToArray()),
                                ["complete"] = true, ["stale"] = false, ["fingerprint"] = entry.Value,
                                ["fetchedAt"] = utcNow().ToString("O", CultureInfo.InvariantCulture)
                            };
                            await WriteCache(CachePath("repository", repository), snapshot, cancellationToken).ConfigureAwait(false);
                            lock (resultLock) { repositories.Add(snapshot); refreshed++; }
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            lock (resultLock)
                            {
                                errors.Add(Error(repository, ex));
                                if (cached != null) { cached["stale"] = true; repositories.Add(cached); }
                                else repositories.Add(new JsonObject { ["repository"] = repository, ["count"] = 0,
                                    ["tags"] = new JsonArray(), ["complete"] = false, ["stale"] = true });
                            }
                        }
                        if (oneRepository) return;
                    }
                }
                stage = "repositories";
                // Finish the priority repository before disk-read scheduling can let others overtake it.
                if (ordered.Length > 0 && ordered[0].Key == priority) await Worker(true).ConfigureAwait(false);
                await Task.WhenAll(Enumerable.Range(0, Math.Min(4, ordered.Length)).Select(_ => Worker())).ConfigureAwait(false);
                stage = "verification";
                var closing = await ListRepositories(namespaceName, auth, cancellationToken).ConfigureAwait(false);
                if (listing.Count != closing.Count || listing.Any(x => !closing.TryGetValue(x.Key, out string? value) || value != x.Value))
                    throw new InvalidDataException();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { errors.Add(Error(stage, ex)); }

            cancellationToken.ThrowIfCancellationRequested();
            bool complete = errors.Count == 0;
            var result = new JsonObject
            {
                ["success"] = complete, ["complete"] = complete, ["namespace"] = namespaceName,
                ["repositoryCount"] = discovered, ["fetchedAt"] = utcNow().ToString("O", CultureInfo.InvariantCulture),
                ["repositories"] = new JsonArray(repositories.OrderBy(r => r["repository"]!.GetValue<string>(), StringComparer.Ordinal).Select(r => (JsonNode)r).ToArray()),
                ["errors"] = new JsonArray(errors.Select(e => (JsonNode)e).ToArray()),
                ["status"] = complete ? "complete" : "incomplete",
                ["refreshedRepositoryCount"] = refreshed, ["cachedRepositoryCount"] = reused
            };
            if (complete)
            {
                try { await WriteCache(CachePath("namespace", namespaceName), result, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    result["success"] = false; result["complete"] = false; result["status"] = "cache-write-failed";
                    ((JsonArray)result["errors"]!).Add(Error("cache", ex));
                }
            }
            return result;
        }
        finally { scanGate.Release(); }
    }

    private async Task<Dictionary<string, string>> ListRepositories(string ns, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var rows = await ReadPages(Route(ns), auth, ct).ConfigureAwait(false);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            string name = row["name"]!.GetValue<string>();
            ValidateSegment(name);
            if (row["namespace"] != null && row["namespace"]!.GetValue<string>() != ns) throw new InvalidDataException();
            result.Add(name, row["last_updated"]?.GetValue<string>() ?? "");
        }
        return result;
    }

    private static Uri Route(string ns, string? repo = null) => new("https://hub.docker.com/v2/namespaces/" + ns +
        "/repositories/" + (repo == null ? "" : repo + "/tags/") + "?page_size=100&ordering=name");

    private async Task<List<JsonObject>> ReadPages(Uri first, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var rows = new List<JsonObject>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        Uri? next = first;
        int? expected = null;
        while (next != null)
        {
            ct.ThrowIfCancellationRequested();
            ValidatePage(next, first);
            if (!visited.Add(next.AbsoluteUri)) throw new InvalidDataException();
            var page = await GetPage(next, auth, ct).ConfigureAwait(false);
            int count = page["count"]?.GetValue<int>() ?? throw new InvalidDataException();
            if (count < 0 || (expected != null && count != expected)) throw new InvalidDataException();
            expected = count;
            if (page["results"] is not JsonArray items) throw new InvalidDataException();
            foreach (var item in items)
            {
                if (item is not JsonObject row || row["name"] is not JsonValue nameValue ||
                    !nameValue.TryGetValue<string>(out string? name) || string.IsNullOrEmpty(name) || !names.Add(name))
                    throw new InvalidDataException();
                rows.Add((JsonObject)row.DeepClone()); // Preserve exact tag case and all image/platform metadata.
            }
            if (rows.Count > count || !page.ContainsKey("next")) throw new InvalidDataException();
            string? link = page["next"]?.GetValue<string>();
            if (link != null && (string.IsNullOrWhiteSpace(link) || items.Count == 0 || rows.Count >= count)) throw new InvalidDataException();
            next = link == null ? null : new Uri(next, link);
        }
        if (rows.Count != expected) throw new InvalidDataException();
        return rows;
    }

    private static void ValidatePage(Uri uri, Uri first)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "hub.docker.com" || uri.Port != 443 ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != first.AbsolutePath)
            throw new InvalidDataException();
    }

    private async Task<JsonObject> GetPage(Uri uri, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = auth;
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is Uri final && final != uri) throw new InvalidDataException();
            if ((response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.ServiceUnavailable) && attempt < 4)
            {
                TimeSpan wait = response.Headers.RetryAfter?.Delta ??
                    (response.Headers.RetryAfter?.Date is DateTimeOffset date ? date - utcNow() : TimeSpan.FromSeconds(Math.Pow(2, attempt)));
                // Never retry earlier than Retry-After; very long cooldowns fail this scan.
                if (wait > TimeSpan.FromMinutes(2)) throw new HttpRequestException(null, null, response.StatusCode);
                await delay(wait < TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : wait, ct).ConfigureAwait(false);
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new HttpRequestException(null, null, response.StatusCode);
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonNode.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false) as JsonObject ?? throw new InvalidDataException();
        }
    }

    private static void ValidateSegment(string value)
    {
        if (string.IsNullOrEmpty(value) || value is "." or ".." || value.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.')))
            throw new ArgumentException("Invalid Docker namespace or repository name.");
    }

    private string CachePath(string kind, string key) => Path.Combine(cacheDirectory,
        kind + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json");

    private static bool ValidRepositoryCache(JsonObject? value, string repository)
    {
        try
        {
            if (value?["complete"]?.GetValue<bool>() != true || value["repository"]?.GetValue<string>() != repository ||
                value["tags"] is not JsonArray tags || value["count"]?.GetValue<int>() != tags.Count) return false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            return tags.All(t => t is JsonObject && t["name"] is JsonValue n && n.TryGetValue<string>(out var name) &&
                !string.IsNullOrEmpty(name) && names.Add(name));
        }
        catch { return false; }
    }

    private static async Task<JsonObject?> ReadCache(string path, CancellationToken ct)
    {
        try { return JsonNode.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)) as JsonObject; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    private static async Task WriteCache(string path, JsonObject value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, value.ToJsonString(), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, true); // Same-directory rename; never truncate the previous snapshot.
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static JsonObject Error(string scope, Exception ex) => new()
    {
        ["scope"] = scope,
        ["code"] = ex is DockerAuthenticationException { StatusCode: null or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } ? "authentication-required" :
            ex is HttpRequestException h && h.StatusCode.HasValue ? "http-" + (int)h.StatusCode.Value :
            ex is OperationCanceledException ? "timeout" : ex is IOException ? "data-or-cache" : "read-failed"
        // Deliberately omit exception messages, response bodies, URLs and tokens.
    };
}
