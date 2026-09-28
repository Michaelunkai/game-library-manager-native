using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

/// <summary>
/// Proves that any number of games can install at the same time: each game gets
/// its own staging and digest-scoped installation folder, distinct downloads and
/// promotions proceed concurrently, and only the same installation path is
/// serialized for its short replacement window.
/// </summary>
internal static class ParallelInstallTests
{
    internal static void Run(string root)
    {
        const int Count = 6;
        string destination = Path.Combine(root, "parallel-install-destination");
        Directory.CreateDirectory(destination);

        var staging = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Count; i++)
        {
            string sourceId = DockerIdentity.Create("proofowner/proofrepo", "game-" + i);
            string digest = "sha256:" + new string((char)('a' + i), 64);
            var record = new InstallJobRecord
            {
                CanonicalGameId = "canonical:game-" + i,
                SourceGameId = sourceId,
                SourceGameIds = new List<string> { sourceId },
                ImageRepository = "proofowner/proofrepo",
                ImageTag = "game-" + i,
                DestinationPath = destination,
                StagingPath = Path.Combine(destination, DockerScripts.StagingDirectoryName, DockerScripts.InstallFolder(sourceId) + "-" + Guid.NewGuid().ToString("N")),
                InstalledPath = Path.Combine(destination, DockerScripts.VersionedInstallFolder(sourceId, digest))
            };
            record.Validate();
            if (!staging.Add(record.StagingPath) || !installed.Add(record.InstalledPath))
                throw new InvalidOperationException("Two games resolved to the same install folder.");
        }

        var coordinator = new GameOperationCoordinator();
        var downloads = new List<Task<IDisposable>>();
        for (int i = 0; i < Count; i++)
            downloads.Add(coordinator.AcquireDownloadAsync("canonical:game-" + i, destination, CancellationToken.None));
        if (!Task.WhenAll(downloads).Wait(TimeSpan.FromSeconds(10)))
            throw new InvalidOperationException("Distinct game downloads did not all run at the same time.");
        foreach (var task in downloads) task.Result.Dispose();

        var promotions = new List<Task<IDisposable>>();
        for (int i = 0; i < Count; i++)
            promotions.Add(coordinator.AcquirePromotionAsync("canonical:game-" + i, Path.Combine(destination, "install-" + i), CancellationToken.None));
        if (!Task.WhenAll(promotions).Wait(TimeSpan.FromSeconds(10)))
            throw new InvalidOperationException("Distinct game promotions did not all run at the same time.");
        foreach (var task in promotions) task.Result.Dispose();

        string shared = Path.Combine(destination, "shared-install");
        IDisposable first = coordinator.AcquirePromotionAsync("canonical:shared", shared, CancellationToken.None).GetAwaiter().GetResult();
        Task<IDisposable> second = coordinator.AcquirePromotionAsync("canonical:shared", shared, CancellationToken.None);
        if (second.Wait(TimeSpan.FromMilliseconds(400)))
            throw new InvalidOperationException("Two promotions of the same installation path ran at once.");
        first.Dispose();
        if (!second.Wait(TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("A waiting promotion did not resume after the replacement window closed.");
        second.Result.Dispose();
    }
}
