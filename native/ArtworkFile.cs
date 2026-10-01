using System;
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
            cache.Remove(path);
        }
    }
    internal static string Revision(string path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path)) return "";
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
            if (cache.TryGetValue(path, out var known) && known.Revision == revision) { TrackUse(path); return known.Image; }
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
