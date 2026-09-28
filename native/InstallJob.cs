using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;

namespace GameLibrary.Native;

internal enum InstallJobStage
{
    Preflight,
    ResolveDigest,
    Pull,
    CreateContainer,
    Extract,
    VerifyPayload,
    WaitForGame,
    Promote,
    VerifyPromoted,
    PublishCompletionMarker,
    RefreshLibrary,
    Completed
}

internal enum InstallJobStatus
{
    Queued,
    Running,
    PauseRequested,
    Paused,
    ResumeRequested,
    StopRequested,
    RetryableFailure,
    Failed,
    Stopped,
    Completed
}

internal enum InstallJobControlAction { None, Pause, Resume, Stop, Retry }

internal sealed class InstallJobRecord
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string OperationId { get; set; } = Guid.NewGuid().ToString("N");
    public string CanonicalGameId { get; set; } = "";
    public string SourceGameId { get; set; } = "";
    public List<string> SourceGameIds { get; set; } = new();
    public string ImageRepository { get; set; } = "";
    public string ImageTag { get; set; } = "";
    public string PinnedDigest { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public string StagingPath { get; set; } = "";
    public string InstalledPath { get; set; } = "";
    public string DockerContext { get; set; } = "";
    public string DockerHost { get; set; } = "";
    public string ShellTarget { get; set; } = "native-windows";
    public InstallJobStage Stage { get; set; } = InstallJobStage.Preflight;
    public InstallJobStatus Status { get; set; } = InstallJobStatus.Queued;
    public InstallJobControlAction RequestedAction { get; set; }
    public int AttemptCount { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastMeaningfulActivityUtc { get; set; } = DateTime.UtcNow;
    public InstallOwnedProcessIdentity? OwnedProcess { get; set; }
    public InstallOwnedProcessIdentity? WorkerProcess { get; set; }
    public string LastControlRequestId { get; set; } = "";
    public string OwnedContainerId { get; set; } = "";
    public string OwnedContainerName { get; set; } = "";
    public List<InstallFileCheckpoint> CompletedFiles { get; set; } = new();
    public string FailureReason { get; set; } = "";
    public string CompletionVerification { get; set; } = "";
    public string RollbackPath { get; set; } = "";
    public int ExtractionGeneration { get; set; }
    // Best-effort compressed bytes from the pinned image manifest, used to
    // render a true pull percentage even for cached or silent layers. Zero
    // means no authoritative total is available.
    public long ImageTotalBytes { get; set; }
    // Blob-size map keyed by 12-character lowercase digest prefix, matching
    // the short layer ids Docker prints during a pull.
    public Dictionary<string, long>? ImageLayerBytes { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? UnknownFields { get; set; }

    internal void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion) throw new InvalidDataException("Unsupported install-job schema version.");
        if (!Guid.TryParseExact(OperationId, "N", out _)) throw new InvalidDataException("The install operation ID must be a 32-character GUID.");
        if (string.IsNullOrWhiteSpace(CanonicalGameId) || string.IsNullOrWhiteSpace(SourceGameId)) throw new InvalidDataException("The install job has no canonical or source game identity.");
        if (SourceGameIds == null || SourceGameIds.Any(string.IsNullOrWhiteSpace) || !SourceGameIds.Contains(SourceGameId, StringComparer.Ordinal)) throw new InvalidDataException("The install job contains an invalid source identity.");
        if (string.IsNullOrWhiteSpace(ImageRepository) || string.IsNullOrWhiteSpace(ImageTag)) throw new InvalidDataException("The install job has no exact source image.");
        if (!DockerIdentity.ValidRepository(ImageRepository) || !DockerScripts.ValidTag(ImageTag)
            || !string.Equals(DockerIdentity.Create(ImageRepository, ImageTag), SourceGameId, StringComparison.Ordinal))
            throw new InvalidDataException("The source game identity does not match the exact repository and tag.");
        if (!Path.IsPathFullyQualified(DestinationPath) || !Path.IsPathFullyQualified(StagingPath) || !Path.IsPathFullyQualified(InstalledPath)) throw new InvalidDataException("Install destination, staging, and version paths must be absolute.");
        EnsureChildPath(DestinationPath, StagingPath, "staging");
        EnsureChildPath(DestinationPath, InstalledPath, "versioned install");
        if (AttemptCount < 0) throw new InvalidDataException("The install attempt count cannot be negative.");
        if (ExtractionGeneration < 0) throw new InvalidDataException("The extraction generation cannot be negative.");
        if (!string.IsNullOrEmpty(PinnedDigest) && !Regex.IsMatch(PinnedDigest, @"\Asha256:[0-9a-f]{64}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new InvalidDataException("The pinned image digest is invalid.");
        if (ImageTotalBytes < 0) throw new InvalidDataException("The image total bytes cannot be negative.");
        if (ImageLayerBytes != null)
        {
            if (ImageLayerBytes.Count > 500) throw new InvalidDataException("The image layer size map is unexpectedly large.");
            foreach (var pair in ImageLayerBytes)
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value < 0)
                    throw new InvalidDataException("An image layer size entry is invalid.");
        }
        foreach (InstallFileCheckpoint file in CompletedFiles)
        {
            file.Validate();
        }
        if (CompletedFiles.Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != CompletedFiles.Count)
            throw new InvalidDataException("The install job contains duplicate file checkpoints.");
    }

    private static void EnsureChildPath(string root, string child, string label)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(child));
        if (Path.IsPathRooted(relative) || relative == "." || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("The " + label + " path must be a child of the install destination.");
    }
}

