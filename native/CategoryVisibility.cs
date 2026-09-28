using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace GameLibrary.Native;

public readonly record struct CategoryVisibilityRule(bool HideTab, bool HideGamesFromAll);

/// <summary>Versioned, per-category local visibility preferences and category-list reconciliation.</summary>
public static class CategoryVisibility
{
    public const int CurrentSchemaVersion = 1;
    private const string ReliabilityKey = "reliability";
    private const string VisibilityKey = "categoryVisibility";
    private const string RulesKey = "categories";

    public static bool EnsureMigrated(LibraryStore store, UserState state, JsonObject effectiveConfig)
    {
        JsonObject reliability = CloneObject(state.LocalCatalog[ReliabilityKey]);
        JsonObject visibility = CloneObject(reliability[VisibilityKey]);
        JsonObject rules = CloneObject(visibility[RulesKey]);
        bool changed = state.LocalCatalog[ReliabilityKey] is not JsonObject
            || reliability[VisibilityKey] is not JsonObject
            || visibility[RulesKey] is not JsonObject;
        int schemaVersion = Integer(visibility["schemaVersion"]);
        if (schemaVersion < CurrentSchemaVersion)
        {
            foreach (string id in LegacyHiddenIds(effectiveConfig, state.LocalCatalog))
            {
                if (rules.ContainsKey(id)) continue;
                rules[id] = RuleJson(new CategoryVisibilityRule(true, true));
                changed = true;
            }
            visibility["schemaVersion"] = CurrentSchemaVersion;
            changed = true;
        }
        foreach (string id in LegacyHiddenIds(effectiveConfig, state.LocalCatalog))
        {
            if (rules.ContainsKey(id)) continue;
            rules[id] = RuleJson(new CategoryVisibilityRule(true, true));
            changed = true;
        }
        if (changed)
        {
            visibility[RulesKey] = rules;
            reliability[VisibilityKey] = visibility;
        }
        if (Integer(reliability["schemaVersion"]) < CurrentSchemaVersion)
        { reliability["schemaVersion"] = CurrentSchemaVersion; changed = true; }
        if (!changed) return false;
        LocalCatalogEdits.Save(store, state, new PendingEdit { Section = ReliabilityKey, After = reliability });
        return true;
    }

    public static CategoryVisibilityRule Get(UserState state, JsonObject effectiveConfig, string categoryId)
    {
        var reliability = state.LocalCatalog[ReliabilityKey] as JsonObject;
        var visibility = reliability?[VisibilityKey] as JsonObject;
        var rules = visibility?[RulesKey] as JsonObject;
        bool legacy = LegacyHiddenIds(effectiveConfig, state.LocalCatalog).Contains(categoryId);
        var item = rules?[categoryId] as JsonObject;
        return new CategoryVisibilityRule(ReadBool(item?["hideTab"], legacy), ReadBool(item?["hideGamesFromAll"], legacy));
    }

    public static List<Category> CompleteDefinitions(IEnumerable<Category> definitions, JsonObject config, UserState state)
    {
        var output = new List<Category>();
        var known = new HashSet<string>(StringComparer.Ordinal);
        void Add(string id, string name)
        {
            if (string.IsNullOrWhiteSpace(id) || id is "all" or "wishlist" or "installed" || !known.Add(id)) return;
            output.Add(new Category(id, string.IsNullOrWhiteSpace(name) ? id : name));
        }
        foreach (var category in definitions) Add(category.Id, category.Name);
        Add("new", "New arrivals");

        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var deleted = state.LocalCatalog["deletedTabs"] is JsonArray tombstones
            ? tombstones.Select(item => DataJson.Text(item)).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        if (config["gameCategories"] is JsonObject assignments)
            foreach (var entry in assignments)
                if (entry.Value is JsonValue value && value.TryGetValue<string>(out var id) && !string.IsNullOrWhiteSpace(id) && !known.Contains(id) && !deleted.Contains(id)) missing.Add(id);
        foreach (var entry in state.LocalGames.Values.Concat(state.WandGames.Values))
            if (!string.IsNullOrWhiteSpace(entry.Category) && !known.Contains(entry.Category) && !deleted.Contains(entry.Category)) missing.Add(entry.Category);
        foreach (string id in LegacyHiddenIds(config, state.LocalCatalog))
            if (!known.Contains(id) && !deleted.Contains(id)) missing.Add(id);
        if (state.LocalCatalog[ReliabilityKey] is JsonObject reliability
            && reliability[VisibilityKey] is JsonObject visibility && visibility[RulesKey] is JsonObject savedRules)
            foreach (var rule in savedRules)
                if (!known.Contains(rule.Key) && !deleted.Contains(rule.Key)) missing.Add(rule.Key);
        foreach (string id in missing) Add(id, id);
        return output;
    }

