using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

internal static class ImportSyncTests
{
    internal static void AddChecks(Action<string, Action> check, string root)
    {
        check("Import waits for deferred sync and survives its final save and restart",
            () => WaitsForSync(Path.Combine(root, "import-deferred-sync")).GetAwaiter().GetResult());
        check("Import rechecks queued edits after a deferred sync before replacing state",
            () => RechecksPending(Path.Combine(root, "import-pending-race")).GetAwaiter().GetResult());
        check("Cancelling an import waiting for sync preserves the current library",
            () => CancelsWhileWaiting(Path.Combine(root, "import-cancelled")).GetAwaiter().GetResult());
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task WaitsForSync(string root)
    {
        var store = new LibraryStore(root);
        var current = new UserState(); current.Wishlist.Add("before-import"); store.Save(current);
        var handler = new DeferredConfigHandler();
        using var client = new SyncClient(store, handler);
        var refresh = client.Refresh(current, false, null);
        try
        {
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var imported = new UserState(); imported.Wishlist.Add("imported"); imported.Settings.Theme = "light";
            bool applied = false, prepared = false;
            var import = client.ImportStateAsync(() => current, _ => { prepared = true; return imported; }, next => { current = next; applied = true; });
            Require(!import.IsCompleted && !applied && !prepared && store.LoadState().Wishlist.Contains("before-import"),
                "Import committed before the active refresh had finished saving the old state.");
            handler.Release.TrySetResult(true);
            await Task.WhenAll(refresh, import).WaitAsync(TimeSpan.FromSeconds(5));
            var restarted = new LibraryStore(root).LoadState();
            Require(applied && ReferenceEquals(current, imported) && restarted.Wishlist.SetEquals(new[] { "imported" }) && restarted.Settings.Theme == "light",
                "The old refresh overwrote the imported preferences.");
            string backup = Directory.GetFiles(root, "state.json.before-import-*").Single();
            Require(DataJson.Read<UserState>(File.ReadAllText(backup)).Wishlist.Contains("before-import"), "The previous library was not preserved.");
            await client.Refresh(current, false, null).WaitAsync(TimeSpan.FromSeconds(5));
            Require(store.LoadState().Wishlist.SetEquals(new[] { "imported" }), "The next refresh did not use the imported state.");
        }
        finally { handler.Release.TrySetResult(true); await refresh.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private static async Task RechecksPending(string root)
    {
        var store = new LibraryStore(root);
        var current = new UserState(); store.Save(current);
        var handler = new DeferredConfigHandler();
        using var client = new SyncClient(store, handler);
        var refresh = client.Refresh(current, false, null);
        try
        {
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            bool applied = false;
            var import = client.ImportStateAsync(() => current, _ => new UserState(), next => { current = next; applied = true; });
            client.Queue(current, "gameCategories", "edited-during-wait", JsonValue.Create("new"));
            handler.Release.TrySetResult(true);
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
            bool rejected = false;
            try { await import.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("queued shared changes", StringComparison.Ordinal)) { rejected = true; }
            Require(rejected && !applied && store.LoadState().Pending.Any(e => e.Key == "edited-during-wait"),
                "Import discarded a queued edit added while it waited for sync.");
            Require(Directory.GetFiles(root, "state.json.before-import-*").Length == 0, "A rejected import reached the commit step.");
        }
        finally { handler.Release.TrySetResult(true); await refresh.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private static async Task CancelsWhileWaiting(string root)
    {
        var store = new LibraryStore(root);
        var current = new UserState(); current.GameTags["existing"] = new() { "keep" }; store.Save(current);
        var handler = new DeferredConfigHandler();
        using var client = new SyncClient(store, handler);
        using var cancellation = new CancellationTokenSource();
        var refresh = client.Refresh(current, false, null);
        try
        {
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            bool applied = false;
            var import = client.ImportStateAsync(() => current, _ => new UserState(), _ => applied = true, cancellation.Token);
            cancellation.Cancel();
            bool cancelled = false;
            try { await import.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled && !applied && store.LoadState().GameTags["existing"].Contains("keep"), "Cancelling the waiting import changed the library.");
            handler.Release.TrySetResult(true);
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
            Require(store.LoadState().GameTags["existing"].Contains("keep") && Directory.GetFiles(root, "state.json.before-import-*").Length == 0,
                "The cancelled import committed after sync finished.");
        }
        finally { handler.Release.TrySetResult(true); await refresh.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private sealed class DeferredConfigHandler : HttpMessageHandler
    {
        internal readonly TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get || request.RequestUri?.AbsolutePath != "/api/admin-config")
                throw new InvalidOperationException("Unexpected request in the isolated import fixture.");
            Started.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"success\":true,\"configVersion\":\"fixture-v1\",\"config\":{\"gameCategories\":{},\"hiddenTabs\":[]}}", Encoding.UTF8, "application/json")
            };
        }
    }
}