internal sealed class InstallFileCheckpoint
{
    public string RelativePath { get; set; } = "";
    public long Length { get; set; }
    public string Sha256 { get; set; } = "";
    public DateTime VerifiedUtc { get; set; }

    internal void Validate()
    {
        string normalized = RelativePath.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith("/", StringComparison.Ordinal)
            || Path.IsPathRooted(normalized) || normalized.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("An install checkpoint contains a path outside staging.");
        if (Length < 0 || !Regex.IsMatch(Sha256, @"\A[0-9a-f]{64}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new InvalidDataException("An install checkpoint has invalid file integrity data.");
    }
}

internal sealed record InstallOwnedProcessIdentity(int ProcessId, long CreationFileTimeUtc, string ExecutablePath, string Transport);

internal sealed record InstallJobEvent(
    DateTime AtUtc,
    string Kind,
    string Stage,
    string Message,
    long? CompletedBytes = null,
    long? TotalBytes = null,
    long? CompletedFiles = null,
    long? TotalFiles = null,
    bool TotalsEstimated = false,
    IReadOnlyDictionary<string, string>? LayerProgress = null);

/// <summary>Durable manifest and bounded event history for one install operation.</summary>
internal sealed class InstallJobStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly JsonSerializerOptions EventJsonOptions = CreateEventJsonOptions();
    private readonly object sync = new();
    private readonly string jobsRoot;

    internal InstallJobStore(string profileRoot)
    {
        jobsRoot = Path.Combine(Path.GetFullPath(profileRoot), "jobs");
        Directory.CreateDirectory(jobsRoot);
    }

    internal string JobsRoot => jobsRoot;
    // Shared reader options: control files are written camelCase, so every
    // reader (terminal, controller, worker) must bind case-insensitively.
    internal static JsonSerializerOptions SharedJsonOptions => JsonOptions;
    internal string JobDirectory(string operationId) => Path.Combine(jobsRoot, ValidateOperationId(operationId));
    internal string ManifestPath(string operationId) => Path.Combine(JobDirectory(operationId), "job.json");
    internal string EventPath(string operationId) => Path.Combine(JobDirectory(operationId), "events.jsonl");
    internal string ControlDirectory(string operationId) => Path.Combine(JobDirectory(operationId), "controls");
    internal string WorkerLockPath(string operationId) => Path.Combine(JobDirectory(operationId), "worker.lock");

    internal InstallJobRecord Create(InstallJobRecord job)
    {
        if (job == null) throw new ArgumentNullException(nameof(job));
        job.Validate();
        lock (sync)
        {
            string directory = JobDirectory(job.OperationId);
            if (Directory.Exists(directory)) throw new IOException("An install job already exists for this operation ID.");
            Directory.CreateDirectory(directory);
            SaveCore(job);
            AppendEventCore(job.OperationId, new InstallJobEvent(DateTime.UtcNow, "created", job.Stage.ToString(), "Install job created."));
            return job;
        }
    }

    internal void Save(InstallJobRecord job)
    {
        if (job == null) throw new ArgumentNullException(nameof(job));
        job.UpdatedUtc = DateTime.UtcNow;
        job.Validate();
        lock (sync) SaveCore(job);
    }

    internal InstallJobRecord Load(string operationId)
    {
        string path = ManifestPath(operationId);
        InstallJobRecord job = JsonSerializer.Deserialize<InstallJobRecord>(ReadSharedText(path, "The install-job manifest was not found."), JsonOptions)
            ?? throw new InvalidDataException("The install-job manifest is empty.");
        job.Validate();
        if (!string.Equals(job.OperationId, operationId, StringComparison.Ordinal)) throw new InvalidDataException("The install-job folder and manifest identities do not match.");
        return job;
    }

    // The worker rewrites the manifest while the terminal, the parent window and
    // recovery controllers read it. A concurrent reader can momentarily observe
    // the file missing or locked, so read through a delete-sharing handle and
    // retry a bounded number of times instead of failing the reader.
    private static string ReadSharedText(string path, string missingMessage)
    {
        const int attempts = 80;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (FileNotFoundException) when (attempt < attempts) { Thread.Sleep(25); }
            catch (DirectoryNotFoundException) when (attempt < attempts) { Thread.Sleep(25); }
            catch (UnauthorizedAccessException) when (attempt < attempts) { Thread.Sleep(25); }
            catch (IOException) when (attempt < attempts) { Thread.Sleep(25); }
            catch (FileNotFoundException) { throw new FileNotFoundException(missingMessage, path); }
            catch (DirectoryNotFoundException) { throw new FileNotFoundException(missingMessage, path); }
        }
    }

