using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace GameLibrary.Native;

public partial class MainWindow
{
    private readonly Dictionary<string, (string Folder, InstalledStorageResult Result)> installedStorage = new(StringComparer.Ordinal);
    private readonly InstalledStorageCache storageMeasurements = new();
    private PersistentInstalledStorageCache? persistentStorageMeasurements;
    private bool measuringInstalledStorage;

    private PersistentInstalledStorageCache PersistentStorageMeasurements =>
        persistentStorageMeasurements ??= new PersistentInstalledStorageCache(Path.Combine(Store.Cache, "installed-storage.json"));

    private string InstallationFolder(string id) => State.InstallationFolders.GetValueOrDefault(id)
        ?? State.LocalGames.GetValueOrDefault(id)?.Folder ?? "";

    private void ApplyInstalledStorage()
    {
        foreach (var game in Games)
        {
            game.InstalledStorageLabel = game.Installed ? "Installed files not yet measured" : "";
            game.InstalledStorageDetail = "Scan installed games to identify the installation folder. Download size is separate.";
            if (game.Installed && installedStorage.TryGetValue(game.Id, out var cached)
                && string.Equals(cached.Folder, InstallationFolder(game.Id), StringComparison.OrdinalIgnoreCase))
            {
                var result = cached.Result;
                game.InstalledStorageLabel = $"{(result.Complete ? "" : "At least ")}{result.Bytes / 1_000_000_000d:0.###} GB installed files";
                game.InstalledStorageDetail = $"{cached.Folder}\nMeasured {result.MeasuredUtc.ToLocalTime():g}; {result.FileCount:N0} files. Logical file lengths; allocated disk space can differ. Files can change during measurement."
                    + (result.Complete ? "" : $"\nIncomplete: {result.Reason}; {result.SkippedCount:N0} skipped.");
            }
            game.Notify(nameof(Game.InstalledStorageLabel));
            game.Notify(nameof(Game.InstalledStorageDetail));
        }
    }

    private async Task RefreshInstalledStorage()
    {
        if (measuringInstalledStorage || closing) return;
        measuringInstalledStorage = true;
        try
        {
            foreach (var game in Games.Where(g => g.Installed).ToArray())
            {
                if (closing) break;
                string folder = InstallationFolder(game.Id);
                if (string.IsNullOrWhiteSpace(folder)) continue;
                var result = storageMeasurements.Fresh(folder, DateTime.UtcNow);
                if (result == null)
                {
                    try
                    {
                        result = PersistentStorageMeasurements.Fresh(game.Id, folder, DateTime.UtcNow);
                        if (result != null) storageMeasurements.Record(folder, result, storageMeasurements.Budget(folder));
                    }
                    catch (Exception ex) { Store.Log("Persistent installed-storage cache read unavailable: " + ex.Message); }
                }
                if (result != null && installedStorage.TryGetValue(game.Id, out var previous) &&
                    string.Equals(previous.Folder, folder, StringComparison.OrdinalIgnoreCase) && ReferenceEquals(previous.Result, result)) continue;
                if (result == null)
                {
                    var budget = storageMeasurements.Budget(folder);
                    result = await InstalledStorage.MeasureAsync(folder, lifetime.Token, new InstalledStorageOptions { MaxDuration = budget });
                    if (!closing) storageMeasurements.Record(folder, result, budget);
                }
                if (closing) break;
                if (string.Equals(folder, InstallationFolder(game.Id), StringComparison.OrdinalIgnoreCase))
                {
                    if (result.Complete)
                    {
                        try { PersistentStorageMeasurements.RecordComplete(game.Id, folder, result); }
                        catch (Exception ex) { Store.Log("Persistent installed-storage cache write unavailable: " + ex.Message); }
                    }
                    installedStorage[game.Id] = (folder, result);
                }
                ApplyInstalledStorage();
            }
        }
        catch (Exception ex) { Store.Log("Installed storage measurement unavailable: " + ex.Message); }
        finally { measuringInstalledStorage = false; }
    }
}
