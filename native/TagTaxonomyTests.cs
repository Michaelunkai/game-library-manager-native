using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GameLibrary.Native;

internal static class TagTaxonomyTests
{
    internal static void Run(string root)
    {
        Directory.CreateDirectory(root);
        void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        var allowedKinds = new HashSet<string>(StringComparer.Ordinal)
        {
            "Game", "Movie", "Tv", "Documentary", "Anime", "Audio", "Book", "Software", "Dlc", "Mod", "Rom", "Other"
        };
        var allowedDest = new HashSet<string>(TagTaxonomy.DestinationCategoryIds, StringComparer.Ordinal);
        var visibleCategories = new HashSet<string>(StringComparer.Ordinal)
        {
            "all", "wishlist", "installed", "new", "2d", "finished", "not_for_me", "meh", "hyperv", "rpg", "action"
        };

        IReadOnlyList<TagClassification> catalog = TagTaxonomy.Catalog();
        Require(catalog.Count >= 120, $"The catalog must contain at least 120 distinct tags but contained {catalog.Count}.");

        var seenTags = new HashSet<string>(StringComparer.Ordinal);
        foreach (TagClassification entry in catalog)
        {
            Require(!string.IsNullOrWhiteSpace(entry.Tag), "A catalog entry has an empty tag.");
            Require(entry.Tag == entry.Tag.Trim().ToLowerInvariant(), $"Catalog tag '{entry.Tag}' is not normalized lowercased/trimmed.");
            Require(seenTags.Add(entry.Tag), $"Catalog tag '{entry.Tag}' is duplicated.");
            Require(allowedKinds.Contains(entry.Kind), $"Catalog tag '{entry.Tag}' has unknown kind '{entry.Kind}'.");
            Require(!string.IsNullOrWhiteSpace(entry.Rationale), $"Catalog tag '{entry.Tag}' has an empty rationale.");
            Require(allowedDest.Contains(entry.TargetCategoryId), $"Catalog tag '{entry.Tag}' targets '{entry.TargetCategoryId}', which is not an allowed hidden destination.");
            Require(entry.TargetCategoryId.StartsWith("hidden-", StringComparison.Ordinal), $"Catalog tag '{entry.Tag}' targets a non-hidden category '{entry.TargetCategoryId}'.");
            Require(!visibleCategories.Contains(entry.TargetCategoryId), $"Catalog tag '{entry.Tag}' targets the visible game category '{entry.TargetCategoryId}'.");
        }

        foreach (string destination in TagTaxonomy.DestinationCategoryIds)
        {
            Require(destination.StartsWith("hidden-", StringComparison.Ordinal), $"Destination '{destination}' is not a hidden category.");
            Require(!visibleCategories.Contains(destination), $"Destination '{destination}' collides with a visible game category.");
        }
        Require(TagTaxonomy.IsDestinationCategoryId("hidden-movies"), "hidden-movies must be recognized as a destination.");
        Require(!TagTaxonomy.IsDestinationCategoryId("rpg"), "A game category must never be treated as a hidden destination.");

        void RequireMove(string tag, string expectedTo)
        {
            IReadOnlyList<TagMove> moves = TagTaxonomy.PlanMoves(new[] { tag }, allowedDest);
            Require(moves.Count == 1, $"Expected exactly one move for '{tag}' but got {moves.Count}.");
            Require(moves[0].Tag == TagTaxonomy.Normalize(tag), $"Move for '{tag}' carried tag '{moves[0].Tag}'.");
            Require(moves[0].To == expectedTo, $"Tag '{tag}' moved to '{moves[0].To}' instead of '{expectedTo}'.");
            Require(moves[0].From == TagTaxonomy.VisibleSourceId, $"Move for '{tag}' had unexpected origin '{moves[0].From}'.");
            Require(!string.IsNullOrWhiteSpace(moves[0].Rationale), $"Move for '{tag}' had an empty rationale.");
        }

        RequireMove("movie", "hidden-movies");
        RequireMove("film", "hidden-movies");
        RequireMove("documentary", "hidden-movies");
        RequireMove("tvshow", "hidden-tv");
        RequireMove("season", "hidden-tv");
        RequireMove("anime", "hidden-anime");
        RequireMove("soundtrack", "hidden-audio");
        RequireMove("flac", "hidden-audio");
        RequireMove("ebook", "hidden-books");
        RequireMove("manga", "hidden-books");
        RequireMove("crack", "hidden-software");
        RequireMove("iso", "hidden-software");
        RequireMove("dlc", "hidden-other");
        RequireMove("mod", "hidden-other");
        RequireMove("rom", "hidden-other");
        RequireMove("fitgirl", "hidden-other");

        foreach (string gameTag in new[] { "rpg", "fps", "soulslike", "roguelike", "co-op", "indie", "action" })
        {
            Require(TagTaxonomy.IsGameTag(gameTag), $"'{gameTag}' must be recognized as a genuine game tag.");
            Require(TagTaxonomy.PlanMoves(new[] { gameTag }, allowedDest).Count == 0, $"Genuine game tag '{gameTag}' must never be moved.");
        }

        Require(TagTaxonomy.PlanMoves(new[] { "totally-unknown-tag-xyz" }, allowedDest).Count == 0, "An unknown tag must not be moved (fail-closed).");
        Require(!TagTaxonomy.IsGameTag("totally-unknown-tag-xyz"), "An unknown tag must not be treated as a game tag.");
        Require(!TagTaxonomy.TryClassify("totally-unknown-tag-xyz", out _), "TryClassify must fail closed for an unknown tag.");
        Require(TagTaxonomy.PlanMoves(new[] { "movie" }, new HashSet<string>(StringComparer.Ordinal)).Count == 0, "A destination missing from the hidden set must not be assigned.");

        Require(TagTaxonomy.PlanMoves(TagTaxonomy.DestinationCategoryIds, allowedDest).Count == 0, "Planning an already-moved tag set must return nothing.");
        IReadOnlyList<TagMove> firstPlan = TagTaxonomy.PlanMoves(new[] { "movie", "anime", "crack" }, allowedDest);
        IReadOnlyList<TagMove> secondPlan = TagTaxonomy.PlanMoves(new[] { "movie", "anime", "crack" }, allowedDest);
        Require(firstPlan.Count == secondPlan.Count && firstPlan.Select(m => m.Tag + ">" + m.To).SequenceEqual(secondPlan.Select(m => m.Tag + ">" + m.To)), "PlanMoves must be deterministic and idempotent for the same input.");

        RequireMove("  MOVIE ", "hidden-movies");
        RequireMove("FlAc", "hidden-audio");
        Require(TagTaxonomy.PlanMoves(new[] { " Movie ", "movie" }, allowedDest).Count == 1, "Tags differing only by case/whitespace must collapse to one move.");
        Require(TagTaxonomy.Normalize("  Aa  ") == "aa", "Normalize must trim and lowercase.");

        int certain = 0, heuristic = 0;
        foreach (TagClassification entry in catalog)
        {
            if (entry.HeuristicOnly)
            {
                heuristic++;
                continue;
            }
            certain++;
            IReadOnlyList<TagMove> moves = TagTaxonomy.PlanMoves(new[] { entry.Tag }, allowedDest);
            Require(moves.Count == 1 && moves[0].To == entry.TargetCategoryId, $"Certain entry '{entry.Tag}' was not reachable by PlanMoves.");
            Require(TagTaxonomy.PlanMoves(new[] { entry.Tag }, allowedDest, includeHeuristics: true).Count == 1, $"Certain entry '{entry.Tag}' must also be reachable when heuristics are included.");
        }
        Require(certain > 0, "The catalog must contain at least one certain entry.");
        Require(heuristic > 0, "The catalog must contain at least one heuristic entry.");
        Require(certain + heuristic == catalog.Count, "Every catalog entry must be counted as certain or heuristic.");

        foreach (string ambiguous in new[]
        {
            "repack", "complete", "collection", "remastered", "proper", "internal", "port", "update", "demo",
            "beta", "patch", "plugin", "ep", "single", "trainer", "bundle", "compilation", "sub", "batch",
            "limited", "strategy-guide"
        })
        {
            TagClassification? entry = catalog.SingleOrDefault(c => c.Tag == ambiguous);
            Require(entry != null, $"Ambiguous tag '{ambiguous}' must exist in the catalog.");
            Require(entry!.HeuristicOnly, $"Ambiguous tag '{ambiguous}' must be flagged HeuristicOnly, not certain.");
            Require(TagTaxonomy.IsHeuristicOnly(ambiguous), $"IsHeuristicOnly('{ambiguous}') must be true.");
            Require(TagTaxonomy.PlanMoves(new[] { ambiguous }, allowedDest).Count == 0, $"Heuristic tag '{ambiguous}' must not move by default.");
            Require(TagTaxonomy.PlanMoves(new[] { ambiguous }, allowedDest, includeHeuristics: true).Count == 1, $"Heuristic tag '{ambiguous}' must move when heuristics are explicitly included.");
        }

        foreach (string certainTag in new[] { "movie", "anime", "flac", "ebook", "crack", "iso" })
        {
            Require(!TagTaxonomy.IsHeuristicOnly(certainTag), $"'{certainTag}' must be classified as certain, not heuristic.");
            Require(!catalog.Single(c => c.Tag == certainTag).HeuristicOnly, $"'{certainTag}' must not carry the heuristic flag.");
        }

        IReadOnlyList<TagMove> described = TagTaxonomy.PlanMoves(new[] { "movie", "film", "anime", "crack" }, allowedDest);
        string summary = TagTaxonomy.DescribePlan(described);
        Require(summary.Contains("hidden-movies", StringComparison.Ordinal), "DescribePlan must name the movies destination.");
        Require(summary.Contains("hidden-anime", StringComparison.Ordinal), "DescribePlan must name the anime destination.");
        Require(summary.Contains("hidden-software", StringComparison.Ordinal), "DescribePlan must name the software destination.");
        Require(summary.Contains("4", StringComparison.Ordinal), "DescribePlan must state the total tag count.");
        Require(TagTaxonomy.DescribePlan(Array.Empty<TagMove>()).Length > 0, "DescribePlan must handle an empty plan.");

        Require(TagTaxonomy.TryClassify(" MOVIE ", out TagClassification? classified) && classified!.TargetCategoryId == "hidden-movies", "TryClassify must normalize and classify a known tag.");
    }
}
