using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace GameLibrary.Native;

internal static class WandLiveLibrary
{
    private static readonly object Gate = new();
    private static string? fingerprint;
    private static string? observedFingerprint;
    private static readonly WandRegistrationRefreshState<WandSupportedGame> refreshState = new();
    private static IReadOnlyList<WandSupportedGame> cached { get => refreshState.Rows; set => refreshState.Publish(value); }
    private static readonly Dictionary<string, FileSystemWatcher> watchers = new(StringComparer.OrdinalIgnoreCase);
    private static Timer? refreshTimer;
    private static Timer? pollTimer;
    private static bool dirty = true;
    private static DateTime lastAttemptUtc = DateTime.MinValue;
    private static DateTime observedChangeUtc = DateTime.MinValue;
    private static string lastFailure = "";

    internal static event EventHandler? RegistrationSetChanged;
    internal static string LastFailure { get { lock (Gate) return lastFailure; } }

    internal static IReadOnlyList<WandSupportedGame> Read()
    {
        lock (Gate)
        {
            EnsurePollTimer();
            DateTime now = DateTime.UtcNow;
            if (!refreshState.CanAttempt(now))
                return cached;
            if (!dirty && now - lastAttemptUtc < TimeSpan.FromSeconds(10)) return cached;
            lastAttemptUtc = now;
            dirty = false;
            try
            {
                string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string[] levelDbRoots = DiscoverLevelDbRoots(roaming);
                EnsureWatchers(levelDbRoots);
                var roots = levelDbRoots.Select(root => new { Root = root, Files = Directory.GetFiles(root)
                        .Where(file => Path.GetExtension(file) is ".log" or ".ldb" || Path.GetFileName(file).StartsWith("MANIFEST-", StringComparison.Ordinal))
                        .Select(file => new FileInfo(file)).ToArray() }).Where(item => item.Files.Length > 0)
                    .OrderByDescending(item => item.Files.Max(file => file.LastWriteTimeUtc)).ToArray();
                if (roots.Length == 0)
                {
                    lastFailure = "No current Wand LevelDB registration snapshot is available.";
                    dirty = true;
                    ScheduleRetry();
                    return cached;
                }

                var selected = roots[0];
                string stamp = selected.Root + "|" + string.Join("|", selected.Files.OrderBy(file => file.Name)
                    .Select(file => file.Name + ":" + file.Length + ":" + file.LastWriteTimeUtc.Ticks));
                if (stamp == fingerprint)
                {
                    lastFailure = "";
                    refreshState.RecordSuccess(cached);
                    return cached;
                }

                bool initialSnapshot = observedFingerprint == null;
                if (!string.Equals(stamp, observedFingerprint, StringComparison.Ordinal))
                {
                    observedFingerprint = stamp;
                    observedChangeUtc = DateTime.UtcNow;
                    if (!initialSnapshot)
                    {
                        dirty = true;
                        ScheduleRefresh();
                        return cached;
                    }
                }
                if (!initialSnapshot && DateTime.UtcNow - observedChangeUtc < TimeSpan.FromMilliseconds(400))
                {
                    dirty = true;
                    ScheduleRefresh();
                    return cached;
                }

                if (!refreshState.TryLoad(DateTime.UtcNow, () => ReadRegistrations(selected.Root),
                    out var rows, out var readFailure))
                {
                    if (readFailure == null) return cached;
                    lastFailure = readFailure.Message;
                    dirty = true;
                    ScheduleRefresh(refreshState.RetryDelayRemaining(DateTime.UtcNow));
                    if (cached.Count > 0) return cached;
                    throw new WandRegistrationReadException("Wand registrations are unavailable: " + readFailure.Message, readFailure);
                }
                var afterReadFiles = Directory.GetFiles(selected.Root)
                    .Where(file => Path.GetExtension(file) is ".log" or ".ldb" || Path.GetFileName(file).StartsWith("MANIFEST-", StringComparison.Ordinal))
                    .Select(file => new FileInfo(file)).OrderBy(file => file.Name, StringComparer.Ordinal).ToArray();
                string afterReadStamp = selected.Root + "|" + string.Join("|", afterReadFiles
                    .Select(file => file.Name + ":" + file.Length + ":" + file.LastWriteTimeUtc.Ticks));
                if (!string.Equals(stamp, afterReadStamp, StringComparison.Ordinal))
                {
                    observedFingerprint = afterReadStamp;
                    observedChangeUtc = DateTime.UtcNow;
                    dirty = true;
                    ScheduleRefresh();
                    return cached;
                }
                NotifyRegistrationSetChangedIfNeeded(cached, rows, initialSnapshot);
                cached = rows;
                fingerprint = stamp;
                observedFingerprint = stamp;
                lastFailure = "";
                return cached;
            }
            catch (WandRegistrationReadException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
            {
                lastFailure = ex.Message;
                dirty = true;
                ScheduleRetry();
                return cached.Count > 0 ? cached : throw new IOException("Wand registrations are unavailable: " + ex.Message, ex);
            }
        }
    }

    internal static string[] DiscoverLevelDbRoots(string roaming)
    {
        return new[] { "wemod", "Wand" }
            .Select(name => Path.Combine(roaming, name, "Local Storage", "leveldb"))
            .Where(Directory.Exists).ToArray();
    }

    private static IReadOnlyList<WandSupportedGame> ReadRegistrations(string levelDbRoot)
    {
        string tools = Path.Combine(AppContext.BaseDirectory, "tools");
        string node = Path.Combine(tools, "node", "node.exe");
        string helper = Path.Combine(tools, "wand_live_library.js");
        if (!File.Exists(node) || !File.Exists(helper)) throw new FileNotFoundException("The packaged Wand registration reader is unavailable.");
        var info = new ProcessStartInfo(node)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(helper);
        info.ArgumentList.Add(levelDbRoot);
        using var process = Process.Start(info) ?? throw new IOException("Could not read the current Wand library.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(20000)) { process.Kill(true); throw new IOException("Reading the current Wand library timed out."); }
        string json = output.GetAwaiter().GetResult();
        string errorText = error.GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new IOException("Wand library read failed: " + errorText.Trim());
        var rows = JsonSerializer.Deserialize<List<WandSupportedGame>>(json, DataJson.Options) ?? throw new IOException("Empty Wand library response.");
        if (rows.Any(row => string.IsNullOrWhiteSpace(row.Folder) || string.IsNullOrWhiteSpace(row.GameId)
            || row.GameId != row.GameId.Trim() || string.IsNullOrWhiteSpace(row.TitleId)
            || string.IsNullOrWhiteSpace(row.Name) || !Path.IsPathFullyQualified(row.Path))
            || rows.Select(row => row.GameId).Distinct(StringComparer.Ordinal).Count() != rows.Count)
            throw new IOException("Invalid Wand library registrations.");
        return rows;
    }

    internal static bool SameRegistrationSet(IReadOnlyList<WandSupportedGame> left, IReadOnlyList<WandSupportedGame> right)
    {
        if (left.Count != right.Count) return false;
        var byId = right.ToDictionary(row => row.GameId, StringComparer.Ordinal);
        foreach (var row in left)
        {
            if (!byId.TryGetValue(row.GameId, out var other)
                || !string.Equals(row.Folder, other.Folder, StringComparison.Ordinal)
                || !string.Equals(row.TitleId, other.TitleId, StringComparison.Ordinal)
                || !string.Equals(row.Name, other.Name, StringComparison.Ordinal)
                || !string.Equals(row.Path, other.Path, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    internal static bool NotifyRegistrationSetChangedIfNeeded(
        IReadOnlyList<WandSupportedGame> previous,
        IReadOnlyList<WandSupportedGame> current,
        bool initialSnapshot)
    {
        if (initialSnapshot || SameRegistrationSet(previous, current)) return false;
        PublishRegistrationSetChanged();
        return true;
    }

    private static void PublishRegistrationSetChanged()
    {
        var handler = RegistrationSetChanged;
        if (handler == null) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            foreach (EventHandler subscriber in handler.GetInvocationList())
            {
                try { subscriber(null, EventArgs.Empty); }
                catch (Exception ex) { Debug.WriteLine("Wand registration change subscriber failed: " + ex); }
            }
        });
    }

    private static void EnsureWatchers(IEnumerable<string> roots)
    {
        var desiredRoots = roots.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string staleRoot in watchers.Keys.Where(root => !desiredRoots.Contains(root)).ToArray())
        {
            try { watchers[staleRoot].Dispose(); }
            catch (Exception ex) { Debug.WriteLine("Could not dispose stale Wand watcher: " + ex.Message); }
            watchers.Remove(staleRoot);
        }
        foreach (string root in desiredRoots)
        {
            if (watchers.ContainsKey(root)) continue;
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                    Filter = "*"
                };
                watcher.Changed += OnRegistrationSourceChanged;
                watcher.Created += OnRegistrationSourceChanged;
                watcher.Deleted += OnRegistrationSourceChanged;
                watcher.Renamed += OnRegistrationSourceChanged;
                watcher.Error += OnRegistrationWatcherError;
                watchers.Add(root, watcher);
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                watchers.Remove(root);
                watcher?.Dispose();
                lastFailure = "Wand registration change notifications are unavailable: " + ex.Message;
            }
        }
    }

    private static void OnRegistrationSourceChanged(object sender, FileSystemEventArgs args) => MarkDirty();
    private static void OnRegistrationWatcherError(object sender, ErrorEventArgs args) => MarkDirty();

    private static void MarkDirty()
    {
        lock (Gate) dirty = true;
        ScheduleRefresh();
    }

    private static void EnsurePollTimer()
    {
        pollTimer ??= new Timer(_ => PollForRegistrationChanges(), null,
            TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
    }

    private static void PollForRegistrationChanges()
    {
        try { _ = Read(); }
        catch (IOException ex) { lock (Gate) lastFailure = ex.Message; }
        finally
        {
            lock (Gate) pollTimer?.Change(TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
        }
    }

    private static void ScheduleRetry()
    {
        var delay = refreshState.RecordFailure(DateTime.UtcNow);
        ScheduleRefresh(delay);
    }

    private static void ScheduleRefresh()
        => ScheduleRefresh(TimeSpan.FromMilliseconds(400));

    private static void ScheduleRefresh(TimeSpan requestedDelay)
    {
        lock (Gate)
        {
            refreshTimer ??= new Timer(_ =>
            {
                try { _ = Read(); }
                catch (IOException ex) { lock (Gate) lastFailure = ex.Message; }
            }, null, Timeout.Infinite, Timeout.Infinite);
            TimeSpan retryDelay = refreshState.RetryDelayRemaining(DateTime.UtcNow);
            TimeSpan delay = requestedDelay > retryDelay ? requestedDelay : retryDelay;
            refreshTimer.Change((int)Math.Clamp(delay.TotalMilliseconds, 1, int.MaxValue), Timeout.Infinite);
        }
    }
}

internal sealed class WandRegistrationReadException : IOException
{
    internal WandRegistrationReadException(string message, Exception innerException) : base(message, innerException) { }
}

internal sealed class WandRegistrationRefreshState<T>
{
    private int consecutiveFailures;
    private DateTime retryNotBeforeUtc = DateTime.MinValue;

    internal IReadOnlyList<T> Rows { get; private set; } = Array.Empty<T>();
    internal int ConsecutiveFailures => consecutiveFailures;

    internal bool CanAttempt(DateTime nowUtc) => nowUtc >= retryNotBeforeUtc;

    internal bool TryLoad(DateTime nowUtc, Func<IReadOnlyList<T>> load,
        out IReadOnlyList<T> rows, out Exception? failure)
    {
        if (!CanAttempt(nowUtc))
        {
            rows = Rows;
            failure = null;
            return false;
        }
        try
        {
            rows = load();
            failure = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            RecordFailure(nowUtc);
            rows = Rows;
            failure = ex;
            return false;
        }
    }

    internal TimeSpan RetryDelayRemaining(DateTime nowUtc) =>
        retryNotBeforeUtc > nowUtc ? retryNotBeforeUtc - nowUtc : TimeSpan.Zero;

    internal TimeSpan RecordFailure(DateTime nowUtc)
    {
        consecutiveFailures = Math.Min(consecutiveFailures + 1, 6);
        double seconds = Math.Min(30, Math.Pow(2, consecutiveFailures - 1));
        TimeSpan delay = TimeSpan.FromSeconds(seconds);
        retryNotBeforeUtc = nowUtc + delay;
        return delay;
    }

    internal void Publish(IReadOnlyList<T> rows)
    {
        Rows = rows;
        consecutiveFailures = 0;
        retryNotBeforeUtc = DateTime.MinValue;
    }

    internal void RecordSuccess(IReadOnlyList<T> rows) => Publish(rows);
}
