using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace GameLibrary.Native;

internal sealed class FrozenProcessIdentity
{
    internal int ProcessId { get; }
    internal string CreationStamp { get; }

    internal FrozenProcessIdentity(int processId, string creationStamp)
    {
        ProcessId = processId;
        CreationStamp = NormalizeCreationStamp(creationStamp);
    }

    internal static string NormalizeCreationStamp(string? value) =>
        (value ?? string.Empty).Trim().TrimStart('0').ToUpperInvariant() switch
        {
            "" => "0",
            var normalized => normalized
        };
}

internal sealed class FrozenProcessRecord
{
    internal int ProcessId { get; }
    internal string CreationStamp { get; }
    internal string State { get; }
    internal string Mode { get; }

    internal FrozenProcessRecord(int processId, string creationStamp, string state, string mode)
    {
        ProcessId = processId;
        CreationStamp = FrozenProcessIdentity.NormalizeCreationStamp(creationStamp);
        State = state.Trim();
        Mode = mode.Trim();
    }

    // AHK keeps a record while it stabilizes a resumed window, but the process
    // is running again in both transitional states. Only the authoritative
    // paused state must stop playtime.
    internal bool IsPaused => State.Equals("paused", StringComparison.OrdinalIgnoreCase);
}

internal sealed class FrozenProcessSnapshot
{
    internal int Count { get; }
    internal IReadOnlyList<FrozenProcessRecord> Processes { get; }

    internal FrozenProcessSnapshot(int count, IReadOnlyList<FrozenProcessRecord> processes)
    {
        Count = count;
        Processes = processes;
    }

    internal bool IsPausedFor(IEnumerable<FrozenProcessIdentity> identities)
    {
        var identitySet = identities
            .Where(identity => identity.ProcessId > 0 && identity.CreationStamp != "0")
            .Select(identity => identity.ProcessId + "\0" + identity.CreationStamp)
            .ToHashSet(StringComparer.Ordinal);
        return Processes.Any(record => record.IsPaused
            && record.CreationStamp != "0"
            && identitySet.Contains(record.ProcessId + "\0" + record.CreationStamp));
    }
}

internal readonly record struct FrozenProcessReadResult(bool IsAvailable, bool IsPaused, string? Error);

internal static class FrozenProcessState
{
    internal static FrozenProcessReadResult Read(string? path, IEnumerable<FrozenProcessIdentity> identities)
    {
        if (string.IsNullOrWhiteSpace(path)) return new(true, false, null);
        string fullPath;
        try { fullPath = Path.GetFullPath(path.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return new(false, false, ex.Message); }
        if (!File.Exists(fullPath)) return new(true, false, null);
        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var snapshot = Parse(reader.ReadToEnd());
            return new(true, snapshot.IsPausedFor(identities), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or ArgumentException)
        {
            return new(false, false, ex.Message);
        }
    }

    internal static FrozenProcessSnapshot Parse(string text)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        using var lines = new StringReader(text.TrimStart('\uFEFF'));
        string? line;
        while ((line = lines.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                string name = line[1..^1].Trim();
                if (name.Length == 0) throw new FormatException("The frozen-process state contains an empty section.");
                current = sections.TryGetValue(name, out var existing)
                    ? existing
                    : sections[name] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            int separator = line.IndexOf('=');
            if (separator <= 0 || current == null) throw new FormatException("The frozen-process state contains an invalid entry.");
            current[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        if (!sections.TryGetValue("FrozenProcesses", out var header)
            || !header.TryGetValue("Count", out var countText)
            || !int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
            || count < 0 || count > 256)
            throw new FormatException("The frozen-process state has an invalid Count.");

        var records = new List<FrozenProcessRecord>(count);
        for (int index = 1; index <= count; index++)
        {
            if (!sections.TryGetValue("FrozenProcess" + index, out var section))
                throw new FormatException("The frozen-process state is missing FrozenProcess" + index + ".");
            if (!section.TryGetValue("Pid", out var pidText)
                || !int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid)
                || pid <= 0
                || !section.TryGetValue("Created", out var created)
                || string.IsNullOrWhiteSpace(created)
                || !section.TryGetValue("State", out var state)
                || string.IsNullOrWhiteSpace(state))
                throw new FormatException("FrozenProcess" + index + " has incomplete identity data.");
            section.TryGetValue("Mode", out var mode);
            records.Add(new FrozenProcessRecord(pid, created, state, mode ?? string.Empty));
        }
        return new FrozenProcessSnapshot(count, records);
    }

    internal static bool TryGetCreationStamp(Process process, out string creationStamp)
    {
        try
        {
            creationStamp = FrozenProcessIdentity.NormalizeCreationStamp(
                process.StartTime.ToUniversalTime().ToFileTimeUtc().ToString("X16", CultureInfo.InvariantCulture));
            return creationStamp != "0";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or NotSupportedException)
        {
            creationStamp = "0";
            return false;
        }
    }
}

internal sealed class ActivePlaytime
{
    private readonly long frequency;
    private long lastTimestamp;
    private double activeSeconds;

    internal ActivePlaytime(double startingSeconds, long startTimestamp, long? timestampFrequency = null)
    {
        StartingSeconds = double.IsFinite(startingSeconds) && startingSeconds > 0 ? startingSeconds : 0;
        frequency = timestampFrequency.GetValueOrDefault(Stopwatch.Frequency);
        if (frequency <= 0) frequency = Stopwatch.Frequency;
        lastTimestamp = startTimestamp;
    }

    internal double StartingSeconds { get; }
    internal bool IsPaused { get; private set; }
    internal double TotalSeconds => StartingSeconds + activeSeconds;

    internal void Sample(long timestamp, bool paused)
    {
        if (timestamp >= lastTimestamp && !IsPaused)
            activeSeconds += (timestamp - lastTimestamp) / (double)frequency;
        lastTimestamp = timestamp;
        IsPaused = paused;
    }
}
