using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

/// <summary>
/// Deterministic coverage for real-time install progress parsing: Docker layer
/// byte ratios become an aggregate byte/percent, extraction files become a
/// file/percent, and the terminal renders a live percentage.
/// </summary>
internal static class InstallProgressTests
{
    internal static void Run(string root)
    {
        var download = InstallProcessRunner.ParseLayerBytes("Downloading [====>            ]  4.0MB/8.0MB");
        if (download.Done != 4L * 1024 * 1024 || download.Total != 8L * 1024 * 1024)
            throw new InvalidOperationException("A download layer ratio was not parsed into bytes.");
        var extract = InstallProcessRunner.ParseLayerBytes("Extracting [==>   ]  1.0MB/4.0MB");
        if (extract.Done != 1L * 1024 * 1024 || extract.Total != 4L * 1024 * 1024)
            throw new InvalidOperationException("An extraction layer ratio was not parsed into bytes.");
        var none = InstallProcessRunner.ParseLayerBytes("Pull complete");
        if (none.Done >= 0 || !InstallProcessRunner.IsLayerComplete("Pull complete"))
            throw new InvalidOperationException("A ratio-less completion line was misclassified.");
        if (!InstallProcessRunner.IsLayerComplete("Already exists") || !InstallProcessRunner.IsLayerComplete("Download complete"))
            throw new InvalidOperationException("A cached/completed layer was not recognized.");

        var layerBytes = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["aaaaaaaa"] = new[] { download.Done, download.Total },
            ["bbbbbbbb"] = new[] { 2L * 1024 * 1024, 4L * 1024 * 1024 }
        };
        var aggregate = InstallProcessRunner.AggregateBytes(layerBytes);
        if (aggregate.Completed != 6L * 1024 * 1024 || aggregate.Total != 12L * 1024 * 1024)
            throw new InvalidOperationException("Aggregate layer bytes were not summed.");
        double percent = 100.0 * aggregate.Completed / aggregate.Total;
        if (percent < 49.9 || percent > 50.1)
            throw new InvalidOperationException("Aggregate byte percent was wrong: " + percent);

        var byteEvent = new InstallJobEvent(DateTime.UtcNow, "progress", InstallJobStage.Pull.ToString(), "Downloading 6.0MB/12.0MB",
            CompletedBytes: aggregate.Completed, TotalBytes: aggregate.Total);
        double? rendered = InstallJobTerminalHost.ProgressPercent(byteEvent);
        if (rendered is null || Math.Abs(rendered.Value - 50.0) > 0.1)
            throw new InvalidOperationException("The terminal did not render the byte percentage.");
        if (!InstallJobTerminalHost.FormatEvent(byteEvent).Contains("50.000%", StringComparison.Ordinal))
            throw new InvalidOperationException("The rendered terminal line omitted the percentage.");

        var fileEvent = new InstallJobEvent(DateTime.UtcNow, "progress", InstallJobStage.Extract.ToString(), "Verified staged payload file 3/12.",
            CompletedFiles: 3, TotalFiles: 12);
        double? filePercent = InstallJobTerminalHost.ProgressPercent(fileEvent);
        if (filePercent is null || Math.Abs(filePercent.Value - 25.0) > 0.001)
            throw new InvalidOperationException("The terminal did not render the extraction file percentage.");

