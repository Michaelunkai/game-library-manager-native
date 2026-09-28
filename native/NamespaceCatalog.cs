using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace GameLibrary.Native;

public partial class MainWindow
{
    private bool namespaceRefreshing;
    private DateTime namespaceNextRefresh;
    private string namespaceStatus = "Repository namespace not checked";
    private string namespaceOwner = "";

    internal static string NamespaceFailureStatus(JsonObject snapshot)
    {
        var errors = (snapshot["errors"] as JsonArray ?? new()).OfType<JsonObject>().ToArray();
        if (errors.Any(error => DataJson.Text(error["code"]) is "authentication-required" or "http-401"))
            return "Docker sign-in required; previous catalog retained";
        return "Docker namespace incomplete; previous catalog retained" +
            (errors.Length == 0 ? "" : " (" + errors.Length + " failed checks; see Sync details)");
    }

    private async Task RefreshNamespace(bool force)
    {
        if (offline || closing || namespaceRefreshing) return;
        string owner = State.Settings.DockerUsername;
        if (namespaceOwner == owner && !force && DateTime.UtcNow < namespaceNextRefresh) return;
        namespaceOwner = owner;
        namespaceRefreshing = true;
        namespaceStatus = "Checking all Docker repositories…";
        try
        {
            using var transport = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
            var client = new DockerNamespaceClient(transport, DockerHubAccess.GetToken, Path.Combine(Store.Cache, "namespaces"));
            string priority = State.Settings.RepoName;
            var snapshot = await Task.Run(() => client.ReadAsync(owner, priority, lifetime.Token), lifetime.Token);
            if (closing || owner != State.Settings.DockerUsername) return;
            // Retain sanitized failed-stage evidence separately; never overwrite
            // the last complete catalog with an incomplete attempt.
            var attempt = new JsonObject();
            foreach (string field in new[] { "namespace", "complete", "status", "fetchedAt", "repositoryCount", "refreshedRepositoryCount", "cachedRepositoryCount", "errors" })
                attempt[field] = snapshot[field]?.DeepClone();
            Store.CacheData("docker-namespace-attempt.json", attempt.ToJsonString());
            if (snapshot["complete"]?.GetValue<bool>() != true)
            {
                JsonObject? ReadSnapshot(string file)
                {
                    try { return JsonNode.Parse(File.ReadAllText(Path.Combine(Store.Cache, file))) as JsonObject; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
                }
                var updates = NamespaceUpdates.Combine(owner, ReadSnapshot("docker-namespace-catalog.json"),
                    ReadSnapshot("docker-namespace-updates.json"), snapshot);
                NamespaceUpdates.Apply(new List<Game>(), updates);
                Store.CacheData("docker-namespace-updates.json", updates.ToJsonString());
                namespaceStatus = NamespaceFailureStatus(snapshot);
                if (DataJson.Number(updates["repositoryCount"]) > 0)
                    namespaceStatus += $"; {updates["repositoryCount"]} verified repository updates available";
                namespaceNextRefresh = DateTime.UtcNow.AddMinutes(5);
                Store.Log(namespaceStatus + "; errors=" + snapshot["errors"]?.ToJsonString());
                Reload();
                return;
            }
            // Validate every repository before exposing any new identities or replacing
            // the last complete namespace catalog. Source-specific caches stay separate.
            LibraryStore.MergeNamespace(new List<Game>(), snapshot);
            Store.CacheData("docker-namespace-catalog.json", snapshot.ToJsonString());
            namespaceStatus = $"Docker namespace: {snapshot["repositoryCount"]} repositories checked";
            namespaceNextRefresh = DateTime.UtcNow.AddSeconds(30);
            Reload();
        }
        catch (OperationCanceledException) when (closing || lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            namespaceStatus = "Docker namespace incomplete; previous catalog retained";
            namespaceNextRefresh = DateTime.UtcNow.AddMinutes(5);
            Store.Log(namespaceStatus + ": " + ex.Message);
        }
        finally
        {
            namespaceRefreshing = false;
            if (!closing) StatusText.Text = Sync.Status + " · " + namespaceStatus;
        }
    }
}
