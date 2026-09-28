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

public partial class MainWindow
{
    private async Task VerifyAutomaticMetadataEntryPaths(Action<string, bool> check, string processFixture)
    {
        if (Program.TestReport == null) throw new InvalidOperationException("Metadata proof requires an isolated UI-test profile.");
        var previousState = DataJson.Read<UserState>(DataJson.Write(State));
        var previousAttempts = metadataAttempts;
        var previousListVisibility = GameList.Visibility;
        string? repairedCoverPath = null;
        byte[]? originalCoverBytes = null;
        string[] files = { "games.json", "docker-tags.json", "docker-namespace-catalog.json", "metadata-attempts.json", "metadata.json" };
        var previousFiles = files.ToDictionary(name => name, name => File.Exists(Path.Combine(Store.Cache, name)) ? File.ReadAllText(Path.Combine(Store.Cache, name)) : null);
        try
        {
            State = new UserState();
            Store.CacheData("games.json", "[{\"id\":\"future-catalog\",\"name\":\"Future Catalog\",\"category\":\"new\"},{\"id\":\"future-unavailable\",\"name\":\"Future Unavailable\",\"category\":\"new\"}]");
            JsonObject Repo(string repository, string tag) => new() { ["success"] = true, ["complete"] = true, ["stale"] = false,
                ["repository"] = repository, ["count"] = 1, ["tags"] = new JsonArray(new JsonObject { ["name"] = tag }) };
            Store.CacheData("docker-tags.json", Repo("michadockermisha/backup", "future-priority").ToJsonString());
            Store.CacheData("docker-namespace-catalog.json", new JsonObject { ["namespace"] = "michadockermisha", ["success"] = true,
                ["complete"] = true, ["repositoryCount"] = 1, ["repositories"] = new JsonArray(Repo("michadockermisha/proof", "future-namespace")) }.ToJsonString());
            metadataAttempts = new();
            Reload();
            string folder = Path.Combine(Store.Root, "metadata-local-fixture"); Directory.CreateDirectory(folder);
            string executable = Path.Combine(folder, "FutureInstalled.exe"); File.Copy(processFixture, executable, true);
            var found = InstalledScanner.Discover(folder, Array.Empty<(string, string)>(), default);
            ApplyInstalledScan(found);
            string localId = found.Games.Single().Id;
            string wandExecutable = Path.Combine(Store.Root, "future-wand.exe"); File.Copy(processFixture, wandExecutable, true);
            wandRegistrationProof = () => new[] { new WandSupportedGame("future-wand", "future", "future", "Future Wand", wandExecutable) };
            await SynchronizeWandSupportedGamesAsync();
            ResetFilters(this, new());
            var expected = new[] { "future-catalog", "future-priority", DockerIdentity.Create("michadockermisha/proof", "future-namespace"), localId, "wand:future" };
            check("Every future-entry fixture reaches automatic metadata eligibility", expected.All(id => Games.Any(g => g.Id == id && MetadataDue(g, metadataAttempts, DateTime.UtcNow))));
            var fixture = new EntryMetadataFixture(File.ReadAllBytes(Directory.EnumerateFiles(Store.Assets, "*.jpg", SearchOption.AllDirectories).First()));
            automaticMetadataClientProof = () => new MetadataClient(fixture);
            // Simulate a view elsewhere in the catalog: none of these rows is visible.
            GameList.Visibility = System.Windows.Visibility.Collapsed;
            await RunAutomaticMetadata();
            GameList.Visibility = previousListVisibility;
            check("Off-screen installed additions receive metadata before older repository entries", fixture.Order.Take(2).ToHashSet().SetEquals(new[] { localId, "wand:future" }));
            check("Automatic loop enriches catalog Docker installed and Wand entries without manual refresh", expected.All(id => Games.Any(g => g.Id == id && g.Time == 12.5 && File.Exists(g.Cover))));
            var persistedAttempts = JsonNode.Parse(File.ReadAllText(Path.Combine(Store.Cache, "metadata-attempts.json")))!.AsObject();
            check("Unavailable metadata does not block other entries and persists its retry", !MetadataDue(Games.Single(g => g.Id == "future-unavailable"), persistedAttempts, DateTime.UtcNow)
                && Games.Single(g => g.Id == "future-unavailable").Time == 0 && expected.All(id => fixture.Requested.Contains(id)));
            State = Store.LoadState(); Reload();
            check("Automatic enrichment survives state and catalog reload for every entry path", expected.All(id => Games.Any(g => g.Id == id && g.Time == 12.5 && File.Exists(g.Cover))));
            // Keep durations fresh so recovery must be driven by actual artwork
            // validity, not by a missing/unsourced time field. No live data touched.
            var verifiedAt = DateTime.UtcNow;
            var metadata = Store.ReadMetadata();
            foreach (string id in expected)
            {
                metadata[id]!["source"]!["time"] = "howlongtobeat-live";
                metadata[id]!["timeUrl"] = "https://howlongtobeat.com/game/12345";
                metadata[id]!["timeTitle"] = metadata[id]!["matchedTitle"]!.DeepClone();
                metadata[id]!["timeQuery"] = metadata[id]!["matchedTitle"]!.DeepClone();
                metadata[id]!["timeSamples"] = 50;
                metadata[id]!["timeFetchedAt"] = verifiedAt.ToString("O");
                persistedAttempts[id]!["attemptedAt"] = verifiedAt.AddMinutes(-10).ToString("O");
                persistedAttempts[id]!["retryAfter"] = verifiedAt.AddMinutes(-5).ToString("O");
            }
            Store.CacheData("metadata.json", metadata.ToJsonString());
            Store.CacheData("metadata-attempts.json", persistedAttempts.ToJsonString());
            repairedCoverPath = Games.Single(g => g.Id == expected[0]).Cover;
            originalCoverBytes = File.ReadAllBytes(repairedCoverPath);
            DateTime validCoverWriteUtc = File.GetLastWriteTimeUtc(repairedCoverPath);
            File.WriteAllBytes(repairedCoverPath, new byte[originalCoverBytes.Length]);
            // An external file mutation has no explicit ArtworkFile.Replaced
            // notification. Make its changed revision deterministic instead of
            // depending on NTFS timestamp resolution under a fast UI proof.
            File.SetLastWriteTimeUtc(repairedCoverPath, validCoverWriteUtc.AddSeconds(-10));
            metadataAttempts = null; State = Store.LoadState(); Reload();
            int beforeRepair = fixture.Order.Count;
            GameList.Visibility = System.Windows.Visibility.Collapsed;
            await RunAutomaticMetadata();
            GameList.Visibility = previousListVisibility;
            check("Automatic metadata repairs a damaged shared cover after reload without manual refresh", File.ReadAllBytes(repairedCoverPath).SequenceEqual(originalCoverBytes)
                && expected.All(id => ArtworkFile.IsUsable(Games.Single(g => g.Id == id).Cover)) && fixture.Order.Count == beforeRepair + 1);
            check("Artwork-only recovery preserves previously verified completion hours and their timestamp", expected.All(id =>
                Games.Single(g => g.Id == id).Time == 12.5 && Games.Single(g => g.Id == id).TimeVerifiedAt == verifiedAt));
        }
        finally
        {
            if (repairedCoverPath != null && originalCoverBytes != null) File.WriteAllBytes(repairedCoverPath, originalCoverBytes);
            automaticMetadataClientProof = null; wandRegistrationProof = null; metadataAttempts = previousAttempts;
            GameList.Visibility = previousListVisibility;
            foreach (var file in previousFiles)
            {
                string path = Path.Combine(Store.Cache, file.Key);
                if (file.Value != null) LibraryStore.AtomicWrite(path, file.Value);
                else if (File.Exists(path)) File.Delete(path);
            }
            wandIncludedGameIds.Clear(); RestoreImportedState(previousState);
        }
    }

    private sealed class EntryMetadataFixture(byte[] image) : HttpMessageHandler
    {
        internal readonly HashSet<string> Requested = new(StringComparer.Ordinal);
        internal readonly List<string> Order = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "image.example") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(image) });
            var query = uri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2)).Where(part => part.Length == 2)
                .ToDictionary(part => part[0], part => Uri.UnescapeDataString(part[1]));
            if (!query.TryGetValue("id", out string? id)) throw new InvalidOperationException("Unexpected metadata source request.");
            Requested.Add(id);
            Order.Add(id);
            var json = id == "future-unavailable" ? new JsonObject { ["success"] = false, ["error"] = "No matching provider record." }
                : new JsonObject { ["success"] = true, ["id"] = id, ["name"] = query["name"], ["category"] = "new",
                    ["time"] = 12.5, ["image"] = "https://image.example/cover.jpg", ["source"] = new JsonObject { ["time"] = "fixture" } };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json") });
        }
    }
}