        // Cached layers complete without a byte ratio but still count by their
        // exact manifest blob size; silent layers contribute only the total, so
        // the aggregate percentage is exact for everything Docker has told us.
        var layerText = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["aaaaaaaaaaaa"] = "Already exists",
            ["bbbbbbbbbbbb"] = "Downloading 1.0KB/2.0KB"
        };
        var layerSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["aaaaaaaaaaaa"] = 1000L,
            ["bbbbbbbbbbbb"] = 2000L
        };
        var sized = DockerProgressBytes.Compute(layerText, layerSizes, 3000L);
        if (sized.Completed != 2024L || sized.Total != 3000L)
            throw new InvalidOperationException("Cached and transferring layers were not aggregated by exact blob size.");
        double? sizedPercent = DockerProgressBytes.Percent(sized.Completed, sized.Total);
        if (sizedPercent is null || Math.Abs(sizedPercent.Value - 67.4666667) > 0.001)
            throw new InvalidOperationException("The exact pull percentage was wrong: " + sizedPercent);
        if (!InstallJobTerminalHost.FormatEvent(new InstallJobEvent(DateTime.UtcNow, "progress", InstallJobStage.Pull.ToString(), "Downloading 1.0KB/2.0KB",
            CompletedBytes: sized.Completed, TotalBytes: sized.Total)).Contains("67.467%", StringComparison.Ordinal))
            throw new InvalidOperationException("The rendered terminal line omitted the exact percentage.");

        var silentText = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cccccccccccc"] = "Pulling fs layer"
        };
        var silentSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["cccccccccccc"] = 5000L
        };
        var silent = DockerProgressBytes.Compute(silentText, silentSizes, 5000L);
        if (silent.Completed != 0 || silent.Total != 5000L)
            throw new InvalidOperationException("A silent layer must not fabricate progress.");

        // Control responses are written camelCase; every reader must bind them
        // case-insensitively or the terminal reports a mismatched job identity
        // and silently drops the acknowledgement.
        var response = new InstallJobControlResponse(1, "0123456789abcdef0123456789abcdef", "req0123456789abcdef0123456789abcdef", true, "accepted", DateTime.UtcNow);
        string responsePath = System.IO.Path.Combine(root, "control-response-fixture.json");
        LibraryStore.AtomicWrite(responsePath, JsonSerializer.Serialize(response, InstallJobStore.SharedJsonOptions));
        InstallJobControlResponse? readback = InstallJobStore.ReadControlResponse(responsePath);
        if (readback is null || !string.Equals(readback.OperationId, response.OperationId, StringComparison.Ordinal)
            || !string.Equals(readback.RequestId, response.RequestId, StringComparison.Ordinal) || !readback.Accepted)
            throw new InvalidOperationException("A camelCase control response did not round-trip with its exact identity.");

        // End-to-end pause acknowledgement: the controller writes a control
        // request, the worker writes the response, and the controller reads it
        // back with its exact identity instead of ignoring it as a mismatch.
        string profile = Path.Combine(root, "control-ack");
        Directory.CreateDirectory(profile);
        var store = new InstallJobStore(profile);
        string operation = Guid.NewGuid().ToString("N");
        string sourceId = DockerIdentity.Create("proofowner/proofrepo", "ack");
        string destination = Path.Combine(root, "control-ack-destination");
        var job = new InstallJobRecord
        {
            OperationId = operation,
            CanonicalGameId = "canonical:ack",
            SourceGameId = sourceId,
            SourceGameIds = new List<string> { sourceId },
            ImageRepository = "proofowner/proofrepo",
            ImageTag = "ack",
            DestinationPath = destination,
            StagingPath = Path.Combine(destination, DockerScripts.StagingDirectoryName, DockerScripts.InstallFolder(sourceId) + "-" + operation),
            InstalledPath = Path.Combine(destination, "installed-ack")
        };
        store.Create(job);
        var controller = new InstallJobController(store, profile, Path.Combine(root, "unused-GameLibrary.exe"));
        string requestId = controller.SendControl(operation, InstallJobControlAction.Pause);
        store.SaveControlResponse(new InstallJobControlResponse(1, operation, requestId, true, "accepted", DateTime.UtcNow));
        InstallJobControlResponse? ack = controller.WaitForResponseAsync(operation, requestId, TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        if (ack is null || !string.Equals(ack.OperationId, operation, StringComparison.Ordinal)
            || !string.Equals(ack.RequestId, requestId, StringComparison.Ordinal) || !ack.Accepted)
            throw new InvalidOperationException("A pause control acknowledgement was ignored instead of accepted.");

        // A piped docker pull emits no output while a large layer downloads, so
        // a silent command must never be killed while its Docker daemon is alive.
        // Only an unhealthy daemon may end the command as inactivity.
        string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        ProcessStartInfo SilentPowershell()
        {
            var start = new ProcessStartInfo(powershell)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("Start-Sleep -Seconds 5");
            return start;
        }
        var policy = new InstallTimeoutPolicy { DownloadInactivity = TimeSpan.FromSeconds(1), HealthCheck = TimeSpan.FromSeconds(1) };
        Func<CancellationToken, Task<bool>> healthy = _ => Task.FromResult(true);
        var healthyRunner = new InstallProcessRunner(timeouts: policy);
        InstallProcessResult healthyResult = healthyRunner.RunAsync(SilentPowershell(), InstallCommandKind.Download, healthCheck: healthy).GetAwaiter().GetResult();
        if (!healthyResult.Succeeded)
            throw new InvalidOperationException("A healthy Docker daemon must not turn a silent download into a stall.");

        Func<CancellationToken, Task<bool>> unhealthy = _ => Task.FromResult(false);
        var unhealthyRunner = new InstallProcessRunner(timeouts: policy);
        InstallProcessResult unhealthyResult = unhealthyRunner.RunAsync(SilentPowershell(), InstallCommandKind.Download, healthCheck: unhealthy).GetAwaiter().GetResult();
        if (unhealthyResult.Termination != InstallProcessTermination.Inactivity)
            throw new InvalidOperationException("An unhealthy Docker daemon should end a silent command instead of waiting forever.");
    }
}
