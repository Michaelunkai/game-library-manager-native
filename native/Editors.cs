using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace GameLibrary.Native;

public sealed class EditorWindow : Window
{
    public StackPanel Fields { get; } = new() { Margin = new Thickness(24, 20, 24, 24) };
    public TextBlock Notice { get; } = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
    private readonly CancellationTokenSource closedCancellation = new();
    private bool closed;
    private int asyncActions;
    private int closedCancellationDisposed;
    internal CancellationToken ClosedToken => closedCancellation.Token;
    internal bool IsClosed => closed;
    public EditorWindow(Window owner, string title, string description)
    {
        Owner = owner; Title = title; Width = 570; MaxHeight = SystemParameters.WorkArea.Height - 70;
        Style = (Style)System.Windows.Application.Current.FindResource(typeof(Window));
        SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MinWidth = 400; Icon = owner.Icon;
        Content = new ScrollViewer { Content = Fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Fields.Children.Add(new TextBlock { Text = title, FontSize = 25, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
        Notice.Text = description; Notice.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); Fields.Children.Add(Notice);
        Closed += (_, _) =>
        {
            closed = true;
            try { closedCancellation.Cancel(); }
            catch (Exception ex) { try { (Owner as MainWindow)?.Store.Log("Dialog cancellation failed: " + ex.Message); } catch { } }
            if (Volatile.Read(ref asyncActions) == 0) DisposeClosedCancellation();
        };
    }
    private void DisposeClosedCancellation()
    {
        if (Interlocked.Exchange(ref closedCancellationDisposed, 1) != 0) return;
        try { closedCancellation.Dispose(); } catch { }
    }
    public TextBlock Paragraph(string text)
    {
        var field = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) }; Fields.Children.Add(field); return field;
    }
    private void Label(string label) => Fields.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 10, 0, 6), FontWeight = FontWeights.SemiBold });
    public TextBox Text(string label, string text, string? id = null)
    {
        Label(label); var field = new TextBox { Text = text }; if (id != null) AutomationProperties.SetAutomationId(field, id); Fields.Children.Add(field); return field;
    }
    public PasswordBox Password(string label, string id)
    {
        Label(label); var field = new PasswordBox(); AutomationProperties.SetAutomationId(field, id); Fields.Children.Add(field); return field;
    }
    public ComboBox Choice<T>(string label, IEnumerable<T> choices, int selected = 0, string? id = null)
    {
        Label(label); var field = new ComboBox { ItemsSource = choices, SelectedIndex = selected }; if (id != null) AutomationProperties.SetAutomationId(field, id); Fields.Children.Add(field); return field;
    }
    public CheckBox Check(string label, bool value)
    {
        var field = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 14, 0, 0) }; Fields.Children.Add(field); return field;
    }
    public Button Action(string label, Action action, string? id = null)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        if (id != null) AutomationProperties.SetAutomationId(button, id);
        button.Click += (_, _) => { try { action(); } catch (Exception ex) { Notice.Text = ex.Message; } };
        Fields.Children.Add(button); return button;
    }
    public Button ActionAsync(string label, Func<Task> action, string? id = null)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        if (id != null) AutomationProperties.SetAutomationId(button, id);
        button.Click += async (_, _) =>
        {
            if (closed) return;
            Interlocked.Increment(ref asyncActions);
            try
            {
                Fields.IsEnabled = false; await action();
            }
            catch (OperationCanceledException) when (closed || (Owner as MainWindow)?.IsClosing == true) { }
            catch (OperationCanceledException) { Notice.Text = "The operation was cancelled; your current library was preserved."; }
            catch (Exception ex)
            {
                try { (Owner as MainWindow)?.Store.Log("Dialog action failed: " + ex); } catch { }
                if (!closed && (Owner as MainWindow)?.IsClosing != true && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                {
                    try { Notice.Text = ex.Message; } catch { }
                }
            }
            finally
            {
                try { if (!closed && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished) Fields.IsEnabled = true; }
                catch (Exception ex) { try { (Owner as MainWindow)?.Store.Log("Dialog control restore failed: " + ex.Message); } catch { } }
                if (Interlocked.Decrement(ref asyncActions) == 0 && closed) DisposeClosedCancellation();
            }
        };
        Fields.Children.Add(button); return button;
    }
}

