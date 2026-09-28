using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

internal sealed record InstallJobProgress(
    DateTime AtUtc,
    InstallJobStage Stage,
    string Message,
    long? CompletedBytes = null,
    long? TotalBytes = null,
    long? CompletedFiles = null,
    long? TotalFiles = null,
    bool TotalsEstimated = false,
    IReadOnlyDictionary<string, string>? LayerProgress = null);

internal sealed record InstallStageOutcome(bool Succeeded, bool Retryable, string Message, string Verification = "")
{
    internal static InstallStageOutcome Success(string message = "Stage completed.", string verification = "") => new(true, false, message, verification);
    internal static InstallStageOutcome Retry(string message) => new(false, true, message);
    internal static InstallStageOutcome Failure(string message) => new(false, false, message);
}

internal interface IInstallJobDriver
{
    Task<InstallStageOutcome> ExecuteStageAsync(InstallJobContext context, InstallJobStage stage, IProgress<InstallJobProgress> progress, CancellationToken cancellation);
}

/// <summary>Serializes job mutations so control requests cannot snapshot half-updated checkpoints.</summary>
internal sealed class InstallJobContext
{
    private readonly object sync;
    private readonly InstallJobStore store;
    private readonly Func<InstallJobRecord> getJob;
    private readonly Action<Action<InstallJobRecord>, InstallJobEvent?> updateJob;

    internal InstallJobContext(object sync, InstallJobStore store, Func<InstallJobRecord> getJob, Action<Action<InstallJobRecord>, InstallJobEvent?> updateJob)
    {
        this.sync = sync;
        this.store = store;
        this.getJob = getJob;
        this.updateJob = updateJob;
    }

    internal InstallJobRecord Snapshot()
    {
        lock (sync) return Clone(getJob());
    }

    internal string JobDirectory => store.JobDirectory(Snapshot().OperationId);
    internal string ManifestPath => store.ManifestPath(Snapshot().OperationId);
    internal string RawLogPath => Path.Combine(JobDirectory, "worker.log");

    internal void Update(Action<InstallJobRecord> mutation, string? eventKind = null, string? message = null)
    {
        if (mutation == null) throw new ArgumentNullException(nameof(mutation));
        InstallJobRecord next = Snapshot();
        mutation(next);
        InstallJobEvent? item = eventKind == null && message == null ? null : new InstallJobEvent(DateTime.UtcNow, eventKind ?? "state", next.Stage.ToString(), message ?? "");
        updateJob(current => CopyInto(next, current), item);
    }

    internal void RecordCheckpoint(InstallFileCheckpoint checkpoint)
    {
        checkpoint.Validate();
        Update(job =>
        {
            int existing = job.CompletedFiles.FindIndex(file => file.RelativePath.Equals(checkpoint.RelativePath, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0) job.CompletedFiles[existing] = checkpoint;
            else job.CompletedFiles.Add(checkpoint);
        }, "checkpoint", "Verified extracted file: " + checkpoint.RelativePath);
    }

    internal void RecordActivity(InstallJobProgress progress)
    {
        if (progress == null) return;
        updateJob(job =>
        {
            job.LastMeaningfulActivityUtc = progress.AtUtc == default ? DateTime.UtcNow : progress.AtUtc;
            job.Stage = progress.Stage;
        }, new InstallJobEvent(
            progress.AtUtc,
            "progress",
            progress.Stage.ToString(),
            progress.Message,
            progress.CompletedBytes,
            progress.TotalBytes,
            progress.CompletedFiles,
            progress.TotalFiles,
            progress.TotalsEstimated,
            progress.LayerProgress));
    }

    internal static InstallJobRecord Clone(InstallJobRecord job)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(job);
        return JsonSerializer.Deserialize<InstallJobRecord>(json) ?? throw new InvalidDataException("Could not clone the install job state.");
    }

