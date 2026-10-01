using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;

namespace GameLibrary.Native;

/// <summary>
/// Drop-in replacement filtering core for the library manager. Pure, UI-free and
/// free of disk access: every searchable field is projected once per catalog
/// version, so a keystroke costs integer candidate work plus one ordinal string
/// probe per surviving card, and an identical query returns the identical cached
/// result instance so the manager can skip re-assigning ItemsSource entirely.
///
/// SearchMode.Substring is the default contract and reproduces the current
/// MainWindow.MatchesSearch result set exactly (the whole trimmed query must
/// appear contiguously in one field, OrdinalIgnoreCase). SearchMode.AllTerms is an
/// explicit opt-in that requires every whitespace term to appear somewhere on the
/// card; it is deliberately NOT compatible with MatchesSearch.
/// </summary>
internal static class SearchPerformance
{
    internal const int QueryCacheCapacity = 512;
    internal const int TrigramPostingBudget = 150_000;
    internal const int LengthBucketCeiling = 12;
    internal const int GramLength = 3;

    private static readonly IReadOnlyList<int> EmptyIndices = new ReadOnlyCollection<int>(Array.Empty<int>());
    private static readonly Func<int, bool> MatchAll = _ => true;
    private static readonly Func<int, bool> MatchNone = _ => false;
    private static readonly IReadOnlyDictionary<string, HashSet<string>> EmptyTags =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

    internal enum SearchMode : byte { Substring = 0, AllTerms = 1 }

    private static long versionCounter;

    internal static long CurrentVersion => Interlocked.Read(ref versionCounter);

    internal static long NextVersion() => Interlocked.Increment(ref versionCounter);

    internal static void MarkStale(SearchIndex? index) { if (index != null) index.IsStale = true; }

    internal static bool NeedsRebuild(SearchIndex? index, IReadOnlyList<Game>? games, UserState? state)
    {
        if (index == null || index.IsStale) return true;
        if (games != null && !ReferenceEquals(games, index.Games)) return true;
        if (state != null && !ReferenceEquals(state, index.State)) return true;
        return index.Version != CurrentVersion;
    }

    internal static SearchIndex Build(IReadOnlyList<Game> games, IReadOnlyDictionary<string, HashSet<string>>? tagsByGame)
        => Build(games, null, tagsByGame);

    internal static SearchIndex Build(IReadOnlyList<Game> games, UserState? state,
        IReadOnlyDictionary<string, HashSet<string>>? tagsByGame)
        => new SearchIndex(games ?? Array.Empty<Game>(), state, tagsByGame, NextVersion());

    internal static IReadOnlyList<int> Query(SearchIndex index, string rawQuery)
        => Query(index, rawQuery, SearchMode.Substring);

    internal static IReadOnlyList<int> Query(SearchIndex index, string rawQuery, SearchMode mode)
    {
        if (index == null || index.Count == 0) return EmptyIndices;
        string query = (rawQuery ?? "").Trim();
        if (query.Length == 0) return index.AllIndices;
        return Cache(index, query, mode).Indices;
    }

    internal static Func<int, bool> MatchPredicate(SearchIndex index, string rawQuery)
        => MatchPredicate(index, rawQuery, SearchMode.Substring);

    internal static Func<int, bool> MatchPredicate(SearchIndex index, string rawQuery, SearchMode mode)
    {
        if (index == null || index.Count == 0) return MatchNone;
        string query = (rawQuery ?? "").Trim();
        if (query.Length == 0) return MatchAll;
        bool[] mask = Cache(index, query, mode).Mask(index.Count);
        return card => (uint)card < (uint)mask.Length && mask[card];
    }

    internal static Func<Game, bool> GameMatchPredicate(SearchIndex index, string rawQuery,
        SearchMode mode = SearchMode.Substring)
    {
        if (index == null || index.Count == 0) return _ => false;
        Func<int, bool> match = MatchPredicate(index, rawQuery, mode);
        Dictionary<Game, int> positions = index.Positions;
        return game => game != null && positions.TryGetValue(game, out int card) && match(card);
    }

