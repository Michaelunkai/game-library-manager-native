using System;
using System.IO;
using System.Text.Json.Nodes;

namespace GameLibrary.Native;

internal static class CatalogIdentityTests
{
    internal static void Run(string root)
    {
        static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        const string defiance = "legacyofkaindefianceremastered", soul = "legacyofkainsr12r";
        var wrong = new Game { Id = defiance, Name = "Legacy of Kain Soul Reaver 1&2 Remastered", Time = 10, Image = "wrong.jpg", Category = "finished" };
        Check(CatalogIdentity.Correct(wrong) && wrong.Name == "Legacy of Kain: Defiance Remastered" && wrong.Time == 0 && wrong.Image == "" && wrong.Category == "finished", "Cross-title catalog data was retained or category changed.");
        Check(!MetadataClient.MatchesGame(wrong, new JsonObject { ["id"] = defiance, ["name"] = "Legacy of Kain Soul Reaver 1&2 Remastered" }), "Cross-title provider data accepted.");
        Check(!CatalogIdentity.AcceptsMetadata(defiance, new JsonObject { ["time"] = 10, ["cover"] = "wrong.jpg" }), "Unattributed old cache accepted.");
        Check(CatalogIdentity.AcceptsMetadata(defiance, new JsonObject { ["matchedTitle"] = wrong.Name, ["time"] = 14 }), "Validated title cache rejected.");
        Check(!CatalogIdentity.AcceptsMetadata(defiance, new JsonObject { ["matchedTitle"] = wrong.Name, ["steamAppId"] = 2521380 }), "Contradictory cached app identity accepted.");
        string fixture = Path.Combine(root, "catalog-identity-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        string exe = Path.Combine(fixture, "SRX.exe"); File.WriteAllBytes(exe, new byte[] { 77, 90 });
        File.WriteAllText(Path.Combine(fixture, "steam_emu.ini"), "[Steam]\r\nAppId=2521380\r\n");
        var state = new UserState();
        state.LaunchPaths[defiance] = exe; state.LaunchPaths[soul] = exe;
        state.InstallationFolders[defiance] = fixture; state.InstallationFolders[soul] = fixture;
        state.InstalledGames.UnionWith(new[] { defiance, soul }); state.Ratings[defiance] = 4; state.Wishlist.Add(defiance);
        var store = new LibraryStore(Path.Combine(fixture, "profile")); store.Save(state);
        var restored = store.LoadState();
        Check(!restored.InstalledGames.Contains(defiance) && !restored.LaunchPaths.ContainsKey(defiance) && !restored.InstallationFolders.ContainsKey(defiance), "Confirmed wrong launcher survived state migration.");
        Check(restored.InstalledGames.Contains(soul) && restored.LaunchPaths[soul] == exe && restored.Ratings[defiance] == 4 && restored.Wishlist.Contains(defiance), "Correct launcher or personal edits changed.");
        Check(!store.LoadState().LaunchPaths.ContainsKey(defiance), "Mapping repair did not persist.");
        var registration = new WandSupportedGame("legacyofkainsr12r", "proof", "proof", "Legacy of Kain Soul Reaver 1&2 Remastered", exe);
        Check(MainWindow.WandLibraryMatchScore(wrong, registration, exe) == 0, "Wand restored wrong-title mapping through path equality.");
        var correct = new Game { Id = soul, Name = registration.Name };
        Check(MainWindow.WandLibraryMatchScore(correct, registration, exe) > 0, "Correct Wand association rejected.");
        restored.LaunchPaths[defiance] = exe; restored.InstalledGames.Add(defiance);
        File.WriteAllText(Path.Combine(fixture, "steam_emu.ini"), "AppId=123456\n");
        Check(!CatalogIdentity.RepairLaunchMappings(restored) && restored.LaunchPaths.ContainsKey(defiance), "Unfamiliar source guessed as another game.");
        File.WriteAllText(Path.Combine(fixture, "steam_emu.ini"), "AppId=2521380\nAppId=3747730\n");
        Check(!CatalogIdentity.RepairLaunchMappings(restored), "Ambiguous source changed user mapping.");
    }
}
