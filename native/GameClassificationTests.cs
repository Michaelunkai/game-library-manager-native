using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace GameLibrary.Native;

/// <summary>Isolated evidence, protection, override, persistence, and undo proofs for GameClassification.</summary>
public static class GameClassificationTests
{
    public static int Run(string root)
    {
        int checks = 0;
        void Require(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(message);
        }

        (LibraryStore store, UserState state) = NewFixture(root, "positive", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["docker:publisher:alpha"] = "new",
            ["local:alpha"] = "rpg"
        });
        var alpha = Target("canonical:alpha", "steam:101", new[] { "docker:publisher:alpha", "local:alpha" },
            ("docker:publisher:alpha", "new"), ("local:alpha", "rpg"));
        var evidence = Corroborated2D("steam:101");
        var first = GameClassification.ApplyBatch(store, state,
            new[] { new GameClassificationInput(alpha, evidence) }, new[] { "2d", "new", "rpg" }, FixedUtc);
        Require(first.ChangedAssignments == 2
            && DataJson.Text(state.LocalCatalog["gameCategories"]?["docker:publisher:alpha"]) == "2d"
            && DataJson.Text(state.LocalCatalog["gameCategories"]?["local:alpha"]) == "2d",
            "Corroborated 2D evidence did not classify all linked source records.");
        var persistedAlpha = state.LocalCatalog["reliability"]?["gameClassification"]?["decisions"]?["canonical:alpha"];
        Require(DataJson.Text(persistedAlpha?["evidence"]?[0]?["sourceUrl"]).StartsWith("https://", StringComparison.Ordinal)
            && DataJson.Text(persistedAlpha?["evidence"]?[0]?["matchedProductIdentity"]) == "steam:101"
            && !string.IsNullOrWhiteSpace(DataJson.Text(persistedAlpha?["evidence"]?[0]?["evidenceUtc"])),
            "The profile did not retain matched-source provenance and its evidence timestamp.");
        var firstReceipt = state.LocalCatalog["reliability"]?["gameClassification"]?["receipts"]?.AsArray()
            .Single(node => DataJson.Text(node?["receiptId"]) == first.ReceiptId);
        Require(firstReceipt?["changes"]?.AsArray().Count == 2
            && DataJson.Text(firstReceipt?["changes"]?[0]?["appliedLocalCategory"]) == "2d",
            "The applied classification did not retain its assignment undo receipt.");

        var twoPointFiveD = GameClassification.Assess(alpha, Corroborated2D("steam:101", GameplayPresentationEvidence.TwoPointFiveDimensional),
            FixedUtc, out string planeReason);
        Require(twoPointFiveD == GameClassificationDecision.Eligible2D
            && planeReason.Contains("plane-based", StringComparison.Ordinal),
            "A corroborated 2D gameplay plane with explicit 2.5D presentation was not retained in the 2D category.");
        var staleEvidence = Corroborated2D("steam:101").Select(item => item with { FreshUntilUtc = FixedUtc.AddMinutes(-1) }).ToArray();
        var staleDecision = GameClassification.Assess(alpha, staleEvidence, FixedUtc, out _);
        Require(staleDecision == GameClassificationDecision.Review,
            "Expired 2D classification evidence was accepted as current.");
        var mixedGameplay = GameClassification.Assess(alpha, Corroborated2D("steam:101").Select(item =>
            item with { GameplayPlane = GameplayPlaneEvidence.Mixed }).ToArray(), FixedUtc, out _);
        Require(mixedGameplay == GameClassificationDecision.Review,
            "Mixed primary gameplay was silently reduced to a 2D classification.");
        var disputedMixedGameplay = GameClassification.Assess(alpha, Corroborated2D("steam:101").Concat(new[]
        {
            Evidence("publisher:mixed", GameDimensionSourceKind.PublisherOrDeveloper, "steam:101", GameplayPlaneEvidence.Mixed)
        }).ToArray(), FixedUtc, out _);
        Require(disputedMixedGameplay == GameClassificationDecision.Review,
            "Corroborated 2D evidence overrode another source's mixed-gameplay description.");
        var samePublisherOrigin = Corroborated2D("steam:101").Select(item => item with { SourceUrl = "https://publisher.example/" + item.SourceId }).ToArray();
        Require(GameClassification.Assess(alpha, samePublisherOrigin, FixedUtc, out _) == GameClassificationDecision.Review,
            "Two labels on one HTTPS origin were counted as independent corroboration.");

