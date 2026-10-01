using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace GameLibrary.Native;

internal static class ArtworkFile
{
    private static readonly Dictionary<string, (string Revision, BitmapSource? Image)> cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, long> replacements = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> replacementLru = new();
    private static readonly Dictionary<string, LinkedListNode<string>> replacementLruIndex = new(StringComparer.OrdinalIgnoreCase);
    private static long sequence;
    internal static void Replaced(string path)
    {
        lock (cache)
        {
            // Evict the least-recently-used replacement marker instead of clearing them
            // all and bumping the epoch. Bumping the epoch invalidates every cached
            // revision at once, which forces a full re-hash and re-decode of every
            // visible cover - the same scroll-freeze cliff as before.
            if (replacementLruIndex.TryGetValue(path, out var node)) { replacementLru.Remove(node); replacementLru.AddFirst(node); }
            else
            {
                replacementLruIndex[path] = replacementLru.AddFirst(path);
                while (replacementLru.Count > MaxCachedArtwork)
                {
                    var oldest = replacementLru.Last;
                    if (oldest is null) break;
                    replacementLru.RemoveLast();
                    replacementLruIndex.Remove(oldest.Value);
                    replacements.Remove(oldest.Value);
                }
            }
            replacements[path] = ++sequence;
            Interlocked.Increment(ref generation);
        }
    }
    /// <summary>
    /// NOT memoized, deliberately. A revision comes from file stat fields, and anything
    /// outside this process can rewrite a cover file - a user, a repair tool, a sync
    /// client - without ever calling Replaced. Memoizing made the cache blind to those
    /// writers, and the self-test caught it: a cover damaged by an external write was
    /// never detected, so the automatic repair never ran. A real stat here is far below
    /// the SHA-256 that used to sit on this path; correctness wins.
    /// </summary>

    /// <summary>
    /// Bumped every time artwork is replaced. Cover caching keys on this rather than on
    /// the revision string, because a repaired file can have byte-identical stat fields:
    /// NTFS timestamp tunnelling and coarse clocks preserve size, mtime and ctime across
    /// a same-length replacement, so a revision-keyed cache would keep serving the stale
    /// fallback. A replacement is an explicit event, so keying on it is both cheaper and
    /// correct where stat-based keying cannot be.
    /// </summary>
    internal static long Generation => Interlocked.Read(ref generation);
    private static long generation;

    /// <summary>
    /// NOT memoized, deliberately. A revision comes from file stat fields, and anything
    /// outside this process can rewrite a cover file - a user, a repair tool, a sync
    /// client - without ever calling Replaced. Memoizing made the cache blind to those
    /// writers and the self-test caught it: a cover damaged by an external write was
    /// never detected, so the automatic repair never ran. A real stat here costs far
    /// less than the SHA-256 that used to sit on this path, and correctness wins.
    /// </summary>
    internal static string Revision(string path) => ComputeRevision(path);

    /// <summary>No-op: there is no memo left to invalidate.</summary>
    internal static void Invalidate(string path) { }

    private static string ComputeRevision(string path)
    {
        try
        {
            var file = new FileInfo(path);
            lock (cache)
                return file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}:{file.CreationTimeUtc.Ticks}:{replacements.GetValueOrDefault(path)}" : "missing";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return "unreadable"; }
    }

    internal static BitmapSource? Load(string path)
    {
        string revision = Revision(path);
        if (revision is "" or "missing" or "unreadable" || path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return null;
        lock (cache)
        {
            // A memoized revision cannot observe deletion, because nothing tells the
            // cache that a file was removed. Existence is the one check that is both
            // cheap enough to run on every conversion and sufficient to catch it: a
            // single existence probe instead of the FileInfo stat plus SHA-256 that
            // made this the dominant cost of the worst scroll steps.
            if (cache.TryGetValue(path, out var known) && known.Revision == revision && File.Exists(path)) { TrackUse(path); return known.Image; }
        }
        BitmapSource? image = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // Cached .img files are content-addressed. A decodable but wrong file
            // at that address is also invalid artwork, not proof of game identity.
            string name = Path.GetFileNameWithoutExtension(path);
            if (Path.GetExtension(path).Equals(".img", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(name, @"\A[0-9a-fA-F]{64}\z"))
            {
                if (stream.Length > 8 * 1024 * 1024 || !Convert.ToHexString(SHA256.HashData(stream)).Equals(name, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Artwork content does not match its cache address.");
                stream.Position = 0;
            }
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.DecodePixelHeight = 180;
            bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit();
            if (bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0) { bitmap.Freeze(); image = bitmap; }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException or FormatException or System.Runtime.InteropServices.COMException) { }
        lock (cache)
        {
            // Eviction is LRU, driven by TrackUse below; never a wholesale Clear.
            cache[path] = (revision, image);
        }
        TrackUse(path);
        return image;
    }
    /// <summary>
    /// Most-recently-used eviction, shared by the artwork and cover caches.
    /// <para>
    /// The previous behaviour was a wholesale <c>Clear()</c> once the cache reached
    /// its bound. That is a latency cliff rather than a memory guard: every cover
    /// already on screen was evicted at once, and the next scroll had to re-hash and
    /// re-decode each one synchronously on the UI thread - and a content-addressed
    /// .img miss SHA256-hashes the whole file, up to 8 MB. That is exactly the
    /// "freezes for a few seconds every so often while scrolling" symptom. Evicting
    /// only the least-recently-used entries keeps the working set warm and removes
    /// the cliff.
    /// </para>
    /// </summary>
    private static readonly LinkedList<string> artworkLru = new();
    private static readonly Dictionary<string, LinkedListNode<string>> artworkLruIndex = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxCachedArtwork = 2500;

    private static void TrackUse(string key)
    {
        lock (cache)
        {
            if (artworkLruIndex.TryGetValue(key, out var existing))
            {
                artworkLru.Remove(existing);
                artworkLru.AddFirst(existing);
                return;
            }
            var node = artworkLru.AddFirst(key);
            artworkLruIndex[key] = node;
            while (artworkLru.Count > MaxCachedArtwork)
            {
                var oldest = artworkLru.Last;
                if (oldest is null) break;
                artworkLru.RemoveLast();
                artworkLruIndex.Remove(oldest.Value);
                cache.Remove(oldest.Value);
            }
        }
    }

    internal static bool IsUsable(string path) => Load(path) != null;
}
