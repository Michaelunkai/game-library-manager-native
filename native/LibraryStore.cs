using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace GameLibrary.Native;

public sealed class LibraryStore
{
    public string Root { get; }
    public string Assets { get; }
    public string Cache => Path.Combine(Root, "cache");
    public string StatePath => Path.Combine(Root, "state.json");
    public string? RecoveryNotice { get; private set; }
    internal static string ResolveDefaultRoot(string baseDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(baseDirectory));
        // The verified distribution keeps its writable profile one level above
        // dist\, alongside the source catalog and evidence. This makes a
        // double-click launch use the same F: profile as the documented route
        // instead of silently creating a second profile under C:.
        if (string.Equals(directory.Name, "dist", StringComparison.OrdinalIgnoreCase) && directory.Parent != null)
            directory = directory.Parent;
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
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak", true);
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
            if (MigrateDefaults(state)) Save(state);
            return state;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException or ArgumentException)
        {
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
        if (state.SchemaVersion != 1 || state.Settings == null || state.Ratings == null || state.GameTags == null || state.Wishlist == null || state.InstalledGames == null || state.Pending == null || state.LaunchPaths == null || state.LocalGames == null)
            throw new FormatException("Unsupported or incomplete library backup.");
        if (state.Ratings.Values.Any(v => v < 0 || v > 5)) throw new FormatException("Ratings must be between zero and five.");
        if (state.PlayTimeSeconds.Values.Any(v => !double.IsFinite(v) || v < 0)) throw new FormatException("Play time must be finite and non-negative.");
        if (!string.IsNullOrWhiteSpace(state.Settings.WandPath) && (!Path.IsPathFullyQualified(state.Settings.WandPath) || state.Settings.WandPath.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)) throw new FormatException("Wand path must be an absolute Windows executable path.");
        if (state.GameTags.Values.Any(v => v == null || v.Any(t => t == null || t.Length > 80))) throw new FormatException("Invalid game tags.");
        if (state.LocalGames.Any(e => e.Value == null || !e.Key.StartsWith("local:", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(e.Value.Name) || !Path.IsPathFullyQualified(e.Value.Folder)))
            throw new FormatException("Invalid local game record.");
        foreach (var e in state.Pending)
            if (e.Section is not ("gameCategories" or "tabs" or "hiddenTabs")) throw new FormatException("Unsupported pending change.");
        return state;
    }
    public void Save(UserState state) => AtomicWrite(StatePath, DataJson.Write(ValidateState(state)));
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
    public JsonObject ReadConfig()
    {
        var file = Path.Combine(Cache, "admin-config.json");
        try { return JsonNode.Parse(File.ReadAllText(File.Exists(file) ? file : Path.Combine(Assets, "data", "admin-config.json")))!.AsObject(); }
        catch { return new JsonObject { ["gameCategories"] = new JsonObject(), ["hiddenTabs"] = new JsonArray() }; }
    }
    public void CacheData(string file, string json) { JsonNode.Parse(json); AtomicWrite(SafeChild(Cache, file), json); }
    public JsonObject ReadMetadata()
    {
        try { return JsonNode.Parse(File.ReadAllText(Path.Combine(Cache, "metadata.json")))!.AsObject(); }
        catch { return new JsonObject(); }
    }
    public List<Game> LoadGames(UserState state, JsonObject config)
    {
        var games = DataJson.Read<List<Game>>(ReadData("games.json"));
        var times = JsonNode.Parse(ReadData("times.json"))!;
        var sizes = JsonNode.Parse(ReadData("image-sizes.json"))!;
        var dates = JsonNode.Parse(ReadData("dates-added.json"))!;
        var metadata = ReadMetadata();
        var tagsFile = Path.Combine(Cache, "docker-tags.json");
        if (File.Exists(tagsFile))
        {
            try { MergeTags(games, JsonNode.Parse(File.ReadAllText(tagsFile))!, state.Settings); }
            catch (System.Text.Json.JsonException) { RecoveryNotice = "The Docker tag cache is damaged; the packaged catalog is still available."; }
        }
        var categories = LoadCategories(config).ToDictionary(c => c.Id, c => c.Name, StringComparer.Ordinal);
        games.AddRange(state.LocalGames.Select(e => new Game { Id = e.Key, Name = e.Value.Name, Category = e.Value.Category, Added = e.Value.AddedUtc, IsLocal = true, Description = "Installed on this PC · " + e.Value.Folder }));
        foreach (var g in games)
        {
            if (!g.IsLocal) g.Category = DataJson.Text(config["gameCategories"]?[g.Id], g.Category);
            g.CategoryName = categories.GetValueOrDefault(g.Category, g.Category);
            g.ShowTime = state.Settings.ShowTimes; g.ShowCategory = state.Settings.ShowCategories;
            g.CoverHeight = state.Settings.GridSize == "small" ? 70 : state.Settings.GridSize == "large" ? 130 : 98;
            double time = DataJson.Number(times[g.Id]); if (time > 0) g.Time = time;
            double size = DataJson.Number(sizes[g.Id]); if (g.SizeGb <= 0 && size > 0) g.SizeGb = size;
            if (g.Added == default && DateTime.TryParse(DataJson.Text(dates[g.Id]), out var added)) g.Added = added;
            g.Rating = state.Ratings.GetValueOrDefault(g.Id);
            g.Wishlisted = state.Wishlist.Contains(g.Id);
            g.Installed = state.InstalledGames.Contains(g.Id);
            g.PlayedHours = state.PlayTimeSeconds.GetValueOrDefault(g.Id) / 3600d;
            g.TagsLabel = string.Join("  ·  ", state.GameTags.GetValueOrDefault(g.Id, new()));
            if (!g.IsLocal && string.IsNullOrEmpty(g.DockerImage)) g.DockerImage = $"{state.Settings.DockerUsername}/{state.Settings.RepoName}:{g.Id}";
            if (!g.IsLocal && string.IsNullOrEmpty(g.DockerImageUrl)) g.DockerImageUrl = $"https://hub.docker.com/r/{state.Settings.DockerUsername}/{state.Settings.RepoName}/tags?name={Uri.EscapeDataString(g.Id)}";
            if (!string.IsNullOrWhiteSpace(g.Image) && !g.Image.StartsWith("data:"))
            {
                if (Uri.TryCreate(g.Image, UriKind.Absolute, out var uri) && uri.Scheme == "https") g.Cover = g.Image;
                else { try { var path = SafeChild(Assets, g.Image.TrimStart('/')); if (File.Exists(path)) g.Cover = path; } catch (ArgumentException) { } }
            }
            if (metadata[g.Id] is JsonObject extra)
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
        var existing = games.ToDictionary(g => g.Id, StringComparer.Ordinal);
        if (response["tags"] is not JsonArray tags) return;
        foreach (var tag in tags)
        {
            var name = tag is JsonValue ? DataJson.Text(tag) : DataJson.Text(tag?["name"]);
            if (!DockerScripts.ValidTag(name)) continue;
            if (!existing.TryGetValue(name, out var game))
            {
                game = new Game { Id = name, Name = FormatName(name), Category = "new", Discovered = true, Description = "Discovered on Docker Hub. Artwork and completion time are looked up automatically when available." };
                games.Add(game); existing.Add(name, game);
            }
            if (tag is JsonObject obj)
            {
                game.SizeGb = DataJson.Number(obj["full_size"]) / 1_000_000_000;
                if (DateTime.TryParse(DataJson.Text(obj["last_updated"]), out var date)) game.Added = date;
            }
        }
    }
    public static string FormatName(string id) => System.Text.RegularExpressions.Regex.Replace(id.Replace('_', ' ').Replace('-', ' '), "([a-z])([A-Z])", "$1 $2");
    public void Log(string message)
    {
        try
        {
            lock (this) File.AppendAllText(Path.Combine(Root, "activity.log"), $"{DateTime.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            // Logging must never take down the UI when the profile volume is
            // full, read-only, or temporarily locked by another process.
            try { Debug.WriteLine("Game Library activity log unavailable: " + ex.Message + " | " + message); } catch { }
        }
    }
}
