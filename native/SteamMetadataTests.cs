using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

internal static class SteamMetadataTests
{
    internal static void Run(LibraryStore store, byte[] image)
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        VerifySuccessfulIncompleteResponseFallback(store, image);
        Check(SteamMetadata.StorageGb("<strong>Storage:</strong> 50 GB available space") == 50, "Storage label missing.");
        Check(Math.Abs(SteamMetadata.StorageGb("Storage: 1250 MB available space") - 1.25) < 0.0001
            && SteamMetadata.StorageGb("Storage: 0 GB available space") == 0,
            "Published storage units or nonpositive requirements were parsed incorrectly.");
        Check(SteamMetadata.StorageGb("Memory: 16 GB RAM") == 0, "RAM treated as disk space.");
        Check(MetadataClient.DurationQueryTitle("Nobodysavestheworld", "Nobodysavestheworld", "Nobody Saves the World", null) == "Nobody Saves the World", "Verified word boundaries were lost in duration search.");
        Check(MetadataClient.DurationQueryTitle("Nocturnal 2", "Nocturnal 2", "Nocturnal", null) == "Nocturnal 2", "Cached title replaced a sequel.");
        // Keep exercising unknown formatting: Little Kitty now has independent
        // verified search spelling and is covered by ReadableMetadataTitleTests.
        var titleCatalog = new JsonObject { ["titles"] = new JsonObject { ["1"] = new JsonObject { ["name"] = "Nobody Saves the World" } } };
        Check(MetadataClient.DurationQueryTitle("Nobodysavestheworld", "Nobodysavestheworld", "", titleCatalog) == "Nobody Saves the World", "Exact cached catalog title was not used for search.");
        titleCatalog["titles"]!["2"] = new JsonObject { ["name"] = "Nobodysaves Theworld" };
        Check(MetadataClient.DurationQueryTitle("Nobodysavestheworld", "Nobodysavestheworld", "", titleCatalog) == "Nobodysavestheworld", "Ambiguous catalog formatting was selected arbitrarily.");
        using var fixture = new Fixture(image);
        using var client = new MetadataClient(fixture);
        var game = new Game { Id = "local:steam-proof", Name = "Metadata Proof", IsLocal = true, Category = "new" };
        Check(MainWindow.MetadataDue(game, new(), DateTime.UtcNow), "Local game excluded.");
        var partialNow = DateTime.UtcNow;
        var partialAttempts = new JsonObject { [game.Id] = new JsonObject { ["available"] = true,
            ["attemptedAt"] = partialNow.ToString("O"), ["retryAfter"] = partialNow.AddDays(1).ToString("O") } };
        Check(!MainWindow.MetadataDue(game, partialAttempts, partialNow.AddMinutes(4)), "Partial result retried too early.");
        Check(MainWindow.MetadataDue(game, partialAttempts, partialNow.AddMinutes(5)), "Partial result still delayed a day.");
        client.Refresh(game, store, true, true, default).GetAwaiter().GetResult();
        var saved = store.ReadMetadata()[game.Id]!;
        Check(DataJson.Number(saved["diskRequirementGb"]) == 50, "Published disk requirement lost.");
        Check(DataJson.Number(saved["time"]) == 0, "Steam fallback invented duration.");
        Check(File.Exists(LibraryStore.SafeChild(store.Cache, DataJson.Text(saved["cover"]))), "Fallback artwork not persisted.");
        var artworkEvidence = MetadataEvidence.ReadField(store, game.Id, MetadataField.Artwork);
        var diskEvidence = MetadataEvidence.ReadField(store, game.Id, MetadataField.PublishedDiskRequirementGb);
        var downloadEvidence = MetadataEvidence.ReadField(store, game.Id, MetadataField.DownloadSizeGb);
        Check(DataJson.Text(artworkEvidence?["validation"]?["status"]) == "ValidFresh"
            && DataJson.Text(artworkEvidence?["evidence"]?["providerProductId"]) == "Steam:1"
            && DataJson.Text(artworkEvidence?["evidence"]?["canonicalProductIdentity"]) == "steam:1"
            && DataJson.Text(diskEvidence?["validation"]?["status"]) == "ValidFresh"
            && DataJson.Number(diskEvidence?["evidence"]?["numericValue"]) == 50
            && DataJson.Text(downloadEvidence?["lastAttemptValidation"]?["status"]) == "Unavailable"
            && downloadEvidence?["evidence"] == null,
            "Direct Steam evidence did not keep artwork, published storage, and download size as separate identity-matched fields.");
        string falseAddressedArtwork = LibraryStore.SafeChild(store.Cache, "covers/" + new string('0', 64) + ".img");
        File.WriteAllBytes(falseAddressedArtwork, image);
        Check(!ArtworkFile.IsUsable(falseAddressedArtwork), "Decodable bytes with a false content-addressed artwork identity were accepted.");
        Check(fixture.HostCalls == 1, "Unexpected hosted request count.");
        client.Refresh(game, store, true, false, default).GetAwaiter().GetResult();
        Check(fixture.HostCalls == 1, "Fallback bypassed hosted cooldown.");
        saved = store.ReadMetadata()[game.Id]!;
        fixture.WrongDetails = true;
        bool rejected = false;
        try { client.Refresh(game, store, true, false, default).GetAwaiter().GetResult(); }
        catch (FormatException) { rejected = true; }
        Check(rejected && store.ReadMetadata()[game.Id]!.ToJsonString() == saved.ToJsonString(), "Wrong-title fallback replaced valid cache.");
        var response = new JsonObject { ["game_name"] = game.Name, ["hltb_id"] = 42, ["source"] = "live",
            ["main"] = new JsonObject { ["avg"] = 12.5, ["polled"] = 50 }, ["extra"] = new JsonObject { ["avg"] = 22.0 } };
        string requestedDurationTitle = "";
        using var durationClient = new MetadataClient(new Fixture(image), durationLookup: (title, _, _) => {
            requestedDurationTitle = title;
            return Task.FromResult(CompletionDuration.Parse(title, response));
        });
        var previousCover = DataJson.Text(saved["cover"]);
        durationClient.Refresh(game, store, false, true, default).GetAwaiter().GetResult();
        var timed = store.ReadMetadata()[game.Id]!;
        Check(DataJson.Number(timed["time"]) == 12.5 && DataJson.Text(timed["cover"]) == previousCover, "Time-only fallback lost artwork or duration.");
        Check(DataJson.Text(timed["timeUrl"]) == "https://howlongtobeat.com/game/42" && DataJson.Number(timed["timeSamples"]) == 50, "Duration attribution lost.");
        var durationEvidence = MetadataEvidence.ReadField(store, game.Id, MetadataField.MainStoryHours);
        Check(DataJson.Text(durationEvidence?["validation"]?["status"]) == "ValidFresh"
            && DataJson.Number(durationEvidence?["evidence"]?["numericValue"]) == 12.5
            && DataJson.Number(durationEvidence?["evidence"]?["sampleCount"]) == 50
            && DataJson.Text(durationEvidence?["evidence"]?["providerProductId"]) == "HLTB:42",
            "Live completion time did not persist its provider product ID, sample count, and field scope.");
        Check(DataJson.Text(MetadataEvidence.ReadField(store, game.Id, MetadataField.MainPlusExtrasHours)?["lastAttemptValidation"]?["status"]) == "Unavailable",
            "An extra-hours estimate without its own provider sample count was treated as verified.");
        response["extra"]!["polled"] = 25;
        response["completionist"] = new JsonObject { ["avg"] = 45.0, ["polled"] = 10 };
        durationClient.Refresh(game, store, false, true, default).GetAwaiter().GetResult();
        Check(DataJson.Text(MetadataEvidence.ReadField(store, game.Id, MetadataField.MainPlusExtrasHours)?["validation"]?["status"]) == "ValidFresh"
            && DataJson.Number(MetadataEvidence.ReadField(store, game.Id, MetadataField.MainPlusExtrasHours)?["evidence"]?["sampleCount"]) == 25
            && DataJson.Text(MetadataEvidence.ReadField(store, game.Id, MetadataField.CompletionistHours)?["validation"]?["status"]) == "ValidFresh",
            "Sampled extra and completionist values were not recorded in their separate completion scopes.");
        game.Name = "MetadataProof";
        for (int repeat = 0; repeat < 2; repeat++)
        {
            durationClient.Refresh(game, store, false, true, default).GetAwaiter().GetResult();
            Check(requestedDurationTitle == "Metadata Proof" && DataJson.Text(store.ReadMetadata()[game.Id]?["timeQuery"]) == "Metadata Proof", "Duration refresh forgot verified word boundaries after cache reload.");
        }
        game.Name = "Metadata Proof";
        timed = store.ReadMetadata()[game.Id]!;
        string timeStamp = DataJson.Text(timed["timeFetchedAt"]);
        Check(DateTime.TryParse(timeStamp, out _), "Duration has no independent verification timestamp.");
        fixture.WrongDetails = false;
        client.Refresh(game, store, true, false, default).GetAwaiter().GetResult();
        Check(DataJson.Text(store.ReadMetadata()[game.Id]?["timeFetchedAt"]) == timeStamp, "Cover-only refresh extended duration freshness.");
        var localState = new UserState();
        localState.LocalGames[game.Id] = new LocalGame { Name = game.Name, Folder = Path.Combine(store.Root, "time-proof") };
        var loaded = System.Linq.Enumerable.Single(store.LoadGames(localState, store.ReadConfig()), g => g.Id == game.Id);
        Check(loaded.TimeVerifiedAt > DateTime.UtcNow.AddMinutes(-1), "Sourced duration freshness lost on reload.");
        response["source"] = "cache-stale";
        durationClient.Refresh(game, store, false, true, default).GetAwaiter().GetResult();
        Check(DataJson.Text(store.ReadMetadata()[game.Id]?["timeFetchedAt"]) == timeStamp,
            "A cached completion estimate extended its verification timestamp.");
        response["source"] = "live";
        response["game_name"] = "Metadata Proof 2";
        rejected = false;
        try { CompletionDuration.Parse(game.Name, response); } catch (FormatException) { rejected = true; }
        Check(rejected, "Duration accepted sequel identity.");
        response["game_name"] = game.Name; response["main"]!["avg"] = 0;
        Check(CompletionDuration.Parse(game.Name, response) == null, "Extra duration was substituted for main story.");
        response["main"]!["avg"] = 12.5;
        response["main"]!["polled"] = 0;
        Check(CompletionDuration.Parse(game.Name, response) == null, "A completion estimate without provider samples was accepted.");
        response["main"]!["polled"] = 50;
        var productTaggedTime = CompletionDuration.Parse(game.Name, response)!;
        Check(CompletionDuration.TryGetProductId(productTaggedTime, out string hltbProductId) && hltbProductId == "HLTB:42",
            "Completion time lost its exact HowLongToBeat product ID.");
        productTaggedTime["timeUrl"] = "https://howlongtobeat.com/game/43";
        Check(CompletionDuration.TryGetProductId(productTaggedTime, out _), "A syntactically valid provider URL could not be parsed for mismatch testing.");
        var wrongProductEvidence = new MetadataEvidenceRecord(game.Id, MetadataField.MainStoryHours, "howlongtobeat", "HLTB:42",
            MetadataSourceLocationKind.Remote, DataJson.Text(productTaggedTime["timeUrl"]),
            new MetadataGameScope(game.Name, null, "PC", null), MetadataEvidenceOutcome.Available, 12.5m, null, "hours",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30), SampleCount: 50, MeasurementBasis: "mainStory",
            CanonicalProductIdentity: "game:" + game.Id);
        var wrongProductIdentity = new MetadataGameIdentity(game.Id, "game:" + game.Id,
            new MetadataGameScope(game.Name, null, "PC", null), new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
            { ["howlongtobeat"] = "HLTB:42" });
        Check(MetadataEvidence.Evaluate(wrongProductIdentity, MetadataField.MainStoryHours, wrongProductEvidence, DateTimeOffset.UtcNow)
            .Status == MetadataCoverageStatus.IdentityMismatch,
            "HowLongToBeat data was accepted when its URL referred to a different product ID.");
        productTaggedTime["timeUrl"] = "https://howlongtobeat.com/game/42?redirect=1";
        Check(!CompletionDuration.TryGetProductId(productTaggedTime, out _),
            "A HowLongToBeat URL with a query string was accepted as canonical product provenance.");
        productTaggedTime["timeUrl"] = "https://user@howlongtobeat.com/game/42";
        Check(!CompletionDuration.TryGetProductId(productTaggedTime, out _),
            "A credential-bearing HowLongToBeat URL was accepted as canonical product provenance.");
        productTaggedTime["timeUrl"] = "https://howlongtobeat.com/game/2147483648";
        Check(!CompletionDuration.TryGetProductId(productTaggedTime, out _),
            "An out-of-range HowLongToBeat product ID was accepted.");
        var aliasResponse = new JsonObject { ["game_name"] = "Sword of The Vagrant", ["provider_alias"] = "The Vagrant",
            ["hltb_id"] = 25254, ["source"] = "live", ["main"] = new JsonObject { ["avg"] = 32701.0 / 3600, ["polled"] = 56 } };
        var aliasDuration = CompletionDuration.Parse("The Vagrant", aliasResponse)!;
        Check(DataJson.Text(aliasDuration["timeTitle"]) == "Sword of The Vagrant" && DataJson.Text(aliasDuration["timeAlias"]) == "The Vagrant", "Duration provider identity evidence was lost.");
        rejected = false;
        try { CompletionDuration.Parse("The Vagrant 2", aliasResponse); } catch (FormatException) { rejected = true; }
        Check(rejected, "Provider alias matched an unrelated sequel.");
        aliasResponse["provider_alias"] = "The Vagrant, Another Game";
        rejected = false;
        try { CompletionDuration.Parse("The Vagrant", aliasResponse); } catch (FormatException) { rejected = true; }
        Check(rejected, "A partial provider alias was accepted.");
        fixture.WrongDetails = false;
        awaitSteamPunctuation();
        var wandGame = new Game { Id = "wand:123", Name = game.Name };
        var catalog = new JsonObject {
            ["games"] = new JsonObject { ["123"] = new JsonObject { ["id"] = "123", ["titleId"] = "456", ["platformId"] = "steam",
                ["correlationIds"] = new JsonArray("steam:1") } },
            ["titles"] = new JsonObject { ["456"] = new JsonObject { ["id"] = "456", ["name"] = game.Name } } };
        Check(SteamMetadata.WandSteamId(catalog, wandGame, game.Name) == 1, "Stable Wand identity was lost.");
        void RejectWand(string message)
        {
            bool failed = false;
            try { SteamMetadata.WandSteamId(catalog, wandGame, game.Name); } catch (FormatException) { failed = true; }
            Check(failed, message);
        }
        catalog["games"]!["123"]!["correlationIds"] = new JsonArray("steam:1", "steam:2");
        RejectWand("Ambiguous Steam identities accepted.");
        catalog["games"]!["123"]!["correlationIds"] = new JsonArray("steam:invalid");
        RejectWand("Malformed Steam identity accepted.");
        catalog["games"]!["123"]!["correlationIds"] = new JsonArray("steam:1");
        catalog["titles"]!["456"]!["name"] = game.Name + " 2";
        RejectWand("Different Wand title accepted.");
        catalog["titles"]!["456"]!["name"] = game.Name;
        catalog["games"]!["123"]!["id"] = "999";
        RejectWand("Mismatched Wand game id accepted.");
        catalog["games"]!["123"]!["id"] = "123";
        string catalogPath = LibraryStore.SafeChild(store.Cache, "wand-catalog.json");
        byte[]? oldCatalog = File.Exists(catalogPath) ? File.ReadAllBytes(catalogPath) : null;
        try
        {
            store.CacheData("wand-catalog.json", catalog.ToJsonString());
            using var http = new HttpClient(fixture, disposeHandler: false);
            fixture.LastSearchTerm = null;
            var direct = SteamMetadata.Read(http, wandGame, game.Name, default, store).GetAwaiter().GetResult();
            Check(DataJson.Number(direct["steamAppId"]) == 1 && fixture.LastSearchTerm == null, "Known platform identity fell back to title search.");
            fixture.WrongDetails = true;
            rejected = false;
            try { SteamMetadata.Read(http, wandGame, game.Name, default, store).GetAwaiter().GetResult(); }
            catch (FormatException) { rejected = true; }
            Check(rejected, "Direct identity skipped Steam title verification.");
        }
        finally
        {
            if (oldCatalog == null) File.Delete(catalogPath); else File.WriteAllBytes(catalogPath, oldCatalog);
        }
        void awaitSteamPunctuation()
        {
            using var http = new HttpClient(fixture, disposeHandler: false);
            SteamMetadata.Read(http, game, "Metadata - Proof", default).GetAwaiter().GetResult();
            Check(fixture.LastSearchTerm == "Metadata Proof", "Folder punctuation became a Steam search term.");
        }
    }

    private static void VerifySuccessfulIncompleteResponseFallback(LibraryStore parentStore, byte[] image)
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var game = new Game { Id = "thevagrant", Name = "The Vagrant", Category = "new" };

        var successfulStore = new LibraryStore(Path.Combine(parentStore.Root, "vagrant-artwork-fallback-" + Guid.NewGuid().ToString("N")));
        var successfulFixture = new IncompleteArtworkFixture(image);
        using (var client = new MetadataClient(successfulFixture))
            client.Refresh(game, successfulStore, cover: true, time: true, CancellationToken.None).GetAwaiter().GetResult();
        var saved = successfulStore.ReadMetadata()[game.Id]!;
        string coverRelativePath = DataJson.Text(saved["cover"]);
        string? coverPath = coverRelativePath.Length == 0 ? null : LibraryStore.SafeChild(successfulStore.Cache, coverRelativePath);
        var artwork = MetadataEvidence.ReadField(successfulStore, game.Id, MetadataField.Artwork);
        var duration = MetadataEvidence.ReadField(successfulStore, game.Id, MetadataField.MainStoryHours);
        Check(successfulFixture.HostCalls == 1 && successfulFixture.SteamSearchCalls == 1
            && successfulFixture.SteamDetailsCalls == 1 && successfulFixture.ImageCalls == 1,
            "A successful but artwork-incomplete response did not use one exact Steam fallback.");
        Check(DataJson.Number(saved["steamAppId"]) == 598700
            && DataJson.Text(saved["matchedTitle"]) == "The Vagrant"
            && DataJson.Text(saved["source"]?["image"]) == "steam-direct"
            && DataJson.Text(artwork?["validation"]?["status"]) == "ValidFresh"
            && DataJson.Text(artwork?["evidence"]?["providerProductId"]) == "Steam:598700"
            && DataJson.Text(artwork?["evidence"]?["canonicalProductIdentity"]) == "steam:598700"
            && coverPath != null && ArtworkFile.IsUsable(coverPath),
            "Steam artwork was not stored with an exact product identity and verified content address.");
        Check(Math.Abs(DataJson.Number(saved["time"]) - 9.08361111111111) < .000001
            && DataJson.Text(saved["timeUrl"]) == "https://howlongtobeat.com/game/25254"
            && DataJson.Text(saved["source"]?["time"]) == "howlongtobeat-live"
            && DataJson.Text(duration?["validation"]?["status"]) == "ValidFresh"
            && DataJson.Text(duration?["evidence"]?["providerProductId"]) == "HLTB:25254",
            "Artwork fallback overwrote or detached The Vagrant's independently sourced main-story time.");
        Check(Math.Abs(DataJson.Number(saved["diskRequirementGb"]) - 1.2) < .000001
            && successfulStore.LoadState().AdditionalData?["installedBytes"] == null,
            "Steam's published requirement was conflated with measured installed size.");

        var mismatchStore = new LibraryStore(Path.Combine(parentStore.Root, "vagrant-artwork-mismatch-" + Guid.NewGuid().ToString("N")));
        var mismatchFixture = new IncompleteArtworkFixture(image, mismatchAppId: true);
        using (var client = new MetadataClient(mismatchFixture))
            client.Refresh(game, mismatchStore, cover: true, time: true, CancellationToken.None).GetAwaiter().GetResult();
        var mismatch = mismatchStore.ReadMetadata()[game.Id]!;
        Check(mismatchFixture.HostCalls == 1 && mismatchFixture.SteamSearchCalls == 1
            && mismatchFixture.SteamDetailsCalls == 1 && mismatchFixture.ImageCalls == 0
            && mismatch["cover"] == null && mismatch["steamAppId"] == null
            && DataJson.Number(mismatch["time"]) > 0
            && DataJson.Text(mismatch["source"]?["time"]) == "howlongtobeat-live",
            "A mismatched Steam product supplied artwork or blocked the independent HLTB result.");

        var invalidImageStore = new LibraryStore(Path.Combine(parentStore.Root, "vagrant-artwork-invalid-" + Guid.NewGuid().ToString("N")));
        var invalidImageFixture = new IncompleteArtworkFixture(Encoding.UTF8.GetBytes("not an image"));
        using (var client = new MetadataClient(invalidImageFixture))
            client.Refresh(game, invalidImageStore, cover: true, time: true, CancellationToken.None).GetAwaiter().GetResult();
        var invalidImage = invalidImageStore.ReadMetadata()[game.Id]!;
        Check(invalidImageFixture.ImageCalls == 1 && invalidImage["cover"] == null
            && DataJson.Number(invalidImage["steamAppId"]) == 598700
            && DataJson.Number(invalidImage["time"]) > 0
            && DataJson.Text(invalidImage["source"]?["time"]) == "howlongtobeat-live"
            && MetadataEvidence.ReadField(invalidImageStore, game.Id, MetadataField.Artwork)?["evidence"] == null,
            "Undecodable Steam artwork was stored or caused a valid time update to be lost.");
    }

    private sealed class IncompleteArtworkFixture(byte[] image, bool mismatchAppId = false) : HttpMessageHandler
    {
        internal int HostCalls;
        internal int SteamSearchCalls;
        internal int SteamDetailsCalls;
        internal int ImageCalls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "game-library-michaelunkai.netlify.app")
            {
                HostCalls++;
                var response = new JsonObject
                {
                    ["success"] = true, ["id"] = "thevagrant", ["name"] = "The Vagrant", ["category"] = "new",
                    ["time"] = 9.08361111111111, ["timeUrl"] = "https://howlongtobeat.com/game/25254",
                    ["timeTitle"] = "The Vagrant", ["timeQuery"] = "The Vagrant", ["timeSamples"] = 82,
                    ["timeIdentityReleaseYear"] = 2018,
                    ["source"] = new JsonObject { ["time"] = "howlongtobeat-live" }
                };
                return Task.FromResult(JsonResponse(response));
            }
            if (uri.Host == "store.steampowered.com" && uri.AbsolutePath.Contains("storesearch", StringComparison.Ordinal))
            {
                SteamSearchCalls++;
                return Task.FromResult(JsonResponse(new JsonObject { ["items"] = new JsonArray(
                    new JsonObject { ["id"] = 598700, ["name"] = "The Vagrant" },
                    new JsonObject { ["id"] = 888360, ["name"] = "The Vagrant Artbook" }) }));
            }
            if (uri.Host == "store.steampowered.com" && uri.AbsolutePath.Contains("appdetails", StringComparison.Ordinal))
            {
                SteamDetailsCalls++;
                return Task.FromResult(JsonResponse(new JsonObject
                {
                    ["598700"] = new JsonObject { ["success"] = true, ["data"] = new JsonObject
                    {
                        ["steam_appid"] = mismatchAppId ? 598701 : 598700, ["name"] = "The Vagrant", ["type"] = "game",
                        ["header_image"] = "https://image.example/the-vagrant.jpg",
                        ["release_date"] = new JsonObject { ["coming_soon"] = false, ["date"] = "Jul 13, 2018" },
                        ["pc_requirements"] = new JsonObject { ["minimum"] = "Storage: 1200 MB available space" }
                    } }
                }));
            }
            if (uri.Host == "image.example")
            {
                ImageCalls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(image) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse(JsonNode json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    private sealed class Fixture(byte[] image) : HttpMessageHandler
    {
        internal int HostCalls;
        internal bool WrongDetails;
        internal string? LastSearchTerm;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.Contains("storesearch")) LastSearchTerm = Uri.UnescapeDataString(uri.Query.Split("term=")[1]);
            if (uri.Host.EndsWith("netlify.app")) { HostCalls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)); }
            if (uri.Host == "image.example") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(image) });
            string json = uri.AbsolutePath.Contains("storesearch")
                ? "{\"items\":[{\"id\":2,\"name\":\"Metadata Proof DEMO\"},{\"id\":3,\"name\":\"Metadata Proof Soundtrack\"},{\"id\":1,\"name\":\"Metadata Proof\"}]}"
                : new JsonObject { ["1"] = new JsonObject { ["success"] = true, ["data"] = new JsonObject {
                    ["steam_appid"] = 1, ["name"] = WrongDetails ? "Metadata Proof 2" : "Metadata Proof", ["type"] = "game",
                    ["header_image"] = "https://image.example/cover.jpg", ["pc_requirements"] = new JsonObject { ["minimum"] = "Storage: 50 GB available space" } } } }.ToJsonString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
