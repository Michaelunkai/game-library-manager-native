using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace GameLibrary.Native;

public sealed class LocalGame
{
    public string Name { get; set; } = "";
    public string Folder { get; set; } = "";
    public string Category { get; set; } = "new";
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;

    // A local identity cannot be confused with a Docker tag or shared catalog key.
    public static string Identity(string folder) => "local:" + Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)).ToUpperInvariant()))).ToLowerInvariant();
}

public sealed record InstalledDiscovery(string Id, string Name, string Folder, string? Launcher, bool IsLocal, long InstalledBytes = 0);

internal static class InstalledSize
{
    internal static long Measure(string folder, System.Threading.CancellationToken cancellation = default)
    {
        if (!Directory.Exists(folder)) return 0;
        long total = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        try
        {
            foreach (string path in Directory.EnumerateFiles(folder, "*", options))
            {
                cancellation.ThrowIfCancellationRequested();
                try { total = checked(total + new FileInfo(path).Length); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return Math.Max(0, total);
    }
}

public sealed class InstalledScanResult
{
    public List<InstalledDiscovery> Games { get; } = new();
    public List<string> Notices { get; } = new();
    internal HashSet<string> AmbiguousIdentities { get; } = new(StringComparer.Ordinal);
    // A catalog folder can be present even when it contains no selectable EXE.
    // Keep that distinction so an explicit scan can clear stale markers without
    // treating an incomplete download as an absent installation.
    internal HashSet<string> CatalogFoldersPresent { get; } = new(StringComparer.Ordinal);
    // A folder with an executable can still require a manual choice when it
    // contains several candidates. Keep that playable distinction separate
    // from mere folder presence so support-only Docker payloads cannot remain
    // marked Installed after an explicit scan.
    internal HashSet<string> CatalogGamesWithExecutable { get; } = new(StringComparer.Ordinal);
    public int LauncherCount => Games.Count(g => g.Launcher != null);
}

public partial class MainWindow
{
    internal bool ApplyInstalledScan(InstalledScanResult result, bool reconcileMissingCatalog = false, bool logNotices = true)
    {
        bool changed = false;
        foreach (var entry in result.Games)
        {
            if (entry.IsLocal && !State.LocalGames.ContainsKey(entry.Id))
            {
                State.LocalGames[entry.Id] = new LocalGame { Name = entry.Name, Folder = entry.Folder };
                changed = true;
            }
            else if (entry.IsLocal && State.LocalGames.TryGetValue(entry.Id, out var local) &&
                (!string.Equals(local.Name, entry.Name, StringComparison.Ordinal) || !string.Equals(local.Folder, entry.Folder, StringComparison.Ordinal)))
            {
                local.Name = entry.Name; local.Folder = entry.Folder; changed = true;
            }
            // An explicit launcher choice wins over scanner inference while it still exists.
            if (entry.Launcher != null && (!State.LaunchPaths.TryGetValue(entry.Id, out var previous) || !File.Exists(previous)))
            {
                State.LaunchPaths[entry.Id] = entry.Launcher;
                changed = true;
            }
            if (State.InstalledGames.Add(entry.Id)) changed = true;
            if (entry.InstalledBytes > 0 && State.InstalledBytes.GetValueOrDefault(entry.Id) != entry.InstalledBytes)
            {
                State.InstalledBytes[entry.Id] = entry.InstalledBytes;
                State.InstalledSizeMeasuredUtc[entry.Id] = DateTime.UtcNow;
                changed = true;
            }
        }
        if (reconcileMissingCatalog)
        {
            // An explicit scan of an available root is authoritative for catalog
            // installs with no playable executable. Preserve a playable folder
            // that still needs an executable choice and preserve a manually
            // selected launcher on another path.
            var catalogIds = Games.Where(g => !g.IsLocal).Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in State.InstalledGames.ToArray())
            {
                if (!catalogIds.Contains(id) || result.CatalogGamesWithExecutable.Contains(id)) continue;
                if (State.LaunchPaths.TryGetValue(id, out var saved) && File.Exists(saved)) continue;
                if (State.InstalledGames.Remove(id)) changed = true;
                if (State.InstalledBytes.Remove(id)) changed = true;
                if (State.InstalledSizeMeasuredUtc.Remove(id)) changed = true;
            }
        }
        if (logNotices)
            foreach (var notice in result.Notices) Store.Log("Installed scan: " + notice);
        if (changed) { Save(); Reload(); }
        return changed;
    }

    internal async Task ScanCompletedDownloads(Game[] games, string destination, bool processSucceeded, DateTime? completionStartedAtUtc = null, string? operationId = null)
    {
        try
        {
            // A job is complete only after its per-game marker was written inside the
            // bind mount. This prevents a partial multi-game job from turning an early
            // executable copy into an Installed marker while still allowing completed
            // games from a failed batch to appear immediately.
            // A marker from an older operation must never be attributed to this
            // job. Jobs that never started have no valid completion timestamp,
            // so they intentionally produce an empty completed set.
            var completed = completionStartedAtUtc is DateTime started
                ? games.Where(game => InstalledScanner.HasFreshCompletionMarker(destination, game.Id, started, operationId: operationId)).ToArray()
                : Array.Empty<Game>();
            var snapshot = games.Select(g => (g.Id, g.Name)).ToArray();
            var found = await Task.Run(() => InstalledScanner.ScanDownloads(destination, snapshot, lifetime.Token));
            var completedIds = completed.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
            found.Games.RemoveAll(entry => !entry.IsLocal && !completedIds.Contains(entry.Id));
            ApplyInstalledScan(found);
            int missing = games.Length - completed.Length;
            int noLauncher = completed.Count(g => !found.Games.Any(f => f.Id == g.Id && f.Launcher != null));
            string prefix = processSucceeded && missing == 0 ? "Download finished." : "Download batch finished with partial results.";
            StatusText.Text = $"{prefix} {completed.Length}/{games.Length} game(s) have a verified completion marker and are now Installed." +
                (noLauncher > 0 ? $" Choose an executable in Details for {noLauncher} game(s)." : "") +
                (missing > 0 ? $" {missing} download(s) did not write a completion marker; existing files were preserved." : "");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Store.Log("Downloaded files preserved; automatic installed scan failed: " + ex.Message);
            StatusText.Text = "Downloaded files were preserved. Scan the download folder to choose a launcher: " + ex.Message;
        }
    }

    internal async Task ScanCompletedGame(Game game, string destination, DateTime? completionStartedAtUtc = null, string? operationId = null)
    {
        try
        {
            if (completionStartedAtUtc is DateTime started)
            {
                if (!InstalledScanner.HasFreshCompletionMarker(destination, game.Id, started, game.Name, operationId)) return;
            }
            else if (!InstalledScanner.HasCompletionMarker(destination, game.Id, game.Name)) return;
            var found = await Task.Run(() => InstalledScanner.ScanDownloads(destination, new[] { (game.Id, game.Name) }, lifetime.Token), lifetime.Token);
            found.Games.RemoveAll(entry => !entry.IsLocal && entry.Id != game.Id);
            if (found.Games.Any(entry => entry.Id == game.Id))
            {
                ApplyInstalledScan(found);
                var entry = found.Games.First(e => e.Id == game.Id);
                StatusText.Text = entry.Launcher == null
                    ? game.Name + " finished installing and is now Installed. Choose its executable in Details."
                    : game.Name + " finished installing and is now Installed.";
            }
            else
            {
                foreach (var notice in found.Notices) Store.Log("Installed scan: " + notice);
                StatusText.Text = game.Name + " wrote a completion marker, but no game executable was found; it was left uninstalled.";
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Store.Log("Immediate installed scan for " + game.Id + " failed; the final batch scan will retry: " + ex.Message);
        }
    }
}
