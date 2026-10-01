using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace GameLibrary.Native;

internal static class SearchPerformanceTests
{
    private const int CatalogSize = 1000;
    private const int ParityQueryCount = 200;
    private const int TimingQueryCount = 1000;
    private const int KeystrokeIterations = 200;
    private const double QueryCeilingMilliseconds = 2000;
    private const int BuildHeadroomFactor = 4;
    private const int CatalogSeed = 20260924;

    private static readonly string[] Words =
    {
        "mario", "zelda", "sonic", "metroid", "castlevania", "finalfantasy", "streetfighter",
        "resident", "silent", "kingdom", "hearts", "halo", "doom", "quake", "portal", "half",
        "life", "death", "cyberpunk", "witcher", "skyrim", "fallout", "borderlands", "mass",
        "effect", "destiny", "valheim", "terraria", "minecraft", "pokemon"
    };
    private static readonly string[] Suffixes =
    {
        "Deluxe", "Remastered", "Anniversary", "Collection", "Gold",
        "Platinum", "Ultra", "Legacy", "Origins", "Reloaded"
    };
    private static readonly string[] CategoryIds =
    {
        "new", "action", "rpg", "strategy", "sports", "racing", "adventure", "shooter", "puzzle", "simulation"
    };
    private static readonly string[] CategoryNames =
    {
        "New releases", "Action", "Role playing", "Strategy", "Sports",
        "Racing", "Adventure", "Shooter", "Puzzle", "Simulation"
    };
    private static readonly string[] TagNames =
    {
        "Favourite", "Co-op", "Controller", "Modded", "Speedrun", "Japanese", "Multiplayer"
    };

    internal static double LastBuildMilliseconds { get; private set; }
    internal static double LastQueryMilliseconds { get; private set; }
    internal static double LastCachedQueryMilliseconds { get; private set; }
    internal static double LastReferenceScanMilliseconds { get; private set; }
    internal static double LastKeystrokeQueryMilliseconds { get; private set; }
    internal static double LastKeystrokeReferenceMilliseconds { get; private set; }
    internal static double LastKeystrokeFirstQueryMilliseconds { get; private set; }
    internal static string LastKeystrokeProbe { get; private set; } = "";
    internal static int LastCatalogSize { get; private set; }
    internal static int LastParityQueries { get; private set; }
    internal static int LastParityMatches { get; private set; }
    internal static int LastTrigramGroups { get; private set; }
    internal static int LastCachedQueries { get; private set; }

    internal static void Run(string root)
    {
        string folder = Path.Combine(root, "search-performance");
        Directory.CreateDirectory(folder);

        var state = new UserState { Settings = new Preferences { MountPath = Path.Combine(folder, "mounts") } };
        List<Game> games = SyntheticCatalog(CatalogSize, state);
        Dictionary<string, HashSet<string>> tags = SyntheticTags(games);
        LastCatalogSize = games.Count;

        var buildTimer = Stopwatch.StartNew();
        SearchPerformance.SearchIndex index = SearchPerformance.Build(games, state, tags);
        buildTimer.Stop();
        double buildMilliseconds = buildTimer.Elapsed.TotalMilliseconds;
        LastBuildMilliseconds = buildMilliseconds;
        LastTrigramGroups = index.TrigramGroupCount;

        Require(index.Count == CatalogSize, $"The index reported {index.Count} cards for a {CatalogSize}-card catalog.");
        Require(!SearchPerformance.NeedsRebuild(index, games, state), "A freshly built index must not be considered stale.");
        Require(index.Version == SearchPerformance.CurrentVersion, "A freshly built index must carry the current version.");

        EmptyQuery(games, index);
        NoMatchQuery(index);
        CaseInsensitivity(games, state, index);
        Parity(games, state, index);
        CachedQueryIdentity(index);
        MultiTermNarrowing(games, state, index);
        CategoryGrouping(games, index);
        InstallStateGrouping(games, index);
        TagGrouping(games, tags, index);
        ExactValueLookup(games, index);
        PredicateParity(games, state, index);
        GroupPredicateParity(games, state, index);
        LengthBuckets(index);
        Timings(games, state, index, buildMilliseconds);
        Versioning(games, state);

        File.WriteAllLines(Path.Combine(folder, "timings.txt"), new[]
        {
            $"catalog-cards                 {LastCatalogSize}",
            $"build-index-once-ms           {LastBuildMilliseconds:0.000}",
            $"trigram-groups                {LastTrigramGroups}",
            $"query-{TimingQueryCount}-unique-ms       {LastQueryMilliseconds:0.000}",
            $"query-{TimingQueryCount}-cached-ms       {LastCachedQueryMilliseconds:0.000}",
            $"reference-{TimingQueryCount}-scan-ms     {LastReferenceScanMilliseconds:0.000}",
            $"parity-queries                {LastParityQueries}",
            $"parity-matches                {LastParityMatches}",
            $"keystroke-probe               \"{LastKeystrokeProbe}\"",
            $"keystroke-reference-scan-ms   {LastKeystrokeReferenceMilliseconds:0.0000}",
            $"keystroke-first-query-ms      {LastKeystrokeFirstQueryMilliseconds:0.0000}",
            $"keystroke-cached-query-ms     {LastKeystrokeQueryMilliseconds:0.0000}",
            $"query-budget-ms               {QueryCeilingMilliseconds:0.0}"
        });
    }

