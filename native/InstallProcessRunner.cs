using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace GameLibrary.Native;

internal enum InstallProcessTermination { Exited, Cancelled, Inactivity, TimedOut, StartFailed }
internal enum InstallCommandKind { Connection, Metadata, Download, Extraction, Other }

internal sealed record InstallProcessOutput(DateTime AtUtc, string Stream, string Text, bool IsProgress, IReadOnlyDictionary<string, string>? LayerProgress = null, long? CompletedBytes = null, long? TotalBytes = null);

internal sealed record InstallProcessResult(
    InstallProcessTermination Termination,
    int? ExitCode,
    string StandardOutputTail,
    string StandardErrorTail,
    TimeSpan Elapsed,
    InstallOwnedProcessIdentity? OwnedProcess,
    string FailureReason,
    IReadOnlyDictionary<string, string> LayerProgress,
    long CompletedBytes = 0,
    long TotalBytes = 0)
{
    internal bool Succeeded => Termination == InstallProcessTermination.Exited && ExitCode == 0;
    internal bool Retryable => Termination is InstallProcessTermination.Inactivity or InstallProcessTermination.TimedOut;
}

internal sealed class InstallTimeoutPolicy
{
    internal TimeSpan Connection { get; init; } = TimeSpan.FromSeconds(30);
    internal TimeSpan Metadata { get; init; } = TimeSpan.FromSeconds(60);
    internal TimeSpan DownloadInactivity { get; init; } = TimeSpan.FromSeconds(45);
    internal TimeSpan ExtractionInactivity { get; init; } = TimeSpan.FromSeconds(180);
    internal TimeSpan HealthCheck { get; init; } = TimeSpan.FromSeconds(10);
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(200);
    internal int MaximumAutomaticAttemptsPerStage { get; init; } = 8;
    internal IReadOnlyList<TimeSpan> RetryDelays { get; init; } = new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) };

    internal TimeSpan? TotalLimit(InstallCommandKind kind) => kind switch
    {
        InstallCommandKind.Connection => Connection,
        InstallCommandKind.Metadata => Metadata,
        _ => null
    };

    internal TimeSpan? InactivityLimit(InstallCommandKind kind) => kind switch
    {
        InstallCommandKind.Download => DownloadInactivity,
        InstallCommandKind.Extraction => ExtractionInactivity,
        _ => null
    };
}

