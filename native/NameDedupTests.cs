using System;
using System.Collections.Generic;
using System.Linq;

namespace GameLibrary.Native;

/// <summary>
/// Duplicate display names collapse to one card, and the most recently pushed
/// Docker tag represents it. Identity grouping stays conservative unless a
/// caller explicitly opts into the display-name edge.
/// </summary>
internal static class NameDedupTests
{
    internal static void Run(string root)
    {
        var games = new[]
        {
            new Game { Id = "docker:alpha-old", Name = "Duplicate Title", DockerImage = "repo:alpha-old", Added = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new Game { Id = "docker:alpha-new", Name = "duplicate title", DockerImage = "repo:alpha-new", Added = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) },
            new Game { Id = "docker:beta", Name = "Unique Title", DockerImage = "repo:beta", Added = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc) }
        };
        var state = new UserState();
        var strict = new GameIdentityIndex(games, state);
        if (strict.Groups().Count != 3)
            throw new InvalidOperationException("Display-name grouping must remain opt-in for verified identity.");

        var index = new GameIdentityIndex(games, state, includeDisplayName: true);
        if (index.Groups().Count != 2)
            throw new InvalidOperationException("Duplicate display names did not collapse to one card.");

        var empty = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var selection = GameCardProjection.Select(games, state, index, empty)
            .Single(entry => entry.SourceIds.Contains("docker:alpha-old"));
        if (!selection.SourceIds.Contains("docker:alpha-new") || selection.PublishedRepresentativeId != "docker:alpha-new")
            throw new InvalidOperationException("The most recently pushed duplicate was not selected.");

        var cards = GameCardProjection.ProjectCards(games, state, index, empty);
        try
        {
            if (cards.Count != 2)
                throw new InvalidOperationException("Duplicate sources were not folded into a single card.");
            var folded = cards.Single(card => card.Id == "docker:alpha-new");
            var sources = typeof(Game).GetProperty("SourceRecords", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(folded) as IReadOnlyList<Game>;
            if (sources == null || sources.Count != 2)
                throw new InvalidOperationException("The folded duplicate card lost a source record.");
        }
        finally { GameCardProjection.Detach(cards); }

        // The authoritative Docker Hub push time still wins when it is available.
        var pushed = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal)
        {
            ["docker:alpha-old"] = new DateTimeOffset(2026, 2, 2, 0, 0, 0, TimeSpan.Zero),
            ["docker:alpha-new"] = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)
        };
        var authoritative = GameCardProjection.Select(games, state, index, pushed)
            .Single(entry => entry.SourceIds.Contains("docker:alpha-old"));
        if (authoritative.PublishedRepresentativeId != "docker:alpha-old" || !authoritative.PublicationFreshnessVerified)
            throw new InvalidOperationException("A newer authoritative Docker Hub push did not override the durable fallback.");
    }
}
