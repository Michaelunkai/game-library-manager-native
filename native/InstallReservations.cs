using System;
using System.Collections.Generic;
using System.Linq;

namespace GameLibrary.Native;

internal sealed class InstallReservationBook
{
    private readonly object sync = new();
    private readonly HashSet<string> reserved = new(StringComparer.Ordinal);

    internal string[] Reserve(IEnumerable<string> requested)
    {
        if (requested == null) throw new ArgumentNullException(nameof(requested));
        var unique = requested.Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.Ordinal).ToArray();
        lock (sync)
        {
            var accepted = new List<string>(unique.Length);
            foreach (var key in unique)
                if (reserved.Add(key)) accepted.Add(key);
            return accepted.ToArray();
        }
    }

    internal void Release(IEnumerable<string> keys)
    {
        if (keys == null) return;
        lock (sync)
            foreach (var key in keys) reserved.Remove(key);
    }
}
