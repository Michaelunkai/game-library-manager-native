using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GameLibrary.Native;

internal static class TagMigrationDialogTests
{
    internal static void Run(string root)
    {
        Directory.CreateDirectory(root);
        void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        static TagMigrationRequest Request(params (string Id, string[] Tags)[] games)
        {
            var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach ((string id, string[] tags) in games) map[id] = tags;
            return new TagMigrationRequest(map);
        }

        MigrationPlan normal = TagMigrationService.Plan(Request(
            ("game-a", new[] { "movie", "anime" }),
            ("game-b", new[] { "tvshow" }),
            ("game-c", new[] { "ebook" }),
            ("game-d", new[] { "rpg" })));
        Require(normal.ChangedGameCount == 3, "Fixture must change three games.");

        TagMigrationPresentation normalView = TagMigrationDialog.Present(normal);
        Require(normalView.ConfirmOffered, "A safe plan that changes games must offer confirm.");
        Require(normalView.Emphasis == "Move 3 games into hidden categories",
            $"Normal emphasis was '{normalView.Emphasis}' instead of the exact outcome sentence.");
        Require(normalView.ConfirmLabel == "Move 3 games into hidden categories",
            $"Normal confirm label was '{normalView.ConfirmLabel}' instead of the exact outcome sentence.");
        Require(normalView.CloseLabel == "Cancel", $"Normal close label was '{normalView.CloseLabel}' instead of 'Cancel'.");
        Require(normalView.Title == "Move non-game tags into hidden categories", $"Normal title was '{normalView.Title}'.");
        Require(normalView.Detail.Contains("hidden-movies", StringComparison.Ordinal)
            && normalView.Detail.Contains("hidden-tv", StringComparison.Ordinal)
            && normalView.Detail.Contains("hidden-books", StringComparison.Ordinal),
            "Normal detail must list every destination via TagMigrationService.Describe.");
        Require(normalView.Detail.Contains("game-a", StringComparison.Ordinal) && normalView.Detail.Contains("game-c", StringComparison.Ordinal),
            "Normal detail must name the games affected.");
        Require(!normalView.ConfirmLabel.Contains("Submit", StringComparison.OrdinalIgnoreCase)
            && normalView.ConfirmLabel.StartsWith("Move ", StringComparison.Ordinal),
            "The confirm label must be an active-voice outcome, never a bare 'Submit'.");

        IReadOnlyList<PendingEdit> expectedEdits = TagMigrationService.ToEdits(normal);
        Require(normalView.Edits.Count == expectedEdits.Count, "The presentation must carry one edit per affected game.");
        for (int i = 0; i < expectedEdits.Count; i++)
        {
            Require(normalView.Edits[i].Section == expectedEdits[i].Section
                && normalView.Edits[i].Key == expectedEdits[i].Key
                && normalView.Edits[i].After?.GetValue<string>() == expectedEdits[i].After?.GetValue<string>(),
                "The presentation edits must match TagMigrationService.ToEdits exactly.");
        }

        MigrationPlan single = TagMigrationService.Plan(Request(("only-game", new[] { "movie" })));
        TagMigrationPresentation singleView = TagMigrationDialog.Present(single);
        Require(singleView.ConfirmOffered && singleView.Emphasis == "Move 1 game into hidden categories",
            $"Singular grammar was wrong: '{singleView.Emphasis}'.");

        MigrationPlan empty = TagMigrationService.Plan(Request(("game-a", new[] { "rpg", "fps" })));
        TagMigrationPresentation emptyView = TagMigrationDialog.Present(empty);
        Require(!emptyView.ConfirmOffered, "A plan that changes nothing must not offer confirm.");
        Require(emptyView.CloseLabel == "Close", $"Empty-plan close label was '{emptyView.CloseLabel}' instead of 'Close'.");
        Require(emptyView.ConfirmLabel.Length == 0, "A plan that changes nothing must not carry a confirm label.");
        Require(emptyView.Edits.Count == 0, "A plan that changes nothing must produce zero edits.");
        Require(emptyView.Emphasis.Contains("No games", StringComparison.Ordinal),
            $"Empty-plan emphasis must state that nothing changes: '{emptyView.Emphasis}'.");
        Require(emptyView.Detail.Contains("nothing to change", StringComparison.Ordinal),
            $"Empty-plan detail must say there is nothing to change: '{emptyView.Detail}'.");

        var unsafeMove = new TagMove("rogue", TagTaxonomy.VisibleSourceId, "rpg", TagKinds.Game, "tampered");
        var unsafePlan = new MigrationPlan(
            new[] { unsafeMove },
            new[] { "game-a" },
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["rogue"] = new[] { "game-a" } },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["game-a"] = "rpg" },
            new[] { "rpg" },
            1,
            0);
        TagMigrationPresentation unsafeView = TagMigrationDialog.Present(unsafePlan);
        Require(!unsafeView.ConfirmOffered, "An unsafe plan must refuse to offer confirm.");
        Require(unsafeView.ConfirmLabel.Length == 0, "An unsafe plan must not carry a confirm label.");
        Require(unsafeView.CloseLabel == "Close without migrating",
            $"Unsafe close label was '{unsafeView.CloseLabel}' instead of 'Close without migrating'.");
        Require(unsafeView.Emphasis.Contains("must not", StringComparison.Ordinal),
            $"Unsafe emphasis must state the plan touches a category it must not: '{unsafeView.Emphasis}'.");
        Require(unsafeView.Detail.Contains("'rogue'", StringComparison.Ordinal) && unsafeView.Detail.Contains("'rpg'", StringComparison.Ordinal),
            $"Unsafe detail must name the tag and the offending destination it found: '{unsafeView.Detail}'.");
        Require(unsafeView.Edits.Count == 0, "An unsafe plan must not expose applyable edits.");

        var hiddenMove = new TagMove("movie", TagTaxonomy.VisibleSourceId, TagTaxonomy.MoviesCategoryId, TagKinds.Movie, "fine");
        var visibleAssignment = new MigrationPlan(
            new[] { hiddenMove },
            new[] { "game-a" },
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["movie"] = new[] { "game-a" } },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["game-a"] = "finished" },
            new[] { TagTaxonomy.MoviesCategoryId },
            1,
            0);
        TagMigrationPresentation assignmentView = TagMigrationDialog.Present(visibleAssignment);
        Require(!assignmentView.ConfirmOffered, "A plan whose game would land in a visible category must refuse to confirm.");
        Require(assignmentView.Detail.Contains("'game-a'", StringComparison.Ordinal)
            && assignmentView.Detail.Contains("'finished'", StringComparison.Ordinal),
            $"The refusal must name the game and the visible category it found: '{assignmentView.Detail}'.");

        foreach ((MigrationPlan fixture, TagMigrationPresentation view) in new[]
        {
            (normal, normalView), (empty, emptyView), (unsafePlan, unsafeView), (visibleAssignment, assignmentView)
        })
        {
            Require(view.ConfirmOffered == (TagMigrationService.IsSafe(fixture) && fixture.ChangedGameCount > 0),
                "Confirm is offered exactly when the plan is safe and changes at least one game.");
            Require(view.Title.Length > 0 && view.Emphasis.Length > 0 && view.Detail.Length > 0 && view.CloseLabel.Length > 0,
                "Every user-facing string must be a real sentence, never empty.");
        }
    }
}
