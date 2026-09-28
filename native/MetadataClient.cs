using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace GameLibrary.Native;

public sealed class MetadataClient : IDisposable
{
    // Search formatting corroborated by exact provider results in the r53 audit.
    // The existing title guard must still accept the candidate: this cannot
    // add words, repair spelling or reinterpret joined Roman numerals.
    private static readonly IReadOnlyDictionary<string, string> ReadableSearchTitles = new[] {
        "The Legend of Tianding", "Blade Chimera", "Bo: Path of the Teal Lotus", "New Super Lucky's Tale",
        "Kaze and the Wild Masks", "The Eternal Castle Remastered", "Boxes: Lost Fragments", "Dungeons of Hinterberg",
        "Degrees of Separation", "Greak: Memories of Azur", "Lorelei and the Laser Eyes", "Lara Croft and the Temple of Osiris",
        "Little Kitty, Big City", "Children of the Sun", "Tails: The Backbone Preludes"
    }.ToDictionary(CompactTitle, title => title, StringComparer.Ordinal);
    private static string ReadableSearchTitle(string title) =>
        ReadableSearchTitles.TryGetValue(CompactTitle(title), out string? readable) && SameTitle(title, readable) ? readable : title;
    // These are the provider identities that cannot be recovered from the
    // Docker tag by ordinary title normalization. Keep this list closed: the
    // provider's "known" labels are evidence that an override was curated,
    // not permission to accept an arbitrary title for the requested id.
    private static readonly IReadOnlyDictionary<string, string[]> KnownProviderAliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["dragonquest1n2hd2dremake"] = new[] { "Dragon Quest I & II HD-2D Remake" },
        ["the-first-berserker-khazan"] = new[] { "The First Berserker Khazan" },
        ["007-first-light"] = new[] { "007 First Light" },
        ["ofashnsteel"] = new[] { "Of Ash and Steel" },
        ["Oceanhorn2"] = new[] { "Oceanhorn 2: Knights of the Lost Realm" },
        ["freedom-planet-2"] = new[] { "Freedom Planet 2" },
        ["tails-of-iron-2"] = new[] { "Tails of Iron 2: Whiskers of Winter" },
        ["song-of-nunu"] = new[] { "Song of Nunu: A League of Legends Story" },
        ["beyond-good-and-evil-20th-ae"] = new[] { "Beyond Good & Evil - 20th Anniversary Edition" },
        ["dying-light-2"] = new[] { "Dying Light 2 Stay Human: Reloaded Edition" },
        ["death-stranding-2"] = new[] { "DEATH STRANDING 2: ON THE BEACH" },
        ["the-last-oricru"] = new[] { "The Last Oricru - Final Cut" },
        ["planet-of-lana-ii"] = new[] { "Planet of Lana II" },
        ["avatarfrontiersofpandora"] = new[] { "Avatar: Frontiers of Pandora" },
        ["mafiatheoldcountry"] = new[] { "Mafia: The Old Country" }
    };
    private readonly HttpClient http;
    private readonly MetadataAvailability availability;
    private readonly Func<string, LibraryStore, CancellationToken, Task<JsonObject?>>? duration;
    private readonly bool defaultDuration;
    public MetadataClient(HttpMessageHandler? handler = null, MetadataAvailability? availability = null,
        Func<string, LibraryStore, CancellationToken, Task<JsonObject?>>? durationLookup = null)
    {
        this.availability = availability ?? new MetadataAvailability();
        duration = durationLookup ?? (handler == null ? CompletionDuration.Read : null);
        defaultDuration = handler == null && durationLookup == null;
        http = handler == null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(25);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GameLibraryNative/1.0");
    }
    private static string NormalizeTitle(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var result = new StringBuilder();
        string decomposed = value.Normalize(NormalizationForm.FormD);
        char previous = '\0';
        bool previousWasWord = false;
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            // Apostrophes are spelling punctuation, not title-word boundaries:
            // "Witch's" and the Docker-safe "witchs" should compare equally.
            if (character is '\'' or '\u2019') continue;
            if (character is '+' or '#')
            {
                if (result.Length > 0 && result[^1] != ' ') result.Append(' ');
                result.Append(character == '+' ? "plus" : "sharp");
                result.Append(' ');
                previous = '\0';
                previousWasWord = false;
                continue;
            }
            if (!char.IsLetterOrDigit(character))
            {
                if (result.Length > 0 && result[^1] != ' ') result.Append(' ');
                previous = '\0';
                previousWasWord = false;
                continue;
            }

            bool splitWord = previousWasWord &&
                ((char.IsLetter(previous) && char.IsDigit(character)) ||
                 (char.IsDigit(previous) && char.IsLetter(character)));
            if (splitWord && result.Length > 0 && result[^1] != ' ') result.Append(' ');
            result.Append(char.ToLowerInvariant(character));
            previous = character;
            previousWasWord = true;
        }
        return result.ToString().Trim();
    }
    private static string CompactTitle(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var result = new StringBuilder();
        string decomposed = value.Normalize(NormalizationForm.FormD);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark || character is '\'' or '\u2019') continue;
            if (character == '+') result.Append("plus");
            else if (character == '#') result.Append("sharp");
            else if (char.IsLetterOrDigit(character)) result.Append(char.ToLowerInvariant(character));
        }
        return result.ToString();
    }
    private static bool HasIdentityNumberToken(string normalized) =>
        normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(token =>
            token.Any(char.IsDigit) || (token.Length > 0 && token.All(character => character is 'i' or 'v' or 'x' or 'l' or 'c' or 'd' or 'm')));
    public static bool SameTitle(string expected, string actual)
    {
        var normalizedExpected = NormalizeTitle(expected);
        var normalizedActual = NormalizeTitle(actual);
        if (normalizedExpected.Length == 0 || normalizedActual.Length == 0) return false;
        if (normalizedExpected == normalizedActual) return true;
        // Compact comparison repairs harmless provider presentation changes
        // such as "LumenTale" versus "lumentale", but is disabled whenever a
        // numeric or Roman-numeral token could change the game's identity.
        return !HasIdentityNumberToken(normalizedExpected) && !HasIdentityNumberToken(normalizedActual) &&
            CompactTitle(expected) == CompactTitle(actual);
    }
    // Shared words do not establish game identity: sequels and spinoffs often contain the entire original title.
    private static bool TitleMatchesExpected(string expected, string actual) => SameTitle(expected, actual);
    internal static string DurationQueryTitle(string expected, string current, string cached, JsonObject? wandCatalog)
    {
        // Search engines need word boundaries that folder/tag names omit.
        // Formatting evidence never relaxes the existing game-identity check.
        foreach (string candidate in new[] { ReadableSearchTitle(cached), ReadableSearchTitle(current) })
            if (!string.Equals(candidate, expected, StringComparison.OrdinalIgnoreCase) && SameTitle(expected, candidate)) return candidate;
        var titles = (wandCatalog?["titles"] as JsonObject ?? new JsonObject()).Select(entry => ReadableSearchTitle(DataJson.Text(entry.Value?["name"])))
            .Where(name => SameTitle(expected, name)).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        return titles.Length == 1 ? titles[0] : expected;
    }

    private static JsonObject? ReadDurationTitleCatalog(LibraryStore store)
    {
        try
        {
            string path = Path.Combine(store.Cache, "wand-catalog.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 32 * 1024 * 1024) return null;
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }
    internal static string ExpectedTitle(Game game) => game.MetadataLookupTitle.Length > 0 ? game.MetadataLookupTitle : ReadableSearchTitle(CatalogIdentity.Known(game.Id)?.Title ?? (DockerIdentity.IsQualified(game.Id) && DockerIdentity.TryParse(game.Id, out var repository, out var tag)
        ? DockerIdentity.MetadataTitle(repository, tag)
        : game.Discovered && game.Id.Length > 0 ? LibraryStore.FormatName(game.Id) : game.Name));
    private static bool IsKnownAlias(Game game, string actual) =>
        KnownProviderAliases.TryGetValue(game.Id, out var aliases) && aliases.Any(alias => SameTitle(alias, actual));
    private static bool IsCuratedProviderOverride(JsonObject metadata) =>
        DataJson.Text(metadata["source"]?["image"]) == "steam-known" && DataJson.Text(metadata["source"]?["time"]) == "known-override";
    internal static bool HasAcceptableDiscoveredTitle(Game game) =>
        TitleMatchesExpected(ExpectedTitle(game), game.Name) || IsKnownAlias(game, game.Name);
    public static bool MatchesGame(Game game, JsonObject metadata)
    {
        if (game.RequiresGameIdentity || game.MetadataIdentityConflict) return false;
        if (!string.Equals(DataJson.Text(metadata["id"]), game.Id, StringComparison.Ordinal)) return false;
        string actual = DataJson.Text(metadata["name"]);
        if (game.MetadataSteamAppId > 0) return DataJson.Number(metadata["steamAppId"]) == game.MetadataSteamAppId &&
            DataJson.Text(metadata["source"]?["identity"]) == "steam-direct" && InstalledPlatformIdentity.ValidTitle(actual);
        if (TitleMatchesExpected(ExpectedTitle(game), actual)) return true;
        // A non-equivalent title is accepted only for a closed, explicitly
        // curated provider alias and its provider override marker. This
        // rejects false positives such as a same-id RAWG result for an
        // unrelated game.
        return IsCuratedProviderOverride(metadata) && IsKnownAlias(game, actual);
    }
    private static bool DurationReleaseYearMatchesSteam(JsonObject raw)
    {
        if (DataJson.Text(raw["source"]?["identity"]) != "steam-direct"
            || DataJson.Text(raw["source"]?["releaseYear"]) != "steam-direct"
            || DataJson.Number(raw["steamAppId"]) <= 0
            || !CompletionDuration.HasSource(raw)) return true;
        double steamYear = DataJson.Number(raw["steamReleaseYear"]);
        return steamYear < 1970 || DataJson.Number(raw["timeIdentityReleaseYear"]) == steamYear;
    }

    private static bool HasSecureImage(JsonObject metadata) =>
        Uri.TryCreate(DataJson.Text(metadata["image"]), UriKind.Absolute, out var image)
        && image.Scheme == Uri.UriSchemeHttps && !image.IsLoopback;

    private static void MergeVerifiedSteamArtwork(JsonObject raw, JsonObject steam)
    {
        foreach (string key in new[] { "image", "steamAppId", "steamReleaseYear", "diskRequirementGb" })
            raw[key] = steam[key]?.DeepClone();
        var sources = raw["source"] as JsonObject ?? new JsonObject();
        foreach (string key in new[] { "identity", "releaseYear", "image", "diskRequirement" })
            sources[key] = steam["source"]?[key]?.DeepClone();
        raw["source"] = sources;
    }

    private static bool TrySteamProduct(JsonObject raw, bool directSteamLookup, out int appId)
    {
        double value = DataJson.Number(raw["steamAppId"]);
        appId = value is > 0 and <= int.MaxValue && value == Math.Truncate(value) ? (int)value : 0;
        return directSteamLookup && appId > 0
            && DataJson.Text(raw["source"]?["identity"]) == "steam-direct"
            && DataJson.Text(raw["source"]?["image"]) == "steam-direct";
    }

    private static bool TryDecimal(JsonNode? node, out decimal value)
    {
        value = 0;
        double numeric = DataJson.Number(node);
        if (!double.IsFinite(numeric) || numeric <= 0 || numeric >= 100000) return false;
        try { value = Convert.ToDecimal(numeric, CultureInfo.InvariantCulture); return value > 0; }
        catch (OverflowException) { return false; }
    }

    private static int PositiveSamples(JsonNode? node)
    {
        double numeric = DataJson.Number(node);
        return double.IsFinite(numeric) && numeric > 0 && numeric <= int.MaxValue && numeric == Math.Truncate(numeric)
            ? (int)numeric : 0;
    }

    private static void RecordEvidence(
        LibraryStore store,
        string canonicalId,
        string canonicalProductIdentity,
        string provider,
        string providerProductId,
        string sourceLocation,
        MetadataGameScope scope,
        MetadataField field,
        DateTimeOffset observedUtc,
        MetadataEvidenceOutcome outcome,
        decimal? numericValue = null,
        string? textValue = null,
        string unit = "",
        string? failureReason = null,
        DateTimeOffset? retryAfterUtc = null,
        int? sampleCount = null,
        string? measurementBasis = null,
        string? contentSha256 = null,
        int? imageWidth = null,
        int? imageHeight = null,
        bool? imageDecodedSuccessfully = null)
    {
        var identity = new MetadataGameIdentity(canonicalId, canonicalProductIdentity, scope,
            new Dictionary<string, string>(StringComparer.Ordinal) { [provider] = providerProductId });
        var evidence = new MetadataEvidenceRecord(canonicalId, field, provider, providerProductId,
            MetadataSourceLocationKind.Remote, sourceLocation, scope, outcome, numericValue, textValue, unit,
            observedUtc, outcome == MetadataEvidenceOutcome.Available ? observedUtc + MetadataEvidence.MaximumAge(field) : null,
            failureReason, retryAfterUtc, sampleCount, measurementBasis, contentSha256, imageWidth, imageHeight,
            imageDecodedSuccessfully, canonicalProductIdentity);
        MetadataEvidence.RecordAttempt(store, identity, field, evidence, observedUtc);
    }

    private static void RecordFetchedEvidence(
        LibraryStore store,
        Game game,
        JsonObject raw,
        bool directSteamLookup,
        bool coverRequested,
        string? artworkRelativePath,
        string? artworkSha256,
        int artworkWidth,
        int artworkHeight,
        DateTimeOffset observedUtc)
    {
        try
        {
            if (TrySteamProduct(raw, directSteamLookup, out int appId))
            {
                string steamProductId = "Steam:" + appId.ToString(CultureInfo.InvariantCulture);
                string canonicalProductIdentity = "steam:" + appId.ToString(CultureInfo.InvariantCulture);
                int steamReleaseYear = (int)DataJson.Number(raw["steamReleaseYear"]);
                var steamScope = new MetadataGameScope(DataJson.Text(raw["name"]), null, "PC", steamReleaseYear >= 1970 ? steamReleaseYear : null);
                string steamSource = SteamMetadata.ProductDetailsUrl(appId);

                if (coverRequested)
                {
                    if (artworkRelativePath != null && artworkSha256 != null && artworkWidth > 0 && artworkHeight > 0)
                        RecordEvidence(store, game.Id, canonicalProductIdentity, "steam", steamProductId, steamSource, steamScope,
                            MetadataField.Artwork, observedUtc, MetadataEvidenceOutcome.Available, textValue: artworkRelativePath,
                            unit: "image", contentSha256: artworkSha256, imageWidth: artworkWidth, imageHeight: artworkHeight,
                            imageDecodedSuccessfully: true);
                    else
                        RecordEvidence(store, game.Id, canonicalProductIdentity, "steam", steamProductId, steamSource, steamScope,
                            MetadataField.Artwork, observedUtc, MetadataEvidenceOutcome.Unavailable, unit: "image",
                            failureReason: "Steam did not provide decodable artwork for this app.", retryAfterUtc: observedUtc.AddDays(7));
                }

                if (TryDecimal(raw["diskRequirementGb"], out decimal diskRequirement))
                    RecordEvidence(store, game.Id, canonicalProductIdentity, "steam", steamProductId, steamSource, steamScope,
                        MetadataField.PublishedDiskRequirementGb, observedUtc, MetadataEvidenceOutcome.Available,
                        numericValue: diskRequirement, unit: "GB-decimal");
                else
                    RecordEvidence(store, game.Id, canonicalProductIdentity, "steam", steamProductId, steamSource, steamScope,
                        MetadataField.PublishedDiskRequirementGb, observedUtc, MetadataEvidenceOutcome.Unavailable,
                        unit: "GB-decimal", failureReason: "Steam did not publish a Windows storage requirement for this app.",
                        retryAfterUtc: observedUtc.AddDays(30));

                RecordEvidence(store, game.Id, canonicalProductIdentity, "steam", steamProductId, steamSource, steamScope,
                    MetadataField.DownloadSizeGb, observedUtc, MetadataEvidenceOutcome.Unavailable, unit: "GB-decimal",
                    failureReason: "Steam app details do not provide a verified download size.", retryAfterUtc: observedUtc.AddDays(30));
            }

            if (!CompletionDuration.HasSource(raw) || !CompletionDuration.TryGetProductId(raw, out string hltbProductId)) return;
            string durationSource = DataJson.Text(raw["timeUrl"]);
            string title = DataJson.Text(raw["timeTitle"]);
            int durationReleaseYear = (int)DataJson.Number(raw["timeIdentityReleaseYear"]);
            string canonical = TrySteamProduct(raw, directSteamLookup, out int steamAppId)
                ? "steam:" + steamAppId.ToString(CultureInfo.InvariantCulture)
                : "game:" + game.Id;
            var durationScope = new MetadataGameScope(title, null, "PC", durationReleaseYear >= 1970 ? durationReleaseYear : null);
            bool freshObservation = CompletionDuration.IsLiveSource(raw);
            string? unavailableReason = freshObservation ? null :
                "The duration response came from cache; its original provider observation time is unavailable for fresh coverage.";
            DateTimeOffset? retryAfter = freshObservation ? null : observedUtc.AddDays(1);
            var styles = raw["timeStyles"] as JsonObject;
            var fields = new[]
            {
                (Field: MetadataField.MainStoryHours, Key: "main", Basis: "mainStory"),
                (Field: MetadataField.MainPlusExtrasHours, Key: "extra", Basis: "mainPlusExtras"),
                (Field: MetadataField.CompletionistHours, Key: "completionist", Basis: "completionist")
            };
            foreach (var item in fields)
            {
                JsonNode? style = styles?[item.Key];
                JsonNode? valueNode = item.Field == MetadataField.MainStoryHours ? raw["time"] : style?["avg"];
                JsonNode? sampleNode = item.Field == MetadataField.MainStoryHours ? raw["timeSamples"] : style?["polled"];
                decimal hours = 0;
                int samples = PositiveSamples(sampleNode);
                if (freshObservation && samples > 0 && TryDecimal(valueNode, out hours))
                    RecordEvidence(store, game.Id, canonical, "howlongtobeat", hltbProductId, durationSource, durationScope,
                        item.Field, observedUtc, MetadataEvidenceOutcome.Available, numericValue: hours, unit: "hours",
                        sampleCount: samples, measurementBasis: item.Basis);
                else
                    RecordEvidence(store, game.Id, canonical, "howlongtobeat", hltbProductId, durationSource, durationScope,
                        item.Field, observedUtc, MetadataEvidenceOutcome.Unavailable, unit: "hours",
                        failureReason: unavailableReason ?? "HowLongToBeat did not provide a sampled estimate for this completion scope.",
                        retryAfterUtc: retryAfter ?? observedUtc.AddDays(30));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
            or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            store.Log("Metadata evidence could not be persisted for " + game.Id + ": " + ex.Message);
        }
    }

    public async Task<JsonObject> Refresh(Game game, LibraryStore store, bool cover, bool time, CancellationToken cancellation)
    {
        if (game.RequiresGameIdentity) throw new InvalidOperationException("This repository tag does not identify a game. Publisher title evidence is required before looking up game metadata.");
        if (game.IsNonGame) throw new InvalidOperationException("Game metadata does not apply to this utility or backup entry.");
        if (game.MetadataIdentityConflict) throw new InvalidOperationException("Local platform identities conflict or could not be validated. Existing personal data was preserved.");
        string queryName = ExpectedTitle(game);
        string url = SyncClient.Production + "api/game-metadata?id=" + Uri.EscapeDataString(game.Id) + "&name=" + Uri.EscapeDataString(queryName) + "&category=" + Uri.EscapeDataString(game.Category);
        cancellation.ThrowIfCancellationRequested();
        JsonObject raw;
        bool directSteamLookup = false;
        bool validateDurationAgainstSteam = false;
        if (game.MetadataSteamAppId > 0)
        {
            raw = await SteamMetadata.Read(http, game, queryName, cancellation, store);
            directSteamLookup = true;
            validateDurationAgainstSteam = true;
            queryName = DataJson.Text(raw["name"]);
        }
        else
        {
        try
        {
            availability.RequireAvailable();
            string payload;
            try { payload = await http.GetStringAsync(url, cancellation); availability.Succeeded(); }
            catch (HttpRequestException ex) when (ex.StatusCode == null || (int)ex.StatusCode >= 500 || (int)ex.StatusCode is 408 or 429)
            { throw availability.Failed(); }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            { throw availability.Failed(); }
            raw = JsonNode.Parse(payload)?.AsObject() ?? throw new FormatException("Empty metadata response.");
        }
        catch (MetadataUnavailableException) when (cover || (time && duration != null))
        {
            raw = new JsonObject { ["success"] = true, ["id"] = game.Id, ["name"] = queryName, ["category"] = game.Category };
            if (cover)
            {
                try
                {
                    raw = await SteamMetadata.Read(http, game, queryName, cancellation, store);
                    directSteamLookup = true;
                    validateDurationAgainstSteam = true;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception ex) when (time && duration != null) { store.Log("Steam artwork preserved: " + ex.Message); }
            }
        }
        }
        if (raw["success"]?.GetValue<bool>() != true) throw new FormatException(DataJson.Text(raw["error"], "Metadata is unavailable."));
        if (!MatchesGame(game, raw)) throw new FormatException("The provider returned a different title (" + DataJson.Text(raw["name"]) + "). Existing metadata was preserved.");
        if (cover && !directSteamLookup && !HasSecureImage(raw))
        {
            try
            {
                var steamArtwork = await SteamMetadata.Read(http, game, queryName, cancellation, store);
                if (!MatchesGame(game, steamArtwork) || !HasSecureImage(steamArtwork))
                    throw new FormatException("Steam did not confirm an exact game identity with secure artwork.");
                MergeVerifiedSteamArtwork(raw, steamArtwork);
                directSteamLookup = true;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception ex) { store.Log("Steam artwork fallback preserved for " + game.Id + ": " + ex.Message); }
        }
        if (validateDurationAgainstSteam && !DurationReleaseYearMatchesSteam(raw))
        {
            raw.Remove("time");
            foreach (string field in new[] { "timeUrl", "timeSamples", "timeStyles", "timeTitle", "timeAlias", "timeQuery", "timeIdentityReleaseYear" }) raw.Remove(field);
            if (raw["source"] is JsonObject sources) sources.Remove("time");
            store.Log("Completion hours preserved: HowLongToBeat release year did not match the verified Steam game identity.");
        }
        if (time && duration != null && (DataJson.Number(raw["time"]) <= 0 || !CompletionDuration.HasSource(raw)))
        {
            try
            {
                var priorDuration = store.ReadMetadata()[game.Id];
                string durationTitle = DurationQueryTitle(queryName, DataJson.Text(raw["name"]),
                    DataJson.Text(priorDuration?["timeQuery"], DataJson.Text(priorDuration?["matchedTitle"])), ReadDurationTitleCatalog(store));
                int releaseYear = DataJson.Text(raw["source"]?["releaseYear"]) == "steam-direct" &&
                    DataJson.Text(raw["source"]?["identity"]) == "steam-direct" && DataJson.Number(raw["steamAppId"]) > 0
                    ? (int)DataJson.Number(raw["steamReleaseYear"]) : 0;
                var times = defaultDuration ? await CompletionDuration.ReadWithReleaseYear(durationTitle, store, cancellation, releaseYear)
                    : await duration(durationTitle, store, cancellation);
                if (times != null && releaseYear > 0 && DataJson.Number(times["timeIdentityReleaseYear"]) != releaseYear)
                    throw new FormatException("HowLongToBeat release year did not match the verified Steam game identity.");
                if (times != null)
                {
                    raw["timeQuery"] = durationTitle;
                    foreach (var field in times.Where(field => field.Key != "source")) raw[field.Key] = field.Value?.DeepClone();
                    var sources = raw["source"] as JsonObject ?? new JsonObject();
                    sources["time"] = times["source"]?["time"]?.DeepClone(); raw["source"] = sources.DeepClone();
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception ex) { store.Log("Completion hours preserved for " + game.Id + ": " + ex.Message); }
        }
        if (time && duration != null && !CompletionDuration.HasSource(raw)) raw.Remove("time");
        var attribution = new JsonObject();
        if (DataJson.Text(raw["source"]?["identity"]) == "steam-direct") attribution["identity"] = "steam-direct";
        var result = new JsonObject { ["fetchedAt"] = DateTime.UtcNow.ToString("O"), ["matchedTitle"] = raw["name"]?.DeepClone(), ["source"] = attribution };
        if (DataJson.Number(raw["steamAppId"]) > 0) result["steamAppId"] = raw["steamAppId"]?.DeepClone();
        if (DataJson.Number(raw["steamReleaseYear"]) >= 1970 && DataJson.Text(raw["source"]?["releaseYear"]) == "steam-direct") {
            result["steamReleaseYear"] = raw["steamReleaseYear"]?.DeepClone(); attribution["releaseYear"] = "steam-direct";
        }
        if (DataJson.Number(raw["diskRequirementGb"]) > 0)
        {
            result["diskRequirementGb"] = raw["diskRequirementGb"]?.DeepClone();
            result["steamAppId"] = raw["steamAppId"]?.DeepClone();
            attribution["diskRequirement"] = raw["source"]?["diskRequirement"]?.DeepClone();
        }
        if (game.Discovered)
        {
            result["name"] = raw["name"]?.DeepClone();
            result["category"] = raw["category"]?.DeepClone();
        }
        // A generic genre estimate must not downgrade an existing catalog time.
        if (time && DataJson.Number(raw["time"]) is > 0 and < 100000 && DataJson.Text(raw["source"]?["time"]) != "genre-estimate")
        {
            result["time"] = raw["time"]?.DeepClone();
            if (DataJson.Text(raw["source"]?["time"]) == "howlongtobeat-live")
                result["timeFetchedAt"] = DateTime.UtcNow.ToString("O");
            attribution["time"] = raw["source"]?["time"]?.DeepClone();
            foreach (string field in new[] { "timeUrl", "timeSamples", "timeStyles", "timeTitle", "timeAlias", "timeQuery", "timeIdentityReleaseYear" })
                result[field] = raw[field]?.DeepClone();
        }
        string? artworkRelativePath = null;
        string? artworkSha256 = null;
        int artworkWidth = 0;
        int artworkHeight = 0;
        DateTimeOffset observedUtc;
        if (cover && HasSecureImage(raw))
        {
            try
            {
                using var response = await http.GetAsync(new Uri(DataJson.Text(raw["image"]), UriKind.Absolute), HttpCompletionOption.ResponseHeadersRead, cancellation);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > 8 * 1024 * 1024) throw new FormatException("Cover exceeds the 8 MB limit.");
                await using var source = await response.Content.ReadAsStreamAsync(cancellation);
                using var content = new MemoryStream(); var buffer = new byte[16384];
                int count;
                while ((count = await source.ReadAsync(buffer, cancellation)) > 0)
                {
                    if (content.Length + count > 8 * 1024 * 1024) throw new FormatException("Cover exceeds the 8 MB limit.");
                    content.Write(buffer, 0, count);
                }
                content.Position = 0;
                var decoder = BitmapDecoder.Create(content, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count == 0 || decoder.Frames.Any(f => f.PixelWidth <= 0 || f.PixelHeight <= 0 || f.PixelWidth > 12000 || f.PixelHeight > 12000)) throw new FormatException("Invalid cover dimensions.");
                var bytes = content.ToArray();
                artworkSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                artworkWidth = decoder.Frames[0].PixelWidth;
                artworkHeight = decoder.Frames[0].PixelHeight;
                string relative = "covers/" + artworkSha256 + ".img";
                string file = LibraryStore.SafeChild(store.Cache, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                bool cachedBytesMatch = false;
                try
                {
                    if (File.Exists(file) && new FileInfo(file).Length == bytes.Length)
                    {
                        await using var cached = File.OpenRead(file);
                        cachedBytesMatch = (await SHA256.HashDataAsync(cached, cancellation)).AsSpan().SequenceEqual(SHA256.HashData(bytes));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                if (!cachedBytesMatch)
                {
                    string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try { await File.WriteAllBytesAsync(temp, bytes, cancellation); File.Move(temp, file, true); ArtworkFile.Replaced(file); }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
                artworkRelativePath = relative;
                result["cover"] = relative;
                attribution["image"] = raw["source"]?["image"]?.DeepClone();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException)
            {
                artworkRelativePath = null;
                artworkSha256 = null;
                artworkWidth = artworkHeight = 0;
                result.Remove("cover");
                attribution.Remove("image");
                store.Log("Cover preserved for " + game.Id + ": " + ex.Message);
            }
        }
        observedUtc = DateTimeOffset.UtcNow;
        if (result["time"] == null && result["cover"] == null) throw new FormatException("The provider returned no usable cover or completion time.");
        var all = store.ReadMetadata();
        var previous = all[game.Id] as JsonObject ?? new JsonObject();
        if (!CatalogIdentity.AcceptsMetadata(game.Id, previous) || !InstalledPlatformIdentity.Accepts(game, previous)) previous = new JsonObject();
        // Source labels describe the fields actually replaced, not every field in the response.
        var previousSource = previous["source"] as JsonObject ?? new JsonObject();
        foreach (var field in attribution) previousSource[field.Key] = field.Value?.DeepClone();
        foreach (var field in result.Where(f => f.Key != "source")) previous[field.Key] = field.Value?.DeepClone();
        previous["source"] = previousSource.DeepClone();
        all[game.Id] = previous.DeepClone();
        store.CacheData("metadata.json", all.ToJsonString());
        RecordFetchedEvidence(store, game, raw, directSteamLookup, cover, artworkRelativePath, artworkSha256,
            artworkWidth, artworkHeight, observedUtc);
        return result;
    }
    public void Dispose() => http.Dispose();
}
public partial class MainWindow
{
    private bool automaticMetadataRunning;
    private JsonObject? metadataAttempts;
    private Func<MetadataClient>? automaticMetadataClientProof;
    internal static bool TimeNeedsRefresh(Game game, DateTime now) => game.Time <= 0 || game.TimeVerifiedAt == default ||
        game.TimeVerifiedAt > now.AddMinutes(5) || now - game.TimeVerifiedAt >= TimeSpan.FromDays(30);
    internal static bool PlatformIdentityPending(Game game, JsonObject? identityCache) => game.MetadataSteamAppId > 0 && (identityCache == null ||
        DataJson.Number(identityCache["steamAppId"]) != game.MetadataSteamAppId ||
        (DataJson.Text(identityCache["source"]?["identity"]) != "steam-direct" && DataJson.Text(identityCache["source"]?["image"]) != "steam-direct"));
    internal static bool DurationWorkAllowed(Game game, JsonObject? identityCache, bool durationWaiting) =>
        !durationWaiting || !ArtworkFile.IsUsable(game.Cover) || PlatformIdentityPending(game, identityCache);
    internal const int DurationContractRevision = 2;
    internal static bool MetadataBackgroundAllowed(Game game, bool admin, bool protectedCategory, bool hiddenCategory) =>
        game.Installed || admin || (!protectedCategory && !hiddenCategory);
    internal static bool MetadataDue(Game game, JsonObject attempts, DateTime now)
    {
        if (game.IsNonGame || game.RequiresGameIdentity || game.MetadataIdentityConflict || string.IsNullOrWhiteSpace(game.Name)) return false;
        if (game.Installed && TimeNeedsRefresh(game, now) && DataJson.Number(attempts[game.Id]?["durationContractRevision"]) != DurationContractRevision) return true;
        if (game.MetadataSteamAppId > 0 && DataJson.Number(attempts[game.Id]?["steamAppId"]) != game.MetadataSteamAppId) return true;
        // A previously accepted provider name can be loaded back into a
        // discovered Docker row. Revisit it when it no longer matches the
        // tag identity, but honor the same persisted retry barrier as missing
        // artwork. Otherwise one rejected provider result is selected again
        // on every loop and can keep the UI/logging/storage stack busy forever.
        bool discoveredTitleMismatch = game.Discovered && !MetadataClient.HasAcceptableDiscoveredTitle(game);
        if (!discoveredTitleMismatch && ArtworkFile.IsUsable(game.Cover) && !TimeNeedsRefresh(game, now)) return false;
        string attemptedTitle = DataJson.Text(attempts[game.Id]?["queryTitle"]);
        // Equivalent joined titles can yield entirely different search results.
        // Persisting the new exact query makes this migration occur only once.
        if (attemptedTitle.Length > 0 && !string.Equals(attemptedTitle, MetadataClient.ExpectedTitle(game), StringComparison.OrdinalIgnoreCase)) return true;
        // One-time migration of old failures that searched the generated build tag.
        // The next attempt records its actual title, including during an outage.
        if (attemptedTitle.Length == 0 && DockerIdentity.IsQualified(game.Id)
            && DockerIdentity.TryParse(game.Id, out var repository, out var tag) && tag.StartsWith("gmenu-", StringComparison.Ordinal)
            && DockerIdentity.MetadataTitle(repository, tag) != LibraryStore.FormatName(tag)) return true;
        // Older releases treated artwork-only success as a complete refresh.
        // Retry missing fields promptly instead of retaining that 24-hour barrier.
        if (attempts[game.Id]?["available"]?.GetValue<bool>() == true &&
            DateTime.TryParse(DataJson.Text(attempts[game.Id]?["attemptedAt"]), out var partialAt))
            return partialAt.ToUniversalTime().AddMinutes(5) <= now;
        return !DateTime.TryParse(DataJson.Text(attempts[game.Id]?["retryAfter"]), out var retry) || retry.ToUniversalTime() <= now;
    }
    private readonly MetadataAvailability metadataAvailability = new();
    private void ScheduleMetadata()
    {
        if (offline || !ready || closing || automaticMetadataRunning || Program.TestReport != null || !IsVisible) return;
        automaticMetadataRunning = true;
        try
        {
            _ = Dispatcher.BeginInvoke(new Action(() => ObserveUiOperation("Automatic metadata", RunAutomaticMetadata)), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        catch (InvalidOperationException) { automaticMetadataRunning = false; }
        catch (Exception ex) { automaticMetadataRunning = false; Store.Log("Automatic metadata dispatch failed: " + ex); }
    }
    private async Task RunAutomaticMetadata()
    {
        int updated = 0, unavailable = 0;
        try
        {
            if (metadataAttempts == null)
            {
                try { metadataAttempts = JsonNode.Parse(File.ReadAllText(Path.Combine(Store.Cache, "metadata-attempts.json")))!.AsObject(); }
                catch { metadataAttempts = new(); }
            }
            using var client = Program.TestReport != null && automaticMetadataClientProof != null
                ? automaticMetadataClientProof() : new MetadataClient(availability: metadataAvailability);
            while (!closing && IsVisible)
            {
                await Task.Delay(5000, lifetime.Token);
                // Prioritize rendered rows, then eligible catalog and local entries.
                // Persisted retry times prevent repeated requests on every startup.
                var visible = filtered.Where(g => GameList.ItemContainerGenerator.ContainerFromItem(g) is System.Windows.FrameworkElement { IsVisible: true });
                var background = Games.Where(g => MetadataBackgroundAllowed(g, true, false, false))
                    .OrderByDescending(g => g.Installed).ThenByDescending(g => g.Added);
                bool durationWaiting = CompletionDuration.RetryAt(Store) > DateTime.UtcNow;
                var currentMetadata = Store.ReadMetadata();
                var game = visible.Concat(background).DistinctBy(g => g.Id)
                    .FirstOrDefault(g => DurationWorkAllowed(g, currentMetadata[g.Id] as JsonObject, durationWaiting) &&
                        MetadataDue(g, metadataAttempts, DateTime.UtcNow));
                if (game == null) break;
                ArtworkStatus.Visibility = System.Windows.Visibility.Visible;
                ArtworkStatus.Text = "Looking up artwork & time · " + game.Name;
                bool success = false;
                string? unavailableReason = null;
                try
                {
                    var identityCache = Store.ReadMetadata()[game.Id] as JsonObject;
                    bool verifyPlatform = PlatformIdentityPending(game, identityCache);
                    await client.Refresh(game, Store, verifyPlatform || !ArtworkFile.IsUsable(game.Cover), verifyPlatform || TimeNeedsRefresh(game, DateTime.UtcNow), lifetime.Token);
                    success = true; updated++;
                    var current = Games.FirstOrDefault(g => g.Id == game.Id);
                    var refreshed = Store.LoadGames(State, Sync.Effective(State)).FirstOrDefault(g => g.Id == game.Id);
                    if (current != null && refreshed != null)
                    {
                        ApplyAutomaticMetadata(current, refreshed);
                    }
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
                catch (MetadataUnavailableException ex)
                {
                    ArtworkStatus.Text = ex.Message;
                    Store.Log(ex.Message);
                    metadataAttempts[game.Id] = new JsonObject { ["retryAfter"] = ex.RetryAt.ToString("O"), ["reason"] = ex.Message, ["queryTitle"] = MetadataClient.ExpectedTitle(game), ["steamAppId"] = game.MetadataSteamAppId, ["durationContractRevision"] = DurationContractRevision };
                    Store.CacheData("metadata-attempts.json", metadataAttempts.ToJsonString());
                    continue;
                }
                catch (Exception ex)
                {
                    unavailable++; unavailableReason = ex.Message;
                    Store.Log("Automatic metadata preserved for " + game.Id + ": " + ex.Message);
                }
                metadataAttempts[game.Id] = new JsonObject
                {
                    ["attemptedAt"] = DateTime.UtcNow.ToString("O"), ["available"] = success,
                    ["queryTitle"] = MetadataClient.ExpectedTitle(game),
                    ["steamAppId"] = game.MetadataSteamAppId,
                    ["durationContractRevision"] = DurationContractRevision,
                    ["retryAfter"] = (success && (!ArtworkFile.IsUsable(game.Cover) || TimeNeedsRefresh(game, DateTime.UtcNow))
                        ? DateTime.UtcNow.AddMinutes(5) : DateTime.UtcNow.AddHours(success ? 24 : 1)).ToString("O"),
                    ["reason"] = unavailableReason
                };
                Store.CacheData("metadata-attempts.json", metadataAttempts.ToJsonString());
            }
            if (updated + unavailable > 0)
                ArtworkStatus.Text = $"Artwork & time · {updated} updated; {unavailable} unavailable. Refresh covers & times can retry.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Store.Log("Automatic metadata paused: " + ex.Message); }
        finally { automaticMetadataRunning = false; }
    }
    private void ApplyAutomaticMetadata(Game current, Game refreshed)
    {
        bool refilter = current.Category != refreshed.Category || current.Name != refreshed.Name ||
            (current.Time != refreshed.Time && SortBox.SelectedItem is "Time to Beat (Low–High)" or "Time to Beat (High–Low)");
        current.Cover = refreshed.Cover; current.Time = refreshed.Time; current.Name = refreshed.Name;
        current.TimeVerifiedAt = refreshed.TimeVerifiedAt;
        current.DiskRequirementGb = refreshed.DiskRequirementGb;
        current.MetadataLookupTitle = refreshed.MetadataLookupTitle; current.MetadataSteamAppId = refreshed.MetadataSteamAppId;
        current.MetadataIdentityConflict = refreshed.MetadataIdentityConflict;
current.Category = refreshed.Category; current.CategoryName = refreshed.CategoryName; current.Notify("");
        catalogStatsDirty = true;
        projectionMutationVersion++;
        if (refilter) ApplyFilter();
        else UpdateStats();
    }
    private void RefreshMetadataMenu(object sender, System.Windows.RoutedEventArgs e) => RefreshMetadata((Selected().Length > 0 ? Selected() : filtered.Where(g => g.Time <= 0 || !ArtworkFile.IsUsable(g.Cover))).ToArray());
    private void RefreshMetadata(Game[] games)
    {
        games = games.Where(g => !g.IsNonGame).ToArray();
        var dialog = new EditorWindow(this, "Refresh covers & times", games.Length == 0 ? "Select games to refresh their metadata. Existing covers and times stay available offline." : $"Refresh {games.Length} selected game(s) using the same metadata service as the website. Names and shared categories are preserved.");
        var covers = dialog.Check("Refresh cover images", true);
        var times = dialog.Check("Refresh approximate completion times", true);
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var cancellation = cancel.Token;
        bool running = false, refreshDialogClosed = false, cancellationDisposed = false;
        void DisposeRefreshCancellation()
        {
            if (cancellationDisposed) return;
            cancellationDisposed = true;
            try { cancel.Dispose(); } catch { }
        }
        var start = dialog.Action("Start refresh", () => { }, "StartMetadataRefresh");
        start.IsEnabled = games.Length > 0;
        start.Click += async (_, _) =>
        {
            if (running || (covers.IsChecked != true && times.IsChecked != true)) return;
            running = true; start.IsEnabled = false;
            int updated = 0, failed = 0;
            try
            {
                using var client = new MetadataClient(availability: metadataAvailability);
                foreach (var game in games)
                {
                    cancellation.ThrowIfCancellationRequested();
                    dialog.Notice.Text = $"{updated + failed}/{games.Length} · {game.Name}";
                    try { await client.Refresh(game, Store, covers.IsChecked == true, times.IsChecked == true, cancellation); updated++; }
                    catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
                    catch (MetadataUnavailableException ex) { dialog.Notice.Text = ex.Message; return; }
                    catch (Exception ex) { failed++; Store.Log("Metadata preserved for " + game.Id + ": " + ex.Message); }
                }
                dialog.Notice.Text = $"Updated {updated}; unavailable or title mismatch {failed}. Details are in the activity log.";
            }
            catch (OperationCanceledException) { dialog.Notice.Text = $"Stopped. {updated} completed updates were saved."; }
            finally
            {
                running = false;
                try { Reload(); }
                catch (Exception ex) { Store.Log("Metadata dialog reload failed; saved metadata was preserved: " + ex); }
                if (refreshDialogClosed) DisposeRefreshCancellation();
            }
        };
        dialog.Action("Stop refresh", () => { try { cancel.Cancel(); } catch (ObjectDisposedException) { } });
        dialog.Closed += (_, _) =>
        {
            refreshDialogClosed = true;
            try { cancel.Cancel(); } catch (ObjectDisposedException) { }
            if (!running) DisposeRefreshCancellation();
        };
        dialog.ShowDialog();
        refreshDialogClosed = true;
        if (!running) DisposeRefreshCancellation();
    }
}
