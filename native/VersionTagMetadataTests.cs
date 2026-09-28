using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

internal static class VersionTagMetadataTests
{
    internal static int Run(string root)
    {
        var checks = new List<(string Name, bool Passed)>();
        const string repository = "michadockermisha/fixture-tools", tag = "2";
        string id = DockerIdentity.Create(repository, tag);
        var game = new Game { Id = id, Name = tag, Discovered = true, DockerImage = repository + ":" + tag };
        var response = new JsonObject { ["success"] = true, ["id"] = id, ["name"] = tag, ["time"] = 3.4 };
        checks.Add(("A version number matching a provider game title does not establish image identity", !MetadataClient.MatchesGame(game, response)));
        checks.Add(("Automatic metadata does not query a version-only qualified tag", !MainWindow.MetadataDue(game, new(), DateTime.UtcNow)));
        var curated = new Game { Id = "2", Name = "2" };
        response["id"] = curated.Id;
        checks.Add(("Curated numeric game titles remain supported", MetadataClient.MatchesGame(curated, response) && MainWindow.MetadataDue(curated, new(), DateTime.UtcNow)));
        var store = new LibraryStore(Path.Combine(root, "version-tag-proof"));
        var state = new UserState(); state.Ratings[id] = 4; state.Wishlist.Add(id); store.Save(state);
        string before = File.ReadAllText(store.StatePath);
        store.CacheData("games.json", "[]");
        foreach (string file in new[] { "times.json", "image-sizes.json", "dates-added.json" }) store.CacheData(file, "{}");
        store.CacheData("docker-namespace-catalog.json", new JsonObject {
            ["namespace"] = "michadockermisha", ["success"] = true, ["complete"] = true, ["repositoryCount"] = 1,
            ["repositories"] = new JsonArray(new JsonObject { ["repository"] = repository, ["complete"] = true, ["stale"] = false,
                ["count"] = 1, ["tags"] = new JsonArray(new JsonObject { ["name"] = tag, ["full_size"] = 1230000000 }) }) }.ToJsonString());
        store.CacheData("metadata.json", new JsonObject { [id] = new JsonObject { ["name"] = "2", ["matchedTitle"] = "2", ["time"] = 3.4,
            ["diskRequirementGb"] = 99, ["timeTitle"] = "2", ["timeQuery"] = "2", ["timeUrl"] = "https://howlongtobeat.com/game/103578",
            ["timeFetchedAt"] = DateTime.UtcNow.ToString("O"), ["source"] = new JsonObject { ["time"] = "howlongtobeat-live" } } }.ToJsonString());
        var loaded = store.LoadGames(state, new JsonObject { ["tabs"] = new JsonArray(new JsonObject { ["id"] = "new", ["name"] = "New" }) }).Single();
        checks.Add(("Cached numeric-title hours and disk requirements are not attributed to an unlinked image", loaded.Time == 0 && loaded.TimeVerifiedAt == default && loaded.DiskRequirementGb == 0));
        checks.Add(("An ambiguous version shows exact repository and tag while retaining download bytes", loaded.Name == repository + ":" + tag && loaded.Id == id && loaded.DockerImage == repository + ":" + tag && loaded.SizeGb == 1.23));
        checks.Add(("Identity rejection preserves personal state and original metadata for recovery", loaded.Rating == 4 && loaded.Wishlisted && File.ReadAllText(store.StatePath) == before && store.ReadMetadata()[id]?["time"]?.GetValue<double>() == 3.4));
        using var transport = new NoNetwork(); using var client = new MetadataClient(transport);
        bool rejected = false;
        try { client.Refresh(game, store, false, true, default).GetAwaiter().GetResult(); }
        catch (InvalidOperationException) { rejected = true; }
        catch (Exception) { }
        checks.Add(("Manual metadata rejects unsupported identity before any network request", rejected && transport.Attempts == 0));
        LibraryStore.AtomicWrite(Path.Combine(root, "version-tag-proof.json"), DataJson.Write(checks.Select(c => new { name = c.Name, passed = c.Passed }).ToArray()));
        if (checks.Any(c => !c.Passed)) throw new InvalidOperationException(string.Join("; ", checks.Where(c => !c.Passed).Select(c => c.Name)));
        return checks.Count;
    }
    private sealed class NoNetwork : HttpMessageHandler
    {
        internal int Attempts;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Attempts++; return Task.FromException<HttpResponseMessage>(new IOException("Fixture network blocked")); }
    }
}
