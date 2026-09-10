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
        dialog.Action("Watch trailer on YouTube", () => Process.Start(new ProcessStartInfo(TrailerUrl(game)) { UseShellExecute = true }), "WatchTrailer");
        var rate = dialog.Choice("Your rating", new[] { "Unrated", "★", "★★", "★★★", "★★★★", "★★★★★" }, game.Rating);
        var tags = dialog.Text("Tags (comma separated)", string.Join(", ", State.GameTags.GetValueOrDefault(game.Id, new())), "GameTags");
        var installed = dialog.Check("Mark as installed", State.InstalledGames.Contains(game.Id));
        var categoryChoices = Store.LoadCategories(Sync.Effective(State)).Where(c => c.Id is not ("all" or "wishlist" or "installed")).ToList();
        if (categoryChoices.All(c => c.Id != "new")) categoryChoices.Insert(0, new Category("new", "New arrivals"));
        var categories = categoryChoices.ToArray();
        int categoryIndex = Math.Max(0, Array.FindIndex(categories, c => c.Id == game.Category));
        var category = dialog.Choice("Move to tab", categories, categoryIndex, "GameCategory");
        category.IsEnabled = IsAdmin || game.IsLocal;
        if (!game.IsLocal && !IsAdmin) category.ToolTip = "Admin sign-in is required to move shared catalog games.";
        dialog.Action("Save personal changes", () =>
        {
            State.Ratings[game.Id] = rate.SelectedIndex;
            var values = tags.Text.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList();
            if (values.Any(t => t.Length > 80)) throw new ArgumentException("Keep each tag under 80 characters.");
            State.GameTags[game.Id] = values;
            if (installed.IsChecked == true) State.InstalledGames.Add(game.Id); else State.InstalledGames.Remove(game.Id);
            if (category.SelectedItem is Category target && target.Id != game.Category)
            {
                if (game.IsLocal) State.LocalGames[game.Id].Category = target.Id;
                else if (!RequireAdmin()) throw new InvalidOperationException("Sign in as admin to move shared catalog games between tabs.");
                else Sync.Queue(State, "gameCategories", game.Id, JsonValue.Create(target.Id));
            }
            Save(); Reload(); dialog.Notice.Text = "Saved on this PC.";
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
        dialog.ActionAsync("Play", async () => { await PlayGame(game); if (!dialog.IsClosed && !closing) dialog.Close(); }, "PlayGame");
        dialog.Action("Open Wand", OpenWand, "OpenWand");
        dialog.ActionAsync("Play with Wand mods", async () => { await PlayWithWand(game, dialog.ClosedToken); if (!dialog.IsClosed && !closing) dialog.Close(); }, "PlayWithWand");
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
                TrackPlayProcess(game, running, ownsProcess: false);
                ActivateProcess(running);
                StatusText.Text = game.Name + " is already running.";
                return;
            }
            StartPlaySession(game, () => Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = true }) ?? throw new InvalidOperationException("The game process could not be started."));
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
        await playLaunchGate.WaitAsync(cancellation);
        try
        {
            if (closing || cancellation.IsCancellationRequested) return;
            if (!TryGetLauncher(game, out var exe)) throw new FileNotFoundException("No game executable was found. Scan the installation folder or choose the game's executable.");
            if (!Path.GetExtension(exe).Equals(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The launcher must be a Windows executable.");
            var wandPath = ResolveWandPath();
            if (TryActivateExistingPlay(game))
            {
                if (activePlays.TryGetValue(game.Id, out var active) && !active.UsesWand)
                    StatusText.Text = game.Name + " is already running without Wand mods. Close it before using Play with Wand.";
                return;
            }
            var result = await WandIntegration.LaunchAsync(game, exe, wandPath, Store, cancellation);
            if (closing || cancellation.IsCancellationRequested)
            {
                if (result.Process != null)
                {
                    if (result.OwnsProcess) StopUntrackedProcess(result.Process, Store);
                    else { try { result.Process.Dispose(); } catch { } }
                }
                return;
            }
            if (result.Process != null) TrackPlayProcess(game, result.Process, usesWand: true, ownsProcess: result.OwnsProcess);
            StatusText.Text = result.Message;
            Store.Log("Wand launch for " + game.Id + "; protocol=" + result.UsedProtocol.ToString().ToLowerInvariant() + "; started=" + (result.Process != null).ToString().ToLowerInvariant());
        }
        finally { playLaunchGate.Release(); }
    }

    private static void StopUntrackedProcess(Process process, LibraryStore store)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            try { store.Log("Could not stop the untracked Wand process after shutdown: " + ex.Message); } catch { }
        }
        finally { try { process.Dispose(); } catch { } }
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
        if (!RequireAdmin()) return;
        var dialog = new EditorWindow(this, "Manage categories", "These changes are shared with the website. Offline edits stay queued until you reconnect and sign in.");
        var categories = Store.LoadCategories(Sync.Effective(State)).Where(c => c.Id is not ("all" or "wishlist" or "installed")).ToArray();
        var selected = dialog.Choice("Category", categories);
        var name = dialog.Text("New name / renamed category", "", "CategoryName");
        var hidden = dialog.Check("Hide selected category from regular users", false);
        selected.SelectionChanged += (_, _) =>
        {
            if (selected.SelectedItem is Category c) { name.Text = c.Name; hidden.IsChecked = Hidden(Sync.Effective(State)).Contains(c.Id); }
        };
        if (selected.SelectedItem is Category initial) { name.Text = initial.Name; hidden.IsChecked = Hidden(Sync.Effective(State)).Contains(initial.Id); }
        dialog.Action("Create category", () =>
        {
            string label = name.Text.Trim(); if (label.Length == 0 || label.Length > 80) throw new ArgumentException("Enter a category name of 1–80 characters.");
            string id = System.Text.RegularExpressions.Regex.Replace(label.ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');
            if (id.Length == 0) id = "category_" + Guid.NewGuid().ToString("N")[..8];
            var tabs = Store.LoadCategories(Sync.Effective(State));
            if (tabs.Any(c => c.Id == id) || id is "wishlist" or "installed") throw new ArgumentException("A category with that identity already exists.");
            tabs.Add(new(id, label)); QueueTabs(tabs); dialog.Close(); Reload(); ObserveUiOperation("Category refresh", () => Refresh(false));
        });
        dialog.Action("Save name and visibility", () =>
        {
            if (selected.SelectedItem is not Category c) return;
            if (name.Text.Trim().Length is < 1 or > 80) throw new ArgumentException("Enter a name of 1–80 characters.");
            var tabs = Store.LoadCategories(Sync.Effective(State)).Select(t => t.Id == c.Id ? new Category(c.Id, name.Text.Trim()) : t).ToList(); QueueTabs(tabs);
            var values = Hidden(Sync.Effective(State)); if (hidden.IsChecked == true) values.Add(c.Id); else values.Remove(c.Id);
            Sync.Queue(State, "hiddenTabs", "", new JsonArray(values.OrderBy(v => v).Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()));
            dialog.Close(); Reload(); ObserveUiOperation("Category refresh", () => Refresh(false));
        });
        void ReorderCategory(int delta)
        {
            if (selected.SelectedItem is not Category c) return;
            var tabs = Store.LoadCategories(Sync.Effective(State));
            if (!MoveCategory(tabs, c.Id, delta))
            {
                dialog.Notice.Text = delta < 0 ? "That category is already first." : "That category is already last.";
                return;
            }
            QueueTabs(tabs); dialog.Close(); Reload(); ObserveUiOperation("Category refresh", () => Refresh(false));
        }
        dialog.Action("Move category up", () => ReorderCategory(-1));
        dialog.Action("Move category down", () => ReorderCategory(1));
        dialog.Action("Remove category (move its games to New)", () =>
        {
            if (selected.SelectedItem is not Category c || c.Id == "new") throw new ArgumentException("The New category cannot be removed.");
            if (System.Windows.MessageBox.Show(dialog, "Remove '" + c.Name + "' and move its games to New?", "Remove category", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            foreach (var game in Games.Where(g => g.Category == c.Id))
                if (game.IsLocal) State.LocalGames[game.Id].Category = "new";
                else Sync.Queue(State, "gameCategories", game.Id, JsonValue.Create("new"));
            QueueTabs(Store.LoadCategories(Sync.Effective(State)).Where(t => t.Id != c.Id).ToList());
            var values = Hidden(Sync.Effective(State)); values.Remove(c.Id);
            Sync.Queue(State, "hiddenTabs", "", new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()));
            dialog.Close(); Reload(); ObserveUiOperation("Category refresh", () => Refresh(false));
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
    private void QueueTabs(List<Category> tabs) => Sync.Queue(State, "tabs", "", JsonNode.Parse(DataJson.Write(tabs)));
    private void ShowSync(object sender, RoutedEventArgs e)
    {
        var dialog = new EditorWindow(this, "Synchronization", Sync.Status);
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
    public static Dictionary<string, string> Scan(string root, IEnumerable<(string Id, string Name)> games, CancellationToken cancellation)
    {
        // Compatibility for callers that need only an unambiguous catalog-to-launcher map.
        return Discover(root, games, cancellation).Games.Where(g => !g.IsLocal && g.Launcher != null)
            .ToDictionary(g => g.Id, g => g.Launcher!, StringComparer.Ordinal);
    }

    public static InstalledScanResult Discover(string root, IEnumerable<(string Id, string Name)> games, CancellationToken cancellation)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The selected game folder is unavailable.");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Choose the actual game folder rather than a folder link.");
        var catalog = games.Where(g => !g.Id.StartsWith("local:", StringComparison.Ordinal)).ToArray();
        var lookups = Lookups(catalog);
        var result = new InstalledScanResult();
        var folders = Directory.EnumerateDirectories(root, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToArray();
        AddCatalogFolderLookups(lookups, root, catalog);
        foreach (var folder in folders) AddCatalogFolderLookups(lookups, folder, catalog);
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
            foreach (var folder in folders) Inspect(folder, lookups, true, result, cancellation);
            if (result.Games.Count == 0 && rootExecutables.Length > 0) Inspect(root, lookups, true, result, cancellation);
            else if (rootExecutables.Length > 0) result.Notices.Add(Path.GetFileName(root) + ": game subfolders were scanned; executables beside those folders were not assumed to be another game.");
        }
        return result;
    }

    public static InstalledScanResult ScanDownloads(string root, IEnumerable<(string Id, string Name)> games, CancellationToken cancellation)
    {
        var catalog = games.Where(g => DockerScripts.ValidTag(g.Id)).ToArray();
        var lookups = Lookups(catalog);
        var result = new InstalledScanResult();
        foreach (var game in catalog)
        {
            cancellation.ThrowIfCancellationRequested();
            var folders = FindCatalogFolders(root, game.Id, game.Name);
            if (folders.Count == 0)
            {
                result.Notices.Add(game.Name + ": the downloaded game folder was not found.");
                continue;
            }
            foreach (var folder in folders)
            {
                AddCatalogFolderLookup(lookups, folder, game);
                Inspect(folder, lookups, false, result, cancellation);
            }
        }
        return result;
    }

    /// <summary>
    /// Returns the current install folder plus safe legacy folders whose name is
    /// the catalog id followed by a separator/hash. Older builds used a different
    /// suffix formula, so exact-path lookup alone loses otherwise valid installs.
    /// </summary>
    internal static IReadOnlyList<string> FindCatalogFolders(string root, string id, string? name = null)
    {
        if (!Directory.Exists(root) || !DockerScripts.ValidTag(id)) return Array.Empty<string>();
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
        Add(Path.Combine(root, id));
        if (!string.IsNullOrWhiteSpace(name)) Add(Path.Combine(root, name));
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(root, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                string folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
                bool friendlyName = !string.IsNullOrWhiteSpace(name) && string.Equals(Normalize(folderName), Normalize(name), StringComparison.Ordinal);
                if (folderName.StartsWith(id + "-", StringComparison.OrdinalIgnoreCase) || friendlyName) Add(folder);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return folders.OrderBy(path => path.Equals(expected, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
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
            if (!IsGameExecutable(folder, path)) continue;
            candidates.Add(path);
            if (candidates.Count > 100) break;
        }
        return candidates;
    }

    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static Dictionary<string, (string Id, string Name)[]> Lookups(IEnumerable<(string Id, string Name)> games) =>
        games.SelectMany(g => new[] { (Key: Normalize(g.Id), Game: g), (Key: Normalize(g.Name), Game: g), (Key: Normalize(DockerScripts.InstallFolder(g.Id)), Game: g) })
            .Where(g => g.Key.Length > 0).GroupBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Game).DistinctBy(x => x.Id, StringComparer.Ordinal).ToArray());

    private static void AddCatalogFolderLookups(Dictionary<string, (string Id, string Name)[]> lookups, string folder, IEnumerable<(string Id, string Name)> games)
    {
        foreach (var game in games)
            AddCatalogFolderLookup(lookups, folder, game);
    }

    private static void AddCatalogFolderLookup(Dictionary<string, (string Id, string Name)[]> lookups, string folder, (string Id, string Name) game)
    {
        string label = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        bool idFolder = label.Equals(game.Id, StringComparison.OrdinalIgnoreCase) || label.StartsWith(game.Id + "-", StringComparison.OrdinalIgnoreCase);
        bool friendlyFolder = string.Equals(Normalize(label), Normalize(game.Name), StringComparison.Ordinal);
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
    private static bool IsGameExecutable(string folder, string path)
    {
        var parts = Path.GetRelativePath(folder, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Take(parts.Length - 1).Any(p => SupportFolders.Contains(Normalize(p)))) return false;
        string name = Normalize(Path.GetFileNameWithoutExtension(path));
        return !System.Text.RegularExpressions.Regex.IsMatch(name,
            @"^(unins|uninstall|setup|install|redist|crashreport|crashhandler|crashpad|reporter|helper|quicksfv|dxsetup|vcredist|ue4prereq|ueprereq|dotnet|unitycrashhandler|qtwebengineprocess|yuzucmd|edencli|edenroom|enbhost|skse|squirrel|inklecate|ffmpeg|editor|toolkit|packager)",
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
            var candidates = FindGameExecutables(folder, cancellation).ToList();
            if (candidates.Count == 0)
            {
                if (!local) result.Notices.Add(label + ": no Windows game executable was found; support utilities were excluded.");
                return;
            }
            var names = local ? new[] { name } : new[] { name, Normalize(ids![0].Id), Normalize(ids[0].Name) };
            var exact = candidates.Where(p => names.Contains(Normalize(Path.GetFileNameWithoutExtension(p)), StringComparer.Ordinal)).ToArray();
            string? chosen = candidates.Count <= 100 && exact.Length == 1 ? exact[0] : candidates.Count == 1 ? candidates[0] : null;
            string id = local ? LocalGame.Identity(folder) : ids![0].Id;
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
