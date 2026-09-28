using System;
using System.Collections.Concurrent;
using System.IO;

namespace GameLibrary.Native;

/// <summary>
/// Caches File.Exists results for launch/cover paths so catalog projection and
/// statistics never hit the disk repeatedly on the UI thread. Cleared whenever
/// the catalog or installed state is reloaded so real filesystem changes are
/// still observed.
/// </summary>
internal static class PathExistsCache
{
    private static readonly ConcurrentDictionary<string, bool> Map = new(StringComparer.OrdinalIgnoreCase);

    internal static bool Check(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return Map.GetOrAdd(path, static p => File.Exists(p));
    }

    internal static void Clear() => Map.Clear();
}