    private static void CopyInto(InstallJobRecord source, InstallJobRecord target)
    {
        target.SchemaVersion = source.SchemaVersion;
        target.OperationId = source.OperationId;
        target.CanonicalGameId = source.CanonicalGameId;
        target.SourceGameId = source.SourceGameId;
        target.SourceGameIds = source.SourceGameIds;
        target.ImageRepository = source.ImageRepository;
        target.ImageTag = source.ImageTag;
        target.PinnedDigest = source.PinnedDigest;
        target.DestinationPath = source.DestinationPath;
        target.StagingPath = source.StagingPath;
        target.InstalledPath = source.InstalledPath;
        target.DockerContext = source.DockerContext;
        target.DockerHost = source.DockerHost;
        target.ShellTarget = source.ShellTarget;
        target.Stage = source.Stage;
        target.Status = source.Status;
        target.RequestedAction = source.RequestedAction;
        target.AttemptCount = source.AttemptCount;
        target.CreatedUtc = source.CreatedUtc;
        target.UpdatedUtc = source.UpdatedUtc;
        target.LastMeaningfulActivityUtc = source.LastMeaningfulActivityUtc;
        target.OwnedProcess = source.OwnedProcess;
        target.WorkerProcess = source.WorkerProcess;
        target.LastControlRequestId = source.LastControlRequestId;
        target.OwnedContainerId = source.OwnedContainerId;
        target.OwnedContainerName = source.OwnedContainerName;
        target.CompletedFiles = source.CompletedFiles;
        target.FailureReason = source.FailureReason;
        target.CompletionVerification = source.CompletionVerification;
        target.RollbackPath = source.RollbackPath;
        target.ExtractionGeneration = source.ExtractionGeneration;
        target.ImageTotalBytes = source.ImageTotalBytes;
        target.ImageLayerBytes = source.ImageLayerBytes;
        target.UnknownFields = source.UnknownFields;
    }
}

/// <summary>
/// Durable control loop for a single installation. Stage drivers own the concrete Docker or filesystem work;
/// this runner owns retries, safe control-state transitions, and manifest persistence.
/// </summary>
internal sealed class InstallJobRunner
{
    private readonly object sync = new();
    private readonly InstallJobStore store;
    private readonly IInstallJobDriver driver;
    private readonly InstallTimeoutPolicy timeouts;
    private readonly TimeProvider clock;
    private readonly InstallJobContext context;
    private readonly InstallJobRecord job;
    private CancellationTokenSource? stageCancellation;
    private CancellationTokenSource? stopCancellation;
    private Task? runTask;
    private DateTime lastProgressPublishedUtc;

    internal event Action<InstallJobRecord>? JobChanged;
    internal event Action<InstallJobProgress>? ProgressChanged;
    internal InstallJobRecord Snapshot => context.Snapshot();
    internal bool IsRunning { get { lock (sync) return runTask is { IsCompleted: false }; } }

    internal InstallJobRunner(InstallJobStore store, InstallJobRecord job, IInstallJobDriver driver, InstallTimeoutPolicy? timeouts = null, TimeProvider? clock = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.job = job ?? throw new ArgumentNullException(nameof(job));
        this.driver = driver ?? throw new ArgumentNullException(nameof(driver));
        this.timeouts = timeouts ?? new InstallTimeoutPolicy();
        this.clock = clock ?? TimeProvider.System;
        this.job.Validate();
        context = new InstallJobContext(sync, store, () => this.job, UpdateCore);
    }

    internal Task StartAsync(CancellationToken cancellation = default)
    {
        lock (sync)
        {
            if (runTask is { IsCompleted: false }) return runTask;
            InstallJobStatus status = job.Status;
            if (status is InstallJobStatus.Completed or InstallJobStatus.Failed or InstallJobStatus.Stopped)
                throw new InvalidOperationException("This install job is terminal and cannot be started again.");
            if (status == InstallJobStatus.Paused) throw new InvalidOperationException("Resume a paused job instead of starting it again.");
            stopCancellation?.Dispose();
            stopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            job.Status = InstallJobStatus.Running;
            job.RequestedAction = InstallJobControlAction.None;
            job.FailureReason = "";
            PersistCore(new InstallJobEvent(DateTime.UtcNow, "started", job.Stage.ToString(), "Installation worker started."));
            runTask = RunLoopAsync(stopCancellation.Token);
            return runTask;
        }
    }

    internal Task PauseAsync()
    {
        CancellationTokenSource? cancel;
        lock (sync)
        {
            if (job.Status == InstallJobStatus.Paused) return Task.CompletedTask;
            if (job.Status != InstallJobStatus.Running) throw new InvalidOperationException("Only a running install can be paused.");
            job.RequestedAction = InstallJobControlAction.Pause;
            job.Status = InstallJobStatus.PauseRequested;
            PersistCore(new InstallJobEvent(DateTime.UtcNow, "pause-requested", job.Stage.ToString(), "Pause requested; waiting for the selected stage to stop safely."));
            cancel = stageCancellation;
        }
        cancel?.Cancel();
        lock (sync) return runTask ?? Task.CompletedTask;
    }

