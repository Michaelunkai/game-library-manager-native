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
    private static long sequence, epoch;
    internal static void Replaced(string path)
    {
        lock (cache)
        {
            if (replacements.Count >= 2500) { replacements.Clear(); epoch = ++sequence; }
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
                return file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}:{file.CreationTimeUtc.Ticks}:{epoch}:{replacements.GetValueOrDefault(path)}" : "missing";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return "unreadable"; }
    }

    internal static BitmapSource? Load(string path)
    {
        string revision = Revision(path);
        if (revision is "" or "missing" or "unreadable" || path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return null;
        lock (cache)
        {
            if (cache.TryGetValue(path, out var known) && known.Revision == revision) return known.Image;
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
            if (cache.Count >= 2500) cache.Clear();
            cache[path] = (revision, image);
        }
        return image;
    }
    internal static bool IsUsable(string path) => Load(path) != null;
}