        (store, state) = NewFixture(root, "unresolved", new Dictionary<string, string>(StringComparer.Ordinal) { ["docker:unresolved"] = "rpg" });
        var unresolved = Target("canonical:unresolved", "steam:202", new[] { "docker:unresolved" }, ("docker:unresolved", "rpg"));
        var noEvidence = GameClassification.ApplyBatch(store, state,
            new[] { new GameClassificationInput(unresolved, Array.Empty<GameDimensionEvidence>()) }, new[] { "2d", "rpg" }, FixedUtc);
        Require(noEvidence.Entries.Single().Decision == GameClassificationDecision.Review
            && noEvidence.ChangedAssignments == 0
            && DataJson.Text(state.LocalCatalog["gameCategories"]?["docker:unresolved"]) == "rpg",
            "An unsupported game was guessed into the 2D category.");

        (store, state) = NewFixture(root, "missing-category", new Dictionary<string, string>(StringComparer.Ordinal)
        { ["docker:no-2d-category"] = "new" });
        var no2dCategory = Target("canonical:no-2d-category", "steam:203", new[] { "docker:no-2d-category" },
            ("docker:no-2d-category", "new"));
        var missingCategoryResult = GameClassification.ApplyBatch(store, state,
            new[] { new GameClassificationInput(no2dCategory, Corroborated2D("steam:203")) }, new[] { "new" }, FixedUtc);
        Require(missingCategoryResult.Entries.Single().ApplicationStatus == "notApplied"
            && missingCategoryResult.ChangedAssignments == 0
            && DataJson.Text(state.LocalCatalog["gameCategories"]?["docker:no-2d-category"]) == "new",
            "The classifier created or applied a 2D category absent from the reconciled category list.");

        (store, state) = NewFixture(root, "incomplete-source-map", new Dictionary<string, string>(StringComparer.Ordinal)
        { ["docker:unmapped"] = "new" });
        var unmapped = new GameClassificationTarget("canonical:unmapped", "steam:204", new[] { "docker:unmapped" },
            new Dictionary<string, string>(StringComparer.Ordinal));
        bool incompleteRejected = false;
        try { GameClassification.ApplyBatch(store, state, new[] { new GameClassificationInput(unmapped, Corroborated2D("steam:204")) }, new[] { "2d", "new" }, FixedUtc); }
        catch (FormatException) { incompleteRejected = true; }
        Require(incompleteRejected && DataJson.Text(state.LocalCatalog["gameCategories"]?["docker:unmapped"]) == "new",
            "An incomplete current-category map reached assignment application.");

        (store, state) = NewFixture(root, "mismatch", new Dictionary<string, string>(StringComparer.Ordinal) { ["docker:mismatch"] = "new" });
        var mismatch = Target("canonical:mismatch", "steam:303", new[] { "docker:mismatch" }, ("docker:mismatch", "new"));
        var mismatchedEvidence = Corroborated2D("steam:another-product");
        var mismatchResult = GameClassification.ApplyBatch(store, state,
            new[] { new GameClassificationInput(mismatch, mismatchedEvidence) }, new[] { "2d", "new" }, FixedUtc);
        Require(mismatchResult.Entries.Single().Decision == GameClassificationDecision.Review && mismatchResult.ChangedAssignments == 0,
            "Evidence for a different product identity was accepted.");

        (store, state) = NewFixture(root, "conflicting", new Dictionary<string, string>(StringComparer.Ordinal) { ["docker:conflict"] = "adventure" });
        var conflict = Target("canonical:conflict", "steam:404", new[] { "docker:conflict" }, ("docker:conflict", "adventure"));
        var conflictEvidence = Corroborated2D("steam:404").Concat(new[]
        {
            Evidence("publisher:conflict", GameDimensionSourceKind.PublisherOrDeveloper, "steam:404", GameplayPlaneEvidence.ThreeDimensional),
            Evidence("platform:conflict", GameDimensionSourceKind.PlatformMetadata, "steam:404", GameplayPlaneEvidence.ThreeDimensional)
        }).ToArray();
        var conflictResult = GameClassification.ApplyBatch(store, state,
            new[] { new GameClassificationInput(conflict, conflictEvidence) }, new[] { "2d", "adventure" }, FixedUtc);
        Require(conflictResult.Entries.Single().Decision == GameClassificationDecision.Review && conflictResult.ChangedAssignments == 0,
            "Contradictory primary-gameplay evidence was resolved by guessing.");