    internal IReadOnlyList<InstallJobRecord> LoadAll()
    {
        var jobs = new List<InstallJobRecord>();
        foreach (string directory in Directory.EnumerateDirectories(jobsRoot))
        {
            string manifest = Path.Combine(directory, "job.json");
            if (!File.Exists(manifest)) continue;
            jobs.Add(Load(Path.GetFileName(directory)));
        }
        return jobs.OrderBy(job => job.CreatedUtc).ToArray();
    }

    internal void RecordActivity(InstallJobRecord job, InstallJobEvent item)
    {
        if (job == null) throw new ArgumentNullException(nameof(job));
        if (item == null) throw new ArgumentNullException(nameof(item));
        lock (sync)
        {
            job.LastMeaningfulActivityUtc = item.AtUtc == default ? DateTime.UtcNow : item.AtUtc;
            job.UpdatedUtc = DateTime.UtcNow;
            SaveCore(job);
            AppendEventCore(job.OperationId, item);
        }
    }

    internal IReadOnlyList<InstallJobEvent> ReadEvents(string operationId, int maximum = 2000)
    {
        if (maximum <= 0) throw new ArgumentOutOfRangeException(nameof(maximum));
        string path = EventPath(operationId);
        if (!File.Exists(path)) return Array.Empty<InstallJobEvent>();
        var result = new Queue<InstallJobEvent>();
        string content;
        try { content = ReadSharedText(path, "The install-job event log was not found."); }
        catch (FileNotFoundException) { return Array.Empty<InstallJobEvent>(); }
        int start = -1;
        int depth = 0;
        bool inString = false;
        bool escaped = false;
        for (int index = 0; index < content.Length; index++)
        {
            char value = content[index];
            if (start < 0)
            {
                if (value == '{' && (index == 0 || content[index - 1] == '\n'))
                {
                    start = index;
                    depth = 1;
                    inString = false;
                    escaped = false;
                }
                continue;
            }

            // A prior version wrote indented multi-line JSON objects. If rotation
            // retained only the tail of one object, resync at the next root object.
            if (!inString && value == '{' && index > 0 && content[index - 1] == '\n')
            {
                start = index;
                depth = 1;
                escaped = false;
                continue;
            }
            if (inString)
            {
                if (escaped) escaped = false;
                else if (value == '\\') escaped = true;
                else if (value == '"') inString = false;
                continue;
            }
            if (value == '"') { inString = true; continue; }
            if (value is '{' or '[') depth++;
            else if (value is '}' or ']') depth--;
            if (depth != 0) continue;

            try
            {
                InstallJobEvent? item = JsonSerializer.Deserialize<InstallJobEvent>(content.Substring(start, index - start + 1), JsonOptions);
                if (item != null)
                {
                    result.Enqueue(item);
                    while (result.Count > maximum) result.Dequeue();
                }
            }
            catch (JsonException) { }
            start = -1;
        }
        return result.ToArray();
    }

