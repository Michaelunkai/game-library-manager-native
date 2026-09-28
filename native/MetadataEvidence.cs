using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace GameLibrary.Native;

public enum MetadataField
{
    Artwork,
    MainStoryHours,
    MainPlusExtrasHours,
    CompletionistHours,
    DownloadSizeGb,
    PublishedDiskRequirementGb,
    InstalledLogicalSizeGb
}

public enum MetadataEvidenceOutcome { Available, Unavailable }
public enum MetadataSourceLocationKind { Remote, LocalMeasurement }

public enum MetadataCoverageStatus
{
    Missing,
    ValidFresh,
    ValidStale,
    Unavailable,
    IdentityMismatch,
    ScopeMismatch,
    NeedsReview,
    InvalidProvenance,
    InvalidValue
}

public sealed record MetadataGameScope(string Title, string? Edition, string? Platform, int? ReleaseYear);

/// <summary>Resolved canonical and provider product identities used to validate one metadata field.</summary>
public sealed record MetadataGameIdentity(
    string CanonicalId,
    string CanonicalProductIdentity,
    MetadataGameScope Scope,
    IReadOnlyDictionary<string, string> ProviderProductIds);

/// <summary>One field's independently sourced result or explicit unavailable attempt.</summary>
public sealed record MetadataEvidenceRecord(
    string CanonicalId,
    MetadataField Field,
    string Provider,
    string ProviderProductId,
    MetadataSourceLocationKind SourceLocationKind,
    string SourceLocation,
    MetadataGameScope Scope,
    MetadataEvidenceOutcome Outcome,
    decimal? NumericValue,
    string? TextValue,
    string Unit,
    DateTimeOffset EvidenceUtc,
    DateTimeOffset? FreshUntilUtc,
    string? FailureReason = null,
    DateTimeOffset? RetryAfterUtc = null,
    int? SampleCount = null,
    string? MeasurementBasis = null,
    string? ContentSha256 = null,
    int? ImageWidth = null,
    int? ImageHeight = null,
    bool? ImageDecodedSuccessfully = null,
    string? CanonicalProductIdentity = null);

public sealed record MetadataCoverageAssessment(
    MetadataField Field,
    MetadataCoverageStatus Status,
    bool Passed,
    bool Fresh,
    string Reason);

/// <summary>
/// Keeps metadata field scope and provenance explicit. A same-title result cannot
/// pass unless its canonical identity, provider product ID, edition/platform/year,
/// requested field, units, source, value, and freshness all agree.
/// </summary>
public static class MetadataEvidence
{
    public const int CurrentSchemaVersion = 2;
    public const string CacheFileName = "metadata-evidence.json";
    private const int MaximumRejectedAttempts = 100;