    private static void EmptyQuery(List<Game> games, SearchPerformance.SearchIndex index)
    {
        IReadOnlyList<int> blank = SearchPerformance.Query(index, "");
        Require(blank.Count == games.Count, $"An empty query returned {blank.Count} of {games.Count} cards.");
        IReadOnlyList<int> padded = SearchPerformance.Query(index, "   ");
        Require(ReferenceEquals(blank, padded), "Whitespace-only and empty queries must resolve to the same cached full result.");
        Require(ReferenceEquals(blank, SearchPerformance.Query(index, "\t\n ")), "Any blank query must resolve to the same cached full result.");
        for (int i = 0; i < games.Count; i++)
            Require(blank[i] == i, $"The empty-query result must stay in catalog order at position {i}.");
    }

    private static void NoMatchQuery(SearchPerformance.SearchIndex index)
    {
        IReadOnlyList<int> none = SearchPerformance.Query(index, "zzqxnothingmatches-42");
        Require(none.Count == 0, $"A query with no match returned {none.Count} cards instead of none.");
        Require(SearchPerformance.Query(index, "\u00e5\u00e5\u00e5-nope").Count == 0, "A non-ASCII query with no match must return nothing.");
        Require(ReferenceEquals(none, SearchPerformance.Query(index, "zzqxnothingmatches-42")), "A no-match query must be cached.");
    }

    private static void CaseInsensitivity(List<Game> games, UserState state, SearchPerformance.SearchIndex index)
    {
        foreach (string probe in new[]
        {
            "mario", "Mario", "MARIO", "mArIo", "DeLuXe", "deluxe", "registry.local",
            "REGISTRY.LOCAL", "mario deluxe", "MARIO DELUXE", "MaRiO DeLuXe", "install-4", "INSTALL-4"
        })
        {
            IReadOnlyList<int> lower = SearchPerformance.Query(index, probe.ToLowerInvariant());
            IReadOnlyList<int> upper = SearchPerformance.Query(index, probe.ToUpperInvariant());
            IReadOnlyList<int> mixed = SearchPerformance.Query(index, probe);
            RequireSameSet($"case-insensitive \"{probe}\"", ReferenceScan(games, state, probe), lower);
            RequireSameSet($"case-insensitive \"{probe}\" (upper)", lower, upper);
            RequireSameSet($"case-insensitive \"{probe}\" (mixed)", lower, mixed);
        }
    }

    private static void Parity(List<Game> games, UserState state, SearchPerformance.SearchIndex index)
    {
        var random = new Random(CatalogSeed);
        int matched = 0;
        for (int attempt = 0; attempt < ParityQueryCount; attempt++)
        {
            string query = RandomQuery(random);
            List<int> expected = ReferenceScan(games, state, query);
            IReadOnlyList<int> actual = SearchPerformance.Query(index, query);
            RequireSameSet($"parity query #{attempt} \"{query}\"", expected, actual);
            matched += expected.Count;

            string padded = "  " + query.ToUpperInvariant() + "   ";
            RequireSameSet($"parity query #{attempt} (padded)", expected, SearchPerformance.Query(index, padded));
        }
        LastParityQueries = ParityQueryCount;
        LastParityMatches = matched;
        Require(matched > 0, "The randomized parity queries never matched a card, so parity was not exercised.");
    }