/// <summary>
/// Starts one owned child process, drains stdout and stderr concurrently, normalizes terminal progress,
/// and cancels only that process tree when an injected deadline or inactivity check fails.
/// </summary>
internal sealed class InstallProcessRunner
{
    private const int TailLimit = 64 * 1024;
    private const long RawLogLimit = 8 * 1024 * 1024;
    private static readonly Regex Ansi = new(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\a]*(?:\a|\x1B\\))", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LayerLine = new(@"\A(?<layer>[0-9a-f]{8,64}):\s*(?<progress>.*)\z", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ByteRatio = new(@"(?<done>[0-9]+(?:\.[0-9]+)?)\s*(?<du>[kKmMgGtT]?i?[bB])\s*/\s*(?<total>[0-9]+(?:\.[0-9]+)?)\s*(?<tu>[kKmMgGtT]?i?[bB])", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Docker reports per-layer "x/y" bytes while downloading and extracting.
    // Tracking the best value per layer lets a live percent survive a completion
    // line that no longer carries a ratio.
    internal static long ScaleBytes(string value, string unit)
    {
        double amount = double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed) ? parsed : 0;
        long factor = unit.ToUpperInvariant().Replace("I", "") switch
        {
            "B" => 1L,
            "KB" => 1024L,
            "MB" => 1024L * 1024,
            "GB" => 1024L * 1024 * 1024,
            "TB" => 1024L * 1024 * 1024 * 1024,
            _ => 1L
        };
        return (long)(amount * factor);
    }

    internal static (long Completed, long Total) AggregateBytes(IReadOnlyDictionary<string, long[]> layerBytes)
    {
        long completed = 0, total = 0;
        foreach (var pair in layerBytes)
        {
            completed += pair.Value[0];
            total += pair.Value[1];
        }
        return (completed, total);
    }

    // Parses a Docker layer "x/y" byte ratio. Returns Done = -1 when the line
    // carries no ratio (for example "Pull complete").
    internal static (long Done, long Total) ParseLayerBytes(string progressText)
    {
        if (ByteRatio.Match(progressText) is { Success: true } ratio)
            return (ScaleBytes(ratio.Groups["done"].Value, ratio.Groups["du"].Value),
                    ScaleBytes(ratio.Groups["total"].Value, ratio.Groups["tu"].Value));
        return (-1, -1);
    }

    internal static bool IsLayerComplete(string progressText) =>
        progressText.Contains("Pull complete", StringComparison.OrdinalIgnoreCase)
        || progressText.Contains("Already exists", StringComparison.OrdinalIgnoreCase)
        || progressText.Contains("Download complete", StringComparison.OrdinalIgnoreCase);
    private readonly TimeProvider clock;
    private readonly InstallTimeoutPolicy timeouts;

    internal InstallProcessRunner(TimeProvider? clock = null, InstallTimeoutPolicy? timeouts = null)
    {
        this.clock = clock ?? TimeProvider.System;
        this.timeouts = timeouts ?? new InstallTimeoutPolicy();
    }

    internal async Task<InstallProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        InstallCommandKind kind,
        string? rawLogPath = null,
        Action<InstallProcessOutput>? onOutput = null,
        Func<CancellationToken, Task<bool>>? verifiedWorkActivity = null,
        Func<CancellationToken, Task<bool>>? healthCheck = null,
        CancellationToken cancellation = default,
        Action<InstallOwnedProcessIdentity>? onStarted = null,
        Action<InstallOwnedProcessIdentity>? onCompleted = null)
    {
        if (startInfo == null) throw new ArgumentNullException(nameof(startInfo));
        if (startInfo.UseShellExecute) throw new ArgumentException("Install workers require an owned, non-shell child process.", nameof(startInfo));
        if (!startInfo.RedirectStandardOutput || !startInfo.RedirectStandardError)
            throw new ArgumentException("Install workers must redirect both output streams so neither pipe can block.", nameof(startInfo));

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stdout = new BoundedText(TailLimit);
        var stderr = new BoundedText(TailLimit);
        var layers = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var layerBytes = new ConcurrentDictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
        var outputQueue = Channel.CreateUnbounded<InstallProcessOutput>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var startedAt = clock.GetTimestamp();
        var activity = new ActivityState(startedAt, clock);
        string? logFullPath = rawLogPath == null ? null : Path.GetFullPath(rawLogPath);

        try
        {
            if (!process.Start()) throw new InvalidOperationException("The install worker process did not start.");
        }
        catch (Exception ex)
        {
            process.Dispose();
            return new InstallProcessResult(InstallProcessTermination.StartFailed, null, "", "", clock.GetElapsedTime(startedAt), null, ex.Message, new Dictionary<string, string>());
        }

        InstallOwnedProcessIdentity identity = CaptureIdentity(process, startInfo.FileName);
        try { onStarted?.Invoke(identity); } catch { }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task logTask = WriteLogAsync(outputQueue.Reader, logFullPath);
        Task stdoutTask = ReadOutputAsync(process.StandardOutput, "stdout", stdout, outputQueue.Writer, onOutput, layers, layerBytes, activity, stop.Token);
        Task stderrTask = ReadOutputAsync(process.StandardError, "stderr", stderr, outputQueue.Writer, onOutput, layers, layerBytes, activity, stop.Token);
        Task<ProcessWaitResult> monitorTask = MonitorAsync(process, kind, activity, verifiedWorkActivity, healthCheck, cancellation, stop.Token);
        ProcessWaitResult monitor;
        try { monitor = await monitorTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { monitor = new ProcessWaitResult(InstallProcessTermination.Cancelled, "Install operation was cancelled."); }
        catch (Exception ex) { monitor = new ProcessWaitResult(InstallProcessTermination.StartFailed, ex.Message); }

        if (monitor.Termination != InstallProcessTermination.Exited)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }
        try { await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); } catch { }
        stop.Cancel();
        outputQueue.Writer.TryComplete();
        try { await logTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); } catch { }
        int? exitCode = null;
        try { if (process.HasExited) exitCode = process.ExitCode; } catch { }
        process.Dispose();
        try { onCompleted?.Invoke(identity); } catch { }
        var (completedBytes, totalBytes) = AggregateBytes(layerBytes);
        return new InstallProcessResult(monitor.Termination, exitCode, stdout.Text, stderr.Text, clock.GetElapsedTime(startedAt), identity, monitor.FailureReason, new Dictionary<string, string>(layers, StringComparer.OrdinalIgnoreCase), completedBytes, totalBytes);
    }

    private async Task<ProcessWaitResult> MonitorAsync(
        Process process,
        InstallCommandKind kind,
        ActivityState activity,
        Func<CancellationToken, Task<bool>>? verifiedWorkActivity,
        Func<CancellationToken, Task<bool>>? healthCheck,
        CancellationToken cancellation,
        CancellationToken internalCancellation)
    {
        long localActivity = activity.Value;
        var totalLimit = timeouts.TotalLimit(kind);
        var inactivityLimit = timeouts.InactivityLimit(kind);
        long started = clock.GetTimestamp();
        while (!process.HasExited)
        {
            cancellation.ThrowIfCancellationRequested();
            internalCancellation.ThrowIfCancellationRequested();
            await Task.Delay(timeouts.PollInterval, clock, internalCancellation).ConfigureAwait(false);
            if (process.HasExited) return new ProcessWaitResult(InstallProcessTermination.Exited, "");
            long observed = activity.Value;
            if (observed > localActivity) localActivity = observed;
            if (totalLimit.HasValue && clock.GetElapsedTime(started) >= totalLimit.Value)
                return new ProcessWaitResult(InstallProcessTermination.TimedOut, "The command exceeded its bounded health/metadata time limit.");
            if (!inactivityLimit.HasValue || clock.GetElapsedTime(localActivity) < inactivityLimit.Value) continue;

            bool workAdvanced = false;
            if (verifiedWorkActivity != null)
            {
                using var check = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                check.CancelAfter(timeouts.HealthCheck);
                try { workAdvanced = await verifiedWorkActivity(check.Token).WaitAsync(timeouts.HealthCheck, cancellation).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (TimeoutException) { }
            }
            if (workAdvanced)
            {
                localActivity = clock.GetTimestamp();
                activity.Set(localActivity);
                continue;
            }

            bool dependencyHealthy = false;
            if (healthCheck != null)
            {
                using var check = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                check.CancelAfter(timeouts.HealthCheck);
                try { dependencyHealthy = await healthCheck(check.Token).WaitAsync(timeouts.HealthCheck, cancellation).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (TimeoutException) { }
            }
            if (dependencyHealthy)
            {
                // A piped docker pull emits no output while a large layer
                // downloads, so "no output" is NOT a stall when the daemon is
                // alive and responding. Treat the healthy dependency as activity
                // and keep waiting; only an unresponsive daemon ends the command.
                localActivity = clock.GetTimestamp();
                activity.Set(localActivity);
                continue;
            }
            return new ProcessWaitResult(InstallProcessTermination.Inactivity,
                "The owned command had no output or verified file activity, and its bounded health check did not confirm the dependency.");
        }
        return new ProcessWaitResult(InstallProcessTermination.Exited, "");
    }

    private static async Task ReadOutputAsync(
        StreamReader reader,
        string streamName,
        BoundedText tail,
        ChannelWriter<InstallProcessOutput> output,
        Action<InstallProcessOutput>? callback,
        ConcurrentDictionary<string, string> layers,
        ConcurrentDictionary<string, long[]> layerBytes,
        ActivityState activity,
        CancellationToken cancellation)
    {
        char[] buffer = new char[4096];
        var line = new StringBuilder();
        string lastLine = "";
        bool afterCarriageReturn = false;
        while (true)
        {
            int read;
            try { read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellation).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            if (read == 0) break;
            for (int i = 0; i < read; i++)
            {
                char current = buffer[i];
                if (current == '\r' || current == '\n')
                {
                    if (current == '\n' && afterCarriageReturn) { afterCarriageReturn = false; continue; }
                    afterCarriageReturn = current == '\r';
                    await PublishLineAsync(line, current == '\r', streamName, tail, output, callback, layers, layerBytes, activity, value => lastLine = value, lastLine).ConfigureAwait(false);
                    continue;
                }
                afterCarriageReturn = false;
                if (line.Length < 32768) line.Append(current);
            }
        }
        await PublishLineAsync(line, false, streamName, tail, output, callback, layers, layerBytes, activity, value => lastLine = value, lastLine).ConfigureAwait(false);
    }

    private static Task PublishLineAsync(
        StringBuilder line,
        bool progress,
        string streamName,
        BoundedText tail,
        ChannelWriter<InstallProcessOutput> output,
        Action<InstallProcessOutput>? callback,
        ConcurrentDictionary<string, string> layers,
        ConcurrentDictionary<string, long[]> layerBytes,
        ActivityState activity,
        Action<string> setLastLine,
        string previousLine)
    {
        if (line.Length == 0) return Task.CompletedTask;
        string text = Ansi.Replace(line.ToString(), "").TrimEnd();
        line.Clear();
        if (text.Length == 0) return Task.CompletedTask;
        if (LayerLine.Match(text) is { Success: true } match)
        {
            string layer = match.Groups["layer"].Value;
            string progressText = match.Groups["progress"].Value;
            layers[layer] = progressText;
            var parsed = ParseLayerBytes(progressText);
            if (parsed.Done >= 0)
            {
                layerBytes.AddOrUpdate(layer, new[] { parsed.Done, parsed.Total }, (_, current) => new[] { Math.Max(current[0], parsed.Done), Math.Max(current[1], parsed.Total) });
            }
            else if (IsLayerComplete(progressText))
            {
                layerBytes.AddOrUpdate(layer, _ => new long[] { 0, 0 }, (_, current) => current[1] > 0 ? new[] { current[1], current[1] } : current);
            }
        }
        tail.Append(text + Environment.NewLine);
        IReadOnlyDictionary<string, string>? layerProgress = layers.IsEmpty ? null : new Dictionary<string, string>(layers, StringComparer.OrdinalIgnoreCase);
        var (completedBytes, totalBytes) = AggregateBytes(layerBytes);
        // Report bytes only once a layer has published a real total, so the
        // terminal never shows a meaningless "0 B/0 B" on unrelated lines.
        long? reportedCompleted = totalBytes > 0 ? completedBytes : null;
        long? reportedTotal = totalBytes > 0 ? totalBytes : null;
        var item = new InstallProcessOutput(DateTime.UtcNow, streamName, text, progress, layerProgress, reportedCompleted, reportedTotal);
        output.TryWrite(item);
        try { callback?.Invoke(item); } catch { }
        if (!string.Equals(text, previousLine, StringComparison.Ordinal)) activity.Set(activity.Clock.GetTimestamp());
        setLastLine(text);
        return Task.CompletedTask;
    }

    private static async Task WriteLogAsync(ChannelReader<InstallProcessOutput> items, string? path)
    {
        if (path == null)
        {
            await foreach (var _ in items.ReadAllAsync().ConfigureAwait(false)) { }
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await foreach (InstallProcessOutput item in items.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length >= RawLogLimit)
                {
                    string previous = path + ".1";
                    if (File.Exists(previous)) File.Delete(previous);
                    File.Move(path, previous);
                }
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.WriteThrough);
                string line = item.AtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + " [" + item.Stream + (item.IsProgress ? ", progress" : "") + "] " + item.Text + Environment.NewLine;
                byte[] bytes = Encoding.UTF8.GetBytes(line);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static InstallOwnedProcessIdentity CaptureIdentity(Process process, string executable)
    {
        long creation = 0;
        string full = executable;
        try { creation = process.StartTime.ToUniversalTime().ToFileTimeUtc(); } catch { }
        try { if (!string.IsNullOrWhiteSpace(process.MainModule?.FileName)) full = Path.GetFullPath(process.MainModule!.FileName!); }
        catch { try { full = Path.GetFullPath(executable); } catch { } }
        return new InstallOwnedProcessIdentity(process.Id, creation, full, "local-process-tree");
    }

    private sealed class BoundedText
    {
        private readonly int limit;
        private readonly StringBuilder value = new();
        private string lastMeaningful = "";
        internal BoundedText(int limit) => this.limit = limit;
        internal string LastMeaningful => lastMeaningful;
        internal string Text => value.ToString();
        internal void Append(string text)
        {
            lock (value)
            {
                lastMeaningful = text.Trim();
                value.Append(text);
                if (value.Length > limit) value.Remove(0, value.Length - limit);
            }
        }
    }

    private sealed class ActivityState
    {
        private long timestamp;
        internal TimeProvider Clock { get; }
        internal ActivityState(long initial, TimeProvider? clock = null) { timestamp = initial; Clock = clock ?? TimeProvider.System; }
        internal long Value => Interlocked.Read(ref timestamp);
        internal void Set(long value) => Interlocked.Exchange(ref timestamp, value);
    }

    private readonly record struct ProcessWaitResult(InstallProcessTermination Termination, string FailureReason);
}