    public static MetadataCoverageAssessment Evaluate(
        MetadataGameIdentity identity,
        MetadataField requestedField,
        MetadataEvidenceRecord? evidence,
        DateTimeOffset evaluatedUtc)
    {
        ArgumentNullException.ThrowIfNull(identity);
        DateTimeOffset now = evaluatedUtc.ToUniversalTime();
        if (evidence == null) return Result(requestedField, MetadataCoverageStatus.Missing, "No field-level evidence has been recorded.");
        if (string.IsNullOrWhiteSpace(identity.CanonicalId)
            || !string.Equals(evidence.CanonicalId, identity.CanonicalId, StringComparison.Ordinal))
            return Result(requestedField, MetadataCoverageStatus.IdentityMismatch, "Evidence belongs to another canonical game.");
        if (string.IsNullOrWhiteSpace(identity.CanonicalProductIdentity)
            || !string.Equals(evidence.CanonicalProductIdentity, identity.CanonicalProductIdentity, StringComparison.Ordinal))
            return Result(requestedField, MetadataCoverageStatus.IdentityMismatch, "Evidence is not tied to the resolved canonical product identity.");
        if (evidence.Field != requestedField)
            return Result(requestedField, MetadataCoverageStatus.ScopeMismatch, $"Requested {requestedField}; evidence is scoped to {evidence.Field}.");
        if (!ProviderProductMatches(identity, evidence))
            return Result(requestedField, MetadataCoverageStatus.IdentityMismatch, "Provider product identity is missing or does not match the resolved game.");

        MetadataCoverageAssessment scope = MatchScope(identity.Scope, evidence.Scope, requestedField);
        if (scope.Status != MetadataCoverageStatus.ValidFresh) return scope;
        if (!ValidSource(evidence))
            return Result(requestedField, MetadataCoverageStatus.InvalidProvenance, "The evidence source is not a valid HTTPS source or absolute local measurement path.");
        if (!SourceMatchesProviderProduct(evidence))
            return Result(requestedField, MetadataCoverageStatus.IdentityMismatch, "The provider source URL does not identify the recorded provider product.");
        if (evidence.EvidenceUtc.Offset != TimeSpan.Zero || evidence.EvidenceUtc > now)
            return Result(requestedField, MetadataCoverageStatus.InvalidProvenance, "Evidence timestamp is not a valid UTC observation time.");
        if (evidence.RetryAfterUtc is DateTimeOffset retry && (retry.Offset != TimeSpan.Zero || retry < evidence.EvidenceUtc))
            return Result(requestedField, MetadataCoverageStatus.InvalidProvenance, "Retry time is invalid or earlier than the recorded attempt.");

        if (evidence.Outcome == MetadataEvidenceOutcome.Unavailable)
        {
            if (string.IsNullOrWhiteSpace(evidence.FailureReason) || HasValue(evidence))
                return Result(requestedField, MetadataCoverageStatus.InvalidValue, "Unavailable outcomes require a reason and cannot contain a metadata value.");
            return Result(requestedField, MetadataCoverageStatus.Unavailable,
                evidence.RetryAfterUtc is DateTimeOffset next
                    ? $"The provider reported this field unavailable; retry is scheduled after {next:O}."
                    : "The provider explicitly reported this field unavailable.");
        }

        string? valueFailure = ValidateAvailableValue(evidence);
        if (valueFailure != null) return Result(requestedField, MetadataCoverageStatus.InvalidValue, valueFailure);
        if (evidence.FreshUntilUtc is not DateTimeOffset freshUntil || freshUntil.Offset != TimeSpan.Zero || freshUntil <= evidence.EvidenceUtc)
            return Result(requestedField, MetadataCoverageStatus.InvalidProvenance, "Available metadata must include a UTC freshness boundary after its observation time.");

        DateTimeOffset policyBoundary = evidence.EvidenceUtc + MaximumAge(requestedField);
        DateTimeOffset effectiveBoundary = freshUntil < policyBoundary ? freshUntil : policyBoundary;
        if (now > effectiveBoundary)
            return new MetadataCoverageAssessment(requestedField, MetadataCoverageStatus.ValidStale, false, false,
                $"The identity-matched field is cached but stale since {effectiveBoundary:O}.");
        return new MetadataCoverageAssessment(requestedField, MetadataCoverageStatus.ValidFresh, true, true,
            "The identity-matched field passed scope, value, source, and freshness checks.");
    }

    /// <summary>
    /// Persists the latest field result in the profile cache. Unavailable outcomes
    /// update the last-attempt state without replacing an earlier accepted value;
    /// invalid results go to a bounded audit list and never replace accepted data.
    /// </summary>
    public static MetadataCoverageAssessment RecordAttempt(
        LibraryStore store,
        MetadataGameIdentity identity,
        MetadataField requestedField,
        MetadataEvidenceRecord evidence,
        DateTimeOffset evaluatedUtc)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(evidence);
        var assessment = Evaluate(identity, requestedField, evidence, evaluatedUtc);
        var document = ReadDocument(store);
        JsonObject games = GetObject(document, "games");
        JsonObject game = GetObject(games, identity.CanonicalId);
        JsonObject fields = GetObject(game, "fields");
        string fieldKey = requestedField.ToString();