    private static void CachedQueryIdentity(SearchPerformance.SearchIndex index)
    {
        IReadOnlyList<int> first = SearchPerformance.Query(index, "identity-probe");
        IReadOnlyList<int> second = SearchPerformance.Query(index, "identity-probe");
        Require(ReferenceEquals(first, second), "An identical query must return the identical cached result instance.");
        Require(!ReferenceEquals(first, SearchPerformance.Query(index, "identity-probe-2")),
            "A different query must not share a cached result instance.");
        Require(ReferenceEquals(SearchPerformance.Query(index, ""), SearchPerformance.Query(index, "  ")),
            "The full-result shortcut must be cached too.");
        for (int i = 0; i < 50; i++)
            Require(ReferenceEquals(first, SearchPerformance.Query(index, "identity-probe")),
                "Cached query identity must survive repeated calls.");
    }

    private static void MultiTermNarrowing(List<Game> games, UserState state, SearchPerformance.SearchIndex index)
    {
        const string probe = "mario deluxe";
        string[] terms = probe.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        List<int> expected = ReferenceTermScan(games, state, terms);
        IReadOnlyList<int> actual = SearchPerformance.Query(index, probe, SearchPerformance.SearchMode.AllTerms);
        Require(expected.Count > 0, "The multi-term probe must match at least one card for the narrowing test to mean anything.");
        RequireSameSet($"multi-term all-terms \"{probe}\"", expected, actual);
        Require(actual.Count < SearchPerformance.Query(index, "mario").Count,
            "Adding a second term must narrow the result set.");
        Require(SearchPerformance.Query(index, " mario   deluxe ", SearchPerformance.SearchMode.AllTerms).Count == actual.Count,
            "All-term narrowing must ignore surrounding and repeated whitespace.");
        Require(SearchPerformance.Query(index, "MARIO DELUXE", SearchPerformance.SearchMode.AllTerms).Count == actual.Count,
            "All-term narrowing must be case-insensitive.");
        Require(SearchPerformance.Query(index, "mario zzzznotpresent", SearchPerformance.SearchMode.AllTerms).Count == 0,
            "An unsatisfiable extra term must empty the all-term result.");
        Require(SearchPerformance.Query(index, "   ", SearchPerformance.SearchMode.AllTerms).Count == games.Count,
            "A blank all-term query must return every card.");
        RequireSameSet($"multi-term substring \"{probe}\"", ReferenceScan(games, state, probe),
            SearchPerformance.Query(index, probe));

// The two modes must stay distinguishable: "mario steamapps" only matches when the
        // two terms are allowed to come from different fields, because no single field
        // in the fixture carries both a catalog name and a Steam library path.
const string split = "mario steamapps";
Require(SearchPerformance.Query(index, split).Count == 0,
            "Substring mode must not match a query whose terms live in different fields.");
List<int> splitTerms = ReferenceTermScan(games, state, new[] { "mario", "steamapps" });
Require(splitTerms.Count > 0, "The split-term fixture must match at least one card.");
RequireSameSet($"multi-term all-terms \"{split}\"", splitTerms,
            SearchPerformance.Query(index, split, SearchPerformance.SearchMode.AllTerms));
Require(SearchPerformance.Query(index, split, SearchPerformance.SearchMode.AllTerms).Count
            >= SearchPerformance.Query(index, split).Count,
            "All-term matching must never be narrower than substring matching for the same words.");
    }

    private static void CategoryGrouping(List<Game> games, SearchPerformance.SearchIndex index)
    {
        foreach (string category in CategoryIds)
        {
            List<int> expected = new();
            for (int i = 0; i < games.Count; i++) if (games[i].Category == category) expected.Add(i);
            IReadOnlyList<int> group = SearchPerformance.ByCategory(index, category);
            RequireSameSet($"category \"{category}\"", expected, group);
            Require(ReferenceEquals(group, SearchPerformance.ByCategory(index, category)),
                "A category group must return the identical cached list instance.");
        }
        int grouped = SearchPerformance.Categories(index).Sum(id => SearchPerformance.ByCategory(index, id).Count);
        Require(grouped == games.Count, $"Category groups covered {grouped} of {games.Count} cards.");
        Require(SearchPerformance.ByCategory(index, "not-a-real-category").Count == 0, "An unknown category must return no cards.");
        Require(SearchPerformance.ByCategory(index, null!).Count == 0, "A null category must return no cards.");
    }

