using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace GameLibrary.Native;

public static class LocalCatalogTests
{
    public static int Run(string root)
    {
        int checks = 0;
        void Check(bool passed, string message) { if (!passed) throw new InvalidOperationException(message); checks++; }
        var store = new LibraryStore(Path.Combine(root, "local-catalog-" + Guid.NewGuid().ToString("N")));
        var state = new UserState();
        state.Pending.Add(new PendingEdit { Key = "older-shared-edit", After = JsonValue.Create("shared-category") });
        string pendingBefore = DataJson.Write(state.Pending);
        PendingEdit Edit(string section, JsonNode value, string key = "") => new() { Section = section, Key = key, After = value };
        JsonArray Tabs(string label) => new(new JsonObject { ["id"] = "new", ["name"] = "New" }, new JsonObject { ["id"] = "personal", ["name"] = label });
        LocalCatalogEdits.Save(store, state, Edit("tabs", Tabs("Personal")), Edit("gameCategories", JsonValue.Create("personal")!, "docker:fixture/one:latest"), Edit("hiddenTabs", new JsonArray("personal")));
        Check(DataJson.Write(state.Pending) == pendingBefore, "Local editing changed the shared publishing queue.");
        var restored = store.LoadState();
        using var client = new SyncClient(store, new OfflineNetworkGuard());
        var effective = client.Effective(restored);
        Check(DataJson.Text(effective["gameCategories"]?["docker:fixture/one:latest"]) == "personal" && store.LoadCategories(effective).Any(c => c.Name == "Personal"), "Local categories did not survive restart.");
        client.Remote["tabs"] = new JsonArray(new JsonObject { ["id"] = "remote", ["name"] = "Remote change" });
        client.Remote["gameCategories"]!["docker:fixture/one:latest"] = "remote";
        effective = client.Effective(restored);
        Check(store.LoadCategories(effective).Any(c => c.Id == "personal") && DataJson.Text(effective["gameCategories"]?["docker:fixture/one:latest"]) == "personal", "Remote refresh overwrote local organization.");
        string previous = state.LocalCatalog.ToJsonString();
        bool failed = false;
        using (var locked = new FileStream(store.StatePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try { LocalCatalogEdits.Save(store, state, Edit("tabs", Tabs("Changed")), Edit("hiddenTabs", new JsonArray())); }
            catch (IOException) { failed = true; }
        }
        Check(failed && state.LocalCatalog.ToJsonString() == previous && store.LoadState().LocalCatalog.ToJsonString() == previous, "Failed multi-field save partially committed local changes.");
        failed = false;
        try { LocalCatalogEdits.Save(store, state, Edit("tabs", new JsonArray(new JsonObject { ["id"] = "same", ["name"] = "A" }, new JsonObject { ["id"] = "same", ["name"] = "B" }))); }
        catch (FormatException) { failed = true; }
        Check(failed && state.LocalCatalog.ToJsonString() == previous, "Duplicate category identity changed state.");
        Check(LibraryStore.ValidateState(DataJson.Read<UserState>("{\"schemaVersion\":1}")).LocalCatalog.Count == 0, "Old profiles require an unsafe migration.");
        LocalCatalogEdits.Save(store, state, Edit("tabs", new JsonArray(new JsonObject { ["id"] = "new", ["name"] = "New" })), Edit("hiddenTabs", new JsonArray()), Edit("gameCategories", JsonValue.Create("new")!, "docker:fixture/one:latest"));
        restored = store.LoadState();
        Check(restored.LocalCatalog["tabs"]!.AsArray().Count == 1 && restored.LocalCatalog["hiddenTabs"]!.AsArray().Count == 0
            && DataJson.Text(restored.LocalCatalog["gameCategories"]?["docker:fixture/one:latest"]) == "new" && DataJson.Write(restored.Pending) == pendingBefore,
            "Category removal did not commit the complete local transaction.");
        return checks;
    }
}
