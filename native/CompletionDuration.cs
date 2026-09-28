using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

internal static class CompletionDuration
{
    internal static DateTime RetryAt(LibraryStore store)
    {
        try {
            string path = Path.Combine(store.Cache, "completion-times", "provider-cooldown.json");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 4096) return default;
            double seconds = DataJson.Number(JsonNode.Parse(stream)?["retry_at"]);
            if (!double.IsFinite(seconds) || seconds <= 0 || seconds > 253402300799) return default;
            var retry = DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)).UtcDateTime;
            return retry > DateTime.UtcNow && retry <= DateTime.UtcNow.AddDays(1) ? retry : default;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException) { return default; }
    }
    internal static bool HasSource(JsonObject value)
    {
        string canonical = DataJson.Text(value["timeTitle"]);
        string requested = DataJson.Text(value["timeQuery"], DataJson.Text(value["matchedTitle"], DataJson.Text(value["name"])));
        double samples = DataJson.Number(value["timeSamples"]);
        return IsRecognizedSource(DataJson.Text(value["source"]?["time"]))
            && TryGetProductId(value, out _)
            && samples > 0 && samples <= int.MaxValue && samples == Math.Truncate(samples)
            && !string.IsNullOrWhiteSpace(canonical) && !string.IsNullOrWhiteSpace(requested)
            && (MetadataClient.SameTitle(requested, canonical) || MetadataClient.SameTitle(requested, DataJson.Text(value["timeAlias"])));
    }

    internal static bool IsLiveSource(JsonObject value) => DataJson.Text(value["source"]?["time"]) == "howlongtobeat-live";

    internal static bool TryGetProductId(JsonObject value, out string productId)
    {
        productId = "";
        string url = DataJson.Text(value["timeUrl"]);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "howlongtobeat.com", StringComparison.OrdinalIgnoreCase)
            || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.Query.Length > 0 || !uri.IsDefaultPort
            || !System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath, @"\A/game/[1-9][0-9]*/?\z")) return false;
        string id = uri.AbsolutePath.TrimEnd('/').Split('/').Last();
        if (!int.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int numericId)
            || numericId <= 0 || id != numericId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || DataJson.Text(value["timeTitle"]).Length == 0) return false;
        productId = "HLTB:" + numericId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    private static bool IsRecognizedSource(string source) =>
        source is "howlongtobeat-live" or "howlongtobeat-cache" or "howlongtobeat-cache-stale";
    internal static Task<JsonObject?> Read(string title, LibraryStore store, CancellationToken cancellation)
        => ReadWithReleaseYear(title, store, cancellation, 0);
    internal static async Task<JsonObject?> ReadWithReleaseYear(string title, LibraryStore store, CancellationToken cancellation, int releaseYear)
    {
        string python = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Python\pythoncore-3.14-64\python.exe");
        if (!File.Exists(python)) throw new FileNotFoundException("Completion-duration runtime is unavailable.");
        var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = GameProgressClient.Project };
        start.Environment["PYTHONPATH"] = Path.Combine(GameProgressClient.Project, "src");
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.StandardOutputEncoding = System.Text.Encoding.UTF8;
        foreach (var arg in new[] { "-m", "gameprogress.hltb", "--name", title, "--platform", "PC", "--cache-dir", Path.Combine(store.Cache, "completion-times") }) start.ArgumentList.Add(arg);
        if (releaseYear >= 1970 && releaseYear <= DateTime.UtcNow.Year) {
            start.ArgumentList.Add("--release-year"); start.ArgumentList.Add(releaseYear.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        using var process = Process.Start(start) ?? throw new IOException("Completion-duration lookup could not start.");
        var output = process.StandardOutput.ReadToEndAsync(cancellation);
        var error = process.StandardError.ReadToEndAsync(cancellation);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        await error;
        if (process.ExitCode != 0) throw new IOException("Completion-duration source did not return a valid response.");
        return Parse(title, JsonNode.Parse(await output) as JsonObject ?? throw new FormatException("Invalid duration response."));
    }

    internal static JsonObject? Parse(string title, JsonObject response)
    {
        if (string.IsNullOrWhiteSpace(DataJson.Text(response["game_name"]))) throw new FormatException("Duration canonical title missing.");
        if (!MetadataClient.SameTitle(title, DataJson.Text(response["game_name"])) &&
            !MetadataClient.SameTitle(title, DataJson.Text(response["provider_alias"]))) throw new FormatException("Duration title mismatch.");
        double id = DataJson.Number(response["hltb_id"]), hours = DataJson.Number(response["main"]?["avg"]);
        double samples = DataJson.Number(response["main"]?["polled"]);
        if (id <= 0 || id > int.MaxValue || id != Math.Truncate(id) || hours <= 0 || hours >= 100000
            || samples <= 0 || samples > int.MaxValue || samples != Math.Truncate(samples)) return null;
        if (DataJson.Text(response["source"]) is not ("live" or "cache" or "cache-stale")) return null;
        return new JsonObject { ["time"] = hours, ["timeUrl"] = "https://howlongtobeat.com/game/" + id.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
            ["timeTitle"] = response["game_name"]?.DeepClone(), ["timeAlias"] = response["provider_alias"]?.DeepClone(),
            ["timeIdentityReleaseYear"] = response["identity_release_year"]?.DeepClone(),
            ["timeSamples"] = response["main"]?["polled"]?.DeepClone(), ["timeStyles"] = new JsonObject {
                ["main"] = response["main"]?.DeepClone(), ["extra"] = response["extra"]?.DeepClone(),
                ["completionist"] = response["completionist"]?.DeepClone() },
            ["source"] = new JsonObject { ["time"] = "howlongtobeat-" + DataJson.Text(response["source"]) } };
    }
}