    internal async Task ResumeAsync(CancellationToken cancellation = default)
    {
        lock (sync)
        {
            if (job.Status == InstallJobStatus.Running && runTask is { IsCompleted: false }) return;
            if (job.Status is not (InstallJobStatus.Paused or InstallJobStatus.RetryableFailure))
                throw new InvalidOperationException("Resume is available only for a paused or explicitly retryable install job.");
            job.RequestedAction = InstallJobControlAction.Resume;
            job.Status = InstallJobStatus.ResumeRequested;
            job.FailureReason = "";
            PersistCore(new InstallJobEvent(DateTime.UtcNow, "resume-requested", job.Stage.ToString(), "The same pinned install job is resuming."));
        }
        await StartAsync(cancellation).ConfigureAwait(false);
    }

    internal async Task StopAsync()
    {
        Task? active;
        CancellationTokenSource? cancelStage;
        CancellationTokenSource? cancelRun;
        lock (sync)
        {
            if (job.Status is InstallJobStatus.Completed or InstallJobStatus.Failed or InstallJobStatus.Stopped) return;
            if (job.Status == InstallJobStatus.Paused)
            {
                job.Status = InstallJobStatus.Stopped;
                job.RequestedAction = InstallJobControlAction.Stop;
                PersistCore(new InstallJobEvent(DateTime.UtcNow, "stopped", job.Stage.ToString(), "Paused install stopped; recovery files were retained."));
                return;
            }
            job.Status = InstallJobStatus.StopRequested;
            job.RequestedAction = InstallJobControlAction.Stop;
            PersistCore(new InstallJobEvent(DateTime.UtcNow, "stop-requested", job.Stage.ToString(), "Stop requested; waiting for the worker to reconcile its current checkpoint."));
            cancelRun = stopCancellation;
            cancelStage = stageCancellation;
            active = runTask;
        }
        cancelRun?.Cancel();
        cancelStage?.Cancel();
        if (active != null) await active.ConfigureAwait(false);
        lock (sync)
        {
            if (job.Status != InstallJobStatus.Completed)
            {
                job.Status = InstallJobStatus.Stopped;
                PersistCore(new InstallJobEvent(DateTime.UtcNow, "stopped", job.Stage.ToString(), "Install stopped; recovery files were retained."));
            }
        }
    }

    internal async Task RetryAsync(CancellationToken cancellation = default)
    {
        lock (sync)
        {
            if (job.Status != InstallJobStatus.RetryableFailure) throw new InvalidOperationException("Only an explicitly retryable install failure can be retried.");
            job.AttemptCount = 0;
            job.FailureReason = "";
            job.RequestedAction = InstallJobControlAction.Retry;
            job.Status = InstallJobStatus.Queued;
            PersistCore(new InstallJobEvent(DateTime.UtcNow, "retry-requested", job.Stage.ToString(), "The failed stage will be retried with the same pinned digest."));
        }
        await StartAsync(cancellation).ConfigureAwait(false);
    }

