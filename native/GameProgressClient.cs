using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

internal sealed record GameProgressSnapshot(string Game, string Executable, string Label, string Detail);

internal static class GameProgressClient
{
    internal const string Project = @"F:\study\projects\games\tools\gameprogress";
    internal const string Backups = @"F:\backup\gamesaves";
    internal static async Task<IReadOnlyList<GameProgressSnapshot>> ReadAsync(CancellationToken cancellation)
    {
        string python = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Python\pythoncore-3.14-64\python.exe");
        if (!File.Exists(python)) throw new FileNotFoundException("Game progress Python runtime was not found.", python);
        var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Project };
        start.Environment["PYTHONPATH"] = Path.Combine(Project, "src");
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.StandardOutputEncoding = System.Text.Encoding.UTF8;
        foreach (string arg in new[] { "-m", "gameprogress", "--json", "--schema-version", "3", "--no-network" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Game progress could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellation);
        var stderr = process.StandardError.ReadToEndAsync(cancellation);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        string error = await stderr;
        if (process.ExitCode != 0) throw new IOException("Game progress is unavailable: " + error.Trim());
        return Parse(await stdout, Backups);
    }

    internal static IReadOnlyList<GameProgressSnapshot> Parse(string json, string backupRoot)
    {
        var rows = JsonNode.Parse(json) as JsonArray ?? throw new FormatException("Invalid game progress response.");
        var results = new List<GameProgressSnapshot>();
        string prefix = Path.GetFullPath(backupRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var row in rows.OfType<JsonObject>())
        {
            int schema = (int)DataJson.Number(row["schema_version"]);
            if ((schema != 2 && schema != 3) || row["error"] != null) continue;
            string name = DataJson.Text(row["game"]), path = DataJson.Text(row["backup"]?["path"]);
            if (string.IsNullOrWhiteSpace(name) || !Path.IsPathFullyQualified(path)) continue;
            path = Path.GetFullPath(path);
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            string executable = "";
            try
            {
                var audit = JsonNode.Parse(File.ReadAllText(Path.Combine(path, "backup.json")));
                executable = DataJson.Text(audit?["executable"]);
                if (!Path.IsPathFullyQualified(executable)) executable = "";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { continue; }
            if (schema == 3)
            {
                string main = FormatScope(row["scopes"]?["main_story"], "Main story");
                string overall = FormatScope(row["scopes"]?["overall_completion"], "Overall");
                string date3 = DateTimeOffset.TryParse(DataJson.Text(row["created_utc"]), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var created3)
                    ? created3.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                    : "creation time not recorded";
                string detail3 = "Latest verified backup: " + date3 + "\nMain story: " + ScopeDetail(row["scopes"]?["main_story"])
                    + "\nOverall: " + ScopeDetail(row["scopes"]?["overall_completion"]) + "\n" + path;
                results.Add(new GameProgressSnapshot(name, executable, main + " · " + overall, detail3));
                continue;
            }
            JsonNode? fractionNode = row["progress"]?["frac"], leftNode = row["styles"]?["main"]?["left_avg"];
            bool hasFraction = fractionNode is JsonValue fv && fv.TryGetValue<double>(out var f) && double.IsFinite(f) && f >= 0 && f <= 1;
            double fraction = hasFraction ? DataJson.Number(fractionNode) : 0;
            bool hasLeft = leftNode is JsonValue lv && lv.TryGetValue<double>(out var l) && double.IsFinite(l) && l >= 0;
            double left = hasLeft ? DataJson.Number(leftNode) : 0;
            string progress = hasFraction
                ? "~" + (100 * fraction).ToString("0.000", CultureInfo.InvariantCulture) + "% estimated"
                : "No verified campaign percentage";
            string remaining = hasFraction && hasLeft
                ? "~" + left.ToString("0.0", CultureInfo.InvariantCulture) + " h remaining"
                : hasFraction ? "No sourced remaining-time estimate" : "Remaining hours need campaign data";
            string date = DateTimeOffset.TryParse(DataJson.Text(row["created_utc"]), CultureInfo.InvariantCulture, DateTimeStyles.None, out var created)
                ? created.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "creation time not recorded";
            string label = progress + " · " + remaining;
            string source = DataJson.Text(row["progress"]?["source"]);
            if (string.IsNullOrWhiteSpace(source) || source.Equals("unknown", StringComparison.OrdinalIgnoreCase)) source = "no validated campaign decoder result";
            string detail = "Latest verified backup: " + date + "\nSource: " + source
                + "\n" + path + "\nThree decimal places are display formatting, not measured accuracy.";
            results.Add(new GameProgressSnapshot(name, executable, label, detail));
        }
        return results;
    }

    private static string FormatScope(JsonNode? scope, string name)
    {
        string status = DataJson.Text(scope?["status"]);
        JsonNode? fractionNode = scope?["fraction"];
        if ((status == "measured" || status == "estimated") && fractionNode is JsonValue value
            && value.TryGetValue<double>(out double fraction) && double.IsFinite(fraction) && fraction >= 0 && fraction <= 1)
        {
            string text = name + " ~" + (100 * fraction).ToString("0.0", CultureInfo.InvariantCulture) + "% " + status;
            JsonNode? remainingNode = scope?["remaining_hours"];
            if (remainingNode is JsonValue remainingValue && remainingValue.TryGetValue<double>(out double remaining)
                && double.IsFinite(remaining) && remaining >= 0)
                text += ", ~" + remaining.ToString("0.0", CultureInfo.InvariantCulture) + " h left";
            return text;
        }
        return name + " unavailable";
    }

    private static string ScopeDetail(JsonNode? scope)
    {
        string basis = DataJson.Text(scope?["basis"]);
        string decoder = DataJson.Text(scope?["decoder"]);
        var details = new List<string>
        {
            (string.IsNullOrWhiteSpace(basis) ? "No validated evidence" : basis)
                + (string.IsNullOrWhiteSpace(decoder) ? "" : " (" + decoder + ")")
        };
        if (scope?["availability_reason"] is JsonObject unavailable)
        {
            string code = DataJson.Text(unavailable["code"]);
            if (!string.IsNullOrWhiteSpace(code)) details.Add("Reason: " + code);
            if (unavailable["missing"] is JsonArray missing)
            {
                foreach (var item in missing.OfType<JsonObject>().Take(4))
                {
                    string name = DataJson.Text(item["item"]), reason = DataJson.Text(item["reason"]);
                    if (!string.IsNullOrWhiteSpace(name))
                        details.Add(name + (string.IsNullOrWhiteSpace(reason) ? "" : ": " + reason));
                }
            }
        }
        var evidence = scope?["evidence"];
        string validation = DataJson.Text(evidence?["backup"]?["manifest_validation"]);
        if (!string.IsNullOrWhiteSpace(validation)) details.Add("Backup validation: " + validation);
        if (evidence?["backup"]?["files"] is JsonArray files) details.Add("Manifest files: " + files.Count);
        string activeProfile = DataJson.Text(evidence?["active_profile"]?["status"]);
        if (!string.IsNullOrWhiteSpace(activeProfile)) details.Add("Active profile: " + activeProfile);
        string build = DataJson.Text(evidence?["game_build"]?["status"]);
        if (!string.IsNullOrWhiteSpace(build)) details.Add("Game build: " + build);
        return string.Join("; ", details);
    }
}
