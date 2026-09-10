using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace GameLibrary.Native;

public sealed class JobWindow : Window
{
    private readonly LibraryStore store;
    private readonly string script;
    private readonly string[] containers;
    private readonly bool openInDefaultTerminal;
    private readonly bool wsl2;
    private readonly string scriptExtension;
    private readonly string? completionDestination;
    private readonly string[] completionGameIds;
    private readonly IReadOnlyDictionary<string, string> containerGameIds;
    private readonly Func<CancellationToken, Task<IDisposable>>? acquireInstallation;
    private readonly HashSet<string> notifiedCompletions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim completionDrainGate = new(1, 1);
    private readonly object stopSync = new();
    private DateTime completionStartedAtUtc;
    private readonly TextBox output = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12 };
    private readonly TextBlock status = new() { Text = "Starting…", Margin = new Thickness(0, 10, 0, 0) };
    private Process? process;
    private CancellationTokenSource? runCancellation;
    private Task? stopCleanup;
    private readonly string log;
    public bool Running { get; private set; }
    internal bool Busy => Running || completionDispatching;
    internal int? LastExitCode { get; private set; }
    internal DateTime? CompletionStartedAtUtc => completionStartedAtUtc == default ? null : completionStartedAtUtc;
    internal string DisplayedOutput => output.Text;
    public event Action<bool>? Completed;
    internal event Func<bool, Task>? CompletedAsync;
    public event Func<string, Task>? GameCompleted;
    private bool cancelled;
    private bool started;
    private bool completionRaised;
    private bool completionDispatching;
    private bool ownsInstallation;
    private bool stopRequested;
    internal string OperationId { get; } = Guid.NewGuid().ToString("N");
    internal static ProcessStartInfo BuildDefaultTerminalStartInfo(string path)
    {
        if (!Path.GetExtension(path).Equals(".bat", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The default Windows terminal launcher requires a BAT script.", nameof(path));
        return new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(path))! };
    }
    internal static string BuildJobLogPath(string root, DateTime timestamp, Guid operationId)
    {
        string jobs = Path.Combine(Path.GetFullPath(root), "jobs");
        Directory.CreateDirectory(jobs);
        return Path.Combine(jobs, timestamp.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture) + "-" + operationId.ToString("N") + ".log");
    }
    internal static string BindJobEnvironment(string script, string extension, string operationId, bool lockHeld)
    {
        if (!Guid.TryParseExact(operationId, "N", out _)) throw new ArgumentException("Invalid install operation identity.", nameof(operationId));
        string held = lockHeld ? "1" : "0";
        return extension switch
        {
            "bat" => "@set \"GLM_INSTALL_OPERATION_ID=" + operationId + "\"\r\n@set \"GLM_NATIVE_INSTALL_LOCK_HELD=" + held + "\"\r\n" + script,
            "sh" => "#!/usr/bin/env bash\nexport GLM_INSTALL_OPERATION_ID='" + operationId + "'\nexport GLM_NATIVE_INSTALL_LOCK_HELD='" + held + "'\n" + script,
            _ => "$env:GLM_INSTALL_OPERATION_ID = '" + operationId + "'\r\n$env:GLM_NATIVE_INSTALL_LOCK_HELD = '" + held + "'\r\n" + script
        };
    }
    public JobWindow(LibraryStore store, string script, string[] containers, bool openInDefaultTerminal = false, string scriptExtension = "ps1", string? completionDestination = null, IEnumerable<string>? completionGameIds = null, Func<CancellationToken, Task<IDisposable>>? acquireInstallation = null)
    {
        this.store = store; this.script = script; this.containers = containers;
        this.openInDefaultTerminal = openInDefaultTerminal;
        this.wsl2 = scriptExtension.TrimStart('.').Equals("sh", StringComparison.OrdinalIgnoreCase);
        this.scriptExtension = scriptExtension.TrimStart('.').ToLowerInvariant() switch { "bat" => "bat", "sh" => "sh", _ => "ps1" };
        this.completionDestination = string.IsNullOrWhiteSpace(completionDestination) ? null : Path.GetFullPath(completionDestination);
        this.completionGameIds = completionGameIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray() ?? Array.Empty<string>();
        this.acquireInstallation = acquireInstallation;
        ownsInstallation = acquireInstallation == null;
        if (acquireInstallation != null && containers.Length != this.completionGameIds.Length)
            throw new ArgumentException("Each install container must have one exact game identity.", nameof(containers));
        var identityMap = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < Math.Min(containers.Length, this.completionGameIds.Length); i++)
        {
            string expectedContainer = completionDestination == null
                ? DockerScripts.ContainerName(this.completionGameIds[i])
                : DockerScripts.ContainerNameForDestination(this.completionGameIds[i], completionDestination);
            if (!string.Equals(containers[i], expectedContainer, StringComparison.Ordinal))
                throw new ArgumentException("An install container did not match its exact game identity.", nameof(containers));
            identityMap[containers[i]] = this.completionGameIds[i];
        }
        this.containerGameIds = identityMap;
        Style = (Style)System.Windows.Application.Current.FindResource(typeof(Window));
        Title = openInDefaultTerminal ? "Game Library · Download terminal" : wsl2 ? "Game Library · WSL2 Ubuntu install" : "Game Library · Download progress";
        Width = 900; Height = 600; MinWidth = 600; MinHeight = 400;
        var grid = new DockPanel { Margin = new Thickness(18) };
        var stop = new Button { Content = "Stop this operation", Margin = new Thickness(0, 0, 0, 12), HorizontalAlignment = HorizontalAlignment.Left };
        stop.Click += async (_, _) =>
        {
            try { await Stop(); }
            catch (Exception ex) { Append("Could not stop the operation: " + ex.Message); }
        }; DockPanel.SetDock(stop, Dock.Top); grid.Children.Add(stop);
        DockPanel.SetDock(status, Dock.Bottom); grid.Children.Add(status); grid.Children.Add(output); Content = grid;
        Directory.CreateDirectory(Path.Combine(store.Root, "jobs"));
        log = BuildJobLogPath(store.Root, DateTime.Now, Guid.NewGuid());
        Loaded += (_, _) => _ = RunObservedAsync();
        Closing += (_, e) => { if (Busy) { e.Cancel = true; status.Text = "Stop the operation before closing. Partial files will be preserved."; } };
    }
    private async Task RunObservedAsync()
    {
        try { await Run(); }
        catch (Exception ex)
        {
            try { store.Log("Download job escaped its error boundary: " + ex); } catch { }
            Append("Could not complete: " + ex.Message);
            Running = false;
        }
        finally
        {
            Running = false;
            ownsInstallation = false;
            try { await RaiseCompletedAsync(); }
            catch (Exception ex) { try { store.Log("Download outer completion boundary failed: " + ex); } catch { } }
        }
    }
    private void Append(string? line)
    {
        if (line == null) return;
        void Write()
        {
            try
            {
                var text = DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine;
                File.AppendAllText(log, text);
                if (output.Text.Length > 500_000) output.Text = output.Text[^250_000..];
                output.AppendText(text); output.ScrollToEnd();
            }
            catch (Exception ex)
            {
                try { store.Log("Job output update failed: " + ex.Message); } catch { }
            }
        }
        try
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            if (Dispatcher.CheckAccess()) Write();
            else _ = Dispatcher.BeginInvoke((Action)Write);
        }
        catch (InvalidOperationException) { }
        catch (Exception ex) { try { store.Log("Job output dispatch failed: " + ex.Message); } catch { } }
    }
    private async Task Run()
    {
        if (started) return;
        started = true;
        Running = true;
        using var completionCancellation = new CancellationTokenSource();
        using var operationCancellation = new CancellationTokenSource();
        runCancellation = operationCancellation;
        Task completionMonitor = Task.CompletedTask;
        IDisposable? installationScope = null;
        try
        {
            if (acquireInstallation != null)
            {
                status.Text = "Waiting for any other install of the same game to finish…";
                Append(status.Text);
                installationScope = await acquireInstallation(operationCancellation.Token);
                operationCancellation.Token.ThrowIfCancellationRequested();
                ownsInstallation = true;
            }
            string path = Path.ChangeExtension(log, "." + scriptExtension);
            string boundScript = BindJobEnvironment(script, scriptExtension, OperationId, acquireInstallation != null && ownsInstallation);
            File.WriteAllText(path, boundScript, new System.Text.UTF8Encoding(scriptExtension == "ps1"));
            ProcessStartInfo start;
            if (openInDefaultTerminal)
            {
                // Launch the script exactly as the website does. UseShellExecute lets
                // Windows hand the .BAT to the user's configured default terminal
                // (Windows Terminal or the legacy console host) instead of hiding a
                // PowerShell child inside the WPF process.
                start = BuildDefaultTerminalStartInfo(path);
            }
            else if (wsl2)
            {
                string wslScript = DockerScripts.ToWslPath(path).Replace("'", "'\\''");
                start = new ProcessStartInfo("wsl.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("--"); start.ArgumentList.Add("bash"); start.ArgumentList.Add("-lc");
                start.ArgumentList.Add("bash '" + wslScript + "'");
            }
            else
            {
                start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", path }) start.ArgumentList.Add(arg);
            }
            process = new Process { StartInfo = start, EnableRaisingEvents = true };
            if (!openInDefaultTerminal)
            {
                process.OutputDataReceived += (_, e) => Append(e.Data); process.ErrorDataReceived += (_, e) => Append(e.Data);
            }
            completionStartedAtUtc = DateTime.UtcNow;
            process.Start();
            if (!openInDefaultTerminal) { process.BeginOutputReadLine(); process.BeginErrorReadLine(); }
            completionMonitor = MonitorCompletionsAsync(completionCancellation.Token);
            status.Text = openInDefaultTerminal ? "Running in your default terminal · " + path : wsl2 ? "Running in WSL2 Ubuntu · " + path : "Running · " + log;
            await process.WaitForExitAsync();
            // A marker can be written immediately before the child exits. Drain
            // once before cancelling the watcher so the final game is reflected
            // without waiting for a second launch or folder scan.
            await DrainCompletionsAsync();
            LastExitCode = process.ExitCode;
            status.Text = cancelled ? "Stopped. Partial files were preserved." : process.ExitCode == 0 ? "Completed successfully · " + log : "Failed · exit code " + process.ExitCode + " · " + log;
            store.Log("Download process exit=" + process.ExitCode + "; cancelled=" + cancelled); Append(status.Text);
        }
        catch (Exception ex) { status.Text = "Could not complete: " + ex.Message; Append(status.Text); }
        finally
        {
            try { completionCancellation.Cancel(); }
            catch (Exception ex) { try { store.Log("Download completion cancellation failed: " + ex.Message); } catch { } }
            try { await completionMonitor; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { try { store.Log("Download completion monitor failed: " + ex); } catch { } }
            // WaitForExitAsync above also waits for redirected output to reach EOF.
            // Consumers can now update installed state without racing a still-running job.
            try { process?.Dispose(); }
            catch (Exception ex) { try { store.Log("Download process cleanup failed: " + ex.Message); } catch { } }
            finally { process = null; runCancellation = null; }
            try { if (stopCleanup != null) await stopCleanup; }
            catch (Exception ex) { try { store.Log("Download stop cleanup failed: " + ex); } catch { } }
            Running = false;
            completionDispatching = true;
            try { await RaiseCompletedAsync(); }
            catch (Exception ex) { try { store.Log("Download completion dispatch failed: " + ex); } catch { } }
            finally { completionDispatching = false; }
            try { installationScope?.Dispose(); }
            catch (Exception ex) { try { store.Log("Download installation lock release failed: " + ex.Message); } catch { } }
            installationScope = null;
            ownsInstallation = false;
        }
    }

    private async Task RaiseCompletedAsync()
    {
        if (completionRaised) return;
        completionRaised = true;
        bool success = LastExitCode == 0 && !cancelled;
        foreach (var handler in Completed?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try { ((Action<bool>)handler)(success); }
            catch (Exception ex) { try { store.Log("Download completion handler: " + ex.Message); } catch { } }
        }
        foreach (var handler in CompletedAsync?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try { await ((Func<bool, Task>)handler)(success); }
            catch (Exception ex) { try { store.Log("Download async completion handler: " + ex); } catch { } }
        }
    }

    private async Task MonitorCompletionsAsync(CancellationToken cancellation)
    {
        if (completionDestination == null || completionGameIds.Length == 0) return;
        while (!cancellation.IsCancellationRequested && notifiedCompletions.Count < completionGameIds.Length)
        {
            await DrainCompletionsAsync();
            if (notifiedCompletions.Count >= completionGameIds.Length) return;
            await Task.Delay(300, cancellation);
        }
    }

    private async Task DrainCompletionsAsync()
    {
        if (completionDestination == null || completionGameIds.Length == 0) return;
        string destination = completionDestination;
        await completionDrainGate.WaitAsync();
        try
        {
            foreach (string id in completionGameIds)
            {
                string expectedOperationId = OperationId;
                if (notifiedCompletions.Contains(id) || !InstalledScanner.HasFreshCompletionMarker(destination, id, completionStartedAtUtc, operationId: expectedOperationId)) continue;
                notifiedCompletions.Add(id);
                foreach (var handler in GameCompleted?.GetInvocationList() ?? Array.Empty<Delegate>())
                {
                    try { await ((Func<string, Task>)handler)(id); }
                    catch (Exception ex)
                    {
                        Append("Immediate installed-state update for " + id + " failed; the final scan will retry: " + ex.Message);
                    }
                }
            }
        }
        finally { completionDrainGate.Release(); }
    }
    private async Task Stop()
    {
        Task cleanup;
        bool firstRequest = false;
        bool shouldCancel = false;
        bool shouldKill = false;
        bool shouldCleanup = false;
        bool wasQueued = false;
        lock (stopSync)
        {
            if (!Running || completionDispatching) return;
            if (stopRequested)
            {
                cleanup = stopCleanup ?? Task.CompletedTask;
            }
            else
            {
                stopRequested = true;
                firstRequest = true;
                bool processRunning = false;
                try { processRunning = process is { HasExited: false }; }
                catch (InvalidOperationException) { }
                wasQueued = acquireInstallation != null && !ownsInstallation;
                shouldCancel = processRunning || wasQueued;
                shouldKill = processRunning;
                shouldCleanup = ownsInstallation && !wasQueued;
                cancelled = shouldCancel;
                stopCleanup = shouldCleanup ? StartStopCleanup() : Task.CompletedTask;
                cleanup = stopCleanup;
            }
        }
        if (!firstRequest) { await cleanup; return; }
        if (shouldCancel)
        {
            try { runCancellation?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        if (shouldKill)
        {
            try { if (process is { HasExited: false }) process.Kill(true); }
            catch (Exception ex) { Append("The download process could not be stopped: " + ex.Message); }
        }
        if (wasQueued) Append("This install was still queued behind another same-game install; the running install was left untouched.");
        await cleanup;
    }
    private Task StartStopCleanup()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = FinishStopCleanupAsync(completion);
        return completion.Task;
    }
    private async Task FinishStopCleanupAsync(TaskCompletionSource<bool> completion)
    {
        try { await StopOwnedContainersAsync(); completion.TrySetResult(true); }
        catch (Exception ex) { completion.TrySetException(ex); }
    }
    private async Task StopOwnedContainersAsync()
    {
        foreach (string container in containers)
        {
            if (containerGameIds.TryGetValue(container, out string? gameId)) await StopOwnedContainerAsync(container, gameId);
            else Append("Refusing to stop " + container + ": no exact game identity was supplied.");
        }
    }
    private async Task StopOwnedContainerAsync(string container, string gameId)
    {
        try
        {
            var inspectStart = new ProcessStartInfo(DockerScripts.Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            inspectStart.ArgumentList.Add("container"); inspectStart.ArgumentList.Add("inspect"); inspectStart.ArgumentList.Add(container); inspectStart.ArgumentList.Add("--format"); inspectStart.ArgumentList.Add(DockerScripts.OwnershipFormat);
            using var inspect = Process.Start(inspectStart)!;
            var inspectOutput = inspect.StandardOutput.ReadToEndAsync(); var inspectError = inspect.StandardError.ReadToEndAsync();
            await inspect.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            if (inspect.ExitCode != 0) { Append(await inspectError); return; } // --rm jobs commonly removed the container already.
            string inspectJson = await inspectOutput;
            if (!DockerScripts.OwnershipMatches(DockerScripts.OwnershipFromLabelsJson(inspectJson), gameId) || !DockerScripts.OperationMatches(inspectJson, OperationId))
            {
                Append("Refusing to stop a container not owned by this install operation: " + container + ".");
                return;
            }
            var stopStart = new ProcessStartInfo(DockerScripts.Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            stopStart.ArgumentList.Add("stop"); stopStart.ArgumentList.Add(container);
            using var stopper = Process.Start(stopStart)!;
            var stdout = stopper.StandardOutput.ReadToEndAsync(); var stderr = stopper.StandardError.ReadToEndAsync();
            await stopper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Append(await stdout); if (stopper.ExitCode != 0) Append(await stderr);
        }
        catch (Exception ex) { Append("Selected container stop: " + ex.Message); }
    }
}
