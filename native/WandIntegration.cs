using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

internal sealed record WandTarget(string TitleId, string GameId, string TitleName, string Platform, string VersionPath);
internal sealed record WandCustomInstallationRequest(string GameId, string ExecutablePath, string WorkingDirectory, string Sku, string CorrelationId);
internal sealed record WandLaunchResult(Process? Process, bool UsedProtocol, string Message, bool OwnsProcess = false, WandSessionSnapshot? Session = null);
internal sealed record WandRegisteredInstallation(string TitleId, string GameId, string ExecutablePath);
internal sealed record WandSupportedGame(string Folder, string TitleId, string GameId, string Name, string Path);
internal sealed record WandProtocolPreflight(bool SafeToDispatch, bool TrainerBusy, string Reason, string? ActiveTrainerGameId, int? ActiveTrainerProcessId);

internal static class WandIntegration
{
    private const string CatalogUrl = "https://storage-cdn.wemod.com/catalog.json";
    private const long MaxCatalogBytes = 24 * 1024 * 1024;
    internal static readonly TimeSpan WandStartupWindow = TimeSpan.FromMinutes(3);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> LaunchGates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> SessionMonitors = new(StringComparer.OrdinalIgnoreCase);
    private sealed class CatalogIndex
    {
        private readonly Dictionary<string, List<(string Key, JsonObject Title)>> byAlias = new(StringComparer.Ordinal);
        internal void Add(string alias, string key, JsonObject title)
        {
            if (alias.Length == 0) return;
            if (!byAlias.TryGetValue(alias, out var values)) byAlias[alias] = values = new();
            values.Add((key, title));
        }
        internal IEnumerable<(string Key, JsonObject Title)> Candidates(IReadOnlyList<string> aliases)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var alias in aliases)
                if (byAlias.TryGetValue(alias, out var values))
                    foreach (var value in values)
                        if (seen.Add(value.Key)) yield return value;
        }
    }
    private static readonly ConditionalWeakTable<JsonObject, CatalogIndex> CatalogIndexes = new();

    internal static string Normalize(string value) => Regex.Replace(value.Trim().ToLowerInvariant(), @"[^\p{L}\p{N}]+", "");

    internal static WandSessionStatus StatusForTrainerEvidence(WandTrainerEvidence evidence) =>
        evidence.Source is "trainer-connected-before-exit" or "game-process-changed" or "game-pid-reused" or "game-exited"
            ? WandSessionStatus.Disconnected
        : evidence.Confirmed ? WandSessionStatus.Connected
        : WandSessionStatus.GameRunningConnectionUnconfirmed;

    private static bool IsHistoricalTrainerEvidence(WandTrainerEvidence evidence) =>
        evidence.Source == "trainer-connected-before-exit";

    internal static string BuildProtocolUri(string titleId, string gameId) =>
        "wemod://play?titleId=" + Uri.EscapeDataString(titleId) + "&gameId=" + Uri.EscapeDataString(gameId);

    internal static WandProtocolPreflight InterpretProtocolPreflight(string output, int exitCode)
    {
        const string unavailable = "trainer-state-unavailable-before-navigation";
        try
        {
            using var result = JsonDocument.Parse(output);
            var root = result.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("navigated", out var navigated) || navigated.ValueKind != JsonValueKind.False
                || !root.TryGetProperty("playDispatched", out var dispatched) || dispatched.ValueKind != JsonValueKind.False
                || !root.TryGetProperty("ok", out var ok)
                || !root.TryGetProperty("state", out var stateValue) || stateValue.ValueKind != JsonValueKind.String)
                return new WandProtocolPreflight(false, false, unavailable, null, null);

            string state = stateValue.GetString() ?? string.Empty;
            if (exitCode == 0 && ok.ValueKind == JsonValueKind.True && state == "idle")
                return new WandProtocolPreflight(true, false, "idle-sidebar-state", null, null);

            if (exitCode == 3 && ok.ValueKind == JsonValueKind.False && state == "blocked"
                && root.TryGetProperty("reason", out var reasonValue)
                && reasonValue.ValueKind == JsonValueKind.String
                && reasonValue.GetString() == "trainer-was-already-launching")
            {
                string? gameId = root.TryGetProperty("activeTrainerGameId", out var gameValue)
                    && gameValue.ValueKind == JsonValueKind.String
                    && Regex.IsMatch(gameValue.GetString() ?? string.Empty, "^[1-9]\\d*$")
                    ? gameValue.GetString() : null;
                int? processId = root.TryGetProperty("activeTrainerProcessId", out var processValue)
                    && processValue.ValueKind == JsonValueKind.Number
                    && processValue.TryGetInt32(out int parsedProcessId)
                    && parsedProcessId > 0 ? parsedProcessId : null;
                return new WandProtocolPreflight(false, true, "trainer-was-already-launching", gameId, processId);
            }
        }
        catch (JsonException) { }
        return new WandProtocolPreflight(false, false, unavailable, null, null);
    }

    internal static string DescribeProtocolPreflightBlock(WandProtocolPreflight preflight, string requestedGameId)
    {
        if (preflight.TrainerBusy)
        {
            string target = preflight.ActiveTrainerGameId == null ? "a trainer"
                : string.Equals(preflight.ActiveTrainerGameId, requestedGameId, StringComparison.Ordinal)
                    ? "the selected game" : "another game";
            string identity = (preflight.ActiveTrainerGameId == null ? string.Empty : " gameId=" + preflight.ActiveTrainerGameId)
                + (preflight.ActiveTrainerProcessId == null ? string.Empty : " PID=" + preflight.ActiveTrainerProcessId);
            return "Wand's sidebar reports a trainer busy for " + target + identity
                + ". Sidebar state does not confirm trainer attachment; no new protocol launch was sent.";
        }
        return "Wand's trainer state could not be read safely; no protocol launch was sent.";
    }

    internal static WandCdpLaunchResult InterpretCdpLaunchResult(string output, int exitCode)
    {
        try
        {
            using var result = JsonDocument.Parse(output);
            var root = result.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("navigated", out var navigatedValue)
                || navigatedValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !root.TryGetProperty("playDispatched", out var dispatchedValue)
                || dispatchedValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return new WandCdpLaunchResult(WandCdpLaunchDisposition.Unknown, true, "helper-output-invalid");

            bool navigated = navigatedValue.ValueKind == JsonValueKind.True;
            bool playDispatched = dispatchedValue.ValueKind == JsonValueKind.True;
            string reason = root.TryGetProperty("reason", out var reasonValue)
                && reasonValue.ValueKind == JsonValueKind.String
                ? reasonValue.GetString() ?? "helper-result"
                : "helper-result";
            string dispatchOutcome = root.TryGetProperty("dispatchOutcome", out var outcomeValue)
                && outcomeValue.ValueKind == JsonValueKind.String
                ? outcomeValue.GetString() ?? string.Empty
                : string.Empty;

            if (navigated)
            {
                return exitCode == 0 && playDispatched && dispatchOutcome == "dispatched"
                    ? new WandCdpLaunchResult(WandCdpLaunchDisposition.PlayDispatched, true, reason)
                    : new WandCdpLaunchResult(WandCdpLaunchDisposition.Unknown, true, reason);
            }
            if (playDispatched)
                return new WandCdpLaunchResult(WandCdpLaunchDisposition.Unknown, false, reason);

            if (exitCode == 3 && reason == "trainer-was-already-launching")
                return new WandCdpLaunchResult(WandCdpLaunchDisposition.SafetyBlocked, false, reason);

            if ((exitCode == 1 && dispatchOutcome == "not-dispatched")
                || (exitCode == 3 && reason == "trainer-state-unavailable-before-navigation"))
                return new WandCdpLaunchResult(WandCdpLaunchDisposition.KnownNoDispatch, false, reason);
        }
        catch (JsonException) { }

        return new WandCdpLaunchResult(WandCdpLaunchDisposition.Unknown, true, "helper-outcome-unknown");
    }

    internal static WandCustomInstallationRequest BuildCustomInstallationRequest(string gameId, string executable)
    {
        if (string.IsNullOrWhiteSpace(gameId)) throw new ArgumentException("Wand game id is required.", nameof(gameId));
        if (string.IsNullOrWhiteSpace(executable)) throw new ArgumentException("An exact executable path is required.", nameof(executable));

        string fullPath = Path.GetFullPath(executable);
        if (!Path.IsPathFullyQualified(fullPath) || !string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Wand custom installations require an absolute .exe path.", nameof(executable));
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The exact game executable was not found.", fullPath);

        string? workingDirectory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(workingDirectory)) throw new ArgumentException("The exact executable has no working directory.", nameof(executable));

        string sku = gameId.Trim() + "_" + fullPath.ToLowerInvariant();
        return new WandCustomInstallationRequest(gameId.Trim(), fullPath, workingDirectory, sku, "custom:" + sku);
    }

    internal static bool TryResolve(JsonObject catalog, Game game, string executable, out WandTarget target)
    {
        target = null!;
        if (catalog["titles"] is not JsonObject || catalog["games"] is not JsonObject allGames) return false;
        var aliases = BuildAliases(game, executable);
        if (aliases.Count == 0) return false;

        var matches = new List<(int score, WandTarget target)>();
        foreach (var titleEntry in CatalogIndexes.GetValue(catalog, BuildIndex).Candidates(aliases))
        {
            string titleKey = titleEntry.Key;
            JsonObject title = titleEntry.Title;
            int titleScore = TitleScore(aliases, title);
            if (titleScore <= 0) continue;
            string titleId = DataJson.Text(title["id"], titleKey);
            var gameEntries = new List<(string id, JsonObject data)>();
            if (title["gameIds"] is JsonArray ids)
            {
                foreach (var idNode in ids)
                {
                    string id = DataJson.Text(idNode);
                    if (id.Length > 0 && allGames[id] is JsonObject data) gameEntries.Add((id, data));
                }
            }
            if (gameEntries.Count == 0)
            {
                foreach (var gameEntry in allGames)
                {
                    if (gameEntry.Value is JsonObject data && DataJson.Text(data["titleId"]) == titleId)
                        gameEntries.Add((gameEntry.Key, data));
                }
            }
            foreach (var gameEntry in gameEntries)
            {
                var data = gameEntry.data;
                string platform = DataJson.Text(data["platformId"]);
                string versionPath = DataJson.Text(data["versionPath"]);
                // A title match is not enough to identify the executable that
                // Wand must inject.  Reject every catalog entry whose exact
                // version path does not identify the selected executable; this
                // prevents a generic launcher name from winning for the wrong
                // game or a sibling version.
                if (!ExactVersionPathMatches(executable, versionPath)) continue;
                int score = titleScore + GameScore(executable, platform, versionPath, data);
                var candidate = new WandTarget(titleId, gameEntry.id, DataJson.Text(title["name"], game.Name), platform, versionPath);
                matches.Add((score, candidate));
            }
        }
        if (matches.Count == 0) return false;
        int bestScore = matches.Max(match => match.score);
        var bestMatches = matches.Where(match => match.score == bestScore).ToArray();
        // Never guess between two catalog games that identify the same local
        // executable with equal confidence. A fail-closed result is safer than
        // injecting the wrong trainer into a valid game.
        if (bestMatches.Length != 1) return false;
        target = bestMatches[0].target;
        return true;
    }

    internal static bool ExactVersionPathMatches(string executable, string versionPath)
    {
        if (string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(versionPath)) return false;
        string raw = versionPath.Trim();
        if (raw.StartsWith("\\", StringComparison.Ordinal) || raw.Contains(':', StringComparison.Ordinal)) return false;

        string relative = raw.Replace('/', '\\');
        var segments = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or "..")) return false;

        string fullPath;
        try { fullPath = Path.GetFullPath(executable); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return false; }

        // Unity catalogs may fingerprint a managed DLL rather than the process
        // executable. The sibling <exe>_Data tree binds that fingerprint to this
        // exact executable; an unrelated DLL or launcher must not qualify.
        if (segments.Length > 1 && segments[0].Equals(Path.GetFileNameWithoutExtension(fullPath) + "_Data", StringComparison.OrdinalIgnoreCase)
            && Path.GetExtension(fullPath).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            && Path.GetExtension(segments[^1]).Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(fullPath)) return false;
            try
            {
                string fingerprint = Path.GetDirectoryName(fullPath)!;
                foreach (string segment in segments)
                {
                    fingerprint = Path.Combine(fingerprint, segment);
                    if ((File.GetAttributes(fingerprint) & FileAttributes.ReparsePoint) != 0) return false;
                }
                return File.Exists(fingerprint);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
        }

        if (segments.Length == 1)
            return string.Equals(Path.GetFileName(fullPath), segments[0], StringComparison.OrdinalIgnoreCase);

        string suffix = "\\" + string.Join("\\", segments);
        return fullPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }

    private static CatalogIndex BuildIndex(JsonObject catalog)
    {
        var index = new CatalogIndex();
        if (catalog["titles"] is not JsonObject titles) return index;
        foreach (var entry in titles)
        {
            if (entry.Value is not JsonObject title) continue;
            index.Add(Normalize(DataJson.Text(title["name"])), entry.Key, title);
            index.Add(Normalize(DataJson.Text(title["slug"])), entry.Key, title);
            if (title["terms"] is JsonArray terms)
                foreach (var term in terms) index.Add(Normalize(DataJson.Text(term)), entry.Key, title);
        }
        return index;
    }

    internal static async Task<JsonObject> LoadCatalogAsync(LibraryStore store, CancellationToken cancellation)
    {
        string path = LibraryStore.SafeChild(store.Cache, "wand-catalog.json");
        if (TryRead(path, out var cached) && File.GetLastWriteTimeUtc(path) >= DateTime.UtcNow.AddHours(-24)) return cached;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("GameLibraryNative/1.0");
            using var response = await http.GetAsync(CatalogUrl, HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxCatalogBytes) throw new FormatException("Wand catalog exceeds the safe size limit.");
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellation);
            if (bytes.LongLength > MaxCatalogBytes) throw new FormatException("Wand catalog exceeds the safe size limit.");
            string json = System.Text.Encoding.UTF8.GetString(bytes);
            if (!TryParse(json, out var fresh)) throw new FormatException("Wand returned an invalid catalog.");
            store.CacheData("wand-catalog.json", json);
            return fresh;
        }
        catch when (TryRead(path, out var stale))
        {
            return stale;
        }
    }

    /// <summary>
    /// The Wand button requires an exact executable in Wand's current local
    /// installation registrations. Missing registration evidence hides the
    /// button instead of creating a new registration or launching unmodified.
    /// </summary>
    internal static bool CanLaunchExistingWandInstall(Game game, string executable, LibraryStore store, out string message)
    {
        // The button and LaunchCore both use the current LevelDB-derived Wand
        // registrations. A packaged supported-games manifest is only a
        // fallback/test fixture and can lag behind a user's current library.
        // LaunchCore validates the exact title/game pair against a refreshed
        // catalog before sending any play request.
        message = "The selected executable is not in Wand's current exact installation registrations.";
        if (!TryGetExistingWandInstallation(executable, out _)) return false;
        message = string.Empty;
        return true;
    }

    internal static bool CanLaunchExistingWandInstall(Game game, string executable, LibraryStore store, IEnumerable<string> manifestPaths, out string message)
    {
        message = "Play with Wand is available only for an exact executable that is already registered in Wand.";
        if (!TryGetExistingWandInstallation(executable, manifestPaths, out var registration)) return false;

        JsonObject catalog;
        try
        {
            string cachePath = LibraryStore.SafeChild(store.Cache, "wand-catalog.json");
            if (!TryRead(cachePath, out catalog))
            {
                message = "Wand's local catalog is unavailable, so Play with Wand stays hidden until it can be verified.";
                return false;
            }
        }
        catch (ArgumentException)
        {
            message = "Wand's local catalog path is invalid, so Play with Wand stays hidden.";
            return false;
        }

        if (!TryResolveRegisteredTarget(catalog, registration, out _))
        {
            message = "Wand's saved registration does not match a current exact catalog title/game pair, so no mod launch was offered.";
            return false;
        }
        message = string.Empty;
        return true;
    }

    internal static bool TryResolveRegisteredTarget(JsonObject catalog, WandRegisteredInstallation registration, out WandTarget target)
    {
        target = null!;
        if (catalog["titles"] is not JsonObject titles || catalog["games"] is not JsonObject games ||
            games[registration.GameId] is not JsonObject game) return false;
        string gameTitleId = DataJson.Text(game["titleId"]);
        if (!string.Equals(gameTitleId, registration.TitleId, StringComparison.Ordinal)) return false;
        JsonObject? title = titles[registration.TitleId] as JsonObject;
        if (title == null)
            title = titles.Select(entry => entry.Value as JsonObject)
                .FirstOrDefault(candidate => candidate != null && string.Equals(DataJson.Text(candidate["id"]), registration.TitleId, StringComparison.Ordinal));
        if (title == null) return false;
        if (title["gameIds"] is JsonArray ids && !ids.Any(id => string.Equals(DataJson.Text(id), registration.GameId, StringComparison.Ordinal))) return false;
        target = new WandTarget(
            registration.TitleId,
            registration.GameId,
            DataJson.Text(title["name"], registration.TitleId),
            DataJson.Text(game["platformId"]),
            DataJson.Text(game["versionPath"]));
        return true;
    }

    internal static bool TryGetExistingWandInstallation(string executable, out WandRegisteredInstallation installation)
    {
        installation = null!;
        var row = LoadSupportedGames().FirstOrDefault(row => SameExecutablePath(row.Path, executable));
        if (row == null) return false;
        installation = new(row.TitleId, row.GameId, row.Path);
        return true;
    }

    // The overload keeps the format testable without reading a real user's Wand
    // data. The production caller only supplies the locally maintained manifest.
    internal static bool TryGetExistingWandInstallation(string executable, IEnumerable<string> manifestPaths, out WandRegisteredInstallation installation)
    {
        installation = null!;
        string fullPath;
        try { fullPath = Path.GetFullPath(executable); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return false; }
        if (!File.Exists(fullPath)) return false;

        foreach (var manifestPath in manifestPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(manifestPath)) continue;
                if (JsonNode.Parse(File.ReadAllText(manifestPath)) is not JsonArray rows) continue;
                foreach (var node in rows)
                {
                    if (node is not JsonObject row) continue;
                    string registeredPath = DataJson.Text(row["path"]);
                    string titleId = DataJson.Text(row["titleId"]);
                    string gameId = DataJson.Text(row["gameId"]);
                    if (titleId.Length == 0 || gameId.Length == 0 || !SameExecutablePath(fullPath, registeredPath)) continue;
                    installation = new WandRegisteredInstallation(titleId, gameId, fullPath);
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException) { }
        }
        return false;
    }

    private static bool RegistrationMatches(WandRegisteredInstallation registration, WandTarget target) =>
        string.Equals(registration.TitleId, target.TitleId, StringComparison.Ordinal)
        && string.Equals(registration.GameId, target.GameId, StringComparison.Ordinal);

    private static bool SameExecutablePath(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return false; }
    }

    private static IEnumerable<string> RegisteredInstallationManifestPaths()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile)) yield break;
        yield return Path.Combine(userProfile, ".codex", "plugins", "mods", "scripts", "supported-games.json");
        yield return Path.Combine(userProfile, ".codex", "skills", "mods", "scripts", "supported-games.json");
        yield return Path.Combine(AppContext.BaseDirectory, "tools", "wand-supported-games.json");
    }

    internal static IReadOnlyList<WandSupportedGame> LoadSupportedGames() => WandLiveLibrary.Read();

    internal static IReadOnlyList<WandSupportedGame> LoadSupportedGames(IEnumerable<string> manifestPaths)
    {
        var candidates = manifestPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                try { return new { Path = path, Exists = File.Exists(path), Modified = File.GetLastWriteTimeUtc(path) }; }
                catch { return new { Path = path, Exists = false, Modified = DateTime.MinValue }; }
            })
            .Where(candidate => candidate.Exists)
            .OrderByDescending(candidate => candidate.Modified)
            .ToArray();
        foreach (var candidate in candidates)
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(candidate.Path)) is not JsonArray rows) continue;
                var games = new List<WandSupportedGame>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var node in rows)
                {
                    if (node is not JsonObject row) continue;
                    string folder = DataJson.Text(row["folder"]);
                    string titleId = DataJson.Text(row["titleId"]);
                    string gameId = DataJson.Text(row["gameId"]);
                    string name = DataJson.Text(row["name"]);
                    string path = DataJson.Text(row["path"]);
                    if (folder.Length == 0 || titleId.Length == 0 || gameId.Length == 0 || name.Length == 0 ||
                        !Path.IsPathFullyQualified(path) || !seen.Add(gameId)) continue;
                    games.Add(new WandSupportedGame(folder, titleId, gameId, name, Path.GetFullPath(path)));
                }
                if (games.Count > 0) return games;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException) { }
        }
        return Array.Empty<WandSupportedGame>();
    }

    internal static string ResolveTrackedExecutable(string launcher)
    {
        string fullPath;
        try { fullPath = Path.GetFullPath(launcher); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return launcher; }
        string extension = Path.GetExtension(fullPath);
        if (!extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)) return fullPath;
        string? root = Path.GetDirectoryName(fullPath);
        if (root == null || !File.Exists(fullPath)) return fullPath;
        string current = root;
        try
        {
            foreach (var raw in File.ReadLines(fullPath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("@echo", StringComparison.OrdinalIgnoreCase) || line.StartsWith("rem ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("::", StringComparison.Ordinal)) continue;
                var cd = Regex.Match(line, @"^(?:cd|pushd)\s+(?:/d\s+)?(?<path>[^&|<>]+)$", RegexOptions.IgnoreCase);
                if (cd.Success)
                {
                    string relative = cd.Groups["path"].Value.Trim().Trim('"');
                    string changed = Path.GetFullPath(Path.IsPathFullyQualified(relative) ? relative : Path.Combine(current, relative));
                    string changedBoundary = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
                    if (changed.Equals(root, StringComparison.OrdinalIgnoreCase) || changed.StartsWith(changedBoundary, StringComparison.OrdinalIgnoreCase)) current = changed;
                    continue;
                }
                var executable = Regex.Match(line, @"^(?:call\s+)?(?:(""(?<quoted>[^""]+\.exe)"")|(?<plain>[^\s&|<>]+\.exe))(?:\s|$)", RegexOptions.IgnoreCase);
                if (!executable.Success) continue;
                string token = executable.Groups["quoted"].Success ? executable.Groups["quoted"].Value : executable.Groups["plain"].Value;
                string candidate = Path.GetFullPath(Path.IsPathFullyQualified(token) ? token : Path.Combine(current, token));
                string candidateBoundary = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
                if ((candidate.Equals(root, StringComparison.OrdinalIgnoreCase) || candidate.StartsWith(candidateBoundary, StringComparison.OrdinalIgnoreCase)) && File.Exists(candidate)) return candidate;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return fullPath;
    }

    internal static string ResolveTrackedExecutable(string launcher, string versionPath)
    {
        string commandTarget = ResolveTrackedExecutable(launcher);
        if (!SameExecutablePath(commandTarget, launcher)) return commandTarget;
        string relative = versionPath.Trim().Replace('/', '\\').TrimStart('\\');
        if (!Path.GetExtension(relative).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return commandTarget;
        if (relative.Length == 0 || relative.Contains(':', StringComparison.Ordinal)) return commandTarget;
        string? current;
        try { current = Path.GetDirectoryName(Path.GetFullPath(launcher)); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return commandTarget; }
        for (var depth = 0; current != null && depth < 6; depth++, current = Path.GetDirectoryName(current))
        {
            try
            {
                string candidate = Path.GetFullPath(Path.Combine(current, relative));
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { }
        }
        return commandTarget;
    }

    internal static string? ResolveInstalledExecutable(Game game, string folder, LibraryStore store)
    {
        if (!Directory.Exists(folder)) return null;
        var candidates = InstalledScanner.FindGameExecutables(folder);
        if (candidates.Count == 0) return null;
        JsonObject? catalog = null;
        try
        {
            string cachePath = LibraryStore.SafeChild(store.Cache, "wand-catalog.json");
            if (TryRead(cachePath, out var cached)) catalog = cached;
        }
        catch (ArgumentException) { }

        var aliases = new[]
        {
            game.Id,
            game.Name,
            Path.GetFileName(Path.TrimEndingDirectorySeparator(folder))
        }.Select(Normalize).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        var ranked = candidates.Select(path =>
        {
            string stem = Normalize(Path.GetFileNameWithoutExtension(path));
            string relative = Path.GetRelativePath(folder, path).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
            string relativeName = Normalize(relative);
            int depth = relative.Count(c => c == '/') + 1;
            int score = aliases.Sum(alias => alias == stem ? 2600 : alias.Length >= 6 && (stem.Contains(alias, StringComparison.Ordinal) || alias.Contains(stem, StringComparison.Ordinal)) ? 500 : 0);
            // A legacy installation normally keeps its real game binary at the folder root,
            // while launchers and helper runtimes are nested or explicitly named. Give that
            // stable layout a strong preference without relying on a fuzzy Wand title match.
            if (depth == 1) score += 2400;
            string relativeLower = relative.ToLowerInvariant();
            if (relativeLower.Contains("/binaries/win64/", StringComparison.Ordinal) || relativeLower.Contains("/binaries/win32/", StringComparison.Ordinal)) score += 1800;
            if (stem.Contains("shipping", StringComparison.Ordinal)) score += 1200;
            else if (stem.Contains("client", StringComparison.Ordinal)) score += 500;
            try
            {
                long bytes = new FileInfo(path).Length;
                if (bytes >= 20 * 1024 * 1024) score += 600;
                else if (bytes >= 4 * 1024 * 1024) score += 400;
                else if (bytes < 512 * 1024) score -= 700;
            }
            catch (IOException) { }
            if (stem.Contains("launcher", StringComparison.Ordinal) || stem.Contains("bootstrap", StringComparison.Ordinal) || stem.Contains("updater", StringComparison.Ordinal) || stem.Contains("installer", StringComparison.Ordinal)) score -= 2600;
            if (stem.StartsWith("start", StringComparison.Ordinal) || stem.StartsWith("setup", StringComparison.Ordinal) || stem.StartsWith("config", StringComparison.Ordinal)) score -= 900;
            score -= Math.Min(depth, 10) * 10;
            if (catalog != null)
            {
                try
                {
                    if (TryResolve(catalog, game, path, out var target))
                    {
                        score += 1200;
                        string version = target.VersionPath.Replace('\\', '/').TrimStart('/');
                        string normalizedVersion = Normalize(version);
                        if (normalizedVersion.Length > 0 && relativeName == normalizedVersion) score += 6000;
                        else if (normalizedVersion.Length > 0 && Normalize(Path.GetFileName(version)) == Normalize(Path.GetFileName(path))) score += 4500;
                    }
                }
                catch (InvalidOperationException) { }
            }
            return new { Path = path, Score = score, Depth = depth };
        }).OrderByDescending(c => c.Score).ThenBy(c => c.Depth).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (ranked.Length == 1) return ranked[0].Path;
        var best = ranked[0];
        var second = ranked[1];
        if (best.Score < 2000)
        {
            // A unique, materially stronger executable is safer than the old
            // shortest-prefix fallback. Legacy folders such as Deus Ex can
            // contain a tiny DX2 bootstrap beside the real DX2Main binary.
            if (best.Score > 0 && best.Score - second.Score >= 500) return best.Path;
            // Some older games expose a short primary executable beside a longer helper
            // (for example DX2.exe and DX2Main.exe) and have no Wand catalog entry. Prefer
            // the unique shortest-prefix executable only when every competing candidate is
            // a longer name in the same family; otherwise keep the choice explicit.
            var primary = ranked.Where(candidate =>
            {
                string candidateStem = Normalize(Path.GetFileNameWithoutExtension(candidate.Path));
                if (candidateStem.Length == 0 || candidateStem.Contains("launcher", StringComparison.Ordinal)) return false;
                return ranked.Where(other => !ReferenceEquals(other, candidate)).All(other =>
                {
                    string otherStem = Normalize(Path.GetFileNameWithoutExtension(other.Path));
                    return otherStem.Contains("launcher", StringComparison.Ordinal) || otherStem.StartsWith(candidateStem, StringComparison.Ordinal);
                });
            }).ToArray();
            if (primary.Length == 1) return primary[0].Path;
            return null;
        }
        if (best.Score == second.Score && best.Score < 4000) return null;
        return best.Path;
    }

    internal static async Task<WandLaunchResult> LaunchAsync(Game game, string executable, string wandPath, LibraryStore store, CancellationToken cancellation)
    {
        string fullPath = Path.GetFullPath(executable);
        string key = game.Id + "\n" + fullPath;
        var gate = LaunchGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellation);
        try { return await LaunchCoreAsync(game, fullPath, wandPath, store, cancellation); }
        finally { gate.Release(); }
    }

    private static async Task<WandLaunchResult> LaunchCoreAsync(Game game, string executable, string wandPath, LibraryStore store, CancellationToken cancellation)
    {
        var session = WandSessionState.Begin(game.Id, executable);
        WandLaunchResult Result(Process? process, bool usedProtocol, string message, bool ownsProcess = false) =>
            BuildLaunchResult(process, usedProtocol, message, ownsProcess, session, wandPath);
        JsonObject? catalog = null;
        try { catalog = await LoadCatalogAsync(store, cancellation); }
        catch (OperationCanceledException)
        {
            session.Transition(WandSessionStatus.Cancelled, "The user cancelled before Wand resolution completed.");
            throw;
        }
        catch (Exception ex)
        {
            store.Log("Wand catalog unavailable; no unmodified fallback will be launched: " + ex.Message);
        }

        if (catalog == null)
        {
            session.Transition(WandSessionStatus.ConnectionFailed, "Wand catalog is unavailable; no game was started.");
            return Result(null, false, "Wand catalog is unavailable. No unmodified game was started; retry after Wand is online.");
        }
        string selectedExecutable = executable;
        if (!TryGetExistingWandInstallation(selectedExecutable, out var registration))
        {
            session.Transition(WandSessionStatus.RegistrationInvalid, "The exact executable is not already registered in Wand.");
            return Result(null, false, "This exact executable is not already registered in Wand. No unmodified game was started and no new Wand registration was created.");
        }
        if (!TryResolveRegisteredTarget(catalog, registration, out var target))
        {
            session.Transition(WandSessionStatus.RegistrationInvalid, "The current Wand catalog does not contain the exact saved title and game registration.");
            return Result(null, false, "Wand's catalog no longer contains the exact saved title and game registration.");
        }
        string wandVersion = ReadWandVersion(wandPath);
        session.Resolve(target.TitleId, target.GameId, wandVersion);
        string trackedExecutable = ResolveTrackedExecutable(selectedExecutable, target.VersionPath);

        Process? ownedBootstrap = null;
        Process? existing = null;
        Process? ownedProtocolGame = null;
        bool ownedBootstrapGame = false;
        bool bootstrapContextObserved = false;
        void ReleaseLaunchHandles(string reason)
        {
            if (ownedProtocolGame != null)
            {
                ReleaseProcessHandleWithoutTermination(ownedProtocolGame);
                ownedProtocolGame = null;
            }
            if (existing != null)
            {
                ReleaseProcessHandleWithoutTermination(existing);
                existing = null;
            }
            if (ownedBootstrap != null)
            {
                ReleaseProcessHandleWithoutTermination(ownedBootstrap);
                ownedBootstrap = null;
            }
            store.Log("Released Wand launch process handles after " + reason + "; no game or Wand process was terminated.");
        }
        try
        {
            store.Log("Wand's pre-existing exact mapping was confirmed for gameId=" + registration.GameId + "; executable=" + registration.ExecutablePath + ".");

existing = FindExactProcess(trackedExecutable);
            if (existing != null)
                session.Transition(WandSessionStatus.StartingWand, "The exact registered game is already running; checking Wand readiness before attachment.");
            if (session.Read().Status is WandSessionStatus.Resolving or WandSessionStatus.WaitingForGame)
                session.Transition(WandSessionStatus.StartingWand, "Starting or checking Wand before any game bootstrap or protocol route.");
bool wandReady = await EnsureWandStartedAsync(wandPath, store, cancellation, restartForCdpDispatch: existing == null);
            if (!wandReady)
            {
                // Wand may be running without the CDP flag or still starting. The
                // exact-game protocol URI reaches Wand through its own handler
                // (it launches or attaches), so do not fail here: the single
                // dispatch attempt will send that URI.
                store.Log("Wand readiness was not fully confirmed; proceeding so the exact-game URI can reach Wand.");
            }
var initialPreflight = await InspectProtocolPreflightAsync(target.GameId, store, cancellation);
            if (!initialPreflight.SafeToDispatch)
            {
                // A busy or unknown trainer state must not block this game's
                // launch. Wand routes the exact-game URI by gameId, so proceed
                // and let the single dispatch attempt (CDP, then the exact-game
                // URI) carry the launch even while another game is playing.
                store.Log("Preflight was not idle before dispatch (" + initialPreflight.Reason + "); proceeding with the single exact-game launch attempt.");
            }
            if (existing == null)
            {
                string? bootstrap = Path.GetExtension(selectedExecutable).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                    ? ResolveBootstrapExecutable(selectedExecutable, target.VersionPath)
                    : null;
                if (bootstrap != null)
                {
                    session.Transition(WandSessionStatus.StartingGame, "Starting the verified root bootstrap to preserve the game's launch context.");
                    var bootstrapObserved = ExistingProcessIds(trackedExecutable);
                    var runningBootstrap = FindExactProcess(bootstrap);
                    if (runningBootstrap != null)
                    {
                        runningBootstrap.Dispose();
                        store.Log("Wand found the existing root bootstrap " + bootstrap + "; waiting for its exact nested executable before starting Wand.");
                    }
                    else
                    {
                        try
                        {
                            ownedBootstrap = Process.Start(new ProcessStartInfo(bootstrap)
                            {
                                WorkingDirectory = Path.GetDirectoryName(bootstrap),
                                UseShellExecute = true
                            });
                            if (ownedBootstrap != null)
                            {
                                store.Log("Started the safe root bootstrap " + bootstrap + " before starting Wand so the game inherits its correct launch context.");
                            }
                        }
                        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                        {
                            store.Log("The safe root bootstrap could not be started; continuing with the exact Wand URI: " + ex.Message);
                        }
                    }

                    session.Transition(WandSessionStatus.WaitingForGame, "Waiting for the exact nested game executable from the bootstrap.");
                    var bootstrappedGame = await WaitForNewGameAsync(trackedExecutable, bootstrapObserved, TimeSpan.FromSeconds(20), cancellation);
                    if (bootstrappedGame != null)
                    {
                        existing = bootstrappedGame;
                        bootstrapContextObserved = true;
                        // The exact process was absent before this launch
                        // attempt, even when the root wrapper pre-existed. It
                        // is therefore owned by this attempt for cleanup.
                        ownedBootstrapGame = true;
                        store.Log("The root bootstrap produced the exact nested executable PID " + existing.Id + "; Wand will be started before the protocol handoff.");
                    }
                    else
                    {
                        existing = FindExactProcess(trackedExecutable);
                        if (existing != null)
                        {
                            bootstrapContextObserved = true;
                            ownedBootstrapGame = true;
                            store.Log("The root bootstrap produced the exact nested executable after the initial wait; Wand will be started before the protocol handoff.");
                        }
                        else if (ownedBootstrap != null)
                        {
                            // A launcher may still be completing work after the
                            // exact process observation window. Release our
                            // handle, but never terminate it automatically.
                            ReleaseProcessHandleWithoutTermination(ownedBootstrap);
                            ownedBootstrap = null;
                        }
                    }
                }
            }

            if (bootstrapContextObserved && existing != null)
            {
                store.Log("Waiting for the bootstrapped exact game to expose a usable window before starting Wand.");
                bool bootstrapWindowUsable = await WaitForUsableGameWindowAsync(existing, TimeSpan.FromSeconds(20), cancellation);
                if (!bootstrapWindowUsable) store.Log("The bootstrap game did not expose a responsive window within 20 seconds; the exact process will remain tracked and Wand attachment will be reported as unconfirmed if no trainer evidence appears.");
                session.Transition(WandSessionStatus.StartingWand, "The exact bootstrap process was observed; checking Wand readiness.");
            }

            if (session.Read().Status is WandSessionStatus.Resolving or WandSessionStatus.WaitingForGame)
                session.Transition(WandSessionStatus.StartingWand, "Starting or checking the Wand client.");

            wandReady = await EnsureWandStartedAsync(wandPath, store, cancellation, restartForCdpDispatch: existing == null);
            if (!wandReady)
            {
                if (ownedBootstrap != null) { ReleaseProcessHandleWithoutTermination(ownedBootstrap); ownedBootstrap = null; }
                if (existing != null)
                {
                    session.TrackProcess(existing, "The selected game is running; Wand readiness could not be confirmed.");
                    if (session.Read().Status == WandSessionStatus.StartingWand)
                        session.Transition(WandSessionStatus.WaitingForGame, "The exact game is running while Wand readiness is unavailable.");
                    session.Transition(WandSessionStatus.GameRunningConnectionUnconfirmed, "The game remains running, but Wand readiness is unavailable.");
                    return Result(existing, false, "The game is running, but Wand could not be verified as ready. The game was left running and is being tracked; only Exit will close it.", ownedBootstrapGame);
                }
                session.Transition(WandSessionStatus.GameFailedToStart, "Wand did not become ready and no exact game process was found.");
                return Result(null, false, "Wand could not be verified as running. No game process was found, and no process was terminated.");
            }
            string label = target.TitleName + " (" + target.Platform + ")";
            store.Log("Wand readiness confirmed for " + label + "; titleId=" + target.TitleId + "; gameId=" + target.GameId + ".");

            if (bootstrapContextObserved && existing != null)
            {
                store.Log("Checking the bootstrapped game's usable window again after Wand readiness.");
            }

            if (existing != null)
            {
                session.Transition(WandSessionStatus.WaitingForGame, "Waiting briefly for a usable window on the exact running process.");
                bool existingWindowUsable = await WaitForUsableGameWindowAsync(existing, TimeSpan.FromSeconds(20), cancellation);
                if (!existingWindowUsable) store.Log("The exact running game did not expose a responsive window before attachment; connection remains unverified until trainer evidence is observed.");
                session.TrackProcess(existing, "The exact game process was verified before the Wand attachment request.");
var preflight = await InspectProtocolPreflightAsync(target.GameId, store, cancellation);
                if (!preflight.SafeToDispatch)
                    store.Log("Preflight not idle for the running game (" + preflight.Reason + "); proceeding with one exact-game attach attempt.");
                session.Transition(WandSessionStatus.Attaching,
                    "Checking current trainer evidence for the exact running process without sending a launch request.");
                var observationStarted = DateTime.UtcNow;
                store.Log("Observing current trainer state for already-running exact process " + selectedExecutable
                    + "; no Play route or protocol URI was sent because this URI's attach-versus-launch behavior is unverified.");
                var existingTrainerEvidence = await WaitForConnectionEvidenceAsync(wandPath, target.GameId, trackedExecutable, existing,
                    session.Read().ProcessCreationFileTime!, observationStarted, TimeSpan.FromSeconds(30), cancellation);
                if (existingTrainerEvidence.Confirmed)
                {
                    session.Transition(WandSessionStatus.Connected, existingTrainerEvidence.Detail, existingTrainerEvidence.Source, existingTrainerEvidence.ObservedUtc);
                    ReleaseProcessHandleWithoutTermination(ownedBootstrap);
                    ownedBootstrap = null;
                    return Result(existing, false, "Wand's current trainer state matches the already running " + label + " process.", ownedBootstrapGame);
                }
                ReleaseProcessHandleWithoutTermination(ownedBootstrap);
                ownedBootstrap = null;
                if (StatusForTrainerEvidence(existingTrainerEvidence) == WandSessionStatus.Disconnected)
                {
                    session.Transition(WandSessionStatus.Disconnected, existingTrainerEvidence.Detail, existingTrainerEvidence.Source, existingTrainerEvidence.ObservedUtc);
                    if (IsHistoricalTrainerEvidence(existingTrainerEvidence))
                    {
                        store.Log("The exact game exited after Tophat recorded a successful trainer command for PID " + existing.Id + ". The trace is historical evidence only; no current connection is reported.");
                        return Result(null, false, "The game exited after Wand recorded a successful trainer command, but no active connection is reported.", ownedBootstrapGame);
                    }
                    store.Log("The exact game process ended or changed before trainer attachment was verified for PID " + existing.Id + ". No current connection is reported. " + existingTrainerEvidence.Detail);
                    return Result(null, false, "The game process ended before a current Wand connection was verified. No active connection is reported. " + existingTrainerEvidence.Detail, ownedBootstrapGame);
                }
session.Transition(WandSessionStatus.GameRunningConnectionUnconfirmed, existingTrainerEvidence.Detail, existingTrainerEvidence.Source, existingTrainerEvidence.ObservedUtc);
                // Re-click on a running game: send the exact-game URI once so Wand
                // attaches the running game's trainer, then re-check for fresh
                // evidence instead of silently observing.
                store.Log("Sending one exact-game URI to attach the running game's trainer: " + selectedExecutable);
                try { Process.Start(new ProcessStartInfo(BuildProtocolUri(target.TitleId, target.GameId)) { UseShellExecute = true }); }
                catch (Exception ex) { store.Log("Could not send the exact-game URI: " + ex.Message); }
                var attachStarted = DateTime.UtcNow;
                var attachEvidence = await WaitForConnectionEvidenceAsync(wandPath, target.GameId, trackedExecutable, existing,
                    session.Read().ProcessCreationFileTime!, attachStarted, TimeSpan.FromSeconds(15), cancellation);
                if (attachEvidence.Confirmed)
                {
                    session.Transition(WandSessionStatus.Connected, attachEvidence.Detail, attachEvidence.Source, attachEvidence.ObservedUtc);
                    return Result(existing, true, "Wand connected to the running " + label + " after the exact-game attach request.", ownedBootstrapGame);
                }
                store.Log("Wand trainer attachment was not confirmed after the exact-game URI for PID " + existing.Id + "; the game was left running. " + attachEvidence.Detail);
                return Result(existing, true, "The game is running. " + attachEvidence.Detail + " The exact-game URI was sent once; the game was left running.", ownedBootstrapGame);
            }

            session.Transition(WandSessionStatus.StartingGame, "Starting the exact registered game with one verified Wand Play attempt.");
            var dispatch = await WandNewGameDispatchCoordinator.DispatchOnceAsync(
                () => ExistingProcessIds(trackedExecutable),
                () => FindExactProcess(trackedExecutable),
                async (_, _) =>
                {
                    store.Log("Trying one exact-target Wand CDP Play dispatch for " + selectedExecutable + ".");
                    return await TryLaunchViaCdpAsync(target, selectedExecutable, store, cancellation);
                },
                () => InspectProtocolPreflightAsync(target.GameId, store, cancellation),
                (_, _) =>
                {
                    store.Log("CDP did not confirm a Play dispatch; sending one exact-game protocol URI.");
                    Process.Start(new ProcessStartInfo(BuildProtocolUri(target.TitleId, target.GameId)) { UseShellExecute = true });
                    return Task.CompletedTask;
                },
                (observed, timeout) => WaitForNewGameAsync(trackedExecutable, observed, timeout, cancellation),
                TimeSpan.FromSeconds(20), cancellation);

            if (dispatch.Blocked)
            {
                string block = dispatch.BlockedPreflight != null
                    ? DescribeProtocolPreflightBlock(dispatch.BlockedPreflight, target.GameId)
                    : "Wand blocked the single launch attempt before dispatch: " + dispatch.Reason + ".";
                ReleaseProcessHandleWithoutTermination(ownedBootstrap);
                ownedBootstrap = null;
                session.Transition(WandSessionStatus.GameFailedToStart, block);
                store.Log(block + " No second launch action was sent.");
                return Result(null, false, block);
            }

            if (dispatch.Process != null && !dispatch.DispatchAttempted)
            {
                var appearedProcess = dispatch.Process;
                session.TrackProcess(appearedProcess, "The exact game appeared before this request sent a launch action; it was left running.");
                session.Transition(WandSessionStatus.GameRunningConnectionUnconfirmed,
                    "The exact game appeared before the one launch action could be sent. No Play or URI action was sent; trainer attachment is not confirmed.");
                store.Log("The exact game process PID " + appearedProcess.Id + " appeared before launch dispatch. It was left running; no Play or URI action was sent.");
                return Result(appearedProcess, false,
                    "The exact game appeared before a launch action was sent. It was left running; no duplicate launch was sent and trainer attachment is not confirmed.");
            }

            var process = dispatch.Process;
            var launchStarted = dispatch.DispatchStartedUtc;
            if (process == null)
            {
                ReleaseProcessHandleWithoutTermination(ownedBootstrap);
                ownedBootstrap = null;
                string message = dispatch.OutcomeUnknown
                    ? "Wand may still be completing the single launch attempt, but no exact game process was observed in the bounded window. No second URI or Play action was sent."
                    : "Wand did not start the exact game. No fallback process was started, and nothing was terminated.";
                store.Log(message + " Selected executable=" + selectedExecutable + "; dispatch=" + dispatch.Reason + ".");
                session.Transition(WandSessionStatus.GameFailedToStart, message);
                return Result(null, dispatch.UsedProtocol, message);
            }

            ownedProtocolGame = process;
            store.Log("The single Wand launch attempt yielded the exact executable PID " + process.Id
                + (dispatch.UsedProtocol ? " through the protocol URI." : " through the CDP Play route."));
            session.TrackProcess(process, "The exact process from the single Wand launch attempt was observed.");
            session.Transition(WandSessionStatus.WaitingForGame, "Waiting for the exact Wand-launched process to expose a usable window.");
            bool launchedWindowUsable = await WaitForUsableGameWindowAsync(process, TimeSpan.FromSeconds(20), cancellation);
            if (!launchedWindowUsable) store.Log("The exact Wand-launched process did not expose a responsive window within 20 seconds.");
            session.TrackProcess(process, "The exact Wand-launched process and current creation stamp were verified.");
            session.Transition(WandSessionStatus.Attaching, "Waiting for fresh trainer-session evidence for the exact process.");
            var launchedTrainerEvidence = await WaitForConnectionEvidenceAsync(wandPath, target.GameId, trackedExecutable, process,
                session.Read().ProcessCreationFileTime!, launchStarted, TimeSpan.FromSeconds(30), cancellation);
            if (launchedTrainerEvidence.Confirmed)
            {
                session.Transition(WandSessionStatus.Connected, launchedTrainerEvidence.Detail, launchedTrainerEvidence.Source, launchedTrainerEvidence.ObservedUtc);
                ownedProtocolGame = null;
                return Result(process, dispatch.UsedProtocol, "Wand connected to " + label + ". Tracking the exact game process.", ownsProcess: true);
            }
            if (StatusForTrainerEvidence(launchedTrainerEvidence) == WandSessionStatus.Disconnected)
            {
                session.Transition(WandSessionStatus.Disconnected, launchedTrainerEvidence.Detail, launchedTrainerEvidence.Source, launchedTrainerEvidence.ObservedUtc);
                ownedProtocolGame = null;
                if (IsHistoricalTrainerEvidence(launchedTrainerEvidence))
                {
                    store.Log("The exact Wand-launched game exited after Tophat recorded a successful trainer command for PID " + process.Id + ". The trace is historical evidence only; no active connection is reported.");
                    return Result(null, dispatch.UsedProtocol, "The game started and exited. Wand recorded a successful trainer command before exit, but the game is no longer running; no active connection is reported.", ownsProcess: false);
                }
                store.Log("The exact Wand-launched game process ended or changed before trainer attachment was verified for PID " + process.Id + ". No current connection is reported. " + launchedTrainerEvidence.Detail);
                return Result(null, dispatch.UsedProtocol, "The game process ended before Wand attachment could be verified. No current connection is reported. " + launchedTrainerEvidence.Detail, ownsProcess: false);
            }
            session.Transition(WandSessionStatus.GameRunningConnectionUnconfirmed, launchedTrainerEvidence.Detail, launchedTrainerEvidence.Source, launchedTrainerEvidence.ObservedUtc);
            ownedProtocolGame = null;
            store.Log("Wand started " + selectedExecutable + " without fresh trainer-session evidence; the exact process was deliberately left running and will remain tracked. " + launchedTrainerEvidence.Detail);
            return Result(process, dispatch.UsedProtocol, "The game started through Wand. " + launchedTrainerEvidence.Detail + " The game was left running and is being tracked; only Exit will close it.", ownsProcess: true);
        }
        catch (OperationCanceledException)
        {
            ReleaseLaunchHandles("cancellation");
            session.Transition(WandSessionStatus.Cancelled, "The user cancelled the Wand launch; any already-running game was left in place.");
            store.Log("Wand launch cancelled; running processes were left untouched.");
            throw;
        }
        catch (Exception ex)
        {
            if (IsProcessRunning(ownedProtocolGame))
            {
                var process = ownedProtocolGame!;
                ownedProtocolGame = null;
                if (existing != null) { ReleaseProcessHandleWithoutTermination(existing); existing = null; }
                if (ownedBootstrap != null) { ReleaseProcessHandleWithoutTermination(ownedBootstrap); ownedBootstrap = null; }
                store.Log("Wand protocol launch reported an error after the exact game started; the game was left running: " + ex.Message);
                session.TrackProcess(process, "The exact game is running after a Wand launch error.");
                session.Transition(WandSessionStatus.GameRunningConnectionUnconfirmed, "The exact game remains running, but trainer attachment could not be verified: " + ex.Message);
                return Result(process, true, "The game is running even though Wand reported an error. It was left running and is being tracked; only Exit will close it.", ownsProcess: true);
            }
            if (IsProcessRunning(existing))
            {
                var process = existing!;
                existing = null;
                if (ownedBootstrap != null) { ReleaseProcessHandleWithoutTermination(ownedBootstrap); ownedBootstrap = null; }
                store.Log("Wand protocol launch reported an error while the exact game was running; the game was left running: " + ex.Message);
                session.TrackProcess(process, "The exact pre-existing game remains running after a Wand launch error.");
                if (session.Read().Status == WandSessionStatus.StartingWand)
                    session.Transition(WandSessionStatus.WaitingForGame, "The exact game is still running after Wand reported an error.");
                session.Transition(WandSessionStatus.GameRunningConnectionUnconfirmed, "The game remains running, but trainer attachment could not be verified: " + ex.Message);
                return Result(process, true, "The game is running even though Wand reported an error. It was left running and is being tracked; only Exit will close it.", ownedBootstrapGame);
            }
            ReleaseLaunchHandles("a failed handoff");
            store.Log("Wand protocol launch failed before an exact game process was observed: " + ex.Message);
            if (session.Read().Status is not (WandSessionStatus.RegistrationInvalid or WandSessionStatus.UnsupportedBuild or WandSessionStatus.Cancelled))
                session.Transition(WandSessionStatus.ConnectionFailed, "Wand launch failed before the exact game process was observed: " + ex.Message);
            return Result(null, false, "Wand protocol could not be sent and no exact game process was found. Nothing was terminated; open Wand and retry.");
        }
    }

    private static WandLaunchResult BuildLaunchResult(Process? process, bool usedProtocol, string message, bool ownsProcess, WandSessionState session, string wandPath)
    {
        var snapshot = session.Read();
        if (process != null && snapshot.ProcessId.HasValue)
            StartSessionEvidenceMonitor(snapshot, wandPath);
        return new WandLaunchResult(process, usedProtocol, message, ownsProcess, snapshot);
    }

    private static void StartSessionEvidenceMonitor(WandSessionSnapshot initial, string wandPath)
    {
        if (initial.ProcessId is null || string.IsNullOrWhiteSpace(initial.ProcessCreationFileTime)) return;
        string monitorKey = SessionMonitorKey(initial);
        if (SessionMonitors.TryRemove(monitorKey, out var previous)) previous.Cancel();
        var cancellation = new CancellationTokenSource();
        if (!SessionMonitors.TryAdd(monitorKey, cancellation))
        {
            cancellation.Dispose();
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellation.Token).ConfigureAwait(false);
                    if (!WandSessionState.TryGet(initial.CanonicalGameId, initial.InstallationPath, out var current)
                        || current.OperationId != initial.OperationId
                        || current.ProcessId is null
                        || current.Status is WandSessionStatus.Disconnected or WandSessionStatus.Cancelled or WandSessionStatus.GameFailedToStart)
                        return;

                    // Keep checking the exact process identity even when this Wand build has
                    // no trainer-session schema. Never poll the unsupported source repeatedly.
                    if (current.EvidenceSource == "unsupported-wand-session-schema")
                    {
                        if (TryMarkSessionDisconnectedIfExited(current, out _)) return;
                        continue;
                    }

                    TryRefreshSessionEvidence(current.CanonicalGameId, current.InstallationPath, wandPath, out var refreshed);
                    if (refreshed?.Status == WandSessionStatus.Disconnected) return;
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex)
            {
                // Monitoring is best-effort and must never terminate or interrupt the game.
                try
                {
                    if (WandSessionState.Find(initial.CanonicalGameId, initial.InstallationPath) is { } state)
                    {
                        var snapshot = state.Read();
                        if (snapshot.Status is WandSessionStatus.Connected or WandSessionStatus.GameRunningConnectionUnconfirmed)
                            state.Transition(snapshot.Status, "Background Wand evidence monitoring is temporarily unavailable: " + ex.Message, snapshot.EvidenceSource, snapshot.EvidenceUtc);
                    }
                }
                catch { }
            }
            finally
            {
                ((ICollection<KeyValuePair<string, CancellationTokenSource>>)SessionMonitors)
                    .Remove(new KeyValuePair<string, CancellationTokenSource>(monitorKey, cancellation));
                cancellation.Dispose();
            }
        });
    }

    private static string SessionMonitorKey(WandSessionSnapshot snapshot) =>
        snapshot.CanonicalGameId + "\n" + Path.GetFullPath(snapshot.InstallationPath);

    private static bool TryMarkSessionDisconnectedIfExited(WandSessionSnapshot snapshot, out WandSessionSnapshot? updated)
    {
        updated = null;
        if (snapshot.ProcessId is not int pid || string.IsNullOrWhiteSpace(snapshot.ProcessCreationFileTime)) return true;
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Refresh();
            if (process.HasExited)
            {
                var exitedState = WandSessionState.Find(snapshot.CanonicalGameId, snapshot.InstallationPath);
                if (exitedState == null || exitedState.Read().OperationId != snapshot.OperationId) return true;
                updated = exitedState.Transition(WandSessionStatus.Disconnected, "The exact game process exited.", "process-exit");
                return true;
            }
            if (!process.HasExited)
            {
                string creation = process.StartTime.ToUniversalTime().ToFileTimeUtc().ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
                if (string.Equals(creation, snapshot.ProcessCreationFileTime, StringComparison.Ordinal)) return false;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Missing or inaccessible identity is not treated as a verified disconnect.
            if (ex is InvalidOperationException or System.ComponentModel.Win32Exception) return false;
        }
        var state = WandSessionState.Find(snapshot.CanonicalGameId, snapshot.InstallationPath);
        if (state == null || state.Read().OperationId != snapshot.OperationId) return true;
        updated = state.Transition(WandSessionStatus.Disconnected, "The exact game process exited or its PID was reused.", "process-identity");
        return true;
    }

    private static bool IsProcessRunning(Process? process)
    {
        try { return process != null && !process.HasExited; }
        catch { return false; }
    }

    internal static void ReleaseProcessHandleWithoutTermination(Process? process)
    {
        try { process?.Dispose(); } catch { }
    }

    internal static string? ResolveBootstrapExecutable(string executable, string versionPath)
    {
        if (string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(versionPath)) return null;
        string fullPath;
        try { fullPath = Path.GetFullPath(executable); }
        catch (ArgumentException) { return null; }
        string relative = versionPath.Trim().Replace('/', '\\').TrimStart('\\');
        if (relative.Length == 0 || relative.Contains(':', StringComparison.Ordinal)) return null;
        string suffix = "\\" + relative;
        if (!fullPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;
        string root = fullPath[..^suffix.Length].TrimEnd('\\');
        if (root.Length == 0) return null;
        string? name = Path.GetFileName(fullPath);
        if (string.IsNullOrWhiteSpace(name)) return null;
        string candidate = Path.Combine(root, name);
        if (string.Equals(candidate, fullPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate)) return null;
        try
        {
            // Only a small same-name executable beside a catalog-resolved nested
            // binary is safe to treat as a launch-context bootstrap. This keeps
            // the resolver's exact nested executable preference intact and avoids
            // guessing among arbitrary installers or launchers.
            if (new FileInfo(candidate).Length > 16 * 1024 * 1024) return null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        return candidate;
    }

    private static async Task<string?> EnsureCustomInstallationAsync(WandTarget target, string executable, LibraryStore store, CancellationToken cancellation)
    {
        WandCustomInstallationRequest request;
        try
        {
            request = BuildCustomInstallationRequest(target.GameId, executable);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            store.Log("Wand exact-install registration rejected the executable: " + ex.Message);
            return "the exact executable path is invalid or unavailable.";
        }

        string? bridge = FindCustomInstallationBridge();
        if (bridge == null)
        {
            store.Log("Wand exact-install bridge was not found; refusing a path-ambiguous protocol launch.");
            return "the native Wand registration bridge is not installed.";
        }

        string node = FindNodeExecutable();
        var startInfo = new ProcessStartInfo(node)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(bridge)!
        };
        startInfo.ArgumentList.Add(bridge);
        startInfo.ArgumentList.Add(request.GameId);
        startInfo.ArgumentList.Add(request.ExecutablePath);

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null) return "the native Wand registration bridge could not be started.";
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(cancellation);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                await standardOutput;
                await standardError;
                throw;
            }

            await standardOutput;
            await standardError;
            if (process.ExitCode != 0)
            {
                store.Log("Wand exact-install bridge exited with code " + process.ExitCode + "; the exact mapping was not confirmed.");
                return "the exact Wand installation mapping was not confirmed.";
            }

            store.Log("Wand exact-install mapping confirmed for gameId=" + request.GameId + "; executable=" + request.ExecutablePath + "; workingDirectory=" + request.WorkingDirectory + ".");
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or FileNotFoundException)
        {
            store.Log("Wand exact-install bridge could not run: " + ex.Message);
            return "the native Wand registration bridge could not run.";
        }
    }

    private static string? FindCustomInstallationBridge()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "wemod_add_custom_install.js"),
            Path.Combine(AppContext.BaseDirectory, "wemod_add_custom_install.js"),
            string.IsNullOrWhiteSpace(userProfile) ? string.Empty : Path.Combine(userProfile, ".codex", "skills", "mods", "scripts", "wemod_add_custom_install.js")
        };
        return candidates.FirstOrDefault(path => path.Length > 0 && File.Exists(path));
    }

    private static Task<WandProtocolPreflight> InspectProtocolPreflightAsync(string requestedGameId, LibraryStore store, CancellationToken cancellation) =>
        WandNewGameDispatchCoordinator.RetryReadOnlyPreflightAsync(
            () => InspectProtocolPreflightOnceAsync(requestedGameId, store, cancellation),
            () => Task.Delay(TimeSpan.FromMilliseconds(350), cancellation),
            cancellation);

    private static async Task<WandProtocolPreflight> InspectProtocolPreflightOnceAsync(string requestedGameId, LibraryStore store, CancellationToken cancellation)
    {
        const string unavailable = "trainer-state-unavailable-before-navigation";
        string bridge = Path.Combine(AppContext.BaseDirectory, "tools", "wand_cdp_launch.js");
        if (!File.Exists(bridge)) return new WandProtocolPreflight(false, false, unavailable, null, null);
        var startInfo = new ProcessStartInfo(FindNodeExecutable())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(bridge)!
        };
        startInfo.ArgumentList.Add(bridge);
        startInfo.ArgumentList.Add("--preflight");
        try
        {
            using var helper = Process.Start(startInfo);
            if (helper == null) return new WandProtocolPreflight(false, false, unavailable, null, null);
            var standardOutput = helper.StandardOutput.ReadToEndAsync();
            var standardError = helper.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try { await helper.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                try { if (!helper.HasExited) helper.Kill(entireProcessTree: true); } catch { }
                store.Log("Wand trainer preflight timed out before protocol dispatch; no game URI was sent.");
                return new WandProtocolPreflight(false, false, unavailable, null, null);
            }
            catch (OperationCanceledException)
            {
                try { if (!helper.HasExited) helper.Kill(entireProcessTree: true); } catch { }
                throw;
            }
            string output = await standardOutput;
            string error = await standardError;
            var preflight = InterpretProtocolPreflight(output, helper.ExitCode);
            store.Log(preflight.SafeToDispatch
                ? "Wand preflight found no busy trainer in the read-only sidebar snapshot; the snapshot does not establish trainer attachment."
                : preflight.TrainerBusy
                    ? DescribeProtocolPreflightBlock(preflight, requestedGameId)
                    : "Wand preflight could not verify trainer state; the protocol launch remains blocked." + (error.Length == 0 ? string.Empty : " " + error.Trim()));
            return preflight;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            store.Log("Wand trainer preflight could not run before protocol dispatch: " + ex.Message);
            return new WandProtocolPreflight(false, false, unavailable, null, null);
        }
    }

    private static async Task<WandCdpLaunchResult> TryLaunchViaCdpAsync(WandTarget target, string executablePath, LibraryStore store, CancellationToken cancellation)
    {
        string bridge = Path.Combine(AppContext.BaseDirectory, "tools", "wand_cdp_launch.js");
        if (!File.Exists(bridge))
        {
            store.Log("Bundled Wand CDP launch helper is unavailable; a fresh trainer preflight may permit one protocol fallback.");
            return new WandCdpLaunchResult(WandCdpLaunchDisposition.KnownNoDispatch, false, "helper-unavailable");
        }
        var startInfo = new ProcessStartInfo(FindNodeExecutable())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(bridge)!
        };
        startInfo.ArgumentList.Add(bridge);
        startInfo.ArgumentList.Add(target.TitleId);
        startInfo.ArgumentList.Add(target.GameId);
        startInfo.ArgumentList.Add(target.TitleName);
        startInfo.ArgumentList.Add(executablePath);
        bool helperStarted = false;
        try
        {
            using var helper = Process.Start(startInfo);
            if (helper == null) return new WandCdpLaunchResult(WandCdpLaunchDisposition.KnownNoDispatch, false, "helper-did-not-start");
            helperStarted = true;
            var standardOutput = helper.StandardOutput.ReadToEndAsync();
            var standardError = helper.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(13));
            try { await helper.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                try { if (!helper.HasExited) helper.Kill(entireProcessTree: true); } catch { }
                store.Log("Wand CDP launch helper timed out; its route outcome is unknown, so only exact-process observation will continue.");
                return new WandCdpLaunchResult(WandCdpLaunchDisposition.Unknown, true, "helper-timeout");
            }
            catch (OperationCanceledException)
            {
                try { if (!helper.HasExited) helper.Kill(entireProcessTree: true); } catch { }
                throw;
            }
            string output = await standardOutput;
            string error = await standardError;
            var launch = InterpretCdpLaunchResult(output, helper.ExitCode);
            store.Log(launch.Disposition == WandCdpLaunchDisposition.PlayDispatched
                ? "Wand local CDP verified the exact registration and dispatched its Play control."
                : launch.Disposition == WandCdpLaunchDisposition.SafetyBlocked
                    ? "Wand local CDP refused Play because the exact trainer or informational-notes gate was ambiguous or unsafe: " + launch.Reason + "."
                    : launch.Disposition == WandCdpLaunchDisposition.KnownNoDispatch
                        ? "Wand CDP proved that it did not route or dispatch Play; the protocol URI fallback still requires a fresh idle preflight."
                        : "Wand local CDP launch outcome is unknown; no second launch action will be sent." + (error.Length == 0 ? string.Empty : " " + error.Trim()));
            return launch;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            if (helperStarted)
            {
                store.Log("Wand local CDP helper failed after starting; its route outcome is unknown, so a second launch action is blocked: " + ex.Message);
                return new WandCdpLaunchResult(WandCdpLaunchDisposition.Unknown, true, "helper-execution-outcome-unknown");
            }
            store.Log("Wand local CDP launch helper could not start; a fresh trainer preflight may permit one protocol fallback: " + ex.Message);
            return new WandCdpLaunchResult(WandCdpLaunchDisposition.KnownNoDispatch, false, "helper-execution-failed");
        }
    }

    private static string FindNodeExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "node", "node.exe"),
            Path.Combine(AppContext.BaseDirectory, "node.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nodejs", "node.exe")
        };
        return candidates.FirstOrDefault(File.Exists) ?? "node.exe";
    }

    private static Process? FindExactProcess(string executable)
    {
        string name = Path.GetFileNameWithoutExtension(executable);
        if (name.Length == 0) return null;
        Process[] processes;
        try { processes = Process.GetProcessesByName(name); }
        catch { return null; }
        foreach (var process in processes)
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) return process;
            }
            catch { }
            try { process.Dispose(); } catch { }
        }
        return null;
    }

    internal static Process? FindRunningExactProcess(string executable) => FindExactProcess(ResolveTrackedExecutable(executable));

    // Called only by the user's explicit "Exit game + Wand" button. Normal
    // launch, recovery, and play-session tracking never use this method.
    internal static int ForceCloseRunningClient(LibraryStore store)
    {
        int stopped = 0;
        Process[] processes;
        try { processes = Process.GetProcessesByName("Wand"); }
        catch { return 0; }
        foreach (var process in processes)
        {
            try
            {
                if (process.HasExited) continue;
                process.Kill(entireProcessTree: true);
                stopped++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
            {
                try { store.Log("Could not force-close a Wand process after the user requested exit: " + ex.Message); } catch { }
            }
            finally { try { process.Dispose(); } catch { } }
        }
        return stopped;
    }

private static async Task<bool> EnsureWandStartedAsync(string wandPath, LibraryStore store, CancellationToken cancellation, bool restartForCdpDispatch)
    {
        if (IsWandRunning())
        {
            // A visible client window can predate the protocol listener (for
            // example after Wand's helper processes have just respawned). Keep
            // the same warm-up for an already-running client before sending a
            // play URI.
            store.Log("Wand client was already visible; verifying its loopback CDP recovery endpoint.");
            await Task.Delay(TimeSpan.FromSeconds(2), cancellation);
            if (await IsWandCdpReachableAsync(cancellation))
            {
                store.Log("Already-running Wand exposes its loopback CDP recovery endpoint; readiness confirmed.");
                return true;
            }
            // Wand is running but does not expose the CDP endpoint the bundled
            // protocol helpers require (it was started without the recovery
            // flag). A read-only preflight can never succeed against it, so
            // every Play request would fail closed even though Wand is open.
            // When the selected game is not already running, restart Wand with
            // the loopback-only debug flags so trainer state can be read and
            // the one-shot dispatch can run. A running game is never disrupted.
            if (!restartForCdpDispatch)
            {
                store.Log("Already-running Wand lacks the CDP recovery endpoint and the selected game is already running; Wand was not restarted and the game was left untouched.");
                return false;
            }
            store.Log("Already-running Wand lacks the CDP recovery endpoint; restarting Wand with the loopback-only recovery flags so trainer state can be verified.");
            CloseRunningClientProcesses();
            await Task.Delay(TimeSpan.FromMilliseconds(750), cancellation);
        }
        if (string.IsNullOrWhiteSpace(wandPath) || !File.Exists(wandPath))
        {
            store.Log("Wand executable was not found; refusing an unmodified Play with Wand launch.");
            return false;
        }
try
        {
            store.Log("Starting Wand client " + wandPath + " with a loopback-only recovery endpoint.");
            var startInfo = new ProcessStartInfo(wandPath) { WorkingDirectory = Path.GetDirectoryName(wandPath), UseShellExecute = true };
            startInfo.ArgumentList.Add("--remote-debugging-port=9222");
            startInfo.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
            Process.Start(startInfo);
            var deadline = DateTime.UtcNow + WandStartupWindow;
            var nextProgressLog = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                if (await IsWandCdpReachableAsync(cancellation))
                {
                    store.Log("Wand exposed its loopback CDP recovery endpoint; readiness confirmed.");
                    return true;
                }
                if (IsWandRunning())
                {
                    // A visible window appears before the protocol listener finishes
                    // loading. Keep waiting for the CDP endpoint so the read-only
                    // preflight and one-shot dispatch can actually run.
                    store.Log("Wand client exposed a visible window; waiting for its loopback CDP recovery endpoint.");
                }
                if (DateTime.UtcNow >= nextProgressLog)
                {
                    store.Log("Wand startup is still pending; Game Library remains responsive while it waits.");
                    nextProgressLog = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                }
                await Task.Delay(250, cancellation);
            }
            store.Log("Wand did not expose its loopback CDP recovery endpoint within the three-minute startup window.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or UriFormatException)
        {
            store.Log("Wand could not be opened automatically: " + ex.Message);
        }
        return IsWandRunning();
    }

private static bool IsWandRunning()
    {
        foreach (var name in new[] { "Wand", "WeMod" })
        {
            try
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    try
                    {
                        process.Refresh();
                        // The launcher creates several background helpers before the
                        // desktop client is ready to receive wemod://play. Treat only
                        // a responsive visible client window as ready.
                        if (process.MainWindowHandle != IntPtr.Zero && process.Responding) return true;
                    }
                    catch (InvalidOperationException) { }
                    finally { process.Dispose(); }
                }
            }
            catch (InvalidOperationException) { }
        }
        return false;
    }

    // The bundled Wand protocol helpers require a loopback CDP listener at
    // 127.0.0.1:9222. Wand only opens it when launched with the recovery flags;
    // an already-running client started without them cannot be inspected at
    // all. This probe distinguishes "Wand is open but unreadable" from "Wand
    // is ready", so Play can restart Wand once instead of failing closed.
    private static async Task<bool> IsWandCdpReachableAsync(CancellationToken cancellation)
    {
        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };
            using var response = await probe.GetAsync("http://127.0.0.1:9222/json/version", cancellation);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AggregateException or IOException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    // Closes only the Wand/WeMod desktop client and its helper processes so a
    // fresh client can be started with the loopback-only CDP recovery flags.
    // Game processes are never touched here.
    private static void CloseRunningClientProcesses()
    {
        foreach (var name in new[] { "Wand", "WeMod" })
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); }
            catch { continue; }
            foreach (var process in processes)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { }
                finally { try { process.Dispose(); } catch { } }
            }
        }
    }

    private static HashSet<int> ExistingProcessIds(string executable)
    {
        var ids = new HashSet<int>();
        string name = Path.GetFileNameWithoutExtension(executable);
        if (name.Length == 0) return ids;
        foreach (var process in Process.GetProcessesByName(name))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                    ids.Add(process.Id);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            finally { process.Dispose(); }
        }
        return ids;
    }

    private static async Task<Process?> WaitForNewGameAsync(string executable, HashSet<int> observed, TimeSpan timeout, CancellationToken cancellation)
    {
        string name = Path.GetFileNameWithoutExtension(executable);
        if (name.Length == 0) return null;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var process = FindNewExactProcess(executable, observed);
            if (process != null) return process;
            try
            {
                await Task.Delay(200, cancellation);
            }
            catch (OperationCanceledException)
            {
                // A process can appear between the last poll and cancellation.
                // Capture it before propagating cancellation so the caller can
                // clean up the exact process it owns.
                process = FindNewExactProcess(executable, observed);
                if (process != null) return process;
                throw;
            }
        }
        return null;
    }

    private static Process? FindNewExactProcess(string executable, HashSet<int> observed)
    {
        string name = Path.GetFileNameWithoutExtension(executable);
        if (name.Length == 0) return null;
        foreach (var process in Process.GetProcessesByName(name))
        {
            bool keep = false;
            try
            {
                if (!observed.Contains(process.Id) && string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                {
                    keep = true;
                    return process;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            finally
            {
                if (!keep) try { process.Dispose(); } catch { }
            }
        }
        return null;
    }

    internal static bool ContainsConnectionEvidence(string log, int? expectedPid = null)
    {
        // This parser is retained for diagnostics and regression fixtures only.
        // An overlay IPC/hook log is not evidence that the trainer loaded.
        if (string.IsNullOrWhiteSpace(log)) return false;
        if (!expectedPid.HasValue)
            return log.Contains("ipc connected", StringComparison.OrdinalIgnoreCase)
                && (log.Contains("hooked: true", StringComparison.OrdinalIgnoreCase)
                    || log.Contains("hook res: true", StringComparison.OrdinalIgnoreCase));

        // Overlay logs are append-only and can contain several game sessions.
        // Require both markers to belong to the same exact game PID; checking
        // for the PID anywhere in the file can combine a stale hook with a new
        // game's unrelated log entry and falsely report a connected session.
        string marker = "[" + expectedPid.Value + ":";
        var pidLines = log.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains(marker, StringComparison.Ordinal));
        return pidLines.Any(line => line.Contains("ipc connected", StringComparison.OrdinalIgnoreCase))
            && pidLines.Any(line => line.Contains("hooked: true", StringComparison.OrdinalIgnoreCase)
                || line.Contains("hook res: true", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<WandTrainerEvidence> WaitForConnectionEvidenceAsync(string wandExecutable, string gameId,
        string executable, Process expectedProcess, string processCreationFileTime, DateTime sinceUtc,
        TimeSpan timeout, CancellationToken cancellation)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(processCreationFileTime, "^[0-9A-Fa-f]{16}$"))
            return new WandTrainerEvidence(false, "game-identity-unavailable", DateTime.UtcNow,
                "The exact game process creation stamp was unavailable for the trainer evidence probe.");

        var deadline = DateTime.UtcNow + timeout;
        WandTrainerEvidence last = WandTrainerEvidenceAdapter.Inspect(wandExecutable, gameId, expectedProcess.Id,
            processCreationFileTime, sinceUtc, executable);
        if (last.Confirmed || last.Source == "trainer-connected-before-exit") return last;
        if (last.Source == "unsupported-wand-session-schema") return last;
        while (DateTime.UtcNow < deadline)
        {
            cancellation.ThrowIfCancellationRequested();
            using var current = FindExactProcess(executable);
            if (current == null || current.Id != expectedProcess.Id)
            {
                var final = WandTrainerEvidenceAdapter.Inspect(wandExecutable, gameId, expectedProcess.Id,
                    processCreationFileTime, sinceUtc, executable);
                return final.Source == "trainer-connected-before-exit" ? final : new WandTrainerEvidence(false,
                    "game-process-changed", DateTime.UtcNow,
                    "The exact game process exited or no longer matches its registered executable before trainer attachment was verified. " + final.Detail);
            }
            string currentCreation;
            try { currentCreation = current.StartTime.ToUniversalTime().ToFileTimeUtc().ToString("X16", System.Globalization.CultureInfo.InvariantCulture); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                var final = WandTrainerEvidenceAdapter.Inspect(wandExecutable, gameId, expectedProcess.Id,
                    processCreationFileTime, sinceUtc, executable);
                return final.Source == "trainer-connected-before-exit" ? final : new WandTrainerEvidence(false,
                    "game-identity-unavailable", DateTime.UtcNow, "The game process creation time could not be read: " + ex.Message);
            }
            if (!string.Equals(currentCreation, processCreationFileTime, StringComparison.OrdinalIgnoreCase))
            {
                var final = WandTrainerEvidenceAdapter.Inspect(wandExecutable, gameId, expectedProcess.Id,
                    processCreationFileTime, sinceUtc, executable);
                return final.Source == "trainer-connected-before-exit" ? final : new WandTrainerEvidence(false,
                    "game-pid-reused", DateTime.UtcNow, "The observed PID now belongs to a different process creation identity.");
            }
            last = WandTrainerEvidenceAdapter.Inspect(wandExecutable, gameId, expectedProcess.Id,
                processCreationFileTime, sinceUtc, executable);
            if (last.Confirmed && last.ObservedUtc >= sinceUtc) return last;
            if (last.Source == "trainer-connected-before-exit") return last;
            if (last.Source == "unsupported-wand-session-schema") return last;
            // The version-pinned trainer probe is a short local CDP request.
            // Sampling at a bounded cadence avoids repeatedly spawning Node
            // while Wand is still starting its trainer.
            await Task.Delay(TimeSpan.FromSeconds(2), cancellation);
        }
        return last with { Detail = "No fresh trainer-session evidence arrived within " + timeout.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " seconds. " + last.Detail };
    }

    private static async Task<bool> WaitForUsableGameWindowAsync(Process process, TimeSpan timeout, CancellationToken cancellation)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                process.Refresh();
                if (process.HasExited) return false;
                IntPtr hwnd = process.MainWindowHandle;
                if (hwnd != IntPtr.Zero && IsWindow(hwnd) && IsWindowVisible(hwnd) && !IsHungAppWindow(hwnd)) return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            await Task.Delay(200, cancellation);
        }
        return false;
    }

    private static string ReadWandVersion(string wandExecutable)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(wandExecutable);
            return !string.IsNullOrWhiteSpace(version.ProductVersion) ? version.ProductVersion
                : !string.IsNullOrWhiteSpace(version.FileVersion) ? version.FileVersion : "unknown";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return "unknown"; }
    }

    internal static bool TryRefreshSessionEvidence(string canonicalGameId, string installationPath, string wandExecutable, out WandSessionSnapshot? updated)
    {
        updated = null;
        if (!WandSessionState.TryGet(canonicalGameId, installationPath, out var snapshot)
            || snapshot.ProcessId is not int pid || string.IsNullOrWhiteSpace(snapshot.ProcessCreationFileTime)) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Refresh();
            if (process.HasExited)
            {
                var exitedState = WandSessionState.Find(canonicalGameId, installationPath);
                if (exitedState == null || exitedState.Read().OperationId != snapshot.OperationId) return false;
                var finalEvidence = WandTrainerEvidenceAdapter.Inspect(wandExecutable, snapshot.GameId, pid,
                    snapshot.ProcessCreationFileTime!, snapshot.StartedUtc, snapshot.InstallationPath);
                bool trainerWasObserved = finalEvidence.Source == "trainer-connected-before-exit";
                updated = exitedState.Transition(WandSessionStatus.Disconnected,
                    trainerWasObserved ? finalEvidence.Detail : "The exact game process exited.",
                    trainerWasObserved ? finalEvidence.Source : "process-exit",
                    trainerWasObserved ? finalEvidence.ObservedUtc : DateTime.UtcNow);
                return true;
            }
            string creation = process.StartTime.ToUniversalTime().ToFileTimeUtc().ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
            if (!string.Equals(creation, snapshot.ProcessCreationFileTime, StringComparison.Ordinal))
            {
                var state = WandSessionState.Find(canonicalGameId, installationPath);
                if (state == null || state.Read().OperationId != snapshot.OperationId) return false;
                var finalEvidence = WandTrainerEvidenceAdapter.Inspect(wandExecutable, snapshot.GameId, pid,
                    snapshot.ProcessCreationFileTime!, snapshot.StartedUtc, snapshot.InstallationPath);
                bool trainerWasObserved = finalEvidence.Source == "trainer-connected-before-exit";
                updated = state.Transition(WandSessionStatus.Disconnected,
                    trainerWasObserved ? finalEvidence.Detail : "The tracked PID was reused by a different process.",
                    trainerWasObserved ? finalEvidence.Source : "process-identity",
                    trainerWasObserved ? finalEvidence.ObservedUtc : DateTime.UtcNow);
                return updated != null;
            }
            var evidence = WandTrainerEvidenceAdapter.Inspect(wandExecutable, snapshot.GameId, pid, creation,
                snapshot.StartedUtc, snapshot.InstallationPath);
            var current = WandSessionState.Find(canonicalGameId, installationPath);
            if (current == null || current.Read().OperationId != snapshot.OperationId) return false;
            if (StatusForTrainerEvidence(evidence) == WandSessionStatus.Disconnected)
                updated = current.Transition(WandSessionStatus.Disconnected, evidence.Detail, evidence.Source, evidence.ObservedUtc);
            else if (StatusForTrainerEvidence(evidence) == WandSessionStatus.Connected)
                updated = current.Transition(WandSessionStatus.Connected, evidence.Detail, evidence.Source, evidence.ObservedUtc);
            else if (snapshot.Status == WandSessionStatus.Connected)
                updated = current.Transition(WandSessionStatus.GameRunningConnectionUnconfirmed,
                    evidence.Detail, evidence.Source, evidence.ObservedUtc);
            else
                updated = current.Transition(snapshot.Status, evidence.Detail, evidence.Source, evidence.ObservedUtc);
            return true;
        }
        catch (ArgumentException)
        {
            var state = WandSessionState.Find(canonicalGameId, installationPath);
            if (state == null || state.Read().OperationId != snapshot.OperationId) return false;
            var finalEvidence = WandTrainerEvidenceAdapter.Inspect(wandExecutable, snapshot.GameId, pid,
                snapshot.ProcessCreationFileTime!, snapshot.StartedUtc, snapshot.InstallationPath);
            bool trainerWasObserved = finalEvidence.Source == "trainer-connected-before-exit";
            updated = state.Transition(WandSessionStatus.Disconnected,
                trainerWasObserved ? finalEvidence.Detail : "The tracked game process no longer exists.",
                trainerWasObserved ? finalEvidence.Source : "process-exit",
                trainerWasObserved ? finalEvidence.ObservedUtc : DateTime.UtcNow);
            return updated != null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            var state = WandSessionState.Find(canonicalGameId, installationPath);
            if (state == null) return false;
            var current = state.Read();
            if (current.OperationId != snapshot.OperationId) return false;
            updated = state.Transition(current.Status == WandSessionStatus.Connected
                    ? WandSessionStatus.GameRunningConnectionUnconfirmed : current.Status,
                "Trainer-session status is temporarily unavailable: " + ex.Message,
                "trainer-session-probe-unavailable", DateTime.UtcNow);
            return true;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool IsHungAppWindow(IntPtr hwnd);

    private static IReadOnlyList<string> BuildAliases(Game game, string executable)
    {
        var values = new[]
        {
            game.Name,
            game.Id,
            Path.GetFileNameWithoutExtension(executable)
        };
        return values.Select(Normalize).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static int TitleScore(IReadOnlyList<string> aliases, JsonObject title)
    {
        string name = Normalize(DataJson.Text(title["name"]));
        string slug = Normalize(DataJson.Text(title["slug"]));
        int best = 0;
        foreach (var alias in aliases)
        {
            int score = alias == name ? 1200 : alias == slug ? 1150 : 0;
            if (score == 0 && title["terms"] is JsonArray terms && terms.Any(term => alias == Normalize(DataJson.Text(term)))) score = 1100;
            if (score > best) best = score;
        }
        return best;
    }

    private static int GameScore(string executable, string platform, string versionPath, JsonObject data)
    {
        int score = 0;
        string executableName = Path.GetFileName(executable);
        string versionName = Path.GetFileName(versionPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
        if (executableName.Length > 0 && versionName.Length > 0 && string.Equals(executableName, versionName, StringComparison.OrdinalIgnoreCase)) score += 1000;
        if (string.Equals(platform, "steam", StringComparison.OrdinalIgnoreCase)) score += 100;
        if (data["trainer"] is JsonObject trainer && DataJson.Number(trainer["cheatCount"]) > 0) score += 10;
        return score;
    }

    internal static bool TryRead(string path, out JsonObject catalog)
    {
        catalog = null!;
        try { return TryParse(File.ReadAllText(path), out catalog); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool TryParse(string json, out JsonObject catalog)
    {
        catalog = null!;
        try
        {
            var root = JsonNode.Parse(json)?.AsObject();
            if (root?["titles"] is not JsonObject || root["games"] is not JsonObject) return false;
            catalog = root;
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}

internal static class WandAudit
{
    internal static int Run(string report, string? dataRoot)
    {
        var store = new LibraryStore(dataRoot);
        store.EnsureAssets();
        var state = store.LoadState();
        var games = store.LoadGames(state, store.ReadConfig()).ToArray();
        var registrations = WandIntegration.LoadSupportedGames();
        JsonObject? catalog = null;
        string cachedCatalog = LibraryStore.SafeChild(store.Cache, "wand-catalog.json");
        if (WandIntegration.TryRead(cachedCatalog, out var loaded)) catalog = loaded;
        var rows = new List<object>();
        var usedGameIds = new HashSet<string>(StringComparer.Ordinal);
        int launcherCount = 0, trackedCount = 0, registrationMatches = 0, protocolCandidates = 0, buttonEligibleCount = 0;
        foreach (var registration in registrations)
        {
            var game = MainWindow.ResolveWandRegistrationMatch(games, state, registration, usedGameIds)
                ?? new Game { Id = "wand:" + registration.GameId, Name = registration.Name, IsLocal = true };
            // Production normalizes the selected card to the manifest title.
            // Mirror that exact launch input here without changing persisted data.
            usedGameIds.Add(game.Id);
            game.Name = registration.Name;
            string launcher = registration.Path;
            bool launcherExists = File.Exists(launcher);
            bool registered = WandIntegration.TryGetExistingWandInstallation(launcher, out var savedRegistration)
                && string.Equals(savedRegistration.TitleId, registration.TitleId, StringComparison.Ordinal)
                && string.Equals(savedRegistration.GameId, registration.GameId, StringComparison.Ordinal);
            WandTarget target = null!;
            bool targetResolved = registered && catalog != null && WandIntegration.TryResolveRegisteredTarget(catalog, savedRegistration, out target);
            string trackedExecutable = targetResolved
                ? WandIntegration.ResolveTrackedExecutable(launcher, target.VersionPath)
                : WandIntegration.ResolveTrackedExecutable(launcher);
            bool trackedExists = File.Exists(trackedExecutable);
            bool protocol = registered && targetResolved && trackedExists;
            bool buttonEligible = launcherExists && WandIntegration.CanLaunchExistingWandInstall(game, launcher, store, out _);
            if (launcherExists) launcherCount++;
            if (trackedExists) trackedCount++;
            if (registered) registrationMatches++;
            if (protocol) protocolCandidates++;
            if (buttonEligible) buttonEligibleCount++;
            rows.Add(new
            {
                folder = registration.Folder,
                id = game.Id,
                name = registration.Name,
                launcher,
                trackedExecutable,
                launcherExists,
                trackedExists,
                registrationMatches = registered,
                protocolCandidate = protocol,
                buttonEligible
            });
        }
        bool wand = File.Exists(MainWindow.DetectWandPath() ?? "");
        bool passed = registrations.Count > 0
            && registrations.Select(registration => registration.GameId).Distinct(StringComparer.Ordinal).Count() == registrations.Count
            && launcherCount == registrations.Count
            && trackedCount == registrations.Count
            && registrationMatches == registrations.Count
            && protocolCandidates == registrations.Count
            && buttonEligibleCount == registrations.Count
            && wand;
        var payload = new
        {
            at = DateTime.UtcNow,
            passed,
            launchVerified = false,
            supportedRegistrationCount = registrations.Count,
            filterIncludedCount = registrations.Count,
            executableCount = registrations.Count,
            resolvedCount = launcherCount,
            trackedExecutableCount = trackedCount,
            registrationMatchCount = registrationMatches,
            protocolCandidateCount = protocolCandidates,
            buttonEligibleCount,
            wandExecutable = MainWindow.DetectWandPath(),
            cachedCatalog = catalog != null,
            games = rows
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        File.WriteAllText(Path.GetFullPath(report), System.Text.Json.JsonSerializer.Serialize(payload, DataJson.Options));
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: latest Wand audit; filter {registrations.Count}; launchers {launcherCount}; tracked executables {trackedCount}; exact registrations {registrationMatches}; protocol candidates {protocolCandidates}; button eligible {buttonEligibleCount}; report={Path.GetFullPath(report)}");
        return passed ? 0 : 1;
    }
}
