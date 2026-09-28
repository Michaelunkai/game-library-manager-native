using System;
using System.Collections.Generic;
using System.IO;

namespace GameLibrary.Native;

internal sealed class InstalledStorageCache
{
    private readonly Dictionary<string, (InstalledStorageResult Result, TimeSpan NextBudget)> folders = new(StringComparer.OrdinalIgnoreCase);
    private static string Key(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
    internal TimeSpan Budget(string folder) => folders.TryGetValue(Key(folder), out var entry) ? entry.NextBudget : TimeSpan.FromSeconds(30);
    internal InstalledStorageResult? Fresh(string folder, DateTime now)
    {
        if (!folders.TryGetValue(Key(folder), out var entry)) return null;
        var ttl = !entry.Result.Complete && entry.Result.Reason == "Time limit reached" ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(10);
        return now - entry.Result.MeasuredUtc < ttl ? entry.Result : null;
    }
    internal void Record(string folder, InstalledStorageResult result, TimeSpan usedBudget)
    {
        var next = !result.Complete && result.Reason == "Time limit reached"
            ? TimeSpan.FromSeconds(Math.Min(300, Math.Max(30, usedBudget.TotalSeconds) * 2))
            : TimeSpan.FromSeconds(Math.Clamp(usedBudget.TotalSeconds, 30, 300));
        folders[Key(folder)] = (result, next);
    }
}
