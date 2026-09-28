using System;
using System.Text.RegularExpressions;

namespace GameLibrary.Native;

public static class DockerIdentity
{
    public const string CatalogRepository = "michadockermisha/backup";
    public static bool ValidRepository(string repository) => Regex.IsMatch(repository,
        @"\A[a-z0-9][a-z0-9_-]*/[a-z0-9][a-z0-9_.-]*\z");
    public static string Create(string repository, string tag)
    {
        if (!ValidRepository(repository) || !DockerScripts.ValidTag(tag)) throw new ArgumentException("Invalid Docker repository or tag.");
        return repository == CatalogRepository ? tag : "docker:" + repository + ":" + tag;
    }
    public static bool TryParse(string id, out string repository, out string tag)
    {
        repository = CatalogRepository; tag = id;
        if (DockerScripts.ValidTag(id)) return true;
        if (!id.StartsWith("docker:", StringComparison.Ordinal)) return false;
        int split = id.LastIndexOf(':');
        if (split <= 7) return false;
        repository = id[7..split]; tag = id[(split + 1)..];
        return ValidRepository(repository) && DockerScripts.ValidTag(tag) && repository != CatalogRepository;
    }
    public static bool IsQualified(string id) => id.StartsWith("docker:", StringComparison.Ordinal);
    internal static bool RequiresGameIdentity(string id) => IsQualified(id) && TryParse(id, out _, out string tag)
        && Regex.IsMatch(tag, @"\A[0-9]+(?:[._-][0-9]+)*\z");
    internal static string MetadataTitle(string repository, string tag)
    {
        // gmenu.ps1 publishes one folder per repository; the timestamp/hash tag
        // identifies its backup version, not the application title. Restrict this
        // contract to its publisher and exact generated format, never strip title numbers.
        bool generated = repository.StartsWith("michadockermisha/", StringComparison.Ordinal)
            && repository != CatalogRepository && Regex.IsMatch(tag, @"\Agmenu-[0-9]{14}-[0-9a-f]{8}\z")
            && DateTime.TryParseExact(tag.Substring(6, 14), "yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _);
        return LibraryStore.FormatName(repository != CatalogRepository && (tag == "latest" || generated)
            ? repository[(repository.IndexOf('/') + 1)..] : tag);
    }
    public static bool Valid(string id) => TryParse(id, out _, out _);
    public static string Image(Game game, Preferences settings)
    {
        if (!TryParse(game.Id, out string repository, out string tag)) throw new ArgumentException("Invalid Docker game identity.");
        // Existing script-only callers may supply a bare tag with settings and no image.
        // Catalog entries always carry the original repository explicitly.
        string expected = repository + ":" + tag;
        if (game.DockerImage.Length == 0)
            return IsQualified(game.Id) ? expected : settings.DockerUsername + "/" + settings.RepoName + ":" + tag;
        if (!string.Equals(game.DockerImage, expected, StringComparison.Ordinal))
            throw new ArgumentException("The image does not match this game's repository and exact tag.");
        return expected;
    }
}
