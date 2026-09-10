using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace GameLibrary.Native;

public sealed class CoverConverter : IValueConverter
{
    private static readonly Dictionary<string, BitmapImage?> cache = new(StringComparer.Ordinal);
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var path = value as string;
        if (string.IsNullOrEmpty(path)) return null;
        if (cache.TryGetValue(path, out var bitmap)) return bitmap;
        try
        {
            bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.DecodePixelHeight = 180;
            bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(path); bitmap.EndInit(); bitmap.Freeze();
        }
        catch { bitmap = null; }
        if (cache.Count > 2500) cache.Clear();
        cache[path] = bitmap;
        return bitmap;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public partial class MainWindow : Window
{
    private static class NativeWindow
    {
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr handle, int command);
        public static void Activate(IntPtr handle) { ShowWindow(handle, 9); SetForegroundWindow(handle); }
    }
    internal readonly LibraryStore Store;
    internal UserState State = new();
    internal readonly SyncClient Sync;
    internal List<Game> Games = new();
    private List<Game> filtered = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer playtime = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer searchDelay = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private DateTime lastCatalogRefresh;
    private Forms.NotifyIcon? tray;
    private bool ready, refreshing, closing, searchPending;
    private bool changingSelection, catalogStatsDirty = true;
    private readonly bool offline;
    private readonly bool secondMonitor;
    private readonly OfflineNetworkGuard? offlineNetwork;
    private bool startupPlacementQueued;
    private bool startupPlacementDone;
    private DateTime startupPlacementGraceUntilUtc;
    private string tab = "all";
    private string? adminToken;
    private readonly List<JobWindow> jobs = new();
    private readonly Dictionary<string, PlaySession> activePlays = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> installGates = new(StringComparer.Ordinal);
    // Direct Play and Play with Wand share one launch gate. This prevents a
    // direct launch from appearing between Wand's PID snapshot and its URI
    // handoff, where it could otherwise be mistaken for a Wand-owned process.
    private readonly SemaphoreSlim playLaunchGate = new(1, 1);
    internal bool IsAdmin => adminToken != null;
    internal bool IsClosing => closing;
    private static readonly HashSet<string> protectedTabs = new(StringComparer.Ordinal) { "not_for_me", "finished", "mybackup", "oporationsystems", "music", "win11maintaince", "3th_party_tools", "gamedownloaders" };

