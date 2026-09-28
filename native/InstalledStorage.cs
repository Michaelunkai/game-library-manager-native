using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace GameLibrary.Native;

// SkippedCount counts omitted entries/subtrees or root failures, not the unknown number
// of files beneath an inaccessible directory. Reason retains the first omission unless
// cancellation/a global budget stops the scan. Bytes is partial whenever Complete is false.
public sealed record InstalledStorageResult(long Bytes, bool Complete, DateTime MeasuredUtc,
    long FileCount, long SkippedCount, string? Reason)
{
    public string MeasurementKind => "Logical installed file bytes";
}

public sealed class InstalledStorageOptions
{
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromSeconds(30);
    public long MaxFiles { get; init; } = 1_000_000;
    // Also bound trees containing only directories, and the number of retained handles.
    public long MaxEntries { get; init; } = 2_000_000;
    public int MaxDepth { get; init; } = 128;
}

/// <summary>
/// Measures the caller's known installation directory; never discovers installation roots.
/// Bytes sums logical lengths per file name (hard links count per name), not allocated disk
/// space or download size. No content is read or changed. Complete means traversal finished
/// without omissions, not an atomic snapshot: files can grow/shrink during measurement.
/// Windows only. Reparse points, including any root ancestor, are rejected/skipped.
/// Cancellation returns an incomplete result, rather than throwing away partial measurements.
/// Limits/cancellation are checked between synchronous filesystem operations, which may block.
/// </summary>
public static class InstalledStorage
{
    public static Task<InstalledStorageResult> MeasureAsync(string directory, CancellationToken cancellationToken)
        => MeasureAsync(directory, cancellationToken, new InstalledStorageOptions());

    public static Task<InstalledStorageResult> MeasureAsync(string directory, CancellationToken cancellationToken,
        InstalledStorageOptions options) => MeasureCoreAsync(directory, cancellationToken, options, null);

    // Deterministic fault/cancellation injection for isolated tests; never used by production callers.
    internal static Task<InstalledStorageResult> MeasureCoreAsync(string directory, CancellationToken token,
        InstalledStorageOptions options, Action<string>? beforeInspect)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxDuration < TimeSpan.Zero || options.MaxFiles < 0 || options.MaxEntries < 0 || options.MaxDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        return Task.Run(() => Scan(directory, token, options, beforeInspect));
    }

    private static InstalledStorageResult Scan(string directory, CancellationToken token,
        InstalledStorageOptions options, Action<string>? beforeInspect)
    {
        long bytes = 0, files = 0, skipped = 0, entries = 0;
        string? reason = null;
        var timer = Stopwatch.StartNew();
        var ancestors = new List<SafeFileHandle>();
        var frames = new Stack<Frame>();
        void Omit(string message) { skipped++; reason ??= message; }
        bool Stop()
        {
            if (token.IsCancellationRequested) { reason = "Cancelled"; return true; }
            if (timer.Elapsed >= options.MaxDuration) { reason = "Time limit reached"; return true; }
            return false;
        }
        InstalledStorageResult Result() => new(bytes, reason == null, DateTime.UtcNow, files, skipped, reason);
        try
        {
            if (Stop()) return Result();
            if (!OperatingSystem.IsWindows()) { Omit("Windows metadata measurement is unavailable on this platform"); return Result(); }
            if (string.IsNullOrWhiteSpace(directory)) { Omit("Installation directory is empty"); return Result(); }
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            // Reject device namespaces; normal drive and UNC paths are supported.
            if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal))
            { Omit("Device namespace paths are unsupported"); return Result(); }
            var paths = new Stack<string>();
            for (string? path = full; path != null; path = Path.GetDirectoryName(path)) paths.Push(path);
            while (paths.Count > 0)
            {
                if (Stop()) return Result();
                string path = paths.Pop();
                var handle = Open(path, out var info);
                ancestors.Add(handle);
                if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
                { Omit("Reparse point in installation root: " + path); return Result(); }
                if ((info.Attributes & (uint)FileAttributes.Directory) == 0)
                { Omit("Installation root is not a directory: " + path); return Result(); }
            }
            frames.Push(new Frame(full, 0, null));
            while (frames.Count > 0)
            {
                if (Stop()) break;
                var frame = frames.Peek();
                string entry;
                try
                {
                    if (!frame.Enumerator.MoveNext()) { frames.Pop().Dispose(); continue; }
                    entry = frame.Enumerator.Current;
                }
                catch (Exception ex) when (IsFilesystemError(ex))
                { Omit(frame.Path + ": " + ex.Message); frames.Pop().Dispose(); continue; }
                if (Stop()) break;
                if (entries >= options.MaxEntries) { reason = "Entry limit reached"; break; }
                entries++;
                SafeFileHandle? child = null;
                try
                {
                    beforeInspect?.Invoke(entry);
                    if (Stop()) break;
                    child = Open(entry, out var info);
                    if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
                    { Omit("Reparse point skipped: " + entry); continue; }
                    if ((info.Attributes & (uint)FileAttributes.Directory) != 0)
                    {
                        if (frame.Depth >= options.MaxDepth) { Omit("Depth limit reached: " + entry); continue; }
                        frames.Push(new Frame(entry, frame.Depth + 1, child));
                        child = null; // Frame now owns the pinned directory handle.
                    }
                    else
                    {
                        if (files >= options.MaxFiles) { reason = "File limit reached"; break; }
                        long length = checked((long)(((ulong)info.SizeHigh << 32) | info.SizeLow));
                        bytes = checked(bytes + length);
                        files++;
                    }
                }
                catch (Exception ex) when (IsFilesystemError(ex) || ex is OverflowException)
                { Omit(entry + ": " + ex.Message); }
                finally { child?.Dispose(); }
            }
            // Detect cancellation/time expiry during the final filesystem call too.
            Stop();
        }
        catch (Exception ex) when (IsFilesystemError(ex) || ex is ArgumentException || ex is NotSupportedException)
        { Omit(ex.Message); }
        finally
        {
            while (frames.Count > 0) frames.Pop().Dispose();
            for (int i = ancestors.Count - 1; i >= 0; i--) ancestors[i].Dispose();
        }
        return Result();
    }

    private static bool IsFilesystemError(Exception ex) =>
        ex is IOException || ex is UnauthorizedAccessException || ex is Win32Exception || ex is System.Security.SecurityException;

    private sealed class Frame : IDisposable
    {
        public string Path { get; }
        public int Depth { get; }
        public IEnumerator<string> Enumerator { get; }
        private readonly SafeFileHandle? handle;
        public Frame(string path, int depth, SafeFileHandle? pinned)
        {
            Path = path; Depth = depth; handle = pinned;
            Enumerator = Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions
            { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false }).GetEnumerator();
        }
        public void Dispose() { try { Enumerator.Dispose(); } finally { handle?.Dispose(); } }
    }

    private static SafeFileHandle Open(string path, out FileInformation info)
    {
        // Zero desired access requests metadata only. Omit FILE_SHARE_DELETE so the
        // opened object/ancestor cannot be renamed or replaced during its traversal.
        var handle = CreateFileW(path, 0, 1 | 2, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
        { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, path + ": " + new Win32Exception(error).Message); }
        if (!GetFileInformationByHandle(handle, out info))
        { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, path + ": " + new Win32Exception(error).Message); }
        return handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
}
