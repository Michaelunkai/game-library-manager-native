using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GameLibrary.Native;

// Dependency-free proof for SaveDataLocator. It builds realistic per-engine save
// fixtures under a caller-supplied scratch root, drives the locator with fake
// registry and known-folder probes (no real registry writes), and throws
// InvalidOperationException on the first failed assertion.
internal static class SaveDataLocatorTests
{
    internal static void Run(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("SaveDataLocatorTests requires a scratch root directory.");
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);

        string localApp = Path.Combine(root, "LocalAppData");
        string roaming = Path.Combine(root, "Roaming");
        string savedGames = Path.Combine(root, "Saved Games");
        string programData = Path.Combine(root, "ProgramData");
        string documents = Path.Combine(root, "Documents");
        string localLow = Path.Combine(root, "LocalAppDataLow");
        foreach (string folder in new[] { localApp, roaming, savedGames, programData, documents, localLow })
            Directory.CreateDirectory(folder);

        var folders = new FakeKnownFolders(new Dictionary<KnownFolder, string>
        {
            [KnownFolder.LocalAppData] = localApp,
            [KnownFolder.LocalAppDataLow] = localLow,
            [KnownFolder.RoamingAppData] = roaming,
            [KnownFolder.ProgramData] = programData,
            [KnownFolder.SavedGames] = savedGames,
            [KnownFolder.Documents] = documents
        });
        var noRegistry = new FakeRegistry();
        var evidence = FileSystemSaveEvidenceProbe.Instance;