    internal IReadOnlyList<InstallJobControlRequest> ReadPendingControls(string operationId, string afterRequestId)
    {
        string directory = ControlDirectory(operationId);
        if (!Directory.Exists(directory)) return Array.Empty<InstallJobControlRequest>();
        var requests = new List<InstallJobControlRequest>();
        foreach (string path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.Ordinal))
        {
            if (path.EndsWith(".response.json", StringComparison.OrdinalIgnoreCase)) continue;
            string requestId = Path.GetFileNameWithoutExtension(path);
            if (File.Exists(Path.Combine(directory, requestId + ".response.json"))) continue;
            try
            {
                InstallJobControlRequest? request = JsonSerializer.Deserialize<InstallJobControlRequest>(File.ReadAllText(path), JsonOptions);
                if (request != null && string.Equals(request.RequestId, requestId, StringComparison.Ordinal) && request.IsValid(operationId)) requests.Add(request);
            }
            catch (IOException) { }
            catch (JsonException) { }
        }
        return requests.OrderBy(request => request.RequestedUtc).ThenBy(request => request.RequestId, StringComparer.Ordinal).ToArray();
    }

    internal static InstallJobControlResponse? ReadControlResponse(string path)
    {
        try { return JsonSerializer.Deserialize<InstallJobControlResponse>(File.ReadAllText(path, System.Text.Encoding.UTF8), JsonOptions); }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal void SaveControlResponse(InstallJobControlResponse response)
    {
        string operationId = ValidateOperationId(response.OperationId);
        string directory = ControlDirectory(operationId);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, response.RequestId + ".response.json");
        LibraryStore.AtomicWrite(path, JsonSerializer.Serialize(response, JsonOptions));
    }

    internal static string ValidateOperationId(string operationId)
    {
        if (!Guid.TryParseExact(operationId, "N", out _)) throw new ArgumentException("Invalid install operation ID.", nameof(operationId));
        return operationId;
    }

    private void SaveCore(InstallJobRecord job)
    {
        string path = ManifestPath(job.OperationId);
        WriteManifestAtomic(path, JsonSerializer.Serialize(job, JsonOptions));
    }

    // File.Replace (used by LibraryStore.AtomicWrite for state that needs a
    // rollback copy) briefly exposes a missing destination between its two
    // renames. The install manifest is read continuously by the terminal and
    // the worker, so replace it with a single overwriting move instead: an
    // atomic operation that never exposes an absent manifest.
    private static void WriteManifestAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream)) { writer.Write(text); writer.Flush(); stream.Flush(true); }
            const int attempts = 120;
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(temp, path, overwrite: true); return; }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < attempts) { Thread.Sleep(25); }
            }
        }
        finally { if (File.Exists(temp)) { try { File.Delete(temp); } catch (IOException) { } } }
    }

    private void AppendEventCore(string operationId, InstallJobEvent item)
    {
        string path = EventPath(operationId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item, EventJsonOptions) + Environment.NewLine);
        long writtenLength;
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
            writtenLength = stream.Length;
        }
        // Keep enough diagnostic history for recovery while bounding disk use.
        if (writtenLength > 8 * 1024 * 1024) RotateEvents(path);
    }

    private static void RotateEvents(string path)
    {
        string temp = path + ".rotate-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var input = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(output))
            {
                var tail = new Queue<string>();
                string? line;
                while ((line = input.ReadLine()) != null)
                {
                    tail.Enqueue(line);
                    while (tail.Count > 10000) tail.Dequeue();
                }
                foreach (string retained in tail) writer.WriteLine(retained);
                writer.Flush();
                output.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static JsonSerializerOptions CreateEventJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonOptions) { WriteIndented = false };
        return options;
    }
}

internal sealed record InstallJobControlRequest(int SchemaVersion, string OperationId, string RequestId, InstallJobControlAction Action, DateTime RequestedUtc)
{
    internal bool IsValid(string operationId) => SchemaVersion == 1
        && string.Equals(OperationId, operationId, StringComparison.Ordinal)
        && Guid.TryParseExact(RequestId, "N", out _)
        && Action is InstallJobControlAction.Pause or InstallJobControlAction.Resume or InstallJobControlAction.Stop or InstallJobControlAction.Retry;
}

internal sealed record InstallJobControlResponse(int SchemaVersion, string OperationId, string RequestId, bool Accepted, string Message, DateTime RespondedUtc);