    internal static IReadOnlyList<int> ByCategory(SearchIndex index, string categoryId)
    {
        if (index == null || categoryId == null) return EmptyIndices;
        return index.CategoryGroups.TryGetValue(categoryId, out IReadOnlyList<int>? group) ? group : EmptyIndices;
    }

    internal static IReadOnlyList<int> ByInstallState(SearchIndex index, bool installed)
    {
        if (index == null) return EmptyIndices;
        return installed ? index.InstalledIndices : index.NotInstalledIndices;
    }

    internal static IReadOnlyList<int> ByTag(SearchIndex index, string tag)
    {
        if (index == null || tag == null) return EmptyIndices;
        return index.TagGroups.TryGetValue(tag, out IReadOnlyList<int>? group) ? group : EmptyIndices;
    }

    // Turns a precomputed group list into an O(1)-per-card membership test so the
    // manager can swap categories inside its existing Where chain instead of
    // re-running a predicate over every field of every card.
    internal static Func<int, bool> GroupPredicate(SearchIndex index, IReadOnlyList<int> positions)
    {
        if (index == null || positions == null || positions.Count == 0) return MatchNone;
        if (positions.Count == index.Count) return MatchAll;
        bool[] mask = new bool[index.Count];
        for (int i = 0; i < positions.Count; i++)
            if ((uint)positions[i] < (uint)mask.Length) mask[positions[i]] = true;
        return card => (uint)card < (uint)mask.Length && mask[card];
    }

    // Projects UserState.GameTags into the shape Build expects, so tag grouping is
    // precomputed once per catalog version instead of per keystroke.
    internal static IReadOnlyDictionary<string, HashSet<string>> TagsFrom(UserState? state)
    {
        if (state?.GameTags == null || state.GameTags.Count == 0) return EmptyTags;
        var tags = new Dictionary<string, HashSet<string>>(state.GameTags.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, List<string>> entry in state.GameTags)
            tags[entry.Key] = new HashSet<string>(entry.Value ?? new List<string>(), StringComparer.Ordinal);
        return tags;
    }

    internal static IReadOnlyList<string> Categories(SearchIndex index) => index?.CategoryIds ?? Array.Empty<string>();

    internal static IReadOnlyList<int> ExactValues(SearchIndex index, string value)
    {
        if (index == null || string.IsNullOrEmpty(value)) return EmptyIndices;
        return index.ExactValueGroups.TryGetValue(value, out IReadOnlyList<int>? group) ? group : EmptyIndices;
    }

    internal static int CandidateCount(SearchIndex index, int minFieldLength)
    {
        if (index == null || minFieldLength <= 0) return index?.Count ?? 0;
        int total = 0;
        for (int bucket = Math.Min(minFieldLength, LengthBucketCeiling); bucket <= LengthBucketCeiling; bucket++)
            total += index.LengthBuckets[bucket].Length;
        return total;
    }

    private static QueryEntry Cache(SearchIndex index, string query, SearchMode mode)
    {
        Dictionary<string, QueryEntry> cache = mode == SearchMode.Substring ? index.SubstringCache : index.AllTermsCache;
        if (cache.TryGetValue(query, out QueryEntry? cached)) return cached;
        List<int> matches = mode == SearchMode.Substring ? SubstringScan(index, query) : AllTermsScan(index, query);
        var entry = new QueryEntry { Indices = new ReadOnlyCollection<int>(matches) };
        if (cache.Count >= QueryCacheCapacity) cache.Clear();
        cache[query] = entry;
        return entry;
    }

