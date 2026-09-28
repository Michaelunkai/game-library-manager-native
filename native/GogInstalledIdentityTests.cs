using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace GameLibrary.Native;

internal static class GogInstalledIdentityTests
{
    internal static void Run(LibraryStore store, string root)
    {
        var checks = new List<(string Name, bool Passed)>();
        string folder = Path.Combine(root, "unfamiliar-folder"); Directory.CreateDirectory(folder);
        string exe = Path.Combine(folder, "main.exe"); File.WriteAllBytes(exe, new byte[] { 77, 90 });
        string id = LocalGame.Identity(folder), manifest = Path.Combine(folder, "goggame-123456.info");
        var state = new UserState(); state.LocalGames[id] = new LocalGame { Name = "unfamiliar-folder", Folder = folder };
        state.LaunchPaths[id] = exe; state.InstalledGames.Add(id); state.Ratings[id] = 4;
        JsonObject Data(string title, string path) => new() { ["gameId"] = "123456", ["name"] = title, ["playTasks"] = new JsonArray(new JsonObject {
            ["category"] = "game", ["type"] = "FileTask", ["isPrimary"] = true, ["path"] = path }) };
        void Write(JsonObject value) => File.WriteAllText(manifest, value.ToJsonString());
        Game Load() => store.LoadGames(state, store.ReadConfig()).Single(g => g.Id == id);
        Write(Data("Verified Future Game", "main.exe"));
        var game = Load();
        checks.Add(("New local game takes its manifest title automatically", game.Name == "Verified Future Game" && game.MetadataLookupTitle == "Verified Future Game"));
        checks.Add(("Manifest title schedules normal automatic metadata", MetadataClient.ExpectedTitle(game) == "Verified Future Game" && MainWindow.MetadataDue(game, new(), DateTime.UtcNow)));
        state.LocalGames[id].Name = "My custom title";
        game = Load();
        checks.Add(("Custom label and rating survive manifest discovery", game.Name == "My custom title" && game.Rating == 4 && game.MetadataLookupTitle == "Verified Future Game"));
        store.Save(state); state = store.LoadState();
        checks.Add(("Manifest association survives saved state reload", Load().MetadataLookupTitle == "Verified Future Game" && state.LaunchPaths[id] == exe));
        var cache = store.ReadMetadata(); cache[id] = new JsonObject { ["matchedTitle"] = "Different Game", ["time"] = 99 }; store.CacheData("metadata.json", cache.ToJsonString());
        checks.Add(("Different-game cached hours are rejected", Load().Time == 0));
        Write(Data("Unrelated Game", "other.exe")); checks.Add(("Unrelated launch task cannot establish title", Load().MetadataLookupTitle == ""));
        Write(Data("Escaping Game", "../main.exe")); checks.Add(("Parent traversal is rejected", Load().MetadataLookupTitle == ""));
        Write(Data("Absolute Game", exe)); checks.Add(("Absolute launch paths are rejected", Load().MetadataLookupTitle == ""));
        var invalid = Data("Wrong ID", "main.exe"); invalid["gameId"] = "999"; Write(invalid);
        checks.Add(("Manifest filename and product ID must agree", Load().MetadataLookupTitle == ""));
        invalid = Data("Support Tool", "main.exe"); invalid["playTasks"]![0]!["category"] = "document"; Write(invalid);
        checks.Add(("Support tasks do not identify the game", Load().MetadataLookupTitle == ""));
        string launcher = Path.Combine(folder, "launcher.exe"); File.WriteAllBytes(launcher, new byte[] { 77, 90 });
        JsonObject Hidden() {
            var value = Data("Verified Future Game", "launcher.exe");
            value["playTasks"]![0]!["category"] = "launcher";
            ((JsonArray)value["playTasks"]!).Add(new JsonObject { ["category"] = "game", ["type"] = "FileTask", ["isHidden"] = true, ["path"] = "main.exe" });
            return value;
        }
        Write(Hidden()); checks.Add(("Hidden exact game task with valid primary launcher establishes title", Load().MetadataLookupTitle == "Verified Future Game"));
        foreach (string category in new[] { "bonus", "document" }) {
            invalid = Hidden(); invalid["playTasks"]![1]!["category"] = category; Write(invalid);
            checks.Add(($"Hidden {category} task rejected", Load().MetadataLookupTitle == ""));
        }
        invalid = Hidden(); invalid["playTasks"]![1]!["isHidden"] = false; Write(invalid);
        checks.Add(("Nonprimary nonhidden task rejected", Load().MetadataLookupTitle == ""));
        invalid = Hidden(); invalid["playTasks"]![1]!["isHidden"] = "true"; Write(invalid);
        checks.Add(("Hidden flag requires boolean", Load().MetadataLookupTitle == ""));
        invalid = Hidden(); invalid["playTasks"]![1]!["path"] = "other.exe"; Write(invalid);
        checks.Add(("Unrelated hidden target rejected", Load().MetadataLookupTitle == ""));
        invalid = Hidden(); invalid["playTasks"]![1]!["path"] = "../main.exe"; Write(invalid);
        checks.Add(("Escaping hidden task rejected", Load().MetadataLookupTitle == ""));
        invalid = Hidden(); invalid["playTasks"]![0]!["path"] = "missing.exe"; Write(invalid);
        checks.Add(("Hidden task with missing primary executable rejected", Load().MetadataLookupTitle == ""));
        invalid = Hidden(); invalid["playTasks"]![0]!["path"] = "../launcher.exe"; Write(invalid);
        checks.Add(("Hidden task with escaping primary rejected", Load().MetadataLookupTitle == ""));
        invalid = Hidden(); invalid["playTasks"]![0]!["isPrimary"] = "true"; Write(invalid);
        checks.Add(("Primary flag requires boolean", Load().MetadataLookupTitle == ""));
        File.WriteAllText(manifest, new string('x', 65537)); checks.Add(("Oversized manifests are rejected", Load().MetadataLookupTitle == ""));
        File.WriteAllText(manifest, "{"); checks.Add(("Malformed manifests do not break library loading", Load().MetadataLookupTitle == ""));
        Write(Data("Verified Future Game", "main.exe"));
        var second = Data("Conflicting Game", "main.exe"); second["gameId"] = "654321"; File.WriteAllText(Path.Combine(folder, "goggame-654321.info"), second.ToJsonString());
        checks.Add(("Conflicting matching manifests are rejected", Load().MetadataLookupTitle == ""));
        string tagged = Path.Combine(root, DockerScripts.InstallFolder("opaque-build")); Directory.CreateDirectory(tagged);
        string taggedExe = Path.Combine(tagged, "main.exe"); File.WriteAllBytes(taggedExe, new byte[] { 77, 90 });
        File.WriteAllText(Path.Combine(tagged, DockerScripts.CompletionMarkerName), "GameLibraryManager|opaque-build");
        string taggedManifest = Path.Combine(tagged, "goggame-123456.info"); File.WriteAllText(taggedManifest, Data("Verified Future Game", "main.exe").ToJsonString());
        checks.Add(("Published executable title takes precedence over opaque package tag", InstalledMetadataIdentity.Title(tagged, taggedExe) == "Verified Future Game"));
        File.WriteAllText(taggedManifest, Data("Unrelated Game", "other.exe").ToJsonString());
        checks.Add(("Unrelated manifest preserves verified installer fallback", InstalledMetadataIdentity.Title(tagged, taggedExe) == "opaque build"));
        LibraryStore.AtomicWrite(Path.Combine(root, "gog-installed-identity-proof.json"), DataJson.Write(checks.Select(c => new { name = c.Name, passed = c.Passed }).ToArray()));
        if (checks.Any(c => !c.Passed)) throw new InvalidOperationException(string.Join("; ", checks.Where(c => !c.Passed).Select(c => c.Name)));
    }
}
