using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace GameLibrary.Native;

public static class Program
{
    public static string? TestReport;
    public static bool PauseProof;
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--install-worker" && args[2] == "--profile-root")
        {
            try { return new InstallJobWorkerHost(args[3], args[1]).RunAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { try { Console.Error.WriteLine("Install worker failed: " + ex.Message); } catch { } return 1; }
        }
        if (args.Length == 4 && args[0] == "--install-terminal" && args[2] == "--profile-root")
        {
            try { return new InstallJobTerminalHost(args[3], args[1]).RunAsync().GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                try
                {
                    Console.Error.WriteLine("Install terminal failed: " + ex.Message);
                    Console.Error.WriteLine("The durable install job was left intact. Press any key to close this terminal.");
                    Console.ReadKey(intercept: true);
                }
                catch { }
                return 1;
            }
        }
        if (args.Length == 2 && args[0] == "--ahk-pipe-proof") return RunDiagnostic(args[1], () =>
        {
            DateTime started = DateTime.UtcNow;
            AhkGameControlResult result = AhkGameControl.ProbeAsync().GetAwaiter().GetResult();
            bool passed = result.State == AhkGameState.Failed
                && result.Detail.Contains("process, creation stamp, window, or session", StringComparison.OrdinalIgnoreCase);
            if (!passed) throw new InvalidOperationException("AHK did not authenticate this client and reject the invalid target.");
            string executable = Environment.ProcessPath ?? "";
            string executableHash = File.Exists(executable)
                ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(executable))) : "";
            LibraryStore.AtomicWrite(Path.GetFullPath(args[1]), DataJson.Write(new
            {
                schemaVersion = 1,
                runId = Guid.NewGuid().ToString("N"),
                testName = "ahk-pipe-authenticated-invalid-target-rejection",
                startedUtc = started,
                finishedUtc = DateTime.UtcNow,
                executablePath = executable,
                executableSha256 = executableHash,
                profilePath = "",
                passed,
                checks = new[] { new { name = "authenticated rejection of invalid process/window identity", passed, state = result.State.ToString(), detail = result.Detail, requestId = result.RequestId } },
                failures = Array.Empty<string>(),
                externalDependencies = new[] { "the current-user AHK game-control pipe and its running controller" },
                artifacts = new[] { Path.GetFullPath(args[1]) }
            }));
            return 0;
        });
        if (args.Length == 3 && args[0] == "--install-job-proof") return RunDiagnostic(args[2], () =>
        {
            string evidenceRoot = Path.GetFullPath(args[1]);
            string? repoRoot = FindRepositoryRoot(AppContext.BaseDirectory);
            string evidenceBase = repoRoot == null ? "" : Path.GetFullPath(Path.Combine(repoRoot, "evidence")) + Path.DirectorySeparatorChar;
            string profileRoot = repoRoot == null ? "" : Path.GetFullPath(Path.Combine(repoRoot, "data")) + Path.DirectorySeparatorChar;
            if (evidenceBase.Length == 0 || !evidenceRoot.StartsWith(evidenceBase, StringComparison.OrdinalIgnoreCase)
                || profileRoot.Length > 0 && (evidenceRoot.StartsWith(profileRoot, StringComparison.OrdinalIgnoreCase) || evidenceRoot.Equals(profileRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Install-job proof accepts only an isolated directory under this repository's evidence folder.");
            System.Collections.Generic.IReadOnlyList<InstallJobTestCheck> checks = InstallJobReliabilityTests.RunAsync(evidenceRoot).GetAwaiter().GetResult();
            bool passed = checks.Count == 8 && checks.All(check => check.Passed);
            LibraryStore.AtomicWrite(Path.GetFullPath(args[2]), DataJson.Write(new { schemaVersion = 1, runId = Guid.NewGuid().ToString("N"), testName = "install-job-reliability", startedUtc = DateTime.UtcNow, finishedUtc = DateTime.UtcNow, executablePath = Environment.ProcessPath, executableSha256 = File.Exists(Environment.ProcessPath) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Environment.ProcessPath))) : "", profilePath = evidenceRoot, passed, checks, failures = checks.Where(check => !check.Passed).ToArray(), externalDependencies = Array.Empty<string>(), artifacts = new[] { evidenceRoot } }));
            return passed ? 0 : 1;
        });
        if (args.Length > 0 && args[0] == "--pause-guardian") return GamePause.RunGuardian(args);
        if (args.Length == 4 && args[0] == "--namespace-read-proof" && args[2] == "--data-dir") return RunDiagnostic(args[1], () =>
        {
            var proofStore = new LibraryStore(args[3]);
            if (File.Exists(proofStore.StatePath)) throw new InvalidOperationException("Use an isolated proof directory without a user profile.");
            using var http = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
            var client = new DockerNamespaceClient(http, DockerHubAccess.GetToken, Path.Combine(proofStore.Cache, "namespaces"));
            var snapshot = client.ReadAsync("michadockermisha", "backup", CancellationToken.None).GetAwaiter().GetResult();
            proofStore.CacheData("namespace-result.json", snapshot.ToJsonString());
            var games = new System.Collections.Generic.List<Game>();
            bool passed = false; string? error = null;
            try { LibraryStore.MergeNamespace(games, snapshot); passed = true; } catch (Exception ex) { error = ex.Message; }
            LibraryStore.AtomicWrite(Path.GetFullPath(args[1]), DataJson.Write(new { passed, error, at = DateTime.UtcNow,
                repositories = snapshot["repositoryCount"], games = games.Count, errors = snapshot["errors"], remoteWrites = false }));
            return passed ? 0 : 1;
        });
        if (args.Length == 2 && args[0] == "--storage-sync-proof") return RunDiagnostic(args[1], () =>
        {
            string proofRoot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, "storage-sync-proof-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(proofRoot);
            int storage = InstalledStorageTests.Run(proofRoot);
            int backoff = SyncBackoffTests.Run(proofRoot);
            int resilience = SyncResilienceTests.Run(proofRoot);
            int fallback = SharedFallbackTests.Run(proofRoot);
            bool passed = storage == 22 && backoff == 23 && resilience == 34 && fallback == 12;
            LibraryStore.AtomicWrite(Path.GetFullPath(args[1]), DataJson.Write(new { passed, storage, backoff, resilience, fallback, at = DateTime.UtcNow }));
            return passed ? 0 : 1;
        });
        if (args.Length == 2 && args[0] == "--native-pause-proof") return RunDiagnostic(args[1], () =>
        {
            GamePauseProof.Run(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
            LibraryStore.AtomicWrite(Path.GetFullPath(args[1]), DataJson.Write(new { passed = true, at = DateTime.UtcNow,
                checks = new[] { "real parent and child suspension", "explicit resume", "identity rejection", "disconnect recovery", "abrupt helper death recovery" } }));
            return 0;
        });
        if (args.Length == 4 && args[0] == "--catalog-read-proof" && args[2] == "--data-dir") return RunDiagnostic(args[1], () =>
        {
            // Dedicated disposable profile and a new empty outbox: this proof
            // performs GETs only and cannot publish the user's queued edits.
            var proofStore = new LibraryStore(args[3]);
            proofStore.EnsureAssets();
            var proofState = new UserState();
            using var client = new SyncClient(proofStore);
            client.Refresh(proofState, true, null).GetAwaiter().GetResult();
            string cache = Path.Combine(proofStore.Cache, "docker-tags.json");
            var tags = File.Exists(cache) ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(cache)) : null;
            bool passed = tags != null && SyncClient.CompleteTagSnapshot(tags, "michadockermisha/backup");
            LibraryStore.AtomicWrite(Path.GetFullPath(args[1]), DataJson.Write(new { passed, at = DateTime.UtcNow,
                shared = client.SharedStatus, catalog = client.CatalogStatus, docker = client.DockerStatus,
                count = tags?["count"], repository = tags?["repository"], sharedOnline = client.Online, remoteWrites = false }));
            return passed ? 0 : 1;
        });
        if (args.Length >= 2 && args[0] == "--self-test") return RunDiagnostic(args[1], () => SelfTests.Run(args[1]));
        if (args.Length >= 4 && args[0] == "--speed-proof")
        {
            // Drives the real production speed path - the same GameSpeedCommand the
            // F1/F2/F3 handler uses - against a live process, so the hotkeys can be
            // proven to actually reach a game rather than only updating a label.
            return RunDiagnostic(args[1], () =>
            {
                int pid = int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
                double factor = double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
                string? dll = GameSpeedNative.ResolveDllPath(GameSpeedNative.DetectArchitecture(pid));
                string injectionFault = "";
                try { GameSpeedNative.InjectAsync(pid, dll ?? string.Empty, CancellationToken.None).GetAwaiter().GetResult(); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                { injectionFault = ex.Message; }
                var applied = GameSpeedCommand.ApplyAsync(pid, factor, NativeSpeedRuntime.Instance, CancellationToken.None)
                    .GetAwaiter().GetResult();
                var status = GameSpeedNative.GetStatusAsync(pid, CancellationToken.None).GetAwaiter().GetResult();
                double readBack = GameSpeedNative.GetSpeedAsync(pid, CancellationToken.None).GetAwaiter().GetResult();
                // The factor being adopted and read back is what "it works" means.
                // hookCount is reported for diagnosis but is not a pass gate: the
                // in-process hook count is republished only at install time, so a
                // manager that attaches afterwards can read a stale zero while the
                // hooks are in fact installed and serving.
                bool passed = applied.Succeeded
                    && Math.Abs(applied.Factor - factor) < 1e-6
                    && Math.Abs(readBack - factor) < 1e-6
                    && !status.Faulted;
                var payload = new System.Text.Json.Nodes.JsonObject
                {
                    ["passed"] = passed,
                    ["processId"] = pid,
                    ["architecture"] = GameSpeedNative.DetectArchitecture(pid).ToString(),
                    ["dll"] = dll ?? "",
                    ["requested"] = factor,
                    ["reported"] = applied.Factor,
                    ["readBack"] = readBack,
                    ["outcome"] = applied.Outcome.ToString(),
                    ["message"] = applied.Message,
                    ["injected"] = status.Injected,
                    ["hooksInstalled"] = status.HooksInstalled,
                    ["hookCount"] = status.HookCount,
                    ["faulted"] = status.Faulted,
                    ["appliedFactor"] = status.AppliedFactor,
                    ["injectionFault"] = injectionFault
                };
                Console.WriteLine(payload.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return passed ? 0 : 1;
            });
        }
        if (args.Length >= 2 && args[0] == "--wand-audit")
        {
            return RunDiagnostic(args[1], () =>
            {
                string? auditData = null;
                for (var i = 2; i < args.Length; i++) if (args[i] == "--data-dir" && i + 1 < args.Length) auditData = Path.GetFullPath(args[++i]);
                return WandAudit.Run(args[1], auditData);
            });
        }
        if (args.Length >= 2 && args[0] == "--live-sync-proof")
        {
            if (Array.Exists(args, a => a == "--offline"))
            {
                LibraryStore.AtomicWrite(Path.GetFullPath(args[1]), "{\"passed\":false,\"networkAttempted\":false,\"error\":\"Live synchronization proof is disabled in offline mode.\"}");
                return 1;
            }
            return LiveSyncProof.Run(args[1]).GetAwaiter().GetResult();
        }
        string? data = null;
        bool offline = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--data-dir" && i + 1 < args.Length) data = Path.GetFullPath(args[++i]);
            else if (args[i] == "--offline") offline = true;
            else if (args[i] == "--ui-test" && i + 1 < args.Length) TestReport = Path.GetFullPath(args[++i]);
            else if (args[i] == "--pause-proof") PauseProof = true;
        }
        var store = new LibraryStore(data);
        var placementRequest = WindowPlacement.Capture(args, store.StatePath);
        string instance = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(store.Root.ToUpperInvariant())))[..24];
        using var mutex = new Mutex(true, "Local\\GameLibrary-" + instance, out bool owner);
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\GameLibrary-Activate-" + instance);
        if (!owner) { activate.Set(); return 0; }
        void LogProcessFailure(string prefix, object? failure)
        {
            try { store.Log(prefix + ": " + failure); } catch { }
        }
        UnhandledExceptionEventHandler processFailure = (_, e) => LogProcessFailure("Unhandled process failure", e.ExceptionObject);
        EventHandler<UnobservedTaskExceptionEventArgs> taskFailure = (_, e) => { LogProcessFailure("Unobserved task failure", e.Exception); e.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += processFailure;
        TaskScheduler.UnobservedTaskException += taskFailure;
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/GameLibrary;component/Theme.xaml", UriKind.Relative) });
        app.DispatcherUnhandledException += (_, e) =>
        {
            LogProcessFailure("Unhandled UI failure", e.Exception);
            try
            {
                if (app.MainWindow is MainWindow window)
                    window.ReportUiFailure("The operation could not finish", e.Exception);
            }
            catch { }
            e.Handled = true;
        };
        try
        {
            var window = new MainWindow(store, offline, placementRequest);
            app.MainWindow = window;
            using var stopping = new CancellationTokenSource();
            var listener = Task.Run(() =>
            {
                while (!stopping.IsCancellationRequested)
                {
                    try
                    {
                        if (activate.WaitOne(500) && !stopping.IsCancellationRequested)
                            app.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    if (!window.IsClosing && !app.Dispatcher.HasShutdownStarted && !app.Dispatcher.HasShutdownFinished)
                                        window.RestoreWindow();
                                }
                                catch (Exception ex) { LogProcessFailure("Activation dispatch failed", ex); }
                            }));
                    }
                    catch (ObjectDisposedException) { if (stopping.IsCancellationRequested || app.Dispatcher.HasShutdownStarted) break; }
                    catch (InvalidOperationException) { if (stopping.IsCancellationRequested || app.Dispatcher.HasShutdownStarted) break; }
                    catch (Exception ex)
                    {
                        LogProcessFailure("Activation listener failed", ex);
                        if (stopping.IsCancellationRequested || app.Dispatcher.HasShutdownStarted) break;
                    }
                }
            });
            var exit = app.Run(window);
            stopping.Cancel();
            listener.Wait(1000);
            return exit;
        }
        catch (Exception ex)
        {
            store.Log("Startup failed: " + ex);
            System.Windows.MessageBox.Show("Game Library could not start.\n\n" + ex.Message + "\n\nDetails: " + Path.Combine(store.Root, "activity.log"), "Game Library", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= taskFailure;
            AppDomain.CurrentDomain.UnhandledException -= processFailure;
            mutex.ReleaseMutex();
        }
    }
    internal static int RunDiagnostic(string report, Func<int> run)
    {
        try { return run(); }
        catch (Exception ex)
        {
            // Diagnostics run without a WPF application/error handler. Report
            // ordinary failures here rather than invoking Windows crash UI.
            try { LibraryStore.AtomicWrite(Path.GetFullPath(report), DataJson.Write(new { passed = false, error = ex.ToString(), at = DateTime.UtcNow })); }
            catch { /* Even an unwritable report must return a failure code. */ }
            try { Console.Error.WriteLine("Diagnostic failed: " + ex.Message); } catch { }
            return 1;
        }
    }
    private static string? FindRepositoryRoot(string start)
    {
        DirectoryInfo? current = new(Path.GetFullPath(start));
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "native")) && Directory.Exists(Path.Combine(current.FullName, "evidence"))) return current.FullName;
            current = current.Parent;
        }
        return null;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
