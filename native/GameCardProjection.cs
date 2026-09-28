using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace GameLibrary.Native;

internal sealed record GameCardVersionSelection(
    string CanonicalId,
    IReadOnlyList<string> SourceIds,
    string PublishedRepresentativeId,
    string? PlayableInstallationId,
    bool PublicationFreshnessVerified,
    IReadOnlyList<string> RelationshipReasons)
{
    internal string ActionTargetId => PlayableInstallationId ?? PublishedRepresentativeId;
}

internal static class GameCardProjection
{
    internal static IReadOnlyList<Game> ProjectCards(
        IEnumerable<Game> games,
        UserState state,
        GameIdentityIndex index,
        IReadOnlyDictionary<string, DateTimeOffset> authoritativePushTimes,
        IReadOnlyDictionary<string, string>? lastVerifiedRepresentatives = null)
    {
        var sourceRecords = games.ToArray();
        var byId = sourceRecords.GroupBy(game => game.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var result = new List<Game>();
        foreach (var selection in Select(sourceRecords, state, index, authoritativePushTimes, lastVerifiedRepresentatives))
        {
            var action = byId[selection.ActionTargetId];
            var published = byId[selection.PublishedRepresentativeId];
            var versions = sourceRecords.Where(game => selection.SourceIds.Contains(game.Id, StringComparer.Ordinal)).ToArray();
            if (selection.SourceIds.Count == 1 && versions.Length == 1)
            {
                result.Add(action);
                continue;
            }

            var card = new Game
            {
                SourceRecords = versions,
                ActionTarget = action,
                PublishedRepresentative = published.DockerImage.Length > 0 ? published : null,
                PublicationFreshnessVerified = selection.PublicationFreshnessVerified,
                CanonicalCardId = selection.CanonicalId,
                IdentityRelationshipReasons = selection.RelationshipReasons
            };
            CopyCardValues(card, published, action, versions, state);
            SubscribeToSources(card, published, action, versions, state);
            result.Add(card);
        }
        return result;
    }

    internal static void Detach(Game card)
    {
        foreach (var subscription in card.ProjectionSubscriptions.ToArray())
            subscription.Source.PropertyChanged -= subscription.Handler;
        card.ProjectionSubscriptions.Clear();
    }

    internal static void Detach(IEnumerable<Game> cards)
    {
        foreach (var card in cards) Detach(card);
    }

    internal static Game? SelectWandSource(Game card, UserState state, IReadOnlySet<string> included)
    {
        var sources = card.SourceRecords.Count > 0 ? card.SourceRecords : new[] { card };
        return sources.Where(source => source.CanPlayWithWand
                && included.Contains(source.Id)
                && state.LaunchPaths.TryGetValue(source.Id, out var path)
                && !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .OrderByDescending(source => string.Equals(source.Id, card.ActionTarget?.Id ?? card.Id, StringComparison.Ordinal))
            .ThenBy(source => source.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    internal static IReadOnlyList<GameCardVersionSelection> Select(
        IEnumerable<Game> games,
        UserState state,
        GameIdentityIndex index,
        IReadOnlyDictionary<string, DateTimeOffset> authoritativePushTimes,
        IReadOnlyDictionary<string, string>? lastVerifiedRepresentatives = null)
    {
        var byId = games.GroupBy(game => game.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var result = new List<GameCardVersionSelection>();
        foreach (var group in index.Groups())
        {
            string canonical = index.CanonicalId(group[0]);
            var published = group.Where(id => byId[id].DockerImage.Length > 0).ToArray();
            var dated = published.Where(authoritativePushTimes.ContainsKey)
                .OrderByDescending(id => authoritativePushTimes[id])
                .ThenBy(id => id, StringComparer.Ordinal).ToArray();
            bool fresh = published.Length > 0 && dated.Length == published.Length;
            // When the freshest Docker Hub snapshot is unavailable, fall back to
            // the durable per-tag push time (Added) so the most recently pushed
            // duplicate still wins instead of an arbitrary ordinal row.
            var byPushFallback = published.Where(id => byId[id].Added != default)
                .OrderByDescending(id => byId[id].Added).ThenBy(id => id, StringComparer.Ordinal).ToArray();
            string representative = fresh
                ? dated[0]
                : byPushFallback.Length > 0 ? byPushFallback[0]
                : lastVerifiedRepresentatives != null
                    && lastVerifiedRepresentatives.TryGetValue(canonical, out var retained)
                    && published.Contains(retained, StringComparer.Ordinal) ? retained
                : published.OrderBy(id => id, StringComparer.Ordinal).FirstOrDefault() ?? group[0];
            string? playable = group.Where(id => state.InstalledGames.Contains(id)
                    && state.LaunchPaths.TryGetValue(id, out var path) && !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .OrderByDescending(id => state.LastPlayedUtc.GetValueOrDefault(id))
                .ThenBy(id => state.LaunchPaths.GetValueOrDefault(id), StringComparer.OrdinalIgnoreCase)
                .ThenBy(id => id, StringComparer.Ordinal).FirstOrDefault();
            result.Add(new GameCardVersionSelection(canonical, group, representative, playable,
                fresh, index.RelationshipReasons(canonical)));
        }
        return result;
    }

    private static void CopyCardValues(Game card, Game published, Game action, IReadOnlyList<Game> versions, UserState state)
    {
        // Published fields describe the candidate image; local identity and
        // personal fields stay tied to the game the user selected or used.
        card.Id = action.Id;
        card.Name = action.Name;
        card.Category = action.Category;
        card.Image = published.Image;
        card.DockerImage = published.DockerImage;
        card.DockerImageUrl = published.DockerImageUrl;
        card.Description = published.Description;
        card.Details = published.Details;
        card.Time = published.Time;
        card.TimeVerifiedAt = published.TimeVerifiedAt;
        card.SizeGb = published.SizeGb;
        card.DiskRequirementGb = published.DiskRequirementGb;
        card.InstalledStorageLabel = action.InstalledStorageLabel;
        card.InstalledStorageDetail = action.InstalledStorageDetail;
        card.Discovered = published.Discovered;
        card.IsLocal = action.IsLocal;
        card.IsNonGame = action.IsNonGame;
        card.MetadataLookupTitle = action.MetadataLookupTitle;
        card.MetadataSteamAppId = action.MetadataSteamAppId;
        card.MetadataIdentityConflict = action.MetadataIdentityConflict;
        card.Added = published.Added;
        card.Cover = published.Cover;
        card.CategoryName = action.CategoryName;
        card.TagsLabel = string.Join("  ·  ", versions
            .SelectMany(game => state.GameTags.GetValueOrDefault(game.Id, new List<string>()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase));
        card.PlayedHours = action.PlayedHours;
        card.LastPlayedUtc = versions.Max(game => game.LastPlayedUtc);
        card.IsPlaying = action.IsPlaying;
        card.IsPlayPaused = action.IsPlayPaused;
        card.IsPauseStateUnknown = action.IsPauseStateUnknown;
        card.ProgressLabel = action.ProgressLabel;
        card.ProgressDetail = action.ProgressDetail;
        // A playable local version need not be the version registered in Wand.
        // Keep the single card eligible when any linked source has an exact
        // registration; the click handler resolves that source independently.
        card.CanPlayWithWand = versions.Any(version => version.CanPlayWithWand);
        card.ShowTime = action.ShowTime;
        card.ShowCategory = action.ShowCategory;
        card.CoverHeight = action.CoverHeight;
        card.Rating = action.Rating;
        card.Wishlisted = versions.Any(game => game.Wishlisted || state.Wishlist.Contains(game.Id));
        card.Installed = action.Installed;
        card.Selected = versions.Any(game => game.Selected);
    }

    private static void SubscribeToSources(Game card, Game published, Game action, IReadOnlyList<Game> versions, UserState state)
    {
        foreach (var source in versions.Distinct())
        {
            PropertyChangedEventHandler handler = (_, _) =>
            {
                CopyCardValues(card, published, action, versions, state);
                card.Notify("");
            };
            source.PropertyChanged += handler;
            card.ProjectionSubscriptions.Add((source, handler));
        }
    }
}