    private static void InstallStateGrouping(List<Game> games, SearchPerformance.SearchIndex index)
    {
        List<int> installed = new();
        List<int> missing = new();
        for (int i = 0; i < games.Count; i++) (games[i].Installed ? installed : missing).Add(i);
        RequireSameSet("installed group", installed, SearchPerformance.ByInstallState(index, true));
        RequireSameSet("not installed group", missing, SearchPerformance.ByInstallState(index, false));
        Require(SearchPerformance.ByInstallState(index, true).Count + SearchPerformance.ByInstallState(index, false).Count == games.Count,
            "Install-state groups must partition the catalog.");
        Require(ReferenceEquals(SearchPerformance.ByInstallState(index, true), SearchPerformance.ByInstallState(index, true)),
            "Install-state groups must be cached list instances.");
        List<int> mountBearing = new();
        for (int i = 0; i < games.Count; i++)
            if (index.Values[i].Any(value => value.Contains("mounts", StringComparison.OrdinalIgnoreCase)))
                mountBearing.Add(i);
        Require(mountBearing.Count > 0, "The fixture must contain cards whose mounted Docker install path is searchable.");
        IReadOnlyList<int> mountHits = SearchPerformance.Query(index, "mounts\\install-");
        Require(mountHits.Count > 0, "The mount-path query must return the installed, non-local cards.");
        foreach (int card in mountHits) Require(mountBearing.Contains(card),
            "A mount-path query must only return cards that expose a path under the mount root.");
        int installedMounted = 0;
        for (int i = 0; i < games.Count; i++)
            if (games[i].Installed && !games[i].IsLocal) installedMounted++;
        Require(installedMounted > 0, "The fixture must contain installed, non-local cards.");
    }

    private static void TagGrouping(List<Game> games, Dictionary<string, HashSet<string>> tags,
        SearchPerformance.SearchIndex index)
    {
        foreach (string tag in TagNames)
        {
            List<int> expected = new();
            for (int i = 0; i < games.Count; i++)
                if (tags.TryGetValue(games[i].Id, out HashSet<string>? set) && set.Contains(tag)) expected.Add(i);
            IReadOnlyList<int> group = SearchPerformance.ByTag(index, tag);
            Require(group.Count == expected.Count, $"Tag group \"{tag}\" returned {group.Count} cards instead of {expected.Count}.");
            foreach (int card in group) Require(expected.Contains(card), $"Tag group \"{tag}\" returned card {card} without that tag.");
        }
        int total = TagNames.Sum(tag => SearchPerformance.ByTag(index, tag).Count);
        Require(total > 0, "The fixture must contain tagged cards.");
    }

    private static void ExactValueLookup(List<Game> games, SearchPerformance.SearchIndex index)
    {
        Game probe = games[1];
        IReadOnlyList<int> exact = SearchPerformance.ExactValues(index, probe.Name);
        Require(exact.Count >= 1, "An exact field value lookup must find the card that owns it.");
        foreach (int card in exact) Require(games[card].Name.Equals(probe.Name, StringComparison.OrdinalIgnoreCase),
            "An exact value lookup must only return cards carrying that exact value.");
        Require(SearchPerformance.ExactValues(index, probe.Name.ToUpperInvariant()).Count == exact.Count,
            "Exact value lookup must be case-insensitive.");
        Require(ReferenceEquals(exact, SearchPerformance.ExactValues(index, probe.Name)),
            "Exact value lookups must return the identical cached list instance.");
        Require(SearchPerformance.ExactValues(index, "no-such-exact-value").Count == 0, "An unknown exact value must return no cards.");
    }

