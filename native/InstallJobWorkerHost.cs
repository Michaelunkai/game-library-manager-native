using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

/// <summary>Single-owner headless worker host. It is entered before WPF startup by Program.</summary>
internal sealed class InstallJobWorkerHost
{
    private readonly string profileRoot;
    private readonly string operationId;
    private readonly GameOperationCoordinator operations;

    internal InstallJobWorkerHost(string profileRoot, string operationId, GameOperationCoordinator? operations = null)
    {
        this.profileRoot = Path.GetFullPath(profileRoot ?? throw new ArgumentNullException(nameof(profileRoot)));
        this.operationId = InstallJobStore.ValidateOperationId(operationId);
        this.operations = operations ?? new GameOperationCoordinator();
    }

    internal async Task<int> RunAsync(CancellationToken cancellation = default)
    {
        var store = new InstallJobStore(profileRoot);
        FileStream lease;
        try { lease = new FileStream(store.WorkerLockPath(operationId), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough); }
        catch (IOException) { return 10; } // An existing worker owns this operation.

        using (lease)
        {
            InstallJobRecord job = store.Load(operationId);
            if (!ReconcileInterruptedWorker(store, job)) return 2;
            using Process process = Process.GetCurrentProcess();
            string executable = Environment.ProcessPath ?? process.MainModule?.FileName ?? "";
            var identity = new InstallOwnedProcessIdentity(process.Id, process.StartTime.ToUniversalTime().ToFileTimeUtc(), Path.GetFullPath(executable), "install-worker");
            job.WorkerProcess = identity;
            store.Save(job);
            FileStream? installationLease = null;
            bool waitingNoticeWritten = false;
            while (installationLease == null)
            {
                cancellation.ThrowIfCancellationRequested();
                job = store.Load(operationId);
                if (job.Status is InstallJobStatus.Completed or InstallJobStatus.Failed or InstallJobStatus.Stopped)
                {
                    job.WorkerProcess = null; store.Save(job);
                    return job.Status == InstallJobStatus.Completed || job.Status == InstallJobStatus.Stopped ? 0 : 2;
                }
                try { installationLease = TryAcquireInstallationLease(job); }
                catch (IOException) { }
                if (installationLease != null) break;
                if (!waitingNoticeWritten)
                {
                    store.RecordActivity(job, new InstallJobEvent(DateTime.UtcNow, "waiting-install-lock", job.Stage.ToString(), "Waiting for the exact installation target to become free."));
                    waitingNoticeWritten = true;
                }
                foreach (InstallJobControlRequest request in store.ReadPendingControls(operationId, job.LastControlRequestId))
                {
                    job = store.Load(operationId);
                    if (File.Exists(Path.Combine(store.ControlDirectory(operationId), request.RequestId + ".response.json"))) continue;
                    job.LastControlRequestId = request.RequestId;
                    bool accepted = false;
                    string message;
                    switch (request.Action)
                    {
                        case InstallJobControlAction.Pause when job.Status is InstallJobStatus.Queued or InstallJobStatus.Paused:
                            job.Status = InstallJobStatus.Paused; accepted = true; message = "The queued job is paused before its worker stage starts."; break;
                        case InstallJobControlAction.Resume when job.Status == InstallJobStatus.Paused:
                            job.Status = InstallJobStatus.Queued; accepted = true; message = "The queued job resumed and will start when its installation lock is available."; break;
                        case InstallJobControlAction.Retry when job.Status == InstallJobStatus.RetryableFailure:
                            job.Status = InstallJobStatus.Queued; job.AttemptCount = 0; job.FailureReason = ""; accepted = true; message = "The retryable job is queued with its existing digest and checkpoints."; break;
                        case InstallJobControlAction.Stop when job.Status is not (InstallJobStatus.Completed or InstallJobStatus.Failed or InstallJobStatus.Stopped):
                            job.Status = InstallJobStatus.Stopped; job.RequestedAction = InstallJobControlAction.Stop; accepted = true; message = "The queued job stopped without changing installed files."; break;
                        default:
                            message = "The requested control is not valid while the job waits for its installation lock."; break;
                    }
                    store.Save(job);
                    store.SaveControlResponse(new InstallJobControlResponse(1, operationId, request.RequestId, accepted, message, DateTime.UtcNow));
                    if (job.Status == InstallJobStatus.Stopped)
                    {
                        job.WorkerProcess = null; store.Save(job); return 0;
                    }
                }
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellation).ConfigureAwait(false);
            }

            using (installationLease)
            {
                job = store.Load(operationId);
                var runner = new InstallJobRunner(store, job, new DockerInstallJobDriver(operations));
            Task? active = null;
            if (job.Status == InstallJobStatus.Running)
            {
                // A previous worker released its lease mid-stage (host teardown,
                // removal, restart). The reconciled child is gone and every stage
                // is idempotent, so continue the same pinned job automatically
                // instead of surfacing a failure the user must retry.
                store.RecordActivity(job, new InstallJobEvent(DateTime.UtcNow, "recovering", job.Stage.ToString(), "Resuming the interrupted stage automatically; verified checkpoints are reused."));
            }
            if (job.Status is InstallJobStatus.Queued or InstallJobStatus.Running)
                active = runner.StartAsync(cancellation);

            while (!cancellation.IsCancellationRequested)
            {
                if (active is { IsCompleted: true })
                {
                    try { await active.ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        job = store.Load(operationId);
                        job.Status = InstallJobStatus.RetryableFailure;
                        job.FailureReason = "Worker loop failed: " + ex.GetType().Name + ": " + ex.Message;
                        store.Save(job);
                    }
                    active = null;
                }

                job = store.Load(operationId);
                if (job.Status is InstallJobStatus.Completed or InstallJobStatus.Failed or InstallJobStatus.Stopped) break;
                IReadOnlyList<InstallJobControlRequest> requests = store.ReadPendingControls(operationId, job.LastControlRequestId);
                foreach (InstallJobControlRequest request in requests)
                {
                    cancellation.ThrowIfCancellationRequested();
                    bool accepted = false;
                    string message;
                    try
                    {
                        job.LastControlRequestId = request.RequestId;
                        store.Save(job);
                        switch (request.Action)
                        {
                            case InstallJobControlAction.Pause:
                                if (runner.IsRunning) await runner.PauseAsync().ConfigureAwait(false);
                                else if (runner.Snapshot.Status != InstallJobStatus.Paused) throw new InvalidOperationException("The job is not running and cannot be paused.");
                                accepted = true;
                                message = "The job reached a safe pause checkpoint.";
                                break;
                            case InstallJobControlAction.Resume:
                                if (active is { IsCompleted: false }) throw new InvalidOperationException("The job is already running.");
                                active = runner.ResumeAsync(cancellation);
                                accepted = true;
                                message = "The same pinned job resumed.";
                                break;
                            case InstallJobControlAction.Retry:
                                if (active is { IsCompleted: false }) throw new InvalidOperationException("The job is already running.");
                                active = runner.RetryAsync(cancellation);
                                accepted = true;
                                message = "The retryable stage was restarted with its existing digest and checkpoints.";
                                break;
                            case InstallJobControlAction.Stop:
                                await runner.StopAsync().ConfigureAwait(false);
                                active = null;
                                accepted = true;
                                message = "The owned operation stopped with recovery data retained.";
                                break;
                            default:
                                throw new InvalidDataException("Unknown install control operation.");
                        }
                    }
                    catch (Exception ex)
                    {
                        message = ex.Message;
                    }
                    store.SaveControlResponse(new InstallJobControlResponse(1, operationId, request.RequestId, accepted, message, DateTime.UtcNow));
                    job = store.Load(operationId);
                }

                if (active == null && job.Status is InstallJobStatus.Queued or InstallJobStatus.Running)
                {
                    // A lost in-memory runner is recoverable only after its recorded child has been reconciled.
                    job.Status = InstallJobStatus.RetryableFailure;
                    job.FailureReason = "Worker loop stopped before the durable job reached a terminal state.";
                    store.Save(job);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellation).ConfigureAwait(false);
            }

            job = store.Load(operationId);
            job.WorkerProcess = null;
            store.Save(job);
            return job.Status == InstallJobStatus.Completed ? 0 : job.Status == InstallJobStatus.Stopped ? 0 : job.Status == InstallJobStatus.Failed ? 2 : 1;
            }
        }
    }

    private static FileStream TryAcquireInstallationLease(InstallJobRecord job)
    {
        string path = DockerScripts.InstallLockPath(job.DestinationPath, job.SourceGameId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
    }

    private static bool ReconcileInterruptedWorker(InstallJobStore store, InstallJobRecord job)
    {
        if (job.OwnedProcess != null)
        {
            InstallOwnedProcessIdentity owned = job.OwnedProcess;
            try
            {
                using Process process = Process.GetProcessById(owned.ProcessId);
                long creation = process.StartTime.ToUniversalTime().ToFileTimeUtc();
                if (creation == owned.CreationFileTimeUtc)
                {
                    string? actualExecutable = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(actualExecutable)
                        || !string.Equals(GameOperationCoordinator.CanonicalPath(actualExecutable), GameOperationCoordinator.CanonicalPath(owned.ExecutablePath), StringComparison.OrdinalIgnoreCase))
                    {
                        job.Status = InstallJobStatus.Failed;
                        job.FailureReason = "The recorded install subprocess still exists, but its executable identity cannot be verified. No process was stopped and no retry was started.";
                        store.Save(job);
                        return false;
                    }
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(10_000);
                    if (!process.HasExited)
                    {
                        job.Status = InstallJobStatus.Failed;
                        job.FailureReason = "The owned install subprocess did not exit after cancellation. No retry was started.";
                        store.Save(job);
                        return false;
                    }
                }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception ex)
            {
                job.Status = InstallJobStatus.Failed;
                job.FailureReason = "Could not verify or stop the exact recorded install subprocess: " + ex.Message;
                store.Save(job);
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                job.Status = InstallJobStatus.Failed;
                job.FailureReason = "Could not access the exact recorded install subprocess: " + ex.Message;
                store.Save(job);
                return false;
            }
        }

        if (job.Status is InstallJobStatus.PauseRequested or InstallJobStatus.ResumeRequested or InstallJobStatus.StopRequested)
        {
            // A held user control request outlives the worker that was acting on
            // it, so surface an explicit retryable state instead of guessing.
            job.Status = InstallJobStatus.RetryableFailure;
            job.RequestedAction = InstallJobControlAction.None;
            job.FailureReason = "A prior worker did not finish this stage. Its exact recorded subprocess was reconciled; inspect diagnostics before retrying.";
            job.OwnedProcess = null;
            store.Save(job);
        }
        else if (job.Status == InstallJobStatus.Running)
        {
            // Leave the job Running: the caller resumes the same pinned stage
            // automatically once the reconciled child is confirmed gone.
            job.OwnedProcess = null;
            job.RequestedAction = InstallJobControlAction.None;
            store.Save(job);
        }
        return true;
    }
}