    public MainWindow(LibraryStore store, bool offline = false, bool secondMonitor = true)
    {
        Store = store; this.offline = offline; this.secondMonitor = secondMonitor;
        offlineNetwork = offline ? new OfflineNetworkGuard() : null;
        Sync = new SyncClient(store, offlineNetwork);
        Program.SetCurrentProcessExplicitAppUserModelID("GameLibraryManager.Native");
        InitializeComponent();
        if (secondMonitor) PlaceOnSecondaryMonitor();
        Style = (Style)System.Windows.Application.Current.FindResource(typeof(Window));
        Loaded += OnLoaded;
        ContentRendered += (_, _) => QueueSecondaryPlacement();
        Closing += OnClosing;
        StateChanged += (_, _) => ObserveUiAction("Window state change", () =>
        {
            if (!ready || WindowState != WindowState.Minimized || !State.Settings.MinimizeToTray) return;
            if (!startupPlacementDone || DateTime.UtcNow < startupPlacementGraceUntilUtc) { QueueSecondaryPlacement(force: true); return; }
            HideToTray();
        });
        PreviewKeyDown += Keyboard;
        poll.Tick += (_, _) => ObserveUiOperation("Catalog poll", () => Refresh(DateTime.UtcNow - lastCatalogRefresh >= TimeSpan.FromSeconds(15)));
        playtime.Tick += (_, _) => ObserveUiAction("Play-time update", UpdatePlaySessions);
        searchDelay.Tick += (_, _) => ObserveUiAction("Search filter", () => { searchDelay.Stop(); ApplyFilter(); });
        GameList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => ScheduleMetadata()));
    }
    private void OnLoaded(object sender, RoutedEventArgs e) => ObserveUiOperation("Startup", InitializeAsync);
    private async Task InitializeAsync()
    {
        try
        {
            StatusText.Text = "Preparing the bundled catalog…";
            await Task.Run(Store.EnsureAssets);
            lifetime.Token.ThrowIfCancellationRequested();
            Sync.ReloadCache();
            State = Store.LoadState();
            lifetime.Token.ThrowIfCancellationRequested();
            Width = Math.Clamp(State.Settings.WindowWidth, MinWidth, SystemParameters.WorkArea.Width);
            Height = Math.Clamp(State.Settings.WindowHeight, MinHeight, SystemParameters.WorkArea.Height);
            if (secondMonitor) PlaceOnSecondaryMonitor();
            tab = State.Settings.LastTab;
            SortBox.ItemsSource = new[] { "Name A–Z", "Name Z–A", "Time to Beat (Low–High)", "Time to Beat (High–Low)", "Recently Added", "Oldest First", "Rating (High–Low)", "Rating (Low–High)", "Size (Small–Large)", "Size (Large–Small)", "Category" };
            SortBox.SelectedItem = State.Settings.SortBy;
            if (SortBox.SelectedIndex < 0) SortBox.SelectedIndex = 4;
            RatingBox.ItemsSource = new[] { "Any rating", "1+ stars", "2+ stars", "3+ stars", "4+ stars", "5 stars" }; RatingBox.SelectedIndex = 0;
            lifetime.Token.ThrowIfCancellationRequested();
            InitializeTray(); ApplyTheme(); ready = true; Reload();
            QueueSecondaryPlacement(force: true);
            // A fast automation client or a user can type while the bundled
            // catalog is still loading. Reapply that text after readiness so
            // the first search is never dropped by the ready guard.
            if (searchPending) { searchPending = false; ApplyFilter(); }
            playtime.Start();
            StatusText.Text = Store.RecoveryNotice ?? $"Offline catalog ready · {Games.Count:N0} exact game identities";
            Store.Log("Window ready; catalog=" + Games.Count);
            if (Program.TestReport != null) { if (!offline) await Refresh(true); await RunUiProof(Program.TestReport); return; }
            if (!offline) { poll.Start(); await Refresh(true); }
            QueueSecondaryPlacement(force: true);
        }
        catch (OperationCanceledException) when (closing || lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Error(ex); }
    }
    private bool PlaceOnSecondaryMonitor()
    {
        var screen = Forms.Screen.AllScreens.Where(candidate => !candidate.Primary).OrderBy(candidate => candidate.Bounds.Left).FirstOrDefault();
        if (screen == null) return false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var area = screen.WorkingArea;
        var width = Math.Min(Width, (double)area.Width);
        var height = Math.Min(Height, (double)area.Height);
        Left = area.Left + Math.Max(0, (area.Width - width) / 2);
        Top = area.Top + Math.Max(0, (area.Height - height) / 2);
        return true;
    }
    private void QueueSecondaryPlacement(bool force = false)
    {
        if (!secondMonitor || closing || (!force && startupPlacementQueued) || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        startupPlacementQueued = true;
        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                startupPlacementQueued = false;
                if (closing) return;
                WindowStartupLocation = WindowStartupLocation.Manual;
                WindowState = WindowState.Normal;
                var placed = PlaceOnSecondaryMonitor();
                if (!IsVisible) Show();
                if (ready && placed)
                {
                    startupPlacementDone = true;
                    startupPlacementGraceUntilUtc = DateTime.UtcNow.AddSeconds(20);
                }
            }));
        }
        catch (InvalidOperationException) { startupPlacementQueued = false; }
    }
    private void InitializeTray()
    {
        var iconStream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/GameLibrary.ico"))!.Stream;
        tray = new Forms.NotifyIcon { Icon = new System.Drawing.Icon(iconStream), Text = "Game Library", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Game Library", null, (_, _) => PostUiAction("Tray restore", RestoreWindow));
        menu.Items.Add("Refresh catalog", null, (_, _) => PostUiOperation("Tray catalog refresh", () => Refresh(true)));
        menu.Items.Add("Open download folder", null, (_, _) => PostUiAction("Tray open download folder", () => OpenFolder(State.Settings.MountPath)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => PostUiAction("Tray exit", Close));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => PostUiAction("Tray restore", RestoreWindow);
    }
    internal void HideToTray() { Hide(); Store.Log("Window hidden to tray"); }
    public void RestoreWindow() { if (closing) return; Show(); WindowState = WindowState.Normal; Activate(); Store.Log("Window restored"); }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        if (jobs.Any(j => j.Busy))
        {
            System.Windows.MessageBox.Show(this, "A download is still running. Stop it in its progress window before exiting.", "Download in progress", MessageBoxButton.OK, MessageBoxImage.Information);
            e.Cancel = true; return;
        }
        closing = true;
        try
        {
            try { lifetime.Cancel(); }
            catch (Exception ex) { try { Store.Log("Shutdown cancellation callbacks failed; continuing cleanup: " + ex); } catch { } }
            if (ready)
            {
                State.Settings.WindowWidth = RestoreBounds.Width; State.Settings.WindowHeight = RestoreBounds.Height;
                FlushPlaySessions();
                Save();
            }
        }
        catch (Exception ex) { Store.Log("Shutdown state flush failed: " + ex); }
        poll.Stop(); searchDelay.Stop(); playtime.Stop();
        try { tray?.Dispose(); } catch (Exception ex) { Store.Log("Tray cleanup failed: " + ex.Message); } finally { tray = null; }
        foreach (var job in jobs.ToArray())
        {
            try { job.Close(); }
            catch (Exception ex) { try { Store.Log("Download window cleanup failed: " + ex.Message); } catch { } }
        }
        try { Store.Log("Clean shutdown"); } catch { }
    }
    internal void Reload()
    {
        catalogStatsDirty = true;
        var selected = Games.Where(g => g.Selected).Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
        var config = Sync.Effective(State);
        Games = Store.LoadGames(State, config);
        foreach (var game in Games) game.Selected = selected.Contains(game.Id);
        var categories = Store.LoadCategories(config);
        var hidden = Hidden(config);
        var choices = new List<Category> { new("all", "◈  All games"), new("wishlist", "♡  Wishlist"), new("installed", "▣  Installed") };
        choices.AddRange(categories.Where(c => c.Id is not ("all" or "wishlist" or "installed") && (IsAdmin || (!protectedTabs.Contains(c.Id) && !hidden.Contains(c.Id)))));
        if (Games.Any(g => g.Category == "new") && choices.All(c => c.Id != "new")) choices.Insert(3, new("new", "New arrivals"));
        if (!choices.Any(c => c.Id == tab)) tab = "all";
        CategoryList.ItemsSource = choices;
        CategoryList.SelectedItem = choices.First(c => c.Id == tab);
        var tag = TagBox.SelectedItem as string;
        TagBox.ItemsSource = new[] { "All tags" }.Concat(State.GameTags.Values.SelectMany(t => t).Distinct().OrderBy(t => t)).ToArray();
        TagBox.SelectedItem = tag ?? "All tags"; if (TagBox.SelectedIndex < 0) TagBox.SelectedIndex = 0;
        ApplyFilter();
    }
    internal Task ImportBackupAsync(JsonObject document, CancellationToken cancellation) => Sync.ImportStateAsync(
        () => State,
        current =>
        {
            if (document["schemaVersion"] != null) return DataJson.Read<UserState>(document.ToJsonString());
            if (document["settings"] is not JsonObject fields) throw new FormatException("This document has no settings.");
            var imported = DataJson.Read<UserState>(DataJson.Write(current));
            var settings = JsonNode.Parse(DataJson.Write(imported.Settings))!.AsObject();
            foreach (var field in fields) settings[field.Key] = field.Value?.DeepClone();
            imported.Settings = DataJson.Read<Preferences>(settings.ToJsonString());
            return imported;
        }, RestoreImportedState, cancellation);
    internal void RestoreImportedState(UserState imported)
    {
        // Prevent selection-change handlers from saving stale pre-import controls over the backup.
        ready = false;
        try
        {
            State = imported; tab = State.Settings.LastTab;
            SortBox.SelectedItem = State.Settings.SortBy;
            if (SortBox.SelectedIndex < 0) SortBox.SelectedIndex = 4;
        }
        finally { ready = true; }
        ApplyTheme(); Reload(); Save();
    }
    private static HashSet<string> Hidden(JsonObject config) => config["hiddenTabs"] is JsonArray a ? a.Select(n => DataJson.Text(n)).ToHashSet(StringComparer.Ordinal) : new();
    internal void ApplyFilter()
    {
        if (!ready) return;
        var hidden = Hidden(Sync.Effective(State));
        IEnumerable<Game> visible = Games;
        if (!IsAdmin) visible = visible.Where(g => !protectedTabs.Contains(g.Category) && !hidden.Contains(g.Category));
        string query = SearchBox.Text.Trim();
        if (tab == "installed") visible = visible.Where(g => g.Installed);
        else if (query.Length == 0)
        {
            if (tab == "wishlist") visible = visible.Where(g => g.Wishlisted);
            else if (tab != "all") visible = visible.Where(g => g.Category == tab);
        }
        if (query.Length > 0) visible = visible.Where(g => MatchesSearch(g, query));
        if (InstalledOnlyFilter.IsChecked == true) visible = visible.Where(g => g.Installed);
        else if (WithoutInstalledFilter.IsChecked == true) visible = visible.Where(g => !g.Installed);
        int rating = Math.Max(0, RatingBox.SelectedIndex); visible = visible.Where(g => g.Rating >= rating);
        string tag = TagBox.SelectedItem as string ?? "All tags";
        if (tag != "All tags") visible = visible.Where(g => State.GameTags.GetValueOrDefault(g.Id, new()).Contains(tag));
        visible = SortGames(visible, SortBox.SelectedItem as string ?? "Recently Added");
        filtered = visible.ToList();
        GameList.ItemsSource = filtered;
        EmptyState.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = query.Length > 0 ? (tab == "installed" || InstalledOnlyFilter.IsChecked == true ? "Search installed games" : "Search all games")
            : (CategoryList.SelectedItem as Category)?.Name.Replace("◈  ", "").Replace("♡  ", "").Replace("▣  ", "") ?? "All games";
        CatalogCaption.Text = $"{Games.Count:N0} games in your catalog · Search, organize, and play";
        UpdateStats();
        ScheduleMetadata();
    }
    internal static IEnumerable<Game> SortGames(IEnumerable<Game> source, string sort)
    {
        static IOrderedEnumerable<Game> NameAsc(IEnumerable<Game> games) => games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.Id, StringComparer.Ordinal);
        static bool HasTime(Game g) => double.IsFinite(g.Time) && g.Time > 0;
        static bool HasSize(Game g) => double.IsFinite(g.SizeGb) && g.SizeGb > 0;
        static bool HasRating(Game g) => g.Rating > 0;
        static bool HasDate(Game g) => g.Added != default || g.Category == "new";
        static DateTime DateValue(Game g) => g.Added != default ? g.Added : DateTime.UtcNow;
        return sort switch
        {
            "Name A–Z" => NameAsc(source),
            "Name Z–A" => source.OrderByDescending(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.Id, StringComparer.Ordinal),
            "Time to Beat (Low–High)" => source.OrderBy(g => HasTime(g) ? 0 : 1).ThenBy(g => HasTime(g) ? g.Time : double.MaxValue).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            "Time to Beat (High–Low)" => source.OrderBy(g => HasTime(g) ? 0 : 1).ThenByDescending(g => HasTime(g) ? g.Time : double.MinValue).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            "Oldest First" => source.OrderBy(g => HasDate(g) ? 0 : 1).ThenBy(g => HasDate(g) ? DateValue(g) : DateTime.MaxValue).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            "Rating (High–Low)" => source.OrderBy(g => HasRating(g) ? 0 : 1).ThenByDescending(g => HasRating(g) ? g.Rating : int.MinValue).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            "Rating (Low–High)" => source.OrderBy(g => HasRating(g) ? 0 : 1).ThenBy(g => HasRating(g) ? g.Rating : int.MaxValue).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            "Size (Small–Large)" => source.OrderBy(g => HasSize(g) ? 0 : 1).ThenBy(g => HasSize(g) ? g.SizeGb : double.MaxValue).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            "Size (Large–Small)" => source.OrderBy(g => HasSize(g) ? 0 : 1).ThenByDescending(g => HasSize(g) ? g.SizeGb : double.MinValue).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            "Category" => source.OrderBy(g => g.CategoryName, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => source.OrderBy(g => HasDate(g) ? 0 : 1).ThenByDescending(g => HasDate(g) ? DateValue(g) : DateTime.MinValue).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
        };
    }
    private bool MatchesSearch(Game game, string query)
    {
        bool Contains(string? value) => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
        return Contains(game.Name) || Contains(game.Id) || Contains(game.Category) || Contains(game.CategoryName) ||
            Contains(game.DockerImage) || Contains(game.DockerImageUrl) ||
            (State.LaunchPaths.TryGetValue(game.Id, out var launcher) && Contains(launcher)) ||
            (State.LocalGames.TryGetValue(game.Id, out var local) && Contains(local.Folder)) ||
            (game.Installed && !game.IsLocal && Contains(Path.Combine(State.Settings.MountPath, DockerScripts.InstallFolder(game.Id))));
    }
    private void UpdateStats()
    {
        if (!ready) return;
        VisibleCount.Text = filtered.Count.ToString("N0");
        if (catalogStatsDirty)
        {
        WishlistCount.Text = Games.Count(g => g.Wishlisted).ToString("N0"); InstalledCount.Text = Games.Count(g => g.Installed).ToString("N0");
        int installed = Games.Count(g => g.Installed);
        InstalledPercent.Text = $"({(Games.Count == 0 ? 0 : Math.Round(100.0 * installed / Games.Count, MidpointRounding.AwayFromZero)):0}%)";
        InstalledPercent.ToolTip = "Percentage of the complete catalog marked installed on this PC. Search and filters do not change this statistic.";
        var times = Games.Where(g => double.IsFinite(g.Time) && g.Time > 0).Select(g => g.Time).ToArray();
        double average = times.Length == 0 ? 0 : times.Average();
        AverageTime.Text = times.Length == 0 ? "—" : average >= 1 ? $"~{average:0.0} h" : $"~{Math.Round(average * 60, MidpointRounding.AwayFromZero):0} min";
        AverageTime.ToolTip = $"Average completion-time estimate across {times.Length:N0} catalog games with a known positive time; unknown times are excluded. Search and filters do not change this statistic.";
        CoverCount.Text = Games.Count(g => !string.IsNullOrWhiteSpace(g.Cover) && File.Exists(g.Cover)).ToString("N0");
        CoverCount.ToolTip = "Games in the complete catalog with a cover file cached on this PC. Search and filters do not change this statistic.";
        catalogStatsDirty = false;
        }
        var selected = Games.Where(g => g.Selected).ToArray();
        SelectedSize.Text = $"{selected.Sum(g => g.SizeGb):0.#} GB";
        SelectedSize.ToolTip = $"{selected.Length} selected; {selected.Count(g => g.SizeGb <= 0)} sizes unknown";
    }
    internal void Save() { try { Store.Save(State); } catch (Exception ex) { Error(ex); } }
    private async Task<IDisposable> AcquireInstallScopeAsync(IEnumerable<string> gameIds, string destination, CancellationToken cancellation)
    {
        var ids = gameIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var held = new List<HeldInstallGate>(ids.Length);
        try
        {
            foreach (var id in ids)
            {
                SemaphoreSlim gate;
                lock (installGates)
                {
                    if (installGates.TryGetValue(id, out var existingGate) && existingGate != null)
                    {
                        gate = existingGate;
                    }
                    else
                    {
                        gate = new SemaphoreSlim(1, 1);
                        installGates.Add(id, gate);
                    }
                }
                await gate.WaitAsync(cancellation);
                FileStream? lockFile = null;
                try
                {
                    string lockPath = DockerScripts.InstallLockPath(destination, id);
                    Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
                    while (lockFile == null)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        try { lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough); }
                        catch (IOException) { await Task.Delay(250, cancellation); }
                    }
                    held.Add(new HeldInstallGate(gate, lockFile));
                    lockFile = null;
                }
                finally
                {
                    if (lockFile != null)
                    {
                        try { lockFile.Dispose(); } catch { }
                        gate.Release();
                    }
                }
            }
            return new InstallScope(held);
        }
        catch
        {
            for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose();
            throw;
        }
    }
    private sealed class InstallScope : IDisposable
    {
        private List<HeldInstallGate>? held;
        internal InstallScope(List<HeldInstallGate> held) => this.held = held;
        public void Dispose()
        {
            var gates = Interlocked.Exchange(ref held, null);
            if (gates == null) return;
            for (var index = gates.Count - 1; index >= 0; index--) gates[index].Dispose();
        }
    }
    private sealed class HeldInstallGate : IDisposable
    {
        private SemaphoreSlim? semaphore;
        private FileStream? lockFile;
        internal HeldInstallGate(SemaphoreSlim semaphore, FileStream lockFile) { this.semaphore = semaphore; this.lockFile = lockFile; }
        public void Dispose()
        {
            var currentLockFile = Interlocked.Exchange(ref lockFile, null);
            if (currentLockFile != null)
            {
                try { currentLockFile.Dispose(); } catch (ObjectDisposedException) { } catch (IOException) { }
            }
            Interlocked.Exchange(ref semaphore, null)?.Release();
        }
    }
    private void ObserveUiAction(string operation, Action action)
    {
        try { action(); }
        catch (Exception ex) { ReportUiFailure(operation, ex); }
    }
    private void ObserveUiOperation(string operation, Func<Task> action) => _ = ObserveUiOperationAsync(operation, action);
    private async Task ObserveUiOperationAsync(string operation, Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) when (closing || lifetime.IsCancellationRequested) { }
        catch (Exception ex) { ReportUiFailure(operation, ex); }
    }
    private void PostUiAction(string operation, Action action)
    {
        try
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            _ = Dispatcher.BeginInvoke(new Action(() => ObserveUiAction(operation, action)));
        }
        catch (InvalidOperationException) { }
        catch (Exception ex) { try { Store.Log(operation + " dispatch failed: " + ex); } catch { } }
    }
    private void PostUiOperation(string operation, Func<Task> action)
    {
        try
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            _ = Dispatcher.BeginInvoke(new Action(() => ObserveUiOperation(operation, action)));
        }
        catch (InvalidOperationException) { }
        catch (Exception ex) { try { Store.Log(operation + " dispatch failed: " + ex); } catch { } }
    }
    private void ReportUiFailure(string operation, Exception ex)
    {
        try { Store.Log(operation + ": " + ex); } catch { }
        if (closing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        try
        {
            StatusText.Text = ex.Message;
            if (IsVisible) System.Windows.MessageBox.Show(this, ex.Message, "Game Library", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception reportError) { try { Store.Log(operation + " reporting failed: " + reportError); } catch { } }
    }
    private void Error(Exception ex) => ReportUiFailure("UI operation failed", ex);
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (!ready) { searchPending = true; return; } searchDelay.Stop(); searchDelay.Start(); }
    private void CategoryChanged(object sender, SelectionChangedEventArgs e) { if (!ready || CategoryList.SelectedItem is not Category category) return; tab = category.Id; State.Settings.LastTab = tab; Save(); ApplyFilter(); }
    private void FilterChanged(object sender, SelectionChangedEventArgs e) { if (!ready) return; State.Settings.SortBy = SortBox.SelectedItem as string ?? "Newest first"; Save(); ApplyFilter(); }
    private void InstalledFilterChanged(object sender, RoutedEventArgs e)
    {
        if (sender == InstalledOnlyFilter && InstalledOnlyFilter.IsChecked == true) WithoutInstalledFilter.IsChecked = false;
        else if (sender == WithoutInstalledFilter && WithoutInstalledFilter.IsChecked == true) InstalledOnlyFilter.IsChecked = false;
        if (ready) ApplyFilter();
    }
    // Kept as a stable test/automation entry point for older callers.
    private void InstalledOnlyChanged(object sender, RoutedEventArgs e) => InstalledFilterChanged(sender, e);
    private void SelectionChecked(object sender, RoutedEventArgs e) { if (!changingSelection) UpdateStats(); }
    private void GameSelectionChanged(object sender, SelectionChangedEventArgs e) { }
    private void SelectAll(object sender, RoutedEventArgs e) { changingSelection = true; try { foreach (var game in filtered) game.Selected = true; } finally { changingSelection = false; } UpdateStats(); }
    private void DeselectAll(object sender, RoutedEventArgs e) { changingSelection = true; try { foreach (var game in Games) game.Selected = false; } finally { changingSelection = false; } UpdateStats(); }
    private void ResetFilters(object sender, RoutedEventArgs e) { SearchBox.Text = ""; RatingBox.SelectedIndex = 0; TagBox.SelectedIndex = 0; InstalledOnlyFilter.IsChecked = false; WithoutInstalledFilter.IsChecked = false; ApplyFilter(); }
    private void ToggleWishlist(object sender, RoutedEventArgs e) { if (((Button)sender).Tag is Game game) { if (!State.Wishlist.Add(game.Id)) State.Wishlist.Remove(game.Id); Save(); Reload(); } }
    private void GameDoubleClick(object sender, MouseButtonEventArgs e) { if (GameList.SelectedItem is Game game) Details(game); }
    private void OpenGameDetails(object sender, RoutedEventArgs e) { if (((Button)sender).Tag is Game game) Details(game); }
    private void PlayFromCard(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not Game game) return;
        ObserveUiOperation("Play", () => PlayGame(game));
    }
    private void PlayWithWandFromCard(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not Game game) return;
        ObserveUiOperation("Play with Wand", () => PlayWithWand(game));
    }
    private void RefreshCatalog(object sender, RoutedEventArgs e) => ObserveUiOperation("Catalog refresh", () => Refresh(true));
    internal async Task Refresh(bool full)
    {
        if (offline) { StatusText.Text = "Offline mode · shared edits stay on this PC until you restart online"; return; }
        if (!ready || refreshing || closing) return;
        if (full) lastCatalogRefresh = DateTime.UtcNow;
        refreshing = true; StatusText.Text = full ? "Refreshing catalog and Docker tags…" : "Checking shared changes…";
        var before = Sync.Effective(State).ToJsonString();
        try { await Sync.Refresh(State, full, adminToken, lifetime.Token); if (!closing) { if (full || before != Sync.Effective(State).ToJsonString()) Reload(); StatusText.Text = Sync.Status; } }
        catch (Exception ex) { if (!closing) Error(ex); }
        finally { refreshing = false; if (!closing) ScheduleMetadata(); }
    }
    private void ToggleTheme(object sender, RoutedEventArgs e) { State.Settings.Theme = State.Settings.Theme == "dark" ? "light" : "dark"; Save(); ApplyTheme(); }
    private void ApplyTheme()
    {
        bool light = State.Settings.Theme == "light";
        var values = new Dictionary<string, string> { ["CanvasBrush"] = light ? "#F3F5F6" : "#101217", ["PanelBrush"] = light ? "#FFFFFF" : "#171B23", ["CardBrush"] = light ? "#E8EDF0" : "#1D222C", ["StrokeBrush"] = light ? "#C8D2D9" : "#303847", ["TextBrush"] = light ? "#17212D" : "#F2F5FA", ["MutedBrush"] = light ? "#536479" : "#A9B5C9", ["AccentBrush"] = light ? "#58B37F" : "#9CE7BB" };
        foreach (var entry in values) System.Windows.Application.Current.Resources[entry.Key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(entry.Value));
    }
    private void Keyboard(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (KeyboardDevice.Modifiers == ModifierKeys.Control && e.Key == Key.K) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.F5) { ObserveUiOperation("Catalog refresh", () => Refresh(true)); e.Handled = true; }
        else if (KeyboardDevice.Modifiers == ModifierKeys.Control && e.Key == Key.A && !SearchBox.IsKeyboardFocused) { SelectAll(this, new()); e.Handled = true; }
        else if (e.Key == Key.Enter && GameList.IsKeyboardFocusWithin && GameList.SelectedItem is Game game) { Details(game); e.Handled = true; }
        else if (e.Key == Key.Escape) { SearchBox.Clear(); DeselectAll(this, new()); }
    }
    private static ModifierKeys KeyboardModifiers => System.Windows.Input.Keyboard.Modifiers;
    private static class KeyboardDevice { public static ModifierKeys Modifiers => System.Windows.Input.Keyboard.Modifiers; }
    private void OpenFolder(string folder)
    {
        if (!Directory.Exists(folder)) { StatusText.Text = "Folder does not exist yet: " + folder; return; }
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { folder } });
    }
    private sealed class PlaySession
    {
        public required Process Process { get; set; }
        public required string ExecutablePath { get; init; }
        public required double StartingSeconds { get; init; }
        public required Stopwatch Clock { get; init; }
        public bool UsesWand { get; init; }
        public DateTime GraceUntilUtc { get; set; }
        public DateTime LastSavedUtc { get; set; }
        public HashSet<int> ObservedProcessIds { get; } = new();
    }
    private void StartPlaySession(Game game, Func<Process> start)
    {
        if (TryActivateExistingPlay(game)) return;
        Process? launched = null;
        try
        {
            launched = start();
            TrackPlayProcess(game, launched, ownsProcess: true);
            launched = null;
        }
        finally
        {
            if (launched != null) StopUntrackedProcess(launched, "direct launch tracking");
        }
    }
    private async Task StartPlaySessionAsync(Game game, Func<Task<Process>> start)
    {
        if (TryActivateExistingPlay(game)) return;
        Process? launched = null;
        try
        {
            launched = await start();
            TrackPlayProcess(game, launched, ownsProcess: true);
            launched = null;
        }
        finally
        {
            if (launched != null) StopUntrackedProcess(launched, "direct async launch tracking");
        }
    }
    private bool TryActivateExistingPlay(Game game)
    {
        if (!activePlays.TryGetValue(game.Id, out var existing)) return false;
        try { if (!existing.Process.HasExited) { existing.Process.Refresh(); ActivateProcess(existing.Process); StatusText.Text = game.Name + " is already running."; return true; } }
        catch { }
        CommitPlaySession(game.Id, existing); Save();
        return false;
    }
    private void TrackPlayProcess(Game game, Process process, bool usesWand = false, bool ownsProcess = false)
    {
        try
        {
            process.EnableRaisingEvents = true;
            string executable;
            try { executable = process.MainModule?.FileName ?? State.LaunchPaths.GetValueOrDefault(game.Id, ""); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { executable = State.LaunchPaths.GetValueOrDefault(game.Id, ""); }
            int processId = process.Id;
            var now = DateTime.UtcNow;
            var session = new PlaySession { Process = process, ExecutablePath = executable, StartingSeconds = State.PlayTimeSeconds.GetValueOrDefault(game.Id), Clock = Stopwatch.StartNew(), UsesWand = usesWand, GraceUntilUtc = now.AddSeconds(20), LastSavedUtc = now };
            session.ObservedProcessIds.Add(processId); activePlays[game.Id] = session;
            Store.Log("Launched game " + game.Id + "; tracking play time from PID " + processId);
            StatusText.Text = "Playing " + game.Name + " · tracking time";
        }
        catch
        {
            if (ownsProcess) StopUntrackedProcess(process, "play tracking");
            else { try { process.Dispose(); } catch { } }
            throw;
        }
    }
    private void StopUntrackedProcess(Process process, string context)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) { try { Store.Log("Could not stop the untracked process after " + context + ": " + ex.Message); } catch { } }
        finally { try { process.Dispose(); } catch { } }
    }
    private static void ActivateProcess(Process process)
    {
        try { if (process.MainWindowHandle != IntPtr.Zero) NativeWindow.Activate(process.MainWindowHandle); } catch { }
    }
    private static Process? FindReplacementProcess(string executable, HashSet<int> observed)
    {
        if (string.IsNullOrWhiteSpace(executable)) return null;
        string name = Path.GetFileNameWithoutExtension(executable);
        foreach (var candidate in Process.GetProcessesByName(name))
        {
            bool keep = false;
            try
            {
                if (observed.Contains(candidate.Id)) continue;
                if (string.Equals(candidate.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                {
                    keep = true;
                    return candidate;
                }
            }
            catch { }
            finally
            {
                if (!keep) try { candidate.Dispose(); } catch { }
            }
        }
        return null;
    }
    private void UpdatePlaySessions()
    {
        if (!ready || activePlays.Count == 0) return;
        bool save = false;
        foreach (var entry in activePlays.ToArray())
        {
            var id = entry.Key; var session = entry.Value; Process? replacement = null;
            bool stateKnown = false;
            try
            {
                session.Process.Refresh();
                if (session.Process.HasExited && DateTime.UtcNow <= session.GraceUntilUtc)
                    replacement = FindReplacementProcess(session.ExecutablePath, session.ObservedProcessIds);
                if (replacement != null) { session.Process.Dispose(); session.Process = replacement; session.ObservedProcessIds.Add(replacement.Id); }
                if (!session.Process.HasExited)
                {
                    stateKnown = true;
                    State.PlayTimeSeconds[id] = session.StartingSeconds + session.Clock.Elapsed.TotalSeconds;
                    if ((DateTime.UtcNow - session.LastSavedUtc).TotalSeconds >= 10) { session.LastSavedUtc = DateTime.UtcNow; save = true; }
                    UpdatePlayedLabel(id, State.PlayTimeSeconds[id]);
                    continue;
                }
                stateKnown = true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
            {
                Store.Log("Could not determine whether game " + id + " is still running; keeping its play session for the next poll: " + ex.Message);
                continue;
            }
            if (!stateKnown) continue;
            CommitPlaySession(id, session); save = true;
        }
        if (save) Save();
    }
    private void CommitPlaySession(string id, PlaySession session)
    {
        State.PlayTimeSeconds[id] = Math.Max(State.PlayTimeSeconds.GetValueOrDefault(id), session.StartingSeconds + session.Clock.Elapsed.TotalSeconds);
        activePlays.Remove(id); session.Process.Dispose(); UpdatePlayedLabel(id, State.PlayTimeSeconds[id]);
        Store.Log("Play session ended for " + id + "; seconds=" + State.PlayTimeSeconds[id].ToString("0"));
    }
    private void FlushPlaySessions()
    {
        foreach (var entry in activePlays.ToArray()) CommitPlaySession(entry.Key, entry.Value);
        if (activePlays.Count == 0 && ready) Save();
    }
    private void UpdatePlayedLabel(string id, double seconds)
    {
        var game = Games.FirstOrDefault(g => g.Id == id); if (game == null) return;
        game.PlayedHours = Math.Max(0, seconds) / 3600d; game.Notify(nameof(Game.PlayedHours)); game.Notify(nameof(Game.PlayedMeta)); game.Notify(nameof(Game.Meta));
    }
    private void AdminSignIn(object sender, RoutedEventArgs e)
    {
        if (IsAdmin) { adminToken = null; AdminButton.Content = "Admin sign in"; AccountLabel.Text = "Personal library"; Reload(); return; }
        var dialog = new EditorWindow(this, "Admin sign in", "Use the same admin password as the website. The password is not saved.");
        var password = dialog.Password("Password", "AdminPassword");
        dialog.Action("Sign in", () =>
        {
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password.Password))).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes("fba92b2c989a5072544ca49d7f75db2005e6479bf286a38902de90e487230762"))) { dialog.Notice.Text = "Incorrect password."; return; }
            adminToken = "glm-admin-2024";
            AdminButton.Content = "Sign out"; AccountLabel.Text = "Admin · shared catalog"; dialog.Close(); Reload(); ObserveUiOperation("Admin refresh", () => Refresh(false));
        });
        dialog.ShowDialog();
    }
    private bool RequireAdmin() { if (IsAdmin) return true; AdminSignIn(this, new()); return IsAdmin; }
    private void MoveSelected(object sender, RoutedEventArgs e) => ObserveUiOperation("Move selected games", MoveSelectedAsync);
    private async Task MoveSelectedAsync()
    {
        var selected = Games.Where(g => g.Selected).ToArray();
        if (selected.Length == 0) { StatusText.Text = "Select games to move first."; return; }
        if (!RequireAdmin()) return;
        var dialog = new EditorWindow(this, "Move selected games", $"Move {selected.Length} game(s). This updates the shared website catalog.");
        var category = dialog.Choice("Category", Store.LoadCategories(Sync.Effective(State)).Where(c => c.Id != "all").ToArray());
        dialog.Action("Move games", () =>
        {
            if (category.SelectedItem is not Category target) return;
            foreach (var game in selected)
                if (game.IsLocal) State.LocalGames[game.Id].Category = target.Id;
                else Sync.Queue(State, "gameCategories", game.Id, JsonValue.Create(target.Id));
            Save();
            dialog.Close(); Reload();
        });
        dialog.ShowDialog(); if (!offline) await Refresh(false);
    }
    private void ExportScriptMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var format in new[] { "bat", "ps1", "sh" })
        {
            var label = format switch { "bat" => "Download .BAT (default)", "ps1" => "Download .PS1 (PowerShell)", _ => "Download .SH (" + (State.Settings.ShellTarget == "wsl2" ? "WSL2" : "Native Linux") + ")" };
            var item = new MenuItem { Header = label }; var f = format;
            item.Click += (_, _) => ExportScript(f, false, State.Settings.ShellTarget); menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var copy = new MenuItem { Header = "Copy PowerShell script" }; copy.Click += (_, _) => { try { System.Windows.Clipboard.SetText(DockerScripts.Generate(Selected(), State.Settings)); StatusText.Text = "Script copied."; } catch (Exception ex) { Error(ex); } }; menu.Items.Add(copy);
        var kill = new MenuItem { Header = "Export stop-selected-containers script" }; kill.Click += (_, _) => ExportScript("ps1", true, State.Settings.ShellTarget); menu.Items.Add(kill);
        menu.Items.Add(new Separator());
        foreach (var format in new[] { "ps1", "bat" })
        {
            var item = new MenuItem { Header = "Export Kill All script (." + format + ")" }; var f = format;
            item.Click += (_, _) => ExportKillAll(f); menu.Items.Add(item);
        }
        menu.PlacementTarget = (Button)sender; menu.IsOpen = true;
    }
    private Game[] Selected() => Games.Where(g => g.Selected).ToArray();
    private void ExportKillAll(string format)
    {
        var review = new EditorWindow(this, "Export Kill All container script", "Saves a script; nothing runs in this app. When you run it, it lists every container in your active Docker target, including containers unrelated to this library. It then requires you to type DELETE ALL before force-stopping and removing the listed containers and their writable layers. Images, volumes, and downloaded game folders are preserved.");
        review.Action("Save script…", () =>
        {
            var script = DockerScripts.GenerateKillAll(format);
            var save = new Microsoft.Win32.SaveFileDialog { FileName = "kill-all-containers", DefaultExt = "." + format, Filter = format.ToUpperInvariant() + " script|*." + format };
            if (save.ShowDialog(review) != true) return;
            File.WriteAllText(save.FileName, script, format == "ps1" ? new UTF8Encoding(true) : new UTF8Encoding(false));
            StatusText.Text = "Saved " + save.FileName + ". No containers were changed.";
            review.Close();
        }, "SaveKillAllScriptButton");
        review.Action("Cancel", () => review.Close(), "CancelKillAllScriptButton");
        review.ShowDialog();
    }
    private void ExportScript(string format, bool stop, string? shellTarget = null)
    {
        try
        {
            var script = DockerScripts.Generate(Selected(), State.Settings, format, stop, shellTarget);
            State.Settings.ScriptFormat = format; Save();
            var save = new Microsoft.Win32.SaveFileDialog { FileName = stop ? "stop-selected-games" : "install-games", DefaultExt = "." + format, Filter = format.ToUpperInvariant() + " script|*." + format };
            if (save.ShowDialog(this) == true) { File.WriteAllText(save.FileName, script, format == "ps1" ? new UTF8Encoding(true) : new UTF8Encoding(false)); StatusText.Text = "Saved " + save.FileName; }
        }
        catch (Exception ex) { Error(ex); }
    }
    private void InstallSelected(object sender, RoutedEventArgs e) => StartInstall(Selected());
    internal void StartInstall(Game[] games)
    {
        try
        {
            games = DockerScripts.DistinctGames(games);
            if (games.Length == 0) { StatusText.Text = "Select games to install first."; return; }
            string destination = State.Settings.MountPath;
            var gameIds = games.Select(g => g.Id).ToArray();
            var review = new EditorWindow(this, "Install selected games", $"{games.Length} game(s) → {State.Settings.MountPath}\nDownload size: {games.Sum(g => g.SizeGb):0.#} GB; {games.Count(g => g.SizeGb <= 0)} unknown. Existing files in each game's folder may be updated.\nChoose the Windows BAT route for your default terminal, or WSL2 Ubuntu for a native Bash .SH install.");
            var names = review.Paragraph(string.Join("\n", games.Select(g => g.Name))); names.MaxHeight = 220;
            var format = review.Choice("Install format", new[] { "Windows default terminal (.BAT)", "WSL2 Ubuntu (.SH)" }, State.Settings.ShellTarget == "wsl2" ? 1 : 0, "InstallFormat");
            review.Action("Start download", () =>
            {
                bool wsl2 = format.SelectedIndex == 1;
                string extension = wsl2 ? "sh" : "bat";
                string script = DockerScripts.Generate(games, State.Settings, extension, shellTarget: wsl2 ? "wsl2" : "native-linux");
                var byId = games.ToDictionary(g => g.Id, StringComparer.Ordinal);
                var job = new JobWindow(Store, script, games.Select(g => DockerScripts.ContainerNameForDestination(g.Id, destination)).ToArray(), openInDefaultTerminal: !wsl2, scriptExtension: extension, completionDestination: destination, completionGameIds: gameIds, acquireInstallation: cancellation => AcquireInstallScopeAsync(gameIds, destination, cancellation));
                job.GameCompleted += id => byId.TryGetValue(id, out var game) ? ScanCompletedGame(game, destination, job.CompletionStartedAtUtc, job.OperationId) : Task.CompletedTask;
                job.CompletedAsync += success => ScanCompletedDownloads(games, destination, success, job.CompletionStartedAtUtc, job.OperationId);
                jobs.Add(job); job.Closed += (_, _) => jobs.Remove(job); job.Show(); review.Close();
            });
            review.ShowDialog();
        }
        catch (Exception ex) { Error(ex); }
    }
    private void ScanFolder(object sender, RoutedEventArgs e) => ObserveUiOperation("Installed folder scan", ScanFolderAsync);
    private async Task ScanFolderAsync()
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the folder containing installed games", InitialDirectory = Directory.Exists(State.Settings.MountPath) ? State.Settings.MountPath : "" };
        if (picker.ShowDialog(this) != true) return;
        string root = picker.FolderName;
        StatusText.Text = "Scanning " + root + "…";
        try
        {
            var snapshot = Games.Select(g => (g.Id, g.Name)).ToArray();
            var found = await Task.Run(() => InstalledScanner.Discover(root, snapshot, lifetime.Token));
            ApplyInstalledScan(found, reconcileMissingCatalog: true);
            // This explicit scan reconciles stale catalog markers only when the
            // selected root is available; manual launchers on another path remain.
            State.Settings.MountPath = root; Save(); Reload(); StatusText.Text = $"Found {found.Games.Count} installed games; {found.LauncherCount} launchers ready. Open Details to choose or play. " + (found.Notices.Count > 0 ? "Scan notices are in the activity log." : "");
        }
        catch (Exception ex) { Error(ex); }
    }
}