        if (assessment.Status is MetadataCoverageStatus.ValidFresh or MetadataCoverageStatus.ValidStale)
        {
            JsonObject? existing = fields[fieldKey] as JsonObject;
            DateTimeOffset existingAt = ParseUtc(existing?["evidence"]?["evidenceUtc"]);
            if (existing != null && existingAt > evidence.EvidenceUtc)
            {
                AppendRejected(game, evidence, assessment, "An older field observation cannot replace a newer stored observation.");
            }
            else
            {
                fields[fieldKey] = new JsonObject
                {
                    ["evidence"] = ToJson(evidence),
                    ["validation"] = AssessmentJson(assessment),
                    ["lastAttempt"] = ToJson(evidence),
                    ["lastFailure"] = null,
                    ["retryAfterUtc"] = null
                };
            }
        }
        else if (assessment.Status == MetadataCoverageStatus.Unavailable)
        {
            JsonObject field = fields[fieldKey] is JsonObject prior ? (JsonObject)prior.DeepClone() : new JsonObject();
            field["lastAttempt"] = ToJson(evidence);
            field["lastFailure"] = evidence.FailureReason;
            field["retryAfterUtc"] = evidence.RetryAfterUtc?.ToString("O");
            field["lastAttemptValidation"] = AssessmentJson(assessment);
            if (field["evidence"] == null) field["validation"] = AssessmentJson(assessment);
            fields[fieldKey] = field;
        }
        else
        {
            AppendRejected(game, evidence, assessment, assessment.Reason);
        }