    private static List<int> SubstringScan(SearchIndex index, string query)
    {
        var matches = new List<int>();
        string lowered = query.ToLowerInvariant();
        int[]? pool = Narrowing(index, lowered);
        bool checkLength = true;
        if (pool == null && query.Length > LengthBucketCeiling)
        {
            pool = index.LengthBuckets[LengthBucketCeiling];
            checkLength = false;
        }
        int iterations = pool?.Length ?? index.Count;
        for (int k = 0; k < iterations; k++)
        {
            int card = pool != null ? pool[k] : k;
            if (checkLength && index.MaxFieldLength[card] < query.Length) continue;
            if (CardContains(index, card, query)) matches.Add(card);
        }
        return matches;
    }

    private static List<int> AllTermsScan(SearchIndex index, string query)
    {
        var matches = new List<int>();
        string[] terms = SplitTerms(query.ToLowerInvariant());
        if (terms.Length == 0) return matches;
        int[]? pool = null;
        int shortest = int.MaxValue;
        for (int t = 0; t < terms.Length; t++)
        {
            if (terms[t].Length < shortest) shortest = terms[t].Length;
            int[]? posting = Narrowing(index, terms[t]);
            if (posting != null && (pool == null || posting.Length < pool.Length)) pool = posting;
        }
        bool checkLength = true;
        if (pool == null && shortest > LengthBucketCeiling)
        {
            pool = index.LengthBuckets[LengthBucketCeiling];
            checkLength = false;
        }
        int iterations = pool?.Length ?? index.Count;
        for (int k = 0; k < iterations; k++)
        {
            int card = pool != null ? pool[k] : k;
            if (checkLength && index.MaxFieldLength[card] < shortest) continue;
            if (CardContainsEveryTerm(index, card, terms)) matches.Add(card);
        }
        return matches;
    }