/// <summary>UI-side process launcher and durable control-file client for one operation.</summary>
internal sealed class InstallJobController
{
    private readonly InstallJobStore store;
    private readonly string profileRoot;
    private readonly string executablePath;

    internal InstallJobController(InstallJobStore store, string profileRoot, string executablePath)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.profileRoot = Path.GetFullPath(profileRoot ?? throw new ArgumentNullException(nameof(profileRoot)));
        this.executablePath = Path.GetFullPath(executablePath ?? throw new ArgumentNullException(nameof(executablePath)));
    }

    internal int? EnsureWorkerStarted(string operationId)
    {
        InstallJobRecord job = store.Load(operationId);
        if (IsSameLiveProcess(job.WorkerProcess)) return null;
        if (job.Status is InstallJobStatus.Completed or InstallJobStatus.Failed or InstallJobStatus.Stopped) return null;
        // The worker holds an exclusive lease for its whole lifetime. If that
        // lease is still held, a worker already owns this operation even before
        // it records itself in the manifest; launching a second one only races
        // the lease and can surface a spurious recovery state in the terminal.
        if (WorkerLeaseHeld(operationId)) return null;
        var start = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory
        };
        start.ArgumentList.Add("--install-worker");
        start.ArgumentList.Add(operationId);
        start.ArgumentList.Add("--profile-root");
        start.ArgumentList.Add(profileRoot);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the install worker.");
        return process.Id;
    }

    internal string SendControl(string operationId, InstallJobControlAction action)
    {
        if (action is not (InstallJobControlAction.Pause or InstallJobControlAction.Resume or InstallJobControlAction.Stop or InstallJobControlAction.Retry))
            throw new ArgumentOutOfRangeException(nameof(action));
        string id = Guid.NewGuid().ToString("N");
        var request = new InstallJobControlRequest(1, InstallJobStore.ValidateOperationId(operationId), id, action, DateTime.UtcNow);
        string directory = store.ControlDirectory(operationId);
        Directory.CreateDirectory(directory);
        LibraryStore.AtomicWrite(Path.Combine(directory, id + ".json"), System.Text.Json.JsonSerializer.Serialize(request));
        return id;
    }

    internal async Task<InstallJobControlResponse?> WaitForResponseAsync(string operationId, string requestId, TimeSpan timeout, CancellationToken cancellation = default)
    {
        string path = Path.Combine(store.ControlDirectory(operationId), requestId + ".response.json");
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellation.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                try { return InstallJobStore.ReadControlResponse(path); }
                catch (System.Text.Json.JsonException) { }
                catch (IOException) { }
            }
            await Task.Delay(200, cancellation).ConfigureAwait(false);
        }
        return null;
    }

    private bool WorkerLeaseHeld(string operationId)
    {
        try
        {
            using var probe = new FileStream(store.WorkerLockPath(operationId), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static bool IsSameLiveProcess(InstallOwnedProcessIdentity? identity)
    {
        if (identity == null || identity.ProcessId <= 0) return false;
        try
        {
            using Process process = Process.GetProcessById(identity.ProcessId);
            return process.StartTime.ToUniversalTime().ToFileTimeUtc() == identity.CreationFileTimeUtc
                && string.Equals(Path.GetFullPath(process.MainModule?.FileName ?? ""), Path.GetFullPath(identity.ExecutablePath), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
