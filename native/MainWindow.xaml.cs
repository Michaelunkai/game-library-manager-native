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
using System.Windows.Automation;
using Forms = System.Windows.Forms;

namespace GameLibrary.Native;

public sealed class CoverConverter : IValueConverter
{
    private const int FallbackWidth = 128;
    private const int FallbackHeight = 180;
    private const int MaxCachedCovers = 2500;
    internal const int MaxCachedCoverCount = MaxCachedCovers;
    /// <summary>Diagnostics for the cache-retention test.</summary>
    internal static int CachedCoverCount
    {
        get { lock (cache) return cache.Count; }
    }
    private static readonly Dictionary<string, ImageSource> cache = new(StringComparer.Ordinal);
    // LRU bookkeeping. Clearing the whole cache here would evict every cover on screen
    // at once, so the next scroll re-decodes (and, for content-addressed .img files,
    // re-hashes up to 8 MB each) synchronously on the UI thread. That is the periodic
    // multi-second stall while scrolling. Evicting only the coldest entries keeps the
    // working set warm and removes the cliff.
    private static readonly LinkedList<string> coverLru = new();
    private static readonly Dictionary<string, LinkedListNode<string>> coverLruIndex = new(StringComparer.Ordinal);

    private static void TrackUse(string key)
    {
        if (coverLruIndex.TryGetValue(key, out var node)) { coverLru.Remove(node); coverLru.AddFirst(node); return; }
        coverLruIndex[key] = coverLru.AddFirst(key);
        while (coverLru.Count > MaxCachedCovers)
        {
            var coldest = coverLru.Last;
            if (coldest is null) break;
            coverLru.RemoveLast();
            coverLruIndex.Remove(coldest.Value);
            cache.Remove(coldest.Value);
        }
    }

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var game = value as Game;
        var source = game != null ? game.Cover ?? string.Empty : value as string ?? string.Empty;
        var fallbackKey = game == null ? source : (string.IsNullOrWhiteSpace(game.Id) ? game.Name ?? string.Empty : game.Id);
        var cacheKey = game == null ? source : "game:" + fallbackKey + "\0" + source;
        cacheKey += "\0" + ArtworkFile.Revision(source);
        if (cache.TryGetValue(cacheKey, out var image)) { TrackUse(cacheKey); return image; }

