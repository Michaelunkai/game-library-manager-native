using System;
using System.IO;
using System.Threading.Tasks;

namespace GameLibrary.Native;

internal static class AtomicWriteTests
{
    internal static void Run(string root)
    {
        string folder = Path.Combine(root, "atomic-write-backup-retry");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "state.json");
        string backup = path + ".bak";
        File.WriteAllText(path, "old-state");
        File.WriteAllText(backup, "older-state");

        using var backupReader = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read);
        Task release = Task.Run(async () =>
        {
            await Task.Delay(250);
            backupReader.Dispose();
        });
        try { LibraryStore.AtomicWrite(path, "new-state"); }
        finally { release.GetAwaiter().GetResult(); }

        if (File.ReadAllText(path) != "new-state" || File.ReadAllText(backup) != "old-state")
            throw new InvalidOperationException("A transiently locked backup did not retain the prior state after atomic replacement.");
    }
}
