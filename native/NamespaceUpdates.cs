using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace GameLibrary.Native;

// Independently verified repository snapshots, never a claim of full namespace success.
internal static class NamespaceUpdates
{
    internal static JsonObject Combine(string owner, JsonObject? baseline, params JsonObject?[] sources)
    {
        DateTimeOffset cutoff = DateTimeOffset.MinValue;
        if (DataJson.Text(baseline?["namespace"]) == owner && baseline?["complete"]?.ToString() == "true")
            DateTimeOffset.TryParse(DataJson.Text(baseline["fetchedAt"]), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out cutoff);
        var verified = new Dictionary<string, (DateTimeOffset At, JsonObject Row)>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (source == null || DataJson.Text(source["namespace"]) != owner || source["repositories"] is not JsonArray rows) continue;
            foreach (var row in rows.OfType<JsonObject>())
            {
                try
                {
                    string repository = DataJson.Text(row["repository"]);
                    if (!DockerIdentity.ValidRepository(repository) || !repository.StartsWith(owner + "/", StringComparison.Ordinal)
                        || row["complete"]?.ToString() != "true" || row["stale"]?.ToString() != "false"
                        || !DateTimeOffset.TryParse(DataJson.Text(row["fetchedAt"]), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                        || at <= cutoff) continue;
                    var check = row.DeepClone().AsObject(); check["success"] = true;
                    if (!SyncClient.CompleteTagSnapshot(check, repository)) continue;
                    if (!verified.TryGetValue(repository, out var old) || at > old.At) verified[repository] = (at, row);
                }
                catch (InvalidOperationException) { /* A malformed row cannot suppress healthy siblings. */ }
            }
        }
        return new JsonObject { ["namespace"] = owner, ["complete"] = false, ["success"] = false,
            ["repositoryCount"] = verified.Count,
            ["repositories"] = new JsonArray(verified.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => (JsonNode)x.Value.Row.DeepClone()).ToArray()) };
    }

    internal static void Apply(List<Game> games, JsonObject updates)
    {
        // Reuse the strict all-rows validation on this explicitly scoped subset.
        // The persisted sidecar itself remains incomplete and cannot replace a baseline.
        var subset = updates.DeepClone().AsObject(); subset["success"] = true; subset["complete"] = true;
        LibraryStore.MergeNamespace(games, subset);
    }
}