    private static bool CardContains(SearchIndex index, int card, string query)
    {
        string[] values = index.Values[card];
        for (int f = 0; f < values.Length; f++)
            if (values[f].IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    private static bool CardContainsEveryTerm(SearchIndex index, int card, string[] terms)
    {
        string[] values = index.Values[card];
        for (int t = 0; t < terms.Length; t++)
        {
            bool found = false;
            for (int f = 0; f < values.Length; f++)
                if (values[f].IndexOf(terms[t], StringComparison.OrdinalIgnoreCase) >= 0) { found = true; break; }
            if (!found) return false;
        }
        return true;
    }

    // A gram list always contains every card that contains the gram, so it is a
    // superset of the true match set; a probe with no indexed gram means no
    // narrowing information is available and the scan falls back to length buckets.
    private static int[]? Narrowing(SearchIndex index, string lowered)
    {
        if (lowered.Length < GramLength || !index.HasGrams) return null;
        int[]? best = null;
        for (int probe = 0; probe < 3; probe++)
        {
            int offset = probe == 0 ? 0 : probe == 1 ? lowered.Length / 2 : lowered.Length - GramLength;
            if (offset < 0 || offset + GramLength > lowered.Length) continue;
            int[]? cards = index.GramCards(Pack(lowered, offset));
            if (cards == null) continue;
            if (best == null || cards.Length < best.Length) best = cards;
        }
        return best;
    }

    private static long Pack(string text, int offset) =>
        ((long)text[offset] << 32) | ((long)text[offset + 1] << 16) | text[offset + 2];

    private static string[] SplitTerms(string lowered)
    {
        var terms = new List<string>(4);
        int start = 0;
        for (int i = 0; i <= lowered.Length; i++)
        {
            if (i < lowered.Length && !char.IsWhiteSpace(lowered[i])) continue;
            if (i > start) terms.Add(lowered[start..i]);
            start = i + 1;
        }
        return terms.Count == 0 ? Array.Empty<string>() : terms.ToArray();
    }

    private sealed class GameReferenceComparer : IEqualityComparer<Game>
    {
        internal static readonly GameReferenceComparer Instance = new();
        public bool Equals(Game? x, Game? y) => ReferenceEquals(x, y);
        public int GetHashCode(Game obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    internal sealed class QueryEntry
    {
        internal IReadOnlyList<int> Indices = Array.Empty<int>();
        private bool[]? mask;

        internal bool[] Mask(int size)
        {
            if (mask == null)
            {
                bool[] created = new bool[size];
                for (int i = 0; i < Indices.Count; i++) created[Indices[i]] = true;
                mask = created;
            }
            return mask;
        }
    }

    private sealed class Gram
    {
        internal readonly int[] Cards;
        internal int Cursor;
        internal Gram(int size) => Cards = new int[size];
    }

    internal sealed class SearchIndex
    {
        internal IReadOnlyList<Game> Games { get; }
        internal UserState? State { get; }
        internal IReadOnlyDictionary<string, HashSet<string>> TagsByGame { get; }
        internal long Version { get; }
        internal bool IsStale { get; set; }
        internal int Count => Games.Count;
        internal string MountPath { get; }
        internal int TrigramGroupCount => Grams.Count;
        internal bool HasGrams => Grams.Count > 0;
        internal int[]? GramCards(long key) => Grams.TryGetValue(key, out Gram? gram) ? gram.Cards : null;

        internal readonly string[][] Values;
        internal readonly string[][] Lower;
        internal readonly string[][] Tokens;
        internal readonly HashSet<string>[] ValueKeys;
        internal readonly int[] MaxFieldLength;
        internal readonly int[][] LengthBuckets;
        internal readonly Dictionary<string, int[]> ValuePostings;
        internal readonly IReadOnlyDictionary<string, IReadOnlyList<int>> ExactValueGroups;
        private readonly Dictionary<long, Gram> Grams;
        internal readonly bool[] InstalledFlags;
        internal readonly Dictionary<string, IReadOnlyList<int>> CategoryGroups;
        internal readonly Dictionary<string, IReadOnlyList<int>> TagGroups;
        internal readonly IReadOnlyList<string> CategoryIds;
        internal readonly IReadOnlyList<int> InstalledIndices;
        internal readonly IReadOnlyList<int> NotInstalledIndices;
        internal readonly IReadOnlyList<int> AllIndices;
        internal readonly Dictionary<Game, int> Positions;
        internal readonly Dictionary<string, QueryEntry> SubstringCache = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, QueryEntry> AllTermsCache = new(StringComparer.Ordinal);

        internal SearchIndex(IReadOnlyList<Game> games, UserState? state,
            IReadOnlyDictionary<string, HashSet<string>>? tagsByGame, long version)
        {
            Games = games;
            State = state;
            TagsByGame = tagsByGame ?? EmptyTags;
            Version = version;
            int count = games.Count;
            MountPath = state?.Settings?.MountPath ?? "";

            Values = new string[count][];
            Lower = new string[count][];
            Tokens = new string[count][];
            ValueKeys = new HashSet<string>[count];
            MaxFieldLength = new int[count];
            InstalledFlags = new bool[count];
            Positions = new Dictionary<Game, int>(count, GameReferenceComparer.Instance);

            var scratch = new List<string>(16);
            var postings = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var gramCounts = new Dictionary<long, int>();
            var cardGrams = new HashSet<long>();
            var categories = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var categoryOrder = new List<string>();
            var tags = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var installed = new List<int>();
            var notInstalled = new List<int>();
            var all = new int[count];

            for (int card = 0; card < count; card++)
            {
                Game game = games[card];
                Positions[game] = card;
                InstalledFlags[card] = game.Installed;
                (game.Installed ? installed : notInstalled).Add(card);
                all[card] = card;

                Collect(game, state, scratch);
                string[] values = scratch.ToArray();
                Values[card] = values;
                var lowered = new string[values.Length];
                var tokens = new List<string>(values.Length * 2);
                int longest = 0;
                for (int f = 0; f < values.Length; f++)
                {
                    lowered[f] = values[f].ToLowerInvariant();
                    if (values[f].Length > longest) longest = values[f].Length;
                    SplitInto(tokens, lowered[f]);
                }
                Lower[card] = lowered;
                Tokens[card] = tokens.ToArray();
                MaxFieldLength[card] = longest;

                var keys = new HashSet<string>(values.Length * 2, StringComparer.OrdinalIgnoreCase);
                for (int f = 0; f < values.Length; f++)
                {
                    if (!keys.Add(values[f])) continue;
                    if (!postings.TryGetValue(values[f], out List<int>? bucket))
                        postings[values[f]] = bucket = new List<int>(1);
                    bucket.Add(card);
                }
                ValueKeys[card] = keys;

                cardGrams.Clear();
                for (int f = 0; f < lowered.Length; f++)
                {
                    string text = lowered[f];
                    for (int p = 0; p + GramLength <= text.Length; p++) cardGrams.Add(Pack(text, p));
                }
                foreach (long key in cardGrams)
                    gramCounts[key] = gramCounts.TryGetValue(key, out int seen) ? seen + 1 : 1;

                if (game.Category != null)
                {
                    if (!categories.TryGetValue(game.Category, out List<int>? categoryBucket))
                    {
                        categories[game.Category] = categoryBucket = new List<int>(8);
                        categoryOrder.Add(game.Category);
                    }
                    categoryBucket.Add(card);
                }
                CollectTags(tags, card, game, TagsByGame);
            }

            AllIndices = new ReadOnlyCollection<int>(all);
            InstalledIndices = new ReadOnlyCollection<int>(installed.ToArray());
            NotInstalledIndices = new ReadOnlyCollection<int>(notInstalled.ToArray());
            CategoryIds = categoryOrder.ToArray();
            CategoryGroups = Freeze(categories);
            TagGroups = Freeze(tags);
            ValuePostings = new Dictionary<string, int[]>(postings.Count, StringComparer.OrdinalIgnoreCase);
            var exact = new Dictionary<string, IReadOnlyList<int>>(postings.Count, StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, List<int>> entry in postings)
            {
                int[] cards = entry.Value.ToArray();
                ValuePostings[entry.Key] = cards;
                exact[entry.Key] = new ReadOnlyCollection<int>(cards);
            }
            ExactValueGroups = exact;
            LengthBuckets = BuildLengthBuckets(MaxFieldLength);
            Grams = BuildGrams(gramCounts);
        }

        private Dictionary<long, Gram> BuildGrams(Dictionary<long, int> counts)
        {
            var selected = new Dictionary<long, Gram>(Math.Min(counts.Count, TrigramPostingBudget), EqualityComparer<long>.Default);
            if (counts.Count == 0) return selected;
            var pairs = new KeyValuePair<long, int>[counts.Count];
            int slot = 0;
            foreach (KeyValuePair<long, int> entry in counts) pairs[slot++] = entry;
            Array.Sort(pairs, (a, b) => a.Value != b.Value ? a.Value.CompareTo(b.Value) : a.Key.CompareTo(b.Key));
            int remaining = TrigramPostingBudget;
            for (int i = 0; i < pairs.Length && remaining > 0; i++)
            {
                if (pairs[i].Value > remaining) continue;
                remaining -= pairs[i].Value;
                selected[pairs[i].Key] = new Gram(pairs[i].Value);
            }
            if (selected.Count == 0) return selected;
            for (int card = 0; card < Count; card++)
            {
                string[] fields = Lower[card];
                for (int f = 0; f < fields.Length; f++)
                {
                    string text = fields[f];
                    for (int p = 0; p + GramLength <= text.Length; p++)
                    {
                        // The count pass records each card once per gram, so repeated
                        // grams inside one card must not be written twice here. Cards are
                        // visited in order, so a repeat is always the previous entry.
                        if (!selected.TryGetValue(Pack(text, p), out Gram? gram)) continue;
                        if (gram.Cursor == 0 || gram.Cards[gram.Cursor - 1] != card) gram.Cards[gram.Cursor++] = card;
                    }
                }
            }
            return selected;
        }

        private static void Collect(Game game, UserState? state, List<string> scratch)
        {
            scratch.Clear();
            Add(scratch, game.Name);
            Add(scratch, game.Id);
            Add(scratch, game.Category);
            Add(scratch, game.CategoryName);
            Add(scratch, game.DockerImage);
            Add(scratch, game.DockerImageUrl);
            IReadOnlyList<Game> sources = game.SourceRecords ?? Array.Empty<Game>();
            for (int s = 0; s < sources.Count; s++)
            {
                Add(scratch, sources[s].Id);
                Add(scratch, sources[s].Name);
                Add(scratch, sources[s].DockerImage);
            }
            if (state == null || state.Settings == null) return;
            if (state.LaunchPaths.TryGetValue(game.Id, out string? launcher)) Add(scratch, launcher);
            if (state.InstallationFolders.TryGetValue(game.Id, out string? installationFolder)) Add(scratch, installationFolder);
            if (state.LocalGames.TryGetValue(game.Id, out LocalGame? local) && local != null) Add(scratch, local.Folder);
            if (!game.Installed || game.IsLocal) return;
            string mount = state.Settings.MountPath ?? "";
            if (mount.Length == 0) return;
            try { Add(scratch, Path.Combine(mount, DockerScripts.InstallFolder(game.Id))); }
            catch (ArgumentException) { }
        }

        private static void Add(List<string> scratch, string? value)
        {
            if (!string.IsNullOrEmpty(value)) scratch.Add(value);
        }

        private static void SplitInto(List<string> tokens, string lowered)
        {
            int start = 0;
            for (int i = 0; i <= lowered.Length; i++)
            {
                if (i < lowered.Length && !char.IsWhiteSpace(lowered[i])) continue;
                if (i > start) tokens.Add(lowered[start..i]);
                start = i + 1;
            }
        }

        private static void CollectTags(Dictionary<string, List<int>> buckets, int card, Game game,
            IReadOnlyDictionary<string, HashSet<string>> tags)
        {
            if (tags.Count == 0) return;
            IReadOnlyList<Game> sources = game.SourceRecords ?? Array.Empty<Game>();
            HashSet<string>? seen = null;
            for (int s = 0; s <= sources.Count; s++)
            {
                string id = s < sources.Count ? sources[s].Id : game.Id;
                if (id == null || !tags.TryGetValue(id, out HashSet<string>? set)) continue;
                foreach (string tag in set)
                {
                    if (tag == null) continue;
                    seen ??= new HashSet<string>(StringComparer.Ordinal);
                    if (!seen.Add(tag)) continue;
                    if (!buckets.TryGetValue(tag, out List<int>? bucket)) buckets[tag] = bucket = new List<int>(1);
                    bucket.Add(card);
                }
            }
        }

        private static Dictionary<string, IReadOnlyList<int>> Freeze(Dictionary<string, List<int>> source)
        {
            var frozen = new Dictionary<string, IReadOnlyList<int>>(source.Count, StringComparer.Ordinal);
            foreach (KeyValuePair<string, List<int>> entry in source)
                frozen[entry.Key] = new ReadOnlyCollection<int>(entry.Value.ToArray());
            return frozen;
        }

        private static int[][] BuildLengthBuckets(int[] maxFieldLength)
        {
            var buckets = new int[LengthBucketCeiling + 1][];
            int[] cursor = new int[LengthBucketCeiling + 1];
            for (int card = 0; card < maxFieldLength.Length; card++)
                cursor[Math.Min(maxFieldLength[card], LengthBucketCeiling)]++;
            for (int b = 0; b <= LengthBucketCeiling; b++) buckets[b] = new int[cursor[b]];
            Array.Clear(cursor);
            for (int card = 0; card < maxFieldLength.Length; card++)
            {
                int bucket = Math.Min(maxFieldLength[card], LengthBucketCeiling);
                buckets[bucket][cursor[bucket]++] = card;
            }
            return buckets;
        }
    }
}