    public static void SaveRules(LibraryStore store, UserState state, JsonObject effectiveConfig,
        IEnumerable<Category> categories, string categoryId, CategoryVisibilityRule updated, params PendingEdit[] additionalEdits)
    {
        SaveRules(store, state, effectiveConfig, categories, new Dictionary<string, CategoryVisibilityRule>(StringComparer.Ordinal)
        { [categoryId] = updated }, additionalEdits);
    }

    public static void ShowAllTabs(LibraryStore store, UserState state, JsonObject effectiveConfig, IEnumerable<Category> categories) =>
        UpdateMany(store, state, effectiveConfig, categories, current => current with { HideTab = false });

    public static void ShowAllGamesInAll(LibraryStore store, UserState state, JsonObject effectiveConfig, IEnumerable<Category> categories) =>
        UpdateMany(store, state, effectiveConfig, categories, current => current with { HideGamesFromAll = false });

    private static void UpdateMany(LibraryStore store, UserState state, JsonObject config, IEnumerable<Category> categories,
        Func<CategoryVisibilityRule, CategoryVisibilityRule> update)
    {
        var changes = categories.ToDictionary(c => c.Id, c => update(Get(state, config, c.Id)), StringComparer.Ordinal);
        SaveRules(store, state, config, categories, changes);
    }

    private static void SaveRules(LibraryStore store, UserState state, JsonObject config, IEnumerable<Category> categories,
        IReadOnlyDictionary<string, CategoryVisibilityRule> changes, params PendingEdit[] additionalEdits)
    {
        var local = (JsonObject)state.LocalCatalog.DeepClone();
        var reliability = CloneObject(local[ReliabilityKey]);
        var visibility = CloneObject(reliability[VisibilityKey]);
        var rules = CloneObject(visibility[RulesKey]);
        foreach (var change in changes)
            rules[change.Key] = RuleJson(change.Value);
        visibility["schemaVersion"] = Math.Max(CurrentSchemaVersion, Integer(visibility["schemaVersion"]));
        visibility[RulesKey] = rules;
        reliability["schemaVersion"] = Math.Max(CurrentSchemaVersion, Integer(reliability["schemaVersion"]));
        reliability[VisibilityKey] = visibility;
        local[ReliabilityKey] = reliability;

        // Keep the legacy list as a conservative rollback mirror. This build reads the
        // versioned independent settings above, so the list no longer drives filtering.
        var hidden = LocalHiddenIds(local);
        foreach (var change in changes)
        {
            if (change.Value.HideTab || change.Value.HideGamesFromAll) hidden.Add(change.Key);
            else hidden.Remove(change.Key);
        }
        local["hiddenTabs"] = new JsonArray(hidden.OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        var edits = new List<PendingEdit>
        {
            new() { Section = ReliabilityKey, After = reliability },
            new() { Section = "hiddenTabs", After = local["hiddenTabs"] }
        };
        edits.AddRange(additionalEdits);
        LocalCatalogEdits.Save(store, state, edits.ToArray());
    }

    public static HashSet<string> LegacyHiddenIds(JsonObject effectiveConfig, JsonObject localCatalog)
    {
        var ids = LocalHiddenIds(localCatalog);
        if (effectiveConfig["hiddenTabs"] is JsonArray remote)
            foreach (var value in remote)
            {
                string id = DataJson.Text(value);
                if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
            }
        return ids;
    }

    private static HashSet<string> LocalHiddenIds(JsonObject localCatalog) => localCatalog["hiddenTabs"] is JsonArray local
        ? local.Select(item => DataJson.Text(item)).Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal)
        : new HashSet<string>(StringComparer.Ordinal);

    private static JsonObject RuleJson(CategoryVisibilityRule rule) => new()
    {
        ["hideTab"] = rule.HideTab,
        ["hideGamesFromAll"] = rule.HideGamesFromAll
    };

    private static JsonObject CloneObject(JsonNode? node) => node is JsonObject obj ? (JsonObject)obj.DeepClone() : new JsonObject();
    private static bool ReadBool(JsonNode? node, bool fallback) => node is JsonValue value && value.TryGetValue<bool>(out var result) ? result : fallback;
    private static int Integer(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var result) ? result : 0;
}
