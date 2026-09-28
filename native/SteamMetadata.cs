using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

internal static class SteamMetadata
{
    internal static string ProductDetailsUrl(int appId) =>
        "https://store.steampowered.com/api/appdetails?cc=us&l=en&appids=" + appId.ToString(CultureInfo.InvariantCulture);

    internal static int ReleaseYear(JsonNode? release)
    {
        if (release?["coming_soon"] is not JsonValue soon || !soon.TryGetValue<bool>(out bool comingSoon) || comingSoon) return 0;
        if (!DateTime.TryParseExact(DataJson.Text(release["date"]), new[] { "d MMM, yyyy", "MMM d, yyyy", "d MMM yyyy" },
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date.Year < 1970 || date.Date > DateTime.UtcNow.Date) return 0;
        return date.Year;
    }
    internal static double StorageGb(string html)
    {
        string text = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " "));
        var match = Regex.Match(text, @"\bStorage\s*:\s*(\d+(?:\.\d+)?)\s*(GB|MB|TB)\s+available\s+space", RegexOptions.IgnoreCase);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double size)) return 0;
        size *= match.Groups[2].Value.ToUpperInvariant() switch { "MB" => .001, "TB" => 1000, _ => 1 };
        return size > 0 && size < 100000 ? size : 0;
    }

    internal static int WandSteamId(JsonObject catalog, Game game, string title)
    {
        if (!game.Id.StartsWith("wand:", StringComparison.Ordinal)) return 0;
        string key = game.Id[5..];
        if (catalog["games"]?[key] is not JsonObject entry || DataJson.Text(entry["id"]) != key)
            throw new FormatException("Wand's exact game identity is unavailable.");
        string titleId = DataJson.Text(entry["titleId"]);
        if (catalog["titles"]?[titleId] is not JsonObject target ||
            DataJson.Text(target["id"]) != titleId || !MetadataClient.SameTitle(title, DataJson.Text(target["name"])))
            throw new FormatException("Wand's game identity does not match the requested title.");
        if (DataJson.Text(entry["platformId"]) != "steam") return 0;
        var identities = (entry["correlationIds"] as JsonArray ?? new()).Select(node => DataJson.Text(node))
            .Where(value => value.StartsWith("steam:", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        if (identities.Length != 1 || !int.TryParse(identities[0][6..], NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id <= 0)
            throw new FormatException("Wand did not provide one valid Steam app identity.");
        return id;
    }

    internal static async Task<JsonObject> Read(HttpClient http, Game game, string title, CancellationToken cancellation, LibraryStore? store = null)
    {
        int id = game.MetadataSteamAppId;
        if (id == 0 && store != null && game.Id.StartsWith("wand:", StringComparison.Ordinal))
            id = WandSteamId(await WandIntegration.LoadCatalogAsync(store, cancellation), game, title);
        if (id == 0)
        {
        string query = Regex.Replace(title.Replace("'", "").Replace("\u2019", ""), @"[^\p{L}\p{Nd}+#]+", " ").Trim();
        var search = JsonNode.Parse(await http.GetStringAsync("https://store.steampowered.com/api/storesearch/?cc=us&l=en&term=" + Uri.EscapeDataString(query), cancellation));
        var matches = (search?["items"] as JsonArray ?? new()).OfType<JsonObject>()
            .Where(item => MetadataClient.SameTitle(title, DataJson.Text(item["name"]))).ToArray();
        if (matches.Length != 1) throw new FormatException("Steam did not return one unambiguous exact game title.");
        id = matches[0]["id"]?.GetValue<int>() ?? 0;
        if (id <= 0) throw new FormatException("Steam returned an invalid app identity.");
        }
        var response = JsonNode.Parse(await http.GetStringAsync(ProductDetailsUrl(id), cancellation))?[id.ToString(CultureInfo.InvariantCulture)];
        var data = response?["data"];
        if (response?["success"]?.GetValue<bool>() != true || data?["steam_appid"]?.GetValue<int>() != id ||
            (game.MetadataSteamAppId == 0 && !MetadataClient.SameTitle(title, DataJson.Text(data?["name"]))) ||
            !InstalledPlatformIdentity.ValidTitle(DataJson.Text(data?["name"])) || DataJson.Text(data?["type"]) != "game")
            throw new FormatException("Steam app details did not confirm the requested game.");
        var requirements = data?["pc_requirements"] as JsonObject;
        double minimum = StorageGb(DataJson.Text(requirements?["minimum"]));
        double recommended = StorageGb(DataJson.Text(requirements?["recommended"]));
        return new JsonObject { ["success"] = true, ["id"] = game.Id, ["name"] = DataJson.Text(data?["name"]),
            ["category"] = game.Category, ["image"] = DataJson.Text(data?["header_image"]),
            ["diskRequirementGb"] = Math.Max(minimum, recommended), ["steamAppId"] = id,
            ["steamReleaseYear"] = ReleaseYear(data?["release_date"]),
            ["source"] = new JsonObject { ["identity"] = "steam-direct", ["releaseYear"] = "steam-direct", ["image"] = "steam-direct", ["diskRequirement"] = "steam-published-requirement" } };
    }
}
