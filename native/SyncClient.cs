using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

internal sealed class SharedQuotaException : HttpRequestException
{
    internal SharedQuotaException() : base("Shared service quota exceeded.", null, System.Net.HttpStatusCode.ServiceUnavailable) { }
}

internal sealed class OfflineNetworkGuard : HttpMessageHandler
{
    public int Attempts { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Attempts++;
        throw new InvalidOperationException("Offline synchronization cannot access the network.");
    }
}

public sealed class SyncClient : IDisposable
{
    public const string Production = "https://game-library-michaelunkai.netlify.app/";
    private readonly HttpClient http;
    private readonly HttpClient dockerHttp;
    private readonly HttpClient configHttp;
    private bool usingConfigFallback;
    private DateTimeOffset? primaryReadAt, fallbackReadAt;
    internal TimeSpan SharedReadInterval { get; set; } = TimeSpan.FromSeconds(60);
    private readonly Func<CancellationToken, Task<string>> dockerTokenProvider;
    private readonly bool transportOffline;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly SourceBackoff sharedBackoff = new();
    private readonly SourceBackoff catalogBackoff = new();
    private readonly SourceBackoff proxyBackoff = new();
    private string? proxyRepository;
    // Only failed website sources are delayed. Docker Hub has a separate transport
    // and must remain refreshable while the website is unavailable.
    private sealed class SourceBackoff
    {
        private int failures;
        private DateTimeOffset retryAt;
        public string Failure { get; private set; } = "";
        public bool Waiting(DateTimeOffset now, bool force) => !force && failures > 0 && now < retryAt;
        public string Notice(DateTimeOffset now) => Failure + "; retry in " + Math.Max(0, Math.Ceiling((retryAt - now).TotalSeconds)) + "s";
        public void Failed(DateTimeOffset now, Exception ex)
        {
            failures = Math.Min(failures + 1, 6);
            retryAt = now.AddSeconds(Math.Min(300, 10 * (1 << (failures - 1))));
            Failure = FailureSummary(ex);
        }
        public void Reset() { failures = 0; retryAt = default; Failure = ""; }
    }
    private readonly LibraryStore store;
    private readonly SemaphoreSlim gate = new(1, 1);
    public JsonObject Remote { get; private set; }
    public string Version { get; private set; } = "";
    // Shared configuration connectivity; catalog/Docker availability is reported separately.
    public bool Online { get; private set; }
    public string Status { get; private set; } = "Offline catalog ready";
    public DateTime? LastSync { get; private set; }
    public bool SupportsConditionalWrites { get; private set; }
    public string? CatalogNotice { get; private set; }
    public string SharedStatus { get; private set; } = "Shared configuration not checked";
    public string CatalogStatus { get; private set; } = "Static catalog not checked";
    public string DockerStatus { get; private set; } = "Docker tags not checked";
    public SyncClient(LibraryStore store, HttpMessageHandler? handler = null,
        HttpMessageHandler? dockerHandler = null, Func<CancellationToken, Task<string>>? dockerTokenProvider = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.store = store;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        http = handler == null ? new HttpClient() : new HttpClient(handler);
        http.BaseAddress = new Uri(Production);
        http.Timeout = TimeSpan.FromSeconds(20);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GameLibraryNative/1.0");
        // An injected transport covers every request unless a separate Docker transport
        // is supplied. Offline/tests must never fall through to real network or credentials.
        dockerHttp = new HttpClient(dockerHandler ?? handler ?? new HttpClientHandler { AllowAutoRedirect = false },
            disposeHandler: dockerHandler != null && !ReferenceEquals(dockerHandler, handler) || handler == null);
        dockerHttp.Timeout = TimeSpan.FromSeconds(20);
        dockerHttp.DefaultRequestHeaders.UserAgent.ParseAdd("GameLibraryNative/1.0");
        // Never share default headers, cookies, authentication, or redirects with the website.
        configHttp = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false }, disposeHandler: handler == null);
        configHttp.Timeout = TimeSpan.FromSeconds(20);
        configHttp.DefaultRequestHeaders.UserAgent.ParseAdd("GameLibraryNative/1.0");
        this.dockerTokenProvider = dockerTokenProvider ?? (handler == null && dockerHandler == null
            ? DockerHubAccess.GetToken
            : _ => Task.FromException<string>(new HttpRequestException("No fixture Docker authentication provider was supplied.")));
        transportOffline = handler is OfflineNetworkGuard;
        Remote = store.ReadConfig();
    }
    public JsonObject Effective(UserState state)
    {
        var config = (JsonObject)Remote.DeepClone();
        foreach (var edit in state.Pending) Merge.Set(config, edit);
        LocalCatalogEdits.Apply(config, state.LocalCatalog);
        return config;
    }
    public void ReloadCache() => Remote = store.ReadConfig();
    internal async Task ImportStateAsync(Func<UserState> currentState, Func<UserState, UserState> prepareImport,
        Action<UserState> applyImport, CancellationToken cancellation = default)
    {
        // A refresh owns its UserState reference across HTTP awaits. Wait for its final save
        // before replacing that reference, and prepare website-settings merges only now.
        await gate.WaitAsync(cancellation);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var current = currentState();
            var imported = LibraryStore.ValidateState(prepareImport(current));
            DockerScripts.Validate(imported.Settings, Array.Empty<Game>());
            if (current.Pending.Count > 0 && !JsonNode.DeepEquals(
                JsonNode.Parse(DataJson.Write(current.Pending)), JsonNode.Parse(DataJson.Write(imported.Pending))))
                throw new InvalidOperationException("Resolve or export your queued shared changes before replacing this library.");
            if (current.PendingGameBackups.Count > 0 && !JsonNode.DeepEquals(
                JsonNode.Parse(DataJson.Write(current.PendingGameBackups)), JsonNode.Parse(DataJson.Write(imported.PendingGameBackups))))
                throw new InvalidOperationException("Resolve or preserve your queued game backups before replacing this library.");
            if (File.Exists(store.StatePath))
                File.Copy(store.StatePath, store.StatePath + ".before-import-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8]);
            store.Save(imported);
            applyImport(imported);
        }
        finally { gate.Release(); }
    }
    public void Queue(UserState state, string section, string key, JsonNode? value)
    {
        var edit = state.Pending.FirstOrDefault(e => e.Section == section && e.Key == key);
        if (edit == null)
        {
            edit = new PendingEdit { Section = section, Key = key };
            edit.Before = Merge.Get(Remote, edit)?.DeepClone();
            state.Pending.Add(edit);
        }
        edit.After = value?.DeepClone(); edit.Conflict = null;
        if (JsonNode.DeepEquals(edit.Before, edit.After)) state.Pending.Remove(edit);
        store.Save(state);
    }
    private async Task<(JsonObject config, string version)> ReadRemote(CancellationToken cancellation)
    {
        using var response = await http.GetAsync("api/admin-config?t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), cancellation);
        await EnsureSharedResponse(response, cancellation);
        var result = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation))?.AsObject() ?? throw new FormatException("The server returned empty configuration.");
        if (result["success"]?.GetValue<bool>() != true || result["config"] is not JsonObject config || config["gameCategories"] is not JsonObject || config["hiddenTabs"] is not JsonArray)
            throw new FormatException("The server returned an invalid configuration; your last catalog was preserved.");
        SupportsConditionalWrites = result["capabilities"]?["conditionalWrites"]?.GetValue<bool>() == true;
        return ((JsonObject)config.DeepClone(), DataJson.Text(result["configVersion"]));
    }
    private async Task<JsonObject> ReadConfigFallback(CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://raw.githubusercontent.com/Michaelunkai/game-library-manager-web/main/data/admin-config.json");
        using var response = await configHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        const int maximum = 2 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maximum) throw new FormatException("GitHub configuration is too large.");
        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var body = new MemoryStream();
        var buffer = new byte[16384]; int count;
        while ((count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, maximum + 1 - body.Length)), token)) > 0)
        {
            body.Write(buffer, 0, count);
            if (body.Length > maximum) throw new FormatException("GitHub configuration is too large.");
        }
        var config = JsonNode.Parse(body.ToArray()) as JsonObject;
        static bool TextValue(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text);
        if (config == null || config["gameCategories"] is not JsonObject categories || categories.Any(p => string.IsNullOrWhiteSpace(p.Key) || !TextValue(p.Value)) ||
            config["hiddenTabs"] is not JsonArray hidden || hidden.Any(t => !TextValue(t)) ||
            config["tabs"] is not JsonArray tabs || tabs.Any(t => t is not JsonObject || !TextValue(t["id"]) || !TextValue(t["name"])) ||
            tabs.Select(t => DataJson.Text(t!["id"])).Distinct(StringComparer.Ordinal).Count() != tabs.Count)
            throw new FormatException("Invalid GitHub configuration; previous cache retained.");
        token.ThrowIfCancellationRequested();
        return config;
    }
    internal static async Task EnsureSharedResponse(HttpResponseMessage response, CancellationToken cancellation)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable && (response.Content.Headers.ContentLength == null || response.Content.Headers.ContentLength <= 65536))
        {
            bool quota = false;
            try
            {
                using var stream = await response.Content.ReadAsStreamAsync(cancellation);
                var bytes = new byte[65537]; int length = 0, read;
                while (length < bytes.Length && (read = await stream.ReadAsync(bytes.AsMemory(length), cancellation)) > 0) length += read;
                if (length <= 65536) quota = DataJson.Text((JsonNode.Parse(Encoding.UTF8.GetString(bytes, 0, length)) as JsonObject)?["error"]) == "usage_exceeded";
            }
            catch (System.Text.Json.JsonException) { }
            if (quota) throw new SharedQuotaException();
        }
        response.EnsureSuccessStatusCode();
    }
    public async Task Refresh(UserState state, bool catalog, string? adminToken, CancellationToken cancellation = default, bool force = false)
    {
        if (!await gate.WaitAsync(0, cancellation)) return;
        string activeSource = "shared";
        try
        {
            bool freshPrimary = Online && primaryReadAt.HasValue && utcNow() - primaryReadAt.Value < SharedReadInterval &&
                !force && !(state.Pending.Count > 0 && adminToken != null && state.Pending.All(edit => edit.Conflict == null));
            if (!freshPrimary) { SharedStatus = "Shared configuration checking"; Online = false; }
            if (catalog)
            {
                CatalogStatus = "Static catalog not checked this refresh";
                DockerStatus = "Docker tags not checked this refresh";
            }
            cancellation.ThrowIfCancellationRequested();
            bool initialReadFailed = true;
            if (freshPrimary)
                SharedStatus = state.Pending.Count == 0 ? "Shared configuration synchronized" : "Shared configuration read";
            else if (sharedBackoff.Waiting(utcNow(), force))
                SharedStatus = "Shared sync unavailable (" + sharedBackoff.Notice(utcNow()) + "); " + (usingConfigFallback ? "GitHub configuration cached, read-only; " : "") + "local edits retained";
            else try
            {
                var remote = await ReadRemote(cancellation);
                initialReadFailed = false;
                cancellation.ThrowIfCancellationRequested();
                store.CacheData("admin-config.json", remote.config.ToJsonString());
                Remote = remote.config; Version = remote.version;
                usingConfigFallback = false;
                Merge.Apply(Remote, state.Pending);
                if (state.Pending.Count > 0 && adminToken != null)
                    await Push(state, adminToken, cancellation);
                cancellation.ThrowIfCancellationRequested();
                sharedBackoff.Reset();
                Online = true; LastSync = utcNow().LocalDateTime;
                primaryReadAt = utcNow();
                SharedStatus = state.Pending.Count == 0 ? "Shared configuration synchronized" : "Shared configuration read";
            }
            catch (Exception ex) when (SourceFailure(ex, cancellation))
            {
                SupportsConditionalWrites = false;
                sharedBackoff.Failed(utcNow(), ex);
                SharedStatus = "Shared sync unavailable (" + sharedBackoff.Notice(utcNow()) + "); local edits retained";
                if (initialReadFailed && !transportOffline && usingConfigFallback && fallbackReadAt.HasValue &&
                    utcNow() - fallbackReadAt.Value < TimeSpan.FromSeconds(60) && !force)
                    SharedStatus += " · GitHub configuration cached, read-only; shared writes unavailable";
                else if (initialReadFailed && !transportOffline)
                {
                    try
                    {
                        var fallback = await ReadConfigFallback(cancellation);
                        cancellation.ThrowIfCancellationRequested();
                        store.CacheData("admin-config.json", fallback.ToJsonString());
                        Remote = fallback; Version = ""; usingConfigFallback = true;
                        fallbackReadAt = utcNow();
                        SharedStatus += " · GitHub configuration loaded, read-only; shared writes unavailable";
                    }
                    catch (Exception fallbackError) when (SourceFailure(fallbackError, cancellation))
                    {
                        SharedStatus += " · GitHub read unavailable (" + FailureSummary(fallbackError) + "); previous cache retained";
                    }
                }
                store.Log(SharedStatus);
            }
            if (catalog && !transportOffline)
            {
                activeSource = "catalog";
                cancellation.ThrowIfCancellationRequested();
                CatalogStatus = "Static catalog checking";
                if (catalogBackoff.Waiting(utcNow(), force))
                    CatalogStatus = "Static catalog unavailable (" + catalogBackoff.Notice(utcNow()) + "); using local catalog";
                else try
                {
                    await ReadCatalog(cancellation);
                    catalogBackoff.Reset();
                    CatalogStatus = "Static catalog updated";
                }
                catch (Exception ex) when (SourceFailure(ex, cancellation))
                {
                    catalogBackoff.Failed(utcNow(), ex);
                    CatalogStatus = "Static catalog unavailable (" + catalogBackoff.Notice(utcNow()) + "); using local catalog";
                    store.Log(CatalogStatus);
                }
                activeSource = "docker";
                cancellation.ThrowIfCancellationRequested();
                DockerStatus = "Docker tags checking";
                await RefreshDockerTags(state.Settings, cancellation, force);
            }
            Status = RefreshStatus(state, catalog);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (activeSource == "shared") { SharedStatus = "Shared sync cancelled; local edits retained"; SupportsConditionalWrites = false; }
            else if (activeSource == "catalog") CatalogStatus = "Static catalog refresh cancelled";
            else { DockerStatus = "Docker refresh cancelled; previous cache retained"; CatalogNotice = DockerStatus; }
            Status = RefreshStatus(state, catalog) + " · Refresh cancelled";
            throw;
        }
        finally
        {
            // Push may have reconciled an interrupted acknowledgment before a later
            // source fails/cancels. Persist that queue even when refresh does not finish.
            try { store.Save(state); }
            finally { gate.Release(); }
        }
    }
    private string RefreshStatus(UserState state, bool catalog)
    {
        string status = SharedStatus + (catalog ? " · " + CatalogStatus + " · " + DockerStatus : "");
        if (state.Pending.Any(e => e.Conflict != null)) status += " · Conflict needs review; local edits are safe";
        else if (state.Pending.Count > 0) status += $" · {state.Pending.Count} changes queued";
        return status;
    }
    private static bool SourceFailure(Exception ex, CancellationToken cancellation) => !cancellation.IsCancellationRequested &&
        ex is HttpRequestException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException or FormatException or InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception;
    internal static string FailureSummary(Exception ex) => ex switch
    {
        DockerAuthenticationException { StatusCode: null or System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden } => "Docker sign-in required",
        SharedQuotaException => "HTTP 503: shared service quota exceeded",
        HttpRequestException { StatusCode: { } code } => "HTTP " + (int)code,
        HttpRequestException => "network request failed",
        OperationCanceledException or TimeoutException => "request timed out",
        IOException or UnauthorizedAccessException => "local storage unavailable",
        System.ComponentModel.Win32Exception => "Docker sign-in helper unavailable",
        _ => "invalid response or settings"
    };
    private async Task ReadCatalog(CancellationToken cancellation)
    {
        var files = new[] { "games.json", "tabs.json", "times.json", "image-sizes.json", "dates-added.json" };
        var downloads = await Task.WhenAll(files.Select(async file =>
        {
            var json = await http.GetStringAsync("data/" + file + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), cancellation);
            var node = JsonNode.Parse(json);
            if (file is "games.json" or "tabs.json" ? node is not JsonArray : node is not JsonObject) throw new FormatException("Invalid catalog response: " + file);
            if (file == "games.json")
            {
                var games = DataJson.Read<List<Game>>(json);
                if (games.Count == 0 || games.Any(g => g == null || string.IsNullOrEmpty(g.Id)) || games.Select(g => g.Id).Distinct(StringComparer.Ordinal).Count() != games.Count)
                    throw new FormatException("The catalog contains empty or duplicate identities.");
            }
            return (file, json);
        }));
        // Do not replace any catalog file until all responses are validated.
        cancellation.ThrowIfCancellationRequested();
        foreach (var (file, json) in downloads) store.CacheData(file, json);
    }
    private async Task RefreshDockerTags(Preferences settings, CancellationToken cancellation, bool force)
    {
        string repository = settings.DockerUsername + "/" + settings.RepoName;
        if (proxyRepository != repository) { proxyBackoff.Reset(); proxyRepository = repository; }
        string proxyFailure;
        if (proxyBackoff.Waiting(utcNow(), force)) proxyFailure = proxyBackoff.Notice(utcNow());
        else try
        {
            string path = "api/docker-tags?user=" + Uri.EscapeDataString(settings.DockerUsername) + "&repo=" + Uri.EscapeDataString(settings.RepoName);
            var tags = JsonNode.Parse(await http.GetStringAsync(path, cancellation));
            if (!CompleteTagSnapshot(tags, repository)) throw new FormatException("Proxy snapshot is degraded, incomplete, or unscoped.");
            cancellation.ThrowIfCancellationRequested();
            store.CacheData("docker-tags.json", tags!.ToJsonString());
            proxyBackoff.Reset();
            DockerStatus = "Docker proxy updated · " + tags["count"] + " tags";
            CatalogNotice = null;
            return;
        }
        catch (Exception ex) when (SourceFailure(ex, cancellation))
        {
            proxyBackoff.Failed(utcNow(), ex);
            proxyFailure = proxyBackoff.Notice(utcNow());
            store.Log("Docker proxy unavailable (" + proxyFailure + "); trying Docker Hub directly.");
        }
        cancellation.ThrowIfCancellationRequested();
        try
        {
            var tags = await ReadDirectDockerTags(settings, cancellation);
            if (!CompleteTagSnapshot(tags, repository)) throw new FormatException("Incomplete direct snapshot.");
            cancellation.ThrowIfCancellationRequested();
            store.CacheData("docker-tags.json", tags.ToJsonString());
            DockerStatus = "Docker Hub direct · " + tags["count"] + " verified tags" +
                (tags["checkedAt"] == null ? "" : " · recent snapshot rechecked") + " (proxy unavailable: " + proxyFailure + ")";
        }
        catch (Exception ex) when (SourceFailure(ex, cancellation))
        {
            DockerStatus = "Docker unavailable (proxy: " + proxyFailure + "; direct: " + FailureSummary(ex) + "); previous cache retained";
            store.Log(DockerStatus);
        }
        CatalogNotice = DockerStatus;
    }
    private static bool True(JsonNode? value) => value is JsonValue v && v.TryGetValue<bool>(out var flag) && flag;
    private static bool FalseOrMissing(JsonNode? value) => value == null || value is JsonValue v && v.TryGetValue<bool>(out var flag) && !flag;
    private static int Count(JsonNode? value) => value is JsonValue v && v.TryGetValue<int>(out var count) ? count : -1;
    public static bool HasUsableTags(JsonNode tags)
    {
        if (tags is not JsonObject obj || obj["tags"] is not JsonArray all) return false;
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in all)
        {
            string name = tag is JsonObject item ? DataJson.Text(item["name"]) : DataJson.Text(tag);
            if (!DockerScripts.ValidTag(name) || !identities.Add(name)) return false;
        }
        // Legacy degraded responses remain recognizable, but never qualify for cache replacement.
        return True(obj["degraded"]) ? all.Count > 0 : True(obj["success"]) && Count(obj["count"]) == all.Count && obj["next"] == null;
    }
    internal static bool CompleteTagSnapshot(JsonNode? tags, string repository) => tags is JsonObject obj &&
        True(obj["success"]) && FalseOrMissing(obj["degraded"]) && HasUsableTags(obj) &&
        DataJson.Text(obj["repository"]) == repository &&
        (obj["fetched"] == null || Count(obj["fetched"]) == Count(obj["count"]));
    private static JsonArray TagProjection(JsonArray tags) => new(tags.Select(tag => (JsonNode?)new JsonObject
    {
        ["name"] = tag?["name"]?.DeepClone(), ["full_size"] = tag?["full_size"]?.DeepClone(), ["last_updated"] = tag?["last_updated"]?.DeepClone()
    }).ToArray());
    internal static bool CanReuseDockerSnapshot(JsonObject cached, JsonObject firstPage, string repository, DateTime now)
    {
        // Validate the stored envelope even when its count and first page still match.
        if (DataJson.Text(cached["source"]) != "docker-hub-direct" || DataJson.Text(cached["repository"]) != repository ||
            (cached["success"] != null && !True(cached["success"])) || !FalseOrMissing(cached["degraded"]) || cached["next"] != null ||
            (cached["fetched"] != null && Count(cached["fetched"]) != Count(cached["count"])) ||
            !DateTime.TryParse(DataJson.Text(cached["fetchedAt"]), out var fetched)) return false;
        var age = now - fetched.ToUniversalTime();
        if (age < TimeSpan.Zero || age >= TimeSpan.FromMinutes(15) ||
            cached["tags"] is not JsonArray all || firstPage["results"] is not JsonArray first || (first.Count == 0 && all.Count != 0) ||
            first.Any(t => t is not JsonObject) || first.Count > all.Count ||
            (first.Count < all.Count ? DataJson.Text(firstPage["next"]).Length == 0 : firstPage["next"] != null) ||
            all.Count != Count(firstPage["count"]) || all.Count != Count(cached["count"]) ||
            all.Any(t => t is not JsonObject item || !DockerScripts.ValidTag(DataJson.Text(item["name"]))) ||
            all.Select(t => DataJson.Text(t?["name"])).Distinct(StringComparer.Ordinal).Count() != all.Count) return false;
        var original = new JsonArray(all.Take(first.Count).Select(t => t?.DeepClone()).ToArray());
        return JsonNode.DeepEquals(original, TagProjection(first));
    }
    private async Task<JsonObject> ReadDirectDockerTags(Preferences settings, CancellationToken cancellation)
    {
        DockerScripts.Validate(settings, Array.Empty<Game>());
        cancellation.ThrowIfCancellationRequested();
        bool attemptedLogin = false;
        string? token = null;
        string first = "https://hub.docker.com/v2/repositories/" + settings.DockerUsername + "/" + settings.RepoName + "/tags?page_size=100";
        string tagsPath = new Uri(first).AbsolutePath.TrimEnd('/');
        var all = new JsonArray(); var identities = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? next = first; int expected = -1, pages = 0; string initialSignature = "";
        while (next != null)
        {
            cancellation.ThrowIfCancellationRequested();
            if (++pages > 1000 || !Uri.TryCreate(next, UriKind.Absolute, out var pageUri) || pageUri.Scheme != "https" || pageUri.Host != "hub.docker.com" ||
                !pageUri.IsDefaultPort || pageUri.UserInfo.Length != 0 || pageUri.Fragment.Length != 0 ||
                pageUri.AbsolutePath.TrimEnd('/') != tagsPath || !visited.Add(pageUri.AbsoluteUri)) throw new FormatException("Invalid Docker Hub pagination response.");
            using var request = new HttpRequestMessage(HttpMethod.Get, pageUri);
            if (token != null) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var pageResponse = await dockerHttp.SendAsync(request, cancellation);
            string pageBody = await pageResponse.Content.ReadAsStringAsync(cancellation);
            if ((int)pageResponse.StatusCode == 403 && pageBody.Contains("pagination offset too large") && !attemptedLogin)
            {
                attemptedLogin = true;
                token = await dockerTokenProvider(cancellation);
                if (string.IsNullOrWhiteSpace(token)) throw new HttpRequestException("Docker authentication returned no token.");
                visited.Remove(pageUri.AbsoluteUri); pages--; continue;
            }
            if (!pageResponse.IsSuccessStatusCode) throw new HttpRequestException($"Docker Hub page {pages} failed.", null, pageResponse.StatusCode);
            var page = JsonNode.Parse(pageBody) as JsonObject ?? throw new FormatException("Invalid Docker Hub page.");
            int count = Count(page["count"]);
            if (count < 0 || page["results"] is not JsonArray results || results.Any(t => t is not JsonObject)) throw new FormatException("Invalid Docker Hub tags page.");
            if (expected < 0)
            {
                expected = count; initialSignature = TagProjection(results).ToJsonString();
                try
                {
                    var cached = JsonNode.Parse(File.ReadAllText(Path.Combine(store.Cache, "docker-tags.json"))) as JsonObject;
                    if (cached != null && CanReuseDockerSnapshot(cached, page, settings.DockerUsername + "/" + settings.RepoName, DateTime.UtcNow))
                    {
                        cached["success"] = true;
                        cached["checkedAt"] = DateTime.UtcNow.ToString("O");
                        store.Log("Docker Hub unchanged first page and count; reusing recent complete snapshot of " + count + " tags.");
                        return cached;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { }
            }
            if (count != expected) throw new FormatException("Docker Hub changed while paging; cache preserved. Refresh again.");
            foreach (var tag in results)
            {
                string name = DataJson.Text(tag?["name"]);
                if (!DockerScripts.ValidTag(name) || !identities.Add(name)) throw new FormatException("Docker Hub returned invalid or duplicate identities; cache preserved.");
                all.Add(new JsonObject { ["name"] = name, ["full_size"] = tag?["full_size"]?.DeepClone(), ["last_updated"] = tag?["last_updated"]?.DeepClone() });
            }
            if (all.Count > expected) throw new FormatException("Docker Hub returned too many tags.");
            if (page["next"] != null && (page["next"] is not JsonValue nextValue || !nextValue.TryGetValue<string>(out _)))
                throw new FormatException("Invalid Docker Hub next page.");
            next = DataJson.Text(page["next"]); if (next.Length == 0) next = null;
            if (next != null && results.Count == 0) throw new FormatException("Docker Hub pagination made no progress.");
        }
        using var finalRequest = new HttpRequestMessage(HttpMethod.Get, first);
        if (token != null) finalRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var finalResponse = await dockerHttp.SendAsync(finalRequest, cancellation);
        finalResponse.EnsureSuccessStatusCode();
        var final = JsonNode.Parse(await finalResponse.Content.ReadAsStringAsync(cancellation)) as JsonObject;
        if (all.Count != expected || final == null || Count(final["count"]) != expected || final["results"] is not JsonArray finalPage ||
            finalPage.Any(t => t is not JsonObject) || TagProjection(finalPage).ToJsonString() != initialSignature) throw new FormatException("Docker Hub changed during the full snapshot; cache preserved. Refresh again.");
        store.Log($"Docker Hub direct snapshot verified: {all.Count} exact identities across {pages} pages.");
        return new JsonObject { ["success"] = true, ["degraded"] = false, ["source"] = "docker-hub-direct", ["repository"] = settings.DockerUsername + "/" + settings.RepoName, ["count"] = all.Count, ["pages"] = pages, ["fetchedAt"] = DateTime.UtcNow.ToString("O"), ["tags"] = all };
    }
    private async Task Push(UserState state, string token, CancellationToken cancellation)
    {
        // Reconcile the latest server value before retrying any previously interrupted write.
        var latest = await ReadRemote(cancellation);
        Remote = latest.config; Version = latest.version;
        state.Pending.RemoveAll(e => JsonNode.DeepEquals(Merge.Get(Remote, e), e.After));
        var pending = state.Pending.ToArray();
        var payload = Merge.Apply(Remote, pending);
        if (pending.Length == 0 || pending.Any(e => e.Conflict != null)) return;
        // Send only a snapshot; edits arriving during an HTTP await remain in the queue.
        var snapshot = pending.Select(e => new PendingEdit { Section = e.Section, Key = e.Key, Before = e.Before?.DeepClone(), After = e.After?.DeepClone() }).ToArray();
        payload["expectedVersion"] = Version;
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/admin-config");
        request.Headers.Add("X-Admin-Token", token);
        if (SupportsConditionalWrites) request.Headers.TryAddWithoutValidation("If-Match", Version);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, cancellation);
        if ((int)response.StatusCode is 409 or 412)
        {
            foreach (var edit in state.Pending) edit.Conflict = "The website changed while saving. Refresh and review this edit.";
            return;
        }
        response.EnsureSuccessStatusCode();
        var result = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation));
        if (result?["success"]?.GetValue<bool>() != true) throw new FormatException("The server did not confirm the save.");
        var confirmed = await ReadRemote(cancellation);
        Remote = confirmed.config; Version = confirmed.version;
        foreach (var saved in snapshot)
        {
            var current = state.Pending.FirstOrDefault(e => e.Section == saved.Section && e.Key == saved.Key);
            if (current == null) continue;
            if (!JsonNode.DeepEquals(Merge.Get(Remote, saved), saved.After)) { current.Conflict = "Save read-back differed from your edit. Your edit is preserved."; continue; }
            if (JsonNode.DeepEquals(current.After, saved.After)) state.Pending.Remove(current);
            else current.Before = saved.After?.DeepClone();
        }
        store.CacheData("admin-config.json", Remote.ToJsonString());
        store.Save(state);
        store.Log("Shared changes acknowledged by production and read back; remaining=" + state.Pending.Count);
    }
    public void Dispose() { configHttp.Dispose(); dockerHttp.Dispose(); http.Dispose(); }
}
