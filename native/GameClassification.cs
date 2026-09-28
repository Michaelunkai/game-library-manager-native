using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace GameLibrary.Native;

public enum GameDimensionSourceKind
{
    PublisherOrDeveloper,
    OfficialStore,
    PlatformMetadata
}

public enum GameplayPlaneEvidence
{
    TwoDimensional,
    ThreeDimensional,
    Mixed,
    Unknown
}

public enum GameplayPresentationEvidence
{
    TwoDimensional,
    TwoPointFiveDimensional,
    ThreeDimensional,
    Mixed,
    Unknown
}

public enum GameClassificationDecision
{
    Eligible2D,
    Not2D,
    Review,
    Protected,
    NonGame
}

public sealed record GameDimensionEvidence(
    string SourceId,
    GameDimensionSourceKind SourceKind,
    string SourceUrl,
    string MatchedProductIdentity,
    bool SourceOwnershipVerified,
    DateTimeOffset EvidenceUtc,
    GameplayPlaneEvidence GameplayPlane,
    bool PrimaryGameplayPlaneDescribed,
    string Summary,
    GameplayPresentationEvidence Presentation = GameplayPresentationEvidence.Unknown,
    DateTimeOffset? FreshUntilUtc = null);

public sealed record GameClassificationTarget(
    string CanonicalId,
    string CanonicalProductIdentity,
    IReadOnlyList<string> SourceIds,
    IReadOnlyDictionary<string, string> CurrentAssignments,
    bool IsExplicitlyNonGame = false,
    IReadOnlyCollection<string>? ProtectedCategoryIds = null);

public sealed record GameClassificationInput(
    GameClassificationTarget Target,
    IReadOnlyList<GameDimensionEvidence> Evidence);

public sealed record GameClassificationEntry(
    string CanonicalId,
    GameClassificationDecision Decision,
    string ApplicationStatus,
    string Reason,
    int EvidenceCount);

public sealed record GameClassificationBatchResult(
    string ReceiptId,
    IReadOnlyList<GameClassificationEntry> Entries,
    int ChangedAssignments);

public sealed record GameClassificationUndoResult(
    string ReceiptId,
    int RestoredAssignments,
    int SkippedAssignments);

/// <summary>
/// Evidence-gated 2D classification. This component never infers a dimension from
/// a title, genre, image, or pixel-art style; callers must supply matched evidence.
/// Decisions, assignments, and the undo receipt are committed in one profile save.
/// </summary>
public static class GameClassification
{
    public const int CurrentSchemaVersion = 2;
    public const string TargetCategoryId = "2d";
    private static readonly TimeSpan MaximumEvidenceAge = TimeSpan.FromDays(180);
    private const string ReliabilityKey = "reliability";
    private const string ClassificationKey = "gameClassification";
    private const string DecisionsKey = "decisions";
    private const string ReceiptsKey = "receipts";
    private static readonly HashSet<string> BuiltInProtected = new(StringComparer.OrdinalIgnoreCase)
    { "finished", "not_for_me", "meh", "hyperv" };

