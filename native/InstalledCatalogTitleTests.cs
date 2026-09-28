using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace GameLibrary.Native;

internal static class InstalledCatalogTitleTests
{
    internal static void Run(string root)
    {
        var checks = new List<(string Name, bool Passed)>();
        foreach (var (id, oldTitle, title, appId) in new[] {
            ("DuneImperium", "Little Army", "Dune: Imperium", 1689500),
            ("planetoflana", "Planet of Lana II", "Planet of Lana", 1608230),
            ("ghostrick", "GhosTrick-The Sacred War of Light vs Shadow", "Ghost Trick: Phantom Detective", 1967430),
            ("highlandsong", "Highlandsong", "A Highland Song", 1240060),
            ("miandthedragonprincess", "Miandthedragonprincess", "Mia and the Dragon Princess", 1837580),
            ("legendoftianding", "Legendoftianding", "The Legend of Tianding", 1406850),
            ("theelderscrollsivoblivionremastered", "Theelderscrollsivoblivionremastered", "The Elder Scrolls IV: Oblivion Remastered", 2623190),
            ("circuselectricque", "Circuselectricque", "Circus Electrique", 1666250),
            ("theradstringclub", "Theradstringclub", "The Red Strings Club", 589780) })
        {
            var game = new Game { Id = id, Name = oldTitle, Time = 12, Image = "wrong.jpg", Cover = "wrong.jpg", Category = "action", Rating = 4 };
            checks.Add((id + " corrects identity and discards unrelated artwork/hours", CatalogIdentity.Correct(game) && game.Name == title && game.Time == 0 && game.Image == "" && game.Cover == ""));
            checks.Add((id + " retains personal fields", game.Id == id && game.Category == "action" && game.Rating == 4));
            checks.Add((id + " rejects old-title cache", !CatalogIdentity.AcceptsMetadata(id, new JsonObject { ["matchedTitle"] = oldTitle, ["time"] = 12 })));
            checks.Add((id + " accepts corroborated title and app ID", CatalogIdentity.AcceptsMetadata(id, new JsonObject { ["matchedTitle"] = title, ["steamAppId"] = appId })));
            checks.Add((id + " rejects contradictory app ID", !CatalogIdentity.AcceptsMetadata(id, new JsonObject { ["matchedTitle"] = title, ["steamAppId"] = 1 })));
            var attempts = new JsonObject { [id] = new JsonObject { ["queryTitle"] = oldTitle, ["retryAfter"] = DateTime.UtcNow.AddDays(1).ToString("O") } };
            checks.Add((id + " retries corrected query without stale cooldown", MetadataClient.ExpectedTitle(game) == title && MainWindow.MetadataDue(game, attempts, DateTime.UtcNow)));
            var state = new UserState(); state.LaunchPaths[id] = @"E:\fixture\game.exe"; state.InstalledGames.Add(id);
            checks.Add((id + " retains launcher registration", !CatalogIdentity.RepairLaunchMappings(state) && state.LaunchPaths[id] == @"E:\fixture\game.exe" && state.InstalledGames.Contains(id)));
        }
        LibraryStore.AtomicWrite(Path.Combine(root, "installed-title-proof.json"), DataJson.Write(checks.Select(c => new { name = c.Name, passed = c.Passed }).ToArray()));
        if (checks.Any(c => !c.Passed)) throw new InvalidOperationException(string.Join("; ", checks.Where(c => !c.Passed).Select(c => c.Name)));
    }
}
