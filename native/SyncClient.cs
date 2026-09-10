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
    private readonly LibraryStore store;
    private readonly SemaphoreSlim gate = new(1, 1);
    public JsonObject Remote { get; private set; }
    public string Version { get; private set; } = "";
    public bool Online { get; private set; }
    public string Status { get; private set; } = "Offline catalog ready";
    public DateTime? LastSync { get; private set; }
    public bool SupportsConditionalWrites { get; private set; }
    public string? CatalogNotice { get; private set; }
    public SyncClient(LibraryStore store, HttpMessageHandler? handler = null)
    {
        this.store = store;
        http = handler == null ? new HttpClient() : new HttpClient(handler);
        http.BaseAddress = new Uri(Production);
        http.Timeout = TimeSpan.FromSeconds(20);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GameLibraryNative/1.0");
        Remote = store.ReadConfig();
    }
    public JsonObject Effective(UserState state)
    {
        var config = (JsonObject)Remote.DeepClone();
        foreach (var edit in state.Pending) Merge.Set(config, edit);
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
        response.EnsureSuccessStatusCode();
        var result = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation))?.AsObject() ?? throw new FormatException("The server returned empty configuration.");
        if (result["success"]?.GetValue<bool>() != true || result["config"] is not JsonObject config || config["gameCategories"] is not JsonObject || config["hiddenTabs"] is not JsonArray)
            throw new FormatException("The server returned an invalid configuration; your last catalog was preserved.");
        SupportsConditionalWrites = result["capabilities"]?["conditionalWrites"]?.GetValue<bool>() == true;
        return ((JsonObject)config.DeepClone(), DataJson.Text(result["configVersion"]));
    }
    public async Task Refresh(UserState state, bool catalog, string? adminToken, CancellationToken cancellation = default)
    {
        if (!await gate.WaitAsync(0, cancellation)) return;
        try
        {
            var remote = await ReadRemote(cancellation);
            Remote = remote.config; Version = remote.version;
            Merge.Apply(Remote, state.Pending);
            Online = true; LastSync = DateTime.Now;
            store.CacheData("admin-config.json", Remote.ToJsonString());
            if (state.Pending.Count > 0 && adminToken != null)
                await Push(state, adminToken, cancellation);
            if (catalog)
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
                        if (games.Count == 0 || games.Any(g => string.IsNullOrEmpty(g.Id)) || games.Select(g => g.Id).Distinct(StringComparer.Ordinal).Count() != games.Count)
                            throw new FormatException("The catalog contains empty or duplicate identities.");
                    }
                    return (file, json);
                }));
                foreach (var (file, json) in downloads) store.CacheData(file, json);
                string path = "api/docker-tags?user=" + Uri.EscapeDataString(state.Settings.DockerUsername) + "&repo=" + Uri.EscapeDataString(state.Settings.RepoName);
                var tagsJson = await http.GetStringAsync(path, cancellation);
                var tags = JsonNode.Parse(tagsJson)!;
                if (!HasUsableTags(tags)) throw new FormatException("Docker Hub sync returned an invalid response.");
                bool degraded = tags["degraded"]?.GetValue<bool>() == true;
                if (degraded)
                {
                    try
                    {
                        tags = await ReadDirectDockerTags(state.Settings, cancellation);
                        tagsJson = tags.ToJsonString(); degraded = false;
                        CatalogNotice = "Docker Hub direct · " + tags["count"] + " verified tags" + (tags["checkedAt"] == null ? "" : " · recent full snapshot checked");
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException or System.Text.Json.JsonException)
                    { store.Log("Direct Docker Hub fallback unavailable: " + ex.Message); }
                }
                // A fallback response must never replace a previously complete tag snapshot.
                if (!degraded || !File.Exists(Path.Combine(store.Cache, "docker-tags.json"))) store.CacheData("docker-tags.json", tagsJson);
                CatalogNotice = degraded ? "Docker tags cached: " + DataJson.Text(tags["error"], "server fallback") : DataJson.Text(tags["source"]) == "docker-hub-direct" ? CatalogNotice : null;
                Status = degraded ? "Catalog updated · Docker Hub unavailable; cached tags retained (" + DataJson.Text(tags["error"], "server fallback") + ")" : "Catalog and Docker tags updated";
            }
            else Status = "Connected · " + LastSync.Value.ToString("HH:mm:ss") + (CatalogNotice == null ? "" : " · " + CatalogNotice);
            if (state.Pending.Any(e => e.Conflict != null)) Status = "Conflict needs review · local edits are safe";
            else if (state.Pending.Count > 0) Status = $"{state.Pending.Count} changes queued · sign in to publish";
            store.Save(state);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or FormatException or InvalidOperationException)
        {
            Online = false;
            Status = "Offline / sync failed · " + ex.Message;
            store.Log("Sync did not complete: " + ex.GetType().Name + ": " + ex.Message);
            store.Save(state);
        }
        finally { gate.Release(); }
    }
    public static bool HasUsableTags(JsonNode tags) => tags["tags"] is JsonArray { Count: > 0 } && (tags["success"]?.GetValue<bool>() == true || tags["degraded"]?.GetValue<bool>() == true);
    private static JsonArray TagProjection(JsonArray tags) => new(tags.Select(tag => (JsonNode?)new JsonObject
    {
        ["name"] = tag?["name"]?.DeepClone(), ["full_size"] = tag?["full_size"]?.DeepClone(), ["last_updated"] = tag?["last_updated"]?.DeepClone()
    }).ToArray());
    internal static bool CanReuseDockerSnapshot(JsonObject cached, JsonObject firstPage, string repository, DateTime now)
    {
        if (DataJson.Text(cached["source"]) != "docker-hub-direct" || DataJson.Text(cached["repository"]) != repository ||
            !DateTime.TryParse(DataJson.Text(cached["fetchedAt"]), out var fetched)) return false;
        var age = now - fetched.ToUniversalTime();
        if (age < TimeSpan.Zero || age >= TimeSpan.FromMinutes(15) ||
            cached["tags"] is not JsonArray all || firstPage["results"] is not JsonArray first || first.Count == 0 ||
            all.Count != DataJson.Number(firstPage["count"]) || all.Count != DataJson.Number(cached["count"]) ||
            all.Select(t => DataJson.Text(t?["name"])).Distinct(StringComparer.Ordinal).Count() != all.Count) return false;
        var original = new JsonArray(all.Take(first.Count).Select(t => t?.DeepClone()).ToArray());
        return JsonNode.DeepEquals(original, TagProjection(first));
    }
    private async Task<JsonObject> ReadDirectDockerTags(Preferences settings, CancellationToken cancellation)
    {
        DockerScripts.Validate(settings, Array.Empty<Game>());
        using var hub = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        hub.DefaultRequestHeaders.UserAgent.ParseAdd("GameLibraryNative/1.0");
        bool attemptedLogin = false;
        string first = "https://hub.docker.com/v2/repositories/" + settings.DockerUsername + "/" + settings.RepoName + "/tags?page_size=100";
        var all = new JsonArray(); var identities = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        string? next = first; int expected = -1, pages = 0; string initialSignature = "";
        while (next != null)
        {
            if (++pages > 1000 || !Uri.TryCreate(next, UriKind.Absolute, out var pageUri) || pageUri.Scheme != "https" || pageUri.Host != "hub.docker.com" || !pageUri.AbsolutePath.StartsWith("/v2/repositories/" + settings.DockerUsername + "/" + settings.RepoName + "/tags")) throw new FormatException("Invalid Docker Hub pagination response.");
            using var pageResponse = await hub.GetAsync(pageUri, cancellation);
            string pageBody = await pageResponse.Content.ReadAsStringAsync(cancellation);
            if ((int)pageResponse.StatusCode == 403 && pageBody.Contains("pagination offset too large") && !attemptedLogin)
            {
                attemptedLogin = true;
                hub.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await DockerHubAccess.GetToken(cancellation));
                pages--; continue;
            }
            if (!pageResponse.IsSuccessStatusCode) throw new HttpRequestException($"Docker Hub page {pages}: HTTP {(int)pageResponse.StatusCode}; " + pageBody[..Math.Min(pageBody.Length, 200)]);
            var page = JsonNode.Parse(pageBody)!.AsObject();
            int count = page["count"]?.GetValue<int>() ?? -1;
            if (page["results"] is not JsonArray results) throw new FormatException("Invalid Docker Hub tags page.");
            if (expected < 0)
            {
                expected = count; initialSignature = TagProjection(results).ToJsonString();
                try
                {
                    var cached = JsonNode.Parse(File.ReadAllText(Path.Combine(store.Cache, "docker-tags.json")))!.AsObject();
                    if (CanReuseDockerSnapshot(cached, page, settings.DockerUsername + "/" + settings.RepoName, DateTime.UtcNow))
                    {
                        cached["checkedAt"] = DateTime.UtcNow.ToString("O");
                        store.Log("Docker Hub unchanged first page and count; reusing recent complete snapshot of " + count + " tags.");
                        return cached;
                    }
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException) { }
            }
            if (count != expected) throw new FormatException("Docker Hub changed while paging; cache preserved. Refresh again.");
            foreach (var tag in results)
            {
                string name = DataJson.Text(tag?["name"]);
                if (!DockerScripts.ValidTag(name) || !identities.Add(name)) throw new FormatException("Docker Hub returned invalid or duplicate identities; cache preserved.");
                all.Add(new JsonObject { ["name"] = name, ["full_size"] = tag?["full_size"]?.DeepClone(), ["last_updated"] = tag?["last_updated"]?.DeepClone() });
            }
            next = DataJson.Text(page["next"]); if (next.Length == 0) next = null;
        }
        var final = JsonNode.Parse(await hub.GetStringAsync(first, cancellation))!;
        if (all.Count != expected || final["count"]?.GetValue<int>() != expected || final["results"] is not JsonArray finalPage || TagProjection(finalPage).ToJsonString() != initialSignature) throw new FormatException("Docker Hub changed during the full snapshot; cache preserved. Refresh again.");
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
    public void Dispose() { http.Dispose(); }
}
