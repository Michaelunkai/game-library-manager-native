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

public sealed record InstalledDiscovery(string Id, string Name, string Folder, string? Launcher, bool IsLocal);

public sealed class InstalledScanResult
{
    public List<InstalledDiscovery> Games { get; } = new();
    public List<string> Notices { get; } = new();
    internal HashSet<string> AmbiguousIdentities { get; } = new(StringComparer.Ordinal);
    // A catalog folder can be present even when it contains no selectable EXE.
    // Keep that distinction so an explicit scan can clear stale markers without
    // treating an incomplete download as an absent installation.
    internal HashSet<string> CatalogFoldersPresent { get; } = new(StringComparer.Ordinal);
    public int LauncherCount => Games.Count(g => g.Launcher != null);
}

public partial class MainWindow
{
    internal void ApplyInstalledScan(InstalledScanResult result, bool reconcileMissingCatalog = false)
    {
        foreach (var entry in result.Games)
        {
            if (entry.IsLocal && !State.LocalGames.ContainsKey(entry.Id))
                State.LocalGames[entry.Id] = new LocalGame { Name = entry.Name, Folder = entry.Folder };
            // An explicit launcher choice wins over scanner inference while it still exists.
            if (entry.Launcher != null && (!State.LaunchPaths.TryGetValue(entry.Id, out var previous) || !File.Exists(previous)))
                State.LaunchPaths[entry.Id] = entry.Launcher;
            State.InstalledGames.Add(entry.Id);
        }
        if (reconcileMissingCatalog)
        {
            // An explicit scan of an available root is authoritative for catalog
            // folders. Preserve a manually selected launcher on another path, and
            // preserve folders that exist but still need an executable choice.
            var catalogIds = Games.Where(g => !g.IsLocal).Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in State.InstalledGames.ToArray())
            {
                if (!catalogIds.Contains(id) || result.CatalogFoldersPresent.Contains(id)) continue;
                if (State.LaunchPaths.TryGetValue(id, out var saved) && File.Exists(saved)) continue;
                State.InstalledGames.Remove(id);
            }
        }
        foreach (var notice in result.Notices) Store.Log("Installed scan: " + notice);
        Save(); Reload();
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
