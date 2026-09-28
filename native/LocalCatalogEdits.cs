using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace GameLibrary.Native;

public static class LocalCatalogEdits
{
    public static void Validate(JsonObject? local)
    {
        if (local == null) throw new FormatException("Invalid local catalog settings.");
        if (local.ContainsKey("gameCategories"))
        {
            if (local["gameCategories"] is not JsonObject values || values.Any(p => string.IsNullOrWhiteSpace(p.Key)
                || p.Value is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text)))
                throw new FormatException("Invalid local game category.");
        }
        if (local.ContainsKey("tabs"))
        {
            if (local["tabs"] is not JsonArray tabs) throw new FormatException("Invalid local tabs.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in tabs)
            {
                if (item is not JsonObject) throw new FormatException("Invalid local category.");
                string id = DataJson.Text(item?["id"]), name = DataJson.Text(item?["name"]);
                if (string.IsNullOrWhiteSpace(id) || !ids.Add(id) || string.IsNullOrWhiteSpace(name) || name.Length > 80)
                    throw new FormatException("Invalid or duplicate local category.");
            }
        }
        if (local.ContainsKey("hiddenTabs") && (local["hiddenTabs"] is not JsonArray hidden || hidden.Any(n =>
            n is not JsonValue value || !value.TryGetValue<string>(out var id) || string.IsNullOrWhiteSpace(id))))
            throw new FormatException("Invalid hidden local categories.");
        // New reliability and deletion data is recoverable. Preserve unknown fields
        // here so a newer build can round-trip them through an older build.
    }

    public static void Apply(JsonObject config, JsonObject local)
    {
        Validate(local);
        if (local["gameCategories"] is JsonObject categories)
            foreach (var category in categories) Merge.Set(config, new PendingEdit { Section = "gameCategories", Key = category.Key, After = category.Value });

        var deleted = local["deletedTabs"] is JsonArray deletedArray
            ? deletedArray.Select(item => DataJson.Text(item)).Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var mergedTabs = new List<JsonNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void AppendTabs(JsonNode? node)
        {
            if (node is not JsonArray array) return;
            foreach (var item in array)
            {
                string id = DataJson.Text(item?["id"]);
                if (string.IsNullOrWhiteSpace(id) || deleted.Contains(id) || !seen.Add(id)) continue;
                mergedTabs.Add(item!.DeepClone());
            }
        }
        // Local definitions retain their names and order; genuinely new shared
        // definitions are appended after them. Explicit deletion tombstones win.
        AppendTabs(local["tabs"]);
        AppendTabs(config["tabs"]);
        if (local.ContainsKey("tabs") || deleted.Count > 0) config["tabs"] = new JsonArray(mergedTabs.ToArray());

        var hidden = new SortedSet<string>(StringComparer.Ordinal);
        void AppendHidden(JsonNode? node)
        {
            if (node is not JsonArray array) return;
            foreach (var item in array)
            {
                string id = DataJson.Text(item);
                if (!string.IsNullOrWhiteSpace(id)) hidden.Add(id);
            }
        }
        AppendHidden(config["hiddenTabs"]);
        AppendHidden(local["hiddenTabs"]);
        if (local.ContainsKey("hiddenTabs")) config["hiddenTabs"] = new JsonArray(hidden.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
    }

    public static void Save(LibraryStore store, UserState state, params PendingEdit[] edits)
    {
        var next = (JsonObject)state.LocalCatalog.DeepClone();
        foreach (var edit in edits)
        {
            if (edit.Section is not ("gameCategories" or "tabs" or "hiddenTabs" or "deletedTabs" or "reliability")) throw new FormatException("Unsupported local catalog edit.");
            Merge.Set(next, edit);
        }
        Validate(next);
        var previous = state.LocalCatalog;
        state.LocalCatalog = next;
        try { store.Save(state); }
        catch { state.LocalCatalog = previous; throw; }
    }
}
