using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace GameLibrary.Native;

// Explicit diagnostic mode. It is never run by normal startup or the offline test suite.
// Exercises the real shared backend, and restores one existing game's original category.
public static class LiveSyncProof
{
    public static async Task<int> Run(string reportPath)
    {
        var checks = new List<object>();
        var report = Path.GetFullPath(reportPath);
        var store = new LibraryStore(Path.Combine(Path.GetDirectoryName(report)!, "live-sync-state"));
        var state = new UserState();
        using var client = new SyncClient(store);
        using var website = new HttpClient { BaseAddress = new Uri(SyncClient.Production), Timeout = TimeSpan.FromSeconds(25) };
        const string id = "staroceanthedivineforce";
        string? token = Environment.GetEnvironmentVariable("GLM_PROOF_ADMIN_TOKEN");
        bool restored = false, mutationAttempted = false;
        JsonNode? original = null;
        string? failure = null;
        string receipt = Path.Combine(store.Root, "rollback.json");
        void Check(string name, bool ok)
        {
            checks.Add(new { name, passed = ok, at = DateTime.UtcNow });
            if (!ok) throw new InvalidOperationException(name);
        }
        try
        {
            if (Environment.GetEnvironmentVariable("GLM_ENABLE_LIVE_SYNC_PROOF") != "1" || string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Explicit live-proof authorization and admin token are required.");
            if (File.Exists(receipt)) throw new InvalidOperationException("A previous live proof needs reconciliation from rollback.json before another mutation.");
            await client.Refresh(state, false, null);
            Check("Packaged native client reads production Netlify config", client.Online && client.Remote["gameCategories"]?[id] != null);
            original = client.Remote["gameCategories"]![id]?.DeepClone();
            var saved = new JsonObject { ["id"] = id, ["original"] = original?.DeepClone(), ["at"] = DateTime.UtcNow.ToString("O"), ["backend"] = SyncClient.Production };
            LibraryStore.AtomicWrite(receipt, saved.ToJsonString());
            var nativeValue = DataJson.Text(original) == "rpg" ? "storydriven" : "rpg";
            var websiteValue = nativeValue == "storydriven" ? "rpg" : "storydriven";
            client.Queue(state, "gameCategories", id, JsonValue.Create(nativeValue)); mutationAttempted = true;
            await client.Refresh(state, false, token);
            Check("Native edit is acknowledged with an empty durable queue", client.Online && state.Pending.Count == 0);
            var webRead = JsonNode.Parse(await website.GetStringAsync("api/admin-config?t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))!.AsObject();
            Check("Independent website API client reads the actual native change", DataJson.Text(webRead["config"]?["gameCategories"]?[id]) == nativeValue);
            var payload = (JsonObject)webRead["config"]!.DeepClone(); payload["gameCategories"]![id] = websiteValue;
            payload["expectedVersion"] = DataJson.Text(webRead["configVersion"]);
            using (var write = new HttpRequestMessage(HttpMethod.Post, "api/admin-config"))
            {
                write.Headers.Add("X-Admin-Token", token); write.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
                using var response = await website.SendAsync(write); response.EnsureSuccessStatusCode();
                Check("Website persistence API acknowledges its own edit", JsonNode.Parse(await response.Content.ReadAsStringAsync())?["success"]?.GetValue<bool>() == true);
            }
            await client.Refresh(state, false, null);
            Check("Native client reads the independent website API change", DataJson.Text(client.Remote["gameCategories"]?[id]) == websiteValue);
            using var restarted = new SyncClient(new LibraryStore(store.Root));
            Check("Native restart preserves the synchronized value offline", DataJson.Text(restarted.Remote["gameCategories"]?[id]) == websiteValue);
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            if (mutationAttempted && token != null)
            {
                try
                {
                    state.Pending.Clear(); await client.Refresh(state, false, null);
                    if (!client.Online) throw new InvalidOperationException("Cannot read production for restoration; rollback receipt retained.");
                    string current = DataJson.Text(client.Remote["gameCategories"]?[id]);
                    if (current != "rpg" && current != "storydriven" && !JsonNode.DeepEquals(client.Remote["gameCategories"]?[id], original)) throw new InvalidOperationException("Another client changed the test game. Automatic restoration stopped; rollback receipt retained.");
                    client.Queue(state, "gameCategories", id, original); await client.Refresh(state, false, token);
                    restored = client.Online && state.Pending.Count == 0 && JsonNode.DeepEquals(client.Remote["gameCategories"]?[id], original);
                    Check("Original game category restored and read back from production", restored);
                    if (restored) File.Move(receipt, Path.Combine(store.Root, "rollback-restored-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".json"));
                }
                catch (Exception ex) { failure = (failure ?? "") + "\nRESTORE: " + ex; }
            }
            LibraryStore.AtomicWrite(report, DataJson.Write(new { at = DateTime.UtcNow, executable = Environment.ProcessPath, targetGame = id, level = "Real production persistence API; browser UI not exercised by this diagnostic", passed = failure == null && restored, restored, conditionalWritesAdvertised = client.SupportsConditionalWrites, error = failure, checks }));
        }
        return failure == null && restored ? 0 : 1;
    }
}
