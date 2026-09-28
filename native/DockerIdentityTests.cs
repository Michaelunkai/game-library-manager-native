using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace GameLibrary.Native;

public static class DockerIdentityTests
{
    public static int Run(string root)
    {
        int count = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); count++; }
        string first = DockerIdentity.Create("michadockermisha/one", "latest");
        string second = DockerIdentity.Create("michadockermisha/two", "latest");
        Check(first != second && DockerIdentity.Create(DockerIdentity.CatalogRepository, "ExactTag") == "ExactTag", "Repository identity collision or legacy remap.");
        Check(DockerIdentity.Valid(first) && !DockerIdentity.Valid("docker:owner/repo:bad/tag") && !DockerIdentity.Valid("docker:owner/../repo:latest"), "Unsafe identity accepted.");
        Check(DockerScripts.InstallFolder(first) != DockerScripts.InstallFolder(second)
            && DockerScripts.InstallFolder(first).IndexOfAny(Path.GetInvalidFileNameChars()) < 0, "Install folders collide or contain invalid characters.");
        var game = new Game { Id = first, Name = "One", DockerImage = "michadockermisha/one:latest" };
        var preferences = new Preferences { DockerUsername = "unrelated", RepoName = "other", MountPath = Path.GetFullPath(root) };
        Check(new[] { "ps1", "sh", "bat" }.All(format => DockerScripts.Generate(new[] { game }, preferences, format).Contains(game.DockerImage, StringComparison.Ordinal)), "Export ignored the per-game image.");
        game.DockerImage = "michadockermisha/two:latest";
        bool rejected = false;
        try { DockerScripts.Validate(preferences, new[] { game }); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "Mismatched image accepted.");
        JsonObject Repo(string name) => new() { ["repository"] = "michadockermisha/" + name, ["count"] = 1,
            ["complete"] = true, ["stale"] = false, ["tags"] = new JsonArray(new JsonObject { ["name"] = "latest" }) };
        var snapshot = new JsonObject { ["success"] = true, ["complete"] = true, ["namespace"] = "michadockermisha", ["repositoryCount"] = 3,
            ["repositories"] = new JsonArray(Repo("backup"), Repo("one"), Repo("two")) };
        var games = new List<Game>(); LibraryStore.MergeNamespace(games, snapshot);
        Check(games.Count == 3 && games.Select(g => g.Id).Distinct().Count() == 3 && games.Any(g => g.Id == "latest"), "Namespace merger collapsed exact tags.");
        snapshot["repositories"]![1]!["stale"] = true;
        var unchanged = new List<Game>(); rejected = false;
        try { LibraryStore.MergeNamespace(unchanged, snapshot); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && unchanged.Count == 0, "Partial namespace mutated the catalog.");
        string scan = Path.Combine(root, "identity-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(scan, "latest"));
        string exact = Path.Combine(scan, DockerScripts.InstallFolder(first)); Directory.CreateDirectory(exact);
        Check(InstalledScanner.FindCatalogFolders(scan, first, "latest").SequenceEqual(new[] { exact })
            && InstalledScanner.FindCatalogFolders(scan, second, "latest").Count == 0, "Qualified identity matched another game's folder.");
        var store = new LibraryStore(Path.Combine(root, "identity-state-" + Guid.NewGuid().ToString("N")));
        var state = new UserState(); state.Ratings[first] = 4; state.Ratings[second] = 2; state.LaunchPaths[first] = Path.Combine(exact, "Game.exe");
        store.Save(state); var restored = store.LoadState();
        Check(restored.Ratings[first] == 4 && restored.Ratings[second] == 2 && restored.LaunchPaths[first] == state.LaunchPaths[first], "Qualified state did not round-trip.");
        return count;
    }
}
