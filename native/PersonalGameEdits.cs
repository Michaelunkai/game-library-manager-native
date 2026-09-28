using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace GameLibrary.Native;

internal static class PersonalGameEdits
{
    internal static void Save(LibraryStore store, UserState state, string gameId, int rating, string tagText, bool installed, string? categoryId)
    {
        if (string.IsNullOrWhiteSpace(gameId)) throw new ArgumentException("Choose a game to edit.");
        if (rating is < 0 or > 5) throw new ArgumentException("Choose a rating between zero and five.");
        var tags = tagText.Split(',').Select(tag => tag.Trim()).Where(tag => tag.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (tags.Any(tag => tag.Length > 80)) throw new ArgumentException("Keep each tag under 80 characters.");

        // Stage every field together. Failed validation or disk writes must not
        // leave a rejected rating/installed flag available to a later sync save.
        var next = DataJson.Read<UserState>(DataJson.Write(state));
        next.Ratings[gameId] = rating;
        next.GameTags[gameId] = tags;
        if (installed) next.InstalledGames.Add(gameId); else next.InstalledGames.Remove(gameId);
        if (categoryId != null)
            LocalCatalogEdits.Save(store, next, new PendingEdit { Section = "gameCategories", Key = gameId, After = JsonValue.Create(categoryId) });
        else store.Save(next);

        // Retain the UserState object held by active sync operations. Publish
        // only the committed fields, without comparing against a stale Game row.
        state.Ratings[gameId] = rating;
        state.GameTags[gameId] = tags;
        if (installed) state.InstalledGames.Add(gameId); else state.InstalledGames.Remove(gameId);
        state.LocalCatalog = next.LocalCatalog;
    }
}
