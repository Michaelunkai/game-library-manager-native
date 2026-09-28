using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

/// <summary>
/// Reproduces concurrent manifest access during an install. The durable worker
/// rewrites the manifest on every progress event while the terminal, the parent
/// window and recovery controllers read it, so a replacement must never expose
/// a missing or locked manifest to a reader.
/// </summary>
internal static class InstallJobConcurrencyTests
{
    internal static void Run(string root)
    {
        string profile = Path.Combine(root, "install-job-concurrency");
        Directory.CreateDirectory(profile);
        var store = new InstallJobStore(profile);
        InstallJobRecord job = CreateValidRecord(root);
        store.Create(job);

        var errors = new List<Exception>();
        using var start = new ManualResetEventSlim(false);
        Task writer = Task.Run(() =>
        {
            start.Wait();
            try
            {
                for (int i = 0; i < 1500; i++)
                {
                    job.Stage = (InstallJobStage)(i % Enum.GetValues<InstallJobStage>().Length);
                    store.Save(job);
                    store.RecordActivity(job, new InstallJobEvent(DateTime.UtcNow, "progress", job.Stage.ToString(), "progress " + i));
                }
            }
            catch (Exception ex) { lock (errors) errors.Add(ex); }
        });
        Task reader = Task.Run(() =>
        {
            start.Wait();
            try
            {
                for (int i = 0; i < 3000; i++)
                {
                    InstallJobRecord loaded = store.Load(job.OperationId);
                    if (!string.Equals(loaded.OperationId, job.OperationId, StringComparison.Ordinal))
                        throw new InvalidDataException("A concurrent manifest read returned a different operation identity.");
                    store.ReadEvents(job.OperationId, 50);
                }
            }
            catch (Exception ex) { lock (errors) errors.Add(ex); }
        });

        start.Set();
        Task.WaitAll(writer, reader);
        if (errors.Count > 0) throw new AggregateException("Concurrent install-job manifest access failed.", errors);
    }

    private static InstallJobRecord CreateValidRecord(string root)
    {
        string destination = Path.Combine(root, "install-job-concurrency-destination");
        string sourceId = DockerIdentity.Create("proofowner/proofrepo", "proof");
        return new InstallJobRecord
        {
            CanonicalGameId = "concurrency-proof",
            SourceGameId = sourceId,
            SourceGameIds = new List<string> { sourceId },
            ImageRepository = "proofowner/proofrepo",
            ImageTag = "proof",
            DestinationPath = destination,
            StagingPath = Path.Combine(destination, ".staging"),
            InstalledPath = Path.Combine(destination, "installed")
        };
    }
}
