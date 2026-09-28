using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

/// <summary>
/// Pulls an image through the Docker Engine API and streams authoritative
/// per-layer byte progress. A piped `docker pull` stays silent while a large
/// layer downloads; the Engine API reports `progressDetail.current/total`, so
/// the install terminal can show a percentage that climbs continuously instead
/// of sitting flat until a layer boundary.
/// </summary>
internal sealed class DockerEnginePull
{
    private const string ApiPath = "/v1.43/images/create";
    private readonly string dockerExecutable;
    private readonly string? context;
    private readonly string? host;

    internal DockerEnginePull(string dockerExecutable, string? context, string? host)
    {
        this.dockerExecutable = dockerExecutable;
        this.context = context;
        this.host = host;
    }

    /// <summary>Returns "OK" on success or an "ERROR:"-prefixed message.</summary>
    internal async Task<string> PullAsync(
        string repository,
        string reference,
        InstallJobStage stage,
        IProgress<InstallJobProgress> progress,
        IReadOnlyDictionary<string, long>? expectedLayerBytes,
        long imageTotalBytes,
        CancellationToken cancellation)
    {
        string engineHost = await ResolveEngineHostAsync(cancellation).ConfigureAwait(false);
        if (engineHost.StartsWith("npipe://", StringComparison.OrdinalIgnoreCase))
            return await PullViaPipeAsync(engineHost, repository, reference, stage, progress, expectedLayerBytes, imageTotalBytes, cancellation).ConfigureAwait(false);
        if (engineHost.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase) || engineHost.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return await PullViaTcpAsync(engineHost, repository, reference, stage, progress, expectedLayerBytes, imageTotalBytes, cancellation).ConfigureAwait(false);
        throw new InvalidOperationException("Unsupported Docker engine endpoint: " + engineHost);
    }

