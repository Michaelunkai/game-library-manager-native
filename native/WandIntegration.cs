using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

internal sealed record WandTarget(string TitleId, string GameId, string TitleName, string Platform, string VersionPath);
internal sealed record WandCustomInstallationRequest(string GameId, string ExecutablePath, string WorkingDirectory, string Sku, string CorrelationId);
internal sealed record WandLaunchResult(Process? Process, bool UsedProtocol, string Message, bool OwnsProcess = false);
internal sealed record WandRegisteredInstallation(string TitleId, string GameId, string ExecutablePath);
internal sealed record WandSupportedGame(string Folder, string TitleId, string GameId, string Name, string Path);

internal static class WandIntegration
{
    private const string CatalogUrl = "https://storage-cdn.wemod.com/catalog.json";
    private const long MaxCatalogBytes = 24 * 1024 * 1024;
    internal static readonly TimeSpan WandStartupWindow = TimeSpan.FromMinutes(3);
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

    internal static string BuildProtocolUri(string titleId, string gameId) =>
        "wemod://play?titleId=" + Uri.EscapeDataString(titleId) + "&gameId=" + Uri.EscapeDataString(gameId);

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
    /// The Wand button is intentionally stricter than catalog support: a game is
    /// eligible only when its exact executable is one of the registrations that
    /// the local Mods/Wand sync maintains.  Missing or unreadable registration
    /// evidence hides the button instead of trying to create a new registration.
    /// </summary>
    internal static bool CanLaunchExistingWandInstall(Game game, string executable, LibraryStore store, out string message) =>
        CanLaunchExistingWandInstall(game, executable, store, RegisteredInstallationManifestPaths(), out message);

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

    internal static bool TryGetExistingWandInstallation(string executable, out WandRegisteredInstallation installation) =>
        TryGetExistingWandInstallation(executable, RegisteredInstallationManifestPaths(), out installation);

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

