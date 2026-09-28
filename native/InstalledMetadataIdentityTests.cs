using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace GameLibrary.Native;

internal static class InstalledMetadataIdentityTests
{
    internal static void Run(LibraryStore store)
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        const string tag = "hellboy---web-of-wyrd";
        string root = Path.Combine(store.Root, "installed-identity-proof");
        string folder = Path.Combine(root, DockerScripts.InstallFolder(tag));
        Directory.CreateDirectory(folder);
        string marker = Path.Combine(folder, DockerScripts.CompletionMarkerName);
        Check(InstalledMetadataIdentity.Title(folder) == "", "Folder hash alone established identity.");
        File.WriteAllText(marker, "GameLibraryManager|" + tag);
        Check(MetadataClient.SameTitle(InstalledMetadataIdentity.Title(folder), "Hellboy: Web of Wyrd"), "Verified installer identity lost.");
        var state = new UserState();
        string id = LocalGame.Identity(folder);
        state.LocalGames[id] = new LocalGame { Name = Path.GetFileName(folder), Folder = folder };
        var game = store.LoadGames(state, store.ReadConfig()).Single(g => g.Id == id);
        Check(MetadataClient.SameTitle(game.Name, "Hellboy: Web of Wyrd"), "Default folder name did not resolve.");
        state.LocalGames[id].Name = "My custom label";
        game = store.LoadGames(state, store.ReadConfig()).Single(g => g.Id == id);
        Check(game.Name == "My custom label" && MetadataClient.MatchesGame(game, new JsonObject {
            ["id"] = id, ["name"] = "Hellboy: Web of Wyrd" }), "Custom name changed or broke provider identity.");
        var cache = store.ReadMetadata();
        cache[id] = new JsonObject { ["matchedTitle"] = "Hellboy 2", ["time"] = 99 };
        store.CacheData("metadata.json", cache.ToJsonString());
        Check(store.LoadGames(state, store.ReadConfig()).Single(g => g.Id == id).Time == 0, "Wrong cached title was displayed.");
        File.WriteAllText(marker, "GameLibraryManager|hellboy-2");
        Check(InstalledMetadataIdentity.Title(folder) == "", "Copied marker from another install accepted.");
        File.WriteAllText(marker, "GameLibraryManager|../hellboy");
        Check(InstalledMetadataIdentity.Title(folder) == "", "Invalid marker identity accepted.");
        File.WriteAllText(marker, new string('x', 1025));
        Check(InstalledMetadataIdentity.Title(folder) == "", "Oversized marker accepted.");
        foreach (string identity in new[] { "game-2", "game-2026", DockerIdentity.Create("michadockermisha/proof", "game-2") })
        {
            string numbered = Path.Combine(root, DockerScripts.InstallFolder(identity));
            Directory.CreateDirectory(numbered);
            File.WriteAllText(Path.Combine(numbered, DockerScripts.CompletionMarkerName), "GameLibraryManager|" + identity + "|operation");
            Check(InstalledMetadataIdentity.Title(numbered) == (identity == "game-2026" ? "game 2026" : "game 2"), "Numbered or qualified title identity changed.");
        }
    }
}