    private static void PredicateParity(List<Game> games, UserState state, SearchPerformance.SearchIndex index)
    {
        Func<int, bool> predicate = SearchPerformance.MatchPredicate(index, "mario");
        for (int i = 0; i < games.Count; i++)
        {
            bool expected = ReferenceMatches(games[i], state, "mario");
            Require(predicate(i) == expected, $"MatchPredicate disagreed with the reference at card {i}.");
        }
        Func<Game, bool> gamePredicate = SearchPerformance.GameMatchPredicate(index, "mario");
        for (int i = 0; i < games.Count; i++)
            Require(gamePredicate(games[i]) == predicate(i), $"GameMatchPredicate disagreed with MatchPredicate at card {i}.");
        Func<int, bool> blank = SearchPerformance.MatchPredicate(index, "");
        for (int i = 0; i < games.Count; i++) Require(blank(i), "A blank predicate must accept every card.");
        Require(SearchPerformance.MatchPredicate(index, "zzqxnothingmatches-42")(-1) == false,
            "A negative card index must not throw and must not match.");
    }

private static void GroupPredicateParity(List<Game> games, UserState state, SearchPerformance.SearchIndex index)
    {
        IReadOnlyList<int> noCards = new int[0];
        Func<int, bool> empty = SearchPerformance.GroupPredicate(index, noCards);
        Require(ReferenceEquals(empty, SearchPerformance.GroupPredicate(index, SearchPerformance.ByCategory(index, "nope"))),
            "An unknown category group must reuse the shared empty-group predicate.");
        Require(!empty(0), "An empty group must reject card 0.");
        Require(!empty(-1), "A negative card index must not throw and must not match an empty group.");
        foreach (string category in CategoryIds)
        {
            Func<int, bool> member = SearchPerformance.GroupPredicate(index, SearchPerformance.ByCategory(index, category));
            for (int i = 0; i < games.Count; i++)
                Require(member(i) == (games[i].Category == category),
                    $"GroupPredicate disagreed with the category rule at card {i} for \"{category}\".");
        }
        Func<int, bool> installed = SearchPerformance.GroupPredicate(index, SearchPerformance.ByInstallState(index, true));
        for (int i = 0; i < games.Count; i++)
            Require(installed(i) == games[i].Installed, $"Install-state GroupPredicate disagreed at card {i}.");
        Func<Game, bool> gameMember = SearchPerformance.GameMatchPredicate(index, "mario");
        Func<int, bool> cardMember = SearchPerformance.MatchPredicate(index, "mario");
        for (int i = 0; i < games.Count; i++)
            Require(gameMember(games[i]) == cardMember(i), $"GameMatchPredicate disagreed at card {i}.");
        Require(SearchPerformance.TagsFrom(null).Count == 0, "A missing state must yield no tags.");
        var tagged = new UserState();
        tagged.GameTags["a"] = new List<string> { "one", "two" };
        Require(SearchPerformance.TagsFrom(tagged).Count == 1, "TagsFrom must project every tagged game.");
        Require(SearchPerformance.TagsFrom(tagged)["a"].Contains("two"), "TagsFrom must carry every tag value.");
        Require(SearchPerformance.TagsFrom(new UserState()).Count == 0, "A state without tags must yield no tags.");
        Require(ReferenceScan(games, state, "mario").Count > 0, "The group predicate probe must be non-trivial.");
    }

    private static void LengthBuckets(SearchPerformance.SearchIndex index)
    {
        int ceiling = SearchPerformance.LengthBucketCeiling;
        Require(SearchPerformance.CandidateCount(index, 0) == index.Count,
            "A zero-length requirement must admit every card.");
        int atCeiling = 0;
        int atOne = 0;
        for (int card = 0; card < index.Count; card++)
        {
            if (index.MaxFieldLength[card] >= ceiling) atCeiling++;
            if (index.MaxFieldLength[card] >= 1) atOne++;
        }
        Require(SearchPerformance.CandidateCount(index, ceiling) == atCeiling,
            $"The clamped length bucket must admit exactly the {atCeiling} cards with a field of at least {ceiling} characters.");
        Require(SearchPerformance.CandidateCount(index, 1) == atOne,
            "The one-character length bucket must admit every card that owns any non-empty field.");
        Require(SearchPerformance.CandidateCount(index, 4096) == SearchPerformance.CandidateCount(index, ceiling),
            "Length-bucket candidate counts must clamp at the ceiling bucket.");
        string query = "install-";
        Require(SearchPerformance.Query(index, query).Count > 0, "The length-bucket probe must match cards.");
        foreach (int card in SearchPerformance.Query(index, query))
            Require(index.MaxFieldLength[card] >= query.Length, "A card with no long enough field must never be returned.");
    }

