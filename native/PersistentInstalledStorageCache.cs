using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GameLibrary.Native;

/// <summary>Small durable cache of complete, path-bound installed-file measurements.</summary>
internal sealed class PersistentInstalledStorageCache
{
    private const int CurrentSchemaVersion = 1;
    private const string MeasurementScope = "logical-file-bytes";
    private const string DisplayUnit = "GB";
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(10);
    private static readonly object Gate = new();
    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = false };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private readonly string path;

    internal PersistentInstalledStorageCache(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A cache path is required.", nameof(path));
        this.path = Path.GetFullPath(path);
    }

    internal InstalledStorageResult? Fresh(string gameId, string installationFolder, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(gameId) || string.IsNullOrWhiteSpace(installationFolder)) return null;
        string normalizedFolder;
        try { normalizedFolder = NormalizeFolder(installationFolder); }
        catch { return null; }
        if (!Directory.Exists(normalizedFolder)) return null;

        lock (Gate)
        {
            JsonObject? root = ReadRoot();
            if (root == null || !HasCurrentSchema(root) || root["measurements"] is not JsonObject measurements ||
                !measurements.TryGetPropertyValue(gameId, out var entryNode) || entryNode is not JsonObject entry ||
                !TryReadEntry(entry, normalizedFolder, out var result)) return null;

            DateTime nowUtc = now.Kind == DateTimeKind.Utc ? now : now.ToUniversalTime();
            TimeSpan age = nowUtc - result.MeasuredUtc;
            return age >= TimeSpan.Zero && age < Freshness ? result : null;
        }
    }

    internal bool RecordComplete(string gameId, string installationFolder, InstalledStorageResult result)
    {
        if (string.IsNullOrWhiteSpace(gameId) || string.IsNullOrWhiteSpace(installationFolder)) return false;
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Complete || result.Bytes < 0 || result.FileCount < 0 || result.SkippedCount != 0 || result.Reason != null)
            return false;

        string normalizedFolder;
        try { normalizedFolder = NormalizeFolder(installationFolder); }
        catch { return false; }
        if (!Directory.Exists(normalizedFolder)) return false;
        DateTime measuredUtc = result.MeasuredUtc.Kind == DateTimeKind.Utc
            ? result.MeasuredUtc
            : result.MeasuredUtc.ToUniversalTime();

        lock (Gate)
        {
            JsonObject root = ReadRootForUpdate();
            if (root["measurements"] is not JsonObject measurements)
            {
                if (root.ContainsKey("measurements"))
                    throw new InvalidDataException("Installed-storage cache measurements field is not an object.");
                measurements = new JsonObject(NodeOptions);
                root["measurements"] = measurements;
            }

            JsonObject? entry = null;
            if (measurements.TryGetPropertyValue(gameId, out var existingNode) && existingNode != null)
            {
                entry = existingNode as JsonObject
                    ?? throw new InvalidDataException("Installed-storage cache entry is not an object: " + gameId);
                if (TryReadEntry(entry, normalizedFolder, out var existing) && existing.MeasuredUtc >= measuredUtc)
                    return true;
            }
            entry ??= new JsonObject(NodeOptions);
            entry["installationFolder"] = normalizedFolder;
            entry["bytes"] = result.Bytes;
            entry["complete"] = true;
            entry["measuredUtc"] = measuredUtc.ToString("O", CultureInfo.InvariantCulture);
            entry["fileCount"] = result.FileCount;
            entry["skippedCount"] = 0;
            entry["scope"] = MeasurementScope;
            entry["unit"] = DisplayUnit;
            entry["decimalGb"] = result.Bytes / 1_000_000_000m;
            measurements[gameId] = entry;
            root["schemaVersion"] = CurrentSchemaVersion;
            WriteRootAtomically(root);
            return true;
        }
    }

    private JsonObject? ReadRoot()
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(path), NodeOptions) as JsonObject;
        }
        catch (JsonException) { return null; }
    }

    private JsonObject ReadRootForUpdate()
    {
        if (!File.Exists(path))
            return new JsonObject(NodeOptions)
            {
                ["schemaVersion"] = CurrentSchemaVersion,
                ["measurements"] = new JsonObject(NodeOptions)
            };

        JsonObject root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path), NodeOptions) as JsonObject
                ?? throw new InvalidDataException("Installed-storage cache root is not an object.");
        }
        catch (JsonException ex) { throw new InvalidDataException("Installed-storage cache is invalid JSON; refusing to replace it.", ex); }
        if (!HasCurrentSchema(root))
            throw new InvalidDataException("Installed-storage cache schema is unknown; refusing to replace it.");
        return root;
    }

    private static bool HasCurrentSchema(JsonObject root)
    {
        try { return root["schemaVersion"]?.GetValue<int>() == CurrentSchemaVersion; }
        catch { return false; }
    }

    private static bool TryReadEntry(JsonObject entry, string expectedFolder, out InstalledStorageResult result)
    {
        result = null!;
        try
        {
            if (entry["complete"]?.GetValue<bool>() != true ||
                entry["scope"]?.GetValue<string>() != MeasurementScope ||
                entry["unit"]?.GetValue<string>() != DisplayUnit ||
                entry["installationFolder"]?.GetValue<string>() is not string savedFolder ||
                !SameFolder(savedFolder, expectedFolder)) return false;

            long bytes = entry["bytes"]!.GetValue<long>();
            long fileCount = entry["fileCount"]!.GetValue<long>();
            long skippedCount = entry["skippedCount"]!.GetValue<long>();
            decimal decimalGb = entry["decimalGb"]!.GetValue<decimal>();
            string measuredText = entry["measuredUtc"]!.GetValue<string>();
            if (bytes < 0 || fileCount < 0 || skippedCount != 0 ||
                decimalGb != bytes / 1_000_000_000m ||
                !DateTime.TryParse(measuredText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var measuredUtc) ||
                measuredUtc.Kind != DateTimeKind.Utc) return false;

            result = new InstalledStorageResult(bytes, true, measuredUtc, fileCount, skippedCount, null);
            return true;
        }
        catch { return false; }
    }

    private static string NormalizeFolder(string folder) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    private static bool SameFolder(string left, string right)
    {
        try
        {
            return string.Equals(NormalizeFolder(left), NormalizeFolder(right), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private void WriteRootAtomically(JsonObject root)
    {
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true))
                { writer.Write(root.ToJsonString(WriteOptions)); writer.Flush(); }
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
