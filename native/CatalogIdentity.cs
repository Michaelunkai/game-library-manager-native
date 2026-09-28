using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GameLibrary.Native;

internal static class CatalogIdentity
{
    // Verified Steam identities repair cross-title substitutions in the bundled
    // catalog. Installed titles are corroborated by local Steam IDs/app.info;
    // evidence/criteria-r51-installed-identities.json and
    // evidence/criteria-r55-installed-identities.json record those sources.
    internal static (string Title, int SteamId)? Known(string id) => id switch
    {
        "legacyofkaindefianceremastered" => ("Legacy of Kain: Defiance Remastered", 3747730),
        "legacyofkainsr12r" => ("Legacy of Kain Soul Reaver 1&2 Remastered", 2521380),
        "DuneImperium" => ("Dune: Imperium", 1689500),
        "planetoflana" => ("Planet of Lana", 1608230),
        "ghostrick" => ("Ghost Trick: Phantom Detective", 1967430),
        "highlandsong" => ("A Highland Song", 1240060),
        "miandthedragonprincess" => ("Mia and the Dragon Princess", 1837580),
        "legendoftianding" => ("The Legend of Tianding", 1406850),
        "theelderscrollsivoblivionremastered" => ("The Elder Scrolls IV: Oblivion Remastered", 2623190),
        "circuselectricque" => ("Circus Electrique", 1666250),
        "theradstringclub" => ("The Red Strings Club", 589780),
        _ => null
    };

    internal static bool Correct(Game game)
    {
        var known = Known(game.Id);
        if (known == null || MetadataClient.SameTitle(game.Name, known.Value.Title)) return false;
        game.Name = known.Value.Title;
        game.Time = 0; game.Image = ""; game.Cover = "";
        return true;
    }

    internal static bool AcceptsMetadata(string id, JsonObject metadata)
    {
        var known = Known(id);
        if (known == null) return true;
        string title = DataJson.Text(metadata["matchedTitle"]);
        double appId = DataJson.Number(metadata["steamAppId"]);
        if (appId > 0 && appId != known.Value.SteamId) return false;
        if (title.Length > 0 && !MetadataClient.SameTitle(known.Value.Title, title)) return false;
        return appId == known.Value.SteamId || title.Length > 0;
    }

    internal static bool RepairLaunchMappings(UserState state)
    {
        bool changed = false;
        foreach (string id in new[] { "legacyofkaindefianceremastered", "legacyofkainsr12r" })
        {
            if (!state.LaunchPaths.TryGetValue(id, out var executable) || !File.Exists(executable)) continue;
            string folder = Path.GetDirectoryName(executable)!;
            string config = Path.Combine(folder, "steam_emu.ini");
            try
            {
                if (!File.Exists(config) || new FileInfo(config).Length > 256 * 1024) continue;
                var matches = Regex.Matches(File.ReadAllText(config), @"^\s*AppId\s*=\s*(\d+)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                if (matches.Count != 1 || !int.TryParse(matches[0].Groups[1].Value, out int actual)) continue;
                // Only the positively identified cross-title conflict is migrated.
                // Missing, unfamiliar or ambiguous source data preserves the user's mapping.
                if (actual is not (2521380 or 3747730) || actual == Known(id)!.Value.SteamId) continue;
                state.LaunchPaths.Remove(id); state.InstallationFolders.Remove(id); state.InstalledGames.Remove(id);
                changed = true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return changed;
    }
}