    private static void Timings(List<Game> games, UserState state, SearchPerformance.SearchIndex index, double buildMilliseconds)
    {
        List<string> timingQueries = TimingQueries(games, TimingQueryCount);
        Require(timingQueries.Count == TimingQueryCount, $"Only {timingQueries.Count} distinct timing queries were generated.");

        var queryTimer = Stopwatch.StartNew();
        foreach (string query in timingQueries) SearchPerformance.Query(index, query);
        queryTimer.Stop();
        double queryMilliseconds = queryTimer.Elapsed.TotalMilliseconds;
        LastQueryMilliseconds = queryMilliseconds;
        LastCachedQueries = TimingQueryCount;
        Require(queryMilliseconds < QueryCeilingMilliseconds,
            $"Querying the {CatalogSize}-card index {TimingQueryCount} times took {queryMilliseconds:0.0} ms, above the {QueryCeilingMilliseconds:0} ms ceiling.");
        Require(index.SubstringCache.Count <= SearchPerformance.QueryCacheCapacity,
            $"The query cache must stay bounded, but it held {index.SubstringCache.Count} entries for {TimingQueryCount} distinct queries.");

        // The cache is a bounded interactive working set, so the warm pass replays the same
        // 400-query window twice and times only the replay, which is guaranteed to hit.
        var warm = new List<string>(timingQueries.Skip(Math.Max(0, timingQueries.Count - 400)));
        Require(warm.Count == 400, $"The warm pass needs 400 resident queries, not {warm.Count}.");
        foreach (string query in warm) SearchPerformance.Query(index, query);
        var cachedTimer = Stopwatch.StartNew();
        foreach (string query in warm) SearchPerformance.Query(index, query);
        cachedTimer.Stop();
        double cachedMilliseconds = cachedTimer.Elapsed.TotalMilliseconds;
        LastCachedQueryMilliseconds = cachedMilliseconds;
        Require(cachedMilliseconds * 10 < queryMilliseconds,
            $"Replaying {warm.Count} cached queries ({cachedMilliseconds:0.00} ms) must be far cheaper than {TimingQueryCount} uncached queries ({queryMilliseconds:0.00} ms).");

        var referenceTimer = Stopwatch.StartNew();
        foreach (string query in timingQueries) ReferenceScan(games, state, query);
        referenceTimer.Stop();
        double referenceMilliseconds = referenceTimer.Elapsed.TotalMilliseconds;
        LastReferenceScanMilliseconds = referenceMilliseconds;
        Require(referenceMilliseconds > 0, "The reference scan timing must be measurable.");
        Require(buildMilliseconds * BuildHeadroomFactor < referenceMilliseconds,
            $"Building the index once ({buildMilliseconds:0.00} ms) must be far cheaper than {TimingQueryCount} uncached reference scans ({referenceMilliseconds:0.00} ms).");

        string probe = games[0].Name.Length >= 4 ? games[0].Name.Substring(0, 4) : games[0].Name;
        LastKeystrokeProbe = probe;
        var probeTimer = Stopwatch.StartNew();
        SearchPerformance.Query(index, probe);
        probeTimer.Stop();
        LastKeystrokeFirstQueryMilliseconds = probeTimer.Elapsed.TotalMilliseconds;
        var cachedProbeTimer = Stopwatch.StartNew();
        for (int i = 0; i < KeystrokeIterations; i++) SearchPerformance.Query(index, probe);
        cachedProbeTimer.Stop();
        LastKeystrokeQueryMilliseconds = cachedProbeTimer.Elapsed.TotalMilliseconds / KeystrokeIterations;
        var referenceProbeTimer = Stopwatch.StartNew();
        for (int i = 0; i < KeystrokeIterations; i++) ReferenceScan(games, state, probe);
        referenceProbeTimer.Stop();
        LastKeystrokeReferenceMilliseconds = referenceProbeTimer.Elapsed.TotalMilliseconds / KeystrokeIterations;
        Require(LastKeystrokeQueryMilliseconds < LastKeystrokeReferenceMilliseconds,
            $"A cached 4-character keystroke ({LastKeystrokeQueryMilliseconds:0.0000} ms) must be faster than the current scan ({LastKeystrokeReferenceMilliseconds:0.0000} ms).");
    }

