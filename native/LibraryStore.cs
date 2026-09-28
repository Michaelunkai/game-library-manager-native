using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;

namespace GameLibrary.Native;

public sealed class LibraryStore
{
    private const int MaxQueuedLogLines = 2048;
    private readonly ConcurrentQueue<string> pendingLogLines = new();
    private int pendingLogCount;
    private int logWriterActive;
    public string Root { get; }
    public string Assets { get; }
    public string Cache => Path.Combine(Root, "cache");
    public string StatePath => Path.Combine(Root, "state.json");
    public string? RecoveryNotice { get; private set; }
    internal static string ResolveDefaultRoot(string baseDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(baseDirectory));
        // native/dist is the repository's Windows distribution. Its original
        // profile belongs to the repository, not the native source directory.
        // Resolve by layout, never by whether a profile happens to exist: a
        // rebuild or temporary unavailable file must not select a new profile.
        if (string.Equals(directory.Name, "dist", StringComparison.OrdinalIgnoreCase) && directory.Parent != null)
        {
            directory = directory.Parent;
            if (string.Equals(directory.Name, "native", StringComparison.OrdinalIgnoreCase) && directory.Parent != null)
                directory = directory.Parent;
        }
        return Path.Combine(directory.FullName, "data");
    }
    public static string DefaultRoot => ResolveDefaultRoot(AppContext.BaseDirectory);
    public LibraryStore(string? root = null)
    {
        Root = root == null ? DefaultRoot : Path.GetFullPath(root);
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("GameLibrary.Native.Assets.catalog.sha256");
        var revision = resource == null ? "v1" : new StreamReader(resource).ReadToEnd().Trim()[..16];
        Assets = Path.Combine(Root, "assets-" + revision);
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Cache);
    }
    public void EnsureAssets()
    {
        if (File.Exists(Path.Combine(Assets, ".complete"))) return;
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("GameLibrary.Native.Assets.catalog.zip")
            ?? throw new FileNotFoundException("The packaged catalog is missing. Rebuild with build.ps1.");
        Directory.CreateDirectory(Assets);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            var target = SafeChild(Assets, entry.FullName);
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
        File.WriteAllText(Path.Combine(Assets, ".complete"), DateTime.UtcNow.ToString("O"));
    }
    public static string SafeChild(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Path escapes the application folder.");
        return full;
    }
    public static void AtomicWrite(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream)) { writer.Write(text); writer.Flush(); stream.Flush(true); }
            if (File.Exists(path))
            {
                for (int attempt = 0; ; attempt++)
                {
                    try { File.Replace(temp, path, path + ".bak", true); break; }
                    catch (IOException ex) when (ex.HResult == unchecked((int)0x80070497) && attempt < 4)
                    {
                        // A transient reader of the rollback file prevents Windows from
                        // removing it. Keep the flushed temp and retry the same replacement.
                        Thread.Sleep(50 << attempt);
                    }
                }
            }
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public UserState LoadState()
    {
        if (!File.Exists(StatePath)) return new();
        try
        {
            var state = ValidateState(DataJson.Read<UserState>(File.ReadAllText(StatePath)));
            if (MigrateDefaults(state) | CatalogIdentity.RepairLaunchMappings(state)) Save(state);
            return state;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException or ArgumentException)
        {
            Log("Profile validation failed; preserving original: " + ex);
            File.Copy(StatePath, StatePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
            if (File.Exists(StatePath + ".bak"))
            {
                var restored = ValidateState(DataJson.Read<UserState>(File.ReadAllText(StatePath + ".bak")));
                RecoveryNotice = "Recovered your library from its last valid backup. The damaged file was preserved.";
                return restored;
            }
            throw new FormatException("Your saved library could not be read. It has been preserved; import a valid backup.", ex);
        }
    }
    private static bool MigrateDefaults(UserState state)
    {
        bool changed = false;
        if (string.Equals(state.Settings.MountPath, Preferences.LegacyDefaultMountPath, StringComparison.OrdinalIgnoreCase))
        { state.Settings.MountPath = Preferences.DefaultMountPath; changed = true; }
        if (string.IsNullOrWhiteSpace(state.Settings.SortBy) || state.Settings.SortBy == "Newest first")
        { state.Settings.SortBy = "Recently Added"; changed = true; }
        if (string.IsNullOrWhiteSpace(state.Settings.ScriptFormat))
        { state.Settings.ScriptFormat = Preferences.DefaultScriptFormat; changed = true; }
        if (string.IsNullOrWhiteSpace(state.Settings.ShellTarget))
        { state.Settings.ShellTarget = Preferences.DefaultShellTarget; changed = true; }
        return changed;
    }
    public static UserState ValidateState(UserState state)
    {
        if (state.PendingGameBackups == null || state.PendingGameBackups.Any(p => string.IsNullOrWhiteSpace(p.Key)
            || string.IsNullOrWhiteSpace(p.Value) || !Path.IsPathFullyQualified(p.Value) || !Path.GetExtension(p.Value).Equals(".exe", StringComparison.OrdinalIgnoreCase)))
            throw new FormatException("Invalid pending game backup.");
        if (state.SchemaVersion != 1 || state.Settings == null || state.Ratings == null || state.GameTags == null || state.Wishlist == null || state.InstalledGames == null || state.Pending == null || state.LaunchPaths == null || state.PlayTimeSeconds == null || state.LastPlayedUtc == null || state.LocalGames == null)
            throw new FormatException("Unsupported or incomplete library backup.");
        LocalCatalogEdits.Validate(state.LocalCatalog);
        if (state.WandGames == null || state.WandGames.Any(e => e.Value == null || !e.Key.StartsWith("wand:", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(e.Value.Name) || !Path.IsPathFullyQualified(e.Value.Folder)))
            throw new FormatException("Invalid saved Wand game.");
        if (state.Ratings.Values.Any(v => v < 0 || v > 5)) throw new FormatException("Ratings must be between zero and five.");
        if (state.PlayTimeSeconds.Values.Any(v => !double.IsFinite(v) || v < 0)) throw new FormatException("Play time must be finite and non-negative.");
        if (state.LastPlayedUtc.Any(e => e.Value.Kind == DateTimeKind.Local)) throw new FormatException("Last-played timestamps must be UTC.");
        if (!string.IsNullOrWhiteSpace(state.Settings.WandPath) && (!Path.IsPathFullyQualified(state.Settings.WandPath) || state.Settings.WandPath.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)) throw new FormatException("Wand path must be an absolute Windows executable path.");
        if (state.GameTags.Values.Any(v => v == null || v.Any(t => t == null || t.Length > 80))) throw new FormatException("Invalid game tags.");
        if (state.LocalGames.Any(e => e.Value == null || !e.Key.StartsWith("local:", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(e.Value.Name) || !Path.IsPathFullyQualified(e.Value.Folder)))
            throw new FormatException("Invalid local game record.");
        foreach (var e in state.Pending)
            if (e.Section is not ("gameCategories" or "tabs" or "hiddenTabs")) throw new FormatException("Unsupported pending change.");
        return state;
    }
    public void Save(UserState state) => AtomicWrite(StatePath, DataJson.Write(ValidateState(state)));
    private readonly ConcurrentDictionary<string, (DateTime LastWriteUtc, long Length, JsonNode Parsed)> jsonCache = new(StringComparer.Ordinal);
    public string ReadData(string file)
    {
        var cache = SafeChild(Cache, file);
        if (File.Exists(cache))
        {
            try { var text = File.ReadAllText(cache); JsonNode.Parse(text); return text; }
            catch (System.Text.Json.JsonException) { RecoveryNotice = $"Using packaged {file}; the cached copy was damaged."; }
        }
        return File.ReadAllText(SafeChild(Path.Combine(Assets, "data"), file));
    }
    // Parses a data file once and reuses the JsonNode until the file on disk
    // changes. The namespace catalog (~3.6 MB), tag snapshots and metadata are
    // re-read on every catalog reload, so caching their parse removes a large
    // repeated UI-thread cost.
    public JsonNode? ReadDataNode(string file)
    {
        string cache = SafeChild(Cache, file);
        string path = File.Exists(cache) ? cache : SafeChild(Path.Combine(Assets, "data"), file);
        try
        {
            var info = new FileInfo(path);
            if (jsonCache.TryGetValue(file, out var cached) && cached.LastWriteUtc == info.LastWriteTimeUtc && cached.Length == info.Length)
                return cached.Parsed;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 64 * 1024 * 1024) return null;
            using var reader = new StreamReader(stream);
            var node = JsonNode.Parse(reader.ReadToEnd());
            if (node == null) throw new InvalidDataException("Empty JSON data.");
            jsonCache[file] = (info.LastWriteTimeUtc, info.Length, node);
            return node;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException) { return null; }
    }
    public JsonObject ReadConfig()
    {
        var file = Path.Combine(Cache, "admin-config.json");
        try { return JsonNode.Parse(File.ReadAllText(File.Exists(file) ? file : Path.Combine(Assets, "data", "admin-config.json")))!.AsObject(); }
        catch { return new JsonObject { ["gameCategories"] = new JsonObject(), ["hiddenTabs"] = new JsonArray() }; }
    }
    public void CacheData(string file, string json) { JsonNode.Parse(json); jsonCache.TryRemove(file, out _); AtomicWrite(SafeChild(Cache, file), json); }
    public JsonObject ReadMetadata()
    {
        try { return (ReadDataNode("metadata.json") as JsonObject) ?? new JsonObject(); }
        catch { return new JsonObject(); }
    }
    public List<Game> LoadGames(UserState state, JsonObject config)
    {
        var games = DataJson.Read<List<Game>>(ReadData("games.json"));
        var times = JsonNode.Parse(ReadData("times.json"))!;
        var sizes = JsonNode.Parse(ReadData("image-sizes.json"))!;
        var dates = JsonNode.Parse(ReadData("dates-added.json"))!;
        var metadata = ReadMetadata();
        JsonObject? namespaceBaseline = null;
        var namespaceFile = Path.Combine(Cache, "docker-namespace-catalog.json");
        if (File.Exists(namespaceFile) && ReadDataNode("docker-namespace-catalog.json") is JsonObject all
            && DataJson.Text(all["namespace"]) == state.Settings.DockerUsername)
        {
            try { MergeNamespace(games, all); namespaceBaseline = all; }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
            { RecoveryNotice = "The repository namespace cache could not be verified; the packaged catalog is retained."; }
        }
        JsonObject? namespaceUpdates = null;
        var updatesFile = Path.Combine(Cache, "docker-namespace-updates.json");
        if (File.Exists(updatesFile))
        {
            try
            {
                namespaceUpdates = NamespaceUpdates.Combine(state.Settings.DockerUsername, namespaceBaseline, ReadDataNode("docker-namespace-updates.json") as JsonObject);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            { RecoveryNotice = "Recent repository updates could not be read; the previous catalog is retained."; }
        }
        var tagsFile = Path.Combine(Cache, "docker-tags.json");
        if (File.Exists(tagsFile) && ReadDataNode("docker-tags.json") is JsonObject snapshot
            && string.Equals(DataJson.Text(snapshot["repository"]), state.Settings.DockerUsername + "/" + state.Settings.RepoName, StringComparison.Ordinal))
        {
            try
            {
                MergeTags(games, snapshot, state.Settings);
                if (namespaceUpdates?["repositories"] is JsonArray rows && DateTimeOffset.TryParse(DataJson.Text(snapshot["fetchedAt"]), out var priorityAt))
                    foreach (var row in rows.ToArray())
                        if (DataJson.Text(row?["repository"]) == DataJson.Text(snapshot["repository"])
                            && DateTimeOffset.TryParse(DataJson.Text(row?["fetchedAt"]), out var updateAt) && priorityAt >= updateAt) rows.Remove(row);
                if (namespaceUpdates != null) namespaceUpdates["repositoryCount"] = (namespaceUpdates["repositories"] as JsonArray)?.Count ?? 0;
            }
            catch (System.Text.Json.JsonException) { RecoveryNotice = "The Docker tag cache is damaged; the packaged catalog is still available."; }
        }
        if (namespaceUpdates != null) NamespaceUpdates.Apply(games, namespaceUpdates);
        var categories = LoadCategories(config).ToDictionary(c => c.Id, c => c.Name, StringComparer.Ordinal);
        games.AddRange(state.LocalGames.Select(e => {
            string title = InstalledMetadataIdentity.Title(e.Value.Folder, state.LaunchPaths.GetValueOrDefault(e.Key));
            string gogTitle = GogInstalledIdentity.Title(e.Value.Folder, state.LaunchPaths.GetValueOrDefault(e.Key));
            bool conflict = false;
            var platform = gogTitle.Length == 0 ? InstalledPlatformIdentity.Resolve(e.Value.Folder, state.LaunchPaths.GetValueOrDefault(e.Key), out conflict) : null;
            var identity = new Game { MetadataSteamAppId = platform?.SteamAppId ?? 0 };
            if (platform != null && metadata[e.Key] is JsonObject cached && InstalledPlatformIdentity.Accepts(identity, cached))
                title = DataJson.Text(cached["matchedTitle"]);
            bool defaultName = string.Equals(e.Value.Name, Path.GetFileName(Path.TrimEndingDirectorySeparator(e.Value.Folder)), StringComparison.OrdinalIgnoreCase);
            return new Game { Id = e.Key, Name = defaultName && title.Length > 0 ? title : e.Value.Name,
                MetadataLookupTitle = title, MetadataSteamAppId = platform?.SteamAppId ?? 0, MetadataIdentityConflict = conflict, Category = e.Value.Category, Added = e.Value.AddedUtc, IsLocal = true,
                Description = "Installed on this PC · " + e.Value.Folder };
        }));
        games.AddRange(state.WandGames.Where(e => games.All(g => g.Id != e.Key)).Select(e => new Game {
            Id = e.Key, Name = e.Value.Name, Category = e.Value.Category, Added = e.Value.AddedUtc, IsLocal = true,
            Description = "Discovered through Wand · " + e.Value.Folder }));
        InstalledOrphanSources.Add(games, state, config);
        foreach (var g in games)
        {
            // Preserve the packaged catalog's explicit kind; neither category names
            // nor a failed metadata search establish whether an entry is a game.
            g.IsNonGame = !g.IsLocal && g.Image.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                && g.Description.Contains(" is a non-game Docker/system tag, not a playable game.", StringComparison.Ordinal);
            if (g.IsNonGame)
            {
                g.Name = FormatName(g.Id);
                g.Description = g.Details = "Utility or backup Docker image: " + g.Id + ". Game completion estimates do not apply.";
                g.Time = 0;
            }
            bool correctedIdentity = CatalogIdentity.Correct(g);
            if (!state.LocalGames.ContainsKey(g.Id) && state.InstalledGames.Contains(g.Id) && !g.IsNonGame && !g.RequiresGameIdentity)
            {
                string folder = state.InstallationFolders.GetValueOrDefault(g.Id) ?? state.WandGames.GetValueOrDefault(g.Id)?.Folder ?? "";
                if (folder.Length > 0)
                {
                    string gog = GogInstalledIdentity.Title(folder, state.LaunchPaths.GetValueOrDefault(g.Id));
                    if (gog.Length > 0) g.MetadataLookupTitle = gog;
                    else
                    {
                        var platform = InstalledPlatformIdentity.Resolve(folder, state.LaunchPaths.GetValueOrDefault(g.Id), out bool conflict);
                        g.MetadataIdentityConflict = conflict;
                        var known = CatalogIdentity.Known(g.Id);
                        if (platform != null && known != null && known.Value.SteamId != platform.SteamAppId) g.MetadataIdentityConflict = true;
                        if (platform != null && !g.MetadataIdentityConflict)
                        {
                            g.MetadataSteamAppId = platform.SteamAppId;
                            if (metadata[g.Id] is JsonObject cached && DataJson.Number(cached["steamAppId"]) == platform.SteamAppId && InstalledPlatformIdentity.Accepts(g, cached))
                                g.MetadataLookupTitle = DataJson.Text(cached["matchedTitle"]);
                        }
                    }
                }
            }
            if (!g.IsLocal && g.MetadataLookupTitle.Length > 0)
            {
                // The selected installation can disprove an old catalog title.
                // Enrich only provider-owned labels; local personal names stay intact.
                if (!MetadataClient.SameTitle(g.Name, g.MetadataLookupTitle))
                {
                    correctedIdentity = true;
                    g.Time = 0; g.Image = ""; g.Cover = "";
                    g.Description = "Installed identity verified from " + (g.MetadataSteamAppId > 0 ? "Steam App ID " + g.MetadataSteamAppId : "the selected GOG executable manifest") + ".";
                    g.Details = "";
                }
                g.Name = g.MetadataLookupTitle;
            }
            if (!g.IsLocal) g.Category = DataJson.Text(config["gameCategories"]?[g.Id], g.Category);
            else g.Category = DataJson.Text(state.LocalCatalog["gameCategories"]?[g.Id], g.Category);
            g.CategoryName = categories.GetValueOrDefault(g.Category, g.Category);
            g.ShowTime = state.Settings.ShowTimes; g.ShowCategory = state.Settings.ShowCategories;
            g.CoverHeight = state.Settings.GridSize == "small" ? 70 : state.Settings.GridSize == "large" ? 130 : 98;
            double time = DataJson.Number(times[g.Id]); if (time > 0 && !correctedIdentity && !g.IsNonGame) g.Time = time;
            double size = DataJson.Number(sizes[g.Id]); if (g.SizeGb <= 0 && size > 0) g.SizeGb = size;
            if (g.Added == default && DateTime.TryParse(DataJson.Text(dates[g.Id]), out var added)) g.Added = added;
            g.Rating = state.Ratings.GetValueOrDefault(g.Id);
            g.Wishlisted = state.Wishlist.Contains(g.Id);
            g.Installed = state.InstalledGames.Contains(g.Id);
            g.PlayedHours = state.PlayTimeSeconds.GetValueOrDefault(g.Id) / 3600d;
            g.LastPlayedUtc = state.LastPlayedUtc.GetValueOrDefault(g.Id);
            g.TagsLabel = string.Join("  ·  ", state.GameTags.GetValueOrDefault(g.Id, new()));
            if (!g.IsLocal && string.IsNullOrEmpty(g.DockerImage)) g.DockerImage = $"{DockerIdentity.CatalogRepository}:{g.Id}";
            if (!g.IsLocal && string.IsNullOrEmpty(g.DockerImageUrl)) g.DockerImageUrl = $"https://hub.docker.com/r/{DockerIdentity.CatalogRepository}/tags?name={Uri.EscapeDataString(g.Id)}";
            if (!string.IsNullOrWhiteSpace(g.Image) && !g.Image.StartsWith("data:"))
            {
                if (Uri.TryCreate(g.Image, UriKind.Absolute, out var uri) && uri.Scheme == "https") g.Cover = g.Image;
                else { try { var path = SafeChild(Assets, g.Image.TrimStart('/')); if (File.Exists(path)) g.Cover = path; } catch (ArgumentException) { } }
            }
            if (g.RequiresGameIdentity && DockerIdentity.TryParse(g.Id, out string sourceRepository, out string sourceTag))
            {
                // A qualified numeric/version tag is an image identity, not a game
                // title. Retain source and personal records, but do not attribute a
                // coincidentally numeric provider title to this image's contents.
                g.Name = sourceRepository + ":" + sourceTag;
                g.Time = 0; g.TimeVerifiedAt = default; g.Cover = ""; g.DiskRequirementGb = 0;
            }
            if (!g.IsNonGame && !g.RequiresGameIdentity && metadata[g.Id] is JsonObject extra && CatalogIdentity.AcceptsMetadata(g.Id, extra) &&
                InstalledPlatformIdentity.Accepts(g, extra))
            {
                // Provider classification only fills new Docker entries, never curated/shared overrides.
                if (g.Discovered)
                {
                    var name = DataJson.Text(extra["name"]); if (name.Length > 0) g.Name = name;
                    var category = DataJson.Text(extra["category"]);
                    if (config["gameCategories"]?[g.Id] == null && categories.ContainsKey(category))
                    { g.Category = category; g.CategoryName = categories[category]; }
                }
                double enrichedTime = DataJson.Number(extra["time"]);
                if (CompletionDuration.HasSource(extra) && DataJson.Text(extra["source"]?["time"]) != "howlongtobeat-cache-stale" &&
                    DateTime.TryParse(DataJson.Text(extra["timeFetchedAt"], DataJson.Text(extra["fetchedAt"])), out var verifiedAt))
                    g.TimeVerifiedAt = verifiedAt.ToUniversalTime();
                g.DiskRequirementGb = DataJson.Number(extra["diskRequirementGb"]);
                if (enrichedTime > 0 && (g.Time <= 0 || DataJson.Text(extra["source"]?["time"]) != "genre-estimate")) g.Time = enrichedTime;
                var cover = DataJson.Text(extra["cover"]);
                if (cover.Length > 0) { try { var path = SafeChild(Cache, cover); if (File.Exists(path)) g.Cover = path; } catch (ArgumentException) { } }
            }
        }
        return games;
    }
    public List<Category> LoadCategories(JsonObject config)
    {
        var raw = config["tabs"] is JsonArray tabs ? tabs.ToJsonString() : ReadData("tabs.json");
        return DataJson.Read<List<Category>>(raw).Where(c => !string.IsNullOrWhiteSpace(c.Id)).DistinctBy(c => c.Id, StringComparer.Ordinal).ToList();
    }
    public static void MergeTags(List<Game> games, JsonNode response, Preferences settings)
    {
        string repository = settings.DockerUsername + "/" + settings.RepoName;
        var existing = games.ToDictionary(g => g.Id, StringComparer.Ordinal);
        if (response["tags"] is not JsonArray tags) return;
        foreach (var tag in tags)
        {
            var name = tag is JsonValue ? DataJson.Text(tag) : DataJson.Text(tag?["name"]);
            if (!DockerScripts.ValidTag(name)) continue;
            string identity = DockerIdentity.Create(repository, name);
            if (!existing.TryGetValue(identity, out var game))
            {
                game = new Game { Id = identity, Name = DockerIdentity.MetadataTitle(repository, name), Category = "new", Discovered = true, Description = "Discovered on Docker Hub. Artwork and completion time are looked up automatically when available." };
                games.Add(game); existing.Add(identity, game);
            }
            game.DockerImage = $"{settings.DockerUsername}/{settings.RepoName}:{name}";
            game.DockerImageUrl = $"https://hub.docker.com/r/{settings.DockerUsername}/{settings.RepoName}/tags?name={Uri.EscapeDataString(name)}";
            if (tag is JsonObject obj)
            {
                game.SizeGb = DataJson.Number(obj["full_size"]) / 1_000_000_000;
                if (DateTime.TryParse(DataJson.Text(obj["last_updated"]), out var date)) game.Added = date;
            }
        }
    }
    public static void MergeNamespace(List<Game> games, JsonObject snapshot)
    {
        string owner = DataJson.Text(snapshot["namespace"]);
        if (snapshot["complete"]?.ToString() != "true" || snapshot["success"]?.ToString() != "true"
            || snapshot["repositories"] is not JsonArray repositories
            || DataJson.Number(snapshot["repositoryCount"]) != repositories.Count)
            throw new InvalidDataException("Incomplete repository namespace.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in repositories)
        {
            string repository = DataJson.Text(item?["repository"]);
            if (!DockerIdentity.ValidRepository(repository) || !repository.StartsWith(owner + "/", StringComparison.Ordinal)
                || !seen.Add(repository) || item?["complete"]?.ToString() != "true" || item?["stale"]?.ToString() != "false")
                throw new InvalidDataException("Invalid repository identity or stale data.");
            var check = item!.DeepClone().AsObject(); check["success"] = true;
            if (!SyncClient.CompleteTagSnapshot(check, repository)) throw new InvalidDataException("Incomplete repository tags.");
        }
        foreach (var item in repositories)
        {
            string repository = DataJson.Text(item!["repository"]);
            MergeTags(games, item, new Preferences { DockerUsername = owner, RepoName = repository[(owner.Length + 1)..] });
        }
    }
    public static string FormatName(string id) => System.Text.RegularExpressions.Regex.Replace(id.Replace('_', ' ').Replace('-', ' '), "([a-z])([A-Z])", "$1 $2");
    public void Log(string message)
    {
        string line = $"{DateTime.UtcNow:O} {message}{Environment.NewLine}";
        pendingLogLines.Enqueue(line);
        int count = Interlocked.Increment(ref pendingLogCount);
        while (count > MaxQueuedLogLines && pendingLogLines.TryDequeue(out _))
        {
            count = Interlocked.Decrement(ref pendingLogCount);
        }
        ScheduleLogWriter();
    }
    private void ScheduleLogWriter()
    {
        if (Interlocked.CompareExchange(ref logWriterActive, 1, 0) != 0) return;
        ThreadPool.QueueUserWorkItem(_ => DrainLogQueue());
    }
    private void DrainLogQueue()
    {
        try
        {
            while (pendingLogLines.TryDequeue(out var line))
            {
                Interlocked.Decrement(ref pendingLogCount);
                try { File.AppendAllText(Path.Combine(Root, "activity.log"), line); }
                catch (Exception ex)
                {
                    // A blocked, full, missing, or read-only profile volume must
                    // consume only this background writer; it can never hold the
                    // WPF dispatcher or prevent buttons/window painting.
                    try { Debug.WriteLine("Game Library activity log unavailable: " + ex.Message + " | " + line); } catch { }
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref logWriterActive, 0);
            if (!pendingLogLines.IsEmpty) ScheduleLogWriter();
        }
    }
}
