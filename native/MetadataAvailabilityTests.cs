using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

public static class MetadataAvailabilityTests
{
    public static int Run(string root) => Task.Run(() => RunAsync(root)).GetAwaiter().GetResult();
    private static async Task<int> RunAsync(string root)
    {
        int checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        var store = new LibraryStore(Path.Combine(root, "metadata-outage-" + Guid.NewGuid().ToString("N")));
        var now = DateTimeOffset.UtcNow;
        var gate = new MetadataAvailability(() => now);
        var handler = new Fixture();
        using var first = new MetadataClient(handler, gate);
        using var second = new MetadataClient(handler, gate);
        async Task<bool> Unavailable(MetadataClient client)
        {
            try { await client.Refresh(new Game { Id = "game", Name = "Game" }, store, false, true, CancellationToken.None); return false; }
            catch (MetadataUnavailableException) { return true; }
        }
        Check(await Unavailable(first) && handler.Calls == 1 && gate.Waiting, "503 did not open service cooldown.");
        Check(await Unavailable(second) && handler.Calls == 1, "A new client bypassed shared cooldown.");
        now = now.AddSeconds(29);
        Check(await Unavailable(first) && handler.Calls == 1, "Retried before cooldown elapsed.");
        now = now.AddSeconds(1); handler.Status = HttpStatusCode.OK;
        await first.Refresh(new Game { Id = "game", Name = "Game" }, store, false, true, CancellationToken.None);
        Check(handler.Calls == 2 && !gate.Waiting && gate.RetryAt == default, "Recovery did not reset cooldown.");
        handler.Status = HttpStatusCode.TooManyRequests;
        Check(await Unavailable(first) && gate.RetryAt == now.AddSeconds(30), "429 cooldown or recovery reset is incorrect.");
        now = gate.RetryAt; handler.Status = HttpStatusCode.NotFound;
        bool notFound = false;
        try { await first.Refresh(new Game { Id = "missing", Name = "Missing" }, store, false, true, CancellationToken.None); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { notFound = true; }
        Check(notFound && !gate.Waiting, "A missing title blocked the entire provider.");
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); int before = handler.Calls;
        bool cancelled = false;
        try { await first.Refresh(new Game { Id = "game", Name = "Game" }, store, false, true, cancel.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && handler.Calls == before && !gate.Waiting, "User cancellation changed service availability.");
        for (int i = 0; i < 9; i++) { gate.Failed(); now = gate.RetryAt; }
        gate.Failed();
        Check(gate.RetryAt - now == TimeSpan.FromMinutes(5), "Metadata backoff is not bounded.");
        return checks;
    }
    private sealed class Fixture : HttpMessageHandler
    {
        public int Calls;
        public HttpStatusCode Status = HttpStatusCode.ServiceUnavailable;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(
                "{\"success\":true,\"id\":\"game\",\"name\":\"Game\",\"time\":12,\"source\":{\"time\":\"fixture\"}}", Encoding.UTF8, "application/json") });
        }
    }
}
