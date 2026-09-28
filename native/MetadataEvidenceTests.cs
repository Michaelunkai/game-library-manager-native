using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

namespace GameLibrary.Native;

/// <summary>Isolated field identity, scope, units, freshness, unavailable, and cache-retention proofs.</summary>
public static class MetadataEvidenceTests
{
    public static int Run(string root)
    {
        int checks = 0;
        void Require(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(message);
        }

        MetadataGameIdentity identity = Identity("AgainsttheStorm", "Steam:1001", "Against the Storm", "Standard", "PC", 2022);
        MetadataEvidenceRecord mainStory = TimeEvidence(identity, MetadataField.MainStoryHours, 18.5m, 42, "mainStory");
        var valid = MetadataEvidence.Evaluate(identity, MetadataField.MainStoryHours, mainStory, FixedUtc.AddHours(1));
        Require(valid.Status == MetadataCoverageStatus.ValidFresh && valid.Passed && valid.Fresh,
            "A fresh, product-matched main-story estimate did not pass field coverage.");

        var wrongField = MetadataEvidence.Evaluate(identity, MetadataField.MainStoryHours,
            mainStory with { Field = MetadataField.CompletionistHours, MeasurementBasis = "completionist" }, FixedUtc.AddHours(1));
        Require(wrongField.Status == MetadataCoverageStatus.ScopeMismatch && !wrongField.Passed,
            "Completionist data was accepted as main-story time.");

        var wrongProduct = MetadataEvidence.Evaluate(identity, MetadataField.MainStoryHours,
            mainStory with { ProviderProductId = "Steam:9999" }, FixedUtc.AddHours(1));
        var wrongCanonicalProduct = MetadataEvidence.Evaluate(identity, MetadataField.MainStoryHours,
            mainStory with { CanonicalProductIdentity = "steam:9999" }, FixedUtc.AddHours(1));
        Require(wrongProduct.Status == MetadataCoverageStatus.IdentityMismatch,
            "A different provider product ID was accepted for the canonical game.");
        Require(wrongCanonicalProduct.Status == MetadataCoverageStatus.IdentityMismatch,
            "Evidence detached from the resolved canonical product was accepted.");

        var wrongTitle = MetadataEvidence.Evaluate(identity, MetadataField.MainStoryHours,
            mainStory with { Scope = mainStory.Scope with { Title = "Against the Storm: Reign of Ruin" } }, FixedUtc.AddHours(1));
        var wrongEdition = MetadataEvidence.Evaluate(identity, MetadataField.MainStoryHours,
            mainStory with { Scope = mainStory.Scope with { Edition = "Deluxe" } }, FixedUtc.AddHours(1));
        var wrongPlatform = MetadataEvidence.Evaluate(identity, MetadataField.MainStoryHours,
            mainStory with { Scope = mainStory.Scope with { Platform = "PlayStation 5" } }, FixedUtc.AddHours(1));
        var wrongYear = MetadataEvidence.Evaluate(identity, MetadataField.MainStoryHours,
            mainStory with { Scope = mainStory.Scope with { ReleaseYear = 2024 } }, FixedUtc.AddHours(1));
        Require(wrongTitle.Status == MetadataCoverageStatus.ScopeMismatch
            && wrongEdition.Status == MetadataCoverageStatus.ScopeMismatch
            && wrongPlatform.Status == MetadataCoverageStatus.ScopeMismatch
            && wrongYear.Status == MetadataCoverageStatus.ScopeMismatch,
            "A title, edition, platform, or release-year mismatch passed field coverage.");

        var wrongUnits = MetadataEvidence.Evaluate(identity, MetadataField.MainStoryHours,
            mainStory with { Unit = "minutes" }, FixedUtc.AddHours(1));
        var noSamples = MetadataEvidence.Evaluate(identity, MetadataField.MainStoryHours,
            mainStory with { SampleCount = 0 }, FixedUtc.AddHours(1));
        var wrongBasis = MetadataEvidence.Evaluate(identity, MetadataField.MainStoryHours,
            mainStory with { MeasurementBasis = "completionist" }, FixedUtc.AddHours(1));
        Require(wrongUnits.Status == MetadataCoverageStatus.InvalidValue
            && noSamples.Status == MetadataCoverageStatus.InvalidValue
            && wrongBasis.Status == MetadataCoverageStatus.InvalidValue,
            "Completion-time units, sample count, or scope were not validated independently.");

        var stale = MetadataEvidence.Evaluate(identity, MetadataField.MainStoryHours, mainStory, FixedUtc.AddDays(31));
        Require(stale.Status == MetadataCoverageStatus.ValidStale && !stale.Passed && !stale.Fresh,
            "Expired completion data was reported as fresh coverage.");

        var unavailable = mainStory with
        {
            Outcome = MetadataEvidenceOutcome.Unavailable,
            NumericValue = null,
            TextValue = null,
            FreshUntilUtc = null,
            FailureReason = "The provider has no main-story sample for this product.",
            RetryAfterUtc = FixedUtc.AddDays(1)
        };
        var unavailableResult = MetadataEvidence.Evaluate(identity, MetadataField.MainStoryHours, unavailable, FixedUtc.AddHours(1));
        var unexplainedUnavailable = MetadataEvidence.Evaluate(identity, MetadataField.MainStoryHours,
            unavailable with { FailureReason = null }, FixedUtc.AddHours(1));
        Require(unavailableResult.Status == MetadataCoverageStatus.Unavailable && !unavailableResult.Passed
            && unexplainedUnavailable.Status == MetadataCoverageStatus.InvalidValue,
            "Unavailable metadata did not require an explicit outcome reason.");

        var artwork = new MetadataEvidenceRecord(identity.CanonicalId, MetadataField.Artwork, "steam", "Steam:1001",
            MetadataSourceLocationKind.Remote, "https://store.steampowered.com/app/1001/", identity.Scope,
            MetadataEvidenceOutcome.Available, null, "covers/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.img", "image", FixedUtc, FixedUtc.AddDays(90),
            ContentSha256: new string('a', 64), ImageWidth: 640, ImageHeight: 360, ImageDecodedSuccessfully: true,
            CanonicalProductIdentity: identity.CanonicalProductIdentity);
        var artworkValid = MetadataEvidence.Evaluate(identity, MetadataField.Artwork, artwork, FixedUtc.AddDays(1));
        var artworkBroken = MetadataEvidence.Evaluate(identity, MetadataField.Artwork,
            artwork with { ImageDecodedSuccessfully = false }, FixedUtc.AddDays(1));
        var artworkWrongProductUrl = MetadataEvidence.Evaluate(identity, MetadataField.Artwork,
            artwork with { SourceLocation = "https://store.steampowered.com/app/9999/" }, FixedUtc.AddDays(1));
        var artworkWrongAddress = MetadataEvidence.Evaluate(identity, MetadataField.Artwork,
            artwork with { TextValue = "covers/" + new string('b', 64) + ".img" }, FixedUtc.AddDays(1));
        var artworkCredentialUrl = MetadataEvidence.Evaluate(identity, MetadataField.Artwork,
            artwork with { SourceLocation = "https://untrusted@store.steampowered.com/app/1001/" }, FixedUtc.AddDays(1));
        Require(artworkValid.Passed && artworkBroken.Status == MetadataCoverageStatus.InvalidValue,
            "Artwork decoding, dimensions, and content integrity were not enforced.");
        Require(artworkWrongProductUrl.Status == MetadataCoverageStatus.IdentityMismatch
            && artworkWrongAddress.Status == MetadataCoverageStatus.InvalidValue
            && artworkCredentialUrl.Status == MetadataCoverageStatus.InvalidProvenance,
            "Artwork accepted a mismatched product URL, content address, or credential-bearing source.");

        var localIdentity = Identity("Installed Sample", "local-game-id", "Installed Sample", "", "Windows", 2020);
        var localStore = new LibraryStore(Path.Combine(root, "installed-size-evidence-" + Guid.NewGuid().ToString("N")));
        string installPath = Path.GetFullPath(Path.Combine(root, "Installed Sample"));
        var installedSize = MetadataEvidence.RecordInstalledLogicalSize(localStore, localIdentity.CanonicalId,
            localIdentity.CanonicalProductIdentity, localIdentity.Scope, installPath, 37_250_000_000, 87, FixedUtc.ToOffset(TimeSpan.FromHours(2)));
        JsonObject? installedSizeField = MetadataEvidence.ReadField(localStore, localIdentity.CanonicalId, MetadataField.InstalledLogicalSizeGb);
        Require(installedSize.Passed && DataJson.Number(installedSizeField?["evidence"]?["numericValue"]) == 37.25
            && DataJson.Text(installedSizeField?["evidence"]?["sourceLocationKind"]) == "LocalMeasurement"
            && DataJson.Text(installedSizeField?["evidence"]?["measurementBasis"]) == "filesystem-logical-file-bytes"
            && DateTimeOffset.TryParse(DataJson.Text(installedSizeField?["evidence"]?["evidenceUtc"]), out var normalizedMeasureTime)
            && normalizedMeasureTime.Offset == TimeSpan.Zero,
            "A complete local logical-size measurement did not retain its exact installation scope and byte basis.");

        var legacyStore = new LibraryStore(Path.Combine(root, "legacy-metadata-evidence-" + Guid.NewGuid().ToString("N")));
        legacyStore.CacheData(MetadataEvidence.CacheFileName, new JsonObject
        {
            ["schemaVersion"] = 1,
            ["games"] = new JsonObject
            {
                [identity.CanonicalId] = new JsonObject
                {
                    ["fields"] = new JsonObject
                    {
                        [MetadataField.MainStoryHours.ToString()] = new JsonObject
                        {
                            ["evidence"] = new JsonObject { ["numericValue"] = 12.5 },
                            ["validation"] = new JsonObject { ["status"] = "ValidFresh", ["passed"] = true }
                        }
                    }
                }
            }
        }.ToJsonString());
        JsonObject? legacyField = MetadataEvidence.ReadField(legacyStore, identity.CanonicalId, MetadataField.MainStoryHours);
        Require(DataJson.Text(legacyField?["validation"]?["status"]) == "NeedsReview"
            && legacyField?["validation"]?["passed"]?.ToString() == "false",
            "Legacy field evidence without a canonical product identity remained accepted after the schema change.");

        var store = new LibraryStore(Path.Combine(root, "metadata-evidence-cache-" + Guid.NewGuid().ToString("N")));
        var saved = MetadataEvidence.RecordAttempt(store, identity, MetadataField.MainStoryHours, mainStory, FixedUtc.AddHours(1));
        var unavailableStored = MetadataEvidence.RecordAttempt(store, identity, MetadataField.MainStoryHours, unavailable, FixedUtc.AddHours(2));
        JsonObject? storedField = MetadataEvidence.ReadField(store, identity.CanonicalId, MetadataField.MainStoryHours);
        Require(saved.Passed && unavailableStored.Status == MetadataCoverageStatus.Unavailable
            && DataJson.Number(storedField?["evidence"]?["numericValue"]) == 18.5
            && DataJson.Text(storedField?["lastAttempt"]?["outcome"]) == "Unavailable"
            && DataJson.Text(storedField?["lastFailure"]) == unavailable.FailureReason,
            "A provider outage overwrote the last accepted field value or lost its failure/retry state.");

        var rejected = MetadataEvidence.RecordAttempt(store, identity, MetadataField.MainStoryHours,
            mainStory with { ProviderProductId = "Steam:9999" }, FixedUtc.AddHours(3));
        storedField = MetadataEvidence.ReadField(store, identity.CanonicalId, MetadataField.MainStoryHours);
        JsonObject afterRejectedCache = JsonNode.Parse(File.ReadAllText(Path.Combine(store.Cache, MetadataEvidence.CacheFileName)),
            new System.Text.Json.Nodes.JsonNodeOptions { PropertyNameCaseInsensitive = false })!.AsObject();
        Require(rejected.Status == MetadataCoverageStatus.IdentityMismatch
            && DataJson.Number(storedField?["evidence"]?["numericValue"]) == 18.5
            && afterRejectedCache["games"]?[identity.CanonicalId]?["rejectedAttempts"]?.AsArray().Count == 1,
            "Rejected attempts replaced the accepted value or were stored under the wrong cache level.");

        var upperIdentity = Identity("AgainsttheStorm", "product-upper", "Against the Storm", "Standard", "PC", 2022);
        var lowerIdentity = Identity("againstthestorm", "product-lower", "Against the Storm", "Standard", "PC", 2022);
        var caseStore = new LibraryStore(Path.Combine(root, "metadata-case-cache-" + Guid.NewGuid().ToString("N")));
        MetadataEvidence.RecordAttempt(caseStore, upperIdentity, MetadataField.MainStoryHours,
            TimeEvidence(upperIdentity, MetadataField.MainStoryHours, 12m, 4, "mainStory"), FixedUtc.AddHours(1));
        MetadataEvidence.RecordAttempt(caseStore, lowerIdentity, MetadataField.MainStoryHours,
            TimeEvidence(lowerIdentity, MetadataField.MainStoryHours, 13m, 5, "mainStory"), FixedUtc.AddHours(1));
        JsonObject cache = JsonNode.Parse(File.ReadAllText(Path.Combine(caseStore.Cache, MetadataEvidence.CacheFileName)),
            new System.Text.Json.Nodes.JsonNodeOptions { PropertyNameCaseInsensitive = false })!.AsObject();
        Require(cache["games"]?.AsObject().Count == 2
            && DataJson.Number(MetadataEvidence.ReadField(caseStore, "AgainsttheStorm", MetadataField.MainStoryHours)?["evidence"]?["numericValue"]) == 12
            && DataJson.Number(MetadataEvidence.ReadField(caseStore, "againstthestorm", MetadataField.MainStoryHours)?["evidence"]?["numericValue"]) == 13,
            "Case-sensitive canonical identities collapsed in the metadata evidence cache.");

        return checks;
    }

    private static MetadataGameIdentity Identity(string canonicalId, string productId, string title, string edition, string platform, int year,
        params (string Provider, string ProductId)[] extraProviders)
    {
        var products = new Dictionary<string, string>(StringComparer.Ordinal) { ["howlongtobeat"] = "HLTB:1001", ["steam"] = "Steam:1001" };
        foreach (var item in extraProviders) products[item.Provider] = item.ProductId;
        return new MetadataGameIdentity(canonicalId, productId, new MetadataGameScope(title, edition, platform, year), products);
    }

    private static MetadataEvidenceRecord TimeEvidence(MetadataGameIdentity identity, MetadataField field, decimal hours, int sampleCount, string basis) =>
        new(identity.CanonicalId, field, "howlongtobeat", identity.ProviderProductIds["howlongtobeat"],
            MetadataSourceLocationKind.Remote, "https://howlongtobeat.com/game/1001", identity.Scope,
            MetadataEvidenceOutcome.Available, hours, null, "hours", FixedUtc, FixedUtc.AddDays(30),
            SampleCount: sampleCount, MeasurementBasis: basis, CanonicalProductIdentity: identity.CanonicalProductIdentity);

    private static DateTimeOffset FixedUtc => new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
}
