using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace GameLibrary.Native;

/// <summary>Launches and observes the separate console attached to one durable install job.</summary>
internal sealed class InstallJobTerminalSession : IDisposable
{
    private readonly InstallJobStore store;
    private readonly InstallJobController controller;
    private readonly string operationId;
    private readonly string profileRoot;
    private readonly string executablePath;
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer monitorTimer;
    private Process? terminalProcess;
    private string lastStateKey = "";
    private DateTime nextWorkerRecoveryUtc = DateTime.MinValue;
    private bool terminalStatusReported;
    private bool disposed;

    internal event Action<InstallJobRecord>? JobChanged;
    internal event Action? TerminalExited;
    internal event Action<string>? MonitorError;
    internal bool IsJobTerminal => terminalStatusReported;

    internal InstallJobTerminalSession(InstallJobStore store, InstallJobController controller, string operationId)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.operationId = InstallJobStore.ValidateOperationId(operationId);
        profileRoot = Path.GetDirectoryName(store.JobsRoot) ?? throw new InvalidOperationException("Could not resolve the durable install profile root.");
        executablePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Could not resolve the native executable for the install terminal.");
        dispatcher = Dispatcher.CurrentDispatcher;
        monitorTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        monitorTimer.Tick += (_, _) => RefreshFromStore();
    }

    internal ProcessStartInfo BuildStartInfo()
    {
        var start = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory
        };
        start.ArgumentList.Add("--install-terminal");
        start.ArgumentList.Add(operationId);
        start.ArgumentList.Add("--profile-root");
        start.ArgumentList.Add(profileRoot);
        return start;
    }

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (terminalProcess != null) throw new InvalidOperationException("This install terminal session has already started.");
        _ = store.Load(operationId);
        terminalProcess = new Process { StartInfo = BuildStartInfo(), EnableRaisingEvents = true };
        terminalProcess.Exited += (_, _) => RaiseTerminalExited();
        try
        {
            if (!terminalProcess.Start()) throw new InvalidOperationException("The operating system did not start the install terminal.");
        }
        catch
        {
            terminalProcess.Dispose();
            terminalProcess = null;
            throw;
        }
        monitorTimer.Start();
        RefreshFromStore();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        monitorTimer.Stop();
        // The console process and the durable worker own separate lifetimes.
        // Disposing this observer must never stop either process.
        terminalProcess?.Dispose();
        terminalProcess = null;
    }

    private void RefreshFromStore()
    {
        if (disposed) return;
        try
        {
            InstallJobRecord job = store.Load(operationId);
            string stateKey = job.Status + "|" + job.Stage + "|" + job.PinnedDigest + "|" + job.FailureReason;
            if (!string.Equals(stateKey, lastStateKey, StringComparison.Ordinal))
            {
                lastStateKey = stateKey;
                try { JobChanged?.Invoke(job); } catch { }
            }
            bool terminal = IsTerminal(job.Status);
            if (terminal && !terminalStatusReported)
            {
                terminalStatusReported = true;
                monitorTimer.Stop();
            }
            else if (!terminal && DateTime.UtcNow >= nextWorkerRecoveryUtc)
            {
                nextWorkerRecoveryUtc = DateTime.UtcNow.AddSeconds(5);
                try { controller.EnsureWorkerStarted(operationId); }
                catch (Exception ex) { try { MonitorError?.Invoke("Worker recovery failed: " + ex.Message); } catch { } }
            }
        }
        catch (Exception ex)
        {
            try { MonitorError?.Invoke("Durable install state is unavailable: " + ex.Message); } catch { }
        }
    }

    private void RaiseTerminalExited()
    {
        void Raise()
        {
            try { TerminalExited?.Invoke(); } catch { }
        }
        try
        {
            if (dispatcher.CheckAccess()) Raise();
            else dispatcher.BeginInvoke((Action)Raise);
        }
        catch (InvalidOperationException) { }
    }

    internal static bool IsTerminal(InstallJobStatus status) =>
        status is InstallJobStatus.Completed or InstallJobStatus.Failed or InstallJobStatus.Stopped;
}

