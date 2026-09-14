using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace GameLibrary.Native;

public sealed class MetadataClient : IDisposable
{
    // These are the provider identities that cannot be recovered from the
    // Docker tag by ordinary title normalization. Keep this list closed: the
    // provider's "known" labels are evidence that an override was curated,
    // not permission to accept an arbitrary title for the requested id.
    private static readonly IReadOnlyDictionary<string, string[]> KnownProviderAliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["dragonquest1n2hd2dremake"] = new[] { "Dragon Quest I & II HD-2D Remake" },
        ["the-first-berserker-khazan"] = new[] { "The First Berserker Khazan" },
        ["007-first-light"] = new[] { "007 First Light" },
        ["ofashnsteel"] = new[] { "Of Ash and Steel" },
        ["Oceanhorn2"] = new[] { "Oceanhorn 2: Knights of the Lost Realm" },
        ["freedom-planet-2"] = new[] { "Freedom Planet 2" },
        ["tails-of-iron-2"] = new[] { "Tails of Iron 2: Whiskers of Winter" },
        ["song-of-nunu"] = new[] { "Song of Nunu: A League of Legends Story" },
        ["beyond-good-and-evil-20th-ae"] = new[] { "Beyond Good & Evil - 20th Anniversary Edition" },
        ["dying-light-2"] = new[] { "Dying Light 2 Stay Human: Reloaded Edition" },
        ["death-stranding-2"] = new[] { "DEATH STRANDING 2: ON THE BEACH" },
        ["the-last-oricru"] = new[] { "The Last Oricru - Final Cut" },
        ["planet-of-lana-ii"] = new[] { "Planet of Lana II" },
        ["avatarfrontiersofpandora"] = new[] { "Avatar: Frontiers of Pandora" },
        ["mafiatheoldcountry"] = new[] { "Mafia: The Old Country" }
    };
    private static readonly HashSet<string> RejectedProviderTokens = new(StringComparer.Ordinal)
    {
        "soundtrack", "ost", "dlc", "demo", "artbook", "expansion", "season", "pass"
    };

    private readonly HttpClient http;
    public MetadataClient(HttpMessageHandler? handler = null)
    {
        http = handler == null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(25);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GameLibraryNative/1.0");
    }
    private static string NormalizeTitle(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var result = new StringBuilder();
        string decomposed = value.Normalize(NormalizationForm.FormD);
        char previous = '\0';
        bool previousWasWord = false;
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            // Apostrophes are spelling punctuation, not title-word boundaries:
            // "Witch's" and the Docker-safe "witchs" should compare equally.
            if (character is '\'' or '\u2019') continue;
            if (character is '+' or '#')
            {
                if (result.Length > 0 && result[^1] != ' ') result.Append(' ');
                result.Append(character == '+' ? "plus" : "sharp");
                result.Append(' ');
                previous = '\0';
                previousWasWord = false;
                continue;
            }
            if (!char.IsLetterOrDigit(character))
            {
                if (result.Length > 0 && result[^1] != ' ') result.Append(' ');
                previous = '\0';
                previousWasWord = false;
                continue;
            }

            bool splitWord = previousWasWord &&
                ((char.IsLetter(previous) && char.IsDigit(character)) ||
                 (char.IsDigit(previous) && char.IsLetter(character)));
            if (splitWord && result.Length > 0 && result[^1] != ' ') result.Append(' ');
            result.Append(char.ToLowerInvariant(character));
            previous = character;
            previousWasWord = true;
        }
        return result.ToString().Trim();
    }
    private static string CompactTitle(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var result = new StringBuilder();
        string decomposed = value.Normalize(NormalizationForm.FormD);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark || character is '\'' or '\u2019') continue;
            if (character == '+') result.Append("plus");
            else if (character == '#') result.Append("sharp");
            else if (char.IsLetterOrDigit(character)) result.Append(char.ToLowerInvariant(character));
        }
        return result.ToString();
    }
    private static bool HasIdentityNumberToken(string normalized) =>
        normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(token =>
            token.Any(char.IsDigit) || (token.Length > 0 && token.All(character => character is 'i' or 'v' or 'x' or 'l' or 'c' or 'd' or 'm')));
    public static bool SameTitle(string expected, string actual)
    {
        var normalizedExpected = NormalizeTitle(expected);
        var normalizedActual = NormalizeTitle(actual);
        if (normalizedExpected.Length == 0 || normalizedActual.Length == 0) return false;
        if (normalizedExpected == normalizedActual) return true;
        // Compact comparison repairs harmless provider presentation changes
        // such as "LumenTale" versus "lumentale", but is disabled whenever a
        // numeric or Roman-numeral token could change the game's identity.
        return !HasIdentityNumberToken(normalizedExpected) && !HasIdentityNumberToken(normalizedActual) &&
            CompactTitle(expected) == CompactTitle(actual);
    }
    private static bool TitleMatchesExpected(string expected, string actual)
    {
        if (SameTitle(expected, actual)) return true;
        var expectedTokens = NormalizeTitle(expected).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var actualTokens = NormalizeTitle(actual).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (expectedTokens.Length == 0 || actualTokens.Length == 0) return false;
        if (actualTokens.Any(token => RejectedProviderTokens.Contains(token))) return false;

        // Provider titles often append a subtitle or edition. Require every
        // meaningful tag token in order, rather than accepting arbitrary
        // fuzzy similarity (which previously admitted unrelated titles).
        var required = expectedTokens.Where(token => token is not ("a" or "an" or "and" or "of" or "the")).ToArray();
        if (required.Length < 2) return false;
        int cursor = 0;
        foreach (var token in required)
        {
            int found = Array.IndexOf(actualTokens, token, cursor);
            if (found < 0) return false;
            cursor = found + 1;
        }
        return true;
    }
    private static string ExpectedTitle(Game game) => game.Discovered && game.Id.Length > 0 ? LibraryStore.FormatName(game.Id) : game.Name;
    private static bool IsKnownAlias(Game game, string actual) =>
        KnownProviderAliases.TryGetValue(game.Id, out var aliases) && aliases.Any(alias => SameTitle(alias, actual));
    private static bool IsCuratedProviderOverride(JsonObject metadata) =>
        DataJson.Text(metadata["source"]?["image"]) == "steam-known" && DataJson.Text(metadata["source"]?["time"]) == "known-override";
    internal static bool HasAcceptableDiscoveredTitle(Game game) =>
        TitleMatchesExpected(ExpectedTitle(game), game.Name) || IsKnownAlias(game, game.Name);
    public static bool MatchesGame(Game game, JsonObject metadata)
    {
        if (!string.Equals(DataJson.Text(metadata["id"]), game.Id, StringComparison.Ordinal)) return false;
        string actual = DataJson.Text(metadata["name"]);
        if (TitleMatchesExpected(ExpectedTitle(game), actual)) return true;
        // A non-equivalent title is accepted only for a closed, explicitly
        // curated provider alias and its provider override marker. This
        // rejects false positives such as a same-id RAWG result for an
        // unrelated game.
        return IsCuratedProviderOverride(metadata) && IsKnownAlias(game, actual);
    }
    public async Task<JsonObject> Refresh(Game game, LibraryStore store, bool cover, bool time, CancellationToken cancellation)
    {
        string queryName = ExpectedTitle(game);
        string url = SyncClient.Production + "api/game-metadata?id=" + Uri.EscapeDataString(game.Id) + "&name=" + Uri.EscapeDataString(queryName) + "&category=" + Uri.EscapeDataString(game.Category);
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
        if (game.IsLocal) return false;
        // A previously accepted provider name can be loaded back into a
        // discovered Docker row. Revisit it when it no longer matches the
        // tag identity, but honor the same persisted retry barrier as missing
        // artwork. Otherwise one rejected provider result is selected again
        // on every loop and can keep the UI/logging/storage stack busy forever.
        bool discoveredTitleMismatch = game.Discovered && !MetadataClient.HasAcceptableDiscoveredTitle(game);
        if (!discoveredTitleMismatch && !string.IsNullOrEmpty(game.Cover) && game.Time > 0) return false;
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
