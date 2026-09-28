using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace GameLibrary.Native;

/// <summary>
/// Keep a saved, playable installation visible when its Docker or Wand source
/// registration is absent from the latest catalog. Source IDs and personal
/// data stay in the existing profile; this only supplies a display record.
/// </summary>
internal static class InstalledOrphanSources
{
    internal static int Add(List<Game> games, UserState state, JsonObject config)
    {
        var present = games.Select(game => game.Id).ToHashSet(StringComparer.Ordinal);
        int added = 0;
        foreach (string id in state.InstalledGames.OrderBy(value => value, StringComparer.Ordinal))
        {
            if (present.Contains(id) || !state.LaunchPaths.TryGetValue(id, out string? launchPath)
                || string.IsNullOrWhiteSpace(launchPath)) continue;
            string fullPath;
            try { fullPath = Path.GetFullPath(launchPath); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (!Path.IsPathFullyQualified(fullPath) || !File.Exists(fullPath)) continue;

            string folder = state.InstallationFolders.GetValueOrDefault(id) ?? "";
            if (folder.Length == 0) folder = Path.GetDirectoryName(fullPath) ?? "";
            string folderName = folder.Length == 0 ? "" : Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
            string title = id.StartsWith("wand:", StringComparison.Ordinal) || id.Equals("Win64", StringComparison.OrdinalIgnoreCase)
                ? folderName : id;
            if (string.IsNullOrWhiteSpace(title)) title = id;
            string category = DataJson.Text(state.LocalCatalog["gameCategories"]?[id],
                DataJson.Text(config["gameCategories"]?[id], "installed"));
            games.Add(new Game
            {
                Id = id,
                Name = title,
                Category = category,
                IsLocal = true,
                Description = "Saved installed game · source registration is unavailable · " + folder,
                Details = "The original source ID, launch path, and personal history are retained."
            });
            present.Add(id);
            added++;
        }
        return added;
    }
}