    private async Task<string> ResolveEngineHostAsync(CancellationToken cancellation)
    {
        if (!string.IsNullOrWhiteSpace(host)) return host.Trim();
        if (string.IsNullOrWhiteSpace(context)) return "npipe:////./pipe/docker_engine";
        var psi = new ProcessStartInfo(dockerExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("--context"); psi.ArgumentList.Add(context);
        psi.ArgumentList.Add("context"); psi.ArgumentList.Add("inspect"); psi.ArgumentList.Add(context);
        psi.ArgumentList.Add("--format"); psi.ArgumentList.Add("{{.Endpoints.docker.Host}}");
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not resolve the Docker engine endpoint.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellation);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellation);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
        catch { if (!process.HasExited) { try { process.Kill(entireProcessTree: true); } catch { } } throw; }
        await error.ConfigureAwait(false);
        string result = (await output.ConfigureAwait(false)).Trim();
        if (result.Length == 0) throw new InvalidOperationException("Docker did not report an engine endpoint.");
        return result;
    }

    private async Task<string> PullViaTcpAsync(string engineHost, string repository, string reference, InstallJobStage stage,
        IProgress<InstallJobProgress> progress, IReadOnlyDictionary<string, long>? expectedLayerBytes, long imageTotalBytes, CancellationToken cancellation)
    {
        string baseUrl = engineHost.Replace("tcp://", "http://", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        string url = baseUrl + ApiPath + "?fromImage=" + Uri.EscapeDataString(repository + "@" + reference);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Headers = { Host = "docker" } };
        request.Content = new ByteArrayContent(Array.Empty<byte>());
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string errBody = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
            return "ERROR:HTTP " + (int)response.StatusCode + " " + errBody;
        }
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        return await ConsumePullStreamAsync(stream, stage, progress, expectedLayerBytes, imageTotalBytes, cancellation).ConfigureAwait(false);
    }

    private async Task<string> PullViaPipeAsync(string engineHost, string repository, string reference, InstallJobStage stage,
        IProgress<InstallJobProgress> progress, IReadOnlyDictionary<string, long>? expectedLayerBytes, long imageTotalBytes, CancellationToken cancellation)
    {
        string pipeName = ParsePipeName(engineHost);
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        connectDeadline.CancelAfter(TimeSpan.FromSeconds(30));
        await pipe.ConnectAsync(connectDeadline.Token).ConfigureAwait(false);
        string path = ApiPath + "?fromImage=" + Uri.EscapeDataString(repository + "@" + reference);
        string request = "POST " + path + " HTTP/1.1\r\nHost: docker\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        byte[] requestBytes = Encoding.ASCII.GetBytes(request);
        await pipe.WriteAsync(requestBytes, 0, requestBytes.Length, cancellation).ConfigureAwait(false);
        return await ConsumePullStreamAsync(pipe, stage, progress, expectedLayerBytes, imageTotalBytes, cancellation).ConfigureAwait(false);
    }

    private static string ParsePipeName(string engineHost)
    {
        // npipe:////./pipe/NAME  or  npipe://./pipe/NAME
        int idx = engineHost.IndexOf("pipe/", StringComparison.OrdinalIgnoreCase);
        string name = idx >= 0 ? engineHost[(idx + 5)..] : engineHost;
        return name.Trim().TrimStart('\\').TrimStart('/');
    }

    private static async Task<string> ConsumePullStreamAsync(Stream stream, InstallJobStage stage,
        IProgress<InstallJobProgress> progress, IReadOnlyDictionary<string, long>? expectedLayerBytes, long imageTotalBytes, CancellationToken cancellation)
    {
        // Response head (status line + headers).
        var headerBytes = new List<byte>(8192);
        int statusCode = 0;
        bool headDone = false;
        var pendingLine = new StringBuilder();
        var firstBody = new List<byte>();
        var buffer = new byte[65536];
        bool inHeaders = true;
        string? error = null;
        var layerCurrent = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var layerTotal = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var layerDone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long lastActivityUtc = DateTimeOffset.UtcNow.UtcTicks;
        DateTimeOffset started = DateTimeOffset.UtcNow;

        using var watchdog = new CancellationTokenSource();
        Task heartbeat = Task.Run(async () =>
        {
            while (!watchdog.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(3), watchdog.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                if (watchdog.IsCancellationRequested) break;
                long completed = AggregateCompleted(layerCurrent, layerDone, expectedLayerBytes, layerTotal);
                long total = imageTotalBytes > 0 ? imageTotalBytes : layerTotal.Values.Sum();
                double? pct = total > 0 ? 100.0 * completed / total : null;
                var quietFor = DateTimeOffset.UtcNow - new DateTimeOffset(Interlocked.Read(ref lastActivityUtc), TimeSpan.Zero);
                var elapsed = DateTimeOffset.UtcNow - started;
                progress.Report(new InstallJobProgress(DateTime.UtcNow, stage,
                    "Pull still running (" + (int)elapsed.TotalSeconds + "s elapsed, last engine event " + (int)quietFor.TotalSeconds + "s ago)."
                    + (pct is double v ? " Transfer so far: " + completed + "/" + total + " B (" + v.ToString("0.000", CultureInfo.InvariantCulture) + "%)." : ""),
                    CompletedBytes: total > 0 ? completed : null,
                    TotalBytes: total > 0 ? total : null));
            }
        }, watchdog.Token);

        void ProcessLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            if (line.Length <= 8 && line.All(c => Uri.IsHexDigit(c))) return; // chunk-size frame
            JsonObject? doc;
            try { doc = JsonNode.Parse(line) as JsonObject; } catch { return; }
            if (doc == null) return;
            Interlocked.Exchange(ref lastActivityUtc, DateTimeOffset.UtcNow.UtcTicks);
            string id = DataJson.Text(doc["id"]);
            string status = DataJson.Text(doc["status"]);
            if (doc["errorDetail"] is JsonObject || doc["error"] != null)
            {
                string message = DataJson.Text(doc["errorDetail"]?["message"], DataJson.Text(doc["error"]));
                if (error == null) error = message.Length == 0 ? "Docker pull failed." : message;
                return;
            }
            JsonObject? detail = doc["progressDetail"] as JsonObject;
            long current = (long)DataJson.Number(detail?["current"]);
            long total = (long)DataJson.Number(detail?["total"]);
            if (id.Length > 0)
            {
                if (current > 0) layerCurrent[id] = Math.Max(layerCurrent.TryGetValue(id, out long c) ? c : 0, current);
                if (total > 0) layerTotal[id] = Math.Max(layerTotal.TryGetValue(id, out long t) ? t : 0, total);
                if (status is "Download complete" or "Pull complete" or "Already exists" or "Extracting") layerDone.Add(id);
            }
            long completed = AggregateCompleted(layerCurrent, layerDone, expectedLayerBytes, layerTotal);
            long aggregateTotal = imageTotalBytes > 0 ? imageTotalBytes : layerTotal.Values.Sum();
            if (aggregateTotal > 0 && completed > aggregateTotal) completed = aggregateTotal;
            progress.Report(new InstallJobProgress(DateTime.UtcNow, stage,
                string.IsNullOrWhiteSpace(id) ? status : id + ": " + status,
                CompletedBytes: aggregateTotal > 0 ? completed : null,
                TotalBytes: aggregateTotal > 0 ? aggregateTotal : null));
        }

        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellation).ConfigureAwait(false);
                if (read <= 0) break;
                int position = 0;
                if (inHeaders)
                {
                    // consume until \r\n\r\n
                    int i = 0;
                    for (; i < read; i++)
                    {
                        headerBytes.Add(buffer[i]);
                        int count = headerBytes.Count;
                        if (count >= 4 && headerBytes[count - 4] == '\r' && headerBytes[count - 3] == '\n' && headerBytes[count - 2] == '\r' && headerBytes[count - 1] == '\n')
                        {
                            string head = Encoding.ASCII.GetString(headerBytes.ToArray(), 0, count - 4);
                            string[] lines = head.Split('\n');
                            string statusLine = lines.Length > 0 ? lines[0].Trim() : "";
                            if (statusLine.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
                            {
                                string[] parts = statusLine.Split(' ');
                                if (parts.Length >= 2) int.TryParse(parts[1], out statusCode);
                            }
                            headDone = true;
                            inHeaders = false;
                            position = i + 1;
                            break;
                        }
                    }
                    if (!headDone && headerBytes.Count > 65536) throw new InvalidDataException("Docker engine response headers are too large.");
                    if (!headDone) continue;
                }
                for (; position < read; position++)
                {
                    char c = (char)buffer[position];
                    if (c == '\n') { ProcessLine(pendingLine.ToString()); pendingLine.Clear(); }
                    else if (c != '\r') pendingLine.Append(c);
                }
            }
            if (pendingLine.Length > 0) ProcessLine(pendingLine.ToString());
            if (!headDone) throw new IOException("Docker engine returned no HTTP response.");
            if (statusCode != 0 && statusCode != 200) return "ERROR:HTTP " + statusCode;
            if (error != null) return "ERROR:" + error;
            return "OK";
        }
        finally
        {
            watchdog.Cancel();
            try { await heartbeat.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private static long AggregateCompleted(IReadOnlyDictionary<string, long> current, ISet<string> done,
        IReadOnlyDictionary<string, long>? expected, IReadOnlyDictionary<string, long> totals)
    {
        long completed = 0;
        foreach (var pair in current)
        {
            long size = expected != null && expected.TryGetValue(pair.Key, out long s) ? s : 0;
            if (done.Contains(pair.Key)) completed += size > 0 ? size : (totals.TryGetValue(pair.Key, out long t) ? t : pair.Value);
            else completed += size > 0 ? Math.Min(pair.Value, size) : pair.Value;
        }
        return completed;
    }
}