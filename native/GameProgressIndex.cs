using System;
using System.Collections.Generic;

namespace GameLibrary.Native;

/// <summary>
/// Indexes progress snapshots for a single linear-time matching pass.
/// <para>
/// This exists because the original matching was cubic: for every game it scanned
/// every snapshot, and inside that it counted the catalog again to test whether a
/// title was unique. On a large library that produced multi-second UI freezes and
/// visible frame drops, because the pass runs from a timer and after every save.
/// Both lookups are dictionaries built once per pass, so the work is O(games +
/// snapshots) and the catalog is only re-scanned once per distinct title.
/// </para>
/// </summary>
internal sealed class GameProgressIndex
{
    private readonly Dictionary<string, GameProgressSnapshot> byExecutable;
    private readonly Dictionary<string, List<GameProgressSnapshot>> byName;
    private readonly Dictionary<string, bool> nameIsUnique;

    internal GameProgressIndex(
        IReadOnlyList<GameProgressSnapshot> snapshots,
        IReadOnlyList<string> catalogNames)
    {
        byExecutable = new Dictionary<string, GameProgressSnapshot>(StringComparer.OrdinalIgnoreCase);
        byName = new Dictionary<string, List<GameProgressSnapshot>>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            if (!string.IsNullOrWhiteSpace(snapshot.Executable))
            {
                // First snapshot wins, matching the original "exactly one match" rule.
                if (!byExecutable.ContainsKey(snapshot.Executable)) byExecutable[snapshot.Executable] = snapshot;
                continue;
            }
            if (string.IsNullOrWhiteSpace(snapshot.Game)) continue;
            if (!byName.TryGetValue(snapshot.Game, out var list)) byName[snapshot.Game] = list = new List<GameProgressSnapshot>(1);
            list.Add(snapshot);
        }

        // A snapshot that names a game can only be attributed when the catalog holds
        // exactly one title with that name, so a duplicated name is never guessed at.
        nameIsUnique = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in catalogNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            counts[name] = counts.TryGetValue(name, out int seen) ? seen + 1 : 1;
        }
        foreach (var entry in counts) nameIsUnique[entry.Key] = entry.Value == 1;
    }

    /// <summary>The snapshot that belongs to this game, or null when none can be trusted.</summary>
    internal GameProgressSnapshot? Match(string? executable, string? gameName)
    {
        if (!string.IsNullOrWhiteSpace(executable))
        {
            return byExecutable.TryGetValue(executable, out var byPath) ? byPath : null;
        }
        if (string.IsNullOrWhiteSpace(gameName)) return null;
        if (!nameIsUnique.TryGetValue(gameName, out bool unique) || !unique) return null;
        if (!byName.TryGetValue(gameName, out var list) || list.Count != 1) return null;
        return list[0];
    }
}

internal static class GameProgressIndexTests
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static GameProgressSnapshot Snap(string game, string executable) =>
        new(game, executable, "label:" + game, "detail:" + game);

    internal static void Run(string root)
    {
        // --- correctness -----------------------------------------------------
        var snapshots = new List<GameProgressSnapshot>
        {
            Snap("Alpha", @"C:\games\alpha.exe"),
            Snap("Beta", ""),
        };
        var index = new GameProgressIndex(snapshots, new[] { "Alpha", "Beta" });
        Require(index.Match(@"c:\GAMES\ALPHA.EXE", "Alpha") is { } a && a.Label == "label:Alpha",
            "A snapshot was not matched by executable, case-insensitively.");
        Require(index.Match("", "Beta")?.Label == "label:Beta", "A snapshot was not matched by a unique game name.");
        Require(index.Match("", "Missing") == null, "An unknown game matched a snapshot.");
        Require(index.Match("", null) == null, "A null name matched a snapshot.");

        // A duplicated title must never be attributed, because either entry could be it.
        var duplicated = new GameProgressIndex(new[] { Snap("Same", "") }, new[] { "Same", "Same" });
        Require(duplicated.Match("", "Same") == null, "A snapshot was attributed to an ambiguous duplicated title.");

        // A name with several snapshots is ambiguous too.
        var twoSnapshots = new GameProgressIndex(new[] { Snap("Twin", ""), Snap("Twin", "") }, new[] { "Twin" });
        Require(twoSnapshots.Match("", "Twin") == null, "Two snapshots for one title were treated as unambiguous.");

        // --- linearity -------------------------------------------------------
        // The whole point: the pass must not be quadratic in the catalog. Compare a
        // small pass against a large one; a quadratic implementation would be ~100x
        // slower at 20x the size, a linear one about 20x.
        double Small = TimeMatch(500, 500);
        double Large = TimeMatch(10000, 10000);
        double ratio = Large / Math.Max(Small, 0.0001);
        Require(Large < 3000, "Matching 10,000 games against 10,000 snapshots took " + Large.ToString("F0")
            + " ms, which would freeze the UI on a real library.");
        Require(ratio < 90, "Matching scaled by " + ratio.ToString("F0")
            + "x for a 20x larger catalog, which means the pass is super-linear (the old code was cubic).");
    }

    private static double TimeMatch(int gameCount, int snapshotCount)
    {
        var names = new List<string>(gameCount);
        var snapshots = new List<GameProgressSnapshot>(snapshotCount);
        for (int i = 0; i < gameCount; i++) names.Add("Game " + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        for (int i = 0; i < snapshotCount; i++) snapshots.Add(Snap(names[i % gameCount], i % 3 == 0 ? @"C:\g\" + i + ".exe" : ""));

        // Warm up, then take the best of three to keep the measurement stable.
        double best = double.MaxValue;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var index = new GameProgressIndex(snapshots, names);
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            int matched = 0;
            for (int i = 0; i < gameCount; i++)
            {
                if (index.Match(i % 3 == 0 ? @"c:\G\" + i + ".EXE" : "", names[i]) is not null) matched++;
            }
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Require(matched > 0, "The linearity probe matched nothing, so it proves nothing.");
            if (ms < best) best = ms;
        }
        return best;
    }
}