        (store, state) = NewFixture(root, "protected", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["game:finished"] = "finished",
            ["game:meh"] = "not_for_me",
            ["game:meh-alias"] = "meh",
            ["game:hyperv"] = "hyperv",
            ["game:utility"] = "utility"
        });
        var protectedCandidates = new[]
        {
            ProtectedTarget("finished", "finished"), ProtectedTarget("meh", "not_for_me"),
            ProtectedTarget("meh-alias", "meh"),
            ProtectedTarget("hyperv", "hyperv"), ProtectedTarget("utility", "utility", new[] { "utility" })
        };
        var protectedResults = GameClassification.ApplyBatch(store, state,
            protectedCandidates.Select(target => new GameClassificationInput(target, Corroborated2D("steam:" + target.CanonicalId))),
            new[] { "2d", "finished", "not_for_me", "meh", "hyperv", "utility" }, FixedUtc);
        Require(protectedResults.ChangedAssignments == 0 && protectedResults.Entries.All(entry => entry.Decision == GameClassificationDecision.Protected)
            && DataJson.Text(state.LocalCatalog["gameCategories"]?["game:finished"]) == "finished"
            && DataJson.Text(state.LocalCatalog["gameCategories"]?["game:meh"]) == "not_for_me"
            && DataJson.Text(state.LocalCatalog["gameCategories"]?["game:meh-alias"]) == "meh"
            && DataJson.Text(state.LocalCatalog["gameCategories"]?["game:hyperv"]) == "hyperv"
            && DataJson.Text(state.LocalCatalog["gameCategories"]?["game:utility"]) == "utility",
            "A protected category assignment changed during classification.");

        (store, state) = NewFixture(root, "non-game", new Dictionary<string, string>(StringComparer.Ordinal) { ["tool:backup"] = "utility" });
        var nonGame = new GameClassificationTarget("canonical:backup-tool", "steam:505", new[] { "tool:backup" },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["tool:backup"] = "utility" }, IsExplicitlyNonGame: true);
        var nonGameResult = GameClassification.ApplyBatch(store, state,
            new[] { new GameClassificationInput(nonGame, Corroborated2D("steam:505")) }, new[] { "2d", "utility" }, FixedUtc);
        Require(nonGameResult.Entries.Single().Decision == GameClassificationDecision.NonGame && nonGameResult.ChangedAssignments == 0,
            "An explicitly non-game entry was classified as a game.");

        (store, state) = NewFixture(root, "identity-case", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AgainsttheStorm"] = "new",
            ["againstthestorm"] = "new"
        });
        var exactCaseInputs = new[]
        {
            new GameClassificationInput(Target("canonical:upper", "steam:601", new[] { "AgainsttheStorm" }, ("AgainsttheStorm", "new")), Corroborated2D("steam:601")),
            new GameClassificationInput(Target("canonical:lower", "steam:602", new[] { "againstthestorm" }, ("againstthestorm", "new")), Corroborated2D("steam:602"))
        };
        GameClassification.ApplyBatch(store, state, exactCaseInputs, new[] { "2d", "new" }, FixedUtc);
        var roundTripped = store.LoadState().LocalCatalog["gameCategories"];
        Require(DataJson.Text(roundTripped?["AgainsttheStorm"]) == "2d"
            && DataJson.Text(roundTripped?["againstthestorm"]) == "2d"
            && roundTripped!.AsObject().Count == 2,
            "Case-sensitive source IDs collapsed during classification persistence.");

        (store, state) = NewFixture(root, "undo", new Dictionary<string, string>(StringComparer.Ordinal) { ["docker:undo-a"] = "new" });
        var undoTarget = Target("canonical:undo", "steam:707", new[] { "docker:undo-a", "docker:undo-b" },
            ("docker:undo-a", "new"), ("docker:undo-b", "rpg"));
        var undoBatch = GameClassification.ApplyBatch(store, state,
            new[] { new GameClassificationInput(undoTarget, Corroborated2D("steam:707")) }, new[] { "2d", "new", "rpg", "personal" }, FixedUtc);
        LocalCatalogEdits.Save(store, state, new PendingEdit { Section = "gameCategories", Key = "docker:undo-b", After = JsonValue.Create("personal") });
        var undo = GameClassification.UndoBatch(store, state, undoBatch.ReceiptId, FixedUtc);
        Require(undo.RestoredAssignments == 1 && undo.SkippedAssignments == 1
            && DataJson.Text(state.LocalCatalog["gameCategories"]?["docker:undo-a"]) == "new"
            && DataJson.Text(state.LocalCatalog["gameCategories"]?["docker:undo-b"]) == "personal",
            "Undo failed to restore only classifier-owned assignments.");
        var repeat = GameClassification.ApplyBatch(store, state,
            new[] { new GameClassificationInput(Target("canonical:undo", "steam:707", new[] { "docker:undo-a", "docker:undo-b" },
                ("docker:undo-a", "new"), ("docker:undo-b", "personal")), Corroborated2D("steam:707")) },
            new[] { "2d", "new", "rpg", "personal" }, FixedUtc);
        Require(repeat.Entries.Single().ApplicationStatus == "userOverride"
            && DataJson.Text(state.LocalCatalog["gameCategories"]?["docker:undo-b"]) == "personal",
            "A later personal category edit was overwritten by a repeated classifier run.");

        return checks;
    }

    private static (LibraryStore Store, UserState State) NewFixture(string root, string name, IReadOnlyDictionary<string, string> categories)
    {
        string profile = Path.Combine(root, "classification-" + name + "-" + Guid.NewGuid().ToString("N"));
        var store = new LibraryStore(profile);
        var state = new UserState { LocalCatalog = new JsonObject() };
        var assignments = new JsonObject();
        foreach (var category in categories) assignments[category.Key] = category.Value;
        state.LocalCatalog["gameCategories"] = assignments;
        store.Save(state);
        return (store, state);
    }

    private static GameClassificationTarget Target(string canonical, string product, string[] sourceIds,
        params (string SourceId, string Category)[] assignments) => new(canonical, product, sourceIds,
            assignments.ToDictionary(item => item.SourceId, item => item.Category, StringComparer.Ordinal));

    private static GameClassificationTarget ProtectedTarget(string id, string category, string[]? extraProtected = null)
    {
        string source = "game:" + id;
        return new GameClassificationTarget("canonical:" + id, "steam:" + id, new[] { source },
            new Dictionary<string, string>(StringComparer.Ordinal) { [source] = category }, ProtectedCategoryIds: extraProtected);
    }

    private static GameDimensionEvidence[] Corroborated2D(
        string product, GameplayPresentationEvidence presentation = GameplayPresentationEvidence.Unknown) => new[]
    {
        Evidence("publisher:fixture", GameDimensionSourceKind.PublisherOrDeveloper, product, GameplayPlaneEvidence.TwoDimensional, presentation),
        Evidence("store:fixture", GameDimensionSourceKind.OfficialStore, product, GameplayPlaneEvidence.TwoDimensional, presentation)
    };

    private static GameDimensionEvidence Evidence(string sourceId, GameDimensionSourceKind kind, string product,
        GameplayPlaneEvidence plane, GameplayPresentationEvidence presentation = GameplayPresentationEvidence.Unknown) =>
        new(sourceId, kind, "https://" + (kind switch
            {
                GameDimensionSourceKind.PublisherOrDeveloper => "publisher.example",
                GameDimensionSourceKind.OfficialStore => "store.example",
                _ => "platform.example"
            }) + "/" + Uri.EscapeDataString(sourceId), product, true, FixedUtc,
            plane, true, "Official product page describes the primary gameplay plane for the matched product.", presentation, FixedUtc.AddDays(90));

    private static DateTimeOffset FixedUtc => new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
}