    /// <summary>
    /// Records evidence for all candidates and applies only corroborated positive
    /// decisions. <paramref name="availableCategoryIds"/> must be the reconciled
    /// category set; the method will not invent a missing 2D category.
    /// </summary>
    public static GameClassificationBatchResult ApplyBatch(
        LibraryStore store,
        UserState state,
        IEnumerable<GameClassificationInput> inputs,
        IEnumerable<string> availableCategoryIds,
        DateTimeOffset? evaluatedUtc = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(availableCategoryIds);

        DateTimeOffset now = (evaluatedUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var categories = availableCategoryIds.ToHashSet(StringComparer.Ordinal);
        var candidates = inputs.ToArray();
        ValidateCandidates(candidates);

        JsonObject reliability = CloneObject(state.LocalCatalog[ReliabilityKey]);
        JsonObject classification = CloneObject(reliability[ClassificationKey]);
        JsonObject decisions = CloneObject(classification[DecisionsKey]);
        var edits = new List<PendingEdit>();
        var sourceOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            foreach (string sourceId in candidate.Target.SourceIds)
            {
                if (sourceOwners.TryGetValue(sourceId, out string? existingCanonical)
                    && !string.Equals(existingCanonical, candidate.Target.CanonicalId, StringComparison.Ordinal))
                    throw new FormatException($"Source identity '{sourceId}' belongs to more than one canonical game in this batch.");
                sourceOwners[sourceId] = candidate.Target.CanonicalId;
            }
        }

        string receiptId = Guid.NewGuid().ToString("N");
        var entries = new List<GameClassificationEntry>(candidates.Length);
        var receiptChanges = new JsonArray();
        int changedAssignments = 0;

        foreach (var candidate in candidates)
        {
            GameClassificationTarget target = candidate.Target;
            var assessment = Assess(target, candidate.Evidence, now);
            JsonObject priorDecision = CloneObject(decisions[target.CanonicalId]);
            string applicationStatus = "notApplied";
            string reason = assessment.Reason;
            var trackedAssignments = CloneObject(priorDecision["trackedAssignments"]);
            bool hasTrackedAssignments = trackedAssignments.Count > 0;

            if (assessment.Decision == GameClassificationDecision.Eligible2D)
            {
                if (!categories.Contains(TargetCategoryId))
                {
                    applicationStatus = "notApplied";
                    reason = "The reconciled local category list does not contain '2d'.";
                }
                else if (hasTrackedAssignments && HasUserOverride(target, trackedAssignments))
                {
                    applicationStatus = "userOverride";
                    reason = "A source assignment changed after the last classifier application; the newer personal choice was preserved.";
                }
                else
                {
                    int candidateChanges = 0;
                    foreach (string sourceId in target.SourceIds)
                    {
                        string current = target.CurrentAssignments.GetValueOrDefault(sourceId, "");
                        if (!string.Equals(current, TargetCategoryId, StringComparison.Ordinal))
                        {
                            edits.Add(new PendingEdit
                            {
                                Section = "gameCategories",
                                Key = sourceId,
                                After = JsonValue.Create(TargetCategoryId)
                            });
                            receiptChanges.Add(CreateChange(state, sourceId, current));
                            changedAssignments++;
                            candidateChanges++;
                        }
                        trackedAssignments[sourceId] = TargetCategoryId;
                    }
                    applicationStatus = candidateChanges > 0 ? "applied" : "alreadyAssigned";
                    reason = "Matched primary-gameplay evidence was corroborated across independent source identities.";
                }
            }

            JsonObject decision = BuildDecision(target, candidate.Evidence, assessment, applicationStatus, reason, trackedAssignments, now);
            decisions[target.CanonicalId] = decision;
            entries.Add(new GameClassificationEntry(target.CanonicalId, assessment.Decision, applicationStatus, reason, candidate.Evidence.Count));
        }

        JsonObject receipt = new()
        {
            ["schemaVersion"] = CurrentSchemaVersion,
            ["receiptId"] = receiptId,
            ["createdUtc"] = now.ToString("O"),
            ["status"] = "applied",
            ["changedAssignments"] = changedAssignments,
            ["changes"] = receiptChanges
        };
        JsonArray receipts = CloneArray(classification[ReceiptsKey]);
        receipts.Add(receipt);
        classification["schemaVersion"] = CurrentSchemaVersion;
        classification[DecisionsKey] = decisions;
        classification[ReceiptsKey] = receipts;
        reliability["schemaVersion"] = Math.Max(CurrentSchemaVersion, Integer(reliability["schemaVersion"]));
        reliability[ClassificationKey] = classification;
        edits.Add(new PendingEdit { Section = ReliabilityKey, After = reliability });

        LocalCatalogEdits.Save(store, state, edits.ToArray());
        return new GameClassificationBatchResult(receiptId, entries, changedAssignments);
    }

    /// <summary>
    /// Restores only source assignments still owned by the classifier's receipt.
    /// A later category edit is detected and left untouched.
    /// </summary>
    public static GameClassificationUndoResult UndoBatch(
        LibraryStore store,
        UserState state,
        string receiptId,
        DateTimeOffset? undoneUtc = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(receiptId)) throw new ArgumentException("An undo receipt ID is required.", nameof(receiptId));