    internal static IReadOnlyList<WandSupportedGame> LoadSupportedGames() => LoadSupportedGames(RegisteredInstallationManifestPaths());

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
        JsonObject? catalog = null;
        try { catalog = await LoadCatalogAsync(store, cancellation); }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            store.Log("Wand catalog unavailable; no unmodified fallback will be launched: " + ex.Message);
        }

        if (catalog == null)
            return new WandLaunchResult(null, false, "Wand catalog is unavailable. No unmodified game was started; retry after Wand is online.");
        string selectedExecutable = executable;
        if (!TryGetExistingWandInstallation(selectedExecutable, out var registration))
            return new WandLaunchResult(null, false, "This exact executable is not already registered in Wand. No unmodified game was started and no new Wand registration was created.");
        if (!TryResolveRegisteredTarget(catalog, registration, out var target))
            return new WandLaunchResult(null, false, "Wand's catalog no longer contains the exact saved title and game registration. No unmodified game was started.");
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
            if (existing == null)
            {
                string? bootstrap = Path.GetExtension(selectedExecutable).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                    ? ResolveBootstrapExecutable(selectedExecutable, target.VersionPath)
                    : null;
                if (bootstrap != null)
                {
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
                // The bootstrap creates the real process before the window and
                // engine context are stable. Give it the same settling window
                // used by the proven manual route before Wand injects the exact
                // nested PID.
                store.Log("Waiting for the bootstrapped exact game to settle before starting Wand.");
                await Task.Delay(TimeSpan.FromSeconds(8), cancellation);
            }

            bool wandReady = await EnsureWandStartedAsync(wandPath, store, cancellation);
            if (!wandReady)
            {
                if (ownedBootstrap != null) { ReleaseProcessHandleWithoutTermination(ownedBootstrap); ownedBootstrap = null; }
                if (existing != null)
                    return new WandLaunchResult(existing, false, "The game is running, but Wand could not be verified as ready. The game was left running and is being tracked; only Exit will close it.", OwnsProcess: ownedBootstrapGame);
                return new WandLaunchResult(null, false, "Wand could not be verified as running. No game process was found, and no process was terminated.");
            }
            string label = target.TitleName + " (" + target.Platform + ")";
            store.Log("Wand readiness confirmed for " + label + "; titleId=" + target.TitleId + "; gameId=" + target.GameId + ".");

            if (bootstrapContextObserved && existing != null)
            {
                // A wrapper-launched game can expose its exact process before its
                // first usable window/graphics context. The proven manual route
                // waits once more after Wand's listener is ready before sending
                // the URI; keep that barrier bounded and only for this bootstrap
                // path so ordinary already-running games remain responsive.
                store.Log("Waiting for the bootstrapped exact game to expose its usable context before the Wand protocol handoff.");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellation);
            }

            if (existing != null)
            {
                var handoffStarted = DateTime.UtcNow;
                store.Log("Sending one Wand protocol handoff to the already running exact process " + selectedExecutable + ".");
                Process.Start(new ProcessStartInfo(BuildProtocolUri(target.TitleId, target.GameId)) { UseShellExecute = true });
                if (await WaitForConnectionEvidenceAsync(trackedExecutable, existing.Id, handoffStarted, TimeSpan.FromSeconds(30), cancellation))
                {
                    ReleaseProcessHandleWithoutTermination(ownedBootstrap);
                    ownedBootstrap = null;
                    return new WandLaunchResult(existing, true, "Wand connected to the already running " + label + " game.", OwnsProcess: ownedBootstrapGame);
                }
                ReleaseProcessHandleWithoutTermination(ownedBootstrap);
                ownedBootstrap = null;
                store.Log("Wand connection evidence was not confirmed for PID " + existing.Id + "; the game was deliberately left running and will remain tracked.");
                return new WandLaunchResult(existing, true, "The game is running. Wand connection evidence was not confirmed yet, so the game was left running and is being tracked; only Exit will close it.", OwnsProcess: ownedBootstrapGame);
            }
            const int maxAttempts = 2;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (attempt > 1)
                {
                    store.Log("Retrying the exact Wand protocol handoff for " + executable + " (attempt " + attempt + "/" + maxAttempts + ").");
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellation);
                    if (!await EnsureWandStartedAsync(wandPath, store, cancellation)) break;
                }
                var observed = ExistingProcessIds(trackedExecutable);
                var launchStarted = DateTime.UtcNow;
                store.Log("Sending Wand protocol for exact executable " + selectedExecutable + " (attempt " + attempt + "/" + maxAttempts + ").");
                Process.Start(new ProcessStartInfo(BuildProtocolUri(target.TitleId, target.GameId)) { UseShellExecute = true });
                // The URI is the only launch route for Play with Wand. Wait for the exact
                // executable; never start a second unmodified copy when the handoff fails.
                var process = await WaitForNewGameAsync(trackedExecutable, observed, TimeSpan.FromSeconds(20), cancellation);
                if (process == null && attempt == 1 && await TryLaunchViaCdpAsync(target, store, cancellation))
                {
                    // Re-sending the same wemod:// route can be a no-op when
                    // Wand is already displaying this title. A unique in-app
                    // route makes autoLaunch run again without starting an
                    // unmodified fallback or touching another process.
                    observed = ExistingProcessIds(trackedExecutable);
                    launchStarted = DateTime.UtcNow;
                    store.Log("Wand protocol was a no-op; a unique local CDP auto-launch was dispatched for " + selectedExecutable + ".");
                    process = await WaitForNewGameAsync(trackedExecutable, observed, TimeSpan.FromSeconds(20), cancellation);
                }
                ownedProtocolGame = process;
                store.Log(process == null
                    ? "Wand protocol did not yield a new exact executable within the launch window."
                    : "Wand protocol yielded exact executable PID " + process.Id + ".");
                if (process != null && await WaitForConnectionEvidenceAsync(trackedExecutable, process.Id, launchStarted, TimeSpan.FromSeconds(30), cancellation))
                {
                    ownedProtocolGame = null;
                    return new WandLaunchResult(process, true, "Wand connected to " + label + ". Tracking the exact game process.", OwnsProcess: true);
                }
                if (process != null)
                {
                    ownedProtocolGame = null;
                    store.Log("Wand protocol started " + selectedExecutable + " without fresh connection evidence; the exact process was deliberately left running and will remain tracked.");
                    return new WandLaunchResult(process, true, "The game started through Wand. Connection evidence was not confirmed yet, so the game was left running and is being tracked; only Exit will close it.", OwnsProcess: true);
                }
                if (attempt < maxAttempts) continue;
            }
            store.Log("Wand protocol returned without observing " + selectedExecutable + "; no fallback process was started or terminated.");
            return new WandLaunchResult(null, false, "Wand did not start the exact game after bounded retries. No fallback process was started, and nothing was terminated.");
        }
        catch (OperationCanceledException)
        {
            ReleaseLaunchHandles("cancellation");
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
                return new WandLaunchResult(process, true, "The game is running even though Wand reported an error. It was left running and is being tracked; only Exit will close it.", OwnsProcess: true);
            }
            if (IsProcessRunning(existing))
            {
                var process = existing!;
                existing = null;
                if (ownedBootstrap != null) { ReleaseProcessHandleWithoutTermination(ownedBootstrap); ownedBootstrap = null; }
                store.Log("Wand protocol launch reported an error while the exact game was running; the game was left running: " + ex.Message);
                return new WandLaunchResult(process, true, "The game is running even though Wand reported an error. It was left running and is being tracked; only Exit will close it.", OwnsProcess: ownedBootstrapGame);
            }
            ReleaseLaunchHandles("a failed handoff");
            store.Log("Wand protocol launch failed before an exact game process was observed: " + ex.Message);
            return new WandLaunchResult(null, false, "Wand protocol could not be sent and no exact game process was found. Nothing was terminated; open Wand and retry.");
        }
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

    private static async Task<bool> TryLaunchViaCdpAsync(WandTarget target, LibraryStore store, CancellationToken cancellation)
    {
        string bridge = Path.Combine(AppContext.BaseDirectory, "tools", "wand_cdp_launch.js");
        if (!File.Exists(bridge))
        {
            store.Log("Bundled Wand CDP launch helper is unavailable; continuing with the bounded protocol retry.");
            return false;
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
        try
        {
            using var helper = Process.Start(startInfo);
            if (helper == null) return false;
            var standardOutput = helper.StandardOutput.ReadToEndAsync();
            var standardError = helper.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try { await helper.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                try { if (!helper.HasExited) helper.Kill(entireProcessTree: true); } catch { }
                store.Log("Wand CDP launch helper timed out; continuing with the bounded protocol retry.");
                return false;
            }
            catch (OperationCanceledException)
            {
                try { if (!helper.HasExited) helper.Kill(entireProcessTree: true); } catch { }
                throw;
            }
            string output = await standardOutput;
            string error = await standardError;
            bool navigated = helper.ExitCode == 0 && output.Contains("\"navigated\":true", StringComparison.Ordinal);
            store.Log(navigated
                ? "Wand local CDP route accepted the unique auto-launch request."
                : "Wand local CDP route was unavailable" + (error.Length == 0 ? "." : ": " + error.Trim()));
            return navigated;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            store.Log("Wand local CDP launch could not run; continuing with the bounded protocol retry: " + ex.Message);
            return false;
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

    private static async Task<bool> EnsureWandStartedAsync(string wandPath, LibraryStore store, CancellationToken cancellation)
    {
        if (IsWandRunning())
        {
            // A visible client window can predate the protocol listener (for
            // example after Wand's helper processes have just respawned). Keep
            // the same warm-up for an already-running client before sending a
            // play URI.
            store.Log("Wand client was already visible; waiting for its protocol listener.");
            await Task.Delay(TimeSpan.FromSeconds(2), cancellation);
            bool ready = IsWandRunning();
            store.Log("Already-running Wand readiness=" + ready);
            return ready;
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
                if (IsWandRunning())
                {
                    // A visible window appears before the protocol listener finishes
                    // loading. Give a freshly started desktop client enough warm-up
                    // time before sending the first wemod://play URI.
                    store.Log("Wand client exposed a visible window; waiting for its protocol listener.");
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellation);
                    bool ready = IsWandRunning();
                    store.Log("Freshly-started Wand readiness=" + ready);
                    return ready;
                }
                if (DateTime.UtcNow >= nextProgressLog)
                {
                    store.Log("Wand startup is still pending; Game Library remains responsive while it waits.");
                    nextProgressLog = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                }
                await Task.Delay(250, cancellation);
            }
            store.Log("Wand did not expose a responsive client within the three-minute startup window.");
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

    private static async Task<bool> WaitForConnectionEvidenceAsync(string executable, int expectedPid, DateTime sinceUtc, TimeSpan timeout, CancellationToken cancellation)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (HasFreshConnectionEvidence(executable, expectedPid, sinceUtc)) return true;
            var process = FindExactProcess(executable);
            process?.Dispose();
            await Task.Delay(250, cancellation);
        }
        return HasFreshConnectionEvidence(executable, expectedPid, sinceUtc);
    }

    private static bool HasFreshConnectionEvidence(string executable, int expectedPid, DateTime sinceUtc)
    {
        string overlayRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wand", "logs", "overlay");
        if (!Directory.Exists(overlayRoot)) return false;
        string stem = Normalize(Path.GetFileNameWithoutExtension(executable));
        if (stem.Length == 0) return false;
        try
        {
            foreach (var logPath in Directory.EnumerateFiles(overlayRoot, "*.log", SearchOption.TopDirectoryOnly))
            {
                if (!string.Equals(Normalize(Path.GetFileNameWithoutExtension(logPath)), stem, StringComparison.Ordinal)) continue;
                if (File.GetLastWriteTimeUtc(logPath) < sinceUtc.AddMilliseconds(-500)) continue;
                string text;
                using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length > 128 * 1024) stream.Seek(-128 * 1024, SeekOrigin.End);
                    using var reader = new StreamReader(stream);
                    text = reader.ReadToEnd();
                }
                if (ContainsConnectionEvidence(text, expectedPid)) return true;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return false;
    }

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
        int launcherCount = 0, trackedCount = 0, registrationMatches = 0, protocolCandidates = 0;
        foreach (var registration in registrations)
        {
            var game = games
                .Select(candidate => new { Game = candidate, Score = MainWindow.WandLibraryMatchScore(candidate, registration, state.LaunchPaths.GetValueOrDefault(candidate.Id)) })
                .Where(candidate => candidate.Score > 0)
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Game.Id, StringComparer.Ordinal)
                .Select(candidate => candidate.Game)
                .FirstOrDefault()
                ?? new Game { Id = "wand:" + registration.GameId, Name = registration.Name, IsLocal = true };
            // Production normalizes the selected card to the manifest title.
            // Mirror that exact launch input here without changing persisted data.
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
            if (launcherExists) launcherCount++;
            if (trackedExists) trackedCount++;
            if (registered) registrationMatches++;
            if (protocol) protocolCandidates++;
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
                protocolCandidate = protocol
            });
        }
        bool wand = File.Exists(MainWindow.DetectWandPath() ?? "");
        bool passed = registrations.Count >= 39
            && registrations.Select(registration => registration.GameId).Distinct(StringComparer.Ordinal).Count() == registrations.Count
            && launcherCount == registrations.Count
            && trackedCount == registrations.Count
            && registrationMatches == registrations.Count
            && protocolCandidates == registrations.Count
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
            wandExecutable = MainWindow.DetectWandPath(),
            cachedCatalog = catalog != null,
            games = rows
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        File.WriteAllText(Path.GetFullPath(report), System.Text.Json.JsonSerializer.Serialize(payload, DataJson.Options));
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: latest Wand audit; filter {registrations.Count}; launchers {launcherCount}; tracked executables {trackedCount}; exact registrations {registrationMatches}; protocol candidates {protocolCandidates}; report={Path.GetFullPath(report)}");
        return passed ? 0 : 1;
    }
}