        SteamUserdataBeatsGenericGuess(root, localApp, folders, noRegistry, evidence);
        EpicSavedGamesIsFound(root, folders, noRegistry, evidence);
        GenericLocalAppDataIsFound(root, localApp, folders, noRegistry, evidence);
        RegistrySaveDirOutranksEngineGuessing(root, localApp, folders, evidence);
        DocumentsMyGamesIsFound(root, localApp, documents, folders, noRegistry, evidence);
        EmptyCandidateLosesToPopulated(root, evidence);
        DirectoryWithoutSaveFilesLosesToSav(root, evidence);
        NoSaveDataAnywhereReturnsNull(root, folders, noRegistry, evidence);
        PermissionDeniedYieldsLowConfidenceCandidate(root, folders, noRegistry);
        CandidatesAreDeduplicatedCaseInsensitively(root, folders, noRegistry);
        OrderingIsDeterministic(root, folders, noRegistry, evidence);
        DescribeNamesEveryCandidateWithEvidence();
        SlugHandlesPunctuationUnicodeAndEmpty();
        UnknownGameWithManualHintResolves(root, folders, noRegistry, evidence);
    }

    private static void SteamUserdataBeatsGenericGuess(string root, string localApp, IKnownFolderProbe folders,
        IRegistryProbe registry, ISaveEvidenceProbe evidence)
    {
        string steamRoot = Path.Combine(root, "Steam");
        string install = Path.Combine(steamRoot, "steamapps", "common", "MyGame");
        Directory.CreateDirectory(install);
        string executable = Path.Combine(install, "MyGame.exe");
        File.WriteAllText(executable, "MZ");
        Directory.CreateDirectory(Path.Combine(steamRoot, "steamapps"));
        File.WriteAllText(Path.Combine(steamRoot, "steamapps", "appmanifest_67890.acf"),
            "\"AppState\"\n{\n\t\"appid\"\t\"67890\"\n\t\"name\"\t\"My Game\"\n\t\"installdir\"\t\"MyGame\"\n}\n");
        string steamSave = SaveFile(Path.Combine(steamRoot, "userdata", "12345", "67890", "remote"), "savegame.sav");
        SaveFile(Path.Combine(localApp, "My", "MyGame"), "save.dat");

        var request = new SaveLocationRequest
        {
            GameId = "steam-" + Guid.NewGuid().ToString("N"),
            DisplayName = "My Game",
            InstallFolder = install,
            ExecutablePath = executable
        };
        var candidates = SaveDataLocator.Locate(request, registry, folders, evidence);
        var selected = SaveDataLocator.Select(candidates, evidence);
        Require(selected != null, "Steam userdata save was not selected.");
        Require(selected!.Kind == SaveKind.SteamUserData,
            "Steam userdata did not beat the generic engine guess; got " + selected.Kind + " at " + selected.Root);
        Require(selected.Root.Equals(Path.GetFullPath(steamSave), StringComparison.OrdinalIgnoreCase),
            "Steam selected the wrong root: " + selected.Root);
    }

    private static void EpicSavedGamesIsFound(string root, IKnownFolderProbe folders, IRegistryProbe registry, ISaveEvidenceProbe evidence)
    {
        string install = Path.Combine(root, "Epic Games", "MyEpicGame");
        string executable = Path.Combine(install, "Binaries", "Win64", "MyEpicGame-Win64-Shipping.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "MZ");
        string epicSave = SaveFile(Path.Combine(install, "Saved", "SaveGames"), "save.sav");

        var request = new SaveLocationRequest
        {
            GameId = "epic-" + Guid.NewGuid().ToString("N"),
            DisplayName = "MyEpicGame",
            InstallFolder = install,
            ExecutablePath = executable
        };
        var candidates = SaveDataLocator.Locate(request, registry, folders, evidence);
        var selected = SaveDataLocator.Select(candidates, evidence);
        Require(selected != null, "Epic Saved\\SaveGames save was not found.");
        Require(selected!.Kind == SaveKind.Epic, "Epic save was not classified as Epic; got " + selected.Kind);
        Require(selected.Root.Equals(Path.GetFullPath(epicSave), StringComparison.OrdinalIgnoreCase),
            "Epic selected the wrong root: " + selected.Root);
    }

    private static void GenericLocalAppDataIsFound(string root, string localApp, IKnownFolderProbe folders,
        IRegistryProbe registry, ISaveEvidenceProbe evidence)
    {
        string genericSave = SaveFile(Path.Combine(localApp, "VendorName", "Puzzle Quest", "Saved"), "profile.dat");

        var request = new SaveLocationRequest
        {
            GameId = "generic-" + Guid.NewGuid().ToString("N"),
            DisplayName = "Puzzle Quest",
            ProcessName = "VendorName"
        };
        var candidates = SaveDataLocator.Locate(request, registry, folders, evidence);
        var selected = SaveDataLocator.Select(candidates, evidence);
        Require(selected != null, "Generic LocalAppData save was not found.");
        Require(selected!.Root.Equals(Path.GetFullPath(genericSave), StringComparison.OrdinalIgnoreCase),
            "Generic LocalAppData selected the wrong root: " + selected.Root);
    }

    private static void RegistrySaveDirOutranksEngineGuessing(string root, string localApp, IKnownFolderProbe folders, ISaveEvidenceProbe evidence)
    {
        string registrySave = SaveFile(Path.Combine(root, "RegistrySaves", "My Game"), "save.sav");
        SaveFile(Path.Combine(localApp, "My", "My Game"), "save.dat");
        var registry = new FakeRegistry(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SaveDir"] = registrySave
        });

        var request = new SaveLocationRequest
        {
            GameId = "registry-" + Guid.NewGuid().ToString("N"),
            DisplayName = "My Game"
        };
        var candidates = SaveDataLocator.Locate(request, registry, folders, evidence);
        var selected = SaveDataLocator.Select(candidates, evidence);
        Require(selected != null, "Registry SaveDir save was not found.");
        Require(selected!.Kind == SaveKind.Registry,
            "Registry SaveDir did not outrank engine guessing; got " + selected.Kind + " at " + selected.Root);
        Require(selected.Root.Equals(Path.GetFullPath(registrySave), StringComparison.OrdinalIgnoreCase),
            "Registry selected the wrong root: " + selected.Root);
        Require(selected.Confidence <= 40,
            "A registry path outside the user-writable roots must be confidence <= 40; got " + selected.Confidence);
    }

    private static void DocumentsMyGamesIsFound(string root, string localApp, string documents, IKnownFolderProbe folders,
        IRegistryProbe registry, ISaveEvidenceProbe evidence)
    {
        SaveFile(Path.Combine(localApp, "My", "My Game"), "save.dat");
        string myGames = SaveFile(Path.Combine(documents, "My Games", "My Game"), "save.sav");

        var request = new SaveLocationRequest
        {
            GameId = "documents-" + Guid.NewGuid().ToString("N"),
            DisplayName = "My Game"
        };
        var candidates = SaveDataLocator.Locate(request, registry, folders, evidence);
        var selected = SaveDataLocator.Select(candidates, evidence);
        Require(selected != null, "Documents\\My Games save was not found.");
        Require(selected!.Root.Equals(Path.GetFullPath(myGames), StringComparison.OrdinalIgnoreCase),
            "Documents\\My Games selected the wrong root: " + selected.Root);
    }

    private static void EmptyCandidateLosesToPopulated(string root, ISaveEvidenceProbe evidence)
    {
        string empty = Path.Combine(root, "empty-candidate");
        Directory.CreateDirectory(empty);
        string populated = SaveFile(Path.Combine(root, "populated-candidate"), "save.sav");
        var candidates = new[]
        {
            new SaveCandidate(empty, SaveKind.LocalAppData, 95, "empty but confident"),
            new SaveCandidate(populated, SaveKind.LocalAppData, 50, "populated")
        };
        var selected = SaveDataLocator.Select(candidates, evidence);
        Require(selected != null, "A populated candidate was not selected over an empty one.");
        Require(selected!.Root.Equals(Path.GetFullPath(populated), StringComparison.OrdinalIgnoreCase),
            "An empty candidate directory beat a populated one: " + selected.Root);
    }

    private static void DirectoryWithoutSaveFilesLosesToSav(string root, ISaveEvidenceProbe evidence)
    {
        string noSave = Path.Combine(root, "no-save-files");
        Directory.CreateDirectory(noSave);
        File.WriteAllText(Path.Combine(noSave, "readme.txt"), "not a save");
        string withSav = SaveFile(Path.Combine(root, "with-sav"), "slot1.sav");
        var candidates = new[]
        {
            new SaveCandidate(noSave, SaveKind.LocalAppData, 95, "present but no save files"),
            new SaveCandidate(withSav, SaveKind.LocalAppData, 50, "contains a .sav")
        };
        var selected = SaveDataLocator.Select(candidates, evidence);
        Require(selected != null, "A directory containing a .sav was not selected.");
        Require(selected!.Root.Equals(Path.GetFullPath(withSav), StringComparison.OrdinalIgnoreCase),
            "A directory without save files beat one containing a .sav: " + selected.Root);
    }

    private static void NoSaveDataAnywhereReturnsNull(string root, IKnownFolderProbe folders, IRegistryProbe registry, ISaveEvidenceProbe evidence)
    {
        var request = new SaveLocationRequest
        {
            GameId = "none-" + Guid.NewGuid().ToString("N"),
            DisplayName = "Completely Absent Game"
        };
        var candidates = SaveDataLocator.Locate(request, registry, folders, evidence);
        var selected = SaveDataLocator.Select(candidates, evidence);
        Require(selected == null, "A game with no save data anywhere returned a candidate instead of null: " + (selected?.Root ?? "null"));
    }

    private static void PermissionDeniedYieldsLowConfidenceCandidate(string root, IKnownFolderProbe folders, IRegistryProbe registry)
    {
        string install = Path.Combine(root, "denied-install");
        Directory.CreateDirectory(install);
        var request = new SaveLocationRequest
        {
            GameId = "denied-" + Guid.NewGuid().ToString("N"),
            DisplayName = "Denied Game",
            InstallFolder = install
        };
        var candidates = SaveDataLocator.Locate(request, registry, folders, new ErrorEvidence());
        var denied = candidates.FirstOrDefault(candidate => candidate.Root.Equals(Path.GetFullPath(install), StringComparison.OrdinalIgnoreCase));
        Require(denied != null, "A permission-denied install folder produced no candidate.");
        Require(denied!.Confidence <= 40, "A permission-denied candidate must be low-confidence; got " + denied.Confidence);
        Require(denied.Evidence.IndexOf("denied", StringComparison.OrdinalIgnoreCase) >= 0
            || denied.Evidence.IndexOf("permission", StringComparison.OrdinalIgnoreCase) >= 0,
            "The permission error was not recorded as evidence: " + denied.Evidence);
        _ = SaveDataLocator.Select(candidates, new ThrowingEvidence());
    }

    private static void CandidatesAreDeduplicatedCaseInsensitively(string root, IKnownFolderProbe folders, IRegistryProbe registry)
    {
        var request = new SaveLocationRequest
        {
            GameId = "dedup-" + Guid.NewGuid().ToString("N"),
            DisplayName = "Dedup Game",
            ManualHintPaths = new[] { @"C:\Saves\DedupGame", @"c:\saves\dedupgame" }
        };
        var candidates = SaveDataLocator.Locate(request, registry, folders, new EmptyEvidence());
        var manualRoots = candidates.Where(candidate => candidate.Kind == SaveKind.Manual)
            .Select(candidate => candidate.Root).ToArray();
        Require(manualRoots.Length == 1,
            "Case-insensitive duplicate hints were not de-duplicated; got " + manualRoots.Length + " manual candidates.");
    }

    private static void OrderingIsDeterministic(string root, IKnownFolderProbe folders, IRegistryProbe registry, ISaveEvidenceProbe evidence)
    {
        var request = new SaveLocationRequest
        {
            GameId = "order-" + Guid.NewGuid().ToString("N"),
            DisplayName = "Order Game",
            ManualHintPaths = new[] { @"C:\Saves\OrderGame" }
        };
        var first = SaveDataLocator.Locate(request, registry, folders, evidence);
        var second = SaveDataLocator.Locate(request, registry, folders, evidence);
        Require(first.Count == second.Count, "Two identical runs produced different candidate counts.");
        for (int index = 0; index < first.Count; index++)
            Require(first[index] == second[index], "Candidate ordering was not deterministic at index " + index + ".");
    }

    private static void DescribeNamesEveryCandidateWithEvidence()
    {
        var candidates = new[]
        {
            new SaveCandidate(@"C:\A", SaveKind.Registry, 90, "registry declared this path"),
            new SaveCandidate(@"C:\B", SaveKind.Generic, 50, "generic engine guess")
        };
        string text = SaveDataLocator.Describe(candidates);
        Require(text.Contains(@"C:\A") && text.Contains("registry declared this path"), "Describe omitted the first candidate or its evidence.");
        Require(text.Contains(@"C:\B") && text.Contains("generic engine guess"), "Describe omitted the second candidate or its evidence.");
        Require(text.Split('\n').Length == 2, "Describe did not emit one line per candidate.");
    }

    private static void SlugHandlesPunctuationUnicodeAndEmpty()
    {
        Require(SaveDataLocator.Slug("") == "", "Slug of empty input must be empty.");
        Require(SaveDataLocator.Slug("   ") == "", "Slug of whitespace must be empty.");
        Require(SaveDataLocator.Slug("---") == "", "Slug of punctuation only must be empty.");
        Require(SaveDataLocator.Slug("My Game!!") == "my-game", "Slug punctuation failed: " + SaveDataLocator.Slug("My Game!!"));
        Require(SaveDataLocator.Slug("Game_Name 2") == "game-name-2", "Slug separator failed: " + SaveDataLocator.Slug("Game_Name 2"));
        Require(SaveDataLocator.Slug("Café Déjà") == "cafe-deja", "Slug unicode failed: " + SaveDataLocator.Slug("Café Déjà"));
        string unicode = SaveDataLocator.Slug("Ωmega");
        Require(unicode.Length > 0 && !unicode.Contains('-'), "Slug unicode-letter handling failed: " + unicode);
    }

    private static void UnknownGameWithManualHintResolves(string root, IKnownFolderProbe folders, IRegistryProbe registry, ISaveEvidenceProbe evidence)
    {
        string hint = SaveFile(Path.Combine(root, "CustomSaves", "Mystery"), "save.sav");
        var request = new SaveLocationRequest
        {
            GameId = "manual-" + Guid.NewGuid().ToString("N"),
            DisplayName = "Totally Unknown Game",
            ManualHintPaths = new[] { hint }
        };
        var candidates = SaveDataLocator.Locate(request, registry, folders, evidence);
        var selected = SaveDataLocator.Select(candidates, evidence);
        Require(selected != null, "A brand-new game with a manual hint did not resolve.");
        Require(selected!.Kind == SaveKind.Manual, "The manual hint was not selected as Manual; got " + selected.Kind);
        Require(selected.Root.Equals(Path.GetFullPath(hint), StringComparison.OrdinalIgnoreCase),
            "The manual hint resolved to the wrong root: " + selected.Root);
    }

    private static string SaveFile(string directory, string name = "save.sav", string content = "progress")
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name), content);
        return directory;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeRegistry : IRegistryProbe
    {
        private readonly Dictionary<string, string> values;
        private readonly List<string> subKeys;

        internal FakeRegistry(Dictionary<string, string>? values = null, List<string>? subKeys = null)
        {
            this.values = values ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            this.subKeys = subKeys ?? new List<string>();
        }

        public IReadOnlyDictionary<string, string> ReadValues(string vendor, string game) => values;
        public IReadOnlyList<string> ReadSubKeyNames(string vendor, string game) => subKeys;
        public IReadOnlyDictionary<string, string> ReadValues(string vendor, string game, string subKey) => values;
    }

    private sealed class FakeKnownFolders : IKnownFolderProbe
    {
        private readonly Dictionary<KnownFolder, string> map;

        internal FakeKnownFolders(Dictionary<KnownFolder, string> map) => this.map = map;

        public string? Resolve(KnownFolder folder) => map.TryGetValue(folder, out string? value) ? value : null;
    }

    private sealed class ErrorEvidence : ISaveEvidenceProbe
    {
        public SaveEvidence Inspect(string root) =>
            new(false, false, false, false, "Access to the path '" + root + "' is denied.", "permission denied: Access is denied");
    }

    private sealed class ThrowingEvidence : ISaveEvidenceProbe
    {
        public SaveEvidence Inspect(string root) => throw new UnauthorizedAccessException("Access is denied.");
    }

    private sealed class EmptyEvidence : ISaveEvidenceProbe
    {
        public SaveEvidence Inspect(string root) => SaveEvidence.Missing;
    }
}