        DateTimeOffset now = (undoneUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        JsonObject reliability = CloneObject(state.LocalCatalog[ReliabilityKey]);
        JsonObject classification = CloneObject(reliability[ClassificationKey]);
        JsonArray receipts = CloneArray(classification[ReceiptsKey]);
        int receiptIndex = -1;
        JsonObject? receipt = null;
        for (int i = 0; i < receipts.Count; i++)
        {
            if (receipts[i] is JsonObject item && string.Equals(DataJson.Text(item["receiptId"]), receiptId, StringComparison.Ordinal))
            { receiptIndex = i; receipt = (JsonObject)item.DeepClone(); break; }
        }
        if (receiptIndex < 0 || receipt == null) throw new FormatException("The classification undo receipt was not found in this profile.");
        if (receipt["undo"] is JsonObject) throw new InvalidOperationException("This classification receipt has already been undone.");

        var edits = new List<PendingEdit>();
        var undoEntries = new JsonArray();
        int restored = 0, skipped = 0;
        if (receipt["changes"] is JsonArray changes)
        {
            foreach (var node in changes)
            {
                if (node is not JsonObject change) continue;
                string sourceId = DataJson.Text(change["sourceId"]);
                if (string.IsNullOrWhiteSpace(sourceId)) continue;
                string? currentLocal = ReadLocalCategoryOverride(state.LocalCatalog, sourceId);
                bool stillOwned = string.Equals(currentLocal, DataJson.Text(change["appliedLocalCategory"]), StringComparison.Ordinal);
                if (stillOwned)
                {
                    bool hadPriorLocal = change["hadPriorLocalCategory"]?.ToString() == "true";
                    JsonNode? priorValue = hadPriorLocal ? JsonValue.Create(DataJson.Text(change["priorLocalCategory"])) : null;
                    edits.Add(new PendingEdit { Section = "gameCategories", Key = sourceId, After = priorValue });
                    undoEntries.Add(new JsonObject { ["sourceId"] = sourceId, ["status"] = "restored" });
                    restored++;
                }
                else
                {
                    undoEntries.Add(new JsonObject { ["sourceId"] = sourceId, ["status"] = "skipped-user-change" });
                    skipped++;
                }
            }
        }
        receipt["status"] = skipped == 0 ? "undone" : "partiallyUndone";
        receipt["undo"] = new JsonObject
        {
            ["undoneUtc"] = now.ToString("O"),
            ["restoredAssignments"] = restored,
            ["skippedAssignments"] = skipped,
            ["entries"] = undoEntries
        };
        receipts[receiptIndex] = receipt;
        classification["receipts"] = receipts;
        reliability[ClassificationKey] = classification;
        edits.Add(new PendingEdit { Section = ReliabilityKey, After = reliability });
        LocalCatalogEdits.Save(store, state, edits.ToArray());
        return new GameClassificationUndoResult(receiptId, restored, skipped);
    }

    public static GameClassificationDecision Assess(GameClassificationTarget target, IReadOnlyList<GameDimensionEvidence> evidence, out string reason)
        => Assess(target, evidence, DateTimeOffset.UtcNow, out reason);

    public static GameClassificationDecision Assess(
        GameClassificationTarget target,
        IReadOnlyList<GameDimensionEvidence> evidence,
        DateTimeOffset evaluatedUtc,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(evidence);
        DateTimeOffset now = evaluatedUtc.ToUniversalTime();
        if (target.IsExplicitlyNonGame)
        { reason = "The canonical identity is explicitly marked as a non-game entry."; return GameClassificationDecision.NonGame; }
        if (IsProtected(target))
        { reason = "At least one linked source has an assignment protected from automatic classification."; return GameClassificationDecision.Protected; }

        var matched = evidence.Where(item => IsMatchedEvidence(target, item, now)).ToArray();
        if (matched.Any(item => item.GameplayPlane == GameplayPlaneEvidence.Mixed))
        { reason = "Matched evidence describes mixed primary gameplay; the category requires review."; return GameClassificationDecision.Review; }
        var primary = matched.Where(item => item.PrimaryGameplayPlaneDescribed
            && item.GameplayPlane is GameplayPlaneEvidence.TwoDimensional or GameplayPlaneEvidence.ThreeDimensional).ToArray();
        bool has2d = primary.Any(item => item.GameplayPlane == GameplayPlaneEvidence.TwoDimensional);
        bool has3d = primary.Any(item => item.GameplayPlane == GameplayPlaneEvidence.ThreeDimensional);
        if (has2d && has3d)
        { reason = "Matched sources disagree about the primary gameplay plane."; return GameClassificationDecision.Review; }
        if (has2d && IsCorroborated(primary.Where(item => item.GameplayPlane == GameplayPlaneEvidence.TwoDimensional)))
        { reason = "Matched primary gameplay is 2D; 3D presentation does not change the plane-based 2D classification."; return GameClassificationDecision.Eligible2D; }
        if (has3d && IsCorroborated(primary.Where(item => item.GameplayPlane == GameplayPlaneEvidence.ThreeDimensional)))
        { reason = "Matched sources corroborate primary 3D gameplay; no 2D assignment was applied."; return GameClassificationDecision.Not2D; }
        reason = matched.Length == 0
            ? "No fresh, authoritative evidence matched the canonical product identity."
            : "Evidence is missing direct primary-plane detail or independent corroboration.";
        return GameClassificationDecision.Review;
    }

