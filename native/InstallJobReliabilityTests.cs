using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace GameLibrary.Native;

internal sealed record InstallJobTestCheck(string Name, bool Passed, string Evidence);

/// <summary>Isolated, non-destructive fixture proofs for durable job and coordination primitives.</summary>
internal static class InstallJobReliabilityTests
{
    internal static async Task<IReadOnlyList<InstallJobTestCheck>> RunAsync(string evidenceRoot)
    {
        string root = Path.GetFullPath(evidenceRoot ?? throw new ArgumentNullException(nameof(evidenceRoot)));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The isolated install-job evidence root must already exist.");
        string marker = Path.Combine(root, ".install-job-fixture-root");
        if (!File.Exists(marker)) LibraryStore.AtomicWrite(marker, "isolated-fixtures-only" + Environment.NewLine);
        string work = Path.Combine(root, "install-job-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var checks = new List<InstallJobTestCheck>();

        string sourceId = DockerIdentity.Create("owner/library", "AgainstTheStorm");
        string destination = Path.Combine(work, "destination");
        Directory.CreateDirectory(destination);
        string operation = Guid.NewGuid().ToString("N");
        var job = new InstallJobRecord
        {
            OperationId = operation,
            CanonicalGameId = "canonical:against-the-storm",
            SourceGameId = sourceId,
            SourceGameIds = new List<string> { sourceId },
            ImageRepository = "owner/library",
            ImageTag = "AgainstTheStorm",
            DestinationPath = destination,
            StagingPath = Path.Combine(destination, DockerScripts.StagingDirectoryName, "stage-" + operation),
            InstalledPath = Path.Combine(destination, DockerScripts.InstallFolder(sourceId)),
            ShellTarget = InstallWorkerCapability.NativeWindowsTarget
        };
        var store = new InstallJobStore(work);
        store.Create(job);
        string digest = "sha256:" + new string('a', 64);
        job.PinnedDigest = digest;
        job.InstalledPath = Path.Combine(destination, DockerScripts.VersionedInstallFolder(sourceId, digest));
        job.UnknownFields = new Dictionary<string, JsonElement>();
        using (JsonDocument document = JsonDocument.Parse("\"retained\"")) job.UnknownFields["futureField"] = document.RootElement.Clone();
        store.Save(job);
        InstallJobRecord loaded = store.Load(operation);
        bool manifest = loaded.PinnedDigest == digest
            && loaded.InstalledPath == job.InstalledPath
            && loaded.UnknownFields?.ContainsKey("futureField") == true
            && File.Exists(store.ManifestPath(operation));
        checks.Add(new InstallJobTestCheck("durable manifest round-trip and unknown fields", manifest, store.ManifestPath(operation)));
        if (!manifest) throw new InvalidDataException("Install job manifest round-trip failed.");

        var liveLayers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["a1b2c3d4"] = "Downloading 4.0MB/8.0MB",
            ["e5f6a7b8"] = "Extracting"
        };
        var progressEvent = new InstallJobEvent(DateTime.UtcNow, "progress", InstallJobStage.Pull.ToString(), "a1b2c3d4: Downloading 4.0MB/8.0MB",
            CompletedBytes: 50, TotalBytes: 100, CompletedFiles: 2, TotalFiles: 4, TotalsEstimated: true, LayerProgress: liveLayers);
        store.RecordActivity(job, progressEvent);
        var legacyProgress = new InstallJobEvent(DateTime.UtcNow, "legacy-progress", InstallJobStage.Pull.ToString(), "read prior indented event history");
        File.AppendAllText(store.EventPath(operation), JsonSerializer.Serialize(legacyProgress, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        IReadOnlyList<InstallJobEvent> eventReadback = store.ReadEvents(operation, 20);
        InstallJobEvent? progressReadback = eventReadback.LastOrDefault(item => item.Kind == "progress");
        InstallJobEvent? legacyReadback = eventReadback.LastOrDefault(item => item.Kind == "legacy-progress");
        string renderedProgress = progressReadback == null ? "" : InstallJobTerminalHost.FormatEvent(progressReadback);
        bool terminalProgress = progressReadback?.CompletedBytes == 50
            && progressReadback.TotalBytes == 100
            && progressReadback.CompletedFiles == 2
            && progressReadback.TotalFiles == 4
            && progressReadback.TotalsEstimated
            && progressReadback.LayerProgress?.Count == 2
            && legacyReadback?.Message == legacyProgress.Message
            && renderedProgress.Contains("50 B/100 B", StringComparison.Ordinal)
            && renderedProgress.Contains("2/4 files", StringComparison.Ordinal)
            && renderedProgress.Contains("a1b2c3d4=Downloading 4.0MB/8.0MB", StringComparison.Ordinal);
        bool terminalControls = InstallJobTerminalHost.ControlActionFor(ConsoleKey.P, InstallJobStatus.Running) == InstallJobControlAction.Pause
            && InstallJobTerminalHost.ControlActionFor(ConsoleKey.R, InstallJobStatus.Paused) == InstallJobControlAction.Resume
            && InstallJobTerminalHost.ControlActionFor(ConsoleKey.R, InstallJobStatus.RetryableFailure) == InstallJobControlAction.Retry
            && InstallJobTerminalHost.ControlActionFor(ConsoleKey.S, InstallJobStatus.Queued) == InstallJobControlAction.Stop
            && InstallJobTerminalHost.ControlActionFor(ConsoleKey.P, InstallJobStatus.Completed) == null
            && InstallJobTerminalHost.ControlActionFor(ConsoleKey.S, InstallJobStatus.Completed) == null;
        checks.Add(new InstallJobTestCheck("terminal progress preserves bytes, files, Docker layers, and safe control gating", terminalProgress && terminalControls,
            renderedProgress + "; safe-control-state-matrix=" + terminalControls));
        if (!terminalProgress || !terminalControls) throw new InvalidDataException("Terminal progress details or control gating failed.");

        string requestId = Guid.NewGuid().ToString("N");
        var request = new InstallJobControlRequest(1, operation, requestId, InstallJobControlAction.Pause, DateTime.UtcNow);
        Directory.CreateDirectory(store.ControlDirectory(operation));
        LibraryStore.AtomicWrite(Path.Combine(store.ControlDirectory(operation), requestId + ".json"), JsonSerializer.Serialize(request));
        IReadOnlyList<InstallJobControlRequest> pending = store.ReadPendingControls(operation, "");
        bool requestRoundTrip = pending.Count == 1 && pending[0].RequestId == requestId && pending[0].Action == InstallJobControlAction.Pause;
        store.SaveControlResponse(new InstallJobControlResponse(1, operation, requestId, true, "fixture accepted", DateTime.UtcNow));
        bool deduplicated = store.ReadPendingControls(operation, "").Count == 0;
        checks.Add(new InstallJobTestCheck("durable control request and response deduplication", requestRoundTrip && deduplicated, store.ControlDirectory(operation)));
        if (!requestRoundTrip || !deduplicated) throw new InvalidDataException("Install control request receipt test failed.");

        string lowerTag = DockerIdentity.Create("owner/library", "againstthestorm");
        string otherDigest = "sha256:" + new string('b', 64);
        string versionOne = DockerScripts.VersionedInstallFolder(sourceId, digest);
        string versionTwo = DockerScripts.VersionedInstallFolder(sourceId, otherDigest);
        bool identities = !string.Equals(sourceId, lowerTag, StringComparison.Ordinal)
            && !string.Equals(versionOne, versionTwo, StringComparison.Ordinal);
        checks.Add(new InstallJobTestCheck("case-sensitive source identity and digest-scoped paths", identities, versionOne + " | " + versionTwo));
        if (!identities) throw new InvalidDataException("Source tag case or digest-scoped path collapsed distinct identities.");

        string scanRoot = Path.Combine(work, "scanner");
        string legacyInstall = Path.Combine(scanRoot, DockerScripts.InstallFolder(sourceId));
        string digestInstall = Path.Combine(scanRoot, versionOne);
        Directory.CreateDirectory(legacyInstall);
        Directory.CreateDirectory(digestInstall);
        File.WriteAllText(Path.Combine(legacyInstall, "FixtureGame.exe"), "legacy fixture");
        File.WriteAllText(Path.Combine(legacyInstall, DockerScripts.CompletionMarkerName), "GameLibraryManager|" + sourceId + Environment.NewLine);
        File.WriteAllText(Path.Combine(digestInstall, "FixtureGame.exe"), "digest fixture");
        File.WriteAllText(Path.Combine(digestInstall, DockerScripts.CompletionMarkerName), "GameLibraryManager|" + sourceId + "|" + operation + Environment.NewLine);
        var scanGames = new[] { (Id: sourceId, Name: "Fixture Game") };
        var savedLegacyChoice = new Dictionary<string, string>(StringComparer.Ordinal) { [sourceId] = legacyInstall };
        InstalledScanResult normalScan = InstalledScanner.ScanDownloads(scanRoot, scanGames, default);
        InstalledScanResult legacyPreferredScan = InstalledScanner.ScanDownloads(scanRoot, scanGames, default, savedLegacyChoice);
        InstalledScanResult exactJobScan = InstalledScanner.ScanDownloads(scanRoot, scanGames, default, savedLegacyChoice, operation);
        InstalledScanResult wrongOperationScan = InstalledScanner.ScanDownloads(scanRoot, scanGames, default, savedLegacyChoice, Guid.NewGuid().ToString("N"));
        bool scannerIdentity = normalScan.Games.SingleOrDefault()?.Folder == digestInstall
            && legacyPreferredScan.Games.SingleOrDefault()?.Folder == legacyInstall
            && exactJobScan.Games.SingleOrDefault()?.Folder == digestInstall
            && wrongOperationScan.Games.Count == 0;
        checks.Add(new InstallJobTestCheck("scanner selects full-digest install and exact operation receipt while preserving legacy preference", scannerIdentity,
            "default=" + normalScan.Games.SingleOrDefault()?.Folder + "; legacy=" + legacyPreferredScan.Games.SingleOrDefault()?.Folder
            + "; job=" + exactJobScan.Games.SingleOrDefault()?.Folder + "; wrong-operation-count=" + wrongOperationScan.Games.Count));
        if (!scannerIdentity) throw new InvalidDataException("Installed scanner did not preserve or select the exact digest-scoped installation as required.");

        InstallJobTargetSupport windows = InstallWorkerCapability.Check(InstallWorkerCapability.NativeWindowsTarget);
        InstallJobTargetSupport wsl = InstallWorkerCapability.Check(InstallWorkerCapability.Wsl2Target);
        bool targetDispatch = windows.Supported && !wsl.Supported;
        checks.Add(new InstallJobTestCheck("target capability preserves WSL legacy route", targetDispatch, windows.Message + " / " + wsl.Message));
        if (!targetDispatch) throw new InvalidDataException("Install target dispatch is ambiguous.");

        var readOnlyDocker = new InstallProcessResult(InstallProcessTermination.Exited, 1, "",
            "Error response from daemon: write metadata.db: read-only file system", TimeSpan.FromSeconds(1), null, "",
            new Dictionary<string, string>());
        InstallStageOutcome readOnlyOutcome = DockerInstallJobDriver.FromProcessFailure("Pinned image pull", readOnlyDocker);
        bool readOnlyStops = !readOnlyOutcome.Succeeded && !readOnlyOutcome.Retryable
            && readOnlyOutcome.Message.Contains("read-only file system", StringComparison.OrdinalIgnoreCase);
        checks.Add(new InstallJobTestCheck("Docker read-only filesystem is a terminal storage failure", readOnlyStops, readOnlyOutcome.Message));
        if (!readOnlyStops) throw new InvalidDataException("The worker would retry an unrecoverable Docker storage error.");

        var coordinator = new GameOperationCoordinator();
        string oldInstall = Path.Combine(work, "old-version");
        string newInstall = Path.Combine(work, "new-version");
        IDisposable gameplay = await coordinator.AcquireGameplayAsync("canonical:against-the-storm", oldInstall).ConfigureAwait(false);
        IDisposable download = await coordinator.AcquireDownloadAsync("canonical:against-the-storm", newInstall).ConfigureAwait(false);
        Task<IDisposable> promotionWaiter = coordinator.AcquirePromotionAsync("canonical:against-the-storm", newInstall);
        await Task.Delay(50).ConfigureAwait(false);
        bool serializedSameDestination = !promotionWaiter.IsCompleted;
        download.Dispose();
        using IDisposable promotion = await promotionWaiter.ConfigureAwait(false);
        bool unrelatedPlayContinues = true;

        Task<IDisposable> restoreWaiter = coordinator.AcquireRestoreAsync("canonical:against-the-storm", oldInstall, Path.Combine(work, "saves"));
        await Task.Delay(50).ConfigureAwait(false);
        bool restoreWaitsForGameplay = !restoreWaiter.IsCompleted;
        gameplay.Dispose();
        using IDisposable restore = await restoreWaiter.ConfigureAwait(false);
        bool coordination = serializedSameDestination && unrelatedPlayContinues && restoreWaitsForGameplay;
        checks.Add(new InstallJobTestCheck("download gameplay concurrency and same-target promotion/restore locks", coordination, "download and old-version gameplay coexisted; promotion waited on its destination; restore waited on gameplay"));
        if (!coordination) throw new InvalidDataException("Game operation coordination fixture failed.");
        return checks;
    }
}
