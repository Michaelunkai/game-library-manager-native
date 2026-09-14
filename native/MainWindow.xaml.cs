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
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace GameLibrary.Native;

public sealed class CoverConverter : IValueConverter
{
    private const int FallbackWidth = 128;
    private const int FallbackHeight = 180;
    private static readonly Dictionary<string, ImageSource> cache = new(StringComparer.Ordinal);

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var game = value as Game;
        var source = game != null ? game.Cover ?? string.Empty : value as string ?? string.Empty;
        var fallbackKey = game == null ? source : (string.IsNullOrWhiteSpace(game.Id) ? game.Name ?? string.Empty : game.Id);
        var cacheKey = game == null ? source : "game:" + fallbackKey + "\0" + source;
        if (cache.TryGetValue(cacheKey, out var image)) return image;

        image = Load(source) ?? CreateFallback(fallbackKey);
        if (cache.Count > 2500) cache.Clear();
        cache[cacheKey] = image;
        return image;
    }

    private static ImageSource? Load(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        try
        {
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.DecodePixelHeight = FallbackHeight;
            bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(source, UriKind.RelativeOrAbsolute); bitmap.EndInit();
            if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0) return null;
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }

    private static ImageSource CreateFallback(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        byte red = (byte)(28 + hash[0] % 128);
        byte green = (byte)(28 + hash[1] % 128);
        byte blue = (byte)(28 + hash[2] % 128);
        byte accentRed = (byte)Math.Min(255, red + 72);
        byte accentGreen = (byte)Math.Min(255, green + 72);
        byte accentBlue = (byte)Math.Min(255, blue + 72);
        const int stride = FallbackWidth * 4;
        var pixels = new byte[stride * FallbackHeight];

        for (var y = 0; y < FallbackHeight; y++)
        {
            for (var x = 0; x < FallbackWidth; x++)
            {
                var accent = ((x / 16) + (y / 16)) % 2 == 0;
                var offset = y * stride + x * 4;
                pixels[offset] = accent ? accentBlue : blue;
                pixels[offset + 1] = accent ? accentGreen : green;
                pixels[offset + 2] = accent ? accentRed : red;
                pixels[offset + 3] = 255;
            }
        }

        var fallback = BitmapSource.Create(FallbackWidth, FallbackHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        fallback.Freeze();
        return fallback;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public partial class MainWindow : Window
{
    private static class NativeWindow
    {
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr handle, int command);
        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr handle, out Rect rect);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private const uint SwpNoOwnerZOrder = 0x0200;
        public static void Activate(IntPtr handle) { ShowWindow(handle, 9); SetForegroundWindow(handle); }
        public static bool PlaceOnWorkingArea(IntPtr handle, System.Drawing.Rectangle area)
        {
            if (handle == IntPtr.Zero || !GetWindowRect(handle, out var current)) return false;
            int width = Math.Min(Math.Max(1, current.Right - current.Left), area.Width);
            int height = Math.Min(Math.Max(1, current.Bottom - current.Top), area.Height);
            int x = area.Left + Math.Max(0, (area.Width - width) / 2);
            int y = area.Top + Math.Max(0, (area.Height - height) / 2);
            return SetWindowPos(handle, IntPtr.Zero, x, y, width, height, SwpNoZOrder | SwpNoActivate | SwpShowWindow | SwpNoOwnerZOrder);
        }
    }
    internal readonly LibraryStore Store;
    internal UserState State = new();
    internal readonly SyncClient Sync;
    internal List<Game> Games = new();
    private List<Game> filtered = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer installedScanPoll = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer playtime = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer searchDelay = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private DateTime lastCatalogRefresh;
    private Forms.NotifyIcon? tray;
    private bool ready, refreshing, closing, searchPending, installedScanning;
    private bool changingSelection, catalogStatsDirty = true;
    private readonly bool offline;
    private readonly StartupPlacement startupPlacement;
    private readonly OfflineNetworkGuard? offlineNetwork;
    private bool startupPlacementQueued;
    private bool startupPlacementDone;
    private DateTime startupPlacementGraceUntilUtc;
    private string tab = "all";
    private string? adminToken;
    private readonly List<JobWindow> jobs = new();
    private readonly Dictionary<string, PlaySession> activePlays = new(StringComparer.Ordinal);
    private readonly HashSet<string> wandIncludedGameIds = new(StringComparer.Ordinal);
    private bool wandRegistrationSyncing;
    private readonly Dictionary<string, SemaphoreSlim> installGates = new(StringComparer.Ordinal);
    private readonly InstallReservationBook installReservations = new();
    // Direct Play and Play with Wand share one launch gate. This prevents a
    // direct launch from appearing between Wand's PID snapshot and its URI
    // handoff, where it could otherwise be mistaken for a Wand-owned process.
    private readonly SemaphoreSlim playLaunchGate = new(1, 1);
    internal bool IsAdmin => adminToken != null;
    internal bool IsClosing => closing;
    private static readonly HashSet<string> protectedTabs = new(StringComparer.Ordinal) { "not_for_me", "finished", "mybackup", "oporationsystems", "music", "win11maintaince", "3th_party_tools", "gamedownloaders" };

    public MainWindow(LibraryStore store, bool offline = false, StartupMonitorMode monitorMode = StartupMonitorMode.Auto)
    {
        Store = store; this.offline = offline; startupPlacement = WindowPlacement.Capture(monitorMode);
        offlineNetwork = offline ? new OfflineNetworkGuard() : null;
        Sync = new SyncClient(store, offlineNetwork);
        Program.SetCurrentProcessExplicitAppUserModelID("GameLibraryManager.Native");
        InitializeComponent();
        PlaceOnStartupMonitor();
        Style = (Style)System.Windows.Application.Current.FindResource(typeof(Window));
        Loaded += OnLoaded;
        ContentRendered += (_, _) => QueueStartupPlacement();
        Closing += OnClosing;
        StateChanged += (_, _) => ObserveUiAction("Window state change", () =>
        {
            if (!ready || WindowState != WindowState.Minimized || !State.Settings.MinimizeToTray) return;
            if (!startupPlacementDone || DateTime.UtcNow < startupPlacementGraceUntilUtc) { QueueStartupPlacement(force: true); return; }
            HideToTray();
        });
        PreviewKeyDown += Keyboard;
        poll.Tick += (_, _) => ObserveUiOperation("Catalog poll", () => Refresh(DateTime.UtcNow - lastCatalogRefresh >= TimeSpan.FromSeconds(15)));
        installedScanPoll.Tick += (_, _) => ObserveUiOperation("Installed game scan", ScanConfiguredInstalledGamesAsync);
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
            State = await Task.Run(() =>
            {
                Sync.ReloadCache();
                return Store.LoadState();
            }, lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            Width = Math.Clamp(State.Settings.WindowWidth, MinWidth, SystemParameters.WorkArea.Width);
            Height = Math.Clamp(State.Settings.WindowHeight, MinHeight, SystemParameters.WorkArea.Height);
            PlaceOnStartupMonitor();
            tab = State.Settings.LastTab;
            SortBox.ItemsSource = new[] { "Name A–Z", "Name Z–A", "Time to Beat (Low–High)", "Time to Beat (High–Low)", "Recently Added", "Recently Played", "Oldest First", "Rating (High–Low)", "Rating (Low–High)", "Size (Small–Large)", "Size (Large–Small)", "Category" };
            SortBox.SelectedItem = State.Settings.SortBy;
            if (SortBox.SelectedIndex < 0) SortBox.SelectedIndex = 4;
            RatingBox.ItemsSource = new[] { "Any rating", "1+ stars", "2+ stars", "3+ stars", "4+ stars", "5 stars" }; RatingBox.SelectedIndex = 0;
            lifetime.Token.ThrowIfCancellationRequested();
            InitializeTray(); ApplyTheme(); ready = true; Reload();
            QueueStartupPlacement(force: true);
            // A fast automation client or a user can type while the bundled
            // catalog is still loading. Reapply that text after readiness so
            // the first search is never dropped by the ready guard.
            if (searchPending) { searchPending = false; ApplyFilter(); }
            playtime.Start();
            StatusText.Text = Store.RecoveryNotice ?? $"Offline catalog ready · {Games.Count:N0} exact game identities";
            Store.Log("Window ready; catalog=" + Games.Count);
            if (Program.TestReport != null) { if (!offline) await Refresh(true); await RunUiProof(Program.TestReport); return; }
            await SynchronizeWandSupportedGamesAsync();
            if (!offline) { poll.Start(); await Refresh(true); }
            await ScanConfiguredInstalledGamesAsync();
            if (!closing) installedScanPoll.Start();
            QueueStartupPlacement(force: true);
        }
        catch (OperationCanceledException) when (closing || lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Error(ex); }
    }
    private Forms.Screen GetStartupScreen() => WindowPlacement.Resolve(startupPlacement);
    private bool PlaceOnStartupMonitor() => PlaceOnStartupMonitor(GetStartupScreen());
    private bool PlaceOnStartupMonitor(Forms.Screen? screen)
    {
        if (screen == null) return false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var area = screen.WorkingArea;
        var width = Math.Min(Width, (double)area.Width);
        var height = Math.Min(Height, (double)area.Height);
        Left = area.Left + Math.Max(0, (area.Width - width) / 2);
        Top = area.Top + Math.Max(0, (area.Height - height) / 2);
        return true;
    }
    private void QueueStartupPlacement(bool force = false)
    {
        if (closing || (!force && startupPlacementQueued) || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        startupPlacementQueued = true;
        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                startupPlacementQueued = false;
                if (closing) return;
                WindowStartupLocation = WindowStartupLocation.Manual;
                WindowState = WindowState.Normal;
                var screen = GetStartupScreen();
                var placed = PlaceOnStartupMonitor(screen);
                if (!IsVisible) Show();
                if (placed && screen != null)
                    placed = NativeWindow.PlaceOnWorkingArea(new WindowInteropHelper(this).Handle, screen.WorkingArea);
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
        poll.Stop(); installedScanPoll.Stop(); searchDelay.Stop(); playtime.Stop();
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
        foreach (var active in activePlays)
        {
            var game = Games.FirstOrDefault(candidate => candidate.Id == active.Key);
            if (game == null) continue;
            game.PlayedHours = Math.Max(0, active.Value.Timing.TotalSeconds) / 3600d;
            game.IsPlaying = true;
            game.IsPlayPaused = active.Value.Timing.IsPaused;
        }
        RefreshWandPlayAvailability();
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
    private void RefreshWandPlayAvailability()
    {
        foreach (var game in Games)
        {
            bool canLaunch = wandIncludedGameIds.Contains(game.Id);
            if (game.CanPlayWithWand == canLaunch) continue;
            game.CanPlayWithWand = canLaunch;
            game.Notify(nameof(Game.CanPlayWithWand));
        }
    }
    internal static int WandLibraryMatchScore(Game game, WandSupportedGame registration, string? savedLaunchPath)
    {
        if (!string.IsNullOrWhiteSpace(savedLaunchPath) && string.Equals(savedLaunchPath, registration.Path, StringComparison.OrdinalIgnoreCase)) return 100_000;
        bool exactName = WandIntegration.Normalize(game.Name) == WandIntegration.Normalize(registration.Name);
        if (exactName && string.Equals(game.Id, registration.Folder, StringComparison.Ordinal)) return 98_000;
        if (exactName && string.Equals(game.Id, registration.Folder, StringComparison.OrdinalIgnoreCase)) return 96_000;
        if (exactName && WandIntegration.Normalize(game.Id) == WandIntegration.Normalize(registration.Folder)) return 94_000;
        if (exactName) return 90_000;
        // A folder-shaped catalog id is not sufficient by itself. The catalog
        // contains collisions such as Ashen/Ashen Empires and Tails of Iron/II;
        // mapping on the id alone would put Wand on the wrong game card.
        return 0;
    }
    private async Task SynchronizeWandSupportedGamesAsync()
    {
        if (closing || wandRegistrationSyncing || Program.TestReport != null) return;
        wandRegistrationSyncing = true;
        try
        {
            var registrations = await Task.Run(WandIntegration.LoadSupportedGames, lifetime.Token);
            if (closing || registrations.Count == 0) return;
            var used = new HashSet<string>(StringComparer.Ordinal);
            var included = new HashSet<string>(StringComparer.Ordinal);
            foreach (var registration in registrations)
            {
                var match = Games
                    .Where(game => !used.Contains(game.Id))
                    .Select(game => new { Game = game, Score = WandLibraryMatchScore(game, registration, State.LaunchPaths.GetValueOrDefault(game.Id)) })
                    .Where(candidate => candidate.Score > 0)
                    .OrderByDescending(candidate => candidate.Score)
                    .ThenBy(candidate => candidate.Game.Id, StringComparer.Ordinal)
                    .Select(candidate => candidate.Game)
                    .FirstOrDefault();
                if (match == null)
                {
                    string runtimeId = Games.Any(game => string.Equals(game.Id, registration.Folder, StringComparison.Ordinal))
                        ? "wand:" + registration.GameId
                        : registration.Folder;
                    match = new Game
                    {
                        Id = runtimeId,
                        Name = registration.Name,
                        Category = "installed",
                        CategoryName = "Installed",
                        Description = "Installed on this PC · exact Wand registration",
                        IsLocal = true,
                        ShowTime = State.Settings.ShowTimes,
                        ShowCategory = State.Settings.ShowCategories,
                        CoverHeight = State.Settings.GridSize == "small" ? 70 : State.Settings.GridSize == "large" ? 130 : 98
                    };
                    Games.Add(match);
                    catalogStatsDirty = true;
                }
                used.Add(match.Id);
                included.Add(match.Id);
                if (!string.Equals(match.Name, registration.Name, StringComparison.Ordinal))
                {
                    match.Name = registration.Name;
                    match.Notify(nameof(Game.Name));
                }
                State.LaunchPaths[match.Id] = registration.Path;
                State.InstalledGames.Add(match.Id);
                match.Installed = true;
                match.CanPlayWithWand = true;
                match.Notify("");
            }
            wandIncludedGameIds.Clear();
            wandIncludedGameIds.UnionWith(included);
            RefreshWandPlayAvailability();
            ApplyFilter();
            Store.Log("Wand included synchronized " + included.Count + " latest supported registrations.");
            if (WandIncludedFilter.IsChecked == true)
                StatusText.Text = "Wand included · " + included.Count + " latest registered games";
        }
        catch (OperationCanceledException) when (closing || lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Store.Log("Wand included refresh preserved the previous list: " + ex.Message);
        }
        finally { wandRegistrationSyncing = false; }
    }
    private bool CanLaunchWithExistingWand(Game game, out string message)
    {
        message = "This game is not in the latest Wand included registration list.";
        if (!game.CanPlayWithWand || !wandIncludedGameIds.Contains(game.Id)) return false;
        if (!State.LaunchPaths.ContainsKey(game.Id)) { message = "The exact Wand launch path is unavailable."; return false; }
        message = string.Empty;
        return true;
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
        bool wandOnly = WandIncludedFilter.IsChecked == true;
        var hidden = Hidden(Sync.Effective(State));
        IEnumerable<Game> visible = wandOnly ? Games.Where(g => g.CanPlayWithWand) : Games;
        if (!wandOnly && !IsAdmin) visible = visible.Where(g => !protectedTabs.Contains(g.Category) && !hidden.Contains(g.Category));
        string query = SearchBox.Text.Trim();
        if (!wandOnly && tab == "installed") visible = visible.Where(g => g.Installed);
        else if (!wandOnly && query.Length == 0)
        {
            if (tab == "wishlist") visible = visible.Where(g => g.Wishlisted);
            else if (tab != "all") visible = visible.Where(g => g.Category == tab);
        }
        if (query.Length > 0) visible = visible.Where(g => MatchesSearch(g, query));
        if (InstalledOnlyFilter.IsChecked == true) visible = visible.Where(g => g.Installed);
        else if (WithoutInstalledFilter.IsChecked == true) visible = visible.Where(g => !g.Installed);
        else if (wandOnly) visible = visible.Where(g => g.CanPlayWithWand);
        int rating = Math.Max(0, RatingBox.SelectedIndex); visible = visible.Where(g => g.Rating >= rating);
        string tag = TagBox.SelectedItem as string ?? "All tags";
        if (tag != "All tags") visible = visible.Where(g => State.GameTags.GetValueOrDefault(g.Id, new()).Contains(tag));
        string sort = SortBox.SelectedItem as string ?? "Recently Added";
        visible = tab == "installed" ? SortInstalledGames(visible, sort) : SortGames(visible, sort);
        filtered = visible.ToList();
        GameList.ItemsSource = filtered;
        EmptyState.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = wandOnly ? (query.Length > 0 ? "Search Wand games" : "Wand included")
            : query.Length > 0 ? (tab == "installed" || InstalledOnlyFilter.IsChecked == true ? "Search installed games" : "Search all games")
            : (CategoryList.SelectedItem as Category)?.Name.Replace("◈  ", "").Replace("♡  ", "").Replace("▣  ", "") ?? "All games";
        CatalogCaption.Text = $"{Games.Count:N0} games in your catalog · Search, organize, and play";
        UpdateStats();
        ScheduleMetadata();
    }
    internal static IEnumerable<Game> SortGames(IEnumerable<Game> source, string sort)
    {
        static IOrderedEnumerable<Game> NameAsc(IEnumerable<Game> games) => games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.Id, StringComparer.Ordinal);
        static bool HasTime(Game g) => double.IsFinite(g.Time) && g.Time > 0;
        static bool HasSize(Game g) => double.IsFinite(g.ComparableSizeGb) && g.ComparableSizeGb > 0;
        static bool HasRating(Game g) => g.Rating > 0;
        static bool HasPlayed(Game g) => g.LastPlayedUtc != default || g.PlayedHours > 0;
        static bool HasDate(Game g) => g.Added != default || g.Category == "new";
        static DateTime DateValue(Game g) => g.Added != default ? g.Added : DateTime.UtcNow;
        return sort switch
        {
            "Name A–Z" => NameAsc(source),
            "Name Z–A" => source.OrderByDescending(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.Id, StringComparer.Ordinal),
            "Time to Beat (Low–High)" => source.OrderBy(g => HasTime(g) ? 0 : 1).ThenBy(g => HasTime(g) ? g.Time : double.MaxValue).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            "Time to Beat (High–Low)" => source.OrderBy(g => HasTime(g) ? 0 : 1).ThenByDescending(g => HasTime(g) ? g.Time : double.MinValue).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            "Oldest First" => source.OrderBy(g => HasDate(g) ? 0 : 1).ThenBy(g => HasDate(g) ? DateValue(g) : DateTime.MaxValue).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            "Recently Played" => source.OrderByDescending(g => HasPlayed(g)).ThenByDescending(g => g.LastPlayedUtc).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            "Rating (High–Low)" => source.OrderBy(g => HasRating(g) ? 0 : 1).ThenByDescending(g => HasRating(g) ? g.Rating : int.MinValue).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            "Rating (Low–High)" => source.OrderBy(g => HasRating(g) ? 0 : 1).ThenBy(g => HasRating(g) ? g.Rating : int.MaxValue).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            "Size (Small–Large)" => source.OrderBy(g => HasSize(g) ? 0 : 1).ThenBy(g => HasSize(g) ? g.ComparableSizeGb : double.MaxValue).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            "Size (Large–Small)" => source.OrderBy(g => HasSize(g) ? 0 : 1).ThenByDescending(g => HasSize(g) ? g.ComparableSizeGb : double.MinValue).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            "Category" => source.OrderBy(g => g.CategoryName, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => source.OrderBy(g => HasDate(g) ? 0 : 1).ThenByDescending(g => HasDate(g) ? DateValue(g) : DateTime.MinValue).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
        };
    }
    internal static IEnumerable<Game> SortInstalledGames(IEnumerable<Game> source, string secondarySort)
    {
        static bool HasPlayed(Game game) => game.LastPlayedUtc != default || game.PlayedHours > 0;
        return SortGames(source, secondarySort).ToList()
            .OrderByDescending(HasPlayed)
            .ThenByDescending(game => game.LastPlayedUtc)
            .ThenByDescending(game => game.PlayedHours);
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
    private string FrozenProcessesPath => string.IsNullOrWhiteSpace(State.Settings.FrozenProcessesPath)
        ? Preferences.DefaultFrozenProcessesPath
        : State.Settings.FrozenProcessesPath.Trim();
    private static string InstallWorkKey(string destination, string gameId) => DockerScripts.InstallWorkKey(destination, gameId);
    private Game[] ReserveInstallGames(IEnumerable<Game> games, string destination)
    {
        var candidates = games.Select(game => (game, key: InstallWorkKey(destination, game.Id))).ToArray();
        var accepted = installReservations.Reserve(candidates.Select(candidate => candidate.key)).ToHashSet(StringComparer.Ordinal);
        return candidates.Where(candidate => accepted.Contains(candidate.key)).Select(candidate => candidate.game).ToArray();
    }
    private void ReleaseInstallGames(IEnumerable<Game> games, string destination)
    {
        installReservations.Release(games.Select(game => InstallWorkKey(destination, game.Id)));
    }
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
                    string workKey = InstallWorkKey(destination, id);
                    if (installGates.TryGetValue(workKey, out var existingGate) && existingGate != null)
                    {
                        gate = existingGate;
                    }
                    else
                    {
                        gate = new SemaphoreSlim(1, 1);
                        installGates.Add(workKey, gate);
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
    internal void ReportUiFailure(string operation, Exception ex)
    {
        try { Store.Log(operation + ": " + ex); } catch { }
        if (closing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        try
        {
            // Operation failures stay non-modal so a stale process, network error,
            // or background task can never hold the entire library behind a dialog.
            StatusText.Text = operation + ": " + ex.Message;
        }
        catch (Exception reportError) { try { Store.Log(operation + " reporting failed: " + reportError); } catch { } }
    }
    private void Error(Exception ex) => ReportUiFailure("UI operation failed", ex);
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (!ready) { searchPending = true; return; } searchDelay.Stop(); searchDelay.Start(); }
    private void CategoryChanged(object sender, SelectionChangedEventArgs e) { if (!ready || CategoryList.SelectedItem is not Category category) return; tab = category.Id; State.Settings.LastTab = tab; Save(); ApplyFilter(); }
    private void FilterChanged(object sender, SelectionChangedEventArgs e) { if (!ready) return; State.Settings.SortBy = SortBox.SelectedItem as string ?? "Newest first"; Save(); ApplyFilter(); }
    private void InstalledFilterChanged(object sender, RoutedEventArgs e)
    {
        if (sender == InstalledOnlyFilter && InstalledOnlyFilter.IsChecked == true)
        {
            WithoutInstalledFilter.IsChecked = false;
            WandIncludedFilter.IsChecked = false;
        }
        else if (sender == WithoutInstalledFilter && WithoutInstalledFilter.IsChecked == true)
        {
            InstalledOnlyFilter.IsChecked = false;
            WandIncludedFilter.IsChecked = false;
        }
        else if (sender == WandIncludedFilter && WandIncludedFilter.IsChecked == true)
        {
            InstalledOnlyFilter.IsChecked = false;
            WithoutInstalledFilter.IsChecked = false;
        }
        if (ready) ApplyFilter();
    }
    // Kept as a stable test/automation entry point for older callers.
    private void InstalledOnlyChanged(object sender, RoutedEventArgs e) => InstalledFilterChanged(sender, e);
    private void SelectionChecked(object sender, RoutedEventArgs e) { if (!changingSelection) UpdateStats(); }
    private void GameSelectionChanged(object sender, SelectionChangedEventArgs e) { }
    private void SelectAll(object sender, RoutedEventArgs e) { changingSelection = true; try { foreach (var game in filtered) game.Selected = true; } finally { changingSelection = false; } UpdateStats(); }
    private void DeselectAll(object sender, RoutedEventArgs e) { changingSelection = true; try { foreach (var game in Games) game.Selected = false; } finally { changingSelection = false; } UpdateStats(); }
    private void ResetFilters(object sender, RoutedEventArgs e) { SearchBox.Text = ""; RatingBox.SelectedIndex = 0; TagBox.SelectedIndex = 0; InstalledOnlyFilter.IsChecked = false; WithoutInstalledFilter.IsChecked = false; WandIncludedFilter.IsChecked = false; ApplyFilter(); }
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
        if (!CanLaunchWithExistingWand(game, out var reason)) { StatusText.Text = reason; return; }
        ObserveUiOperation("Play with Wand", () => PlayWithWand(game));
    }
    private void ForceExitGameAndWandFromCard(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not Game game) return;
        ObserveUiAction("Force exit game and Wand", () => ForceExitGameAndWand(game));
    }
    private void RefreshCatalog(object sender, RoutedEventArgs e) => ObserveUiOperation("Catalog refresh", () => Refresh(true));
    internal async Task Refresh(bool full)
    {
        if (offline) { StatusText.Text = "Offline mode · shared edits stay on this PC until you restart online"; return; }
        if (!ready || refreshing || closing) return;
        if (full) lastCatalogRefresh = DateTime.UtcNow;
        refreshing = true; StatusText.Text = full ? "Refreshing catalog and Docker tags…" : "Checking shared changes…";
        var before = Sync.Effective(State).ToJsonString();
        try
        {
            await Sync.Refresh(State, full, adminToken, lifetime.Token);
            if (!closing)
            {
                if (full || before != Sync.Effective(State).ToJsonString()) Reload();
                await SynchronizeWandSupportedGamesAsync();
                StatusText.Text = Sync.Status;
            }
        }
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
        public required ActivePlaytime Timing { get; init; }
        public bool UsesWand { get; init; }
        public DateTime GraceUntilUtc { get; set; }
        public DateTime LastSavedUtc { get; set; }
        public HashSet<int> ObservedProcessIds { get; } = new();
        public Dictionary<int, string> ProcessCreationStamps { get; } = new();
        public bool PauseStateUnavailable { get; set; }
    }
    private bool StartPlaySession(Game game, Func<Process> start)
    {
        if (TryActivateExistingPlay(game)) return true;
        Process? launched = null;
        try
        {
            launched = start();
            bool tracked = TrackPlayProcess(game, launched, ownsProcess: true);
            launched = null;
            return tracked;
        }
        finally
        {
            if (launched != null) ReleaseUntrackedProcessHandle(launched, "direct launch tracking");
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
            if (launched != null) ReleaseUntrackedProcessHandle(launched, "direct async launch tracking");
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
    private void ForceExitGameAndWand(Game game)
    {
        Store.Log("User requested Exit game + Wand for " + game.Id + ".");
        if (!activePlays.TryGetValue(game.Id, out var session))
        {
            SetGamePlayState(game.Id, playing: false, paused: false);
            StatusText.Text = game.Name + " is no longer being tracked as running.";
            return;
        }

        bool gameStopped = false;
        try
        {
            if (session.Process.HasExited) gameStopped = true;
            else
            {
                gameStopped = RequestImmediateProcessTreeExit(session.Process);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            Store.Log("User-requested force exit could not stop " + game.Id + ": " + ex.Message);
        }

        if (gameStopped && activePlays.TryGetValue(game.Id, out var current) && ReferenceEquals(current, session))
        {
            CommitPlaySession(game.Id, session);
            Save();
        }

        // This method is reached only from the explicitly labelled destructive
        // control above; normal play tracking never closes Wand or another game.
        int closedWandProcesses = WandIntegration.ForceCloseRunningClient(Store);
        StatusText.Text = gameStopped
            ? "Force-closed " + game.Name + " and " + closedWandProcesses + " Wand process(es)."
            : "Could not confirm that " + game.Name + " exited; requested Wand shutdown for " + closedWandProcesses + " process(es).";
    }
    internal static bool RequestImmediateProcessTreeExit(Process process)
    {
        if (process.HasExited) return true;
        process.Kill(entireProcessTree: true);
        // Kill is an immediate OS request. Do not hold the UI for a process wait;
        // the explicit Exit action must also reach Wand without delay.
        return true;
    }
    private bool TrackPlayProcess(Game game, Process process, bool usesWand = false, bool ownsProcess = false)
    {
        try
        {
            process.Refresh();
            if (process.HasExited)
            {
                ReleaseUntrackedProcessHandle(process, "process exited before tracking");
                Store.Log("Game " + game.Id + " exited before play tracking could begin; no error dialog was shown.");
                return false;
            }
            process.EnableRaisingEvents = true;
            string executable;
            try { executable = process.MainModule?.FileName ?? State.LaunchPaths.GetValueOrDefault(game.Id, ""); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { executable = State.LaunchPaths.GetValueOrDefault(game.Id, ""); }
            int processId = process.Id;
            var now = DateTime.UtcNow;
            State.LastPlayedUtc[game.Id] = now;
            var session = new PlaySession { Process = process, ExecutablePath = executable, Timing = new ActivePlaytime(State.PlayTimeSeconds.GetValueOrDefault(game.Id), Stopwatch.GetTimestamp()), UsesWand = usesWand, GraceUntilUtc = now.AddSeconds(20), LastSavedUtc = now };
            session.ObservedProcessIds.Add(processId);
            if (FrozenProcessState.TryGetCreationStamp(process, out var creationStamp)) session.ProcessCreationStamps[processId] = creationStamp;
            activePlays[game.Id] = session;
            SetGamePlayState(game.Id, playing: true, paused: false);
            try { SamplePlaySession(game.Id, session, announceTransition: false); }
            catch (Exception ex) { Store.Log("Initial AHK pause-state sample failed for " + game.Id + "; playtime will continue from the next poll: " + ex.Message); }
            Store.Log("Launched game " + game.Id + "; tracking play time from PID " + processId);
            StatusText.Text = "Playing " + game.Name + " · tracking time";
            UpdateLastPlayed(game.Id, now);
            Save();
            ApplyFilter();
            return true;
        }
        catch (InvalidOperationException)
        {
            bool exited;
            try { exited = process.HasExited; }
            catch (InvalidOperationException) { exited = true; }
            if (exited)
            {
                ReleaseUntrackedProcessHandle(process, "process exited during tracking setup");
                Store.Log("Game " + game.Id + " exited during play tracking setup; the stale process race was ignored.");
                return false;
            }
            if (ownsProcess) ReleaseUntrackedProcessHandle(process, "play tracking");
            else { try { process.Dispose(); } catch { } }
            throw;
        }
        catch
        {
            if (ownsProcess) ReleaseUntrackedProcessHandle(process, "play tracking");
            else { try { process.Dispose(); } catch { } }
            throw;
        }
    }
    private void ReleaseUntrackedProcessHandle(Process process, string context)
    {
        try { process.Dispose(); }
        catch (Exception ex) { try { Store.Log("Could not release the untracked process handle after " + context + ": " + ex.Message); } catch { } }
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
                if (replacement != null)
                {
                    session.Process.Dispose(); session.Process = replacement; session.ObservedProcessIds.Add(replacement.Id);
                    if (FrozenProcessState.TryGetCreationStamp(replacement, out var replacementStamp)) session.ProcessCreationStamps[replacement.Id] = replacementStamp;
                }
                if (!session.Process.HasExited)
                {
                    stateKnown = true;
                    if (FrozenProcessState.TryGetCreationStamp(session.Process, out var creationStamp)) session.ProcessCreationStamps[session.Process.Id] = creationStamp;
                    SamplePlaySession(id, session, announceTransition: true);
                    if ((DateTime.UtcNow - session.LastSavedUtc).TotalSeconds >= 10) { session.LastSavedUtc = DateTime.UtcNow; save = true; }
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
    private void SamplePlaySession(string id, PlaySession session, bool announceTransition)
    {
        var identities = session.ProcessCreationStamps.Select(entry => new FrozenProcessIdentity(entry.Key, entry.Value));
        var pauseRead = FrozenProcessState.Read(FrozenProcessesPath, identities);
        bool paused = pauseRead.IsAvailable ? pauseRead.IsPaused : session.Timing.IsPaused;
        if (!pauseRead.IsAvailable && !session.PauseStateUnavailable)
        {
            session.PauseStateUnavailable = true;
            Store.Log("AHK pause state was temporarily unavailable for " + id + "; playtime is held at the last known state.");
        }
        else if (pauseRead.IsAvailable && session.PauseStateUnavailable)
        {
            session.PauseStateUnavailable = false;
            Store.Log("AHK pause state is available again for " + id + ".");
        }
        bool wasPaused = session.Timing.IsPaused;
        session.Timing.Sample(Stopwatch.GetTimestamp(), paused);
        State.PlayTimeSeconds[id] = session.Timing.TotalSeconds;
        SetGamePlayState(id, playing: true, paused: paused);
        UpdatePlayedLabel(id, State.PlayTimeSeconds[id]);
        if (announceTransition && pauseRead.IsAvailable && wasPaused != paused)
        {
            var game = Games.FirstOrDefault(candidate => candidate.Id == id);
            if (game != null) StatusText.Text = paused
                ? game.Name + " paused · play time held"
                : game.Name + " resumed · play time continues";
        }
    }
    private void CommitPlaySession(string id, PlaySession session)
    {
        try { SamplePlaySession(id, session, announceTransition: false); }
        catch (Exception ex) { Store.Log("Final AHK pause-state sample failed for " + id + "; preserving the last tracked playtime: " + ex.Message); }
        State.PlayTimeSeconds[id] = Math.Max(State.PlayTimeSeconds.GetValueOrDefault(id), session.Timing.TotalSeconds);
        activePlays.Remove(id); session.Process.Dispose(); SetGamePlayState(id, playing: false, paused: false); UpdatePlayedLabel(id, State.PlayTimeSeconds[id]);
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
    private void UpdateLastPlayed(string id, DateTime utc)
    {
        var game = Games.FirstOrDefault(g => g.Id == id); if (game == null) return;
        game.LastPlayedUtc = utc; game.Notify(nameof(Game.LastPlayedUtc));
    }
    private void SetGamePlayState(string id, bool playing, bool paused)
    {
        var game = Games.FirstOrDefault(candidate => candidate.Id == id); if (game == null) return;
        game.IsPlaying = playing; game.IsPlayPaused = playing && paused;
        game.Notify(nameof(Game.IsPlaying)); game.Notify(nameof(Game.IsPlayPaused)); game.Notify(nameof(Game.PlayLabel)); game.Notify(nameof(Game.PlayedMeta));
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
            var review = new EditorWindow(this, "Install selected games", $"{games.Length} game(s) → {State.Settings.MountPath}\nDownload size: {games.Sum(g => g.SizeGb):0.#} GB; {games.Count(g => g.SizeGb <= 0)} unknown. Existing files in each game's folder may be updated.\nChoose the Windows BAT route for your default terminal, or WSL2 Ubuntu for a native Bash .SH install.");
            var names = review.Paragraph(string.Join("\n", games.Select(g => g.Name))); names.MaxHeight = 220;
            var format = review.Choice("Install format", new[] { "Windows default terminal (.BAT)", "WSL2 Ubuntu (.SH)" }, State.Settings.ShellTarget == "wsl2" ? 1 : 0, "InstallFormat");
            review.Action("Start download", () =>
            {
                var launchGames = ReserveInstallGames(games, destination);
                if (launchGames.Length == 0)
                {
                    StatusText.Text = "Those games are already being installed. The existing operation is still in control of their files.";
                    review.Close();
                    return;
                }
                if (launchGames.Length != games.Length)
                    StatusText.Text = $"Skipped {games.Length - launchGames.Length} game(s) already being installed; starting the remaining {launchGames.Length}.";
                var launchIds = launchGames.Select(g => g.Id).ToArray();
                bool released = false;
                void ReleaseReservations()
                {
                    if (released) return;
                    released = true;
                    ReleaseInstallGames(launchGames, destination);
                }
                bool wsl2 = format.SelectedIndex == 1;
                string extension = wsl2 ? "sh" : "bat";
                try
                {
                    string script = DockerScripts.Generate(launchGames, State.Settings, extension, shellTarget: wsl2 ? "wsl2" : "native-linux");
                    var byId = launchGames.ToDictionary(g => g.Id, StringComparer.Ordinal);
                    var job = new JobWindow(Store, script, launchGames.Select(g => DockerScripts.ContainerNameForDestination(g.Id, destination)).ToArray(), openInDefaultTerminal: !wsl2, scriptExtension: extension, completionDestination: destination, completionGameIds: launchIds, acquireInstallation: cancellation => AcquireInstallScopeAsync(launchIds, destination, cancellation));
                    job.GameCompleted += id => byId.TryGetValue(id, out var game) ? ScanCompletedGame(game, destination, job.CompletionStartedAtUtc, job.OperationId) : Task.CompletedTask;
                    job.CompletedAsync += async success =>
                    {
                        try { await ScanCompletedDownloads(launchGames, destination, success, job.CompletionStartedAtUtc, job.OperationId); }
                        finally { ReleaseReservations(); }
                    };
                    jobs.Add(job); job.Closed += (_, _) => { jobs.Remove(job); ReleaseReservations(); }; job.Show(); review.Close();
                }
                catch
                {
                    ReleaseReservations();
                    throw;
                }
            });
            review.ShowDialog();
        }
        catch (Exception ex) { Error(ex); }
    }
    private async Task ScanConfiguredInstalledGamesAsync()
    {
        if (!ready || closing || installedScanning) return;
        await SynchronizeWandSupportedGamesAsync();
        string root = State.Settings.MountPath?.Trim() ?? "";
        if (root.Length == 0 || !Path.IsPathFullyQualified(root) || !Directory.Exists(root)) return;
        installedScanning = true;
        try
        {
            var snapshot = Games.Where(g => !g.IsLocal).Select(g => (g.Id, g.Name)).ToArray();
            var found = await Task.Run(() => InstalledScanner.Discover(root, snapshot, lifetime.Token), lifetime.Token);
            if (closing) return;
            bool changed = ApplyInstalledScan(found, logNotices: false);
            if (changed)
                StatusText.Text = $"Installed scan updated the saved library · {found.Games.Count:N0} playable game(s) found";
        }
        catch (OperationCanceledException) when (closing || lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Store.Log("Automatic installed scan failed: " + ex.Message);
            StatusText.Text = "Installed scan unavailable; saved installed games were preserved.";
        }
        finally { installedScanning = false; }
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
