using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GameLibrary.Native;

internal static class GogInstalledIdentity
{
    private static bool RegularChild(string root, string path)
    {
        string full = Path.GetFullPath(path), prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return false;
        string current = Path.TrimEndingDirectorySeparator(root);
        foreach (string part in Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        }
        return true;
    }

    internal static string Title(string folder, string? executable)
    {
        try
        {
            if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder) ||
                (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0 || string.IsNullOrWhiteSpace(executable) ||
                !Path.IsPathFullyQualified(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                !RegularChild(folder, executable)) return "";
            var files = Directory.EnumerateFiles(folder, "goggame-*.info", SearchOption.TopDirectoryOnly).Take(33).ToArray();
            if (files.Length > 32) return "";
            var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                var identity = Regex.Match(Path.GetFileName(file), @"\Agoggame-([1-9][0-9]*)\.info\z", RegexOptions.IgnoreCase);
                if (!identity.Success || !RegularChild(folder, file)) return "";
                // Bound the actual read, including a file that grows after opening.
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                byte[] bytes = new byte[65537]; int used = 0, count;
                while (used < bytes.Length && (count = stream.Read(bytes, used, bytes.Length - used)) > 0) used += count;
                if (used > 65536) return "";
                string json = Encoding.UTF8.GetString(bytes, 0, used).TrimStart('\uFEFF');
                if (JsonNode.Parse(json) is not JsonObject data || DataJson.Text(data["gameId"]) != identity.Groups[1].Value) return "";
                string title = DataJson.Text(data["name"]).Trim();
                if (title.Length is 0 or > 200 || title.Any(char.IsControl)) return "";
                var tasks = (data["playTasks"] as JsonArray ?? new()).OfType<JsonObject>().ToArray();
                bool Flag(JsonObject task, string name) => task[name] is JsonValue value && value.TryGetValue<bool>(out bool flag) && flag;
                string Target(JsonObject task)
                {
                    if (DataJson.Text(task["type"]) != "FileTask") return "";
                    string relative = DataJson.Text(task["path"]);
                    if (!relative.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(relative) || relative.Contains(':') ||
                        relative.Split('/', '\\').Any(part => part is "." or "..")) return "";
                    string target = Path.GetFullPath(Path.Combine(folder, relative));
                    return RegularChild(folder, target) ? target : "";
                }
                bool validPrimary = tasks.Any(task => Flag(task, "isPrimary") &&
                    DataJson.Text(task["category"]) is "launcher" or "game" && Target(task).Length > 0);
                foreach (var task in tasks)
                {
                    if (DataJson.Text(task["category"]) != "game" ||
                        !(Flag(task, "isPrimary") || (validPrimary && Flag(task, "isHidden")))) continue;
                    if (string.Equals(Target(task), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)) titles.Add(title);
                }
            }
            return titles.Count == 1 ? titles.Single() : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException or NotSupportedException)
        { return ""; }
    }
}