        game["fields"] = fields;
        games[identity.CanonicalId] = game;
        document["schemaVersion"] = CurrentSchemaVersion;
        document["games"] = games;
        store.CacheData(CacheFileName, document.ToJsonString(DataJson.Options));
        return assessment;
    }

    public static JsonObject? ReadField(LibraryStore store, string canonicalId, MetadataField field)
    {
        var document = ReadDocument(store);
        return document["games"]?[canonicalId]?["fields"]?[field.ToString()] is JsonObject record
            ? (JsonObject)record.DeepClone() : null;
    }

    /// <summary>
    /// Records an installed-size measurement against one exact installation path.
    /// The byte count is converted to decimal GB; an empty or unmeasured target is
    /// not represented as a zero-sized install.
    /// </summary>
    public static MetadataCoverageAssessment RecordInstalledLogicalSize(
        LibraryStore store,
        string canonicalId,
        string canonicalProductIdentity,
        MetadataGameScope scope,
        string installationPath,
        long logicalBytes,
        int sampledFiles,
        DateTimeOffset measuredUtc)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(canonicalId) || string.IsNullOrWhiteSpace(canonicalProductIdentity))
            throw new ArgumentException("A canonical game and product identity are required.");
        if (!Path.IsPathFullyQualified(installationPath)) throw new ArgumentException("The installation path must be absolute.", nameof(installationPath));
        if (logicalBytes <= 0 || sampledFiles <= 0) throw new ArgumentOutOfRangeException(nameof(logicalBytes), "A positive file-backed measurement is required.");
        measuredUtc = measuredUtc.ToUniversalTime();

        string fullPath = Path.GetFullPath(installationPath);
        string providerProductId = "path-sha256:" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(NormalizeLocalPath(fullPath)))).ToLowerInvariant();
        const string provider = "installed-storage";
        var identity = new MetadataGameIdentity(canonicalId, canonicalProductIdentity, scope,
            new Dictionary<string, string>(StringComparer.Ordinal) { [provider] = providerProductId });
        var evidence = new MetadataEvidenceRecord(canonicalId, MetadataField.InstalledLogicalSizeGb, provider, providerProductId,
            MetadataSourceLocationKind.LocalMeasurement, fullPath, scope, MetadataEvidenceOutcome.Available,
            logicalBytes / 1_000_000_000m, null, "GB-decimal", measuredUtc, measuredUtc + MaximumAge(MetadataField.InstalledLogicalSizeGb),
            SampleCount: sampledFiles, MeasurementBasis: "filesystem-logical-file-bytes", CanonicalProductIdentity: canonicalProductIdentity);
        return RecordAttempt(store, identity, MetadataField.InstalledLogicalSizeGb, evidence, measuredUtc);
    }

    public static TimeSpan MaximumAge(MetadataField field) => field switch
    {
        MetadataField.Artwork => TimeSpan.FromDays(90),
        MetadataField.InstalledLogicalSizeGb => TimeSpan.FromDays(7),
        MetadataField.MainStoryHours or MetadataField.MainPlusExtrasHours or MetadataField.CompletionistHours => TimeSpan.FromDays(30),
        MetadataField.DownloadSizeGb or MetadataField.PublishedDiskRequirementGb => TimeSpan.FromDays(30),
        _ => TimeSpan.FromDays(7)
    };

    private static MetadataCoverageAssessment MatchScope(MetadataGameScope expected, MetadataGameScope actual, MetadataField field)
    {
        if (expected == null || actual == null || string.IsNullOrWhiteSpace(expected.Title) || string.IsNullOrWhiteSpace(actual.Title)
            || !string.Equals(Normalize(expected.Title), Normalize(actual.Title), StringComparison.Ordinal))
            return Result(field, MetadataCoverageStatus.ScopeMismatch, "The exact game title was not matched.");
        var edition = MatchOptional(expected.Edition, actual.Edition, "edition", field);
        if (edition != null) return edition;
        var platform = MatchOptional(expected.Platform, actual.Platform, "platform", field);
        if (platform != null) return platform;
        if (expected.ReleaseYear.HasValue && actual.ReleaseYear != expected.ReleaseYear)
            return Result(field, MetadataCoverageStatus.ScopeMismatch, "The release year differs from the resolved game.");
        if (!expected.ReleaseYear.HasValue && actual.ReleaseYear.HasValue)
            return Result(field, MetadataCoverageStatus.NeedsReview, "The source has a release year that is not resolved for the canonical game.");
        return Result(field, MetadataCoverageStatus.ValidFresh, "Metadata scope matches the resolved game.");
    }

    private static MetadataCoverageAssessment? MatchOptional(string? expected, string? actual, string dimension, MetadataField field)
    {
        bool expectedKnown = !string.IsNullOrWhiteSpace(expected), actualKnown = !string.IsNullOrWhiteSpace(actual);
        if (expectedKnown && (!actualKnown || !string.Equals(Normalize(expected!), Normalize(actual!), StringComparison.Ordinal)))
            return Result(field, MetadataCoverageStatus.ScopeMismatch, $"The source {dimension} does not match the resolved game.");
        if (!expectedKnown && actualKnown)
            return Result(field, MetadataCoverageStatus.NeedsReview, $"The source has a {dimension} that is not resolved for the canonical game.");
        return null;
    }

    private static bool ProviderProductMatches(MetadataGameIdentity identity, MetadataEvidenceRecord evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence.Provider) || string.IsNullOrWhiteSpace(evidence.ProviderProductId)
            || identity.ProviderProductIds == null) return false;
        return identity.ProviderProductIds.TryGetValue(evidence.Provider, out string? expected)
            && !string.IsNullOrWhiteSpace(expected)
            && string.Equals(expected, evidence.ProviderProductId, StringComparison.Ordinal);
    }

    private static bool SourceMatchesProviderProduct(MetadataEvidenceRecord evidence)
    {
        if (evidence.SourceLocationKind == MetadataSourceLocationKind.LocalMeasurement
            && string.Equals(evidence.Provider, "installed-storage", StringComparison.OrdinalIgnoreCase))
            return Path.IsPathFullyQualified(evidence.SourceLocation)
                && string.Equals(evidence.ProviderProductId, LocalPathProductId(evidence.SourceLocation), StringComparison.Ordinal);
        if (evidence.SourceLocationKind != MetadataSourceLocationKind.Remote) return true;
        if (!Uri.TryCreate(evidence.SourceLocation, UriKind.Absolute, out var uri)) return false;
        if (string.Equals(evidence.Provider, "steam", StringComparison.OrdinalIgnoreCase))
            return ProviderPathMatches(uri, "store.steampowered.com", "Steam:", "app", evidence.ProviderProductId);
        if (string.Equals(evidence.Provider, "howlongtobeat", StringComparison.OrdinalIgnoreCase))
            return ProviderPathMatches(uri, "howlongtobeat.com", "HLTB:", "game", evidence.ProviderProductId);
        return true;
    }

    private static string LocalPathProductId(string path) => "path-sha256:" + Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(NormalizeLocalPath(Path.GetFullPath(path))))).ToLowerInvariant();

    private static string NormalizeLocalPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant();

    private static bool ProviderPathMatches(Uri uri, string host, string idPrefix, string pathPrefix, string providerProductId)
    {
        if (!string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase)) return false;
        if (!providerProductId.StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(providerProductId[idPrefix.Length..], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int id) || id <= 0
            || providerProductId[idPrefix.Length..] != id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            return false;
        if (uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || !uri.IsDefaultPort) return false;
        if (string.Equals(pathPrefix, "app", StringComparison.Ordinal)
            && string.Equals(uri.AbsolutePath, "/api/appdetails", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] keyValue = part.Split('=', 2);
                if (keyValue.Length == 2 && string.Equals(Uri.UnescapeDataString(keyValue[0]), "appids", StringComparison.OrdinalIgnoreCase))
                    return string.Equals(Uri.UnescapeDataString(keyValue[1]), id.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
            }
            return false;
        }
        string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2
            && string.Equals(segments[0], pathPrefix, StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[1], id.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static bool ValidSource(MetadataEvidenceRecord evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence.SourceLocation)) return false;
        if (evidence.SourceLocationKind == MetadataSourceLocationKind.Remote)
            return Uri.TryCreate(evidence.SourceLocation, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrWhiteSpace(uri.Host)
                && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.IsDefaultPort;
        return evidence.SourceLocationKind == MetadataSourceLocationKind.LocalMeasurement
            && Path.IsPathFullyQualified(evidence.SourceLocation);
    }

    private static string? ValidateAvailableValue(MetadataEvidenceRecord evidence)
    {
        string expectedUnit = UnitFor(evidence.Field);
        if (!string.Equals(evidence.Unit, expectedUnit, StringComparison.Ordinal))
            return $"Field {evidence.Field} requires unit '{expectedUnit}'.";
        if (evidence.Field == MetadataField.Artwork)
        {
            if (string.IsNullOrWhiteSpace(evidence.TextValue) || evidence.NumericValue.HasValue)
                return "Artwork evidence requires an image value and no numeric value.";
            if (evidence.ImageDecodedSuccessfully != true || evidence.ImageWidth is not > 0 || evidence.ImageHeight is not > 0)
                return "Artwork must decode successfully and report positive dimensions.";
            if (!IsSha256(evidence.ContentSha256)) return "Artwork requires a verified SHA-256 content hash.";
            string expectedPath = "covers/" + evidence.ContentSha256!.ToLowerInvariant() + ".img";
            if (!string.Equals(evidence.TextValue.Replace('\\', '/'), expectedPath, StringComparison.OrdinalIgnoreCase))
                return "Artwork path must be content-addressed by its verified SHA-256 hash.";
            return null;
        }
        if (!evidence.NumericValue.HasValue || evidence.NumericValue.Value <= 0 || evidence.NumericValue.Value >= 100000 || evidence.TextValue != null)
            return "Numeric metadata must be positive, finite, and contain no text value.";
        if (evidence.Field is MetadataField.MainStoryHours or MetadataField.MainPlusExtrasHours or MetadataField.CompletionistHours)
        {
            string expectedBasis = evidence.Field switch
            {
                MetadataField.MainStoryHours => "mainStory",
                MetadataField.MainPlusExtrasHours => "mainPlusExtras",
                _ => "completionist"
            };
            if (evidence.SampleCount is not > 0) return "Completion-time estimates require a positive provider sample count.";
            if (!string.Equals(evidence.MeasurementBasis, expectedBasis, StringComparison.Ordinal))
                return $"Field {evidence.Field} requires measurement basis '{expectedBasis}'.";
        }
        if (evidence.Field == MetadataField.InstalledLogicalSizeGb && evidence.SourceLocationKind != MetadataSourceLocationKind.LocalMeasurement)
            return "Installed logical size must come from a local installation measurement.";
        return null;
    }

    private static bool HasValue(MetadataEvidenceRecord evidence) => evidence.NumericValue.HasValue || evidence.TextValue != null;
    private static string UnitFor(MetadataField field) => field switch
    {
        MetadataField.Artwork => "image",
        MetadataField.MainStoryHours or MetadataField.MainPlusExtrasHours or MetadataField.CompletionistHours => "hours",
        MetadataField.DownloadSizeGb or MetadataField.PublishedDiskRequirementGb or MetadataField.InstalledLogicalSizeGb => "GB-decimal",
        _ => ""
    };

    private static bool IsSha256(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static string Normalize(string value) => string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    private static MetadataCoverageAssessment Result(MetadataField field, MetadataCoverageStatus status, string reason) =>
        new(field, status, false, false, reason);

    private static JsonObject ToJson(MetadataEvidenceRecord evidence) => new()
    {
        ["canonicalId"] = evidence.CanonicalId,
        ["canonicalProductIdentity"] = evidence.CanonicalProductIdentity,
        ["field"] = evidence.Field.ToString(),
        ["provider"] = evidence.Provider,
        ["providerProductId"] = evidence.ProviderProductId,
        ["sourceLocationKind"] = evidence.SourceLocationKind.ToString(),
        ["sourceLocation"] = evidence.SourceLocation,
        ["scope"] = new JsonObject
        {
            ["title"] = evidence.Scope.Title,
            ["edition"] = evidence.Scope.Edition,
            ["platform"] = evidence.Scope.Platform,
            ["releaseYear"] = evidence.Scope.ReleaseYear
        },
        ["outcome"] = evidence.Outcome.ToString(),
        ["numericValue"] = evidence.NumericValue,
        ["textValue"] = evidence.TextValue,
        ["unit"] = evidence.Unit,
        ["evidenceUtc"] = evidence.EvidenceUtc.ToString("O"),
        ["freshUntilUtc"] = evidence.FreshUntilUtc?.ToString("O"),
        ["failureReason"] = evidence.FailureReason,
        ["retryAfterUtc"] = evidence.RetryAfterUtc?.ToString("O"),
        ["sampleCount"] = evidence.SampleCount,
        ["measurementBasis"] = evidence.MeasurementBasis,
        ["contentSha256"] = evidence.ContentSha256,
        ["imageWidth"] = evidence.ImageWidth,
        ["imageHeight"] = evidence.ImageHeight,
        ["imageDecodedSuccessfully"] = evidence.ImageDecodedSuccessfully
    };

    private static JsonObject AssessmentJson(MetadataCoverageAssessment assessment) => new()
    {
        ["status"] = assessment.Status.ToString(),
        ["passed"] = assessment.Passed,
        ["fresh"] = assessment.Fresh,
        ["reason"] = assessment.Reason
    };

    private static void AppendRejected(JsonObject game, MetadataEvidenceRecord evidence, MetadataCoverageAssessment assessment, string reason)
    {
        JsonArray rejected = game["rejectedAttempts"] is JsonArray current ? (JsonArray)current.DeepClone() : new JsonArray();
        rejected.Add(new JsonObject { ["evidence"] = ToJson(evidence), ["validation"] = AssessmentJson(assessment), ["reason"] = reason });
        while (rejected.Count > MaximumRejectedAttempts) rejected.RemoveAt(0);
        game["rejectedAttempts"] = rejected;
    }

    private static JsonObject ReadDocument(LibraryStore store)
    {
        string path = Path.Combine(store.Cache, CacheFileName);
        if (!File.Exists(path)) return new JsonObject { ["schemaVersion"] = CurrentSchemaVersion, ["games"] = new JsonObject() };
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            JsonObject document = JsonNode.Parse(stream, new JsonNodeOptions { PropertyNameCaseInsensitive = false })?.AsObject()
                ?? new JsonObject { ["schemaVersion"] = CurrentSchemaVersion, ["games"] = new JsonObject() };
            MarkLegacyFieldsForReview(document);
            return document;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            throw new InvalidDataException("The metadata evidence cache could not be read and was preserved.", ex);
        }
    }

    private static void MarkLegacyFieldsForReview(JsonObject document)
    {
        if (document["games"] is JsonObject games)
        {
            foreach (var game in games.Select(entry => entry.Value).OfType<JsonObject>())
            {
                if (game["fields"] is not JsonObject fields) continue;
                foreach (var field in fields.Select(entry => entry.Value).OfType<JsonObject>())
                {
                    if (field["evidence"] is not JsonObject evidence
                        || !string.IsNullOrWhiteSpace(DataJson.Text(evidence["canonicalProductIdentity"]))) continue;
                    field["validation"] = AssessmentJson(Result(MetadataField.Artwork, MetadataCoverageStatus.NeedsReview,
                        "Legacy metadata evidence has no canonical product identity and must be refreshed."));
                }
            }
        }
        document["schemaVersion"] = CurrentSchemaVersion;
    }

    private static JsonObject GetObject(JsonObject parent, string key) => parent[key] is JsonObject value ? (JsonObject)value.DeepClone() : new JsonObject();

    private static DateTimeOffset ParseUtc(JsonNode? node) => DateTimeOffset.TryParse(DataJson.Text(node), out var result)
        ? result.ToUniversalTime() : DateTimeOffset.MinValue;
}