        image = Load(source) ?? CreateFallback(fallbackKey);
        cache[cacheKey] = image;
        TrackUse(cacheKey);
        return image;
    }

    private static ImageSource? Load(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        if (Path.IsPathFullyQualified(source)) return ArtworkFile.Load(source);
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
    private List<Game> projectedCards = new();
    private object? projectionGames;
    private UserState? projectionState;
    private IReadOnlyDictionary<string, DateTimeOffset>? projectionPushTimes;
    private long projectionMutationVersion;
    private long projectionAppliedVersion;
    private bool projectionComputed;
    private List<Game> filtered = new();
    /// <summary>Prebuilt search index; rebuilt only when the catalog or state changes,
    /// so a keystroke is an integer lookup instead of an O(n) rescan with disk checks.</summary>
    private SearchPerformance.SearchIndex? searchIndex;
    /// <summary>
    /// Cheap fingerprint of the state collections the index searches. The catalog and
    /// state references do not change when a game is installed or a launch path is
    /// added, because those mutate the same instances in place, so the index is
    /// rebuilt whenever these counts move. Value-level edits are covered by Save(),
    /// which drops the index outright.
    /// </summary>
    private (int launch, int folders, int local, int playtime, int played) searchStateShape;
    private IReadOnlyDictionary<string, DateTimeOffset> authoritativePushTimes = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer installedScanPoll = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer wandRegistrationSyncDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer playtime = new() { Interval = TimeSpan.FromSeconds(1) };
    /// <summary>Debounces the scroll handler so metadata work only runs once scrolling stops.</summary>
    private readonly DispatcherTimer scrollIdle = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private void ScrollIdleElapsed(object? sender, EventArgs e)
    {
        scrollIdle.Stop();
        scrollIdle.Tick -= ScrollIdleElapsed;
        ScheduleMetadata();
    }
    private readonly DispatcherTimer progressPoll = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool progressRefreshing;
    private IReadOnlyList<GameProgressSnapshot> progressSnapshots = Array.Empty<GameProgressSnapshot>();
    private readonly DispatcherTimer searchDelay = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private readonly DispatcherTimer placementSaveDelay = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private DateTime lastCatalogRefresh;
    private Forms.NotifyIcon? tray;
    private bool ready, refreshing, closing, searchPending, installedScanning;
    private bool initializing, startupFailed;
    private bool changingSelection, catalogStatsDirty = true;
    private readonly bool offline;
    private readonly WindowPlacementRequest placementRequest;
    private readonly OfflineNetworkGuard? offlineNetwork;
    private bool startupPlacementDone;
    private bool placementUserChanged;
    private bool persistingPlacement;
    private bool applyingInitialPlacement;
    private bool updatingShowHiddenToggle;
    private CheckBox? showHiddenCategoriesToggle;
    private HwndSource? windowSource;
    private string tab = "all";
    private string? adminToken;
    private readonly List<JobWindow> jobs = new();
    private readonly List<InstallJobTerminalSession> durableInstallSessions = new();
    private readonly Dictionary<string, PlaySession> activePlays = new(StringComparer.Ordinal);
    private readonly HashSet<string> wandIncludedGameIds = new(StringComparer.Ordinal);
    private bool wandRegistrationSyncing;
    private bool wandRegistrationSyncPending;
    private readonly Dictionary<string, SemaphoreSlim> installGates = new(StringComparer.Ordinal);
    private readonly InstallReservationBook installReservations = new();
    // Direct Play and Play with Wand share one launch gate. This prevents a
    // direct launch from appearing between Wand's PID snapshot and its URI
    // handoff, where it could otherwise be mistaken for a Wand-owned process.
    private readonly SemaphoreSlim playLaunchGate = new(1, 1);
    internal bool IsAdmin => adminToken != null;
    internal bool IsClosing => closing;

    public MainWindow(LibraryStore store, bool offline = false, WindowPlacementRequest? placementRequest = null)
    {
        Store = store; this.offline = offline;
        // F1/F2/F3 adjust the running game's speed. The handler is fail-closed: with
        // no positively identified running game it does nothing at all, so the keys
        // never change the machine's state outside a game session.
        PreviewKeyDown += SpeedHotkeyPreview;
        this.placementRequest = placementRequest ?? WindowPlacement.Capture(Array.Empty<string>(), store.StatePath);
        offlineNetwork = offline ? new OfflineNetworkGuard() : null;
        Sync = new SyncClient(store, offlineNetwork);
        Program.SetCurrentProcessExplicitAppUserModelID("GameLibraryManager.Native");
        InitializeComponent();
        Width = Math.Max(MinWidth, this.placementRequest.SavedWidthDip);
        Height = Math.Max(MinHeight, this.placementRequest.SavedHeightDip);
        AddShowHiddenCategoriesToggle();
        progressPoll.Tick += async (_, _) => await RefreshProgress();
        Style = (Style)System.Windows.Application.Current.FindResource(typeof(Window));
        SourceInitialized += OnWindowSourceInitialized;
        Loaded += OnLoaded;
        LocationChanged += (_, _) => WindowGeometryChanged();
        SizeChanged += (_, _) => WindowGeometryChanged();
        placementSaveDelay.Tick += (_, _) => { placementSaveDelay.Stop(); PersistWindowPlacement(); };
        wandRegistrationSyncDelay.Tick += WandRegistrationSyncDelayTick;
        WandLiveLibrary.RegistrationSetChanged += OnWandRegistrationSetChanged;
        Closing += OnClosing;
        StateChanged += (_, _) => ObserveUiAction("Window state change", () =>
        {
            if (!ready || WindowState != WindowState.Minimized || !State.Settings.MinimizeToTray) return;
            HideToTray();
        });
        PreviewKeyDown += Keyboard;
        poll.Tick += (_, _) => ObserveUiOperation("Catalog poll", () => Refresh(DateTime.UtcNow - lastCatalogRefresh >= TimeSpan.FromSeconds(15)));
        installedScanPoll.Tick += (_, _) => ObserveUiOperation("Installed game scan", ScanConfiguredInstalledGamesAsync);
        playtime.Tick += (_, _) => ObserveUiAction("Play-time update", UpdatePlaySessions);
        searchDelay.Tick += (_, _) => ObserveUiAction("Search filter", () => { searchDelay.Stop(); ApplyFilter(); });
        // ScrollChanged fires on every pixel of movement. Dispatching metadata work on
        // each tick interleaves UI-thread work with scrolling and shows up as stutter,
        // so the request is debounced and only honoured once scrolling settles.
        GameList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, args) =>
        {
            if (args is null || args.VerticalChange == 0) return;
            scrollIdle.Stop();
            scrollIdle.Tick += ScrollIdleElapsed;
            scrollIdle.Start();
        }));
    }
    private void OnLoaded(object sender, RoutedEventArgs e) => ObserveUiOperation("Startup", InitializeAsync);
    private void OnWandRegistrationSetChanged(object? sender, EventArgs e) => RequestWandRegistrationSync();
    private void RequestWandRegistrationSync()
    {
        if (closing || Program.TestReport != null) return;
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try { Dispatcher.BeginInvoke(new Action(RequestWandRegistrationSync)); }
            catch (InvalidOperationException) { }
            return;
        }
        wandRegistrationSyncPending = true;
        SchedulePendingWandRegistrationSync();
    }
    private void SchedulePendingWandRegistrationSync()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try { Dispatcher.BeginInvoke(new Action(SchedulePendingWandRegistrationSync)); }
            catch (InvalidOperationException) { }
            return;
        }
        if (!wandRegistrationSyncPending || !ready || initializing || closing || Program.TestReport != null
            || wandRegistrationSyncing || wandRegistrationSyncDelay.IsEnabled) return;
        wandRegistrationSyncDelay.Start();
    }
    private async void WandRegistrationSyncDelayTick(object? sender, EventArgs e)
    {
        wandRegistrationSyncDelay.Stop();
        if (!wandRegistrationSyncPending || !ready || initializing || closing || Program.TestReport != null
            || wandRegistrationSyncing) return;
        wandRegistrationSyncPending = false;
        await SynchronizeWandSupportedGamesAsync();
        SchedulePendingWandRegistrationSync();
    }
    private async Task InitializeAsync()
    {
        if (initializing || ready || closing) return;
        initializing = true; startupFailed = false;
        LibraryContent.IsEnabled = false;
        StartupRecovery.Visibility = Visibility.Collapsed;
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
            tab = State.Settings.LastTab;
            SortBox.ItemsSource = new[] { "Name A–Z", "Name Z–A", "Time to Beat (Low–High)", "Time to Beat (High–Low)", "Recently Added", "Recently Played", "Oldest First", "Rating (High–Low)", "Rating (Low–High)", "Size (Small–Large)", "Size (Large–Small)", "Category" };
            SortBox.SelectedItem = DefaultSortForTab(tab, State.Settings.SortBy);
            if (SortBox.SelectedIndex < 0) SortBox.SelectedIndex = 4;
            RatingBox.ItemsSource = new[] { "Any rating", "1+ stars", "2+ stars", "3+ stars", "4+ stars", "5 stars" }; RatingBox.SelectedIndex = 0;
            lifetime.Token.ThrowIfCancellationRequested();
            InitializeTray(); ApplyTheme(); ready = true; Reload();
            LibraryContent.IsEnabled = true;
            if (showHiddenCategoriesToggle != null)
            {
                updatingShowHiddenToggle = true;
                showHiddenCategoriesToggle.IsChecked = State.Settings.ShowHiddenCategories;
                updatingShowHiddenToggle = false;
            }
            if (placementUserChanged) PersistWindowPlacement();
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
            foreach (var pending in State.PendingGameBackups.ToArray())
                _ = RunGameSave(pending.Key, Games.FirstOrDefault(g => g.Id == pending.Key)?.Name ?? pending.Key, pending.Value, false);
            if (!closing) { progressPoll.Start(); _ = RefreshProgress(); }
            if (!closing) installedScanPoll.Start();
        }
        catch (OperationCanceledException) when (closing || lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Error(ex);
            if (!LibraryContent.IsEnabled && !closing)
            {
                ready = false; startupFailed = true;
                StartupRecovery.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            initializing = false;
            SchedulePendingWandRegistrationSync();
        }
    }
    private void OnWindowSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        windowSource = HwndSource.FromHwnd(handle);
        windowSource?.AddHook(WindowMessage);
        try
        {
            applyingInitialPlacement = true;
            startupPlacementDone = WindowPlacement.ApplyInitial(this, placementRequest);
        }
        catch (Exception ex) { Store.Log("Initial monitor placement failed: " + ex); }
        finally { applyingInitialPlacement = false; }
        Store.Log($"Initial window monitor selected from {placementRequest.SelectionSource}: {placementRequest.PreferredMonitorDeviceName}");
    }

    private IntPtr WindowMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmDisplayChange = 0x007E;
        if (message == WmDisplayChange && !closing && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(RecoverWindowAfterDisplayChange));
        return IntPtr.Zero;
    }

    private void RecoverWindowAfterDisplayChange()
    {
        if (closing || !startupPlacementDone) return;
        try
        {
            if (!WindowPlacement.RecoverIfInaccessible(new WindowInteropHelper(this).Handle, out var snapshot) || snapshot == null) return;
            placementUserChanged = true;
            var size = WindowPlacement.ToDip(snapshot);
            if (WindowState == WindowState.Normal) { Width = size.WidthDip; Height = size.HeightDip; }
            SaveWindowPlacement(snapshot, size.WidthDip, size.HeightDip);
        }
        catch (Exception ex) { Store.Log("Disconnected-monitor recovery failed: " + ex); }
    }

    private void WindowGeometryChanged()
    {
        if (!startupPlacementDone || applyingInitialPlacement || persistingPlacement || WindowState != WindowState.Normal || closing) return;
        placementUserChanged = true;
        placementSaveDelay.Stop();
        placementSaveDelay.Start();
    }

    private void PersistWindowPlacement()
    {
        if (!ready || closing || WindowState != WindowState.Normal) return;
        var snapshot = WindowPlacement.ReadWindow(new WindowInteropHelper(this).Handle);
        if (snapshot == null) return;
        var size = WindowPlacement.ToDip(snapshot);
        SaveWindowPlacement(snapshot, size.WidthDip, size.HeightDip);
    }

    private void SaveWindowPlacement(WindowPlacementSnapshot snapshot, double widthDip, double heightDip)
    {
        if (!ready) return;
        persistingPlacement = true;
        try
        {
            State.Settings.WindowWidth = widthDip;
            State.Settings.WindowHeight = heightDip;
            State.Settings.WindowMonitorDeviceName = snapshot.MonitorDeviceName;
            State.Settings.WindowBoundsLeftPixels = snapshot.Bounds.X;
            State.Settings.WindowBoundsTopPixels = snapshot.Bounds.Y;
            State.Settings.WindowBoundsWidthPixels = snapshot.Bounds.Width;
            State.Settings.WindowBoundsHeightPixels = snapshot.Bounds.Height;
            Save();
        }
        finally { persistingPlacement = false; }
    }

    private void AddShowHiddenCategoriesToggle()
    {
        if (CategoryList.Parent is not DockPanel sidebar) return;
        showHiddenCategoriesToggle = new CheckBox
        {
            Content = "Show hidden categories",
            Margin = new Thickness(4, 0, 4, 10),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Temporarily show category tabs marked Hide tab in Manage categories."
        };
        AutomationProperties.SetAutomationId(showHiddenCategoriesToggle, "ShowHiddenCategories");
        showHiddenCategoriesToggle.Checked += ShowHiddenCategoriesChanged;
        showHiddenCategoriesToggle.Unchecked += ShowHiddenCategoriesChanged;
        DockPanel.SetDock(showHiddenCategoriesToggle, Dock.Top);
        sidebar.Children.Insert(Math.Max(0, sidebar.Children.IndexOf(CategoryList)), showHiddenCategoriesToggle);
    }

    private void ShowHiddenCategoriesChanged(object sender, RoutedEventArgs e)
    {
        if (updatingShowHiddenToggle || !ready || showHiddenCategoriesToggle == null) return;
        State.Settings.ShowHiddenCategories = showHiddenCategoriesToggle.IsChecked == true;
        Save();
        Reload();
    }
    private void InitializeTray()
    {
        if (tray != null) return;
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
        if (gameSaveOperations.Count > 0)
        {
            System.Windows.MessageBox.Show(this, "A game backup or restore is still running. Wait for it to finish before exiting.", "Save operation in progress", MessageBoxButton.OK, MessageBoxImage.Information);
            e.Cancel = true; return;
        }
        if (jobs.Any(j => j.Busy))
        {
            System.Windows.MessageBox.Show(this, "A download is still running. Stop it in its progress window before exiting.", "Download in progress", MessageBoxButton.OK, MessageBoxImage.Information);
            e.Cancel = true; return;
        }
        // Persist before cancelling timers or releasing tracked processes. A full,
        // disconnected or locked profile must leave the window available to retry.
        if (ready)
        {
            try
            {
                placementSaveDelay.Stop();
                if (WindowState == WindowState.Normal) PersistWindowPlacement();
                else { State.Settings.WindowWidth = RestoreBounds.Width; State.Settings.WindowHeight = RestoreBounds.Height; }
                foreach (var entry in activePlays.ToArray()) SamplePlaySession(entry.Key, entry.Value, announceTransition: false);
                Save();
            }
            catch (Exception ex)
            {
                e.Cancel = true;
                ReportUiFailure("Cannot close until library changes are saved", ex);
                return;
            }
        }
        closing = true;
        WandLiveLibrary.RegistrationSetChanged -= OnWandRegistrationSetChanged;
        wandRegistrationSyncDelay.Stop();
        try
        {
            try { lifetime.Cancel(); }
            catch (Exception ex) { try { Store.Log("Shutdown cancellation callbacks failed; continuing cleanup: " + ex); } catch { } }
            if (ready)
            {
                if (WindowState != WindowState.Normal)
                { State.Settings.WindowWidth = RestoreBounds.Width; State.Settings.WindowHeight = RestoreBounds.Height; }
                FlushPlaySessions();
                Save();
            }
        }
        catch (Exception ex) { Store.Log("Shutdown state flush failed: " + ex); }
        poll.Stop(); installedScanPoll.Stop(); wandRegistrationSyncDelay.Stop(); searchDelay.Stop(); playtime.Stop(); progressPoll.Stop(); placementSaveDelay.Stop();
        windowSource?.RemoveHook(WindowMessage);
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
        PathExistsCache.Clear();
        catalogStatsDirty = true;
        var selected = Games.Where(g => g.Selected).Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
        var config = Sync.Effective(State);
        if (CategoryVisibility.EnsureMigrated(Store, State, config)) config = Sync.Effective(State);
        Games = Store.LoadGames(State, config);
        authoritativePushTimes = ReadAuthoritativePushTimes();
        ApplyProgress();
        ApplyInstalledStorage();
        if (Program.TestReport == null) _ = RefreshInstalledStorage();
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
        var categories = EffectiveCategories(config);
        bool changedLastTab = false;
        if (tab is not ("all" or "wishlist" or "installed")
            && CategoryVisibility.Get(State, config, tab).HideTab
            && State.Settings.ShowHiddenCategories != true)
        {
            tab = "all";
            State.Settings.LastTab = tab;
            changedLastTab = true;
        }
        var choices = new List<Category> { new("all", "◈  All games"), new("wishlist", "♡  Wishlist"), new("installed", "▣  Installed") };
        choices.AddRange(categories.Where(c => c.Id is not ("all" or "wishlist" or "installed")
            && (State.Settings.ShowHiddenCategories || !CategoryVisibility.Get(State, config, c.Id).HideTab)));
        if (!choices.Any(c => c.Id == tab)) { tab = "all"; State.Settings.LastTab = tab; changedLastTab = true; }
        CategoryList.ItemsSource = choices;
        CategoryList.SelectedItem = choices.First(c => c.Id == tab);
        if (showHiddenCategoriesToggle != null)
        {
            updatingShowHiddenToggle = true;
            showHiddenCategoriesToggle.IsChecked = State.Settings.ShowHiddenCategories;
            updatingShowHiddenToggle = false;
        }
        var tag = TagBox.SelectedItem as string;
        TagBox.ItemsSource = new[] { "All tags" }.Concat(State.GameTags.Values.SelectMany(t => t).Distinct().OrderBy(t => t)).ToArray();
        TagBox.SelectedItem = tag ?? "All tags"; if (TagBox.SelectedIndex < 0) TagBox.SelectedIndex = 0;
        if (changedLastTab) Save();
        ApplyFilter();
        RequestWandRegistrationSync();
    }
    internal List<Category> EffectiveCategories(JsonObject? config = null)
    {
        config ??= Sync.Effective(State);
        return CategoryVisibility.CompleteDefinitions(Store.LoadCategories(config).Concat(Store.LoadCategories(new JsonObject())), config, State);
    }
    private void RefreshWandPlayAvailability()
    {
        projectionMutationVersion++;
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
        var known = CatalogIdentity.Known(game.Id);
        if (known != null && !MetadataClient.SameTitle(known.Value.Title, registration.Name)) return 0;
        bool exactName = WandIntegration.Normalize(game.Name) == WandIntegration.Normalize(registration.Name);
        bool exactPath = !string.IsNullOrWhiteSpace(savedLaunchPath)
            && string.Equals(savedLaunchPath, registration.Path, StringComparison.OrdinalIgnoreCase);
        if (exactPath && string.Equals(game.Id, "wand:" + registration.GameId, StringComparison.Ordinal)) return 200_000;
        bool exactFolder = string.Equals(game.Id, registration.Folder, StringComparison.OrdinalIgnoreCase);
        bool normalizedFolder = WandIntegration.Normalize(game.Id) == WandIntegration.Normalize(registration.Folder);
        if (exactPath) return 100_000 + (exactName ? 1_000 : 0) + (exactFolder ? 200 : normalizedFolder ? 100 : 0);
        if (exactName && string.Equals(game.Id, registration.Folder, StringComparison.Ordinal)) return 98_000;
        if (exactName && string.Equals(game.Id, registration.Folder, StringComparison.OrdinalIgnoreCase)) return 96_000;
        if (exactName && normalizedFolder) return 94_000;
        if (exactName) return 90_000;
        // A folder-shaped catalog id is not sufficient by itself. The catalog
        // contains collisions such as Ashen/Ashen Empires and Tails of Iron/II;
        // mapping on the id alone would put Wand on the wrong game card.
        return 0;
    }
    internal static Game? ResolveWandRegistrationMatch(IEnumerable<Game> games, UserState state, WandSupportedGame registration, ISet<string>? used = null)
    {
        var candidates = games
            .Where(game => used == null || !used.Contains(game.Id))
            .Select(game => new { Game = game, Score = WandLibraryMatchScore(game, registration, state.LaunchPaths.GetValueOrDefault(game.Id)) })
            .Where(candidate => candidate.Score > 0)
            .ToArray();
        if (candidates.Length == 0) return null;
        int bestScore = candidates.Max(candidate => candidate.Score);
        var best = candidates.Where(candidate => candidate.Score == bestScore).ToArray();
        return best.Length == 1 ? best[0].Game : null;
    }
    private Func<IReadOnlyList<WandSupportedGame>>? wandRegistrationProof;
    private async Task SynchronizeWandSupportedGamesAsync()
    {
        if (closing || wandRegistrationSyncing || (Program.TestReport != null && wandRegistrationProof == null)) return;
        wandRegistrationSyncPending = false;
        wandRegistrationSyncDelay.Stop();
        wandRegistrationSyncing = true;
        try
        {
            var registrations = await Task.Run(() => wandRegistrationProof?.Invoke() ?? WandIntegration.LoadSupportedGames(), lifetime.Token);
            if (closing) return;
            var used = new HashSet<string>(StringComparer.Ordinal);
            var included = new HashSet<string>(StringComparer.Ordinal);
            bool registrationsChanged = false;
            foreach (var registration in registrations)
            {
                var match = ResolveWandRegistrationMatch(Games, State, registration, used);
                if (match == null)
                {
                    string runtimeId = "wand:" + registration.GameId;
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
                if (match.Id.StartsWith("wand:", StringComparison.Ordinal))
                {
                    string folder = Path.GetDirectoryName(registration.Path)!;
                    if (!State.WandGames.TryGetValue(match.Id, out var savedWand))
                    {
                        State.WandGames[match.Id] = new LocalGame { Name = registration.Name, Folder = folder, Category = "installed" };
                        registrationsChanged = true;
                    }
                    else if (savedWand.Name != registration.Name || !string.Equals(savedWand.Folder, folder, StringComparison.OrdinalIgnoreCase))
                    { savedWand.Name = registration.Name; savedWand.Folder = folder; registrationsChanged = true; }
                }
                used.Add(match.Id);
                included.Add(match.Id);
                if (!string.Equals(match.Name, registration.Name, StringComparison.Ordinal))
                {
                    match.Name = registration.Name;
                    match.Notify(nameof(Game.Name));
                }
                if (!string.Equals(State.LaunchPaths.GetValueOrDefault(match.Id), registration.Path, StringComparison.OrdinalIgnoreCase))
                { State.LaunchPaths[match.Id] = registration.Path; registrationsChanged = true; }
                registrationsChanged |= State.InstalledGames.Add(match.Id);
                match.Installed = File.Exists(registration.Path);
                match.CanPlayWithWand = true;
                match.Notify("");
            }
            wandIncludedGameIds.Clear();
            wandIncludedGameIds.UnionWith(included);
            if (registrationsChanged) Save();
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
            if (WandIncludedFilter.IsChecked == true) StatusText.Text = "Wand library refresh unavailable · showing the last successful list";
        }
        finally
        {
            wandRegistrationSyncing = false;
            SchedulePendingWandRegistrationSync();
        }
    }
    private bool CanLaunchWithExistingWand(Game game, out string message)
    {
        message = "This game is not in the latest Wand included registration list.";
        if (!game.CanPlayWithWand || ResolveWandCardTarget(game) == null) return false;
        message = string.Empty;
        return true;
    }
    private Game? ResolveWandCardTarget(Game card)
    {
        return GameCardProjection.SelectWandSource(card, State, wandIncludedGameIds);
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
            SortBox.SelectedItem = DefaultSortForTab(tab, State.Settings.SortBy);
            if (SortBox.SelectedIndex < 0) SortBox.SelectedIndex = 4;
        }
        finally { ready = true; }
        ApplyTheme(); Reload(); Save();
    }
    private static HashSet<string> Hidden(JsonObject config) => config["hiddenTabs"] is JsonArray a ? a.Select(n => DataJson.Text(n)).ToHashSet(StringComparer.Ordinal) : new();
    private IReadOnlyDictionary<string, DateTimeOffset> ReadAuthoritativePushTimes()
    {
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        try
        {
            var snapshot = JsonNode.Parse(File.ReadAllText(Path.Combine(Store.Cache, "docker-tags.json"))) as JsonObject;
            string repository = State.Settings.DockerUsername + "/" + State.Settings.RepoName;
            if (snapshot == null || !SyncClient.CompleteTagSnapshot(snapshot, repository) || snapshot["tags"] is not JsonArray tags) return result;
            string verificationStamp = DataJson.Text(snapshot["checkedAt"], DataJson.Text(snapshot["fetchedAt"]));
            if (!DateTimeOffset.TryParse(verificationStamp, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var verifiedAt)) return result;
            var age = DateTimeOffset.UtcNow - verifiedAt.ToUniversalTime();
            if (age < TimeSpan.Zero || age >= TimeSpan.FromMinutes(15)) return result;
            foreach (var tag in tags)
            {
                string name = DataJson.Text(tag?["name"]);
                if (!DockerScripts.ValidTag(name) || !DateTimeOffset.TryParse(DataJson.Text(tag?["last_updated"]),
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var pushedAt)) continue;
                result[DockerIdentity.Create(repository, name)] = pushedAt.ToUniversalTime();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException) { }
        return result;
    }
    internal void ApplyFilter()
    {
        if (!ready) return;
        // The identity projection depends only on Games, State and push times.
        // Pure view changes (search text, sort, filters, tab) reuse the last
        // projection, so a keystroke no longer re-runs the O(n) identity index
        // with per-game disk checks on the UI thread.
        bool recompute = !projectionComputed || !ReferenceEquals(Games, projectionGames)
            || !ReferenceEquals(State, projectionState)
            || !ReferenceEquals(authoritativePushTimes, projectionPushTimes)
            || projectionMutationVersion != projectionAppliedVersion;
        if (recompute || searchIndex is null || searchIndex.IsStale || SearchStateShape() != searchStateShape)
        {
            GameCardProjection.Detach(projectedCards);
            projectedCards = GameCardProjection.ProjectCards(Games, State,
                new GameIdentityIndex(Games, State, includeDisplayName: true), authoritativePushTimes).ToList();
            projectionGames = Games; projectionState = State; projectionPushTimes = authoritativePushTimes;
            projectionAppliedVersion = projectionMutationVersion; projectionComputed = true;
            // The search index is built in the same pass: it depends on exactly the
            // same inputs, so a catalog push costs one build instead of one build
            // per keystroke. The previous result is dropped so nothing stale survives.
            searchIndex = SearchPerformance.Build(projectedCards, State, SearchPerformance.TagsFrom(State));
            searchStateShape = SearchStateShape();
        }
        bool wandOnly = WandIncludedFilter.IsChecked == true;
        var config = Sync.Effective(State);
        IEnumerable<Game> visible = wandOnly ? projectedCards.Where(g => g.CanPlayWithWand) : projectedCards;
        string query = SearchBox.Text.Trim();
        // Search resolves through the prebuilt index when one exists, and falls back
        // to the original scan otherwise. The predicate occupies the same position in
        // the filter chain as the old scan did, so ordering and every other filter are
        // untouched; only the per-card cost changes. Identical queries resolve to a
        // cached result, which is what removes the visible stall on a keystroke.
        // Category hiding from All games applies to searches launched from All
        // games. Explicit category, Installed, Wishlist, and Wand views keep
        // their own independently selected scope.
        if (!wandOnly && tab == "all")
            visible = visible.Where(g => !CategoryVisibility.Get(State, config, g.Category).HideGamesFromAll);
        if (!wandOnly && tab == "installed") visible = visible.Where(g => g.Installed);
        else if (!wandOnly && query.Length == 0)
        {
            if (tab == "wishlist") visible = visible.Where(g => g.Wishlisted);
            else if (tab != "all") visible = visible.Where(g => g.Category == tab);
        }
        if (query.Length > 0) visible = visible.Where(searchIndex is null
            ? new Func<Game, bool>(g => MatchesSearch(g, query))
            : SearchPerformance.GameMatchPredicate(searchIndex, query));
        if (InstalledOnlyFilter.IsChecked == true) visible = visible.Where(g => g.Installed);
        else if (WithoutInstalledFilter.IsChecked == true) visible = visible.Where(g => !g.Installed);
        else if (wandOnly) visible = visible.Where(g => g.CanPlayWithWand);
        int rating = Math.Max(0, RatingBox.SelectedIndex); visible = visible.Where(g => g.Rating >= rating);
        string tag = TagBox.SelectedItem as string ?? "All tags";
        if (tag != "All tags") visible = visible.Where(g => g.SourceRecords.Count > 1
            ? g.SourceRecords.Any(source => State.GameTags.GetValueOrDefault(source.Id, new()).Contains(tag))
            : State.GameTags.GetValueOrDefault(g.Id, new()).Contains(tag));
        string maximumText = MaxSizeFilter.Text.Trim();
        bool maximumActive = double.TryParse(maximumText, NumberStyles.Float, CultureInfo.CurrentCulture, out double maxDownloadGb)
            || double.TryParse(maximumText, NumberStyles.Float, CultureInfo.InvariantCulture, out maxDownloadGb);
        if (maximumText.Length > 0 && maximumActive && double.IsFinite(maxDownloadGb) && maxDownloadGb >= 0)
            visible = visible.Where(g => WithinMaxDownloadSize(g, maxDownloadGb));
        string sort = SortBox.SelectedItem as string ?? "Recently Added";
        visible = tab == "installed" ? SortInstalledGames(visible, sort) : SortGames(visible, sort);
        filtered = visible.ToList();
        GameList.ItemsSource = filtered;
        EmptyState.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = wandOnly ? (query.Length > 0 ? "Search Wand games" : "Wand included")
            : query.Length > 0 ? (tab == "installed" || InstalledOnlyFilter.IsChecked == true ? "Search installed games" : "Search all games")
            : (CategoryList.SelectedItem as Category)?.Name.Replace("◈  ", "").Replace("♡  ", "").Replace("▣  ", "") ?? "All games";
        CatalogCaption.Text = wandOnly ? $"{wandIncludedGameIds.Count:N0} games in your current Wand library · {filtered.Count:N0} shown"
            : $"{Games.Count:N0} source entries · {projectedCards.Count:N0} identity cards · Search, organize, and play";
        if (maximumText.Length > 0)
            CatalogCaption.Text += maximumActive && double.IsFinite(maxDownloadGb) && maxDownloadGb >= 0
                ? $" · max {maxDownloadGb:0.##} GB download (unknown sizes excluded)"
                : " · enter a valid nonnegative maximum download GB";
        UpdateStats();
        ScheduleMetadata();
    }
    internal static bool WithinMaxDownloadSize(Game game, double maxGb) =>
        double.IsFinite(maxGb) && maxGb >= 0 && double.IsFinite(game.SizeGb) && game.SizeGb > 0 && game.SizeGb <= maxGb;
    internal static IEnumerable<Game> SortGames(IEnumerable<Game> source, string sort)
    {
        static IOrderedEnumerable<Game> NameAsc(IEnumerable<Game> games) => games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.Id, StringComparer.Ordinal);
        static bool HasTime(Game g) => double.IsFinite(g.Time) && g.Time > 0;
        static bool HasSize(Game g) => double.IsFinite(g.SizeGb) && g.SizeGb > 0;
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
            "Size (Small–Large)" => source.OrderBy(g => HasSize(g) ? 0 : 1).ThenBy(g => HasSize(g) ? g.SizeGb : double.MaxValue).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            "Size (Large–Small)" => source.OrderBy(g => HasSize(g) ? 0 : 1).ThenByDescending(g => HasSize(g) ? g.SizeGb : double.MinValue).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
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
            game.SourceRecords.Any(source => Contains(source.Id) || Contains(source.Name) || Contains(source.DockerImage)) ||
            (State.LaunchPaths.TryGetValue(game.Id, out var launcher) && Contains(launcher)) ||
            (State.InstallationFolders.TryGetValue(game.Id, out var installationFolder) && Contains(installationFolder)) ||
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
        AverageTime.ToolTip = $"Average completion-time estimate across {times.Length:N0} catalog games with a positive time; games without estimates are excluded. Search and filters do not change this statistic.";
        CoverCount.Text = Games.Count(g => !string.IsNullOrWhiteSpace(g.Cover) && PathExistsCache.Check(g.Cover)).ToString("N0");
        CoverCount.ToolTip = "Games in the complete catalog with a cover file cached on this PC. Search and filters do not change this statistic.";
        catalogStatsDirty = false;
        }
        var selected = Selected();
        SelectedSize.Text = $"{selected.Sum(g => g.SizeGb):0.#} GB";
        SelectedSize.ToolTip = $"{selected.Length} selected; {selected.Count(g => g.SizeGb <= 0)} without download-size data";
    }
    internal void Save()
    {
        if (!ready) throw new InvalidOperationException("Your saved library has not finished loading. Retry loading or import a complete backup before editing.");
        // Every state mutation is persisted through this method, and those mutations
        // happen in place on the same State instance, so reference-change detection
        // cannot see them. Dropping the search index here is what keeps a newly added
        // launch path, install folder or local game searchable on the very next
        // keystroke instead of only after the next catalog push.
        searchIndex = null;
        try { Store.Save(State); }
        catch (Exception ex)
        {
            var failure = new IOException("Changes could not be saved on this PC. Keep the app open and retry after resolving the storage problem. " + ex.Message, ex);
            Error(failure);
            throw failure;
        }
    }
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
    internal static string DefaultSortForTab(string category, string savedSort) => category == "installed" ? "Recently Played" : savedSort;
    private void CategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || CategoryList.SelectedItem is not Category category) return;
        bool changed = tab != category.Id;
        tab = category.Id;
        State.Settings.LastTab = tab;
        if (changed) SortBox.SelectedItem = DefaultSortForTab(tab, State.Settings.SortBy);
        Save(); ApplyFilter();
    }
    private void FilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready) return;
        // Installed always starts with recency; retain the user's catalog sort
        // when entering/leaving it instead of changing their other views.
        if (tab != "installed") State.Settings.SortBy = SortBox.SelectedItem as string ?? "Recently Added";
        Save(); ApplyFilter();
    }
    private void MaxSizeChanged(object sender, TextChangedEventArgs e) { if (ready) ApplyFilter(); }
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
            if (ready) ObserveUiOperation("Refresh Wand library", SynchronizeWandSupportedGamesAsync);
        }
        if (ready) ApplyFilter();
    }
    // Kept as a stable test/automation entry point for older callers.
    private void InstalledOnlyChanged(object sender, RoutedEventArgs e) => InstalledFilterChanged(sender, e);
    private static void SetCardSelection(Game card, bool selected)
    {
        card.Selected = selected;
        if (card.SourceRecords.Count == 0)
        {
            (card.ActionTarget ?? card).Selected = selected;
            return;
        }
        var action = card.ActionTarget ?? card;
        foreach (var source in card.SourceRecords) source.Selected = selected && ReferenceEquals(source, action);
    }
    private void SelectionChecked(object sender, RoutedEventArgs e)
    {
        if (changingSelection) return;
        if (sender is FrameworkElement { DataContext: Game card }) SetCardSelection(card, card.Selected);
        UpdateStats();
    }
    private void GameSelectionChanged(object sender, SelectionChangedEventArgs e) { }
    private void SelectAll(object sender, RoutedEventArgs e) { changingSelection = true; try { foreach (var game in filtered) SetCardSelection(game, true); } finally { changingSelection = false; } UpdateStats(); }
    private void SelectCurrentCategory(object sender, RoutedEventArgs e)
    {
        changingSelection = true;
        try { foreach (var game in filtered) SetCardSelection(game, true); }
        finally { changingSelection = false; }
        UpdateStats();
        StatusText.Text = "Selected " + filtered.Count + " visible games in " + (CategoryList.SelectedItem as Category)?.Name + ".";
    }
    private void RandomVisibleGame(object sender, RoutedEventArgs e)
    {
        var eligible = filtered.Where(game => !game.IsNonGame && !game.RequiresGameIdentity).ToArray();
        if (eligible.Length == 0) { StatusText.Text = "No eligible visible game to pick."; return; }
        var chosen = eligible[RandomNumberGenerator.GetInt32(eligible.Length)];
        GameList.SelectedItem = chosen;
        GameList.ScrollIntoView(chosen);
        StatusText.Text = "Random pick: " + chosen.Name;
    }
    private void DeselectAll(object sender, RoutedEventArgs e) { changingSelection = true; try { foreach (var game in Games) game.Selected = false; foreach (var card in projectedCards) card.Selected = false; } finally { changingSelection = false; } UpdateStats(); }
    private void ResetFilters(object sender, RoutedEventArgs e) { SearchBox.Text = ""; MaxSizeFilter.Text = ""; RatingBox.SelectedIndex = 0; TagBox.SelectedIndex = 0; InstalledOnlyFilter.IsChecked = false; WithoutInstalledFilter.IsChecked = false; WandIncludedFilter.IsChecked = false; ApplyFilter(); }
    private void ToggleWishlist(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not Game card) return;
        var ids = card.SourceRecords.Count > 1 ? card.SourceRecords.Select(source => source.Id).Distinct(StringComparer.Ordinal).ToArray() : new[] { card.Id };
        bool remove = ids.Any(State.Wishlist.Contains);
        foreach (string id in ids)
        {
            if (remove) State.Wishlist.Remove(id);
            else State.Wishlist.Add(id);
        }
        Save(); Reload();
    }
    private void GameDoubleClick(object sender, MouseButtonEventArgs e) { if (GameList.SelectedItem is Game game) Details(game); }
    private void OpenGameDetails(object sender, RoutedEventArgs e) { if (((Button)sender).Tag is Game game) Details(game); }
    private void PlayFromCard(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not Game game) return;
        ObserveUiOperation("Play", () => PlayGame(game.ActionTarget ?? game));
    }
    private void PlayWithWandFromCard(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not Game game) return;
        if (!CanLaunchWithExistingWand(game, out var reason)) { StatusText.Text = reason; return; }
        var wandTarget = ResolveWandCardTarget(game);
        if (wandTarget == null) { StatusText.Text = "The exact Wand launch path is unavailable."; return; }
        ObserveUiOperation("Play with Wand", () => PlayWithWand(wandTarget));
    }
    private void ForceExitGameAndWandFromCard(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not Game game) return;
        ObserveUiAction("Force exit game and Wand", () => ForceExitGameAndWand(game.ActionTarget ?? game));
    }
    private void RefreshCatalog(object sender, RoutedEventArgs e) => ObserveUiOperation("Catalog refresh", () => Refresh(true, force: true));
    internal async Task Refresh(bool full, bool force = false)
    {
        if (offline) { StatusText.Text = "Offline mode · shared edits stay on this PC until you restart online"; return; }
        if (!ready || refreshing || closing) return;
        if (full) lastCatalogRefresh = DateTime.UtcNow;
        refreshing = true; StatusText.Text = full ? "Refreshing catalog and Docker tags…" : "Checking shared changes…";
        var before = Sync.Effective(State).ToJsonString();
        try
        {
            await Sync.Refresh(State, full, adminToken, lifetime.Token, force);
            if (!closing)
            {
                if (full || before != Sync.Effective(State).ToJsonString()) Reload();
                await SynchronizeWandSupportedGamesAsync();
                StatusText.Text = Sync.Status + " · " + namespaceStatus;
                if (full) _ = RefreshNamespace(force);
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
        if (!ready) return;
        if (KeyboardDevice.Modifiers == ModifierKeys.Control && e.Key == Key.K) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.F5) { ObserveUiOperation("Catalog refresh", () => Refresh(true, force: true)); e.Handled = true; }
        else if (KeyboardDevice.Modifiers == ModifierKeys.Control && e.Key == Key.A && !SearchBox.IsKeyboardFocused) { SelectAll(this, new()); e.Handled = true; }
        else if (e.Key == Key.Enter && GameList.IsKeyboardFocusWithin && GameList.SelectedItem is Game game) { Details(game.ActionTarget ?? game); e.Handled = true; }
        else if (e.Key == Key.Escape) { SearchBox.Clear(); DeselectAll(this, new()); }
    }
    private static ModifierKeys KeyboardModifiers => System.Windows.Input.Keyboard.Modifiers;    private static class KeyboardDevice { public static ModifierKeys Modifiers => System.Windows.Input.Keyboard.Modifiers; }
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
        public bool UsesWand { get; set; }
        public DateTime GraceUntilUtc { get; set; }
        public DateTime LastSavedUtc { get; set; }
        public HashSet<int> ObservedProcessIds { get; } = new();
        public Dictionary<int, string> ProcessCreationStamps { get; } = new();
        public bool PauseStateUnavailable { get; set; }
        public bool LastExternalPaused { get; set; }
        public GamePause? NativePause { get; set; }
        public bool PauseChanging { get; set; }
    }
    private readonly HashSet<string> gameSaveOperations = new(StringComparer.OrdinalIgnoreCase);
    // Packaged UI proofs substitute a save helper to avoid mutating real saves.
    private Func<string, bool, Task<GameSaveResult>>? saveOperationProof;
    private async Task RefreshProgress()
    {
        if (progressRefreshing || closing) return;
        progressRefreshing = true;
        try
        {
            progressSnapshots = await GameProgressClient.ReadAsync(lifetime.Token);
            ApplyProgress();
        }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception ex)
        {
            Store.Log("Progress refresh unavailable: " + ex.Message);
            foreach (var game in Games.Where(g => g.ProgressLabel.Length > 0))
            { game.ProgressLabel = "Progress refresh unavailable · last backup data retained"; game.Notify(nameof(Game.ProgressLabel)); }
        }
        finally { progressRefreshing = false; }
    }
    private void ApplyProgress()
    {
        foreach (var game in Games)
        {
            if (game.IsNonGame)
            {
                game.ProgressLabel = ""; game.ProgressDetail = "Campaign progress does not apply to a utility or backup image.";
                game.Notify(nameof(Game.ProgressLabel)); game.Notify(nameof(Game.ProgressDetail));
                continue;
            }
            string executable = State.LaunchPaths.GetValueOrDefault(game.Id, "");
            var matches = progressSnapshots.Where(p => !string.IsNullOrWhiteSpace(executable)
                ? string.Equals(p.Executable, executable, StringComparison.OrdinalIgnoreCase)
                : string.Equals(p.Game, game.Name, StringComparison.OrdinalIgnoreCase)
                    && Games.Count(g => string.Equals(g.Name, game.Name, StringComparison.OrdinalIgnoreCase)) == 1).ToArray();
            var match = matches.Length == 1 ? matches[0] : null;
            game.ProgressLabel = match?.Label ?? (game.PlayedHours > 0 || game.IsPlaying ? "Progress needs a matching verified backup" : "");
            game.ProgressDetail = match?.Detail ?? "A current, matching save backup is needed to estimate campaign progress.";
            game.Notify(nameof(Game.ProgressLabel)); game.Notify(nameof(Game.ProgressDetail));
        }
    }
    private readonly SemaphoreSlim gameSaveGate = new(1, 1);
    private async void BackupFromCard(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Game card }) { var game = card.ActionTarget ?? card; await RunGameSave(game.Id, game.Name, State.LaunchPaths.GetValueOrDefault(game.Id, ""), false); }
    }
    private async void RestoreFromCard(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Game card }) { var game = card.ActionTarget ?? card; await RunGameSave(game.Id, game.Name, State.LaunchPaths.GetValueOrDefault(game.Id, ""), true); }
    }
    /// <summary>Per-card speed control; opens the same dialog as the toolbar menu.</summary>
    private void SpeedFromCard(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Game card }) ShowSpeedDialog(card.ActionTarget ?? card);
    }
    /// <summary>
    /// Per-card "Delete from all drives". The tag is the card's own game, so this acts
    /// on the game whose button was pressed rather than on whatever is selected.
    /// Planning and the confirmation both run before anything is removed, and
    /// GameRemoval refuses any path it cannot prove is inside an allow-listed root.
    /// </summary>
    private async void DeleteFromCard(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Game card }) return;
        var game = card.ActionTarget ?? card;
        try
        {
            var request = new GameRemovalRequest
            {
                GameId = game.Id,
                GameName = game.Name,
                MountPath = State.Settings.MountPath,
                LocalFolders = State.LocalGames.TryGetValue(game.Id, out var local) && !string.IsNullOrWhiteSpace(local?.Folder)
                    ? new[] { local!.Folder } : Array.Empty<string>(),
                InstallationFolders = State.InstallationFolders.TryGetValue(game.Id, out var folder) && !string.IsNullOrWhiteSpace(folder)
                    ? new[] { folder } : Array.Empty<string>(),
                LaunchPath = State.LaunchPaths.GetValueOrDefault(game.Id, string.Empty)
            };
            // Planning touches disk and possibly Docker, so it never runs on the UI thread.
            var plan = await Task.Run(() => GameRemoval.Plan(request));
            if (plan.Targets.Count == 0)
            {
                StatusText.Text = "Nothing on disk belongs to " + game.Name + ".";
                MessageBox.Show(this, "There is nothing on disk to delete for " + game.Name + ".",
                    "Nothing to delete", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var confirm = MessageBox.Show(this,
                GameRemoval.Describe(plan) + "\n\nDelete all of this permanently?",
                "Delete " + game.Name, MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) { StatusText.Text = "Delete cancelled."; return; }
            StatusText.Text = "Deleting " + game.Name + "...";
            var result = await GameRemoval.ExecuteAsync(plan, null, lifetime.Token);
            Reload();
            StatusText.Text = result.FailedCount > 0
                ? $"Deleted {result.DeletedCount} item(s) for {game.Name}; {result.FailedCount} could not be removed."
                : $"Deleted {result.DeletedCount} item(s) for {game.Name} ({result.SkippedCount} already absent).";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportUiFailure("Delete for " + game.Name, ex); }
    }
    private async Task RunGameSave(string id, string name, string executable, bool restore)
    {
        string? reserved = null;
        bool gateHeld = false;
        try
        {
            executable = GameSaveOperations.ValidateExecutable(executable);
            if (restore && activePlays.ContainsKey(id)) throw new InvalidOperationException("Exit this game before restoring its saves.");
            if (!gameSaveOperations.Add(executable)) { StatusText.Text = "A save operation is already queued or running for " + name; return; }
            reserved = executable;
            if (!restore) { State.PendingGameBackups[id] = executable; Store.Save(State); }
            StatusText.Text = (restore ? "Restore" : "Backup") + " queued for " + name;
            await gameSaveGate.WaitAsync(); gateHeld = true;
            if (restore && activePlays.ContainsKey(id)) throw new InvalidOperationException("Exit this game before restoring its saves.");
            StatusText.Text = (restore ? "Restoring " : "Backing up ") + name + "…";
            // Capture the game's real save data first. The helper below only receives an
            // executable path and does its own discovery, which is why progress went
            // unbacked whenever the save tree was not where the helper expected. This
            // snapshot runs even while the game is live and is additive, so the existing
            // helper receipts, logs and verification still run exactly as before.
            SaveOperationOutcome? snapshot = null;
            if (!restore && Program.TestReport == null && !saveOperationProofIsActive(saveOperationProof))
            {
                try
                {
                    snapshot = await GameSaveOperations.BackupSaveRootsAsync(SaveRequestForGame(id, name), null, lifetime.Token);
                    if (!snapshot.Success && snapshot.Items.Count > 0)
                        Store.Log("Save snapshot incomplete for " + name + ": " + snapshot.Summary);
                }
                catch (Exception ex) { Store.Log("Save snapshot failed for " + name + ": " + ex.Message); }
            }
            var result = Program.TestReport != null && saveOperationProof != null
                ? await saveOperationProof(executable, restore)
                : await GameSaveOperations.RunAsync(Store, executable, restore);
            if (!restore) { State.PendingGameBackups.Remove(id); Store.Save(State); }
            StatusText.Text = result.NoSaveData
                ? (snapshot is { Success: true }
                    ? $"Backed up {snapshot.Items.Count} real save files for {name}"
                    : "No save data found for " + name + " · receipt: " + result.ReceiptPath)
                : (restore ? "Restore" : "Backup") + " completed for " + name;
            Store.Log(StatusText.Text + "; log=" + result.LogPath);
            _ = RefreshProgress();
        }
        catch (Exception ex) { ReportUiFailure((restore ? "Restore" : "Backup") + " for " + name, ex); }
        finally { if (gateHeld) gameSaveGate.Release(); if (reserved != null) gameSaveOperations.Remove(reserved); }
    }
    private async void PauseFromCard(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Game game }
            || !activePlays.TryGetValue(game.Id, out var session) || session.PauseChanging) return;
        session.PauseChanging = true;
        try
        {
            if (session.NativePause != null && !session.NativePause.IsPaused)
            { session.NativePause.Dispose(); session.NativePause = null; }
            if (session.NativePause != null)
            {
                try { await session.NativePause.ResumeAsync(); }
                finally { session.NativePause = null; }
            }
            else if (Program.TestReport == null)
            {
                var targetProcess = session.Process;
                var status = await AhkGameControl.GetStatusAsync(targetProcess, targetProcess.MainWindowHandle, lifetime.Token, frozenStatePath: FrozenProcessesPath);
                if (status.State == AhkGameState.Paused)
                    await AhkGameControl.ResumeAsync(targetProcess, targetProcess.MainWindowHandle, lifetime.Token, frozenStatePath: FrozenProcessesPath);
                else if (status.State == AhkGameState.Running)
                    await AhkGameControl.PauseAsync(targetProcess, targetProcess.MainWindowHandle, lifetime.Token, frozenStatePath: FrozenProcessesPath);
                else
                    throw new IOException("AHK could not identify this game's current pause state: " + status.State + ". " + status.Detail);
            }
            else
            {
                var external = FrozenProcessState.Read(FrozenProcessesPath,
                    session.ProcessCreationStamps.Select(p => new FrozenProcessIdentity(p.Key, p.Value)));
                if (!external.IsAvailable) throw new IOException("The external pause state cannot be read. Try again after it becomes available.");
                if (external.IsPaused) throw new InvalidOperationException("This game was paused by AHK. Resume it with Alt+H before using the library's pause control.");
                var targetProcess = session.Process;
                var paused = await GamePause.PauseAsync(targetProcess, lifetime.Token);
                if (closing || !activePlays.TryGetValue(game.Id, out var current) || !ReferenceEquals(current, session)
                    || !ReferenceEquals(targetProcess, session.Process))
                { paused.Dispose(); return; }
                session.NativePause = paused;
            }
            SamplePlaySession(game.Id, session, announceTransition: true);
            Save();
        }
        catch (Exception ex) { ReportUiFailure("Could not change pause for " + game.Name, ex); }
        finally { session.PauseChanging = false; }
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
        string executable = State.LaunchPaths.GetValueOrDefault(game.Id, "");
        if (!string.IsNullOrWhiteSpace(executable) && gameSaveOperations.Contains(Path.GetFullPath(executable)))
        { StatusText.Text = "Wait for this game's backup or restore to finish before playing."; return true; }
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
            projectionMutationVersion++;
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
            bool normalExit = false;
            try { normalExit = session.Process.ExitCode == 0; } catch { }
            string executable = session.ExecutablePath;
            CommitPlaySession(id, session); save = true;
            if (normalExit && !closing && (Program.TestReport == null || saveOperationProof != null))
                _ = RunGameSave(id, Games.FirstOrDefault(g => g.Id == id)?.Name ?? id, executable, false);
        }
        if (save) Save();
    }
    private void SamplePlaySession(string id, PlaySession session, bool announceTransition)
    {
        var identities = session.ProcessCreationStamps.Select(entry => new FrozenProcessIdentity(entry.Key, entry.Value));
        var pauseRead = FrozenProcessState.Read(FrozenProcessesPath, identities);
        bool wasUnavailable = session.PauseStateUnavailable;
        if (pauseRead.IsAvailable) session.LastExternalPaused = pauseRead.IsPaused;
        bool paused = session.NativePause?.IsPaused == true || session.LastExternalPaused;
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
        long sampleAt = Stopwatch.GetTimestamp();
        if (!pauseRead.IsAvailable) session.Timing.Hold(sampleAt);
        else
        {
            if (wasUnavailable) session.Timing.Hold(sampleAt);
            session.Timing.Sample(sampleAt, paused);
        }
        State.PlayTimeSeconds[id] = session.Timing.TotalSeconds;
        SetGamePlayState(id, playing: true, paused: paused);
        var stateGame = Games.FirstOrDefault(candidate => candidate.Id == id);
        if (stateGame != null)
        {
            stateGame.IsPauseStateUnknown = !pauseRead.IsAvailable;
            stateGame.Notify(nameof(Game.PauseLabel));
            stateGame.Notify(nameof(Game.PlayedMeta));
        }
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
        session.NativePause?.Dispose(); session.NativePause = null;
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
        game.Notify(nameof(Game.IsPlaying)); game.Notify(nameof(Game.IsPlayPaused)); game.Notify(nameof(Game.PlayLabel)); game.Notify(nameof(Game.PauseLabel)); game.Notify(nameof(Game.PlayedMeta));
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
    private Task MoveSelectedAsync()
    {
        var selected = Games.Where(g => g.Selected).ToArray();
        if (selected.Length == 0) { StatusText.Text = "Select games to move first."; return Task.CompletedTask; }
        var dialog = new EditorWindow(this, "Move selected games", $"Move {selected.Length} game(s). Changes are saved in this Windows library.");
        var category = dialog.Choice("Category", EffectiveCategories().Where(c => c.Id != "all").ToArray());
        dialog.Action("Move games", () =>
        {
            if (category.SelectedItem is not Category target) return;
            LocalCatalogEdits.Save(Store, State, selected.Select(game => new PendingEdit { Section = "gameCategories", Key = game.Id, After = JsonValue.Create(target.Id) }).ToArray());
            dialog.Close(); Reload();
        });
        dialog.ShowDialog(); return Task.CompletedTask;
    }
    private (int launch, int folders, int local, int playtime, int played) SearchStateShape() =>
        (State.LaunchPaths.Count, State.InstallationFolders.Count, State.LocalGames.Count,
         State.PlayTimeSeconds.Count, State.LastPlayedUtc.Count);

    private static bool saveOperationProofIsActive(Func<string, bool, Task<GameSaveResult>>? proof) => proof is not null;

    private void SpeedHotkeyPreview(object sender, KeyEventArgs e)
    {
        var key = e.Key switch
        {
            Key.F1 => SpeedHotkey.Boost,
            Key.F2 => SpeedHotkey.Reduce,
            Key.F3 => SpeedHotkey.Normal,
            _ => (SpeedHotkey?)null
        };
        if (key is null) return;
        e.Handled = true;
        // The gate is this library's own set of running plays, so the keys can only
        // ever act on a game launched from here - never an arbitrary process, and
        // never anything at all when no game is running.
        if (!TryGetRunningPlay(out var play))
        {
            StatusText.Text = "Start a game before using F1/F2/F3.";
            return;
        }
        _ = ApplySpeedToRunningPlay(play.gameId, play.gameName, play.processId, key);
    }

    private readonly record struct RunningPlay(string gameId, string gameName, int processId);

    private bool TryGetRunningPlay(out RunningPlay play)
    {
        foreach (var entry in activePlays)
        {
            Process? process = entry.Value.Process;
            if (process is null) continue;
            try
            {
                if (process.HasExited) continue;
                int id = process.Id;
                if (id <= 0) continue;
                string name = Games.FirstOrDefault(g => string.Equals(g.Id, entry.Key, StringComparison.Ordinal))?.Name ?? entry.Key;
                play = new RunningPlay(entry.Key, name, id);
                return true;
            }
            catch (InvalidOperationException) { }
        }
        play = default;
        return false;
    }

    /// <summary>
    /// Applies a speed to the running game and only records it once the hook has
    /// confirmed it, so the status text can never claim a speed the game is not
    /// actually running at. Re-prompting with the confirmed value keeps repeated F1
    /// presses walking the ladder instead of stalling on an unconfirmed one.
    /// </summary>
    private async Task ApplySpeedToRunningPlay(string gameId, string gameName, int processId, SpeedHotkey? hotkey, double? explicitFactor = null)
    {
        double current = GameSpeedController.ForGame(gameId, State.SpeedByGame, GameSpeedNative.NormalFactor);
        double target = explicitFactor ?? GameSpeedController.ApplyHotkey(current, hotkey!.Value);
        var result = await GameSpeedCommand.ApplyAsync(processId, target, NativeSpeedRuntime.Instance, lifetime.Token);
        if (result.Succeeded)
        {
            State.SpeedByGame[gameId] = result.Factor;
            Save();
            StatusText.Text = result.Message + " (" + gameName + ")";
        }
        else
        {
            StatusText.Text = result.Message;
        }
    }

    private SaveOperationRequest SaveRequestForGame(string id, string name)
    {
        activePlays.TryGetValue(id, out var session);
        var manual = State.SaveDataOverrides.TryGetValue(id, out var overridePath) && !string.IsNullOrWhiteSpace(overridePath)
            ? new[] { overridePath } : Array.Empty<string>();
        return new SaveOperationRequest(id, name, manual,
            State.InstallationFolders.GetValueOrDefault(id, string.Empty),
            session?.Process?.ProcessName, true);
    }

    private SaveOperationRequest SaveRequestFor(Game game) => SaveRequestForGame(game.Id, game.Name);

    private async void BackupGameSaves(Game game) => await RunGameSave(game.Id, game.Name, State.LaunchPaths.GetValueOrDefault(game.Id, ""), false);

    private async void RestoreGameSaves(Game game) => await RunGameSave(game.Id, game.Name, State.LaunchPaths.GetValueOrDefault(game.Id, ""), true);

    private void ShowSpeedDialog(Game game)
    {
        var live = TryGetRunningPlay(out var play) && play.gameId == game.Id;
        double current = GameSpeedController.ForGame(game.Id, State.SpeedByGame, GameSpeedNative.NormalFactor);
        var input = new System.Windows.Controls.TextBox { Text = current.ToString("0.##", CultureInfo.CurrentCulture), Margin = new Thickness(0, 6, 0, 10) };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Speed multiplier for " + game.Name, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(input);
        panel.Children.Add(new TextBlock
        {
            Text = live
                ? "Or press F1 / F2 / F3 now. F1 adds 0.5x, F2 removes 0.5x, F3 returns to exactly normal. Keys work while this game is running."
                : "This game is not running, so a speed can be set now and will be applied the next time it starts. F1 / F2 / F3 only work while a game is running.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Margin = new Thickness(0, 4, 0, 0)
        });
        var dialog = new Window { Title = "Game speed", Content = panel, Width = 380, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ResizeMode = ResizeMode.NoResize, Background = SystemColors.ControlBrush };
        var ok = new Button { Content = live ? "Apply now" : "Save", IsDefault = true, Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "Close", IsCancel = true, Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(8, 12, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        cancel.Click += (_, _) => dialog.Close();
        ok.Click += async (_, _) =>
        {
            if (!(double.TryParse(input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double parsed)
                || double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)))
            {
                StatusText.Text = "Enter a speed such as 1.5 or 2.";
                return;
            }
            // Clamp before use so a hand-typed value can never store a non-finite or
            // out-of-range multiplier.
            double wanted = GameSpeedController.Clamp(parsed);
            if (live)
            {
                // Applying to the live process is the whole point: report what actually
                // happened rather than silently storing a number.
                await ApplySpeedToRunningPlay(game.Id, game.Name, play.processId, null, wanted);
            }
            else
            {
                State.SpeedByGame[game.Id] = wanted;
                Save();
                StatusText.Text = "Speed for " + game.Name + " set to " + GameSpeedController.Describe(wanted) + "; it applies when the game starts.";
            }
            dialog.Close();
        };
        dialog.ShowDialog();
    }

    private void ExportScriptMenu(object sender, RoutedEventArgs e)
    {
        var menu = BuildExportMenu();
        menu.PlacementTarget = (Button)sender; menu.IsOpen = true;
    }
    private ContextMenu BuildExportMenu()
    {
        var menu = new ContextMenu();
        foreach (var format in new[] { "bat", "ps1", "sh" })
        {
            var label = format switch { "bat" => "Download .BAT (default)", "ps1" => "Download .PS1 (PowerShell)", _ => "Download .SH (" + (State.Settings.ShellTarget == "wsl2" ? "WSL2" : "Native Linux") + ")" };
            var item = new MenuItem { Header = label }; var f = format;
            item.Click += (_, _) => ExportScript(f, false, State.Settings.ShellTarget); menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var copy = new MenuItem { Header = "Copy PowerShell script" }; copy.Click += (_, _) => { try { System.Windows.Clipboard.SetText(DockerScripts.Generate(SelectedForInstall(), State.Settings)); StatusText.Text = "Script copied."; } catch (Exception ex) { Error(ex); } }; menu.Items.Add(copy);
        var kill = new MenuItem { Header = "Export stop-selected-containers script" }; kill.Click += (_, _) => ExportScript("ps1", true, State.Settings.ShellTarget); menu.Items.Add(kill);
        menu.Items.Add(new Separator());
        foreach (var format in new[] { "ps1", "bat" })
        {
            var item = new MenuItem { Header = "Export Kill All script (." + format + ")" }; var f = format;
            item.Click += (_, _) => ExportKillAll(f); menu.Items.Add(item);
        }
        return menu;
    }

    /// <summary>
    /// Builds the per-game action menu. "Delete from all drives" is offered for
    /// every card and routes through GameRemoval, which refuses anything it cannot
    /// prove is inside an allow-listed root, so the entry point can never widen a
    /// delete beyond the game's own files and leftovers.
    /// </summary>
    private void GameActionsMenu(object sender, RoutedEventArgs e)
    {
        var game = Selected().FirstOrDefault() ?? SelectedForInstall().FirstOrDefault();
        var menu = new ContextMenu();
        var play = new MenuItem { Header = game is null ? "Play" : "Play " + game.Name };
        play.IsEnabled = game is not null;
        play.Click += (_, _) => { if (game is not null) ObserveUiOperation("Play", () => PlayGame(game.ActionTarget ?? game)); };
        menu.Items.Add(play);
        var backup = new MenuItem { Header = "Back up save data" };
        backup.IsEnabled = game is not null;
        backup.Click += (_, _) => { if (game is not null) BackupGameSaves(game); };
        menu.Items.Add(backup);
        var restore = new MenuItem { Header = "Restore save data" };
        restore.IsEnabled = game is not null;
        restore.Click += (_, _) => { if (game is not null) RestoreGameSaves(game); };
        menu.Items.Add(restore);
        var speed = new MenuItem { Header = "Speed" };
        speed.IsEnabled = game is not null;
        speed.Click += (_, _) => { if (game is not null) ShowSpeedDialog(game); };
        menu.Items.Add(speed);
        menu.Items.Add(new Separator());
        var remove = new MenuItem { Header = "Delete from all drives" };
        remove.IsEnabled = game is not null;
        remove.Click += (_, _) => { if (game is not null) DeleteFromCard(sender: new FrameworkElement { Tag = game }, e: new RoutedEventArgs()); };
        menu.Items.Add(remove);
        menu.Items.Add(new Separator());
        foreach (var item in BuildExportMenu().Items)
            if (item is Separator) menu.Items.Add(new Separator()); else menu.Items.Add(item);
        menu.PlacementTarget = (Button)sender; menu.IsOpen = true;
    }

    private Game[] Selected() => projectedCards.Where(card => card.Selected)
        .Select(card => card.ActionTarget ?? card).DistinctBy(game => game.Id, StringComparer.Ordinal).ToArray();
    private Game[] SelectedForInstall() => projectedCards.Where(card => card.Selected)
        .Select(card => card.PublicationFreshnessVerified && card.PublishedRepresentative is Game published
            ? published : card.ActionTarget ?? card).DistinctBy(game => game.Id, StringComparer.Ordinal).ToArray();
    private void CopySelectedCommands(object sender, RoutedEventArgs e)
    {
        try
        {
            var games = SelectedForInstall();
            if (games.Length == 0) { StatusText.Text = "Select games before copying commands."; return; }
            string script = DockerScripts.Generate(games, State.Settings, "ps1", false, State.Settings.ShellTarget);
            System.Windows.Clipboard.SetText(script);
            StatusText.Text = "Copied install commands for " + games.Length + " selected games.";
        }
        catch (Exception ex) { Error(ex); }
    }
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
            var script = DockerScripts.Generate(stop ? Selected() : SelectedForInstall(), State.Settings, format, stop, shellTarget);
            State.Settings.ScriptFormat = format; Save();
            var save = new Microsoft.Win32.SaveFileDialog { FileName = stop ? "stop-selected-games" : "install-games", DefaultExt = "." + format, Filter = format.ToUpperInvariant() + " script|*." + format };
            if (save.ShowDialog(this) == true) { File.WriteAllText(save.FileName, script, format == "ps1" ? new UTF8Encoding(true) : new UTF8Encoding(false)); StatusText.Text = "Saved " + save.FileName; }
        }
        catch (Exception ex) { Error(ex); }
    }
    private void InstallSelected(object sender, RoutedEventArgs e) => StartInstall(SelectedForInstall());
    internal void StartInstall(Game[] games)
    {
        try
        {
            games = DockerScripts.DistinctGames(games);
            if (games.Length == 0) { StatusText.Text = "Select games to install first."; return; }
            string destination = State.Settings.MountPath;
            var review = new EditorWindow(this, "Install selected games", $"{games.Length} game(s) → {State.Settings.MountPath}\nReported download size: {games.Sum(g => g.SizeGb):0.#} GB; {games.Count(g => g.SizeGb <= 0)} without size data. New image digests install into separate version folders; existing installations are preserved.\nChoose the Windows BAT / default terminal route (.BAT) for durable Pause, Resume, and Stop, or WSL2 Ubuntu (.SH) to use its existing script route.");
            var names = review.Paragraph(string.Join("\n", games.Select(g => g.Name))); names.MaxHeight = 220;
            var format = review.Choice("Install format", new[] { "Windows default terminal (.BAT)", "WSL2 Ubuntu (.SH)" }, State.Settings.ShellTarget == "wsl2" ? 1 : 0, "InstallFormat");
            review.Action("Start download", () =>
            {
                bool wsl2 = format.SelectedIndex == 1;
                InstallJobStore? durableStore = null;
                InstallJobController? durableController = null;
                string? executablePath = null;
                Game[] candidates = games;
                bool reopenedExisting = false;
                if (!wsl2)
                {
                    try
                    {
                        durableStore = new InstallJobStore(Store.Root);
                        executablePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                        if (string.IsNullOrWhiteSpace(executablePath)) throw new InvalidOperationException("Could not resolve the running native executable for its worker process.");
                        durableController = new InstallJobController(durableStore, Store.Root, executablePath);
                        var activeBySource = durableStore.LoadAll().Where(job =>
                            PathEquals(job.DestinationPath, destination)
                            && job.Status is InstallJobStatus.Queued or InstallJobStatus.Running or InstallJobStatus.PauseRequested or InstallJobStatus.Paused or InstallJobStatus.ResumeRequested or InstallJobStatus.StopRequested or InstallJobStatus.RetryableFailure)
                            .GroupBy(job => job.SourceGameId, StringComparer.Ordinal)
                            .ToDictionary(group => group.Key, group => group.OrderByDescending(job => job.UpdatedUtc).First(), StringComparer.Ordinal);
                        var remaining = new List<Game>();
                        foreach (Game game in candidates)
                        {
                            if (activeBySource.TryGetValue(game.Id, out InstallJobRecord? existing))
                            {
                                StartDurableInstallTerminal(game, existing, durableStore, durableController, destination, reservationHeld: false);
                                reopenedExisting = true;
                                StatusText.Text = game.Name + " already has a durable install job · " + existing.Status + ". Reopened that job instead of starting another.";
                            }
                            else remaining.Add(game);
                        }
                        candidates = remaining.ToArray();
                    }
                    catch (Exception ex)
                    {
                        Store.Log("Could not inspect durable install jobs; no worker was started: " + ex);
                        StatusText.Text = "Install could not safely inspect existing jobs: " + ex.Message;
                        return;
                    }
                }
                var launchGames = ReserveInstallGames(candidates, destination);
                if (launchGames.Length == 0)
                {
                    if (!reopenedExisting) StatusText.Text = "Those games are already being installed. The existing operation is still in control of their files.";
                    review.Close();
                    return;
                }
                if (launchGames.Length != candidates.Length)
                    StatusText.Text = $"Skipped {candidates.Length - launchGames.Length} game(s) already being installed; starting the remaining {launchGames.Length}.";
                var launchIds = launchGames.Select(g => g.Id).ToArray();
                bool released = false;
                void ReleaseReservations()
                {
                    if (released) return;
                    released = true;
                    ReleaseInstallGames(launchGames, destination);
                }
                string extension = wsl2 ? "sh" : "bat";
                try
                {
                    if (wsl2)
                    {
                        // WSL2 stays on its established script/JobWindow path until the worker can own and reconcile its Linux process group.
                        StatusText.Text = "WSL2 install uses the existing script route; durable Pause and Resume are not available for this target yet.";
                        string script = DockerScripts.Generate(launchGames, State.Settings, extension, shellTarget: "wsl2");
                        var byId = launchGames.ToDictionary(g => g.Id, StringComparer.Ordinal);
                        var job = new JobWindow(Store, script, launchGames.Select(g => DockerScripts.ContainerNameForDestination(g.Id, destination)).ToArray(), openInDefaultTerminal: false, scriptExtension: extension, completionDestination: destination, completionGameIds: launchIds, acquireInstallation: cancellation => AcquireInstallScopeAsync(launchIds, destination, cancellation));
                        job.GameCompleted += id => byId.TryGetValue(id, out var game) ? ScanCompletedGame(game, destination, job.CompletionStartedAtUtc, job.OperationId) : Task.CompletedTask;
                        job.CompletedAsync += async success =>
                        {
                            try { await ScanCompletedDownloads(launchGames, destination, success, job.CompletionStartedAtUtc, job.OperationId); }
                            finally { ReleaseReservations(); }
                        };
                        jobs.Add(job); job.Closed += (_, _) => { jobs.Remove(job); ReleaseReservations(); }; job.Show();
                    }
                    else
                    {
                        if (durableStore == null || durableController == null || executablePath == null)
                            throw new InvalidOperationException("The native durable worker was not initialized.");
                        InstallJobTargetSupport support = InstallWorkerCapability.Check(InstallWorkerCapability.NativeWindowsTarget);
                        if (!support.Supported) throw new InvalidOperationException(support.Message);
                        foreach (Game game in launchGames)
                        {
                            InstallJobRecord record = DockerScripts.CreateInstallJobRecord(game, State.Settings, game.Id, destination, relatedSourceGameIds: new[] { game.Id }, executionTarget: support.Target);
                            durableStore.Create(record);
                            StartDurableInstallTerminal(game, record, durableStore, durableController, destination, reservationHeld: true);
                        }
                    }
                    review.Close();
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

    private static bool PathEquals(string left, string right)
    {
        try { return string.Equals(GameOperationCoordinator.CanonicalPath(left), GameOperationCoordinator.CanonicalPath(right), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private void StartDurableInstallTerminal(Game game, InstallJobRecord record, InstallJobStore jobStore, InstallJobController controller, string destination, bool reservationHeld)
    {
        var session = new InstallJobTerminalSession(jobStore, controller, record.OperationId);
        durableInstallSessions.Add(session);
        session.JobChanged += snapshot =>
        {
            if (snapshot.Status is not (InstallJobStatus.Completed or InstallJobStatus.Failed or InstallJobStatus.Stopped)) return;
            if (reservationHeld) ReleaseInstallGames(new[] { game }, destination);
            if (snapshot.Status == InstallJobStatus.Completed)
                ObserveUiOperation("Refresh installed game", () => ScanCompletedGame(game, destination, record.CreatedUtc, record.OperationId, record.InstalledPath));
            durableInstallSessions.Remove(session);
            session.Dispose();
        };
        session.MonitorError += message => Store.Log("Install terminal " + record.OperationId + ": " + message);
        try { session.Start(); }
        catch
        {
            durableInstallSessions.Remove(session);
            session.Dispose();
            throw;
        }
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
            var found = await Task.Run(() => InstalledScanner.Discover(root, snapshot, lifetime.Token, State.InstallationFolders), lifetime.Token);
            if (closing) return;
            bool changed = ApplyInstalledScan(found, reconcileMissingCatalog: true, logNotices: false);
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
            var found = await Task.Run(() => InstalledScanner.Discover(root, snapshot, lifetime.Token, State.InstallationFolders));
            ApplyInstalledScan(found, reconcileMissingCatalog: true);
            // This explicit scan reconciles stale catalog markers only when the
            // selected root is available; manual launchers on another path remain.
            State.Settings.MountPath = root; Save(); Reload(); StatusText.Text = $"Found {found.Games.Count} installed games; {found.LauncherCount} launchers ready. Open Details to choose or play. " + (found.Notices.Count > 0 ? "Scan notices are in the activity log." : "");
        }
        catch (Exception ex) { Error(ex); }
    }
}
