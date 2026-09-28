using System;
using System.IO;

namespace GameLibrary.Native;

internal static class InstalledMetadataIdentity
{
    // A folder suffix alone is not evidence of a build number. Require the
    // installer marker and its exact deterministic folder identity together.
    internal static string Title(string folder, string? executable = null)
    {
        // A package tag can be an opaque or misspelled label. The manifest's
        // primary task identifies the actual selected game executable.
        string published = GogInstalledIdentity.Title(folder, executable);
        return published.Length > 0 ? published : InstallerTitle(folder);
    }
    private static string InstallerTitle(string folder)
    {
        try
        {
            if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder) ||
                (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return "";
            string marker = Path.Combine(folder, DockerScripts.CompletionMarkerName);
            var file = new FileInfo(marker);
            if (!file.Exists || file.Length > 1024 || (file.Attributes & FileAttributes.ReparsePoint) != 0) return "";
            string[] fields = File.ReadAllText(marker).Trim().Split('|');
            if (fields.Length is < 2 or > 3 || fields[0] != "GameLibraryManager" || !DockerIdentity.Valid(fields[1])) return "";
            string id = fields[1];
            string folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
            bool legacyFolder = string.Equals(folderName, DockerScripts.InstallFolder(id), StringComparison.OrdinalIgnoreCase);
            bool versionedFolder = DockerScripts.IsVersionedInstallFolder(id, folderName) && fields.Length == 3 && Guid.TryParseExact(fields[2], "N", out _);
            if (!legacyFolder && !versionedFolder) return "";
            if (DockerIdentity.IsQualified(id))
            {
                if (!DockerIdentity.TryParse(id, out var repository, out var tag)) return "";
                return DockerIdentity.MetadataTitle(repository, tag);
            }
            return LibraryStore.FormatName(id);
        }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
        catch (ArgumentException) { return ""; }
    }
}