    private static void Versioning(List<Game> games, UserState state)
    {
        SearchPerformance.SearchIndex first = SearchPerformance.Build(games, state, null);
        SearchPerformance.SearchIndex second = SearchPerformance.Build(games, state, null);
        Require(second.Version > first.Version, "Each build must publish a strictly increasing version.");
        Require(SearchPerformance.NeedsRebuild(first, games, state), "A superseded index must be reported as needing a rebuild.");
        Require(!SearchPerformance.NeedsRebuild(second, games, state), "The newest index must not be reported as needing a rebuild.");
        SearchPerformance.MarkStale(second);
        Require(SearchPerformance.NeedsRebuild(second, games, state), "MarkStale must force a rebuild.");
        Require(SearchPerformance.NeedsRebuild(null, games, state), "A missing index must be reported as needing a rebuild.");
        long version = SearchPerformance.CurrentVersion;
        Require(SearchPerformance.NextVersion() > version, "The catalog version counter must advance.");
        Require(SearchPerformance.Build(games, null).Version > version, "A build must adopt the newest version.");
        SearchPerformance.SearchIndex stateFree = SearchPerformance.Build(games, null);
        Require(stateFree.MountPath.Length == 0, "A state-free build must not invent a mount path.");
        Require(SearchPerformance.Query(stateFree, "mario").Count > 0, "A state-free build must still search the game fields.");
        Require(SearchPerformance.Query(stateFree, "mounts").Count == 0,
            "A state-free build must not invent mounted install paths.");
    }

    private static List<Game> SyntheticCatalog(int count, UserState state)
    {
        var random = new Random(CatalogSeed);
        var games = new List<Game>(count);
        for (int i = 0; i < count; i++)
        {
            string word = Words[i % Words.Length];
            string suffix = Suffixes[(i / Words.Length) % Suffixes.Length];
            string edition = (i % 97).ToString(CultureInfo.InvariantCulture);
            string id = i % 50 == 0
                ? "docker:library/" + word + ":v" + (i % 9 + 1)
                : "game-" + i.ToString("D4", CultureInfo.InvariantCulture) + "-" + word;
            var game = new Game
            {
                Id = id,
                Name = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(word) + " " + suffix + " " + edition,
                Category = CategoryIds[i % CategoryIds.Length],
                CategoryName = CategoryNames[i % CategoryNames.Length],
                DockerImage = i % 5 == 0 ? "" : "registry.local/games/" + word + ":v" + (i % 13),
                DockerImageUrl = i % 7 == 0 ? "" : "https://registry.local/v2/games/" + word + "/tags/list",
                Time = random.NextDouble() * 90,
                SizeGb = random.NextDouble() * 120,
                Rating = random.Next(0, 6)
            };
            game.Installed = i % 3 == 0;
            game.IsLocal = i % 11 == 0;
            if (i % 5 == 0)
                game.SourceRecords = new[]
                {
                    game,
                    new Game
                    {
                        Id = id + "-legacy",
                        Name = game.Name + " (Legacy)",
                        Category = game.Category,
                        CategoryName = game.CategoryName,
                        DockerImage = "registry.local/games/" + word + ":legacy"
                    }
                };
            if (game.Installed && i % 2 == 0)
                state.LaunchPaths[id] = @"E:\Steam\steamapps\common\" + suffix + @"\launch.exe";
            if (game.Installed && i % 4 == 0)
                state.InstallationFolders[id] = Path.Combine(state.Settings.MountPath, "install-" + i);
            if (i % 9 == 0)
                state.LocalGames[id] = new LocalGame { Name = game.Name, Folder = Path.Combine(@"C:\Users\player\LocalGames", suffix) };
            games.Add(game);
        }
        return games;
    }