/// <summary>Interactive console host for a durable install operation.</summary>
internal sealed class InstallJobTerminalHost
{
    private readonly string profileRoot;
    private readonly string operationId;

    internal InstallJobTerminalHost(string profileRoot, string operationId)
    {
        this.profileRoot = Path.GetFullPath(profileRoot ?? throw new ArgumentNullException(nameof(profileRoot)));
        this.operationId = InstallJobStore.ValidateOperationId(operationId);
    }

    internal async Task<int> RunAsync()
    {
        if (!EnsureConsole()) throw new InvalidOperationException("Windows could not create an install terminal window.");
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.Title = "Game Library · Install terminal";

        var store = new InstallJobStore(profileRoot);
        var controller = new InstallJobController(store, profileRoot, Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "");
        InstallJobRecord job = store.Load(operationId);
        int detachRequested = 0;
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Interlocked.Exchange(ref detachRequested, 1);
        };
        Console.CancelKeyPress += cancelHandler;
        var pending = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var seenOrder = new Queue<string>();
        try
        {
            PrintBanner(job);
            controller.EnsureWorkerStarted(operationId);
            foreach (InstallJobEvent item in store.ReadEvents(operationId, 40))
                WriteEvent(item, seen, seenOrder);

            InstallJobStatus? lastStatus = null;
            InstallJobStage? lastStage = null;
            string lastFailure = "";
            bool completionNoticeWritten = false;
            while (Volatile.Read(ref detachRequested) == 0)
            {
                try { job = store.Load(operationId); }
                catch (IOException ex)
                {
                    WriteLine("Waiting for the durable install state to become readable: " + ex.Message, ConsoleColor.DarkYellow);
                    await Task.Delay(200).ConfigureAwait(false);
                    continue;
                }
                if (job.Status != lastStatus || job.Stage != lastStage || !string.Equals(job.FailureReason, lastFailure, StringComparison.Ordinal))
                {
                    lastStatus = job.Status;
                    lastStage = job.Stage;
                    lastFailure = job.FailureReason;
                    PrintJobState(job);
                    if (InstallJobTerminalSession.IsTerminal(job.Status) && !completionNoticeWritten)
                    {
                        completionNoticeWritten = true;
                        WriteLine("Job is complete. Press Q to close this terminal; the durable result remains saved.", ConsoleColor.Green);
                    }
                }

                foreach (InstallJobEvent item in store.ReadEvents(operationId, 250))
                    WriteEvent(item, seen, seenOrder);
                PollControlResponses(store, pending);
                if (Console.KeyAvailable)
                {
                    ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                    if (key.Key is ConsoleKey.Q or ConsoleKey.Escape)
                    {
                        WriteLine("Terminal detached. The install worker continues independently.", ConsoleColor.DarkGray);
                        return 0;
                    }
                    HandleControlKey(key.Key, job, controller, pending);
                }
                await Task.Delay(200).ConfigureAwait(false);
            }
            WriteLine("Terminal detached. The install worker continues independently.", ConsoleColor.DarkGray);
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    internal static InstallJobControlAction? ControlActionFor(ConsoleKey key, InstallJobStatus status)
    {
        if (key == ConsoleKey.P && status == InstallJobStatus.Running) return InstallJobControlAction.Pause;
        if (key == ConsoleKey.R && status == InstallJobStatus.Paused) return InstallJobControlAction.Resume;
        if (key == ConsoleKey.R && status == InstallJobStatus.RetryableFailure) return InstallJobControlAction.Retry;
        if (key == ConsoleKey.S && status is InstallJobStatus.Queued or InstallJobStatus.Running or InstallJobStatus.PauseRequested or InstallJobStatus.Paused or InstallJobStatus.RetryableFailure or InstallJobStatus.ResumeRequested)
            return InstallJobControlAction.Stop;
        return null;
    }

    internal static string FormatEvent(InstallJobEvent item)
    {
        var line = new StringBuilder();
        line.Append(item.AtUtc.ToLocalTime().ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture));
        line.Append("  [").Append(item.Stage).Append('/').Append(item.Kind).Append("] ").Append(item.Message);
        if (item.TotalBytes is > 0)
            line.Append(" | ").Append(FormatBytes(item.CompletedBytes ?? 0)).Append('/').Append(FormatBytes(item.TotalBytes.Value));
        if (item.CompletedFiles.HasValue || item.TotalFiles.HasValue)
        {
            line.Append(" | ").Append(item.CompletedFiles ?? 0).Append('/').Append(item.TotalFiles?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?").Append(" files");
            if (item.TotalsEstimated) line.Append(" estimated");
        }
        if (ProgressPercent(item) is double percent)
        {
            line.Append(" | ").Append(percent.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)).Append('%');
            int filled = (int)System.Math.Round(percent / 100.0 * 24);
            if (filled < 0) filled = 0;
            if (filled > 24) filled = 24;
            line.Append(" [").Append(new string('#', filled)).Append(new string('-', 24 - filled)).Append(']');
        }
        if (item.LayerProgress is { Count: > 0 })
        {
            line.Append(" | layers: ");
            line.Append(string.Join("; ", item.LayerProgress.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => pair.Key + "=" + pair.Value)));
        }
        return line.ToString();
    }

    internal static double? ProgressPercent(InstallJobEvent item)
    {
        if (item.CompletedBytes is long bytes && item.TotalBytes is long total && total > 0) return 100.0 * bytes / total;
        if (item.CompletedFiles is long files && item.TotalFiles is long all && all > 0) return 100.0 * files / all;
        return null;
    }

    private static string FormatBytes(long value)
    {
        if (value < 1024) return value.ToString(System.Globalization.CultureInfo.InvariantCulture) + " B";
        string[] units = { "KB", "MB", "GB", "TB" };
        double scaled = value;
        int unit = -1;
        do { scaled /= 1024; unit++; } while (scaled >= 1024 && unit < units.Length - 1);
        return scaled.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " " + units[unit];
    }

    private static void HandleControlKey(ConsoleKey key, InstallJobRecord job, InstallJobController controller, Dictionary<string, DateTime> pending)
    {
        InstallJobControlAction? action = ControlActionFor(key, job.Status);
        if (action == null)
        {
            string hint = key switch
            {
                ConsoleKey.P => "Pause is available while the job is running.",
                ConsoleKey.R => job.Status == InstallJobStatus.RetryableFailure ? "Resume is not available here; retry will reuse the same pinned job." : "Resume is available when the job is paused.",
                ConsoleKey.S => "Stop is unavailable after the job has finished.",
                _ => "Use P to pause, R to resume or retry, S to stop, or Q to detach."
            };
            WriteLine(hint, ConsoleColor.DarkYellow);
            return;
        }
        if (pending.Count > 0)
        {
            WriteLine("Waiting for the previous control request to be acknowledged.", ConsoleColor.DarkYellow);
            return;
        }
        try
        {
            string requestId = controller.SendControl(job.OperationId, action.Value);
            pending[requestId] = DateTime.UtcNow.AddSeconds(30);
            WriteLine("Requested " + action.Value + "; the durable worker will confirm the result.", ConsoleColor.Cyan);
        }
        catch (Exception ex) { WriteLine("Control request failed: " + ex.Message, ConsoleColor.Red); }
    }

    private void PollControlResponses(InstallJobStore store, Dictionary<string, DateTime> pending)
    {
        foreach (var request in pending.ToArray())
        {
            string responsePath = Path.Combine(store.ControlDirectory(operationId), request.Key + ".response.json");
            if (File.Exists(responsePath))
            {
                try
                {
                    InstallJobControlResponse? response = InstallJobStore.ReadControlResponse(responsePath);
                    if (response != null)
                    {
                        bool exactRequest = string.Equals(response.OperationId, operationId, StringComparison.Ordinal)
                            && string.Equals(response.RequestId, request.Key, StringComparison.Ordinal);
                        WriteLine(!exactRequest ? "Ignored a control response with a mismatched job identity."
                            : response.Accepted ? response.Message : "Control rejected: " + response.Message,
                            !exactRequest ? ConsoleColor.Red : response.Accepted ? ConsoleColor.Green : ConsoleColor.DarkYellow);
                    }
                    pending.Remove(request.Key);
                    continue;
                }
                catch (IOException) { }
                catch (JsonException) { }
            }
            if (DateTime.UtcNow >= request.Value)
            {
                pending.Remove(request.Key);
                WriteLine("No control acknowledgement arrived yet. The worker remains in control of the durable job.", ConsoleColor.DarkYellow);
            }
        }
    }

    private static void WriteEvent(InstallJobEvent item, HashSet<string> seen, Queue<string> seenOrder)
    {
        string key = EventKey(item);
        if (!seen.Add(key)) return;
        seenOrder.Enqueue(key);
        while (seenOrder.Count > 10000) seen.Remove(seenOrder.Dequeue());
        WriteLine(FormatEvent(item), EventColor(item));
    }

    private static string EventKey(InstallJobEvent item) =>
        item.AtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "|" + item.Kind + "|" + item.Stage + "|" + item.Message + "|"
        + item.CompletedBytes + "|" + item.TotalBytes + "|" + item.CompletedFiles + "|" + item.TotalFiles + "|" + item.TotalsEstimated + "|"
        + string.Join(";", (item.LayerProgress ?? new Dictionary<string, string>()).OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Key + "=" + pair.Value));

    private static ConsoleColor EventColor(InstallJobEvent item) => item.Kind switch
    {
        "failed" or "error" => ConsoleColor.Red,
        "completed" or "promotion-verified" or "completion-marker" => ConsoleColor.Green,
        "progress" => ConsoleColor.Gray,
        "checkpoint" => ConsoleColor.DarkCyan,
        _ => ConsoleColor.Cyan
    };

    private static void PrintBanner(InstallJobRecord job)
    {
        Console.Clear();
        WriteLine("GAME LIBRARY · DURABLE INSTALL TERMINAL", ConsoleColor.Cyan);
        WriteLine("Game: " + job.CanonicalGameId, ConsoleColor.White);
        WriteLine("Source: " + job.ImageRepository + ":" + job.ImageTag, ConsoleColor.Gray);
        WriteLine("Destination: " + job.InstalledPath, ConsoleColor.Gray);
        WriteLine("Controls: P pause · R resume/retry · S stop · Q detach without stopping the worker", ConsoleColor.Yellow);
        WriteLine(new string('─', Math.Min(Console.WindowWidth > 0 ? Console.WindowWidth : 100, 120)), ConsoleColor.DarkGray);
    }

    private static void PrintJobState(InstallJobRecord job)
    {
        ConsoleColor color = job.Status switch
        {
            InstallJobStatus.Completed => ConsoleColor.Green,
            InstallJobStatus.Failed or InstallJobStatus.RetryableFailure => ConsoleColor.Red,
            InstallJobStatus.Stopped => ConsoleColor.DarkYellow,
            _ => ConsoleColor.Yellow
        };
        WriteLine("[job] " + job.Stage + " · " + job.Status
            + (string.IsNullOrWhiteSpace(job.PinnedDigest) ? " · digest resolving" : " · " + job.PinnedDigest)
            + (string.IsNullOrWhiteSpace(job.FailureReason) ? "" : " · " + job.FailureReason), color);
    }

    private static void WriteLine(string text, ConsoleColor color)
    {
        try
        {
            ConsoleColor previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ForegroundColor = previous;
        }
        catch (IOException) { }
        catch (InvalidOperationException) { }
    }

    private static bool EnsureConsole()
    {
        if (GetConsoleWindow() != IntPtr.Zero) return true;
        return AllocConsole();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
}
