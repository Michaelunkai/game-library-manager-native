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
    internal string? LibraryRoot { get; set; }
    public List<InstalledDiscovery> Games { get; } = new();
    internal List<(string Launcher, string Folder)> ExecutableFolders { get; } = new();
    internal List<(string Id, string Folder)> ExplicitCatalogFolders { get; } = new();
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

public static class InstalledFolderMapping
{
    internal static bool ApplyLocalDiscovery(UserState state, InstalledDiscovery entry)
    {
        if (!entry.IsLocal) return false;
        if (!state.LocalGames.TryGetValue(entry.Id, out var local))
        {
            state.LocalGames[entry.Id] = new LocalGame { Name = entry.Name, Folder = entry.Folder };
            return true;
        }
        bool defaultName = string.IsNullOrWhiteSpace(local.Name) || string.Equals(
            local.Name, Path.GetFileName(Path.TrimEndingDirectorySeparator(local.Folder)), StringComparison.OrdinalIgnoreCase);
        bool changed = false;
        if (defaultName && local.Name != entry.Name) { local.Name = entry.Name; changed = true; }
        if (local.Folder != entry.Folder) { local.Folder = entry.Folder; changed = true; }
        return changed;
    }
    internal static bool ReconcileMissing(UserState state, InstalledScanResult scan)
    {
        // Only a successfully enumerated library can prove a recorded game's
        // immediate directory was removed. Existence APIs alone hide access errors.
        if (scan.LibraryRoot is not string root) return false;
        try
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return false;
            var present = Directory.GetFileSystemEntries(root).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            bool changed = false;
            foreach (string id in state.InstalledGames.ToArray())
            {
                if (scan.Games.Any(g => g.Id == id) || scan.CatalogGamesWithExecutable.Contains(id)) continue;
                string? launcher = state.LaunchPaths.GetValueOrDefault(id);
                if (launcher != null && File.Exists(launcher)) continue;
                string? location = state.InstallationFolders.GetValueOrDefault(id);
                if (string.IsNullOrWhiteSpace(location)) location = state.LocalGames.GetValueOrDefault(id)?.Folder;
                if (string.IsNullOrWhiteSpace(location)) location = launcher;
                if (string.IsNullOrWhiteSpace(location) || !Path.IsPathFullyQualified(location)) continue;
                string full;
                try { full = Path.GetFullPath(location); }
                catch (ArgumentException) { continue; }
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string relative = full[prefix.Length..];
                string child = Path.Combine(root, relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0]);
                if (present.Contains(child)) continue;
                // Recheck absence after enumeration; permission failures and
                // temporarily disconnected roots must never imply uninstall.
                try { File.GetAttributes(child); continue; }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                if (!Directory.Exists(root)) return changed;
                changed |= state.InstalledGames.Remove(id);
            }
            return changed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    internal static bool RemoveStagingRegistrations(UserState state)
    {
        bool changed = false;
        foreach (var entry in state.LocalGames.ToArray())
        {
            if (!InstalledScanner.IsInstallerStagingPath(entry.Value.Folder)) continue;
            state.LocalGames.Remove(entry.Key);
            state.InstalledGames.Remove(entry.Key);
            changed = true;
        }
        return changed;
    }
    public static bool Apply(UserState state, InstalledScanResult scan)
    {
        bool changed = false;
        foreach (var id in state.InstalledGames)
        {
            // Existing local registrations may later gain a catalog identity.
            // Recover the launcher by their exact saved folder without replacing
            // either identity or its personal history.
            if (!state.LaunchPaths.TryGetValue(id, out var chosen) || !File.Exists(chosen))
            {
                string? savedFolder = state.InstallationFolders.GetValueOrDefault(id);
                if (string.IsNullOrWhiteSpace(savedFolder)) savedFolder = state.LocalGames.GetValueOrDefault(id)?.Folder;
                if (string.IsNullOrWhiteSpace(savedFolder)) savedFolder = state.WandGames.GetValueOrDefault(id)?.Folder;
                if (!string.IsNullOrWhiteSpace(savedFolder))
                {
                    var discovered = scan.Games.Where(entry => string.Equals(Path.TrimEndingDirectorySeparator(entry.Folder),
                        Path.TrimEndingDirectorySeparator(savedFolder), StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (discovered.Length == 1 && discovered[0].Launcher is string recovered && File.Exists(recovered))
                    {
                        state.LaunchPaths[id] = recovered;
                        state.InstallationFolders[id] = discovered[0].Folder;
                        changed = true;
                    }
                }
            }
            if (!string.IsNullOrWhiteSpace(state.InstallationFolders.GetValueOrDefault(id))) continue;
            if (!state.LaunchPaths.TryGetValue(id, out var launcher) || !File.Exists(launcher)) continue;
            var folders = scan.Games.Where(entry => string.Equals(entry.Launcher, launcher, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Folder).Concat(scan.ExecutableFolders
                    .Where(entry => string.Equals(entry.Launcher, launcher, StringComparison.OrdinalIgnoreCase)).Select(entry => entry.Folder))
                .Concat(scan.ExplicitCatalogFolders.Where(entry => entry.Id == id &&
                    Path.GetFullPath(launcher).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry.Folder)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    .Select(entry => entry.Folder))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (folders.Length != 1 || !Directory.Exists(folders[0])) continue;
            state.InstallationFolders[id] = folders[0];
            changed = true;
        }
        return changed;
    }
}

public partial class MainWindow
{
    internal bool ApplyInstalledScan(InstalledScanResult result, bool reconcileMissingCatalog = false, bool logNotices = true)
    {
        bool changed = InstalledFolderMapping.RemoveStagingRegistrations(State);
        foreach (var entry in result.Games)
        {
            if (!string.Equals(State.InstallationFolders.GetValueOrDefault(entry.Id), entry.Folder, StringComparison.OrdinalIgnoreCase))
            {
                State.InstallationFolders[entry.Id] = entry.Folder;
                changed = true;
            }
            changed |= InstalledFolderMapping.ApplyLocalDiscovery(State, entry);
            // An explicit launcher choice wins over scanner inference while it still exists.
            if (entry.Launcher != null && (!State.LaunchPaths.TryGetValue(entry.Id, out var previous) || !File.Exists(previous)))
            {
                State.LaunchPaths[entry.Id] = entry.Launcher;
                changed = true;
            }
            if (State.InstalledGames.Add(entry.Id)) changed = true;
        }
        if (reconcileMissingCatalog)
        {
            changed |= InstalledFolderMapping.ReconcileMissing(State, result);
        }
        changed |= InstalledFolderMapping.Apply(State, result);
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
            var found = await Task.Run(() => InstalledScanner.ScanDownloads(destination, snapshot, lifetime.Token, State.InstallationFolders));
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

    internal async Task ScanCompletedGame(Game game, string destination, DateTime? completionStartedAtUtc = null, string? operationId = null, string? expectedInstallationPath = null)
    {
        try
        {
            if (completionStartedAtUtc is DateTime started)
            {
                if (!InstalledScanner.HasFreshCompletionMarker(destination, game.Id, started, game.Name, operationId)) return;
            }
            else if (!InstalledScanner.HasCompletionMarker(destination, game.Id, game.Name)) return;
            IReadOnlyDictionary<string, string>? preferredInstallations = State.InstallationFolders;
            string? requiredOperation = null;
            if (!string.IsNullOrWhiteSpace(expectedInstallationPath))
            {
                // A durable job pins its installation folder to one full digest.
                // Do not let an older saved launch-folder choice redirect its own
                // completion scan to a different installed version.
                preferredInstallations = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [game.Id] = expectedInstallationPath
                };
                requiredOperation = operationId;
            }
            var found = await Task.Run(() => InstalledScanner.ScanDownloads(destination, new[] { (game.Id, game.Name) }, lifetime.Token, preferredInstallations, requiredOperation), lifetime.Token);
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