    private async Task RunLoopAsync(CancellationToken stopToken)
    {
        try
        {
            while (true)
            {
                InstallJobStage stage;
                CancellationToken token;
                lock (sync)
                {
                    if (job.RequestedAction == InstallJobControlAction.Stop || stopToken.IsCancellationRequested)
                    {
                        job.Status = InstallJobStatus.Stopped;
                        PersistCore(new InstallJobEvent(DateTime.UtcNow, "stopped", job.Stage.ToString(), "Install stopped at a safe stage boundary."));
                        return;
                    }
                    if (job.RequestedAction == InstallJobControlAction.Pause)
                    {
                        job.Status = InstallJobStatus.Paused;
                        job.RequestedAction = InstallJobControlAction.None;
                        PersistCore(new InstallJobEvent(DateTime.UtcNow, "paused", job.Stage.ToString(), "Install paused at a safe stage boundary."));
                        return;
                    }
                    if (job.Stage == InstallJobStage.Completed)
                    {
                        job.Status = InstallJobStatus.Completed;
                        job.RequestedAction = InstallJobControlAction.None;
                        PersistCore(new InstallJobEvent(DateTime.UtcNow, "completed", job.Stage.ToString(), "Install completed and was verified."));
                        return;
                    }
                    stage = job.Stage;
                    job.Status = InstallJobStatus.Running;
                    job.RequestedAction = InstallJobControlAction.None;
                    stageCancellation?.Dispose();
                    stageCancellation = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
                    token = stageCancellation.Token;
                    PersistCore(new InstallJobEvent(DateTime.UtcNow, "stage-started", stage.ToString(), "Stage started."));
                }

                InstallStageOutcome outcome;
                try
                {
                    var progress = new ThrottledProgress(this, stage);
                    outcome = await driver.ExecuteStageAsync(context, stage, progress, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    outcome = InstallStageOutcome.Retry("The stage ended after a control request.");
                }
                catch (Exception ex)
                {
                    outcome = IsNonRetryable(ex) ? InstallStageOutcome.Failure(ex.Message) : InstallStageOutcome.Retry(ex.GetType().Name + ": " + ex.Message);
                }
                finally
                {
                    lock (sync) { stageCancellation?.Dispose(); stageCancellation = null; }
                }

                lock (sync)
                {
                    if (job.RequestedAction == InstallJobControlAction.Stop || stopToken.IsCancellationRequested)
                    {
                        job.Status = InstallJobStatus.Stopped;
                        job.FailureReason = "";
                        job.RequestedAction = InstallJobControlAction.Stop;
                        PersistCore(new InstallJobEvent(DateTime.UtcNow, "stopped", stage.ToString(), "Install stopped after the selected stage reconciled."));
                        return;
                    }
                    if (job.RequestedAction == InstallJobControlAction.Pause)
                    {
                        job.Status = InstallJobStatus.Paused;
                        job.FailureReason = "";
                        job.RequestedAction = InstallJobControlAction.None;
                        PersistCore(new InstallJobEvent(DateTime.UtcNow, "paused", stage.ToString(), "Install paused after the selected stage reconciled."));
                        return;
                    }
                    if (outcome.Succeeded)
                    {
                        job.AttemptCount = 0;
                        job.FailureReason = "";
                        if (!string.IsNullOrWhiteSpace(outcome.Verification)) job.CompletionVerification = outcome.Verification;
                        job.Stage = NextStage(stage);
                        job.Status = InstallJobStatus.Running;
                        PersistCore(new InstallJobEvent(DateTime.UtcNow, "stage-completed", stage.ToString(), outcome.Message));
                        continue;
                    }

                    job.FailureReason = outcome.Message;
                    job.AttemptCount++;
                    int maxAttempts = Math.Max(1, timeouts.MaximumAutomaticAttemptsPerStage);
                    if (outcome.Retryable && job.AttemptCount < maxAttempts)
                    {
                        job.Status = InstallJobStatus.Running;
                        TimeSpan delay = timeouts.RetryDelays.Count == 0 ? TimeSpan.FromSeconds(2) : timeouts.RetryDelays[Math.Min(job.AttemptCount - 1, timeouts.RetryDelays.Count - 1)];
                        PersistCore(new InstallJobEvent(DateTime.UtcNow, "stage-retry", stage.ToString(), outcome.Message + " Restarting automatically (attempt " + job.AttemptCount + " of " + maxAttempts + ") in " + delay.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " seconds."));
                        // Delay outside the lock so controls remain responsive.
                    }
                    else
                    {
                        job.Status = outcome.Retryable ? InstallJobStatus.RetryableFailure : InstallJobStatus.Failed;
                        PersistCore(new InstallJobEvent(DateTime.UtcNow, outcome.Retryable ? "retryable-failure" : "failed", stage.ToString(), outcome.Message));
                        return;
                    }
                }

                    if (job.Status == InstallJobStatus.Running)
                {
                    TimeSpan delay;
                    lock (sync) delay = GetLatestRetryDelayCore();
                    if (delay > TimeSpan.Zero) await DelayRetryUntilControlAsync(delay, stopToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            lock (sync)
            {
                job.Status = InstallJobStatus.Stopped;
                job.RequestedAction = InstallJobControlAction.Stop;
                PersistCore(new InstallJobEvent(DateTime.UtcNow, "stopped", job.Stage.ToString(), "Install stopped; recovery files were retained."));
            }
        }
        finally
        {
            lock (sync)
            {
                stageCancellation?.Dispose();
                stageCancellation = null;
                stopCancellation?.Dispose();
                stopCancellation = null;
            }
        }
    }

    private async Task DelayRetryUntilControlAsync(TimeSpan delay, CancellationToken cancellation)
    {
        long started = clock.GetTimestamp();
        while (clock.GetElapsedTime(started) < delay)
        {
            cancellation.ThrowIfCancellationRequested();
            lock (sync) if (job.RequestedAction is InstallJobControlAction.Pause or InstallJobControlAction.Stop) return;
            TimeSpan remaining = delay - clock.GetElapsedTime(started);
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(200) ? remaining : TimeSpan.FromMilliseconds(200), clock, cancellation).ConfigureAwait(false);
        }
    }

    private TimeSpan GetLatestRetryDelayCore()
    {
        int index = Math.Max(0, job.AttemptCount - 1);
        return timeouts.RetryDelays.Count == 0 ? TimeSpan.FromSeconds(2) : timeouts.RetryDelays[Math.Min(index, timeouts.RetryDelays.Count - 1)];
    }

    private static InstallJobStage NextStage(InstallJobStage current) => current switch
    {
        InstallJobStage.Preflight => InstallJobStage.ResolveDigest,
        InstallJobStage.ResolveDigest => InstallJobStage.Pull,
        InstallJobStage.Pull => InstallJobStage.CreateContainer,
        InstallJobStage.CreateContainer => InstallJobStage.Extract,
        InstallJobStage.Extract => InstallJobStage.VerifyPayload,
        InstallJobStage.VerifyPayload => InstallJobStage.WaitForGame,
        InstallJobStage.WaitForGame => InstallJobStage.Promote,
        InstallJobStage.Promote => InstallJobStage.VerifyPromoted,
        InstallJobStage.VerifyPromoted => InstallJobStage.PublishCompletionMarker,
        InstallJobStage.PublishCompletionMarker => InstallJobStage.RefreshLibrary,
        InstallJobStage.RefreshLibrary => InstallJobStage.Completed,
        InstallJobStage.Completed => InstallJobStage.Completed,
        _ => throw new ArgumentOutOfRangeException(nameof(current))
    };

    private static bool IsNonRetryable(Exception exception) => exception is ArgumentException or InvalidDataException or UnauthorizedAccessException or DockerAuthenticationException or DriveNotFoundException or PathTooLongException;

    private void PublishProgress(InstallJobProgress progress)
    {
        DateTime now = clock.GetUtcNow().UtcDateTime;
        lock (sync)
        {
            if (now - lastProgressPublishedUtc < TimeSpan.FromMilliseconds(250)) return;
            lastProgressPublishedUtc = now;
        }
        var updated = progress with { AtUtc = now };
        context.RecordActivity(updated);
        foreach (Delegate handler in ProgressChanged?.GetInvocationList() ?? Array.Empty<Delegate>())
            try { ((Action<InstallJobProgress>)handler)(updated); } catch { }
        NotifyJobChanged();
    }

    private void UpdateCore(Action<InstallJobRecord> mutation, InstallJobEvent? item)
    {
        lock (sync)
        {
            mutation(job);
            job.UpdatedUtc = clock.GetUtcNow().UtcDateTime;
            job.Validate();
            store.Save(job);
            if (item != null) store.RecordActivity(job, item);
        }
        NotifyJobChanged();
    }

    private void PersistCore(InstallJobEvent? item = null)
    {
        job.UpdatedUtc = clock.GetUtcNow().UtcDateTime;
        job.Validate();
        store.Save(job);
        if (item != null) store.RecordActivity(job, item);
        NotifyJobChanged();
    }

    private void NotifyJobChanged()
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            InstallJobRecord snapshot;
            lock (sync) snapshot = InstallJobContext.Clone(job);
            foreach (Delegate handler in JobChanged?.GetInvocationList() ?? Array.Empty<Delegate>())
                try { ((Action<InstallJobRecord>)handler)(snapshot); } catch { }
        });
    }

    private sealed class ThrottledProgress(InstallJobRunner owner, InstallJobStage stage) : IProgress<InstallJobProgress>
    {
        public void Report(InstallJobProgress value)
        {
            InstallJobProgress next = value.Stage == stage ? value : value with { Stage = stage };
            owner.PublishProgress(next);
        }
    }
}