public partial class MainWindow
{
    internal static string TrailerUrl(Game game) => "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(game.Name + " trailer");
    internal static string? DetectWandPath()
    {
        string localWandRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wand");
        var candidates = new List<string>
        {
            Path.Combine(localWandRoot, "Wand.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Wand", "Wand.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Wand", "Wand.exe")
        };
        try
        {
            candidates.AddRange(Directory.EnumerateDirectories(localWandRoot, "app-*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => Path.Combine(path, "Wand.exe")));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return candidates.FirstOrDefault(File.Exists);
    }
    private string ResolveWandPath() => !string.IsNullOrWhiteSpace(State.Settings.WandPath) && File.Exists(State.Settings.WandPath) ? State.Settings.WandPath : DetectWandPath() ?? "";
    private void OpenWand()
    {
        var path = ResolveWandPath();
        if (path.Length == 0) throw new FileNotFoundException("Wand was not found. Set Wand.exe in Settings & backups first.");
        Process.Start(new ProcessStartInfo(path) { WorkingDirectory = Path.GetDirectoryName(path), UseShellExecute = true });
        StatusText.Text = "Wand opened.";
    }
    private void Details(Game game)
    {
        var dialog = new EditorWindow(this, game.Name, game.Meta + "\n" + game.Id);
        dialog.Paragraph(string.IsNullOrWhiteSpace(game.Description) ? game.Details : game.Description);
        var localStorage = dialog.Paragraph("Local install files: measuring…");
        string localMountPath = State.Settings.MountPath;
        string localGameFolder = game.IsLocal && State.LocalGames.TryGetValue(game.Id, out var localGame) ? localGame.Folder : "";
        string localGameId = game.Id, localGameName = game.Name;
        var localStorageCancellation = dialog.ClosedToken;
        dialog.Loaded += async (_, _) =>
        {
            try
            {
                var measurement = await Task.Run(() =>
                {
                    string[] folders = localGameFolder.Length > 0
                        ? Directory.Exists(localGameFolder) ? new[] { localGameFolder } : Array.Empty<string>()
                        : InstalledScanner.FindCatalogFolders(localMountPath, localGameId, localGameName).ToArray();
                    return (Folders: folders, Bytes: InstalledScanner.MeasureFolders(folders, localStorageCancellation));
                }, localStorageCancellation);
                if (dialog.IsClosed) return;
                if (measurement.Folders.Length == 0)
                    localStorage.Text = "Local install files: no matching folder was found under the configured library.";
                else if (measurement.Bytes is long bytes)
                    localStorage.Text = $"Local install files: {bytes / 1_000_000_000d:0.##} GB measured across {measurement.Folders.Length} folder(s). Logical file lengths; allocated disk space can differ. This is separate from the Docker Hub image/download size above.";
                else
                    localStorage.Text = "Local install files: full measurement unavailable (the folder changed or access was denied).";
            }
            catch (OperationCanceledException) when (localStorageCancellation.IsCancellationRequested) { }
            catch
            {
                if (!dialog.IsClosed) localStorage.Text = "Local install files: measurement unavailable.";
            }
        };
        dialog.Action("Watch trailer on YouTube", () => Process.Start(new ProcessStartInfo(TrailerUrl(game)) { UseShellExecute = true }), "WatchTrailer");
        var rate = dialog.Choice("Your rating", new[] { "Unrated", "★", "★★", "★★★", "★★★★", "★★★★★" }, game.Rating);
        var tags = dialog.Text("Tags (comma separated)", string.Join(", ", State.GameTags.GetValueOrDefault(game.Id, new())), "GameTags");
        var installed = dialog.Check("Mark as installed", State.InstalledGames.Contains(game.Id));
        var categoryChoices = EffectiveCategories().Where(c => c.Id is not ("all" or "wishlist" or "installed")).ToList();
        var categories = categoryChoices.ToArray();
        int categoryIndex = Math.Max(0, Array.FindIndex(categories, c => c.Id == game.Category));
        var category = dialog.Choice("Move to tab", categories, categoryIndex, "GameCategory");
        category.ToolTip = "Saved in this Windows library.";
        dialog.Action("Save personal changes", () =>
        {
            PersonalGameEdits.Save(Store, State, game.Id, rate.SelectedIndex, tags.Text, installed.IsChecked == true,
                (category.SelectedItem as Category)?.Id);
            Reload(); dialog.Notice.Text = "Saved on this PC.";
        }, "SaveGameDetails");
        dialog.Action(game.Wishlisted ? "Remove from wishlist" : "Add to wishlist", () =>
        {
            if (!State.Wishlist.Add(game.Id)) State.Wishlist.Remove(game.Id); Save(); Reload(); dialog.Close();
        });
        if (!game.IsLocal)
        {
            dialog.Action("Install this game", () => { dialog.Close(); StartInstall(new[] { game }); });
            dialog.Action("Refresh cover & completion time", () => { dialog.Close(); RefreshMetadata(new[] { game }); });
        }
        dialog.Action("Choose game executable…", () =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Windows game executable|*.exe", Title = "Choose the actual game executable", InitialDirectory = Directory.Exists(State.Settings.MountPath) ? State.Settings.MountPath : "" };
            if (picker.ShowDialog(dialog) != true) return;
            State.LaunchPaths[game.Id] = picker.FileName; State.InstalledGames.Add(game.Id); Save(); Reload(); dialog.Notice.Text = "Launcher saved: " + picker.FileName;
        });
        if (game.Installed)
        {
            dialog.ActionAsync("Play", async () => { await PlayGame(game); if (!dialog.IsClosed && !closing) dialog.Close(); }, "PlayGame");
            dialog.Action("Open Wand", OpenWand, "OpenWand");
        }
        if (game.Installed && CanLaunchWithExistingWand(game, out _))
            dialog.ActionAsync("Play with Wand mods", async () =>
            {
                var target = ResolveWandCardTarget(game) ?? throw new InvalidOperationException("The exact Wand registration is no longer available.");
                await PlayWithWand(target, dialog.ClosedToken);
                if (!dialog.IsClosed && !closing) dialog.Close();
            }, "PlayWithWand");
        dialog.Action("Open installation folder", () => OpenFolder(ResolveInstallationFolder(game)));
        if (!game.IsLocal)
        {
            dialog.Action("Copy Docker command", () => { System.Windows.Clipboard.SetText(DockerScripts.Generate(new[] { game }, State.Settings)); dialog.Notice.Text = "PowerShell download script copied."; });
            dialog.Action("Copy Docker Hub link", () => { System.Windows.Clipboard.SetText(game.DockerImageUrl); dialog.Notice.Text = "Docker Hub URL copied."; });
        }
        dialog.ShowDialog();
    }
    private async Task PlayGame(Game game)
    {
        if (!game.Installed) throw new InvalidOperationException("Install this game before playing it.");
        await playLaunchGate.WaitAsync(lifetime.Token);
        try
        {
            if (closing) return;
            if (!TryGetLauncher(game, out var exe)) throw new FileNotFoundException("No game executable was found. Scan the installation folder or choose the game's executable.");
            if (!Path.GetExtension(exe).Equals(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The launcher must be a Windows executable.");
            if (TryActivateExistingPlay(game)) return;
            var running = WandIntegration.FindRunningExactProcess(exe);
            if (running != null)
            {
                if (TrackPlayProcess(game, running, ownsProcess: false))
                {
                    ActivateProcess(running);
                    StatusText.Text = game.Name + " is already running.";
                }
                else StatusText.Text = game.Name + " closed before it could be tracked.";
                return;
            }
            bool started = StartPlaySession(game, () => Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = true }) ?? throw new InvalidOperationException("The game process could not be started."));
            if (!started) { StatusText.Text = game.Name + " closed before it could be tracked."; return; }
            StatusText.Text = "Playing " + game.Name + " · tracking time";
        }
        finally { playLaunchGate.Release(); }
    }
    private string ResolveInstallationFolder(Game game)
    {
        if (State.LaunchPaths.TryGetValue(game.Id, out var executable) && File.Exists(executable))
            return Path.GetDirectoryName(executable)!;
        if (game.IsLocal && State.LocalGames.TryGetValue(game.Id, out var local)) return local.Folder;
        return InstalledScanner.FindCatalogFolders(State.Settings.MountPath, game.Id, game.Name).FirstOrDefault()
            ?? Path.Combine(State.Settings.MountPath, DockerScripts.InstallFolder(game.Id));
    }
    private Task PlayWithWand(Game game) => PlayWithWand(game, lifetime.Token);
    private async Task PlayWithWand(Game game, CancellationToken cancellation)
    {
        if (!game.Installed) throw new InvalidOperationException("Install this game before playing it.");
        await playLaunchGate.WaitAsync(cancellation);
        try
        {
            if (closing || cancellation.IsCancellationRequested) return;
            if (!State.LaunchPaths.TryGetValue(game.Id, out var savedLauncher) || string.IsNullOrWhiteSpace(savedLauncher))
                throw new FileNotFoundException("No exact Wand launcher is saved for this game.");
            string configuredWandPath = State.Settings.WandPath;
            var launch = await Task.Run(() =>
            {
                string exe = Path.GetFullPath(savedLauncher);
                if (!File.Exists(exe)) throw new FileNotFoundException("The exact Wand launcher is unavailable.", exe);
                string extension = Path.GetExtension(exe);
                if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("The Wand launcher must be an exact Windows executable or registered command script.");
                string wandPath = !string.IsNullOrWhiteSpace(configuredWandPath) && File.Exists(configuredWandPath)
                    ? configuredWandPath
                    : DetectWandPath() ?? "";
                if (wandPath.Length == 0) throw new FileNotFoundException("Wand was not found. Set Wand.exe in Settings & backups first.");
                if (!WandIntegration.CanLaunchExistingWandInstall(game, exe, Store, out var reason)) throw new InvalidOperationException(reason);
                return (Executable: exe, WandPath: wandPath);
            }, cancellation);
            string exe = launch.Executable;
            string wandPath = launch.WandPath;
            WandSessionState.TryGet(game.Id, exe, out var currentWandSession);
            bool previouslyConnected = activePlays.TryGetValue(game.Id, out var existingWand)
                && !existingWand.Process.HasExited
                && currentWandSession != null
                && currentWandSession.Status == WandSessionStatus.Connected
                && currentWandSession.ProcessId == existingWand.Process.Id
                && FrozenProcessState.TryGetCreationStamp(existingWand.Process, out var currentCreation)
                && string.Equals(currentCreation, currentWandSession.ProcessCreationFileTime, StringComparison.Ordinal);
            if (previouslyConnected)
            {
                // A cached Connected state can outlive the trainer. Inspect the
                // exact running trainer off the dispatcher before treating this
                // click as a request merely to focus the existing game.
                var prior = currentWandSession!;
                var fresh = await Task.Run(() => WandTrainerEvidenceAdapter.Inspect(wandPath, prior.GameId,
                    prior.ProcessId!.Value, prior.ProcessCreationFileTime!, DateTime.UtcNow.AddSeconds(-1), exe), cancellation);
                if (fresh.Confirmed && TryActivateExistingPlay(game)) return;
                if (WandSessionState.Find(game.Id, exe) is { } stale && stale.Read().OperationId == prior.OperationId)
                {
                    try
                    {
                        if (stale.Read().Status == WandSessionStatus.Connected)
                            stale.Transition(WandIntegration.StatusForTrainerEvidence(fresh),
                                fresh.Detail, fresh.Source, fresh.ObservedUtc);
                    }
                    catch (InvalidOperationException)
                    {
                        // An exit or newer launch can change the session while
                        // this read-only probe is completing; keep its newer state.
                    }
                }
            }
            using var observationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            var observation = ObserveWandGameProcess(game, exe, observationCancellation.Token);
            WandLaunchResult result;
            try
            {
                // The Wand/catalog/process/storage workflow is intentionally kept
                // off the WPF dispatcher. A blocked game drive or profile volume
                // cannot stop window painting, filtering, or the Exit control.
                result = await Task.Run(() => WandIntegration.LaunchAsync(game, exe, wandPath, Store, cancellation), cancellation);
            }
            finally
            {
                observationCancellation.Cancel();
                try { await observation; }
                catch (OperationCanceledException) when (observationCancellation.IsCancellationRequested) { }
            }
            if (closing || cancellation.IsCancellationRequested)
            {
                // Closing Game Library must never terminate a game or Wand.
                // Release only this diagnostic handle; the OS process continues.
                if (result.Process != null) try { result.Process.Dispose(); } catch { }
                return;
            }
            if (result.Process != null)
            {
                if (activePlays.TryGetValue(game.Id, out var tracked))
                {
                    tracked.UsesWand = result.Session?.Status == WandSessionStatus.Connected;
                    if (!ReferenceEquals(tracked.Process, result.Process)) try { result.Process.Dispose(); } catch { }
                }
                else if (!TrackPlayProcess(game, result.Process, usesWand: result.Session?.Status == WandSessionStatus.Connected, ownsProcess: result.OwnsProcess))
                    result = result with { Process = null, OwnsProcess = false, Message = game.Name + " closed before Wand launch confirmation. Game Library did not terminate it." };
            }
            StatusText.Text = result.Message;
            Store.Log("Wand launch for " + game.Id + "; protocol=" + result.UsedProtocol.ToString().ToLowerInvariant() + "; started=" + (result.Process != null).ToString().ToLowerInvariant());
        }
        finally { playLaunchGate.Release(); }
    }

    private async Task ObserveWandGameProcess(Game game, string executable, CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested && !closing)
            {
                var process = WandIntegration.FindRunningExactProcess(executable);
                if (process != null)
                {
                    if (activePlays.ContainsKey(game.Id)) { process.Dispose(); return; }
                    else if (TrackPlayProcess(game, process, usesWand: false, ownsProcess: false)) return;
                }
                await Task.Delay(200, cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            try { Store.Log("Early Wand game observation failed; final launch tracking will retry: " + ex.Message); } catch { }
        }
    }
    private bool TryGetLauncher(Game game, out string executable)
    {
        if (State.LaunchPaths.TryGetValue(game.Id, out var saved) && File.Exists(saved))
        {
            string selected = saved;
            if (LooksLikeBootstrapExecutable(saved))
            {
                string? folder = Path.GetDirectoryName(Path.GetFullPath(saved));
                string? corrected = folder == null ? null : WandIntegration.ResolveInstalledExecutable(game, folder, Store);
                if (!string.IsNullOrWhiteSpace(corrected) && !string.Equals(corrected, saved, StringComparison.OrdinalIgnoreCase))
                {
                    selected = corrected;
                    State.LaunchPaths[game.Id] = corrected;
                    Save();
                    Store.Log("Corrected bootstrap launcher for " + game.Id + " to the exact game executable: " + corrected);
                }
            }
            executable = selected;
            return true;
        }
        var folders = game.IsLocal && State.LocalGames.TryGetValue(game.Id, out var local)
            ? new[] { local.Folder }
            : InstalledScanner.FindCatalogFolders(State.Settings.MountPath, game.Id, game.Name).ToArray();
        string? discovered = folders.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(folder => WandIntegration.ResolveInstalledExecutable(game, folder, Store))
            .FirstOrDefault(path => path != null);
        if (discovered == null)
        {
            executable = "";
            return false;
        }
        State.LaunchPaths[game.Id] = discovered;
        State.InstalledGames.Add(game.Id);
        Save();
        Store.Log("Auto-selected game executable for " + game.Id + ": " + discovered);
        executable = discovered;
        return true;
    }
    private static bool LooksLikeBootstrapExecutable(string executable)
    {
        try
        {
            string stem = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
            if (stem.Contains("launcher", StringComparison.Ordinal)
                || stem.Contains("bootstrap", StringComparison.Ordinal)
                || stem.Contains("updater", StringComparison.Ordinal)
                || stem.Contains("installer", StringComparison.Ordinal)) return true;
            return new FileInfo(executable).Length < 512 * 1024;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (ArgumentException) { return false; }
    }
    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new EditorWindow(this, "Settings & backups", "Shared categories and tabs use the website backend. Wishlist, ratings, tags, launch paths, and settings are saved on this PC.");
        var importCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var importToken = importCancellation.Token;
        bool importRunning = false, importDialogClosed = false, importCancellationDisposed = false;
        void DisposeImportCancellation()
        {
            if (importCancellationDisposed) return;
            importCancellationDisposed = true;
            try { importCancellation.Dispose(); } catch { }
        }
        dialog.Closed += (_, _) =>
        {
            importDialogClosed = true;
            try { importCancellation.Cancel(); } catch (ObjectDisposedException) { }
            if (!importRunning) DisposeImportCancellation();
        };
        var root = dialog.Text("Download folder", State.Settings.MountPath, "DownloadFolder");
        dialog.Paragraph("New installs default to E:\\games. You can choose another folder at any time; existing files are never moved automatically.");
        dialog.Action("Browse folder…", () =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Download folder" };
            if (picker.ShowDialog(dialog) == true) root.Text = picker.FolderName;
        });
        var user = dialog.Text("Docker Hub username", State.Settings.DockerUsername);
        var repo = dialog.Text("Repository", State.Settings.RepoName);
        var scriptFormat = dialog.Choice("Default export format", new[] { "bat", "ps1", "sh" }, State.Settings.ScriptFormat == "ps1" ? 1 : State.Settings.ScriptFormat == "sh" ? 2 : 0, "ScriptFormat");
        var shellTarget = dialog.Choice("Bash target", new[] { "native-linux", "wsl2" }, State.Settings.ShellTarget == "wsl2" ? 1 : 0, "ShellTarget");
        var wand = dialog.Text("Wand executable (optional)", ResolveWandPath(), "WandPath");
        var frozenProcesses = dialog.Text("AHK frozen-process state file", State.Settings.FrozenProcessesPath, "FrozenProcessesPath");
        dialog.Paragraph("Playtime automatically pauses when Ctrl+H records this game's exact PID and creation stamp as paused in the file. A missing file means the game is running normally; an unreadable file holds the last known timing state.");
        dialog.Action("Browse AHK state file...", () =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Title = "Choose frozen-processes.ini", Filter = "AHK state file|frozen-processes.ini;*.ini|All files|*.*", CheckFileExists = false };
            if (picker.ShowDialog(dialog) == true) frozenProcesses.Text = picker.FileName;
        }, "BrowseFrozenProcesses");
        dialog.Action("Detect installed Wand", () => { wand.Text = DetectWandPath() ?? ""; dialog.Notice.Text = wand.Text.Length > 0 ? "Wand executable found." : "Wand was not found; browse to Wand.exe or leave it blank."; }, "DetectWand");
        dialog.Action("Browse Wand executable…", () =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Title = "Choose Wand.exe", Filter = "Wand executable|Wand.exe;*.exe" };
            if (picker.ShowDialog(dialog) == true) wand.Text = picker.FileName;
        }, "BrowseWand");
        var theme = dialog.Choice("Appearance", new[] { "dark", "light" }, State.Settings.Theme == "light" ? 1 : 0);
        var density = dialog.Choice("Cover size", new[] { "small", "medium", "large" }, State.Settings.GridSize == "small" ? 0 : State.Settings.GridSize == "large" ? 2 : 1);
        var showTimes = dialog.Check("Show approximate completion times", State.Settings.ShowTimes);
        var showCategories = dialog.Check("Show game categories", State.Settings.ShowCategories);
        var minimize = dialog.Check("Minimize to tray (Close exits the app)", State.Settings.MinimizeToTray);
        dialog.Action("Save settings", () =>
        {
            var next = DataJson.Read<Preferences>(DataJson.Write(State.Settings));
            next.MountPath = root.Text.Trim(); next.DockerUsername = user.Text.Trim(); next.RepoName = repo.Text.Trim();
            DockerScripts.Validate(next, Array.Empty<Game>());
            next.Theme = theme.SelectedItem as string ?? "dark"; next.MinimizeToTray = minimize.IsChecked == true;
            next.GridSize = density.SelectedItem as string ?? "medium"; next.ShowTimes = showTimes.IsChecked == true; next.ShowCategories = showCategories.IsChecked == true;
            next.ScriptFormat = scriptFormat.SelectedItem as string ?? Preferences.DefaultScriptFormat;
            next.ShellTarget = shellTarget.SelectedItem as string ?? Preferences.DefaultShellTarget;
            next.WandPath = wand.Text.Trim();
            if (next.WandPath.Length > 0 && (!Path.IsPathFullyQualified(next.WandPath) || !File.Exists(next.WandPath) || !Path.GetFileName(next.WandPath).Equals("Wand.exe", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Choose an existing Wand.exe, or leave the Wand path blank to use automatic detection.");
            next.FrozenProcessesPath = string.IsNullOrWhiteSpace(frozenProcesses.Text) ? Preferences.DefaultFrozenProcessesPath : Path.GetFullPath(frozenProcesses.Text.Trim());
            State.Settings = next; Save(); ApplyTheme(); Reload(); dialog.Notice.Text = "Settings saved.";
        }, "SaveSettings");
        dialog.Action("Export complete backup…", () =>
        {
            var picker = new Microsoft.Win32.SaveFileDialog { Title = "Export library backup", FileName = "game-library-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), DefaultExt = ".json", Filter = "Library backup|*.json" };
            if (picker.ShowDialog(dialog) != true) return;
            var backup = JsonNode.Parse(DataJson.Write(State))!.AsObject();
            backup["selectedGames"] = new JsonArray(Selected().Select(g => (JsonNode?)JsonValue.Create(g.Id)).ToArray());
            backup["exportDate"] = DateTime.UtcNow.ToString("O");
            File.WriteAllText(picker.FileName, backup.ToJsonString(DataJson.Options)); dialog.Notice.Text = "Backup exported. Its settings and selections can also be imported by the website.";
        }, "ExportBackup");
        dialog.ActionAsync("Import backup or website settings…", async () =>
        {
            importRunning = true;
            try
            {
                var picker = new Microsoft.Win32.OpenFileDialog { Title = "Import library backup", Filter = "Library backup / website settings|*.json" };
                if (picker.ShowDialog(dialog) != true) return;
                var raw = File.ReadAllText(picker.FileName);
                var parsed = JsonNode.Parse(raw)?.AsObject() ?? throw new FormatException("Invalid backup document.");
                dialog.Notice.Text = "Waiting for any active sync before applying this backup…";
                await ImportBackupAsync(parsed, importToken);
                if (parsed["selectedGames"] is JsonArray ids)
                {
                    var selection = ids.Select(n => DataJson.Text(n)).ToHashSet(StringComparer.Ordinal);
                    foreach (var game in Games) game.Selected = selection.Contains(game.Id); UpdateStats();
                }
                dialog.Close(); StatusText.Text = "Backup imported; the previous library was preserved.";
            }
            finally
            {
                importRunning = false;
                if (importDialogClosed) DisposeImportCancellation();
            }
        }, "ImportBackup");
        dialog.Action("Open data & logs folder", () => OpenFolder(Store.Root));
        dialog.Paragraph("Keyboard: Ctrl+K search · Ctrl+A select visible · Enter details · Esc clear · F5 refresh\nVersion 1.0 · Native WPF / Windows · " + Store.Root);
        dialog.ShowDialog();
    }
    private void ManageCategories(object sender, RoutedEventArgs e)
    {
        var dialog = new EditorWindow(this, "Manage categories", "Category changes are saved in this Windows library, including while offline.");
        var effective = Sync.Effective(State);
        var categories = EffectiveCategories(effective).Where(c => c.Id is not ("all" or "wishlist" or "installed")).ToArray();
        var selected = dialog.Choice("Category", categories);
        var name = dialog.Text("New name / renamed category", "", "CategoryName");
        var hideTab = dialog.Check("Hide category tab", false);
        AutomationProperties.SetAutomationId(hideTab, "CategoryHideTab");
        var hideGamesFromAll = dialog.Check("Hide games from All games", false);
        AutomationProperties.SetAutomationId(hideGamesFromAll, "CategoryHideGamesFromAll");
        void LoadSelectedCategory()
        {
            if (selected.SelectedItem is not Category c) return;
            name.Text = c.Name;
            var current = Sync.Effective(State);
            var rule = CategoryVisibility.Get(State, current, c.Id);
            hideTab.IsChecked = rule.HideTab;
            hideGamesFromAll.IsChecked = rule.HideGamesFromAll;
        }
        selected.SelectionChanged += (_, _) =>
        {
            LoadSelectedCategory();
        };
        LoadSelectedCategory();
        dialog.Action("Create category", () =>
        {
            string label = name.Text.Trim(); if (label.Length == 0 || label.Length > 80) throw new ArgumentException("Enter a category name of 1–80 characters.");
            string id = System.Text.RegularExpressions.Regex.Replace(label.ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');
            if (id.Length == 0) id = "category_" + Guid.NewGuid().ToString("N")[..8];
            var tabs = EffectiveCategories(Sync.Effective(State));
            if (tabs.Any(c => c.Id == id) || id is "wishlist" or "installed") throw new ArgumentException("A category with that identity already exists.");
            tabs.Add(new(id, label));
            var deleted = State.LocalCatalog["deletedTabs"] is JsonArray oldDeleted
                ? oldDeleted.Select(item => DataJson.Text(item)).Where(value => value != id).ToList()
                : new List<string>();
            LocalCatalogEdits.Save(Store, State,
                new PendingEdit { Section = "tabs", After = JsonNode.Parse(DataJson.Write(tabs)) },
                new PendingEdit { Section = "deletedTabs", After = new JsonArray(deleted.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()) });
            dialog.Close(); Reload();
        });
        dialog.Action("Save name and visibility", () =>
        {
            if (selected.SelectedItem is not Category c) return;
            if (name.Text.Trim().Length is < 1 or > 80) throw new ArgumentException("Enter a name of 1–80 characters.");
            var current = Sync.Effective(State);
            var updatedCategories = EffectiveCategories(current);
            var tabs = updatedCategories.Select(t => t.Id == c.Id ? new Category(c.Id, name.Text.Trim()) : t).ToList();
            var updated = new CategoryVisibilityRule(hideTab.IsChecked == true, hideGamesFromAll.IsChecked == true);
            CategoryVisibility.SaveRules(Store, State, current, updatedCategories, c.Id, updated,
                new PendingEdit { Section = "tabs", After = JsonNode.Parse(DataJson.Write(tabs)) });
            dialog.Close(); Reload();
        });
        dialog.Action("Show all tabs", () =>
        {
            var current = Sync.Effective(State);
            CategoryVisibility.ShowAllTabs(Store, State, current, EffectiveCategories(current));
            LoadSelectedCategory();
            dialog.Notice.Text = "All category tabs are visible.";
            Reload();
        });
        dialog.Action("Show all games in All games", () =>
        {
            var current = Sync.Effective(State);
            CategoryVisibility.ShowAllGamesInAll(Store, State, current, EffectiveCategories(current));
            LoadSelectedCategory();
            dialog.Notice.Text = "All categories are included in All games.";
            Reload();
        });
        void ReorderCategory(int delta)
        {
            if (selected.SelectedItem is not Category c) return;
            var tabs = EffectiveCategories(Sync.Effective(State));
            if (!MoveCategory(tabs, c.Id, delta))
            {
                dialog.Notice.Text = delta < 0 ? "That category is already first." : "That category is already last.";
                return;
            }
            QueueTabs(tabs); dialog.Close(); Reload();
        }
        dialog.Action("Move category up", () => ReorderCategory(-1));
        dialog.Action("Move category down", () => ReorderCategory(1));
        dialog.Action("Remove category (move its games to New)", () =>
        {
            if (selected.SelectedItem is not Category c || c.Id == "new") throw new ArgumentException("The New category cannot be removed.");
            if (System.Windows.MessageBox.Show(dialog, "Remove '" + c.Name + "' and move its games to New?", "Remove category", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var effective = Sync.Effective(State);
            var categoriesBeforeDelete = EffectiveCategories(effective);
            var loadedIds = Games.Select(game => game.Id).ToHashSet(StringComparer.Ordinal);
            var affectedIds = Games.Where(game => game.Category == c.Id).Select(game => game.Id).ToHashSet(StringComparer.Ordinal);
            // A temporarily absent repository game still has a saved assignment.
            // Move it too, so rediscovery cannot revive a deleted category. For
            // loaded games retain their actual category (including local overrides).
            if (effective["gameCategories"] is JsonObject assignments)
                foreach (var assignment in assignments)
                    if (!loadedIds.Contains(assignment.Key) && DataJson.Text(assignment.Value) == c.Id) affectedIds.Add(assignment.Key);
            var edits = affectedIds.OrderBy(id => id, StringComparer.Ordinal).Select(id => new PendingEdit { Section = "gameCategories", Key = id, After = JsonValue.Create("new") }).ToList();
            edits.Add(new PendingEdit { Section = "tabs", After = JsonNode.Parse(DataJson.Write(categoriesBeforeDelete.Where(t => t.Id != c.Id).ToList())) });
            var deletedIds = State.LocalCatalog["deletedTabs"] is JsonArray existingDeleted
                ? existingDeleted.Select(item => DataJson.Text(item)).Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            deletedIds.Add(c.Id);
            edits.Add(new PendingEdit { Section = "deletedTabs", After = new JsonArray(deletedIds.OrderBy(id => id, StringComparer.Ordinal).Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) });
            var values = Hidden(effective); values.Remove(c.Id);
            edits.Add(new PendingEdit { Section = "hiddenTabs", After = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) });
            var reliability = State.LocalCatalog["reliability"] is JsonObject currentReliability ? (JsonObject)currentReliability.DeepClone() : new JsonObject();
            if (reliability["categoryVisibility"] is JsonObject visibility && visibility["categories"] is JsonObject rules)
                rules.Remove(c.Id);
            edits.Add(new PendingEdit { Section = "reliability", After = reliability });
            LocalCatalogEdits.Save(Store, State, edits.ToArray());
            dialog.Close(); Reload();
        });
        dialog.ShowDialog();
    }
    internal static bool MoveCategory(IList<Category> tabs, string id, int delta)
    {
        int index = -1;
        for (int i = 0; i < tabs.Count; i++)
            if (string.Equals(tabs[i].Id, id, StringComparison.Ordinal)) { index = i; break; }
        int target = index < 0 ? -1 : index + delta;
        if (index < 0 || target < 0 || target >= tabs.Count) return false;
        (tabs[index], tabs[target]) = (tabs[target], tabs[index]);
        return true;
    }
    private void QueueTabs(List<Category> tabs) => LocalCatalogEdits.Save(Store, State, new PendingEdit { Section = "tabs", After = JsonNode.Parse(DataJson.Write(tabs)) });
    private void ShowSync(object sender, RoutedEventArgs e)
    {
        var dialog = new EditorWindow(this, "Synchronization", Sync.Status);
        dialog.Paragraph(Sync.SharedStatus + "\n" + Sync.CatalogStatus + "\n" + Sync.DockerStatus + "\n" + namespaceStatus);
        try
        {
            string attemptPath = Path.Combine(Store.Cache, "docker-namespace-attempt.json");
            if (File.Exists(attemptPath) && JsonNode.Parse(File.ReadAllText(attemptPath))?["errors"] is JsonArray errors && errors.Count > 0)
                dialog.Paragraph("Latest Docker checks:\n" + string.Join("\n", errors.Take(12).Select(error => DataJson.Text(error?["scope"]) + ": " + DataJson.Text(error?["code"]))) +
                    (errors.Count > 12 ? "\nAdditional failures are recorded in the local sync log." : ""));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { Store.Log("Sync diagnostic read failed: " + ex.GetType().Name); }
        dialog.Paragraph("Backend: " + SyncClient.Production + "\nLast successful contact: " + (Sync.LastSync?.ToString("g") ?? "not yet this session") + "\nQueued changes: " + State.Pending.Count);
        dialog.Paragraph(Sync.SupportsConditionalWrites ? "The server supports conditional writes." : "This server does not advertise atomic conditional writes. The app checks for stale edits before saving and verifies read-back, but simultaneous writes from other clients can still race.");
        if (State.Pending.Count > 0)
        {
            var choice = dialog.Choice("Pending edit", State.Pending.Select(e => new PendingChoice(e)).ToArray());
            dialog.Action("Keep website value for selected edit", () =>
            {
                if (choice.SelectedItem is not PendingChoice chosen) return;
                State.Pending.Remove(chosen.Edit); Save(); dialog.Close(); Reload();
            });
            dialog.Action("Rebase my selected edit on current website value", () =>
            {
                if (choice.SelectedItem is not PendingChoice chosen) return;
                chosen.Edit.Before = Merge.Get(Sync.Remote, chosen.Edit)?.DeepClone(); chosen.Edit.Conflict = null; Save(); dialog.Close(); Reload();
            });
        }
        dialog.Action("Refresh / publish queued changes", () => { dialog.Close(); ObserveUiOperation("Sync refresh", () => Refresh(false)); });
        dialog.Action("Open local sync log", () => OpenFolder(Store.Root)); dialog.ShowDialog();
    }
    private sealed record PendingChoice(PendingEdit Edit) { public override string ToString() => Edit.Section + "/" + Edit.Key + (Edit.Conflict != null ? " · CONFLICT" : " · queued"); }
}

public static class InstalledScanner
{
    internal static bool IsInstallerStagingPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return Path.GetFullPath(path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part.Equals(DockerScripts.StagingDirectoryName, StringComparison.OrdinalIgnoreCase));
    }
    // Return logical file lengths for exact, caller-resolved install folders.
    // Do not follow junctions/symlinks, and never return a partial total as if it
    // were complete when an install is changing or a file is inaccessible.
    internal static long? MeasureFolders(IEnumerable<string> folders, CancellationToken cancellation)
    {
        try
        {
            var roots = folders.Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.GetFullPath(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (roots.Length == 0) return null;
            long bytes = 0;
            var options = new EnumerationOptions
            {
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false
            };
            foreach (var root in roots)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return null;
                var pending = new Stack<string>();
                pending.Push(root);
                while (pending.Count > 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var folder = pending.Pop();
                    if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return null;
                    foreach (var file in Directory.EnumerateFiles(folder, "*", options))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                            bytes = checked(bytes + new FileInfo(file).Length);
                    }
                    foreach (var child in Directory.EnumerateDirectories(folder, "*", options))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
                    }
                }
            }
            return bytes;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or System.Security.SecurityException or OverflowException) { return null; }
    }

    public static Dictionary<string, string> Scan(string root, IEnumerable<(string Id, string Name)> games, CancellationToken cancellation)
    {
        // Compatibility for callers that need only an unambiguous catalog-to-launcher map.
        return Discover(root, games, cancellation).Games.Where(g => !g.IsLocal && g.Launcher != null)
            .ToDictionary(g => g.Id, g => g.Launcher!, StringComparer.Ordinal);
    }

    public static InstalledScanResult Discover(string root, IEnumerable<(string Id, string Name)> games, CancellationToken cancellation, IReadOnlyDictionary<string, string>? preferredInstallations = null)
    {
        root = Path.GetFullPath(root);
        if (IsInstallerStagingPath(root)) return new InstalledScanResult();
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The selected game folder is unavailable.");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Choose the actual game folder rather than a folder link.");
        var catalog = games.Where(g => !g.Id.StartsWith("local:", StringComparison.Ordinal)).ToArray();
        var lookups = Lookups(catalog);
        var result = new InstalledScanResult();
        var folders = Directory.EnumerateDirectories(root, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToArray();
        AddCatalogFolderLookups(lookups, root, catalog);
        foreach (var folder in folders) AddCatalogFolderLookups(lookups, folder, catalog);
        var selectedInstallFolders = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var game in catalog)
        {
            string? preferred = preferredInstallations?.GetValueOrDefault(game.Id);
            string? selected = SelectCatalogFolder(root, game.Id, game.Name, preferred);
            if (selected != null) selectedInstallFolders[game.Id] = selected;
        }
        // A launcher alongside a library's game subfolders must not hide those games.
        // A catalog folder or a same-named executable identifies a directly selected game.
        string rootName = Normalize(Path.GetFileName(Path.TrimEndingDirectorySeparator(root)));
        var rootExecutables = Directory.EnumerateFiles(root, "*.exe", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(p => IsGameExecutable(root, p)).Take(101).ToArray();
        bool isGameRoot = lookups.ContainsKey(rootName) ||
            rootExecutables.Any(p => Normalize(Path.GetFileNameWithoutExtension(p)) == rootName);
        if (isGameRoot) Inspect(root, lookups, true, result, cancellation);
        else
        {
            result.LibraryRoot = root;
            foreach (var folder in folders)
            {
                string label = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
                bool unselectedCanonicalInstall = catalog.Any(game =>
                {
                    bool installFolder = label.Equals(DockerScripts.InstallFolder(game.Id), StringComparison.OrdinalIgnoreCase)
                        || DockerScripts.IsVersionedInstallFolder(game.Id, label);
                    if (!installFolder) return false;
                    return !selectedInstallFolders.TryGetValue(game.Id, out string? chosen)
                        || !string.Equals(Path.GetFullPath(chosen), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase);
                });
                if (!unselectedCanonicalInstall) Inspect(folder, lookups, true, result, cancellation);
            }
            if (result.Games.Count == 0 && rootExecutables.Length > 0) Inspect(root, lookups, true, result, cancellation);
            else if (rootExecutables.Length > 0) result.Notices.Add(Path.GetFileName(root) + ": game subfolders were scanned; executables beside those folders were not assumed to be another game.");
        }
        return result;
    }

    public static InstalledScanResult ScanDownloads(string root, IEnumerable<(string Id, string Name)> games, CancellationToken cancellation, IReadOnlyDictionary<string, string>? preferredInstallations = null, string? requiredOperationId = null)
    {
        var catalog = games.Where(g => DockerIdentity.Valid(g.Id)).ToArray();
        var lookups = Lookups(catalog);
        var result = new InstalledScanResult();
        foreach (var game in catalog)
        {
            cancellation.ThrowIfCancellationRequested();
            string? preferred = preferredInstallations?.GetValueOrDefault(game.Id);
            string? folder = SelectCatalogFolder(root, game.Id, game.Name, preferred, requiredOperationId);
            if (folder == null)
            {
                // Keep a non-playable payload visible to diagnostics without
                // promoting it to an installed game. This includes folders
                // containing only support utilities.
                if (FindCatalogFolders(root, game.Id, game.Name, requiredOperationId).Count > 0)
                {
                    result.CatalogFoldersPresent.Add(game.Id);
                    result.Notices.Add(game.Name + ": a download folder exists, but no Windows game executable was found.");
                }
                else result.Notices.Add(game.Name + ": the downloaded game folder was not found.");
                continue;
            }
            AddCatalogFolderLookup(lookups, folder, game);
            Inspect(folder, lookups, false, result, cancellation);
        }
        return result;
    }

    /// <summary>
    /// Returns the current install folder plus safe legacy folders whose name is
    /// the catalog id followed by a separator/hash. Older builds used a different
    /// suffix formula, so exact-path lookup alone loses otherwise valid installs.
    /// </summary>
    internal static IReadOnlyList<string> FindCatalogFolders(string root, string id, string? name = null, string? requiredOperationId = null)
    {
        if (!Directory.Exists(root) || !DockerIdentity.Valid(id)) return Array.Empty<string>();
        root = Path.GetFullPath(root);
        var expected = Path.Combine(root, DockerScripts.InstallFolder(id));
        var folders = new List<string>();
        void Add(string path)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
                if (!folders.Contains(path, StringComparer.OrdinalIgnoreCase)) folders.Add(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Add(expected);
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(root, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                string folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
                if (DockerScripts.IsVersionedInstallFolder(id, folderName)
                    && IsValidVersionedCompletionMarker(Path.Combine(folder, DockerScripts.CompletionMarkerName), id, requiredOperationId)) Add(folder);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        if (DockerIdentity.IsQualified(id)) return folders.OrderBy(path => path.Equals(expected, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ThenBy(path => path, StringComparer.Ordinal).ToArray();
        Add(Path.Combine(root, id));
        if (!string.IsNullOrWhiteSpace(name)) Add(Path.Combine(root, name));
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(root, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                string folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
                bool friendlyName = !string.IsNullOrWhiteSpace(name) && FolderIdentityMatches(folderName, name);
                bool idAlias = FolderIdentityMatches(folderName, id);
                if (DockerScripts.IsVersionedInstallFolder(id, folderName))
                {
                    if (IsValidVersionedCompletionMarker(Path.Combine(folder, DockerScripts.CompletionMarkerName), id, requiredOperationId)) Add(folder);
                    continue;
                }
                if (folderName.StartsWith(id + "-", StringComparison.OrdinalIgnoreCase) || idAlias || friendlyName) Add(folder);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return folders.OrderBy(path => path.Equals(expected, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? SelectCatalogFolder(string root, string id, string? name = null, string? preferredPath = null, string? requiredOperationId = null)
    {
        IReadOnlyList<string> candidates = FindCatalogFolders(root, id, name, requiredOperationId);
        if (!string.IsNullOrWhiteSpace(requiredOperationId))
            candidates = candidates.Where(path => IsValidCompletionMarker(Path.Combine(path, DockerScripts.CompletionMarkerName), id, requiredOperationId)).ToArray();
        if (candidates.Count == 0) return null;
        string? preferred = candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(preferredPath)
            && string.Equals(Path.GetFullPath(path), Path.GetFullPath(preferredPath), StringComparison.OrdinalIgnoreCase)
            && FindGameExecutables(path).Count > 0);
        if (preferred != null) return preferred;
        var versioned = candidates.Where(path => DockerScripts.IsVersionedInstallFolder(id, Path.GetFileName(Path.TrimEndingDirectorySeparator(path)))
                && IsValidVersionedCompletionMarker(Path.Combine(path, DockerScripts.CompletionMarkerName), id, requiredOperationId)
                && FindGameExecutables(path).Count > 0)
            .Select(path => new { Path = path, MarkerTime = SafeMarkerWriteTime(Path.Combine(path, DockerScripts.CompletionMarkerName)) })
            .OrderByDescending(item => item.MarkerTime).ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Path, StringComparer.Ordinal)
            .FirstOrDefault();
        if (versioned != null) return versioned.Path;
        string expected = Path.Combine(Path.GetFullPath(root), DockerScripts.InstallFolder(id));
        return candidates.Where(path => !DockerScripts.IsVersionedInstallFolder(id, Path.GetFileName(Path.TrimEndingDirectorySeparator(path))) && FindGameExecutables(path).Count > 0)
            .OrderBy(path => string.Equals(Path.GetFullPath(path), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ThenBy(path => path, StringComparer.Ordinal).FirstOrDefault();
    }

    private static DateTime SafeMarkerWriteTime(string markerPath)
    {
        try { return File.GetLastWriteTimeUtc(markerPath); }
        catch (IOException) { return DateTime.MinValue; }
        catch (UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    private static bool IsValidVersionedCompletionMarker(string markerPath, string id, string? requiredOperationId = null)
    {
        if (!IsValidCompletionMarker(markerPath, id, requiredOperationId)) return false;
        try
        {
            var info = new FileInfo(markerPath);
            if (!info.Exists || info.Length > 1024 || (info.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            string[] fields = File.ReadAllText(markerPath).Trim().Split('|');
            return fields.Length == 3 && string.Equals(fields[1], id, StringComparison.Ordinal)
                && Guid.TryParseExact(fields[2], "N", out _)
                && (requiredOperationId == null || string.Equals(fields[2], requiredOperationId, StringComparison.Ordinal));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    internal static bool HasCompletionMarker(string root, string id, string? name = null) =>
        FindCatalogFolders(root, id, name).Any(folder => IsValidCompletionMarker(Path.Combine(folder, DockerScripts.CompletionMarkerName), id));

    internal static bool HasFreshCompletionMarker(string root, string id, DateTime sinceUtc, string? name = null, string? operationId = null) =>
        FindCatalogFolders(root, id, name).Any(folder =>
        {
            string marker = Path.Combine(folder, DockerScripts.CompletionMarkerName);
            try { return IsValidCompletionMarker(marker, id, operationId) && File.GetLastWriteTimeUtc(marker) >= sinceUtc; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        });

    internal static bool IsValidCompletionMarker(string markerPath, string id, string? operationId = null)
    {
        try
        {
            if (!File.Exists(markerPath)) return false;
            string expected = "GameLibraryManager|" + id;
            string actual = File.ReadAllText(markerPath).Trim();
            if (operationId == null)
                return string.Equals(actual, expected, StringComparison.Ordinal)
                    || actual.StartsWith(expected + "|", StringComparison.Ordinal);
            return string.Equals(actual, expected + "|" + operationId, StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    internal static IReadOnlyList<string> FindGameExecutables(string folder, CancellationToken cancellation = default)
    {
        if (!Directory.Exists(folder)) return Array.Empty<string>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 6, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var candidates = new List<string>();
        foreach (var path in Directory.EnumerateFiles(folder, "*.exe", options))
        {
            cancellation.ThrowIfCancellationRequested();
            if (IsInstallerStagingPath(path)) continue;
            if (!IsGameExecutable(folder, path)) continue;
            candidates.Add(path);
            if (candidates.Count > 100) break;
        }
        return candidates;
    }

    internal static bool IsPlayableExecutable(string folder, string path)
    {
        try
        {
            if (!File.Exists(path) || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return false;
            string fullFolder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(fullFolder, StringComparison.OrdinalIgnoreCase)) return false;
            return IsGameExecutable(Path.GetFullPath(folder), fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static Dictionary<string, (string Id, string Name)[]> Lookups(IEnumerable<(string Id, string Name)> games) =>
        games.SelectMany(game => LookupKeys(game).Select(key => (Key: key, Game: game)))
            .Where(g => g.Key.Length > 0).GroupBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Game).DistinctBy(x => x.Id, StringComparer.Ordinal).ToArray());

    private static IEnumerable<string> LookupKeys((string Id, string Name) game)
    {
        if (DockerIdentity.IsQualified(game.Id))
        {
            yield return Normalize(DockerScripts.InstallFolder(game.Id));
            yield break;
        }
        foreach (var value in new[] { game.Id, game.Name, DockerScripts.InstallFolder(game.Id) })
        {
            string key = Normalize(value);
            if (key.Length == 0) continue;
            yield return key;
            // Some older folders omit an initial article (for example the
            // catalog's "thelegendoftianding" versus "legendoftianding").
            // Keeping both keys lets a unique catalog identity recover its
            // real game executable without treating the folder as local.
            if (key.StartsWith("the", StringComparison.Ordinal) && key.Length > 3) yield return key[3..];
        }
    }

    private static bool FolderIdentityMatches(string left, string right)
    {
        var leftKeys = LookupNameKeys(left).ToHashSet(StringComparer.Ordinal);
        return LookupNameKeys(right).Any(leftKeys.Contains);
    }

    private static IEnumerable<string> LookupNameKeys(string value)
    {
        string key = Normalize(value);
        if (key.Length == 0) yield break;
        yield return key;
        if (key.StartsWith("the", StringComparison.Ordinal) && key.Length > 3) yield return key[3..];
    }

    private static void AddCatalogFolderLookups(Dictionary<string, (string Id, string Name)[]> lookups, string folder, IEnumerable<(string Id, string Name)> games)
    {
        foreach (var game in games)
            AddCatalogFolderLookup(lookups, folder, game);
    }

    private static void AddCatalogFolderLookup(Dictionary<string, (string Id, string Name)[]> lookups, string folder, (string Id, string Name) game)
    {
        string label = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        bool qualifiedExact = DockerIdentity.IsQualified(game.Id) && (label.Equals(DockerScripts.InstallFolder(game.Id), StringComparison.OrdinalIgnoreCase)
            || DockerScripts.IsVersionedInstallFolder(game.Id, label));
        if (DockerIdentity.IsQualified(game.Id) && !qualifiedExact) return;
        bool idFolder = qualifiedExact || label.Equals(game.Id, StringComparison.OrdinalIgnoreCase)
            || label.StartsWith(game.Id + "-", StringComparison.OrdinalIgnoreCase)
            || FolderIdentityMatches(label, game.Id);
        bool friendlyFolder = FolderIdentityMatches(label, game.Name);
        if (!idFolder && !friendlyFolder) return;
        string key = Normalize(label);
        if (key.Length == 0) return;
        if (!lookups.TryGetValue(key, out var existing)) lookups[key] = new[] { game };
        else lookups[key] = existing.Append(game).DistinctBy(x => x.Id, StringComparer.Ordinal).ToArray();
    }

    private static readonly HashSet<string> SupportFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "support", "redist", "redistributables", "redistributable", "commonredist", "prerequisites", "directx", "vcredist", "dotnet", "installers", "installer", "crashreporter", "crashreportclient",
        "trainer", "wemod", "fling", "flingtrainer", "thirdpartylibs", "imageioffmpeg", "emulators", "modding"
    };

    private static string? ChooseCatalogLauncher(string folder, IReadOnlyList<string> candidates, IReadOnlyCollection<string> aliases)
    {
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        var ranked = candidates.Select(path =>
        {
            string stem = Normalize(Path.GetFileNameWithoutExtension(path));
            string relative = Path.GetRelativePath(folder, path).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
            string relativeLower = relative.ToLowerInvariant();
            int depth = relative.Count(c => c == '/') + 1;
            int score = aliases.Sum(alias => alias == stem
                ? 2600
                : alias.Length >= 6 && (stem.Contains(alias, StringComparison.Ordinal) || alias.Contains(stem, StringComparison.Ordinal)) ? 500
                : 0);

            // A root binary is usually the real executable for older/small
            // games, while an Unreal-style Binaries\Win64 shipping binary is
            // normally the playable payload behind a tiny root bootstrap.
            if (depth == 1) score += 900;
            if (relativeLower.Contains("/binaries/win64/", StringComparison.Ordinal)
                || relativeLower.Contains("/binaries/win32/", StringComparison.Ordinal)) score += 1800;
            if (stem.Contains("shipping", StringComparison.Ordinal)) score += 1200;
            else if (stem.Contains("client", StringComparison.Ordinal)) score += 500;

            try
            {
                long bytes = new FileInfo(path).Length;
                if (bytes >= 20 * 1024 * 1024) score += 600;
                else if (bytes >= 4 * 1024 * 1024) score += 400;
                else if (bytes < 512 * 1024) score -= 700;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            string stemLower = stem.ToLowerInvariant();
            if (stemLower.Contains("launcher", StringComparison.Ordinal)
                || stemLower.Contains("bootstrap", StringComparison.Ordinal)
                || stemLower.Contains("updater", StringComparison.Ordinal)
                || stemLower.Contains("installer", StringComparison.Ordinal)) score -= 2600;
            if (stemLower.StartsWith("start", StringComparison.Ordinal)
                || stemLower.StartsWith("setup", StringComparison.Ordinal)
                || stemLower.StartsWith("config", StringComparison.Ordinal)) score -= 900;
            score -= Math.Min(depth, 10) * 10;
            return new { Path = path, Score = score, Depth = depth };
        }).OrderByDescending(c => c.Score).ThenBy(c => c.Depth).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToArray();

        var best = ranked[0];
        var second = ranked[1];
        // Do not guess when the catalog identity and install layout provide no
        // useful signal. A Details-level manual choice remains available.
        if (best.Score <= 0 || best.Score - second.Score < 500) return null;
        return best.Path;
    }

    private static bool IsGameExecutable(string folder, string path)
    {
        var parts = Path.GetRelativePath(folder, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Take(parts.Length - 1).Any(p => SupportFolders.Contains(Normalize(p)))) return false;
        string name = Normalize(Path.GetFileNameWithoutExtension(path));
        if (name is "epicwebhelper" or "fitgirllauncher") return false;
        if (name.Contains("editor", StringComparison.OrdinalIgnoreCase)
            || name.Contains("toolkit", StringComparison.OrdinalIgnoreCase)
            || name.Contains("packager", StringComparison.OrdinalIgnoreCase)) return false;
        return !System.Text.RegularExpressions.Regex.IsMatch(name,
            @"^(unins|uninstall|setup|install|launcher|gamebootstrapper|eaclauncher|redist|crashreport|crashhandler|crashpad|reporter|helper|quicksfv|dxsetup|vcredist|ue4prereq|ueprereq|dotnet|unitycrashhandler|qtwebengineprocess|yuzu|ryujinx|citron|sudachi|eden|yuzucmd|edencli|edenroom|enbhost|skse|squirrel|inklecate|ffmpeg|editor|toolkit|packager|languageselector|workshop|unrealcefsubprocess)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static void Inspect(string folder, Dictionary<string, (string Id, string Name)[]> lookups, bool includeLocal, InstalledScanResult result, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        try
        {
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return;
            string label = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
            string name = Normalize(label);
            if (SupportFolders.Contains(name)) return;
            lookups.TryGetValue(name, out var ids);
            if (ids != null && ids.Any(identity => DockerScripts.IsVersionedInstallFolder(identity.Id, label))
                && !ids.Any(identity => DockerScripts.IsVersionedInstallFolder(identity.Id, label)
                    && IsValidVersionedCompletionMarker(Path.Combine(folder, DockerScripts.CompletionMarkerName), identity.Id)))
            {
                result.Notices.Add(label + ": digest-scoped installation has no valid exact operation receipt; it was not marked installed.");
                return;
            }
            // A saved custom launcher can live in a catalog-ID folder even
            // when automatic executable selection deliberately excludes it.
            foreach (var identity in ids ?? Array.Empty<(string Id, string Name)>())
                if (label.Equals(identity.Id, StringComparison.OrdinalIgnoreCase) ||
                    (DockerIdentity.Valid(identity.Id) && (label.Equals(DockerScripts.InstallFolder(identity.Id), StringComparison.OrdinalIgnoreCase)
                        || DockerScripts.IsVersionedInstallFolder(identity.Id, label))))
                    result.ExplicitCatalogFolders.Add((identity.Id, folder));
            // Filesystem location is independent of catalog-title ambiguity.
            // Retain it for already chosen launchers without assigning a game ID.
            var candidates = FindGameExecutables(folder, cancellation).ToList();
            foreach (string executable in candidates) result.ExecutableFolders.Add((executable, folder));
            if (ids is { Length: > 1 })
            {
                // Windows spelling alone cannot identify case-distinct Docker catalog entries.
                result.Notices.Add(label + ": matches multiple catalog identities. Choose the executable from the intended game's Details.");
                return;
            }
            bool local = ids == null;
            if (local && !includeLocal) return;
            if (!local)
                foreach (var catalogGame in ids!) result.CatalogFoldersPresent.Add(catalogGame.Id);
            if (candidates.Count == 0)
            {
                if (!local) result.Notices.Add(label + ": no Windows game executable was found; support utilities were excluded.");
                return;
            }
            var names = (local ? new[] { name } : new[] { name, Normalize(ids![0].Id), Normalize(ids[0].Name) })
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var exact = candidates.Where(p => names.Contains(Normalize(Path.GetFileNameWithoutExtension(p)), StringComparer.Ordinal)).ToArray();
            // Local folders have no catalog identity to guide a safe choice, so
            // retain the explicit-choice behavior. Catalog installs do have a
            // stable title/id and common build layouts, which lets us choose a
            // real game binary over a tiny bootstrap or a duplicated payload.
            string? chosen = local
                ? candidates.Count <= 100 && exact.Length == 1 ? exact[0] : candidates.Count == 1 ? candidates[0] : null
                : ChooseCatalogLauncher(folder, candidates, names);
            string id = local ? LocalGame.Identity(folder) : ids![0].Id;
            if (!local) result.CatalogGamesWithExecutable.Add(id);
            if (result.AmbiguousIdentities.Contains(id)) return;
            if (result.Games.Any(g => g.Id == id))
            {
                result.Games.RemoveAll(g => g.Id == id);
                result.AmbiguousIdentities.Add(id);
                result.Notices.Add(label + ": multiple installation folders match this catalog game. Existing launcher choices were preserved.");
                return;
            }
            result.Games.Add(new(id, local ? label : ids![0].Name, folder, chosen, local));
            if (chosen == null) result.Notices.Add(label + ": multiple game executables were found. Choose the launcher in Details.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Notices.Add(Path.GetFileName(folder) + ": could not scan this folder: " + ex.Message);
        }
    }
}
