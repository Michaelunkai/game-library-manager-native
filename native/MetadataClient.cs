using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace GameLibrary.Native;

public sealed class MetadataClient : IDisposable
{
    private readonly HttpClient http;
    public MetadataClient(HttpMessageHandler? handler = null)
    {
        http = handler == null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(25);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GameLibraryNative/1.0");
    }
    public static bool SameTitle(string expected, string actual)
    {
        static string Normalize(string value) => Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{N}]", "");
        return Normalize(expected).Length > 0 && Normalize(expected) == Normalize(actual);
    }
    public static bool MatchesGame(Game game, JsonObject metadata) => DataJson.Text(metadata["id"]) == game.Id &&
        (SameTitle(game.Name, DataJson.Text(metadata["name"])) ||
         // The existing first-party endpoint explicitly marks its curated tag-to-Steam aliases.
         // Generic search matches still require the exact normalized title (no soundtrack/DLC guess).
         (DataJson.Text(metadata["name"]).Length > 0 && DataJson.Text(metadata["source"]?["image"]) == "steam-known" && DataJson.Text(metadata["source"]?["time"]) == "known-override"));
    public async Task<JsonObject> Refresh(Game game, LibraryStore store, bool cover, bool time, CancellationToken cancellation)
    {
        string url = SyncClient.Production + "api/game-metadata?id=" + Uri.EscapeDataString(game.Id) + "&name=" + Uri.EscapeDataString(game.Name) + "&category=" + Uri.EscapeDataString(game.Category);
        var raw = JsonNode.Parse(await http.GetStringAsync(url, cancellation))?.AsObject() ?? throw new FormatException("Empty metadata response.");
        if (raw["success"]?.GetValue<bool>() != true) throw new FormatException(DataJson.Text(raw["error"], "Metadata is unavailable."));
        if (!MatchesGame(game, raw)) throw new FormatException("The provider returned a different title (" + DataJson.Text(raw["name"]) + "). Existing metadata was preserved.");
        var attribution = new JsonObject();
        var result = new JsonObject { ["fetchedAt"] = DateTime.UtcNow.ToString("O"), ["source"] = attribution };
        if (game.Discovered)
        {
            result["name"] = raw["name"]?.DeepClone();
            result["category"] = raw["category"]?.DeepClone();
        }
        // A generic genre estimate must not downgrade an existing catalog time.
        if (time && DataJson.Number(raw["time"]) is > 0 and < 100000 && (game.Time <= 0 || DataJson.Text(raw["source"]?["time"]) != "genre-estimate"))
        {
            result["time"] = raw["time"]?.DeepClone();
            attribution["time"] = raw["source"]?["time"]?.DeepClone();
        }
        if (cover && Uri.TryCreate(DataJson.Text(raw["image"]), UriKind.Absolute, out var image) && image.Scheme == "https" && !image.IsLoopback)
        {
            using var response = await http.GetAsync(image, HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 8 * 1024 * 1024) throw new FormatException("Cover exceeds the 8 MB limit.");
            await using var source = await response.Content.ReadAsStreamAsync(cancellation);
            using var content = new MemoryStream(); var buffer = new byte[16384];
            int count;
            while ((count = await source.ReadAsync(buffer, cancellation)) > 0)
            {
                if (content.Length + count > 8 * 1024 * 1024) throw new FormatException("Cover exceeds the 8 MB limit.");
                content.Write(buffer, 0, count);
            }
            content.Position = 0;
            var decoder = BitmapDecoder.Create(content, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0 || decoder.Frames.Any(f => f.PixelWidth > 12000 || f.PixelHeight > 12000)) throw new FormatException("Invalid cover dimensions.");
            var bytes = content.ToArray();
            string relative = "covers/" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + ".img";
            string file = LibraryStore.SafeChild(store.Cache, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            if (!File.Exists(file))
            {
                string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { await File.WriteAllBytesAsync(temp, bytes, cancellation); File.Move(temp, file, true); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            result["cover"] = relative;
            attribution["image"] = raw["source"]?["image"]?.DeepClone();
        }
        if (result["time"] == null && result["cover"] == null) throw new FormatException("The provider returned no usable cover or completion time.");
        var all = store.ReadMetadata();
        var previous = all[game.Id] as JsonObject ?? new JsonObject();
        // Source labels describe the fields actually replaced, not every field in the response.
        var previousSource = previous["source"] as JsonObject ?? new JsonObject();
        foreach (var field in attribution) previousSource[field.Key] = field.Value?.DeepClone();
        foreach (var field in result.Where(f => f.Key != "source")) previous[field.Key] = field.Value?.DeepClone();
        previous["source"] = previousSource.DeepClone();
        all[game.Id] = previous.DeepClone();
        store.CacheData("metadata.json", all.ToJsonString());
        return result;
    }
    public void Dispose() => http.Dispose();
}

public partial class MainWindow
{
    private bool automaticMetadataRunning;
    private JsonObject? metadataAttempts;
    internal static bool MetadataDue(Game game, JsonObject attempts, DateTime now)
    {
        if (game.IsLocal || (!string.IsNullOrEmpty(game.Cover) && game.Time > 0)) return false;
        return !DateTime.TryParse(DataJson.Text(attempts[game.Id]?["retryAfter"]), out var retry) || retry.ToUniversalTime() <= now;
    }
    private void ScheduleMetadata()
    {
        if (offline || !ready || closing || automaticMetadataRunning || Program.TestReport != null || !IsVisible) return;
        automaticMetadataRunning = true;
        try
        {
            _ = Dispatcher.BeginInvoke(new Action(() => ObserveUiOperation("Automatic metadata", RunAutomaticMetadata)), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        catch (InvalidOperationException) { automaticMetadataRunning = false; }
        catch (Exception ex) { automaticMetadataRunning = false; Store.Log("Automatic metadata dispatch failed: " + ex); }
    }
    private async Task RunAutomaticMetadata()
    {
        int updated = 0, unavailable = 0;
        try
        {
            if (metadataAttempts == null)
            {
                try { metadataAttempts = JsonNode.Parse(File.ReadAllText(Path.Combine(Store.Cache, "metadata-attempts.json")))!.AsObject(); }
                catch { metadataAttempts = new(); }
            }
            using var client = new MetadataClient();
            while (!closing && IsVisible)
            {
                await Task.Delay(500, lifetime.Token);
                // Prioritize rendered rows, then newly discovered Docker entries. Curated entries
                // outside the current view do not generate a bulk request on every startup.
                var visible = filtered.Where(g => GameList.ItemContainerGenerator.ContainerFromItem(g) is System.Windows.FrameworkElement { IsVisible: true });
                var hidden = Hidden(Sync.Effective(State));
                var game = visible.Concat(Games.Where(g => g.Discovered && (IsAdmin || (!protectedTabs.Contains(g.Category) && !hidden.Contains(g.Category))))).DistinctBy(g => g.Id)
                    .FirstOrDefault(g => MetadataDue(g, metadataAttempts, DateTime.UtcNow));
                if (game == null) break;
                ArtworkStatus.Visibility = System.Windows.Visibility.Visible;
                ArtworkStatus.Text = "Looking up artwork & time · " + game.Name;
                bool success = false;
                string? unavailableReason = null;
                try
                {
                    await client.Refresh(game, Store, string.IsNullOrEmpty(game.Cover), game.Time <= 0, lifetime.Token);
                    success = true; updated++;
                    var current = Games.FirstOrDefault(g => g.Id == game.Id);
                    var refreshed = Store.LoadGames(State, Sync.Effective(State)).FirstOrDefault(g => g.Id == game.Id);
                    if (current != null && refreshed != null)
                    {
                        bool refilter = current.Category != refreshed.Category || current.Name != refreshed.Name ||
                            (current.Time != refreshed.Time && SortBox.SelectedItem is "Shortest first" or "Longest first");
                        current.Cover = refreshed.Cover; current.Time = refreshed.Time; current.Name = refreshed.Name;
                        current.Category = refreshed.Category; current.CategoryName = refreshed.CategoryName; current.Notify("");
                        catalogStatsDirty = true;
                        if (refilter) ApplyFilter();
                        else UpdateStats();
                    }
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    unavailable++; unavailableReason = ex.Message;
                    Store.Log("Automatic metadata preserved for " + game.Id + ": " + ex.Message);
                }
                metadataAttempts[game.Id] = new JsonObject
                {
                    ["attemptedAt"] = DateTime.UtcNow.ToString("O"), ["available"] = success,
                    ["retryAfter"] = DateTime.UtcNow.AddHours(success ? 24 : 1).ToString("O"),
                    ["reason"] = unavailableReason
                };
                Store.CacheData("metadata-attempts.json", metadataAttempts.ToJsonString());
            }
            if (updated + unavailable > 0)
                ArtworkStatus.Text = $"Artwork & time · {updated} updated; {unavailable} unavailable. Refresh covers & times can retry.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Store.Log("Automatic metadata paused: " + ex.Message); }
        finally { automaticMetadataRunning = false; }
    }
    private void RefreshMetadataMenu(object sender, System.Windows.RoutedEventArgs e) => RefreshMetadata((Selected().Length > 0 ? Selected() : filtered.Where(g => g.Time <= 0 || string.IsNullOrEmpty(g.Cover))).Where(g => !g.IsLocal).ToArray());
    private void RefreshMetadata(Game[] games)
    {
        var dialog = new EditorWindow(this, "Refresh covers & times", games.Length == 0 ? "Select games to refresh their metadata. Existing covers and times stay available offline." : $"Refresh {games.Length} selected game(s) using the same metadata service as the website. Names and shared categories are preserved.");
        var covers = dialog.Check("Refresh cover images", true);
        var times = dialog.Check("Refresh approximate completion times", true);
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var cancellation = cancel.Token;
        bool running = false, refreshDialogClosed = false, cancellationDisposed = false;
        void DisposeRefreshCancellation()
        {
            if (cancellationDisposed) return;
            cancellationDisposed = true;
            try { cancel.Dispose(); } catch { }
        }
        var start = dialog.Action("Start refresh", () => { }, "StartMetadataRefresh");
        start.IsEnabled = games.Length > 0;
        start.Click += async (_, _) =>
        {
            if (running || (covers.IsChecked != true && times.IsChecked != true)) return;
            running = true; start.IsEnabled = false;
            int updated = 0, failed = 0;
            try
            {
                using var client = new MetadataClient();
                foreach (var game in games)
                {
                    cancellation.ThrowIfCancellationRequested();
                    dialog.Notice.Text = $"{updated + failed}/{games.Length} · {game.Name}";
                    try { await client.Refresh(game, Store, covers.IsChecked == true, times.IsChecked == true, cancellation); updated++; }
                    catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
                    catch (Exception ex) { failed++; Store.Log("Metadata preserved for " + game.Id + ": " + ex.Message); }
                }
                dialog.Notice.Text = $"Updated {updated}; unavailable or title mismatch {failed}. Details are in the activity log.";
            }
            catch (OperationCanceledException) { dialog.Notice.Text = $"Stopped. {updated} completed updates were saved."; }
            finally
            {
                running = false;
                try { Reload(); }
                catch (Exception ex) { Store.Log("Metadata dialog reload failed; saved metadata was preserved: " + ex); }
                if (refreshDialogClosed) DisposeRefreshCancellation();
            }
        };
        dialog.Action("Stop refresh", () => { try { cancel.Cancel(); } catch (ObjectDisposedException) { } });
        dialog.Closed += (_, _) =>
        {
            refreshDialogClosed = true;
            try { cancel.Cancel(); } catch (ObjectDisposedException) { }
            if (!running) DisposeRefreshCancellation();
        };
        dialog.ShowDialog();
        refreshDialogClosed = true;
        if (!running) DisposeRefreshCancellation();
    }
}
