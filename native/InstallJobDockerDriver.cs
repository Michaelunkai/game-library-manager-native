using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

internal interface IDockerImageDigestResolver
{
    Task<string> ResolveAsync(string repository, string tag, CancellationToken cancellation);
}

/// <summary>Resolves an immutable public Docker Hub manifest digest before the worker starts its pull.</summary>
internal sealed class DockerHubImageDigestResolver : IDockerImageDigestResolver
{
    private const string ManifestAccept = "application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.list.v2+json, application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json";
    private readonly HttpClient http;

    internal DockerHubImageDigestResolver(HttpClient? http = null) => this.http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<string> ResolveAsync(string repository, string tag, CancellationToken cancellation)
    {
        var (digest, _, _, _) = await ResolveDigestAndSizeAsync(repository, tag, cancellation).ConfigureAwait(false);
        return digest;
    }

    // Resolves the pinned digest and the exact compressed blob sizes so pull
    // progress can be reported as a true percentage even when Docker stays
    // quiet or layers are already cached. Sizes are best-effort: any failure
    // yields TotalBytes = 0, an empty layer map, and a SizeError explanation.
    internal async Task<(string Digest, long TotalBytes, Dictionary<string, long> LayerBytes, string SizeError)> ResolveDigestAndSizeAsync(string repository, string tag, CancellationToken cancellation)
    {
        if (!DockerIdentity.ValidRepository(repository) || !DockerScripts.ValidTag(tag)) throw new ArgumentException("The exact Docker image identity is invalid.");
        string token = await GetRegistryTokenAsync(repository, cancellation).ConfigureAwait(false);
        string digest;
        try
        {
            digest = await ResolveDigestAsync(repository, tag, token, cancellation).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException or OperationCanceledException)
        {
            return ("", 0, new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase), "digest: " + ex.GetType().Name + ": " + ex.Message);
        }
        try
        {
            var sized = await ResolvePlatformManifestSizeAsync(repository, tag, token, cancellation).ConfigureAwait(false);
            return (sized.Digest, sized.TotalBytes, sized.LayerBytes, "");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException or OperationCanceledException)
        {
            return (digest, 0, new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase), "sizes: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private async Task<string> GetRegistryTokenAsync(string repository, CancellationToken cancellation)
    {
        string tokenUrl = "https://auth.docker.io/token?service=registry.docker.io&scope=" + Uri.EscapeDataString("repository:" + repository + ":pull");
        using HttpResponseMessage tokenResponse = await http.GetAsync(tokenUrl, cancellation).ConfigureAwait(false);
        tokenResponse.EnsureSuccessStatusCode();
        JsonNode tokenDocument = JsonNode.Parse(await tokenResponse.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false))
            ?? throw new InvalidDataException("Docker Hub did not return a registry token.");
        string token = DataJson.Text(tokenDocument["token"]);
        if (token.Length == 0) token = DataJson.Text(tokenDocument["access_token"]);
        if (token.Length == 0) throw new InvalidDataException("Docker Hub returned an empty registry token.");
        return token;
    }