    private static (GameClassificationDecision Decision, string Reason) Assess(
        GameClassificationTarget target, IReadOnlyList<GameDimensionEvidence> evidence, DateTimeOffset evaluatedUtc)
    {
        var decision = Assess(target, evidence, evaluatedUtc, out string reason);
        return (decision, reason);
    }

    private static void ValidateCandidates(IReadOnlyList<GameClassificationInput> candidates)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (candidate?.Target == null || string.IsNullOrWhiteSpace(candidate.Target.CanonicalId))
                throw new FormatException("Every classification candidate requires a canonical identity.");
            if (!ids.Add(candidate.Target.CanonicalId))
                throw new FormatException($"Canonical identity '{candidate.Target.CanonicalId}' occurs more than once in the batch.");
            if (candidate.Target.SourceIds == null || candidate.Target.SourceIds.Count == 0
                || candidate.Target.SourceIds.Any(string.IsNullOrWhiteSpace)
                || candidate.Target.SourceIds.Distinct(StringComparer.Ordinal).Count() != candidate.Target.SourceIds.Count)
                throw new FormatException($"Canonical identity '{candidate.Target.CanonicalId}' must have distinct source IDs.");
            if (candidate.Target.CurrentAssignments == null || candidate.Evidence == null)
                throw new FormatException($"Canonical identity '{candidate.Target.CanonicalId}' has incomplete classification input.");
            if (candidate.Target.CurrentAssignments.Count != candidate.Target.SourceIds.Count
                || candidate.Target.CurrentAssignments.Keys.Any(id => !candidate.Target.SourceIds.Contains(id, StringComparer.Ordinal)))
                throw new FormatException($"Canonical identity '{candidate.Target.CanonicalId}' must include the effective assignment for every linked source ID.");
            if (candidate.Evidence.Any(item => item == null))
                throw new FormatException($"Canonical identity '{candidate.Target.CanonicalId}' contains an empty evidence record.");
        }
    }

    private static bool IsProtected(GameClassificationTarget target)
    {
        var protectedIds = new HashSet<string>(BuiltInProtected, StringComparer.OrdinalIgnoreCase);
        if (target.ProtectedCategoryIds != null)
            foreach (string id in target.ProtectedCategoryIds)
                if (!string.IsNullOrWhiteSpace(id)) protectedIds.Add(id);
        return target.SourceIds.Any(sourceId => target.CurrentAssignments.TryGetValue(sourceId, out string? category)
            && protectedIds.Contains(category));
    }

    private static bool IsMatchedEvidence(GameClassificationTarget target, GameDimensionEvidence item, DateTimeOffset evaluatedUtc)
    {
        if (item == null || !item.SourceOwnershipVerified || !item.PrimaryGameplayPlaneDescribed
            || string.IsNullOrWhiteSpace(item.SourceId) || string.IsNullOrWhiteSpace(target.CanonicalProductIdentity)
            || !string.Equals(item.MatchedProductIdentity.Trim(), target.CanonicalProductIdentity.Trim(), StringComparison.OrdinalIgnoreCase)
            || item.EvidenceUtc.Offset != TimeSpan.Zero || item.EvidenceUtc > evaluatedUtc
            || item.FreshUntilUtc is not DateTimeOffset freshUntil || freshUntil.Offset != TimeSpan.Zero
            || freshUntil <= item.EvidenceUtc || freshUntil - item.EvidenceUtc > MaximumEvidenceAge
            || evaluatedUtc > freshUntil || string.IsNullOrWhiteSpace(item.Summary) || item.Summary.Length > 1200
            || !Enum.IsDefined(item.SourceKind) || !Enum.IsDefined(item.GameplayPlane) || !Enum.IsDefined(item.Presentation))
            return false;
        if (!Uri.TryCreate(item.SourceUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            return false;
        return item.SourceKind is GameDimensionSourceKind.PublisherOrDeveloper or GameDimensionSourceKind.OfficialStore or GameDimensionSourceKind.PlatformMetadata;
    }

    private static bool IsCorroborated(IEnumerable<GameDimensionEvidence> evidence)
    {
        var sources = evidence.ToArray();
        return sources.Select(item => item.SourceId.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2
            && sources.Select(item => item.SourceKind).Distinct().Count() >= 2
            && sources.Select(item => new Uri(item.SourceUrl).IdnHost).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2;
    }

    private static bool HasUserOverride(GameClassificationTarget target, JsonObject tracked)
    {
        foreach (var item in tracked)
        {
            if (!target.SourceIds.Contains(item.Key, StringComparer.Ordinal)) continue;
            string current = target.CurrentAssignments.GetValueOrDefault(item.Key, "");
            if (!string.Equals(current, DataJson.Text(item.Value), StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static JsonObject BuildDecision(GameClassificationTarget target, IReadOnlyList<GameDimensionEvidence> evidence,
        (GameClassificationDecision Decision, string Reason) assessment, string applicationStatus, string reason,
        JsonObject trackedAssignments, DateTimeOffset now)
    {
        var sourceIds = new JsonArray(target.SourceIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        var evidenceArray = new JsonArray(evidence.Select(ToJson).Cast<JsonNode?>().ToArray());
        return new JsonObject
        {
            ["canonicalId"] = target.CanonicalId,
            ["canonicalProductIdentity"] = target.CanonicalProductIdentity,
            ["decision"] = assessment.Decision.ToString(),
            ["applicationStatus"] = applicationStatus,
            ["reason"] = reason,
            ["evaluatedUtc"] = now.ToString("O"),
            ["sourceIds"] = sourceIds,
            ["evidence"] = evidenceArray,
            ["trackedAssignments"] = trackedAssignments
        };
    }

    private static JsonObject ToJson(GameDimensionEvidence evidence) => new()
    {
        ["sourceId"] = evidence.SourceId,
        ["sourceKind"] = evidence.SourceKind.ToString(),
        ["sourceUrl"] = evidence.SourceUrl,
        ["matchedProductIdentity"] = evidence.MatchedProductIdentity,
        ["sourceOwnershipVerified"] = evidence.SourceOwnershipVerified,
        ["evidenceUtc"] = evidence.EvidenceUtc.ToString("O"),
        ["gameplayPlane"] = evidence.GameplayPlane.ToString(),
        ["presentation"] = evidence.Presentation.ToString(),
        ["primaryGameplayPlaneDescribed"] = evidence.PrimaryGameplayPlaneDescribed,
        ["freshUntilUtc"] = evidence.FreshUntilUtc?.ToString("O"),
        ["summary"] = evidence.Summary
    };

    private static JsonObject CreateChange(UserState state, string sourceId, string priorEffectiveCategory)
    {
        bool hadPriorLocal = state.LocalCatalog["gameCategories"] is JsonObject categories && categories.ContainsKey(sourceId);
        string priorLocal = hadPriorLocal ? DataJson.Text(state.LocalCatalog["gameCategories"]?[sourceId]) : "";
        return new JsonObject
        {
            ["sourceId"] = sourceId,
            ["priorEffectiveCategory"] = priorEffectiveCategory,
            ["hadPriorLocalCategory"] = hadPriorLocal,
            ["priorLocalCategory"] = hadPriorLocal ? priorLocal : null,
            ["appliedLocalCategory"] = TargetCategoryId
        };
    }

    private static string? ReadLocalCategoryOverride(JsonObject localCatalog, string sourceId)
    {
        if (localCatalog["gameCategories"] is JsonObject categories && categories.TryGetPropertyValue(sourceId, out var value))
            return DataJson.Text(value);
        return null;
    }

    private static JsonObject CloneObject(JsonNode? node) => node is JsonObject value ? (JsonObject)value.DeepClone() : new JsonObject();
    private static JsonArray CloneArray(JsonNode? node) => node is JsonArray value ? (JsonArray)value.DeepClone() : new JsonArray();
    private static int Integer(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out int result) ? result : 0;
}
