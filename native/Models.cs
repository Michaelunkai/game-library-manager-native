using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GameLibrary.Native;

public sealed class Game : INotifyPropertyChanged
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "new";
    public string Image { get; set; } = "";
    public string DockerImage { get; set; } = "";
    public string DockerImageUrl { get; set; } = "";
    public string Description { get; set; } = "";
    public string Details { get; set; } = "";
    public double Time { get; set; }
    [JsonIgnore] public DateTime TimeVerifiedAt { get; set; }
    [JsonIgnore] public double SizeGb { get; set; }
    [JsonIgnore] public double DiskRequirementGb { get; set; }
    [JsonIgnore] public string InstalledStorageLabel { get; set; } = "";
    [JsonIgnore] public string InstalledStorageDetail { get; set; } = "";
    [JsonIgnore] public bool Discovered { get; set; }
    [JsonIgnore] public bool IsLocal { get; set; }
    [JsonIgnore] public bool IsNonGame { get; set; }
    [JsonIgnore] public bool RequiresGameIdentity => DockerIdentity.RequiresGameIdentity(Id);
    [JsonIgnore] public string MetadataLookupTitle { get; set; } = "";
    [JsonIgnore] public int MetadataSteamAppId { get; set; }
    [JsonIgnore] public bool MetadataIdentityConflict { get; set; }
    [JsonIgnore] public DateTime Added { get; set; }
    [JsonIgnore] public string Cover { get; set; } = "";
    [JsonIgnore] public string CategoryName { get; set; } = "";
    [JsonIgnore] public string TagsLabel { get; set; } = "";
    [JsonIgnore] public double PlayedHours { get; set; }
    [JsonIgnore] public DateTime LastPlayedUtc { get; set; }
    [JsonIgnore] public bool IsPlaying { get; set; }
    [JsonIgnore] public bool IsPlayPaused { get; set; }
    [JsonIgnore] public bool IsPauseStateUnknown { get; set; }
    [JsonIgnore] public string ProgressLabel { get; set; } = "";
    [JsonIgnore] public string ProgressDetail { get; set; } = "";
    // This is deliberately an exact, locally verified Wand registration rather
    // than a broad catalog match. The UI must never advertise a mod launch for
    // a game that Wand does not already know at this executable path.
    [JsonIgnore] public bool CanPlayWithWand { get; set; }
    [JsonIgnore] public string PlayLabel => IsPlaying ? "●  Playing" : "▶  Play";
    [JsonIgnore] public string PauseLabel => IsPauseStateUnknown ? "Pause status unknown" : IsPlayPaused ? "Resume" : "Pause";
    [JsonIgnore] public string PlayedMeta => IsPlaying
        ? (IsPauseStateUnknown ? $"Pause state unknown · {PlayedHours:0.0} h · timing held"
            : IsPlayPaused ? $"Paused · {PlayedHours:0.0} h · timing held" : $"Playing · {PlayedHours:0.0} h")
        : PlayedHours > 0 ? $"Played {PlayedHours:0.0} h" : "Not played";
    [JsonIgnore] public bool ShowTime { get; set; } = true;
    [JsonIgnore] public bool ShowCategory { get; set; } = true;
    [JsonIgnore] public double CoverHeight { get; set; } = 98;
    [JsonIgnore] public double CoverWidth => CoverHeight * 0.735;
    [JsonIgnore] internal IReadOnlyList<Game> SourceRecords { get; set; } = Array.Empty<Game>();
    [JsonIgnore] internal Game? ActionTarget { get; set; }
    [JsonIgnore] internal Game? PublishedRepresentative { get; set; }
    [JsonIgnore] internal bool PublicationFreshnessVerified { get; set; }
    [JsonIgnore] internal string CanonicalCardId { get; set; } = "";
    [JsonIgnore] internal IReadOnlyList<string> IdentityRelationshipReasons { get; set; } = Array.Empty<string>();
    [JsonIgnore] internal List<(Game Source, PropertyChangedEventHandler Handler)> ProjectionSubscriptions { get; } = new();
    [JsonIgnore] internal string ProjectionVersionLabel => SourceRecords.Count > 1 && PublishedRepresentative is { DockerImage.Length: > 0 } published
        ? (PublicationFreshnessVerified ? $"Latest published image: {published.DockerImage}" : $"Published image candidate: {published.DockerImage} (freshness unverified)")
            + (string.Equals(published.Name, Name, StringComparison.Ordinal) ? "" : $" · Published title: {published.Name}")
            + (string.Equals(published.Id, Id, StringComparison.Ordinal) ? "" : $" · Play/save target remains {Id}")
        : "";
    [JsonIgnore] public string Meta => string.Join("  ·  ", new[] { ShowCategory ? CategoryName : "", RequiresGameIdentity ? "Game identity not verified" : IsNonGame ? "Utility / backup" : ShowTime ? (Time > 0 ? $"~{Time:0.#} h" + (TimeVerifiedAt == default ? " (unverified estimate)" : "") : "No sourced completion time") : "", DiskRequirementGb > 0 ? $"{DiskRequirementGb:0.##} GB disk required" : SizeGb > 0 ? $"{SizeGb:0.##} GB download" : "No published size data", ProjectionVersionLabel }.Where(s => s.Length > 0));
    [JsonIgnore] public string Initial => string.IsNullOrWhiteSpace(Name) ? "G" : Name[..1].ToUpperInvariant();
    [JsonIgnore] public string RatingLabel => Rating > 0 ? new string('★', Rating) + new string('☆', 5 - Rating) : "☆☆☆☆☆";
    [JsonIgnore] public string WishlistLabel => Wishlisted ? "♥ Saved" : "♡ Save";
    [JsonIgnore] public string InstalledLabel => Installed ? "Installed" : "Not installed";
    [JsonIgnore] public int Rating { get; set; }
    [JsonIgnore] public bool Wishlisted { get; set; }
    [JsonIgnore] public bool Installed { get; set; }
    private bool selected;
    [JsonIgnore] public bool Selected { get => selected; set { selected = value; Notify(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record Category(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed class Preferences
{
    public const string DefaultMountPath = @"E:\games";
    public const string LegacyDefaultMountPath = @"F:\Games";
    public const string DefaultFrozenProcessesPath = @"F:\study\Platforms\windows\autohotkey\mymainahk\frozen-processes.ini";
    public const string DefaultScriptFormat = "bat";
    public const string DefaultShellTarget = "native-linux";
    public string Theme { get; set; } = "dark";
    public string GridSize { get; set; } = "medium";
    public bool ShowTimes { get; set; } = true;
    public bool ShowCategories { get; set; } = true;
    public string DockerUsername { get; set; } = "michadockermisha";
    public string RepoName { get; set; } = "backup";
    public string MountPath { get; set; } = DefaultMountPath;
    public bool MinimizeToTray { get; set; } = true;
    public string FrozenProcessesPath { get; set; } = DefaultFrozenProcessesPath;
    public string SortBy { get; set; } = "Recently Added";
    public string ScriptFormat { get; set; } = DefaultScriptFormat;
    public string ShellTarget { get; set; } = DefaultShellTarget;
    public string WandPath { get; set; } = "";
    public string LastTab { get; set; } = "all";
    public double WindowWidth { get; set; } = 1440;
    public double WindowHeight { get; set; } = 900;
    public string WindowMonitorDeviceName { get; set; } = "";
    public int WindowBoundsLeftPixels { get; set; }
    public int WindowBoundsTopPixels { get; set; }
    public int WindowBoundsWidthPixels { get; set; }
    public int WindowBoundsHeightPixels { get; set; }
    public bool ShowHiddenCategories { get; set; }
}

public sealed class UserState
{
    // Keep older/newer profile fields across releases, even when this build
    // does not consume them (for example legacy installed-size measurements).
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public Preferences Settings { get; set; } = new();
    public HashSet<string> Wishlist { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> InstalledGames { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> Ratings { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<string>> GameTags { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> LaunchPaths { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> InstallationFolders { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> PendingGameBackups { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> PlayTimeSeconds { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, DateTime> LastPlayedUtc { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, LocalGame> LocalGames { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, LocalGame> WandGames { get; set; } = new(StringComparer.Ordinal);
    public List<PendingEdit> Pending { get; set; } = new();
    [JsonConverter(typeof(ExactIdentityJsonObjectConverter))]
    public JsonObject LocalCatalog { get; set; } = new();
}

// Property names on a model can be case-insensitive; dictionary game IDs
// cannot. JsonNode otherwise inherits the serializer's case-insensitive option
// and rejects valid legacy IDs such as AgainsttheStorm / againstthestorm.
public sealed class ExactIdentityJsonObjectConverter : JsonConverter<JsonObject>
{
    public override JsonObject? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        => JsonNode.Parse(ref reader, new JsonNodeOptions { PropertyNameCaseInsensitive = false })?.AsObject();
    public override void Write(Utf8JsonWriter writer, JsonObject value, JsonSerializerOptions options) => value.WriteTo(writer);
}

public sealed class PendingEdit
{
    public string Section { get; set; } = "gameCategories";
    public string Key { get; set; } = "";
    public JsonNode? Before { get; set; }
    public JsonNode? After { get; set; }
    public string? Conflict { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public static class DataJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };
    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options) ?? throw new FormatException("Empty data document.");
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static string Text(JsonNode? value, string fallback = "") => value is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;
    public static double Number(JsonNode? value)
    {
        if (value is JsonValue v)
        {
            if (v.TryGetValue<double>(out var number)) return double.IsFinite(number) ? number : 0;
            if (v.TryGetValue<long>(out var whole)) return whole;
            if (v.TryGetValue<int>(out var integer)) return integer;
            if (v.TryGetValue<decimal>(out var precise)) return (double)precise;
        }
        return double.TryParse(Text(value), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) ? parsed : 0;
    }
}

public static class Merge
{
    public static JsonNode? Get(JsonObject config, PendingEdit edit) => edit.Section == "gameCategories" ? config[edit.Section]?[edit.Key] : config[edit.Section];
    public static void Set(JsonObject config, PendingEdit edit)
    {
        if (edit.Section == "gameCategories")
        {
            var categories = config[edit.Section] as JsonObject ?? new JsonObject();
            if (config[edit.Section] == null) config[edit.Section] = categories;
            if (edit.After == null) categories.Remove(edit.Key); else categories[edit.Key] = edit.After.DeepClone();
        }
        else config[edit.Section] = edit.After?.DeepClone();
    }
    public static JsonObject Apply(JsonObject remote, IEnumerable<PendingEdit> edits)
    {
        var merged = (JsonObject)remote.DeepClone();
        foreach (var edit in edits)
        {
            var current = Get(remote, edit);
            edit.Conflict = !JsonNode.DeepEquals(current, edit.Before) && !JsonNode.DeepEquals(current, edit.After)
                ? $"{edit.Section}/{edit.Key} changed on the website. Review before publishing." : null;
            if (edit.Conflict == null) Set(merged, edit);
        }
        return merged;
    }
}
