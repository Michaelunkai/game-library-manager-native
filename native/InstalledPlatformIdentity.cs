using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GameLibrary.Native;

internal sealed record InstalledPlatformIdentity(int SteamAppId, string EvidencePath)
{
    private static readonly string[] IdentityNames = { "steam_appid.txt", "steam_emu.ini", "flt.ini", "steam_api64.ini", "tenoke.ini" };

    private static string[] IniValues(string name, string text)
    {
        if (name != "tenoke.ini")
            return Regex.Matches(text, @"(?im)^\s*AppId\s*=\s*([^\r\n]*)").Select(m => m.Groups[1].Value.Split(';', '#')[0].Trim()).ToArray();
        var values = new List<string>(); bool inTenoke = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Split(';', '#')[0].Trim();
            if (line.StartsWith('['))
            {
                inTenoke = line.Equals("[TENOKE]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            var match = Regex.Match(line, @"\Aid\s*=\s*(.*)\z", RegexOptions.IgnoreCase);
            if (inTenoke && match.Success) values.Add(match.Groups[1].Value.Trim());
        }
        return values.ToArray();
    }

    private static bool Regular(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }

    internal static InstalledPlatformIdentity? Resolve(string folder, string? executable)
        => Resolve(folder, executable, out _);

    internal static InstalledPlatformIdentity? Resolve(string folder, string? executable, out bool rejected)
    {
        rejected = false;
        try
        {
            if (!Path.IsPathFullyQualified(folder) || string.IsNullOrWhiteSpace(executable) ||
                !Path.IsPathFullyQualified(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(folder) || !File.Exists(executable)) return null;
            rejected = true;
            if (folder.Split('/', '\\').Any(s => s is "." or "..") || executable.Split('/', '\\').Any(s => s is "." or "..")) return null;
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), exe = Path.GetFullPath(executable);
            if (!exe.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Regular(exe) || !Regular(root)) return null;
            // Only the selected executable's ancestry and its own Unity plugin
            // directories are candidates. Sibling games/artbooks are not searched.
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
            string? current = Path.GetDirectoryName(exe);
            for (int depth = 0; current != null && !current.Equals(root, StringComparison.OrdinalIgnoreCase); depth++)
            {
                if (depth >= 8) return null;
                directories.Add(current); current = Path.GetDirectoryName(current);
            }
            string unity = Path.Combine(Path.GetDirectoryName(exe)!, Path.GetFileNameWithoutExtension(exe) + "_Data", "Plugins");
            foreach (string candidate in new[] { unity, Path.Combine(unity, "x86"), Path.Combine(unity, "x86_64") })
                if (Directory.Exists(candidate)) directories.Add(candidate);
            string unreal = Path.Combine(root, "Engine", "Binaries", "ThirdParty", "Steamworks");
            if (Directory.Exists(unreal))
            {
                if (!Regular(unreal)) return null;
                var versions = Directory.EnumerateDirectories(unreal, "Steamv*", SearchOption.TopDirectoryOnly).Take(9).ToArray();
                if (versions.Length > 8) return null;
                foreach (string version in versions)
                {
                    if (!Regex.IsMatch(Path.GetFileName(version), @"\ASteamv[0-9]+\z", RegexOptions.IgnoreCase) || !Regular(version)) return null;
                    string candidate = Path.Combine(version, "Win64");
                    if (Directory.Exists(candidate)) directories.Add(candidate);
                }
            }
            var ids = new HashSet<int>(); string evidence = ""; int count = 0;
            foreach (string directory in directories)
            {
                if (!Regular(directory)) return null;
                foreach (string name in IdentityNames)
                {
                    string path = Path.Combine(directory, name);
                    if (!File.Exists(path)) continue;
                    if (++count > 32 || !Regular(path)) return null;
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    // TENOKE bundles localized achievement descriptions; read
                    // the whole bounded file so a late conflicting ID is seen.
                    byte[] bytes = new byte[(name == "tenoke.ini" ? 262144 : 65536) + 1]; int used = 0, read;
                    while (used < bytes.Length && (read = stream.Read(bytes, used, bytes.Length - used)) > 0) used += read;
                    if (used == bytes.Length) return null;
                    // Emulator INIs commonly contain legacy box-art banners.
                    // Decode those bytes one-to-one; only ASCII AppId digits
                    // establish identity. BOM-marked Unicode stays supported.
                    string text = used >= 2 && bytes[0] == 255 && bytes[1] == 254
                        ? new UnicodeEncoding(false, true, true).GetString(bytes, 2, used - 2)
                        : used >= 2 && bytes[0] == 254 && bytes[1] == 255
                        ? new UnicodeEncoding(true, true, true).GetString(bytes, 2, used - 2)
                        : name == "steam_appid.txt" ? new UTF8Encoding(false, true).GetString(bytes, 0, used).TrimStart('\uFEFF')
                        : Encoding.Latin1.GetString(bytes, used >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191 ? 3 : 0,
                            used - (used >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191 ? 3 : 0));
                    string[] values = name == "steam_appid.txt" ? new[] { text.Trim() } :
                        IniValues(name, text);
                    if (values.Length == 0) return null;
                    foreach (string value in values)
                    {
                        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int appId) || appId <= 0) return null;
                        ids.Add(appId);
                    }
                    evidence = path;
                    if (ids.Count > 1) return null;
                }
            }
            rejected = false;
            return ids.Count == 1 ? new(ids.Single(), evidence) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    internal static bool Accepts(Game game, JsonObject metadata)
    {
        if (game.MetadataIdentityConflict) return false;
        if (game.MetadataSteamAppId <= 0) return game.MetadataLookupTitle.Length == 0 ||
            MetadataClient.SameTitle(game.MetadataLookupTitle, DataJson.Text(metadata["matchedTitle"]));
        // Existing catalog metadata with independently matching title remains
        // usable while the newly discovered ID is verified. It cannot establish
        // a canonical title or replace explicit contradictory platform evidence.
        if (DataJson.Number(metadata["steamAppId"]) == 0)
            return MetadataClient.SameTitle(MetadataClient.ExpectedTitle(game), DataJson.Text(metadata["matchedTitle"]));
        return DataJson.Number(metadata["steamAppId"]) == game.MetadataSteamAppId &&
            (DataJson.Text(metadata["source"]?["identity"]) == "steam-direct" || DataJson.Text(metadata["source"]?["image"]) == "steam-direct") &&
            ValidTitle(DataJson.Text(metadata["matchedTitle"]));
    }

    internal static bool ValidTitle(string title) => !string.IsNullOrWhiteSpace(title) && title.Length <= 300 && !title.Any(char.IsControl);
}
