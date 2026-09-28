using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace GameLibrary.Native;
internal static class LocalScanNameTests
{
    internal static void Run(string root)
    {
        var state = new UserState();
        string folder = Path.Combine(root, "folder-label"), id = LocalGame.Identity(folder);
        var entry = new InstalledDiscovery(id, "folder-label", folder, null, true);
        var checks = new List<(string Name, bool Passed)>();
        checks.Add(("First scan registers the local game", InstalledFolderMapping.ApplyLocalDiscovery(state, entry) && state.LocalGames[id].Name == "folder-label"));
        state.LocalGames[id].Name = "My permanent title"; state.LocalGames[id].Category = "favorites";
        var added = state.LocalGames[id].AddedUtc;
        checks.Add(("Repeat scan preserves the custom title without unnecessary writes", !InstalledFolderMapping.ApplyLocalDiscovery(state, entry) && state.LocalGames[id].Name == "My permanent title"));
        var store = new LibraryStore(Path.Combine(root, "scan-name-profile")); store.Save(state); state = store.LoadState();
        checks.Add(("A later scan after restart preserves the custom title", !InstalledFolderMapping.ApplyLocalDiscovery(state, entry) && state.LocalGames[id].Name == "My permanent title"));
        checks.Add(("Personal category and added date survive scans", state.LocalGames[id].Category == "favorites" && state.LocalGames[id].AddedUtc == added));
        state.LocalGames[id].Name = "folder-label";
        var moved = entry with { Folder = Path.Combine(root, "new-folder"), Name = "new-folder" };
        checks.Add(("Default folder label follows a changed folder", InstalledFolderMapping.ApplyLocalDiscovery(state, moved) && state.LocalGames[id].Name == "new-folder"));
        LibraryStore.AtomicWrite(Path.Combine(root, "scan-name-proof.json"), DataJson.Write(checks.Select(c => new { name = c.Name, passed = c.Passed }).ToArray()));
        if (checks.Any(c => !c.Passed)) throw new InvalidOperationException(string.Join("; ", checks.Where(c => !c.Passed).Select(c => c.Name)));
    }
}
