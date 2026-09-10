using System;
using System.Diagnostics;
using System.IO;
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
        if (args.Length >= 2 && args[0] == "--self-test") return RunDiagnostic(args[1], () => SelfTests.Run(args[1]));
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
        bool secondMonitor = true;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--data-dir" && i + 1 < args.Length) data = Path.GetFullPath(args[++i]);
            else if (args[i] == "--offline") offline = true;
            else if (args[i] == "--second-monitor") secondMonitor = true;
            else if (args[i] == "--main-monitor") secondMonitor = false;
            else if (args[i] == "--ui-test" && i + 1 < args.Length) TestReport = Path.GetFullPath(args[++i]);
            else if (args[i] == "--pause-proof") PauseProof = true;
        }
        var store = new LibraryStore(data);
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
            try { System.Windows.MessageBox.Show("The operation could not finish. Your saved library is preserved.\n\n" + e.Exception.Message, "Game Library", MessageBoxButton.OK, MessageBoxImage.Error); } catch { }
            e.Handled = true;
        };
        try
        {
            var window = new MainWindow(store, offline, secondMonitor);
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
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