    private async Task<string> ResolveDigestAsync(string repository, string tag, string token, CancellationToken cancellation)
    {
        string manifestUrl = "https://registry-1.docker.io/v2/" + repository + "/manifests/" + Uri.EscapeDataString(tag);
        using var request = new HttpRequestMessage(HttpMethod.Get, manifestUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd(ManifestAccept);
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Docker Hub manifest lookup failed with HTTP " + (int)response.StatusCode + ".", null, response.StatusCode);
        string? digest = response.Headers.TryGetValues("Docker-Content-Digest", out IEnumerable<string>? values) ? values.FirstOrDefault() : null;
        if (string.IsNullOrWhiteSpace(digest) || !Regex.IsMatch(digest, @"\Asha256:[0-9a-f]{64}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new InvalidDataException("Docker Hub did not provide a valid content digest for the selected tag.");
        return digest.ToLowerInvariant();
    }

    private async Task<(string Digest, long TotalBytes, Dictionary<string, long> LayerBytes)> ResolvePlatformManifestSizeAsync(string repository, string tag, string token, CancellationToken cancellation)
    {
        // Read the tag manifest. If it is a multi-arch index, follow the
        // linux/amd64 (or first) child before measuring.
        JsonNode tagDocument = await GetManifestDocumentAsync(repository, tag, token, cancellation).ConfigureAwait(false);
        JsonNode document = tagDocument;
        if (document["manifests"] is JsonArray children && children.Count > 0)
        {
            JsonNode? choice = null;
            foreach (var child in children)
            {
                string arch = DataJson.Text(child?["platform"]?["architecture"]);
                string os = DataJson.Text(child?["platform"]?["os"]);
                string childDigest = DataJson.Text(child?["digest"]);
                if (childDigest.Length == 0) continue;
                if (choice == null) choice = child;
                if (string.Equals(arch, "amd64", StringComparison.OrdinalIgnoreCase)
                    && (os.Length == 0 || string.Equals(os, "linux", StringComparison.OrdinalIgnoreCase))) { choice = child; break; }
            }
            string childDigestValue = DataJson.Text(choice?["digest"]);
            if (childDigestValue.Length == 0) throw new InvalidDataException("The Docker Hub manifest index has no usable platform.");
            document = await GetManifestDocumentAsync(repository, childDigestValue, token, cancellation).ConfigureAwait(false);
        }
        if (document["layers"] is not JsonArray layers || layers.Count == 0)
            throw new InvalidDataException("The Docker Hub image manifest has no layers.");
        long total = 0;
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in layers)
        {
            string digestValue = DataJson.Text(layer?["digest"]);
            double sizeValue = DataJson.Number(layer?["size"]);
            if (!digestValue.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || digestValue.Length < 20 || sizeValue <= 0 || sizeValue > long.MaxValue) continue;
            long size = (long)sizeValue;
            total = checked(total + size);
            string prefix = digestValue.Substring("sha256:".Length, Math.Min(12, digestValue.Length - "sha256:".Length)).ToLowerInvariant();
            map[prefix] = size;
        }
        if (total <= 0 || map.Count == 0) throw new InvalidDataException("The Docker Hub image manifest has no measurable layers.");
        string pinned = await ResolveDigestAsync(repository, tag, token, cancellation).ConfigureAwait(false);
        return (pinned, total, map);
    }

    private async Task<JsonNode> GetManifestDocumentAsync(string repository, string reference, string token, CancellationToken cancellation)
    {
        string manifestUrl = "https://registry-1.docker.io/v2/" + repository + "/manifests/" + Uri.EscapeDataString(reference);
        using var request = new HttpRequestMessage(HttpMethod.Get, manifestUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd(ManifestAccept);
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Docker Hub manifest fetch failed with HTTP " + (int)response.StatusCode + ".", null, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false))
            ?? throw new InvalidDataException("Docker Hub returned an unreadable image manifest.");
    }
}

/// <summary>
/// True pull percentage from Docker layer text plus the authoritative manifest
/// sizes. Layers that complete without a byte ratio still count by their exact
/// blob size, and cached layers count in full, so the aggregate never stalls
/// just because Docker stayed quiet.
/// </summary>
internal static class DockerProgressBytes
{
    internal static (long Completed, long Total) Compute(
        IReadOnlyDictionary<string, string>? layers,
        IReadOnlyDictionary<string, long>? expectedByPrefix,
        long imageTotalBytes)
    {
        long done = 0, reported = 0;
        if (layers != null)
        {
            foreach (var pair in layers)
            {
                string id = pair.Key ?? "";
                long expected = 0;
                if (expectedByPrefix != null && id.Length >= 8)
                {
                    string prefix = id.Substring(0, Math.Min(12, id.Length)).ToLowerInvariant();
                    expectedByPrefix.TryGetValue(prefix, out expected);
                }
                var parsed = InstallProcessRunner.ParseLayerBytes(pair.Value ?? "");
                if (parsed.Done >= 0)
                {
                    long d = expected > 0 ? Math.Min(parsed.Done, expected) : parsed.Done;
                    long t = expected > 0 ? expected : Math.Max(parsed.Total, 0);
                    done += d;
                    reported += t;
                }
                else if (InstallProcessRunner.IsLayerComplete(pair.Value ?? "") && expected > 0)
                {
                    done += expected;
                    reported += expected;
                }
            }
        }
        long total = imageTotalBytes > 0 ? imageTotalBytes : reported;
        if (done > total) done = total;
        return (done, total);
    }

    internal static double? Percent(long completed, long total) => total > 0 ? 100.0 * completed / total : null;
}

/// <summary>Native staged installer backed by exact Docker processes owned by one durable job.</summary>
internal sealed class DockerInstallJobDriver : IInstallJobDriver
{
    private readonly string dockerExecutable;
    private readonly InstallProcessRunner processes;
    private readonly IDockerImageDigestResolver digestResolver;
    private readonly GameOperationCoordinator operations;
    private readonly Func<string, Task>? refreshLibrary;

    internal DockerInstallJobDriver(
        GameOperationCoordinator operations,
        InstallProcessRunner? processes = null,
        IDockerImageDigestResolver? digestResolver = null,
        string? dockerExecutable = null,
        Func<string, Task>? refreshLibrary = null)
    {
        this.operations = operations ?? throw new ArgumentNullException(nameof(operations));
        this.processes = processes ?? new InstallProcessRunner();
        this.digestResolver = digestResolver ?? new DockerHubImageDigestResolver();
        this.dockerExecutable = dockerExecutable ?? DockerScripts.Executable;
        this.refreshLibrary = refreshLibrary;
    }

    public Task<InstallStageOutcome> ExecuteStageAsync(InstallJobContext context, InstallJobStage stage, IProgress<InstallJobProgress> progress, CancellationToken cancellation)
    {
        return stage switch
        {
            InstallJobStage.Preflight => PreflightAsync(context, progress, cancellation),
            InstallJobStage.ResolveDigest => ResolveDigestAsync(context, progress, cancellation),
            InstallJobStage.Pull => PullAsync(context, progress, cancellation),
            InstallJobStage.CreateContainer => CreateContainerAsync(context, progress, cancellation),
            InstallJobStage.Extract => ExtractAsync(context, progress, cancellation),
            InstallJobStage.VerifyPayload => VerifyPayloadAsync(context, progress, cancellation),
            InstallJobStage.WaitForGame => WaitForGameAsync(context, progress, cancellation),
            InstallJobStage.Promote => PromoteAsync(context, progress, cancellation),
            InstallJobStage.VerifyPromoted => VerifyPromotedAsync(context, progress, cancellation),
            InstallJobStage.PublishCompletionMarker => PublishCompletionMarkerAsync(context, progress, cancellation),
            InstallJobStage.RefreshLibrary => RefreshLibraryAsync(context, progress, cancellation),
            InstallJobStage.Completed => Task.FromResult(InstallStageOutcome.Success("The install was already complete.")),
            _ => Task.FromResult(InstallStageOutcome.Failure("The install job contains an unknown stage."))
        };
    }

    private async Task<InstallStageOutcome> PreflightAsync(InstallJobContext context, IProgress<InstallJobProgress> progress, CancellationToken cancellation)
    {
        InstallJobRecord job = context.Snapshot();
        InstallJobTargetSupport support = InstallWorkerCapability.Check(job.ShellTarget);
        if (!support.Supported) return InstallStageOutcome.Failure(support.Message);
        try { Directory.CreateDirectory(job.DestinationPath); Directory.CreateDirectory(Path.GetDirectoryName(job.StagingPath)!); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return InstallStageOutcome.Failure("Install destination preflight failed: " + ex.Message); }
        Report(progress, InstallJobStage.Preflight, "Checking the selected Docker target.");
        InstallProcessResult result = await RunDockerAsync(context, job, new[] { "info", "--format", "{{.OSType}}" }, InstallCommandKind.Connection, progress, cancellation).ConfigureAwait(false);
        if (!result.Succeeded) return FromProcessFailure("Docker target preflight", result);
        if (!string.IsNullOrWhiteSpace(job.DockerContext) || !string.IsNullOrWhiteSpace(job.DockerHost)) return InstallStageOutcome.Success("The selected Docker target is reachable.", "docker-target-reachable");
        InstallProcessResult current = await RunDockerAsync(context, job, new[] { "context", "show" }, InstallCommandKind.Metadata, progress, cancellation).ConfigureAwait(false);
        if (!current.Succeeded) return FromProcessFailure("Could not record the active Docker context", current);
        string contextName = current.StandardOutputTail.Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        if (contextName.Length == 0) return InstallStageOutcome.Retry("Docker did not return a current context name.");
        context.Update(record => record.DockerContext = contextName, "target", "Recorded Docker context: " + contextName);
        return InstallStageOutcome.Success("The selected Docker target is reachable.", "docker-target-reachable");
    }

    private async Task<InstallStageOutcome> ResolveDigestAsync(InstallJobContext context, IProgress<InstallJobProgress> progress, CancellationToken cancellation)
    {
        InstallJobRecord job = context.Snapshot();
        if (Regex.IsMatch(job.PinnedDigest, @"\Asha256:[0-9a-f]{64}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return InstallStageOutcome.Success("The job retained its previously pinned image digest.", "digest=" + job.PinnedDigest.ToLowerInvariant());
        try
        {
            Report(progress, InstallJobStage.ResolveDigest, "Resolving the exact published image digest before download.");
            string digest;
            long imageTotalBytes = 0;
            Dictionary<string, long>? imageLayerBytes = null;
            if (digestResolver is DockerHubImageDigestResolver hubResolver)
            {
                // Single call returns the pinned digest plus the exact compressed
                // blob sizes used for a true pull percentage. Any size failure
                // leaves the authoritative-zero fallback.
                var sized = await hubResolver.ResolveDigestAndSizeAsync(job.ImageRepository, job.ImageTag, cancellation).ConfigureAwait(false);
                digest = sized.Digest;
                if (sized.TotalBytes > 0 && sized.LayerBytes.Count > 0)
                {
                    imageTotalBytes = sized.TotalBytes;
                    imageLayerBytes = sized.LayerBytes;
                }
                else if (sized.SizeError.Length > 0)
                {
                    Report(progress, InstallJobStage.ResolveDigest, "Image size lookup unavailable (" + sized.SizeError + "); pull percentage will use reported bytes.");
                }
            }
            else
            {
                digest = await digestResolver.ResolveAsync(job.ImageRepository, job.ImageTag, cancellation).ConfigureAwait(false);
            }
            if (!Regex.IsMatch(digest, @"\Asha256:[0-9a-f]{64}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return InstallStageOutcome.Failure("The image digest resolver returned an invalid digest.");
            string pinned = digest.ToLowerInvariant();
            string installedPath = Path.Combine(job.DestinationPath, DockerScripts.VersionedInstallFolder(job.SourceGameId, pinned));
            context.Update(record => { record.PinnedDigest = pinned; record.InstalledPath = installedPath;
                if (imageTotalBytes > 0) { record.ImageTotalBytes = imageTotalBytes; record.ImageLayerBytes = imageLayerBytes; } },
                "digest-pinned", "Pinned image digest and version-scoped install path: " + pinned
                + (imageTotalBytes > 0 ? " (" + imageTotalBytes + " compressed bytes)." : "."));
            return InstallStageOutcome.Success("The selected image tag is pinned to an immutable digest.", "digest=" + digest.ToLowerInvariant());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            return InstallStageOutcome.Retry("Could not resolve the image digest: " + ex.Message);
        }
    }

    private async Task<InstallStageOutcome> PullAsync(InstallJobContext context, IProgress<InstallJobProgress> progress, CancellationToken cancellation)
    {
        InstallJobRecord job = context.Snapshot();
        string image = PinnedImage(job);
        using IDisposable download = await operations.AcquireDownloadAsync(job.CanonicalGameId, job.DestinationPath, cancellation).ConfigureAwait(false);
        Report(progress, InstallJobStage.Pull, "Pulling the pinned image with live byte progress. An interrupted layer resumes automatically.");
        try
        {
            // The Engine API reports progressDetail bytes per layer, so the
            // terminal percentage climbs continuously even while a piped
            // docker pull would stay silent on a large layer.
            var engine = new DockerEnginePull(dockerExecutable, job.DockerContext, job.DockerHost);
            string reference = job.PinnedDigest;
            if (!reference.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) reference = "latest";
            string outcome = await engine.PullAsync(job.ImageRepository, reference, InstallJobStage.Pull, progress, job.ImageLayerBytes, job.ImageTotalBytes, cancellation).ConfigureAwait(false);
            if (outcome.StartsWith("ERROR:", StringComparison.Ordinal))
                return InstallStageOutcome.Retry("Pinned image pull: " + outcome.Substring("ERROR:".Length));
            return InstallStageOutcome.Success("The pinned image is available.", "image=" + image);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.Net.Http.HttpRequestException)
        {
            // Engine-API progress unavailable (older Docker, remote engine);
            // fall back to the owned CLI pull which still completes and resumes
            // cached layers.
            Report(progress, InstallJobStage.Pull, "Engine progress unavailable (" + ex.Message + "); using the docker CLI pull.");
            InstallProcessResult result = await RunDockerAsync(context, job, new[] { "pull", image }, InstallCommandKind.Download, progress, cancellation).ConfigureAwait(false);
            if (!result.Succeeded) return FromProcessFailure("Pinned image pull", result);
            return InstallStageOutcome.Success("The pinned image is available.", "image=" + image);
        }
    }

    private async Task<InstallStageOutcome> CreateContainerAsync(InstallJobContext context, IProgress<InstallJobProgress> progress, CancellationToken cancellation)
    {
        InstallJobRecord job = context.Snapshot();
        string sourceId = job.SourceGameId;
        string containerName = DockerScripts.ContainerNameForDestination(sourceId, job.DestinationPath);
        InstallProcessResult existing = await RunDockerAsync(context, job, new[] { "container", "inspect", containerName, "--format", DockerScripts.OwnedContainerInspectFormat }, InstallCommandKind.Metadata, progress, cancellation).ConfigureAwait(false);
        if (existing.Succeeded)
        {
            (string containerId, string labels) = ParseContainerInspect(existing.StandardOutputTail);
            if (!DockerScripts.OwnershipMatches(DockerScripts.OwnershipFromLabelsJson(labels), sourceId) || !DockerScripts.OperationMatches(labels, job.OperationId))
                return InstallStageOutcome.Failure("A container with the reserved name belongs to another operation; it was left untouched.");
            context.Update(record => { record.OwnedContainerName = containerName; record.OwnedContainerId = containerId; }, "container-reconciled", "Reconciled the container already owned by this operation.");
            return InstallStageOutcome.Success("The existing operation-owned extraction container was reconciled.");
        }
        if (existing.ExitCode != 1 || !existing.StandardErrorTail.Contains("No such object", StringComparison.OrdinalIgnoreCase)
            && !existing.StandardErrorTail.Contains("No such container", StringComparison.OrdinalIgnoreCase))
            return FromProcessFailure("Extraction container inspection", existing);

        var args = new List<string> { "container", "create", "--name", containerName,
            "--env", "GLM_INSTALL_OPERATION_ID=" + job.OperationId,
            "--label", "com.gamelibrary.owner=native",
            "--label", "com.gamelibrary.game-id=" + sourceId,
            "--label", DockerScripts.OperationLabel + "=" + job.OperationId,
            PinnedImage(job) };
        InstallProcessResult create = await RunDockerAsync(context, job, args, InstallCommandKind.Metadata, progress, cancellation).ConfigureAwait(false);
        if (!create.Succeeded) return FromProcessFailure("Extraction container creation", create);
        string id = create.StandardOutputTail.Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        if (!Regex.IsMatch(id, @"\A[0-9a-f]{12,64}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return InstallStageOutcome.Retry("Docker created a container but did not return a verifiable container identity.");
        context.Update(record => { record.OwnedContainerName = containerName; record.OwnedContainerId = id; }, "container-created", "Created the operation-owned extraction container.");
        return InstallStageOutcome.Success("The extraction container is owned by this install operation.");
    }

    private async Task<InstallStageOutcome> ExtractAsync(InstallJobContext context, IProgress<InstallJobProgress> progress, CancellationToken cancellation)
    {
        InstallJobRecord job = context.Snapshot();
        string containerName = job.OwnedContainerName;
        if (string.IsNullOrWhiteSpace(containerName)) return InstallStageOutcome.Retry("The owned extraction container identity was not recorded; retry from container creation.");
        // The extraction container can disappear after a Docker Desktop restart
        // or a daemon recovery. Recreate it here instead of failing the extract
        // stage forever; CreateContainerAsync inspects, reconciles, or creates.
        InstallStageOutcome containerReady = await CreateContainerAsync(context, progress, cancellation).ConfigureAwait(false);
        if (!containerReady.Succeeded) return containerReady;
        job = context.Snapshot();
        await VerifyCheckpointFilesAsync(job.StagingPath, job.CompletedFiles, context, cancellation).ConfigureAwait(false);
        job = context.Snapshot();
        int generation = job.ExtractionGeneration + 1;
        context.Update(record => record.ExtractionGeneration = generation, "extraction-started", "Started extraction generation " + generation + ".");
        job = context.Snapshot();
        string pending = PendingPath(job);
        Directory.CreateDirectory(pending);
        using var activity = new FileSystemActivity(pending);
        Report(progress, InstallJobStage.Extract, "Extracting into job-owned staging; file hashes are verified before checkpoints are reused.");
        // "docker cp" prints no progress of its own, so report the growing
        // staging copy directly. This is real extracted bytes, not an estimate.
        using var copyProgressCancellation = new CancellationTokenSource();
        Task copyProgress = Task.Run(async () =>
        {
            while (!copyProgressCancellation.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(2), copyProgressCancellation.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                if (copyProgressCancellation.IsCancellationRequested) break;
                long bytes = 0;
                int files = 0;
                try
                {
                    foreach (string file in Directory.EnumerateFiles(pending, "*", SearchOption.AllDirectories))
                    {
                        try { bytes = checked(bytes + new FileInfo(file).Length); files++; }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                progress.Report(new InstallJobProgress(DateTime.UtcNow, InstallJobStage.Extract,
                    "Copying image payload into staging (" + files + " files, " + bytes + " B so far).",
                    CompletedBytes: bytes));
            }
        }, copyProgressCancellation.Token);
        InstallProcessResult result;
        try
        {
            result = await RunDockerAsync(context, job, new[] { "cp", "--follow-link", containerName + ":/home/.", pending }, InstallCommandKind.Extraction, progress, cancellation,
                verifiedWorkActivity: _ => Task.FromResult(activity.ConsumeActivity())).ConfigureAwait(false);
        }
        finally
        {
            copyProgressCancellation.Cancel();
            try { await copyProgress.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        if (!result.Succeeded) return FromProcessFailure("Image extraction", result);

        string staging = Path.GetFullPath(job.StagingPath);
        Directory.CreateDirectory(staging);
        IReadOnlyList<string> files;
        try { files = EnumerateSafeFiles(pending); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return InstallStageOutcome.Failure("Extracted payload failed path validation: " + ex.Message); }
        long totalSize = 0;
        foreach (string source in files)
        {
            try { totalSize = checked(totalSize + new FileInfo(source).Length); }
            catch (Exception ex) when (ex is IOException or OverflowException) { }
        }
        long total = 0;
        int index = 0;
        foreach (string source in files)
        {
            cancellation.ThrowIfCancellationRequested();
            string relative = RelativePayloadPath(pending, source);
            string destination = SafeChild(staging, relative);
            FileInfo sourceInfo = new(source);
            long length = sourceInfo.Length;
            string sourceHash = await HashFileAsync(source, cancellation).ConfigureAwait(false);
            if (new FileInfo(source).Length != length) return InstallStageOutcome.Retry("An extracted file changed while its integrity hash was being read: " + relative);
            bool reusable = await IsCheckpointValidAsync(destination, relative, length, sourceHash, job.CompletedFiles, cancellation).ConfigureAwait(false);
            if (!reusable)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                string incomplete = destination + ".glm-incomplete-" + job.OperationId;
                await CopyVerifiedAsync(source, incomplete, sourceHash, cancellation).ConfigureAwait(false);
                File.Move(incomplete, destination, true);
                context.RecordCheckpoint(new InstallFileCheckpoint { RelativePath = relative, Length = length, Sha256 = sourceHash, VerifiedUtc = DateTime.UtcNow });
            }
            total = checked(total + length);
            index++;
            progress.Report(new InstallJobProgress(DateTime.UtcNow, InstallJobStage.Extract, "Verified staged payload file " + index + "/" + files.Count + ".", total, totalSize > 0 ? totalSize : null, index, files.Count, TotalsEstimated: false));
        }
        if (files.Count == 0) return InstallStageOutcome.Failure("The image contains no files under /home and cannot be installed as a playable game.");
        try { Directory.Delete(pending, recursive: true); } catch (IOException) { }
        return InstallStageOutcome.Success("Extracted " + files.Count + " files and verified their hashes.", "files=" + files.Count + ";bytes=" + total);
    }

    private async Task<InstallStageOutcome> VerifyPayloadAsync(InstallJobContext context, IProgress<InstallJobProgress> progress, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        InstallJobRecord job = context.Snapshot();
        await VerifyCheckpointFilesAsync(job.StagingPath, job.CompletedFiles, context, cancellation).ConfigureAwait(false);
        job = context.Snapshot();
        if (job.CompletedFiles.Count == 0) return InstallStageOutcome.Failure("The staged payload has no verified file checkpoints.");
        if (!HasPlayableExecutable(job.StagingPath)) return InstallStageOutcome.Failure("The image contains no playable Windows executable; the existing installation was preserved.");
        Report(progress, InstallJobStage.VerifyPayload, "Verified staged files and a playable executable.");
        return InstallStageOutcome.Success("The staged payload passed file and executable verification.", "payload-valid;files=" + job.CompletedFiles.Count);
    }

    private async Task<InstallStageOutcome> WaitForGameAsync(InstallJobContext context, IProgress<InstallJobProgress> progress, CancellationToken cancellation)
    {
        InstallJobRecord job = context.Snapshot();
        string installation = FinalInstallPath(job);
        Report(progress, InstallJobStage.WaitForGame, "Waiting only for a process using this installation before promotion.");
        IReadOnlyList<RunningInstallationProcess> active = await operations.WaitForInstallationIdleAsync(installation, TimeSpan.FromMilliseconds(500), processes =>
        {
            string identities = string.Join(", ", processes.Select(process => process.ProcessName + " (PID " + process.ProcessId + ")"));
            progress.Report(new InstallJobProgress(DateTime.UtcNow, InstallJobStage.WaitForGame, "Promotion is waiting for " + identities + "."));
        }, cancellation).ConfigureAwait(false);
        return InstallStageOutcome.Success(active.Count == 0 ? "No process uses this installation." : "The installation is idle.");
    }

    private async Task<InstallStageOutcome> PromoteAsync(InstallJobContext context, IProgress<InstallJobProgress> progress, CancellationToken cancellation)
    {
        InstallJobRecord job = context.Snapshot();
        string finalPath = FinalInstallPath(job);
        using IDisposable operation = await operations.AcquirePromotionAsync(job.CanonicalGameId, finalPath, cancellation).ConfigureAwait(false);
        await operations.WaitForInstallationIdleAsync(finalPath, TimeSpan.FromMilliseconds(500), active =>
        {
            string names = string.Join(", ", active.Select(process => process.ProcessName + " (PID " + process.ProcessId + ")"));
            progress.Report(new InstallJobProgress(DateTime.UtcNow, InstallJobStage.Promote, "Promotion is waiting for " + names + "."));
        }, cancellation).ConfigureAwait(false);
        if (!HasPlayableExecutable(job.StagingPath)) return InstallStageOutcome.Failure("The staged payload no longer passes playable-file verification.");
        if (Directory.Exists(finalPath))
        {
            bool exactExisting = await MatchesCheckpointsAsync(finalPath, job.CompletedFiles, cancellation).ConfigureAwait(false) && HasPlayableExecutable(finalPath);
            if (!exactExisting) return InstallStageOutcome.Failure("The digest-scoped version folder already exists but does not match this payload. It was preserved without replacement.");
            return InstallStageOutcome.Success("The exact digest-scoped version is already installed and matches this payload; no files were replaced.", "already-installed=" + finalPath);
        }
        if (!Directory.Exists(job.StagingPath)) return InstallStageOutcome.Failure("Neither the staged payload nor its digest-scoped install path exists.");

        context.Update(record => record.RollbackPath = "", "promotion-started", "Beginning the short promotion into a new digest-scoped installation path.");
        try
        {
            Directory.Move(job.StagingPath, finalPath);
            if (!HasPlayableExecutable(finalPath)) throw new InvalidDataException("Promoted installation failed its executable check.");
            return InstallStageOutcome.Success("The verified payload was promoted into a new digest-scoped folder. Existing installations remain untouched.", "promoted=" + finalPath);
        }
        catch (Exception ex)
        {
            if (!Directory.Exists(job.StagingPath) && Directory.Exists(finalPath)
                && await MatchesCheckpointsAsync(finalPath, job.CompletedFiles, cancellation).ConfigureAwait(false))
                return InstallStageOutcome.Success("The directory move completed before the worker observed the result; the promoted files were reverified.", "promoted=" + finalPath);
            return InstallStageOutcome.Failure("Promotion failed; the previous installation was not touched and staged recovery data remains: " + ex.Message);
        }
    }

    private async Task<InstallStageOutcome> VerifyPromotedAsync(InstallJobContext context, IProgress<InstallJobProgress> progress, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        InstallJobRecord job = context.Snapshot();
        string final = FinalInstallPath(job);
        if (!HasPlayableExecutable(final)) return InstallStageOutcome.Failure("The promoted installation does not contain a playable executable.");
        foreach (InstallFileCheckpoint checkpoint in job.CompletedFiles)
        {
            cancellation.ThrowIfCancellationRequested();
            string path = SafeChild(final, checkpoint.RelativePath);
            if (!File.Exists(path) || new FileInfo(path).Length != checkpoint.Length) return InstallStageOutcome.Failure("Promoted file is missing or has a different size: " + checkpoint.RelativePath);
            if (!string.Equals(await HashFileAsync(path, cancellation).ConfigureAwait(false), checkpoint.Sha256, StringComparison.OrdinalIgnoreCase))
                return InstallStageOutcome.Failure("Promoted file failed its hash check: " + checkpoint.RelativePath);
        }
        Report(progress, InstallJobStage.VerifyPromoted, "Verified the promoted installation against staged file hashes.");
        string verification = "promoted-files=" + job.CompletedFiles.Count + ";playable-executable=true;digest=" + job.PinnedDigest;
        context.Update(record => record.CompletionVerification = verification, "promotion-verified", verification);
        return InstallStageOutcome.Success("The promoted installation passed its manifest checks.", verification);
    }

    private async Task<InstallStageOutcome> PublishCompletionMarkerAsync(InstallJobContext context, IProgress<InstallJobProgress> progress, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        InstallJobRecord job = context.Snapshot();
        string final = FinalInstallPath(job);
        if (!HasPlayableExecutable(final)) return InstallStageOutcome.Failure("Completion marker was withheld because the promoted payload is no longer valid.");
        foreach (InstallFileCheckpoint checkpoint in job.CompletedFiles)
        {
            cancellation.ThrowIfCancellationRequested();
            string path = SafeChild(final, checkpoint.RelativePath);
            if (!File.Exists(path) || new FileInfo(path).Length != checkpoint.Length
                || !string.Equals(await HashFileAsync(path, cancellation).ConfigureAwait(false), checkpoint.Sha256, StringComparison.OrdinalIgnoreCase))
                return InstallStageOutcome.Failure("Completion marker was withheld because an installed file failed verification: " + checkpoint.RelativePath);
        }
        string sourceId = job.SourceGameId;
        string marker = Path.Combine(final, DockerScripts.CompletionMarkerName);
        string markerText = "GameLibraryManager|" + sourceId + "|" + job.OperationId;
        if (File.Exists(marker))
        {
            string existing = File.ReadAllText(marker).Trim();
            if (!existing.StartsWith("GameLibraryManager|" + sourceId + "|", StringComparison.Ordinal))
                return InstallStageOutcome.Failure("An existing completion marker belongs to a different source identity; it was preserved.");
            string priorOperation = existing[("GameLibraryManager|" + sourceId + "|").Length..];
            if (!Guid.TryParseExact(priorOperation, "N", out _))
                return InstallStageOutcome.Failure("An existing completion marker has an invalid operation identity; it was preserved.");
            if (!string.Equals(existing, markerText, StringComparison.Ordinal)) LibraryStore.AtomicWrite(marker, markerText + Environment.NewLine);
        }
        else LibraryStore.AtomicWrite(marker, markerText + Environment.NewLine);
        string readback = File.ReadAllText(marker).Trim();
        if (!string.Equals(readback, markerText, StringComparison.Ordinal)) return InstallStageOutcome.Failure("The completion marker did not match this exact game source and operation.");
        context.Update(record => record.CompletionVerification += ";marker=" + markerText, "completion-marker", "Published and read back the verified game and operation marker.");
        Report(progress, InstallJobStage.PublishCompletionMarker, "Published the verified completion marker.");
        return InstallStageOutcome.Success("The completion marker matches the exact source game and operation.");
    }

    private async Task<InstallStageOutcome> RefreshLibraryAsync(InstallJobContext context, IProgress<InstallJobProgress> progress, CancellationToken cancellation)
    {
        InstallJobRecord job = context.Snapshot();
        if (refreshLibrary != null) await refreshLibrary(job.CanonicalGameId).ConfigureAwait(false);
        await RemoveOwnedContainerAsync(context, cancellation).ConfigureAwait(false);
        Report(progress, InstallJobStage.RefreshLibrary, "Library state refreshed from the verified installation.");
        return InstallStageOutcome.Success("The verified install is ready in the library.");
    }

    private async Task<InstallProcessResult> RunDockerAsync(
        InstallJobContext context,
        InstallJobRecord job,
        IEnumerable<string> arguments,
        InstallCommandKind kind,
        IProgress<InstallJobProgress> progress,
        CancellationToken cancellation,
        Func<CancellationToken, Task<bool>>? verifiedWorkActivity = null)
    {
        var start = new ProcessStartInfo(dockerExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = job.DestinationPath
        };
        AddDockerTarget(start, job);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        // Every Docker command must be observably alive even while the child is
        // silent (a large layer can go seconds without a status line). The output
        // callback stamps each line, and a heartbeat reports at least every few
        // seconds so the terminal never sits still.
        InstallJobStage commandStage = job.Stage;
        DateTimeOffset commandStarted = DateTimeOffset.UtcNow;
        long lastOutputTicks = commandStarted.UtcTicks;
        long lastCompleted = 0, lastTotal = 0;
        IReadOnlyDictionary<string, string>? latestLayers = null;
        int downloadCompleteFlag = 0;
        void ReportCommandOutput(InstallProcessOutput output)
        {
            Interlocked.Exchange(ref lastOutputTicks, DateTimeOffset.UtcNow.UtcTicks);
            if (output.LayerProgress != null) latestLayers = output.LayerProgress;
            var (done, total) = DockerProgressBytes.Compute(output.LayerProgress, job.ImageLayerBytes, job.ImageTotalBytes);
            long? reportedCompleted = total > 0 ? done : output.CompletedBytes;
            long? reportedTotal = total > 0 ? total : output.TotalBytes;
            if (reportedCompleted.HasValue) Interlocked.Exchange(ref lastCompleted, reportedCompleted.Value);
            if (reportedTotal.HasValue) Interlocked.Exchange(ref lastTotal, reportedTotal.Value);
            // Once every byte is downloaded, the remaining silent work is Docker
            // extracting/finalizing. That must never be mistaken for a stall.
            if (kind == InstallCommandKind.Download && total > 0 && done >= total)
                Interlocked.Exchange(ref downloadCompleteFlag, 1);
            progress.Report(new InstallJobProgress(output.AtUtc, commandStage,
                output.Stream.Equals("stderr", StringComparison.OrdinalIgnoreCase) ? "[stderr] " + output.Text : output.Text,
                CompletedBytes: reportedCompleted,
                TotalBytes: reportedTotal,
                LayerProgress: output.LayerProgress));
        }
        Func<CancellationToken, Task<bool>>? effectiveVerifiedWork = verifiedWorkActivity;
        if (kind == InstallCommandKind.Download)
        {
            var innerVerifiedWork = effectiveVerifiedWork;
            effectiveVerifiedWork = async token =>
            {
                if (Volatile.Read(ref downloadCompleteFlag) != 0) return true;
                if (innerVerifiedWork != null) return await innerVerifiedWork(token).ConfigureAwait(false);
                return false;
            };
        }
        using var heartbeatCancellation = new CancellationTokenSource();
        Task heartbeat = Task.Run(async () =>
        {
            while (!heartbeatCancellation.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(3), heartbeatCancellation.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                if (heartbeatCancellation.IsCancellationRequested) break;
                var quietFor = DateTimeOffset.UtcNow - new DateTimeOffset(Interlocked.Read(ref lastOutputTicks), TimeSpan.Zero);
                var elapsed = DateTimeOffset.UtcNow - commandStarted;
                var sized = DockerProgressBytes.Compute(latestLayers, job.ImageLayerBytes, job.ImageTotalBytes);
                long completed = sized.Total > 0 ? sized.Completed : Interlocked.Read(ref lastCompleted);
                long total = sized.Total > 0 ? sized.Total : Interlocked.Read(ref lastTotal);
                Interlocked.Exchange(ref lastCompleted, completed);
                Interlocked.Exchange(ref lastTotal, total);
                double? percent = DockerProgressBytes.Percent(completed, total);
                int layerCount = latestLayers?.Count ?? 0;
                int layersDone = 0;
                if (latestLayers != null)
                    foreach (var pair in latestLayers)
                        if (InstallProcessRunner.IsLayerComplete(pair.Value ?? "")) layersDone++;
                string transfer = total > 0 && percent is double value
                    ? " Transfer so far: " + completed + "/" + total + " B (" + value.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + "%)."
                    : "";
                string message;
                if (total > 0 && completed >= total && layerCount > 0 && layersDone >= layerCount)
                    message = "All " + layerCount + " layers downloaded (100%). Waiting for Docker to finish extracting and exit (" + (int)elapsed.TotalSeconds + "s elapsed).";
                else
                    message = "Docker " + kind + " still running (" + (int)elapsed.TotalSeconds + "s elapsed, last layer output " + (int)quietFor.TotalSeconds + "s ago)."
                        + (layerCount > 0 ? " Layers: " + layersDone + "/" + layerCount + " done." : "")
                        + transfer;
                progress.Report(new InstallJobProgress(DateTime.UtcNow, commandStage, message,
                    CompletedBytes: total > 0 ? completed : null,
                    TotalBytes: total > 0 ? total : null));
            }
        }, heartbeatCancellation.Token);
        InstallProcessResult result;
        try
        {
            result = await processes.RunAsync(start, kind, context.RawLogPath,
                ReportCommandOutput,
                effectiveVerifiedWork,
                healthCheck: kind is InstallCommandKind.Download or InstallCommandKind.Extraction
                    ? token => CheckDockerHealthAsync(job, token)
                    : null,
                cancellation: cancellation,
                onStarted: identity => context.Update(record => record.OwnedProcess = identity, "process-started", "Started the owned Docker subprocess " + identity.ProcessId + "."),
                onCompleted: identity => context.Update(record =>
                {
                    if (record.OwnedProcess == identity) record.OwnedProcess = null;
                }, "process-completed", "The owned Docker subprocess exited and was reconciled." )).ConfigureAwait(false);
        }
        finally
        {
            heartbeatCancellation.Cancel();
            try { await heartbeat.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        return result;
    }

    private async Task<bool> CheckDockerHealthAsync(InstallJobRecord job, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(dockerExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = job.DestinationPath
        };
        AddDockerTarget(start, job);
        start.ArgumentList.Add("info");
        start.ArgumentList.Add("--format");
        start.ArgumentList.Add("{{.OSType}}");
        InstallProcessResult result = await processes.RunAsync(start, InstallCommandKind.Connection, cancellation: cancellation).ConfigureAwait(false);
        return result.Succeeded;
    }

    private async Task RemoveOwnedContainerAsync(InstallJobContext context, CancellationToken cancellation)
    {
        InstallJobRecord job = context.Snapshot();
        if (string.IsNullOrWhiteSpace(job.OwnedContainerName)) return;
        string sourceId = SourceIdentity(job);
        InstallProcessResult inspect = await RunDockerAsync(context, job, new[] { "container", "inspect", job.OwnedContainerName, "--format", DockerScripts.OwnedContainerInspectFormat }, InstallCommandKind.Metadata, NullProgress.Instance, cancellation).ConfigureAwait(false);
        if (!inspect.Succeeded) return;
        (string containerId, string labels) = ParseContainerInspect(inspect.StandardOutputTail);
        if (string.IsNullOrWhiteSpace(containerId)
            || (!string.IsNullOrWhiteSpace(job.OwnedContainerId) && !string.Equals(containerId, job.OwnedContainerId, StringComparison.OrdinalIgnoreCase))
            || !DockerScripts.OwnershipMatches(DockerScripts.OwnershipFromLabelsJson(labels), sourceId)
            || !DockerScripts.OperationMatches(labels, job.OperationId)) return;
        InstallProcessResult removal = await RunDockerAsync(context, job, new[] { "container", "rm", "--force", job.OwnedContainerName }, InstallCommandKind.Metadata, NullProgress.Instance, cancellation).ConfigureAwait(false);
        if (removal.Succeeded) context.Update(record => { record.OwnedContainerId = ""; record.OwnedContainerName = ""; }, "container-removed", "Removed the extraction container owned by this completed operation.");
    }

    private static void AddDockerTarget(ProcessStartInfo start, InstallJobRecord job)
    {
        if (!string.IsNullOrWhiteSpace(job.DockerHost)) { start.ArgumentList.Add("--host"); start.ArgumentList.Add(job.DockerHost); }
        else if (!string.IsNullOrWhiteSpace(job.DockerContext)) { start.ArgumentList.Add("--context"); start.ArgumentList.Add(job.DockerContext); }
    }

    private static string PinnedImage(InstallJobRecord job)
    {
        if (!Regex.IsMatch(job.PinnedDigest, @"\Asha256:[0-9a-f]{64}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) throw new InvalidDataException("The job has no pinned image digest.");
        return job.ImageRepository + "@" + job.PinnedDigest.ToLowerInvariant();
    }

    private static string SourceIdentity(InstallJobRecord job) => job.SourceGameId;
    private static string FinalInstallPath(InstallJobRecord job) => Path.GetFullPath(job.InstalledPath);
    private static string PendingPath(InstallJobRecord job) => LibraryStore.SafeChild(Path.GetDirectoryName(job.StagingPath)!, Path.GetFileName(job.StagingPath) + "-extract-" + Math.Max(1, job.ExtractionGeneration));

    private static (string ContainerId, string LabelsJson) ParseContainerInspect(string output)
    {
        string line = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? string.Empty;
        int delimiter = line.IndexOf('|');
        if (delimiter <= 0 || delimiter == line.Length - 1) return (string.Empty, string.Empty);
        string id = line[..delimiter].Trim();
        string labels = line[(delimiter + 1)..].Trim();
        if (!Regex.IsMatch(id, @"\A[0-9a-f]{12,64}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return (string.Empty, string.Empty);
        return (id, labels);
    }

    internal static InstallStageOutcome FromProcessFailure(string action, InstallProcessResult result)
    {
        string detail = (result.FailureReason + " " + result.StandardErrorTail + " " + result.StandardOutputTail).Trim();
        if (detail.Length == 0) detail = "exit code " + (result.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown");
        if (ContainsNonRetryableDockerFailure(detail)) return InstallStageOutcome.Failure(action + " failed: " + TrimMessage(detail));
        if (result.Termination is InstallProcessTermination.Inactivity or InstallProcessTermination.TimedOut) return InstallStageOutcome.Retry(action + " stalled (" + TrimMessage(detail) + "); restarting automatically to re-establish the transfer.");
        if (result.Termination == InstallProcessTermination.Cancelled) return InstallStageOutcome.Retry(action + " was cancelled (" + TrimMessage(detail) + "); restarting automatically.");
        if (result.ExitCode == 0 && result.Termination == InstallProcessTermination.Exited) return InstallStageOutcome.Success(action + " completed.");
        return InstallStageOutcome.Retry(action + ": " + TrimMessage(detail) + "; retrying automatically.");
    }

    private static bool ContainsNonRetryableDockerFailure(string text)
    {
        string[] markers = { "unauthorized", "authentication required", "denied: requested access", "no space left on device", "disk full", "permission denied", "operation not permitted", "read-only file system", "readonly file system", "invalid tar", "corrupt", "no space" };
        return markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static string TrimMessage(string text) => text.Length <= 1200 ? text : text[^1200..];

    private static IReadOnlyList<string> EnumerateSafeFiles(string root)
    {
        string fullRoot = Path.GetFullPath(root);
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(fullRoot);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            var info = new DirectoryInfo(current);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Reparse points are not accepted in extracted payload paths.");
            foreach (string child in Directory.EnumerateFileSystemEntries(current))
            {
                FileAttributes attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Reparse points are not accepted in extracted payload paths.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(child);
                else
                {
                    string relative = RelativePayloadPath(fullRoot, child);
                    _ = SafeChild(fullRoot, relative);
                    result.Add(Path.GetFullPath(child));
                }
            }
        }
        return result.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ThenBy(path => path, StringComparer.Ordinal).ToArray();
    }

    private static string RelativePayloadPath(string root, string fullPath)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(fullPath)).Replace('\\', '/');
        if (relative.StartsWith("../", StringComparison.Ordinal) || relative == ".." || Path.IsPathRooted(relative)) throw new InvalidDataException("The archive path escapes the staging root.");
        if (relative.Split('/').Any(part => part is "" or "." or "..")) throw new InvalidDataException("The archive contains an unsafe relative path.");
        return relative;
    }

    private static string SafeChild(string root, string relative)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A payload path escapes its verified root.");
        return full;
    }

    private static async Task<bool> IsCheckpointValidAsync(string path, string relative, long length, string sha256, IReadOnlyCollection<InstallFileCheckpoint> checkpoints, CancellationToken cancellation)
    {
        InstallFileCheckpoint? checkpoint = checkpoints.FirstOrDefault(item => item.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase));
        if (checkpoint == null || checkpoint.Length != length || !checkpoint.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
        if (new FileInfo(path).Length != checkpoint.Length) return false;
        return string.Equals(await HashFileAsync(path, cancellation).ConfigureAwait(false), checkpoint.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task VerifyCheckpointFilesAsync(string root, IEnumerable<InstallFileCheckpoint> checkpoints, InstallJobContext context, CancellationToken cancellation)
    {
        InstallFileCheckpoint[] original = checkpoints.ToArray();
        var valid = new List<InstallFileCheckpoint>();
        foreach (InstallFileCheckpoint checkpoint in original)
        {
            cancellation.ThrowIfCancellationRequested();
            string path;
            try { checkpoint.Validate(); path = SafeChild(root, checkpoint.RelativePath); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException) { continue; }
            if (!File.Exists(path)) continue;
            var info = new FileInfo(path);
            if (info.Length != checkpoint.Length) continue;
            string hash;
            try { hash = await HashFileAsync(path, cancellation).ConfigureAwait(false); }
            catch (IOException) { continue; }
            if (hash.Equals(checkpoint.Sha256, StringComparison.OrdinalIgnoreCase)) valid.Add(checkpoint);
        }
        if (valid.Count != original.Length)
            context.Update(job => job.CompletedFiles = valid, "checkpoint-reconciled", "Invalid or incomplete file checkpoints were removed and will be re-extracted.");
    }

    private static async Task<bool> MatchesCheckpointsAsync(string root, IEnumerable<InstallFileCheckpoint> checkpoints, CancellationToken cancellation)
    {
        InstallFileCheckpoint[] expected = checkpoints.ToArray();
        if (expected.Length == 0 || !Directory.Exists(root)) return false;
        foreach (InstallFileCheckpoint checkpoint in expected)
        {
            cancellation.ThrowIfCancellationRequested();
            string path;
            try { checkpoint.Validate(); path = SafeChild(root, checkpoint.RelativePath); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException) { return false; }
            if (!File.Exists(path)) return false;
            var info = new FileInfo(path);
            if (info.Length != checkpoint.Length) return false;
            try
            {
                if (!string.Equals(await HashFileAsync(path, cancellation).ConfigureAwait(false), checkpoint.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
            }
            catch (IOException) { return false; }
        }
        return true;
    }

    private static async Task CopyVerifiedAsync(string source, string incomplete, string expectedHash, CancellationToken cancellation)
    {
        try
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
            using var output = new FileStream(incomplete, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.WriteThrough | FileOptions.Asynchronous);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[1024 * 1024];
            int count;
            while ((count = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellation).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);
                hash.AppendData(buffer, 0, count);
            }
            await output.FlushAsync(cancellation).ConfigureAwait(false);
            output.Flush(true);
            string actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The staged copy failed its SHA-256 verification.");
        }
        catch
        {
            // The job-owned incomplete suffix is intentionally retained for inspection and never checkpointed.
            throw;
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellation)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellation).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool HasPlayableExecutable(string root)
    {
        if (!Directory.Exists(root)) return false;
        string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string utility = @"(?i)^(unins|uninstall|setup|install|launcher|gamebootstrapper|eaclauncher|redist|crashreport|crashhandler|crashpad|reporter|helper|quicksfv|dxsetup|vcredist|ue4prereq|ueprereq|dotnet|unitycrashhandler|qtwebengineprocess|yuzu|ryujinx|citron|sudachi|eden|yuzucmd|edencli|edenroom|enbhost|skse|squirrel|inklecate|ffmpeg|languageselector|workshop|unrealcefsubprocess|.*editor|.*toolkit|.*packager)$";
        string support = @"(?i)(^|\\)(support|redist|redistributables|redistributable|commonredist|prerequisites|directx|vcredist|dotnet|installers|installer|crashreporter|crashreportclient|trainer|wemod|fling|flingtrainer|thirdpartylibs|imageioffmpeg|emulators|modding)(\\|$)";
        foreach (string file in EnumerateSafeFiles(root).Where(path => Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)))
        {
            string relative = file.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? file[rootFull.Length..] : file;
            if (Regex.IsMatch(relative, support) || Regex.IsMatch(Path.GetFileNameWithoutExtension(file), utility)) continue;
            return true;
        }
        return false;
    }

    private static void Report(IProgress<InstallJobProgress> progress, InstallJobStage stage, string message) => progress.Report(new InstallJobProgress(DateTime.UtcNow, stage, message));

    private sealed class FileSystemActivity : IDisposable
    {
        private readonly FileSystemWatcher watcher;
        private long changes;
        private long consumed;
        internal FileSystemActivity(string path)
        {
            watcher = new FileSystemWatcher(path) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite, InternalBufferSize = 64 * 1024 };
            watcher.Created += Changed; watcher.Changed += Changed; watcher.Renamed += Changed; watcher.Deleted += Changed;
            watcher.EnableRaisingEvents = true;
        }
        internal bool ConsumeActivity()
        {
            long current = Interlocked.Read(ref changes);
            long previous = Interlocked.Exchange(ref consumed, current);
            return current > previous;
        }
        private void Changed(object sender, FileSystemEventArgs e) => Interlocked.Increment(ref changes);
        public void Dispose() => watcher.Dispose();
    }

    private sealed class NullProgress : IProgress<InstallJobProgress>
    {
        internal static readonly NullProgress Instance = new();
        public void Report(InstallJobProgress value) { }
    }
}