    private static Dictionary<string, HashSet<string>> SyntheticTags(List<Game> games)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        for (int i = 0; i < games.Count; i++)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (i % 2 == 0) set.Add(TagNames[i % TagNames.Length]);
            if (i % 5 == 0) set.Add(TagNames[(i + 3) % TagNames.Length]);
            if (i % 13 == 0) set.Add("All tags");
            if (set.Count > 0) result[games[i].Id] = set;
        }
        return result;
    }

    private static string RandomQuery(Random random)
    {
        int shape = random.Next(13);
        string word = Words[random.Next(Words.Length)];
        string other = Words[random.Next(Words.Length)];
        return shape switch
        {
            0 => "",
            1 => "   ",
            2 => "\t",
            3 => word[..(1 + random.Next(2))],
            4 => word + " " + other,
            5 => "  " + word.ToUpperInvariant() + "   " + other + " ",
            6 => word.ToUpperInvariant(),
            7 => "docker-library-" + random.Next(9),
            8 => @"E:\Steam\steamapps\common\" + Suffixes[random.Next(Suffixes.Length)],
            9 => new string((char)('a' + random.Next(26)), 1 + random.Next(3)),
            10 => "zzz" + random.Next(10000),
            11 => "\u0130stanbul \u212Aelvin " + word,
            12 => "\u00c4\u00d6\u00dc \u0130stanbul",
            _ => word[..(1 + random.Next(Math.Max(1, word.Length)))]
        };
    }

    private static List<string> TimingQueries(List<Game> games, int wanted)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queries = new List<string>(wanted);
        int longest = games.Max(game => game.Name.Length);
        for (int length = 1; length <= longest && queries.Count < wanted; length++)
            for (int i = 0; i < games.Count && queries.Count < wanted; i++)
            {
                string name = games[i].Name;
                if (name.Length < length) continue;
                string candidate = name[..length];
                if (seen.Add(candidate)) queries.Add(candidate);
            }
        return queries;
    }

    private static bool ReferenceContains(string? value, string query)
        => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    // Verbatim copy of MainWindow.MatchesSearch as it stands today, including the
    // installed-and-not-local mounted install path clause.
    private static bool ReferenceMatches(Game game, UserState state, string query) =>
        ReferenceContains(game.Name, query) || ReferenceContains(game.Id, query) || ReferenceContains(game.Category, query)
        || ReferenceContains(game.CategoryName, query) || ReferenceContains(game.DockerImage, query) || ReferenceContains(game.DockerImageUrl, query)
        || game.SourceRecords.Any(source => ReferenceContains(source.Id, query) || ReferenceContains(source.Name, query) || ReferenceContains(source.DockerImage, query))
        || (state.LaunchPaths.TryGetValue(game.Id, out var launcher) && ReferenceContains(launcher, query))
        || (state.InstallationFolders.TryGetValue(game.Id, out var installationFolder) && ReferenceContains(installationFolder, query))
        || (state.LocalGames.TryGetValue(game.Id, out var local) && ReferenceContains(local.Folder, query))
        || (game.Installed && !game.IsLocal
            && ReferenceContains(Path.Combine(state.Settings.MountPath, DockerScripts.InstallFolder(game.Id)), query));

    private static List<int> ReferenceScan(List<Game> games, UserState state, string rawQuery)
    {
        string query = rawQuery.Trim();
        var matches = new List<int>();
        if (query.Length == 0)
        {
            for (int i = 0; i < games.Count; i++) matches.Add(i);
            return matches;
        }
        for (int i = 0; i < games.Count; i++) if (ReferenceMatches(games[i], state, query)) matches.Add(i);
        return matches;
    }

    private static bool ReferenceHasTerm(Game game, UserState state, string term)
    {
        if (ReferenceContains(game.Name, term) || ReferenceContains(game.Id, term) || ReferenceContains(game.Category, term)
            || ReferenceContains(game.CategoryName, term) || ReferenceContains(game.DockerImage, term)
            || ReferenceContains(game.DockerImageUrl, term)) return true;
        if (game.SourceRecords.Any(source => ReferenceContains(source.Id, term) || ReferenceContains(source.Name, term)
            || ReferenceContains(source.DockerImage, term))) return true;
        if (state.LaunchPaths.TryGetValue(game.Id, out var launcher) && ReferenceContains(launcher, term)) return true;
        if (state.InstallationFolders.TryGetValue(game.Id, out var installationFolder) && ReferenceContains(installationFolder, term)) return true;
        if (state.LocalGames.TryGetValue(game.Id, out var local) && ReferenceContains(local.Folder, term)) return true;
        return game.Installed && !game.IsLocal
            && ReferenceContains(Path.Combine(state.Settings.MountPath, DockerScripts.InstallFolder(game.Id)), term);
    }

    private static List<int> ReferenceTermScan(List<Game> games, UserState state, string[] terms)
    {
        var matches = new List<int>();
        for (int i = 0; i < games.Count; i++)
        {
            bool every = true;
            foreach (string term in terms)
                if (!ReferenceHasTerm(games[i], state, term)) { every = false; break; }
            if (every) matches.Add(i);
        }
        return matches;
    }

    private static void RequireSameSet(string label, IReadOnlyList<int> expected, IReadOnlyList<int> actual)
    {
        if (expected.Count != actual.Count)
            throw new InvalidOperationException($"{label}: expected {expected.Count} matches but the index returned {actual.Count}.");
        for (int i = 0; i < expected.Count; i++)
            if (expected[i] != actual[i])
                throw new InvalidOperationException($"{label}: match {i} differs, reference card {expected[i]} versus index card {actual[i]}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}