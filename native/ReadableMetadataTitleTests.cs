using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace GameLibrary.Native;

internal static class ReadableMetadataTitleTests
{
    internal static void Run(string root)
    {
        var checks = new List<(string Name, bool Passed)>();
        foreach (string title in new[] { "The Legend of Tianding", "Blade Chimera", "Bo: Path of the Teal Lotus", "New Super Lucky's Tale",
            "Kaze and the Wild Masks", "The Eternal Castle Remastered", "Boxes: Lost Fragments", "Dungeons of Hinterberg",
            "Degrees of Separation", "Greak: Memories of Azur", "Lorelei and the Laser Eyes", "Lara Croft and the Temple of Osiris",
            "Little Kitty, Big City", "Children of the Sun", "Tails: The Backbone Preludes" })
        {
            string joined = new string(title.Where(char.IsLetterOrDigit).ToArray());
            var game = new Game { Id = "test-" + joined, Name = joined, Category = "action", Rating = 4 };
            checks.Add((title + " has readable lookup", MetadataClient.ExpectedTitle(game) == title));
            checks.Add((title + " keeps readable duration query over old current/cache names", MetadataClient.DurationQueryTitle(title, joined, joined, null) == title));
            var wand = new JsonObject { ["titles"] = new JsonObject { ["fixture"] = new JsonObject { ["name"] = joined } } };
            checks.Add((title + " keeps readable query over joined Wand title", MetadataClient.DurationQueryTitle(title, joined, joined, wand) == title));
            var attempts = new JsonObject { [game.Id] = new JsonObject { ["queryTitle"] = joined, ["retryAfter"] = DateTime.UtcNow.AddDays(1).ToString("O") } };
            checks.Add((title + " retries old joined query", MainWindow.MetadataDue(game, attempts, DateTime.UtcNow)));
            attempts[game.Id]!["queryTitle"] = title;
            checks.Add((title + " honors new-query backoff", !MainWindow.MetadataDue(game, attempts, DateTime.UtcNow)));
            checks.Add((title + " does not mutate personal/catalog fields", game.Name == joined && game.Category == "action" && game.Rating == 4));
        }
        var explicitLocal = new Game { Id = "local", IsLocal = true, Name = "BladeChimera", MetadataLookupTitle = "BladeChimera" };
        checks.Add(("Explicit local lookup override remains exact", MetadataClient.ExpectedTitle(explicitLocal) == "BladeChimera"));
        foreach (string protectedTitle in new[] { "Dragonquestviireimagined", "Theelderscrollsivoblivionremastered", "Highlandsong", "Theradstringclub" })
            checks.Add((protectedTitle + " is not guessed into a different spelling or numeral identity", MetadataClient.ExpectedTitle(new Game { Name = protectedTitle }) == protectedTitle));
        checks.Add(("Sequels still fail title matching", !MetadataClient.SameTitle("Blade Chimera", "Blade Chimera 2")));
        LibraryStore.AtomicWrite(Path.Combine(root, "readable-metadata-title-proof.json"), DataJson.Write(checks.Select(c => new { name = c.Name, passed = c.Passed }).ToArray()));
        if (checks.Any(c => !c.Passed)) throw new InvalidOperationException(string.Join("; ", checks.Where(c => !c.Passed).Select(c => c.Name)));
    }
}
