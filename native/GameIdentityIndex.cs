using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GameLibrary.Native;

/// <summary>
/// Relationships between source rows. By default only verified relationships
/// are edges: a matching display title is intentionally never an edge because
/// editions and unrelated games can share one. Callers that render the library
/// may opt into a display-name edge so duplicate names collapse to one card.
/// </summary>
internal sealed class GameIdentityIndex
{
    private readonly Dictionary<string, string> parents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> reasons = new(StringComparer.Ordinal);

    // Case-insensitive, whitespace-normalized display name used only when a
    // caller asks for the duplicate-name edge (library card rendering).
    internal static string DisplayNameKey(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var builder = new System.Text.StringBuilder(name.Length);
        bool pendingSpace = false;
        foreach (char raw in name.Trim())
        {
            if (char.IsWhiteSpace(raw)) { if (builder.Length > 0) pendingSpace = true; continue; }
            if (pendingSpace) { builder.Append(' '); pendingSpace = false; }
            builder.Append(char.ToUpperInvariant(raw));
        }
        return builder.ToString();
    }

    internal GameIdentityIndex(IEnumerable<Game> games, UserState state,
        IReadOnlyDictionary<string, string>? verifiedDigests = null,
        IReadOnlyDictionary<string, string>? verifiedCanonical = null,
        bool includeDisplayName = false)
    {
        var source = games.ToArray();
        foreach (var game in source)
        {
            parents.TryAdd(game.Id, game.Id);
            reasons.TryAdd(game.Id, new List<string>());
        }
        var byCanonical = new Dictionary<string, string>(StringComparer.Ordinal);
        if (verifiedCanonical != null)
            foreach (var pair in verifiedCanonical.OrderBy(x => x.Key, StringComparer.Ordinal))
                if (parents.ContainsKey(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                {
                    if (byCanonical.TryGetValue(pair.Value, out var other)) Join(pair.Key, other, "verified canonical mapping");
                    else byCanonical[pair.Value] = pair.Key;
                }

        var byProduct = new Dictionary<int, string>();
        var byExecutable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byDigest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in source.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            if (game.MetadataSteamAppId > 0 && !game.MetadataIdentityConflict)
            {
                if (byProduct.TryGetValue(game.MetadataSteamAppId, out var other))
                    Join(game.Id, other, "matching Steam product " + game.MetadataSteamAppId);
                else byProduct[game.MetadataSteamAppId] = game.Id;
            }
            if (state.LaunchPaths.TryGetValue(game.Id, out var selectedPath) && !string.IsNullOrWhiteSpace(selectedPath))
            {
                try
                {
                    string fullPath = Path.GetFullPath(selectedPath);
                    if (Path.IsPathFullyQualified(fullPath) && PathExistsCache.Check(fullPath))
                    {
                        if (byExecutable.TryGetValue(fullPath, out var other)) Join(game.Id, other, "same verified executable");
                        else byExecutable[fullPath] = game.Id;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
            }
            if (verifiedDigests != null && verifiedDigests.TryGetValue(game.Id, out var digest)
                && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && digest.Length == 71)
            {
                if (byDigest.TryGetValue(digest, out var other)) Join(game.Id, other, "same verified image digest");
                else byDigest[digest] = game.Id;
            }
        }
        if (includeDisplayName)
        {
            // Duplicate display names collapse to one card. Numeric/version-only
            // image tags keep their repo:tag name and never merge here.
            var byName = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var game in source.OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                if (game.RequiresGameIdentity) continue;
                string key = DisplayNameKey(game.Name);
                if (key.Length == 0) continue;
                if (byName.TryGetValue(key, out var other)) Join(game.Id, other, "same display name");
                else byName[key] = game.Id;
            }
        }
    }

    internal string CanonicalId(string sourceId) => Find(sourceId);

    internal IReadOnlyList<IReadOnlyList<string>> Groups() => parents.Keys
        .GroupBy(Find, StringComparer.Ordinal)
        .Select(group => (IReadOnlyList<string>)group.OrderBy(x => x, StringComparer.Ordinal).ToArray())
        .OrderBy(group => group[0], StringComparer.Ordinal).ToArray();

    internal IReadOnlyList<string> RelationshipReasons(string sourceId) =>
        reasons.TryGetValue(Find(sourceId), out var values) ? values : Array.Empty<string>();

    private string Find(string sourceId)
    {
        if (!parents.TryGetValue(sourceId, out var parent)) throw new KeyNotFoundException(sourceId);
        if (parent == sourceId) return parent;
        return parents[sourceId] = Find(parent);
    }

    private void Join(string left, string right, string reason)
    {
        string first = Find(left), second = Find(right);
        if (first == second) return;
        string keep = StringComparer.Ordinal.Compare(first, second) <= 0 ? first : second;
        string merge = keep == first ? second : first;
        parents[merge] = keep;
        reasons[keep].AddRange(reasons[merge]);
        reasons[keep].Add(reason);
        reasons.Remove(merge);
    }
}
