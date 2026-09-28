using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

/// <summary>Isolated fixtures are retained beneath root; no files are deleted or moved.</summary>
public static class InstalledStorageTests
{
    public static int Run(string root) => Task.Run(() => RunAsync(root)).GetAwaiter().GetResult();

    private static async Task<int> RunAsync(string root)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows storage tests");
        string suite = Path.Combine(root, "installed-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(suite);
        int passed = 0;
        async Task Check(string name, Func<string, Task> test)
        {
            string path = Path.Combine(suite, name);
            Directory.CreateDirectory(path);
            try { await test(path); passed++; }
            catch (Exception ex) { throw new InvalidOperationException("Installed storage [" + name + "]: " + ex.Message, ex); }
        }
        Task<InstalledStorageResult> Measure(string path, InstalledStorageOptions? options = null) =>
            InstalledStorage.MeasureAsync(path, CancellationToken.None, options ?? new InstalledStorageOptions());
        await Check("known-nested-content", async path =>
        {
            byte[] content = Enumerable.Range(0, 251).Select(i => (byte)i).ToArray();
            File.WriteAllBytes(Path.Combine(path, "game.bin"), content);
            string nested = Directory.CreateDirectory(Path.Combine(path, "a", "b")).FullName;
            File.WriteAllBytes(Path.Combine(nested, "other.bin"), new byte[19]);
            DateTime before = DateTime.UtcNow;
            var result = await Measure(path);
            Exact(result, 270, 2);
            Require(result.MeasuredUtc.Kind == DateTimeKind.Utc && result.MeasuredUtc >= before && result.MeasuredUtc <= DateTime.UtcNow, "Invalid measurement time");
            Require(result.MeasurementKind == "Logical installed file bytes", "Missing semantic label");
            Require(File.ReadAllBytes(Path.Combine(path, "game.bin")).SequenceEqual(content), "Content changed");
        });
        await Check("empty-directory", async path => Exact(await Measure(path), 0, 0));
        await Check("zero-length-file", async path =>
        { File.WriteAllBytes(Path.Combine(path, "empty"), Array.Empty<byte>()); Exact(await Measure(path), 0, 1); });
        await Check("missing-root", async path => Incomplete(await Measure(Path.Combine(path, "missing"))));
        await Check("file-as-root", async path =>
        { string file = Path.Combine(path, "file"); File.WriteAllBytes(file, new byte[3]); Incomplete(await Measure(file)); });
        await Check("blank-root", async _ => Incomplete(await Measure(" ")));
        await Check("cancelled-before-scan", async path =>
        {
            using var source = new CancellationTokenSource(); source.Cancel();
            var result = await InstalledStorage.MeasureAsync(path, source.Token);
            Incomplete(result); Require(result.Reason == "Cancelled" && result.FileCount == 0, "Cancellation lost");
        });
        await Check("cancelled-during-scan", async path =>
        {
            File.WriteAllBytes(Path.Combine(path, "file"), new byte[7]);
            using var source = new CancellationTokenSource();
            var result = await InstalledStorage.MeasureCoreAsync(path, source.Token, new InstalledStorageOptions(), _ => source.Cancel());
            Incomplete(result); Require(result.Reason == "Cancelled" && result.Bytes == 0, "Cancellation was ignored");
        });
        await Check("time-budget", async path =>
        { var result = await Measure(path, new InstalledStorageOptions { MaxDuration = TimeSpan.Zero }); Incomplete(result); Require(result.Reason == "Time limit reached", "Wrong limit"); });
        await Check("file-budget", async path =>
        {
            File.WriteAllBytes(Path.Combine(path, "a"), new byte[5]); File.WriteAllBytes(Path.Combine(path, "b"), new byte[5]);
            var result = await Measure(path, new InstalledStorageOptions { MaxFiles = 1 });
            Incomplete(result); Require(result.Bytes == 5 && result.FileCount == 1 && result.Reason == "File limit reached", "File budget exceeded");
        });
        await Check("exact-file-budget", async path =>
        { File.WriteAllBytes(Path.Combine(path, "a"), new byte[5]); Exact(await Measure(path, new InstalledStorageOptions { MaxFiles = 1 }), 5, 1); });
        await Check("entry-budget", async path =>
        { Directory.CreateDirectory(Path.Combine(path, "a")); Incomplete(await Measure(path, new InstalledStorageOptions { MaxEntries = 0 })); });
        await Check("depth-budget", async path =>
        { Directory.CreateDirectory(Path.Combine(path, "a")); Incomplete(await Measure(path, new InstalledStorageOptions { MaxDepth = 0 })); });
        foreach (string fault in new[] { "inaccessible", "disappeared", "locked" })
            await Check(fault, async path =>
            {
                File.WriteAllBytes(Path.Combine(path, "good"), new byte[11]);
                File.WriteAllBytes(Path.Combine(path, "bad"), new byte[19]);
                var result = await InstalledStorage.MeasureCoreAsync(path, CancellationToken.None, new InstalledStorageOptions(), entry =>
                {
                    if (Path.GetFileName(entry) != "bad") return;
                    if (fault == "inaccessible") throw new UnauthorizedAccessException("Injected access denied");
                    if (fault == "disappeared") throw new FileNotFoundException("Injected disappearance");
                    throw new IOException("Injected sharing violation");
                });
                Incomplete(result);
                Require(result.Bytes == 11 && result.FileCount == 1 && result.SkippedCount == 1, "Partial result lost or mislabeled");
                Require(File.ReadAllBytes(Path.Combine(path, "bad")).Length == 19, "Fault test changed fixture");
            });
        await Check("exclusive-content-lock", async path =>
        {
            string file = Path.Combine(path, "file"); File.WriteAllBytes(file, new byte[23]);
            using var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var result = await Measure(path);
            // Windows may permit metadata-only handles even when content access is exclusive.
            if (result.Complete) Exact(result, 23, 1);
            else { Incomplete(result); Require(result.Bytes == 0 && result.FileCount == 0, "Failed metadata counted"); }
        });
        await Check("invalid-options", async path =>
        {
            bool rejected = false;
            try { await Measure(path, new InstalledStorageOptions { MaxFiles = -1 }); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Require(rejected, "Invalid options accepted");
        });

        // A privilege denial skips only link checks; it must not mask a measurement failure.
        string target = Directory.CreateDirectory(Path.Combine(suite, "link-target")).FullName;
        File.WriteAllBytes(Path.Combine(target, "outside"), new byte[101]);
        string linkedTree = Directory.CreateDirectory(Path.Combine(suite, "link-tree")).FullName;
        string link = Path.Combine(linkedTree, "link");
        bool linkCreated = false;
        try { Directory.CreateSymbolicLink(link, target); linkCreated = true; }
        catch (UnauthorizedAccessException) { }
        catch (IOException ex) when ((ex.HResult & 0xffff) == 1314 || (ex.HResult & 0xffff) == 50) { }
        if (linkCreated)
        {
            await Check("linked-child", async _ =>
            { var result = await Measure(linkedTree); Incomplete(result); Require(result.Bytes == 0 && result.FileCount == 0 && result.SkippedCount == 1, "Followed linked child"); });
            await Check("linked-root", async _ =>
            { var result = await Measure(link); Incomplete(result); Require(result.Bytes == 0, "Followed linked root"); });
            string below = Directory.CreateDirectory(Path.Combine(target, "below")).FullName;
            File.WriteAllBytes(Path.Combine(below, "file"), new byte[31]);
            await Check("linked-ancestor", async _ =>
            { var result = await Measure(Path.Combine(link, "below")); Incomplete(result); Require(result.Bytes == 0, "Followed linked ancestor"); });
        }
        else Console.WriteLine("InstalledStorageTests: 3 directory-link checks skipped (symlink unavailable).");
        string fileLinkTree = Directory.CreateDirectory(Path.Combine(suite, "file-link-tree")).FullName;
        string fileLink = Path.Combine(fileLinkTree, "linked-file");
        bool fileLinkCreated = false;
        try { File.CreateSymbolicLink(fileLink, Path.Combine(target, "outside")); fileLinkCreated = true; }
        catch (UnauthorizedAccessException) { }
        catch (IOException ex) when ((ex.HResult & 0xffff) == 1314 || (ex.HResult & 0xffff) == 50) { }
        if (fileLinkCreated)
            await Check("linked-file", async _ =>
            { var result = await Measure(fileLinkTree); Incomplete(result); Require(result.Bytes == 0 && result.FileCount == 0 && result.SkippedCount == 1, "Followed file symlink"); });
        else Console.WriteLine("InstalledStorageTests: file-link check skipped (symlink unavailable).");
        return passed;
    }

    private static void Exact(InstalledStorageResult result, long bytes, long count) =>
        Require(result.Complete && result.Bytes == bytes && result.FileCount == count && result.SkippedCount == 0 && result.Reason == null,
            "Expected complete logical total; got " + result);
    private static void Incomplete(InstalledStorageResult result) =>
        Require(!result.Complete && !string.IsNullOrWhiteSpace(result.Reason), "Incomplete scan presented as exact");
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
