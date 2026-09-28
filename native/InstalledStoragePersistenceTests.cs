using System;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;

namespace GameLibrary.Native;

public static class InstalledStoragePersistenceTests
{
    public static int Run(string root)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows installed-storage persistence tests");
        string suite = Path.Combine(root, "installed-storage-persistence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(suite);
        var cacheType = typeof(InstalledStorageResult).Assembly.GetType("GameLibrary.Native.PersistentInstalledStorageCache");
        Require(cacheType != null, "Persistent installed-size cache is missing; complete measurements cannot hydrate after restart.");

        string cachePath = Path.Combine(suite, "installed-storage.json");
        string folder = Directory.CreateDirectory(Path.Combine(suite, "GameFolder")).FullName;
        string changedFolder = Directory.CreateDirectory(Path.Combine(suite, "MovedGameFolder")).FullName;
        File.WriteAllText(cachePath,
            "{\"schemaVersion\":1,\"unrelated\":{\"keep\":true},\"measurements\":{" +
            "\"GameID\":{\"futureField\":\"keep-entry\",\"complete\":false}," +
            "\"legacyAlias\":{\"futureField\":\"keep-alias\"}," +
            "\"partialSeed\":{\"installationFolder\":\"" + Escape(folder) + "\",\"bytes\":9,\"complete\":false,\"measuredUtc\":\"2026-09-28T00:00:00Z\",\"fileCount\":1,\"skippedCount\":1,\"scope\":\"logical-file-bytes\",\"unit\":\"GB\",\"decimalGb\":0.000000009}}}");

        DateTime measuredUtc = DateTime.UtcNow.AddSeconds(-1);
        DateTime now = DateTime.UtcNow;
        var first = new InstalledStorageResult(12_345_000_000L, true, measuredUtc, 17, 0, null);
        var caseAlias = new InstalledStorageResult(8_765_000_000L, true, measuredUtc, 9, 0, null);
        var cache = Create(cacheType!, cachePath);
        Require(Record(cacheType!, cache, "GameID", folder, first), "Complete result was not stored.");
        Require(Record(cacheType!, cache, "gameid", folder, caseAlias), "Case-distinct alias was not stored.");

        var partial = new InstalledStorageResult(99, false, measuredUtc, 1, 1, "Injected omission");
        Require(!Record(cacheType!, cache, "partialAttempt", folder, partial), "Partial result was accepted as an exact cache entry.");

        var reloaded = Create(cacheType!, cachePath);
        Require(Fresh(cacheType!, reloaded, "GameID", folder, now) == first, "Fresh path-matched result did not round-trip exactly.");
        Require(Fresh(cacheType!, reloaded, "gameid", folder, now) == caseAlias, "Case-distinct alias was collapsed or lost.");
        Require(Fresh(cacheType!, reloaded, "GameID", changedFolder, now) == null, "Changed installation path reused an old measurement.");
        Require(Fresh(cacheType!, reloaded, "GameID", folder, measuredUtc.AddMinutes(11)) == null, "Stale measurement was hydrated.");
        Require(Fresh(cacheType!, reloaded, "GameID", folder, measuredUtc.AddSeconds(-1)) == null, "Future-dated measurement was hydrated.");
        Require(Fresh(cacheType!, reloaded, "partialSeed", folder, now) == null, "Partial persisted row was hydrated.");
        Require(Fresh(cacheType!, reloaded, "partialAttempt", folder, now) == null, "Rejected partial result became readable.");

        var folderAliasCache = new InstalledStorageCache();
        folderAliasCache.Record(folder, first, TimeSpan.FromSeconds(30));
        Require(folderAliasCache.Fresh(folder + Path.DirectorySeparatorChar, now) == first,
            "Two game aliases sharing one installation folder did not reuse the complete measurement.");

        string missingCachePath = Path.Combine(suite, "missing-cache.json");
        var missingCache = Create(cacheType!, missingCachePath);
        Require(Fresh(cacheType!, missingCache, "GameID", folder, now) == null, "Missing cache produced a fabricated measurement.");
        string corruptCachePath = Path.Combine(suite, "corrupt-cache.json");
        File.WriteAllText(corruptCachePath, "{broken");
        var corruptCache = Create(cacheType!, corruptCachePath);
        Require(Fresh(cacheType!, corruptCache, "GameID", folder, now) == null, "Corrupt cache produced a fabricated measurement.");
        bool corruptWriteRejected = false;
        try { Record(cacheType!, corruptCache, "GameID", folder, first); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { corruptWriteRejected = true; }
        Require(corruptWriteRejected && File.ReadAllText(corruptCachePath) == "{broken",
            "A corrupt cache was overwritten while preserving no recoverable preimage.");

        var document = JsonNode.Parse(File.ReadAllText(cachePath), new JsonNodeOptions { PropertyNameCaseInsensitive = false })!.AsObject();
        Require(document["unrelated"]?["keep"]?.GetValue<bool>() == true, "Unknown root field was discarded.");
        var entries = document["measurements"]!.AsObject();
        Require(entries["GameID"]?["futureField"]?.GetValue<string>() == "keep-entry", "Unknown updated-entry field was discarded.");
        Require(entries["legacyAlias"]?["futureField"]?.GetValue<string>() == "keep-alias", "Unrelated alias entry was discarded.");
        Require(entries.ContainsKey("GameID") && entries.ContainsKey("gameid"), "Case-distinct aliases did not remain separate.");
        var receipt = entries["GameID"]!.AsObject();
        Require(receipt["scope"]?.GetValue<string>() == "logical-file-bytes", "Measurement scope was not retained.");
        Require(receipt["unit"]?.GetValue<string>() == "GB", "Decimal GB unit was not retained.");
        Require(receipt["decimalGb"]?.GetValue<decimal>() == 12.345m, "Decimal GB value was not retained exactly.");
        Require(DateTime.Parse(receipt["measuredUtc"]!.GetValue<string>()).ToUniversalTime() == measuredUtc,
            "UTC measurement timestamp was not retained.");
        Require(!entries.ContainsKey("partialAttempt"), "Rejected partial result mutated the cache.");
        return 10;
    }

    private static object Create(Type type, string path) =>
        Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: new object[] { path }, culture: null)
        ?? throw new InvalidOperationException("Could not construct the persistent storage cache.");

    private static bool Record(Type type, object cache, string id, string folder, InstalledStorageResult result)
    {
        var method = type.GetMethod("RecordComplete", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Persistent cache is missing RecordComplete.");
        return (bool)(method.Invoke(cache, new object[] { id, folder, result })
            ?? throw new InvalidOperationException("RecordComplete returned no result."));
    }

    private static InstalledStorageResult? Fresh(Type type, object cache, string id, string folder, DateTime now)
    {
        var method = type.GetMethod("Fresh", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Persistent cache is missing Fresh.");
        return method.Invoke(cache, new object[] { id, folder, now }) as InstalledStorageResult;
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
