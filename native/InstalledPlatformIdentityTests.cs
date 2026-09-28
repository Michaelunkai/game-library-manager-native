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

internal static class InstalledPlatformIdentityTests
{
    internal static void Run(LibraryStore store, string root, byte[] image)
    {
        var checks = new List<(string Name, bool Passed)>();
        void Check(string name, bool passed) => checks.Add((name, passed));
        Check("Steam exact release date yields year", SteamMetadata.ReleaseYear(new JsonObject { ["coming_soon"] = false, ["date"] = "18 Apr, 2017" }) == 2017);
        foreach (string date in new[] { "2017", "04/05/2017", "Coming soon", "1 Jan, 2999", "18 Apr, 1960" })
            Check("Steam ambiguous future or invalid release date rejected: " + date, SteamMetadata.ReleaseYear(new JsonObject { ["coming_soon"] = false, ["date"] = date }) == 0);
        Check("Coming soon cannot establish release year", SteamMetadata.ReleaseYear(new JsonObject { ["coming_soon"] = true, ["date"] = "18 Apr, 2017" }) == 0);
        Check("Coming soon flag requires boolean", SteamMetadata.ReleaseYear(new JsonObject { ["coming_soon"] = "false", ["date"] = "18 Apr, 2017" }) == 0);
        string folder = Path.Combine(root, "opaque-folder"); Directory.CreateDirectory(folder);
        string exe = Path.Combine(folder, "main.exe"); File.WriteAllBytes(exe, new byte[] { 77, 90 });
        string appid = Path.Combine(folder, "steam_appid.txt"), ini = Path.Combine(folder, "steam_emu.ini");
        string id = LocalGame.Identity(folder);
        var state = new UserState(); state.LocalGames[id] = new LocalGame { Name = "opaque-folder", Folder = folder, Category = "action" };
        state.LaunchPaths[id] = exe; state.Ratings[id] = 4; state.InstalledGames.Add(id);
        Game Load() => store.LoadGames(state, store.ReadConfig()).Single(g => g.Id == id);
        int AppId(Game game) => (int?)typeof(Game).GetProperty("MetadataSteamAppId")?.GetValue(game) ?? 0;
        JsonObject Cached(int value = 1234) => new() { ["steamAppId"] = value, ["matchedTitle"] = "Canonical Future Game", ["time"] = 12,
            ["source"] = new JsonObject { ["image"] = "steam-direct" } };
        void Cache(JsonObject value) { var cache = store.ReadMetadata(); cache[id] = value; store.CacheData("metadata.json", cache.ToJsonString()); }
        File.WriteAllText(appid, "1234\n"); Cache(Cached());
        var game = Load();
        Check("Exact local platform identity enriches folder title", game.Name == "Canonical Future Game" && game.MetadataLookupTitle == "Canonical Future Game" && AppId(game) == 1234);
        state.LocalGames[id].Name = "My personal label";
        game = Load();
        Check("Custom name category rating and launcher preserved", game.Name == "My personal label" && game.Category == "action" && game.Rating == 4 && state.LaunchPaths[id] == exe);
        store.Save(state); state = store.LoadState();
        Check("Platform association survives restart", AppId(Load()) == 1234 && Load().MetadataLookupTitle == "Canonical Future Game");
        Cache(Cached(5678)); Check("Conflicting cached platform ID rejects hours", Load().Time == 0);
        Cache(new JsonObject());
        string unity = Path.Combine(folder, "main_Data"); Directory.CreateDirectory(unity); File.WriteAllText(Path.Combine(unity, "app.info"), "Publisher\nZebra\n");
        game = Load(); Check("Internal Unity project name is not a public title", game.MetadataLookupTitle != "Zebra" && AppId(game) == 1234);
        using var fixture = new Fixture(image);
        using var client = new MetadataClient(fixture, durationLookup: (title, _, _) => {
            Check("Duration query uses canonical exact-ID title", title == "Canonical Future Game");
            return Task.FromResult<JsonObject?>(CompletionDuration.Parse(title, new JsonObject { ["game_name"] = title, ["hltb_id"] = 42,
                ["identity_release_year"] = 2017, ["source"] = "live", ["main"] = new JsonObject { ["avg"] = 12.5, ["polled"] = 4 } }));
        });
        try {
            client.Refresh(game, store, true, true, default).GetAwaiter().GetResult();
            Check("Exact-ID lookup bypasses hosted and title search", fixture.Hosted == 0 && fixture.Searches == 0 && fixture.Details == 1);
            Check("Canonical title cover duration survive reload", Load().MetadataLookupTitle == "Canonical Future Game" && Load().Time == 12.5 && File.Exists(Load().Cover));
            Check("Verified Steam release year survives metadata attribution", DataJson.Number(store.ReadMetadata()[id]?["steamReleaseYear"]) == 2017 && DataJson.Text(store.ReadMetadata()[id]?["source"]?["releaseYear"]) == "steam-direct");
            var gate = typeof(MainWindow).GetMethod("DurationWorkAllowed", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            var loaded = Load(); var identity = store.ReadMetadata()[id] as JsonObject;
            bool Allowed(Game candidate, JsonObject? cached, bool waiting) => gate != null && (bool)gate.Invoke(null, new object?[] { candidate, cached, waiting })!;
            Check("Duration cooldown skips attributed artwork-complete row", gate != null && !Allowed(loaded, identity, true));
            Check("Duration cooldown still permits unverified platform identity", Allowed(loaded, null, true));
            string savedCover = loaded.Cover; loaded.Cover = "";
            Check("Duration cooldown still permits missing artwork", Allowed(loaded, identity, true));
            loaded.Cover = savedCover;
            Check("Expired duration cooldown permits normal scheduling", Allowed(loaded, identity, false));
            var background = typeof(MainWindow).GetMethod("MetadataBackgroundAllowed", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            bool Background(Game candidate, bool admin, bool protectedCategory, bool hiddenCategory) => background != null && (bool)background.Invoke(null, new object[] { candidate, admin, protectedCategory, hiddenCategory })!;
            string personalBefore = DataJson.Write(state);
            Check("Installed hidden category remains background metadata eligible", Background(loaded, false, false, true));
            Check("Installed protected category remains background metadata eligible", Background(loaded, false, true, false));
            var uninstalled = new Game { Id = "hidden-uninstalled", Name = "Hidden Uninstalled" };
            Check("Uninstalled hidden category stays background ineligible", background != null && !Background(uninstalled, false, false, true));
            Check("Background eligibility preserves personal state", personalBefore == DataJson.Write(state));
            string completionFolder = Path.Combine(store.Cache, "completion-times"); Directory.CreateDirectory(completionFolder);
            string cooldownPath = Path.Combine(completionFolder, "provider-cooldown.json");
            File.WriteAllText(cooldownPath, new JsonObject { ["retry_at"] = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeMilliseconds() / 1000d }.ToJsonString());
            Check("Native reads durable helper provider cooldown", CompletionDuration.RetryAt(store) > DateTime.UtcNow.AddMinutes(29));
            File.WriteAllText(cooldownPath, new JsonObject { ["retry_at"] = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds() }.ToJsonString());
            Check("Native releases expired helper cooldown", CompletionDuration.RetryAt(store) == default);
            File.WriteAllText(cooldownPath, "{");
            Check("Malformed helper cooldown does not block forever", CompletionDuration.RetryAt(store) == default);
            File.Delete(cooldownPath);
        } catch (Exception) { Check("Exact-ID lookup succeeds from opaque personal label", false); }
        fixture.WrongId = true;
        bool rejected = false;
        try { using var http = new HttpClient(fixture, false); SteamMetadata.Read(http, game, game.Name, default).GetAwaiter().GetResult(); } catch (FormatException) { rejected = true; }
        Check("Mismatched appdetails ID rejected", rejected);
        fixture.WrongId = false; fixture.Utility = true; rejected = false;
        try { using var http = new HttpClient(fixture, false); SteamMetadata.Read(http, game, game.Name, default).GetAwaiter().GetResult(); } catch (FormatException) { rejected = true; }
        Check("Non-game appdetails rejected", rejected);
        Cache(Cached());
        File.WriteAllText(ini, "[GameSettings]\nAppId=5678\n"); Check("Conflicting local IDs reject resolution", AppId(Load()) == 0);
        Check("Conflicting local IDs reject previously attributed hours", Load().Time == 0);
        Check("Conflicting local IDs do not schedule title guess", !MainWindow.MetadataDue(Load(), new(), DateTime.UtcNow));
        File.WriteAllText(ini, "[GameSettings]\nAppId=1234\n"); Check("Matching repeated identity corroborates", AppId(Load()) == 1234);
        File.WriteAllBytes(ini, new byte[] { 219, 220, 219, 13, 10 }.Concat(Encoding.ASCII.GetBytes("[GameSettings]\nAppId=1234\n")).ToArray());
        Check("Legacy ANSI INI banner does not invalidate ASCII AppId", AppId(Load()) == 1234);
        File.WriteAllText(ini, "[GameSettings]\nAppId=1234\n");
        state.LocalGames[id].Name = "Canonical Future Game";
        var legacy = Cached(); legacy.Remove("steamAppId"); Cache(legacy);
        Check("Matching legacy title cache remains while exact ID refresh is pending", Load().Time == 12 && AppId(Load()) == 1234);
        state.LocalGames[id].Name = "My personal label"; Cache(Cached());
        File.WriteAllText(appid, "1234garbage"); Check("Malformed identity rejects resolution", AppId(Load()) == 0);
        File.WriteAllText(appid, new string('1', 65537)); Check("Oversized identity rejects resolution", AppId(Load()) == 0);
        File.WriteAllText(appid, "0"); Check("Zero identity rejected", AppId(Load()) == 0);
        File.WriteAllText(appid, "1234");
        state.LaunchPaths[id] = Path.Combine(root, "outside.exe"); File.WriteAllBytes(state.LaunchPaths[id], new byte[] { 77, 90 });
        Check("Executable outside root rejects identity", AppId(Load()) == 0);
        state.LaunchPaths[id] = exe;
        File.Delete(appid); File.Delete(ini); File.WriteAllText(Path.Combine(root, "steam_appid.txt"), "1234");
        Check("Parent identity and Unity hint alone ignored", AppId(Load()) == 0 && Load().MetadataLookupTitle.Length == 0);
        string nested = Path.Combine(folder, "bin"); Directory.CreateDirectory(nested);
        state.LaunchPaths[id] = Path.Combine(nested, "game.exe"); File.WriteAllBytes(state.LaunchPaths[id], new byte[] { 77, 90 });
        File.WriteAllText(Path.Combine(nested, "flt.ini"), "[Steam]\nAppId=1234\n");
        Check("Selected executable directory identity supported", AppId(Load()) == 1234);
        var attempts = new JsonObject { [id] = new JsonObject { ["queryTitle"] = MetadataClient.ExpectedTitle(Load()), ["retryAfter"] = DateTime.UtcNow.AddHours(1).ToString("O") } };
        Check("New platform association retries obsolete title failure", MainWindow.MetadataDue(Load(), attempts, DateTime.UtcNow));
        attempts[id]!["steamAppId"] = 1234;
        attempts[id]!["durationContractRevision"] = 2;
        Check("Same platform association honors failure backoff", !MainWindow.MetadataDue(Load(), attempts, DateTime.UtcNow));
        attempts[id]!.AsObject().Remove("durationContractRevision");
        Check("Old installed duration contract receives one retry", MainWindow.MetadataDue(Load(), attempts, DateTime.UtcNow));
        attempts[id]!["durationContractRevision"] = 2;
        Check("Current duration contract failure retains backoff", !MainWindow.MetadataDue(Load(), attempts, DateTime.UtcNow));
        File.Delete(Path.Combine(nested, "flt.ini")); state.LaunchPaths[id] = exe;
        string plugins = Path.Combine(unity, "Plugins", "x86_64"); Directory.CreateDirectory(plugins);
        File.WriteAllText(Path.Combine(plugins, "steam_emu.ini"), "[Steam]\nAppId=1234\n");
        Check("Selected Unity executable plugin ID supported", AppId(Load()) == 1234);
        File.Delete(Path.Combine(plugins, "steam_emu.ini"));
        string otherPlugins = Path.Combine(folder, "DigitalArtbook_Data", "Plugins", "x86_64"); Directory.CreateDirectory(otherPlugins);
        File.WriteAllText(Path.Combine(otherPlugins, "steam_emu.ini"), "AppId=9999");
        Check("Sibling Unity artbook identity ignored", AppId(Load()) == 0);
        string steamworks = Path.Combine(folder, "Engine", "Binaries", "ThirdParty", "Steamworks", "Steamv157", "Win64"); Directory.CreateDirectory(steamworks);
        File.WriteAllText(Path.Combine(steamworks, "steam_emu.ini"), "AppId=1234");
        Check("Bounded Unreal Steamworks identity supported", AppId(Load()) == 1234);
        File.Delete(Path.Combine(steamworks, "steam_emu.ini"));
        string tenoke = Path.Combine(steamworks, "tenoke.ini");
        File.WriteAllText(tenoke, "[TENOKE]\nid = 1234 # canonical game\n[Other]\nid=9999\n");
        Check("Bounded Unreal TENOKE section ID supported", AppId(Load()) == 1234);
        File.WriteAllText(tenoke, "[TENOKE]\nid=1234\n[ACHIEVEMENTS]\n#" + new string('x', 100000));
        Check("Large bounded TENOKE achievement catalog retains identity", AppId(Load()) == 1234);
        File.AppendAllText(tenoke, "\n[TENOKE]\nid=5678\n");
        Check("Late conflicting TENOKE ID beyond 64KB rejected", AppId(Load()) == 0);
        File.WriteAllText(tenoke, "[TENOKE]\nid=1234\n#" + new string('x', 262145));
        Check("TENOKE beyond 256KB rejected", AppId(Load()) == 0);
        File.WriteAllText(tenoke, "[Other]\nid=1234\n");
        Check("TENOKE wrong section cannot establish identity", InstalledPlatformIdentity.Resolve(folder, exe, out bool wrongSection) == null && wrongSection);
        File.WriteAllText(tenoke, "[TENOKE]\nid=1234\nid=5678\n");
        Check("TENOKE conflicting section IDs rejected", AppId(Load()) == 0);
        File.Delete(tenoke);
        string unityTenoke = Path.Combine(plugins, "tenoke.ini");
        File.WriteAllText(unityTenoke, "[TENOKE]\nid=1234\n");
        Check("Selected Unity TENOKE identity supported", AppId(Load()) == 1234);
        File.WriteAllText(Path.Combine(otherPlugins, "tenoke.ini"), "[TENOKE]\nid=9999\n");
        Check("Unrelated Unity TENOKE identity ignored", AppId(Load()) == 1234);
        string api = Path.Combine(folder, "steam_api64.ini");
        File.WriteAllText(api, "[Game]\nAppId=1234\n");
        Check("Steam API64 corroborates exact selected game ID", AppId(Load()) == 1234);
        File.WriteAllText(api, "[Game]\nAppId=5678\n");
        Check("Steam API64 conflicting format rejected", AppId(Load()) == 0);
        File.Delete(unityTenoke);
        File.WriteAllText(api, "[Game]\nAppId=1234\n");
        Check("Standalone Steam API64 identity supported", AppId(Load()) == 1234);
        File.Delete(api);
        File.WriteAllText(Path.Combine(steamworks, "steam_emu.ini"), "AppId=1234");
        string catalogId = store.LoadGames(state, store.ReadConfig()).First(g => !g.IsLocal && !g.RequiresGameIdentity && !g.IsNonGame && CatalogIdentity.Known(g.Id) == null).Id;
        state.InstallationFolders[catalogId] = folder; state.LaunchPaths[catalogId] = exe; state.InstalledGames.Add(catalogId);
        Check("Installed catalog row uses exact local platform ID", AppId(store.LoadGames(state, store.ReadConfig()).Single(g => g.Id == catalogId)) == 1234);
        var catalogCache = store.ReadMetadata(); var originalCatalogMetadata = catalogCache[catalogId]?.DeepClone();
        catalogCache[catalogId] = Cached(); store.CacheData("metadata.json", catalogCache.ToJsonString());
        var enrichedCatalog = store.LoadGames(state, store.ReadConfig()).Single(g => g.Id == catalogId);
        Check("Verified platform title corrects stale catalog display", enrichedCatalog.Name == "Canonical Future Game" && enrichedCatalog.MetadataLookupTitle == "Canonical Future Game");
        string manifest = Path.Combine(folder, "goggame-123456.info");
        File.WriteAllText(manifest, new JsonObject { ["gameId"] = "123456", ["name"] = "GOG Canonical Game", ["playTasks"] = new JsonArray(new JsonObject {
            ["category"] = "game", ["type"] = "FileTask", ["isPrimary"] = true, ["path"] = "main.exe" }) }.ToJsonString());
        enrichedCatalog = store.LoadGames(state, store.ReadConfig()).Single(g => g.Id == catalogId);
        Check("GOG primary task corrects catalog display and rejects unrelated packaged metadata", enrichedCatalog.Name == "GOG Canonical Game" && enrichedCatalog.Time == 0 && enrichedCatalog.Cover == "");
        File.Delete(manifest);
        catalogCache = store.ReadMetadata();
        if (originalCatalogMetadata == null) catalogCache.Remove(catalogId); else catalogCache[catalogId] = originalCatalogMetadata;
        store.CacheData("metadata.json", catalogCache.ToJsonString());
        state.LaunchPaths[id] = Path.Combine(folder, "bin", "..", "main.exe");
        Check("Lexical traversal input rejected", AppId(Load()) == 0);
        LibraryStore.AtomicWrite(Path.Combine(root, "platform-identity-proof.json"), DataJson.Write(checks.Select(c => new { name = c.Name, passed = c.Passed }).ToArray()));
        if (checks.Any(c => !c.Passed)) throw new InvalidOperationException(string.Join("; ", checks.Where(c => !c.Passed).Select(c => c.Name)));
    }

    private sealed class Fixture(byte[] image) : HttpMessageHandler
    {
        internal int Hosted, Searches, Details;
        internal bool WrongId, Utility;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host.EndsWith("netlify.app")) { Hosted++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)); }
            if (uri.Host == "image.example") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(image) });
            if (uri.AbsolutePath.Contains("storesearch")) { Searches++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"items\":[]}") }); }
            Details++;
            var body = new JsonObject { ["1234"] = new JsonObject { ["success"] = true, ["data"] = new JsonObject { ["steam_appid"] = WrongId ? 5678 : 1234,
                ["name"] = "Canonical Future Game", ["type"] = Utility ? "tool" : "game", ["header_image"] = "https://image.example/cover.jpg" } } };
            body["1234"]!["data"]!["release_date"] = new JsonObject { ["coming_soon"] = false, ["date"] = "18 Apr, 2017" };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") });
        }
    }
}
