using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GameLibrary.Native;

internal static class TagMigrationServiceTests
{
    internal static void Run(string root)
    {
        Directory.CreateDirectory(root);
        void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        var hidden = new HashSet<string>(TagTaxonomy.DestinationCategoryIds, StringComparer.Ordinal);
        var visibleCategories = new HashSet<string>(StringComparer.Ordinal)
        {
            "all", "wishlist", "installed", "new", "2d", "finished", "not_for_me", "meh", "hyperv", "rpg", "action"
        };

        static TagMigrationRequest Request(params (string Id, string[] Tags)[] games)
        {
            var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach ((string id, string[] tags) in games) map[id] = tags;
            return new TagMigrationRequest(map);
        }

        TagMigrationRequest library = Request(
            ("game-a", new[] { "rpg", "movie", "anime" }),
            ("game-b", new[] { "tvshow", "soundtrack" }),
            ("game-c", new[] { "ebook" }),
            ("game-d", new[] { "fps" }));

        MigrationPlan plan = TagMigrationService.Plan(library);

        Require(plan.Moves.Count == 5, $"Expected 5 moves for movie/anime/tvshow/soundtrack/ebook but got {plan.Moves.Count}.");
        foreach (TagMove move in plan.Moves)
        {
            Require(hidden.Contains(move.To), $"Move '{move.Tag}' targeted '{move.To}', which is not one of the seven hidden destinations.");
            Require(move.To.StartsWith("hidden-", StringComparison.Ordinal), $"Move '{move.Tag}' targeted non-hidden category '{move.To}'.");
            Require(!visibleCategories.Contains(move.To), $"Move '{move.Tag}' targeted the visible game category '{move.To}'.");
        }

        Require(plan.DestinationCategoryIds.All(hidden.Contains), "A destination category was not one of the seven hidden destinations.");
        Require(!plan.DestinationCategoryIds.Contains("rpg", StringComparer.Ordinal), "A visible game category id was used as a destination.");
        Require(TagMigrationService.IsSafe(plan), "A plan that only targets hidden destinations must be safe.");

        Require(plan.GamesByMove["movie"].SequenceEqual(new[] { "game-a" }), "movie must touch only game-a.");
        Require(plan.GamesByMove["anime"].SequenceEqual(new[] { "game-a" }), "anime must touch only game-a.");
        Require(plan.GamesByMove["tvshow"].SequenceEqual(new[] { "game-b" }), "tvshow must touch only game-b.");
        Require(plan.GamesByMove["soundtrack"].SequenceEqual(new[] { "game-b" }), "soundtrack must touch only game-b.");
        Require(plan.GamesByMove["ebook"].SequenceEqual(new[] { "game-c" }), "ebook must touch only game-c.");
        Require(plan.AffectedGameIds.SequenceEqual(new[] { "game-a", "game-b", "game-c" }), "The affected game set is wrong.");
        Require(plan.ChangedGameCount == 3 && plan.UnchangedGameCount == 1, "Changed/unchanged game counts are wrong.");
        Require(plan.DestinationByGame["game-a"] == "hidden-movies", "game-a must resolve to hidden-movies by canonical destination priority.");
        Require(plan.DestinationByGame["game-b"] == "hidden-tv", "game-b must resolve to hidden-tv by canonical destination priority.");

        IReadOnlyList<PendingEdit> edits = TagMigrationService.ToEdits(plan);
        Require(edits.Count == plan.AffectedGameIds.Count, "ToEdits must emit exactly one edit per affected game.");
        foreach (PendingEdit edit in edits)
        {
            Require(edit.Section == "gameCategories", $"ToEdits used section '{edit.Section}' instead of the real section 'gameCategories'.");
            Require(plan.AffectedGameIds.Contains(edit.Key, StringComparer.Ordinal), $"ToEdits keyed edit '{edit.Key}' which is not an affected game id.");
            Require(edit.After != null && hidden.Contains(edit.After.GetValue<string>()), "ToEdits wrote a non-hidden destination category.");
        }
        Require(edits.Select(edit => edit.Key).OrderBy(key => key, StringComparer.Ordinal).SequenceEqual(plan.AffectedGameIds.OrderBy(id => id, StringComparer.Ordinal)),
            "ToEdits keys do not match the affected game ids.");

        string description = TagMigrationService.Describe(plan);
        foreach (string destination in plan.DestinationCategoryIds)
            Require(description.Contains(destination, StringComparison.Ordinal), $"Describe did not name destination '{destination}'.");
        foreach (string gameId in plan.AffectedGameIds)
            Require(description.Contains(gameId, StringComparison.Ordinal), $"Describe did not name affected game '{gameId}'.");
        Require(description.Contains("movie", StringComparison.Ordinal) && description.Contains("soundtrack", StringComparison.Ordinal),
            "Describe did not name the moving tags.");

        var unsafeMove = new TagMove("rogue", TagTaxonomy.VisibleSourceId, "rpg", TagKinds.Game, "tampered");
        var unsafePlan = new MigrationPlan(
            new[] { unsafeMove },
            new[] { "game-a" },
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["rogue"] = new[] { "game-a" } },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["game-a"] = "rpg" },
            new[] { "rpg" },
            1,
            0);
        Require(!TagMigrationService.IsSafe(unsafePlan), "IsSafe must be false when a move targets a non-hidden category id.");
        Require(!TagMigrationService.IsSafe(new MigrationPlan(
            new[] { new TagMove("rogue", TagTaxonomy.VisibleSourceId, "hidden-movies", TagKinds.Movie, "looks fine") },
            new[] { "game-a" },
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["rogue"] = new[] { "game-a" } },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["game-a"] = "finished" },
            new[] { "hidden-movies" },
            1,
            0)), "IsSafe must be false when an affected-game destination is a visible category.");

        MigrationPlan migrated = TagMigrationService.Plan(Request(
            ("game-a", new[] { "hidden-movies", "rpg" }),
            ("game-b", new[] { "hidden-audio", "hidden-tv" })));
        Require(migrated.Moves.Count == 0 && migrated.ChangedGameCount == 0, "Re-planning an already-migrated library must return an empty plan.");
        Require(migrated.UnchangedGameCount == 2, "An empty plan must report every game as unchanged.");

        MigrationPlan unknown = TagMigrationService.Plan(Request(("game-a", new[] { "totally-unknown-tag-xyz", "rpg" })));
        Require(unknown.Moves.Count == 0, "An unknown tag must yield no move (fail-closed).");

        MigrationPlan empty = TagMigrationService.Plan(Request(("game-a", new[] { "rpg", "fps" })));
        Require(empty.Moves.Count == 0 && empty.AffectedGameIds.Count == 0, "A library with no non-game tags must produce no moves.");
        Require(TagMigrationService.IsSafe(empty), "A plan with zero affected games must be safe.");
        Require(TagMigrationService.ToEdits(empty).Count == 0, "A plan with zero affected games must produce zero edits.");
        Require(TagMigrationService.Describe(empty).Length > 0, "Describe must handle an empty plan.");

        MigrationPlan heuristic = TagMigrationService.Plan(Request(("game-a", new[] { "repack", "complete" })));
        Require(heuristic.Moves.Count == 0, "Heuristic-only tags must not move by default.");

        MigrationPlan custom = TagMigrationService.Plan(new TagMigrationRequest(
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["game-a"] = new[] { "movie" } },
            new HashSet<string>(StringComparer.Ordinal) { "hidden-custom" }));
        Require(custom.Moves.Count == 0, "A move is only produced when its destination is in the supplied hidden set.");
    }
}
