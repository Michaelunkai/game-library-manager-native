using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GameLibrary.Native;

// The engine-agnostic save-data locator. It answers one question for every
// game, including ones it has never seen: "where does this game actually write
// its progress?" The answer is an ordered, auditable candidate list rather than
// a single blind guess, because a wrong path silently backs up nothing.
internal enum SaveKind
{
    Cloud,
    Registry,
    Document,
    LocalAppData,
    AppData,
    ProgramData,
    SteamUserData,
    GOG,
    Epic,
    Ubisoft,
    Generic,
    Manual
}

internal sealed record SaveCandidate(string Root, SaveKind Kind, int Confidence, string Evidence);

internal enum KnownFolder
{
    SavedGames,
    LocalAppData,
    LocalAppDataLow,
    RoamingAppData,
    ProgramData,
    Documents
}

// Registry access is abstracted so the locator can be unit-tested without
// touching the real registry.
internal interface IRegistryProbe
{
    IReadOnlyDictionary<string, string> ReadValues(string vendor, string game);
    IReadOnlyList<string> ReadSubKeyNames(string vendor, string game);
    IReadOnlyDictionary<string, string> ReadValues(string vendor, string game, string subKey);
}

// Known-folder resolution is abstracted so tests can supply a hermetic scratch
// layout instead of the real profile folders.
internal interface IKnownFolderProbe
{
    string? Resolve(KnownFolder folder);
}

internal sealed record SaveEvidence(
    bool DirectoryExists,
    bool HasSaveFile,
    bool HasNonEmptySaveFile,
    bool HasConfigHint,
    string? Error,
    string Detail)
{
    internal static readonly SaveEvidence Missing = new(false, false, false, false, null, "directory does not exist");
}

// Directory inspection is abstracted so Select can be driven with fakes and so a
// permission-denied directory becomes evidence instead of an exception.
internal interface ISaveEvidenceProbe
{
    SaveEvidence Inspect(string root);
}

internal sealed record SaveLocationRequest
{
    public string GameId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string InstallFolder { get; init; } = "";
    public string ExecutablePath { get; init; } = "";
    public string MountPath { get; init; } = "";
    public string ProcessName { get; init; } = "";
    public IReadOnlyList<string> ManualHintPaths { get; init; } = Array.Empty<string>();
}

internal static class SaveDataLocator
{
    private const int MaxCacheEntries = 256;

    private static readonly ConcurrentDictionary<string, IReadOnlyList<SaveCandidate>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] SaveValueNames = { "SaveDir", "SavePath", "SavedGames", "Saves" };
    private static readonly string[] DataValueNames = { "InstallPath", "DataDir", "ConfigDir" };

    private static readonly string[] SaveSubfolders =
        { @"Saved\SaveGames", "Saved", "save", "saves", "SaveData", "SaveGames", "Saves" };

    private static readonly string[] ElectronSubfolders = { "Local Storage", "leveldb" };

    // ---- Public entry points -------------------------------------------------

    internal static IReadOnlyList<SaveCandidate> Locate(SaveLocationRequest request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (!string.IsNullOrWhiteSpace(request.GameId) && Cache.TryGetValue(request.GameId, out var cached))
            return cached;
        var result = Locate(request, Win32RegistryProbe.Instance, Win32KnownFolderProbe.Instance, FileSystemSaveEvidenceProbe.Instance);
        if (!string.IsNullOrWhiteSpace(request.GameId))
        {
            TrimCache();
            Cache[request.GameId] = result;
        }
        return result;
    }

    internal static IReadOnlyList<SaveCandidate> Locate(
        SaveLocationRequest request,
        IRegistryProbe registry,
        IKnownFolderProbe knownFolders,
        ISaveEvidenceProbe evidence)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        registry ??= EmptyRegistryProbe.Instance;
        knownFolders ??= EmptyKnownFolderProbe.Instance;
        evidence ??= FileSystemSaveEvidenceProbe.Instance;

        var raw = new List<SaveCandidate>();
        var allowed = BuildAllowedRoots(request, knownFolders);

        AddRegistry(raw, request, registry, allowed);
        AddEngineRoots(raw, request, knownFolders, allowed);
        AddKnownFolderRoots(raw, request, knownFolders, allowed);
        AddConfigSniffing(raw, request, knownFolders, evidence, allowed);
        AddManual(raw, request, allowed);

        return Order(Dedupe(raw));
    }

    internal static SaveCandidate? Select(IReadOnlyList<SaveCandidate> candidates, ISaveEvidenceProbe probe)
    {
        if (candidates == null || candidates.Count == 0) return null;
        probe ??= FileSystemSaveEvidenceProbe.Instance;
        var scored = new List<(SaveCandidate Candidate, SaveEvidence Evidence, int Rank)>(candidates.Count);
        foreach (var candidate in candidates)
        {
            SaveEvidence evidence = SafeInspect(probe, candidate.Root);
            int rank = !evidence.DirectoryExists ? 4
                : evidence.HasNonEmptySaveFile ? 0
                : evidence.HasSaveFile ? 1
                : 2;
            scored.Add((candidate, evidence, rank));
        }

        var eligible = scored
            .Where(item => item.Rank < 4 && (item.Rank < 2 || item.Candidate.Confidence >= 50))
            .ToList();
        if (eligible.Count == 0) return null;

        eligible.Sort((a, b) =>
        {
            int rank = a.Rank.CompareTo(b.Rank);
            if (rank != 0) return rank;
            // A registry-declared path wins outright; manual hints yield to any
            // engine-derived candidate; within the engine tier the most
            // confident candidate wins.
            bool aRegistry = a.Candidate.Kind == SaveKind.Registry;
            bool bRegistry = b.Candidate.Kind == SaveKind.Registry;
            if (aRegistry != bRegistry) return aRegistry ? -1 : 1;
            bool aManual = a.Candidate.Kind == SaveKind.Manual;
            bool bManual = b.Candidate.Kind == SaveKind.Manual;
            if (aManual != bManual) return aManual ? 1 : -1;
            int confidence = b.Candidate.Confidence.CompareTo(a.Candidate.Confidence);
            if (confidence != 0) return confidence;
            int kind = KindPriority(a.Candidate.Kind).CompareTo(KindPriority(b.Candidate.Kind));
            if (kind != 0) return kind;
            return string.Compare(a.Candidate.Root, b.Candidate.Root, StringComparison.OrdinalIgnoreCase);
        });
        return eligible[0].Candidate;
    }

    internal static string Describe(IReadOnlyList<SaveCandidate> candidates)
    {
        if (candidates == null || candidates.Count == 0) return "No save-data candidates were found.";
        var builder = new StringBuilder();
        foreach (var candidate in candidates)
        {
            builder.Append('[').Append(candidate.Confidence).Append("] ")
                .Append(candidate.Kind).Append(": ").Append(candidate.Root)
                .Append(" - ").Append(candidate.Evidence).AppendLine();
        }
        return builder.ToString().TrimEnd('\r', '\n');
    }

    internal static string Slug(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        string normalized = name.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        bool pendingDash = false;
        foreach (char character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(character))
            {
                if (pendingDash && builder.Length > 0) builder.Append('-');
                pendingDash = false;
                builder.Append(char.ToLowerInvariant(character));
            }
            else
            {
                pendingDash = true;
            }
        }
        return builder.ToString();
    }

    internal static void Invalidate(string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return;
        Cache.TryRemove(gameId, out _);
    }

    // ---- Layered discovery ---------------------------------------------------

    private static void AddRegistry(List<SaveCandidate> raw, SaveLocationRequest request, IRegistryProbe registry, IReadOnlyList<string> allowed)
    {
        foreach (string vendor in VendorVariants(request))
        {
            foreach (string game in GameVariants(request))
            {
                AddRegistryAt(raw, request, allowed, registry, vendor, game, null, 95);
                foreach (string subKey in SafeSubKeys(registry, vendor, game))
                    AddRegistryAt(raw, request, allowed, registry, vendor, game, subKey, 70);
            }
        }
    }

    private static void AddRegistryAt(List<SaveCandidate> raw, SaveLocationRequest request, IReadOnlyList<string> allowed,
        IRegistryProbe registry, string vendor, string game, string? subKey, int baseConfidence)
    {
        IReadOnlyDictionary<string, string> values;
        try
        {
            values = subKey == null ? registry.ReadValues(vendor, game) : registry.ReadValues(vendor, game, subKey);
        }
        catch { return; }
        if (values == null) return;

        string label = subKey == null ? vendor + "\\" + game : vendor + "\\" + game + "\\" + subKey;
        foreach (var pair in values)
        {
            if (string.IsNullOrWhiteSpace(pair.Value)) continue;
            bool isSave = SaveValueNames.Any(name => name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
            bool isData = DataValueNames.Any(name => name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
            if (!isSave && !isData) continue;
            int confidence = isSave ? baseConfidence : baseConfidence - 15;
            string evidence = "Registry HKCU\\Software\\" + label + " value '" + pair.Key + "' declared this path.";
            AddCandidate(raw, request, new SaveCandidate(pair.Value, SaveKind.Registry, confidence, evidence), allowed, registryOrManual: true);
        }
    }

    private static void AddEngineRoots(List<SaveCandidate> raw, SaveLocationRequest request, IKnownFolderProbe knownFolders, IReadOnlyList<string> allowed)
    {
        string executable = request.ExecutablePath ?? "";
        string install = request.InstallFolder ?? "";
        string process = request.ProcessName ?? "";

        bool steam = Contains(executable, "steamapps") || Contains(install, "steamapps") || Contains(process, "steam") || Contains(executable, "\\Steam\\");
        bool epic = Contains(executable, "Epic Games") || Contains(install, "Epic Games") || Contains(process, "Epic");
        bool gog = Contains(executable, "GOG") || Contains(install, "GOG") || Contains(install, "goggames") || Contains(process, "gog");
        bool ubisoft = Contains(executable, "Ubisoft") || Contains(install, "Ubisoft") || Contains(process, "ubisoft");

        if (steam) AddSteam(raw, request, allowed);
        if (epic) AddEpic(raw, request, knownFolders, allowed);
        if (gog) AddGog(raw, request, knownFolders, allowed);
        if (ubisoft) AddUbisoft(raw, request, knownFolders, allowed);
        AddGenericEngine(raw, request, knownFolders, allowed);
    }

    private static void AddSteam(List<SaveCandidate> raw, SaveLocationRequest request, IReadOnlyList<string> allowed)
    {
        string? steamApps = FindSteamApps(request.InstallFolder, request.ExecutablePath);
        if (steamApps == null) return;
        string? steamRoot = Directory.GetParent(steamApps)?.FullName;
        if (steamRoot == null) return;
        string userdata = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userdata)) return;

        var appIds = SteamAppIds(steamApps, request);
        foreach (string steamId in SafeDirectories(userdata))
        {
            if (appIds.Count == 0)
            {
                foreach (string appId in SafeDirectories(steamId))
                    AddCandidate(raw, request, new SaveCandidate(Path.Combine(appId, "remote"), SaveKind.SteamUserData, 60,
                        "Steam userdata folder derived from the install path (no appmanifest matched)."), allowed, false);
                continue;
            }
            foreach (string appId in appIds)
            {
                string remote = Path.Combine(steamId, appId, "remote");
                AddCandidate(raw, request, new SaveCandidate(remote, SaveKind.SteamUserData, 88,
                    "Steam userdata\\<steamid>\\" + appId + "\\remote matched from appmanifest_" + appId + ".acf."), allowed, false);
                AddCandidate(raw, request, new SaveCandidate(Path.Combine(steamId, appId), SaveKind.SteamUserData, 80,
                    "Steam userdata\\<steamid>\\" + appId + " matched from appmanifest_" + appId + ".acf."), allowed, false);
            }
        }
    }

    private static void AddEpic(List<SaveCandidate> raw, SaveLocationRequest request, IKnownFolderProbe knownFolders, IReadOnlyList<string> allowed)
    {
        string? localApp = knownFolders.Resolve(KnownFolder.LocalAppData);
        string install = request.InstallFolder ?? "";
        if (!string.IsNullOrWhiteSpace(install))
        {
            AddCandidate(raw, request, new SaveCandidate(Path.Combine(install, "Saved", "SaveGames"), SaveKind.Epic, 85,
                "Unreal/Epic 'Saved\\SaveGames' under the install folder."), allowed, false);
            AddCandidate(raw, request, new SaveCandidate(Path.Combine(install, "Saved"), SaveKind.Epic, 72,
                "Unreal/Epic 'Saved' folder under the install folder."), allowed, false);
        }
        if (string.IsNullOrWhiteSpace(localApp)) return;
        foreach (string game in GameVariants(request))
        {
            AddCandidate(raw, request, new SaveCandidate(Path.Combine(localApp, "Epic Games", game, "Saved", "SaveGames"), SaveKind.Epic, 84,
                "Epic Launcher saved-games root for '" + game + "'."), allowed, false);
            AddCandidate(raw, request, new SaveCandidate(Path.Combine(localApp, game, "Saved", "SaveGames"), SaveKind.Epic, 80,
                "Epic/Unreal per-game Saved\\SaveGames under LocalAppData."), allowed, false);
        }
        AddCandidate(raw, request, new SaveCandidate(Path.Combine(localApp, "Epic Games", "Saved", "SaveGames"), SaveKind.Epic, 70,
            "Epic Launcher shared Saved\\SaveGames root."), allowed, false);
    }

    private static void AddGog(List<SaveCandidate> raw, SaveLocationRequest request, IKnownFolderProbe knownFolders, IReadOnlyList<string> allowed)
    {
        string? documents = knownFolders.Resolve(KnownFolder.Documents);
        if (string.IsNullOrWhiteSpace(documents)) return;
        foreach (string game in GameVariants(request))
            AddCandidate(raw, request, new SaveCandidate(Path.Combine(documents, "My Games", game), SaveKind.GOG, 74,
                "GOG install detected; GOG titles commonly save under Documents\\My Games\\" + game + "."), allowed, false);
    }

    private static void AddUbisoft(List<SaveCandidate> raw, SaveLocationRequest request, IKnownFolderProbe knownFolders, IReadOnlyList<string> allowed)
    {
        string? localApp = knownFolders.Resolve(KnownFolder.LocalAppData);
        string? documents = knownFolders.Resolve(KnownFolder.Documents);
        if (!string.IsNullOrWhiteSpace(localApp))
        {
            AddCandidate(raw, request, new SaveCandidate(Path.Combine(localApp, "Ubisoft Game Store", "Saved"), SaveKind.Ubisoft, 76,
                "Ubisoft Game Store saved-games root under LocalAppData."), allowed, false);
            AddCandidate(raw, request, new SaveCandidate(Path.Combine(localApp, "Ubisoft Game Launcher", "savegames"), SaveKind.Ubisoft, 70,
                "Ubisoft Game Launcher savegames root under LocalAppData."), allowed, false);
        }
        if (!string.IsNullOrWhiteSpace(documents))
            foreach (string game in GameVariants(request))
                AddCandidate(raw, request, new SaveCandidate(Path.Combine(documents, "Ubisoft", game), SaveKind.Ubisoft, 65,
                    "Ubisoft titles commonly save under Documents\\Ubisoft\\" + game + "."), allowed, false);
    }

    private static void AddGenericEngine(List<SaveCandidate> raw, SaveLocationRequest request, IKnownFolderProbe knownFolders, IReadOnlyList<string> allowed)
    {
        string? localApp = knownFolders.Resolve(KnownFolder.LocalAppData);
        string? roaming = knownFolders.Resolve(KnownFolder.RoamingAppData);
        string? savedGames = knownFolders.Resolve(KnownFolder.SavedGames);
        string? programData = knownFolders.Resolve(KnownFolder.ProgramData);
        string? documents = knownFolders.Resolve(KnownFolder.Documents);
        var games = GameVariants(request).ToArray();
        var vendors = VendorVariants(request).ToArray();

        foreach (string game in games)
        {
            if (!string.IsNullOrWhiteSpace(localApp))
            {
                AddCandidate(raw, request, new SaveCandidate(Path.Combine(localApp, game, "Saved"), SaveKind.LocalAppData, 70,
                    "Generic engine save under LocalAppData\\" + game + "\\Saved."), allowed, false);
                AddCandidate(raw, request, new SaveCandidate(Path.Combine(localApp, game), SaveKind.LocalAppData, 64,
                    "Generic per-game LocalAppData folder for '" + game + "'."), allowed, false);
            }
            if (!string.IsNullOrWhiteSpace(roaming))
            {
                AddCandidate(raw, request, new SaveCandidate(Path.Combine(roaming, game), SaveKind.AppData, 62,
                    "Generic per-game Roaming AppData folder for '" + game + "'."), allowed, false);
            }
            if (!string.IsNullOrWhiteSpace(savedGames))
            {
                AddCandidate(raw, request, new SaveCandidate(Path.Combine(savedGames, game), SaveKind.Document, 68,
                    "Windows Saved Games folder entry for '" + game + "'."), allowed, false);
            }
            if (!string.IsNullOrWhiteSpace(programData))
            {
                AddCandidate(raw, request, new SaveCandidate(Path.Combine(programData, game), SaveKind.ProgramData, 60,
                    "Generic per-game ProgramData folder for '" + game + "'."), allowed, false);
            }
            if (!string.IsNullOrWhiteSpace(documents))
            {
                AddCandidate(raw, request, new SaveCandidate(Path.Combine(documents, game), SaveKind.Document, 62,
                    "Generic per-game Documents folder for '" + game + "'."), allowed, false);
            }
        }

        foreach (string vendor in vendors)
        {
            foreach (string game in games)
            {
                if (!string.IsNullOrWhiteSpace(localApp))
                {
                    AddCandidate(raw, request, new SaveCandidate(Path.Combine(localApp, vendor, game, "Saved"), SaveKind.LocalAppData, 72,
                        "Vendor\\game save under LocalAppData\\" + vendor + "\\" + game + "\\Saved."), allowed, false);
                    AddCandidate(raw, request, new SaveCandidate(Path.Combine(localApp, vendor, game), SaveKind.LocalAppData, 66,
                        "Vendor\\game folder under LocalAppData."), allowed, false);
                }
                if (!string.IsNullOrWhiteSpace(roaming))
                {
                    AddCandidate(raw, request, new SaveCandidate(Path.Combine(roaming, vendor, game), SaveKind.AppData, 70,
                        "Vendor\\game folder under Roaming AppData."), allowed, false);
                }
                if (!string.IsNullOrWhiteSpace(programData))
                {
                    AddCandidate(raw, request, new SaveCandidate(Path.Combine(programData, vendor, game), SaveKind.ProgramData, 58,
                        "Vendor\\game folder under ProgramData."), allowed, false);
                }
            }
        }
    }

    private static void AddKnownFolderRoots(List<SaveCandidate> raw, SaveLocationRequest request, IKnownFolderProbe knownFolders, IReadOnlyList<string> allowed)
    {
        string? documents = knownFolders.Resolve(KnownFolder.Documents);
        string? savedGames = knownFolders.Resolve(KnownFolder.SavedGames);
        string? localLow = knownFolders.Resolve(KnownFolder.LocalAppDataLow);

        foreach (string game in GameVariants(request))
        {
            if (!string.IsNullOrWhiteSpace(documents))
            {
                AddCandidate(raw, request, new SaveCandidate(Path.Combine(documents, "My Games", game), SaveKind.Document, 75,
                    "Documents\\My Games\\" + game + " (Unreal/UE3/legacy convention)."), allowed, false);
                AddCandidate(raw, request, new SaveCandidate(Path.Combine(documents, game), SaveKind.Document, 62,
                    "Documents\\" + game + " convention."), allowed, false);
            }
            if (!string.IsNullOrWhiteSpace(savedGames))
            {
                AddCandidate(raw, request, new SaveCandidate(Path.Combine(savedGames, game), SaveKind.Document, 68,
                    "Saved Games\\" + game + " convention."), allowed, false);
            }
            if (!string.IsNullOrWhiteSpace(localLow))
            {
                AddCandidate(raw, request, new SaveCandidate(Path.Combine(localLow, game), SaveKind.LocalAppData, 64,
                    "LocalLow\\" + game + " (Unity persistentDataPath convention)."), allowed, false);
            }
        }

        if (!string.IsNullOrWhiteSpace(localLow))
        {
            foreach (string vendor in VendorVariants(request))
                foreach (string game in GameVariants(request))
                    AddCandidate(raw, request, new SaveCandidate(Path.Combine(localLow, vendor, game), SaveKind.LocalAppData, 70,
                        "Unity LocalLow\\" + vendor + "\\" + game + " persistentDataPath convention."), allowed, false);
        }
    }

    private static void AddConfigSniffing(List<SaveCandidate> raw, SaveLocationRequest request, IKnownFolderProbe knownFolders, ISaveEvidenceProbe evidence, IReadOnlyList<string> allowed)
    {
        string install = request.InstallFolder ?? "";
        if (!string.IsNullOrWhiteSpace(install))
        {
            SaveEvidence inspected = SafeInspect(evidence, install);
            if (inspected.Error != null)
            {
                AddCandidate(raw, request, new SaveCandidate(install, SaveKind.Generic, 20,
                    "Could not read the install folder (" + inspected.Error + "); recorded as low-confidence."), allowed, false);
            }
            else if (inspected.DirectoryExists)
            {
                if (inspected.HasSaveFile)
                    AddCandidate(raw, request, new SaveCandidate(install, SaveKind.Generic, 78,
                        "Save blob found directly inside the install folder: " + inspected.Detail + "."), allowed, false);
                else if (inspected.HasConfigHint)
                    AddCandidate(raw, request, new SaveCandidate(install, SaveKind.Generic, 60,
                        "Engine config hint file found in the install folder: " + inspected.Detail + "."), allowed, false);
            }
            foreach (string subfolder in SaveSubfolders)
            {
                string path = Path.Combine(install, subfolder);
                if (Directory.Exists(path))
                    AddCandidate(raw, request, new SaveCandidate(path, SaveKind.Generic, 72,
                        "Known save subfolder '" + subfolder + "' exists inside the install folder."), allowed, false);
            }
        }

        string? localApp = knownFolders.Resolve(KnownFolder.LocalAppData);
        string? roaming = knownFolders.Resolve(KnownFolder.RoamingAppData);
        foreach (string game in GameVariants(request))
        {
            foreach (string electron in ElectronSubfolders)
            {
                if (!string.IsNullOrWhiteSpace(localApp))
                    AddCandidate(raw, request, new SaveCandidate(Path.Combine(localApp, game, electron), SaveKind.LocalAppData, 66,
                        "Electron/Chromium '" + electron + "' store under LocalAppData\\" + game + "."), allowed, false);
                if (!string.IsNullOrWhiteSpace(roaming))
                    AddCandidate(raw, request, new SaveCandidate(Path.Combine(roaming, game, electron), SaveKind.AppData, 64,
                        "Electron/Chromium '" + electron + "' store under Roaming AppData\\" + game + "."), allowed, false);
            }
        }
    }

    private static void AddManual(List<SaveCandidate> raw, SaveLocationRequest request, IReadOnlyList<string> allowed)
    {
        if (request.ManualHintPaths == null) return;
        foreach (string hint in request.ManualHintPaths)
        {
            if (string.IsNullOrWhiteSpace(hint)) continue;
            AddCandidate(raw, request, new SaveCandidate(hint, SaveKind.Manual, 45,
                "Manual hint provided for this game."), allowed, registryOrManual: true);
        }
    }

    // ---- Candidate plumbing --------------------------------------------------

    private static void AddCandidate(List<SaveCandidate> raw, SaveLocationRequest request, SaveCandidate candidate,
        IReadOnlyList<string> allowedRoots, bool registryOrManual)
    {
        string expanded = Expand(candidate.Root);
        if (string.IsNullOrWhiteSpace(expanded)) return;
        string root;
        try { root = Path.GetFullPath(expanded); }
        catch { return; }

        bool allowed = allowedRoots.Any(allowedRoot => IsUnder(root, allowedRoot));
        int confidence = candidate.Confidence;
        if (!allowed)
        {
            // A path outside the user-writable root set is only ever surfaced
            // when the user (manual) or an explicit registry value named it, and
            // then it can never be auto-selected without the visible evidence.
            if (!registryOrManual) return;
            confidence = Math.Min(confidence, 40);
        }
        confidence = Math.Max(0, Math.Min(100, confidence));
        raw.Add(candidate with { Root = root, Confidence = confidence });
    }

    private static IReadOnlyList<string> BuildAllowedRoots(SaveLocationRequest request, IKnownFolderProbe knownFolders)
    {
        var roots = new List<string>();
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { roots.Add(Path.GetFullPath(path!)); } catch { }
        }
        Add(knownFolders.Resolve(KnownFolder.LocalAppData));
        Add(knownFolders.Resolve(KnownFolder.LocalAppDataLow));
        Add(knownFolders.Resolve(KnownFolder.RoamingAppData));
        Add(knownFolders.Resolve(KnownFolder.ProgramData));
        Add(knownFolders.Resolve(KnownFolder.SavedGames));
        Add(knownFolders.Resolve(KnownFolder.Documents));
        Add(request.InstallFolder);
        Add(request.MountPath);
        // A Steam library root is derived from the verified install path (the
        // game lives under steamapps\common), so its sibling userdata tree is a
        // legitimate engine-owned save root rather than an arbitrary guess.
        string? steamApps = FindSteamApps(request.InstallFolder, request.ExecutablePath);
        if (steamApps != null) Add(Directory.GetParent(steamApps)?.FullName);
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static List<SaveCandidate> Dedupe(IEnumerable<SaveCandidate> candidates)
    {
        var map = new Dictionary<string, SaveCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            string key;
            try { key = Path.GetFullPath(candidate.Root); }
            catch { key = candidate.Root; }
            if (!map.TryGetValue(key, out var existing) || candidate.Confidence > existing.Confidence)
                map[key] = candidate with { Root = key };
        }
        return map.Values.ToList();
    }

    private static IReadOnlyList<SaveCandidate> Order(IEnumerable<SaveCandidate> candidates) =>
        candidates
            .OrderByDescending(candidate => candidate.Confidence)
            .ThenBy(candidate => (int)candidate.Kind)
            .ThenBy(candidate => candidate.Root, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static int KindPriority(SaveKind kind) => kind switch
    {
        SaveKind.Registry => 0,
        SaveKind.SteamUserData => 1,
        SaveKind.GOG => 2,
        SaveKind.Epic => 3,
        SaveKind.Ubisoft => 4,
        SaveKind.LocalAppData => 5,
        SaveKind.AppData => 6,
        SaveKind.ProgramData => 7,
        SaveKind.Document => 8,
        SaveKind.Cloud => 9,
        SaveKind.Generic => 10,
        SaveKind.Manual => 11,
        _ => 12
    };

    // ---- Helpers -------------------------------------------------------------

    private static IEnumerable<string> VendorVariants(SaveLocationRequest request)
    {
        var variants = new List<string>();
        string first = FirstWord(request.DisplayName);
        if (!string.IsNullOrWhiteSpace(first)) variants.Add(first);
        if (!string.IsNullOrWhiteSpace(request.ProcessName))
        {
            string process = request.ProcessName.Trim();
            variants.Add(process);
            variants.Add(Path.GetFileNameWithoutExtension(process));
        }
        string slug = Slug(request.DisplayName);
        if (!string.IsNullOrWhiteSpace(slug)) variants.Add(slug);
        return variants.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> GameVariants(SaveLocationRequest request)
    {
        foreach (string variant in NameVariants(request.DisplayName)) yield return variant;
        foreach (string variant in NameVariants(request.ProcessName)) yield return variant;
        string? installName = string.IsNullOrWhiteSpace(request.InstallFolder)
            ? null
            : Path.GetFileName(Path.TrimEndingDirectorySeparator(request.InstallFolder));
        if (!string.IsNullOrWhiteSpace(installName))
            foreach (string variant in NameVariants(installName!)) yield return variant;
    }

    private static IEnumerable<string> NameVariants(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) yield break;
        string trimmed = name.Trim();
        yield return trimmed;
        string slug = Slug(trimmed);
        if (slug.Length > 0) yield return slug;
        string alphanumeric = new string(trimmed.Where(char.IsLetterOrDigit).ToArray());
        if (alphanumeric.Length > 0) yield return alphanumeric;
        string noSpaces = trimmed.Replace(" ", "").Replace("\t", "");
        if (noSpaces.Length > 0 && !noSpaces.Equals(trimmed, StringComparison.Ordinal)) yield return noSpaces;
        string first = FirstWord(trimmed);
        if (first.Length > 0) yield return first;
    }

    private static string FirstWord(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        return name.Trim().Split(new[] { ' ', '\t', '-', '_', ':' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
    }

    private static string? FindSteamApps(string installFolder, string executable)
    {
        foreach (string start in new[] { installFolder, executable })
        {
            if (string.IsNullOrWhiteSpace(start)) continue;
            DirectoryInfo? current;
            try
            {
                string path = Directory.Exists(start) ? start : Path.GetDirectoryName(start) ?? "";
                if (string.IsNullOrWhiteSpace(path)) continue;
                current = new DirectoryInfo(Path.GetFullPath(path));
            }
            catch { continue; }
            while (current != null)
            {
                if (current.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase)) return current.FullName;
                string candidate = Path.Combine(current.FullName, "steamapps");
                if (Directory.Exists(candidate)) return candidate;
                current = current.Parent;
            }
        }
        return null;
    }

    private static List<string> SteamAppIds(string steamApps, SaveLocationRequest request)
    {
        var ids = new List<string>();
        try
        {
            string installName = string.IsNullOrWhiteSpace(request.InstallFolder)
                ? "" : Path.GetFileName(Path.TrimEndingDirectorySeparator(request.InstallFolder));
            foreach (string acf in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"))
            {
                string text;
                try { text = File.ReadAllText(acf); } catch { continue; }
                string id = ExtractAcf(text, "appid");
                string name = ExtractAcf(text, "name");
                string directory = ExtractAcf(text, "installdir");
                bool matches = (!string.IsNullOrWhiteSpace(directory)
                        && (directory.Equals(installName, StringComparison.OrdinalIgnoreCase)
                            || (!string.IsNullOrWhiteSpace(request.InstallFolder) && request.InstallFolder.Contains(directory, StringComparison.OrdinalIgnoreCase))))
                    || (!string.IsNullOrWhiteSpace(name) && name.Equals(request.DisplayName, StringComparison.OrdinalIgnoreCase));
                if (matches && !string.IsNullOrWhiteSpace(id)) ids.Add(id);
            }
        }
        catch { }
        return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string ExtractAcf(string text, string key)
    {
        var match = Regex.Match(text, "\"" + key + "\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : "";
    }

    private static IReadOnlyList<string> SafeSubKeys(IRegistryProbe registry, string vendor, string game)
    {
        try { return registry.ReadSubKeyNames(vendor, game) ?? Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try { return Directory.EnumerateDirectories(path).ToArray(); }
        catch { return Array.Empty<string>(); }
    }

    private static SaveEvidence SafeInspect(ISaveEvidenceProbe probe, string root)
    {
        if (probe == null || string.IsNullOrWhiteSpace(root)) return SaveEvidence.Missing;
        try { return probe.Inspect(root) ?? SaveEvidence.Missing; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return new SaveEvidence(false, false, false, false, ex.Message, "permission denied: " + ex.Message);
        }
        catch { return SaveEvidence.Missing; }
    }

    private static bool Contains(string haystack, string needle) =>
        !string.IsNullOrWhiteSpace(haystack) && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.Equals(normalized, StringComparison.OrdinalIgnoreCase)) return true;
            return full.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string Expand(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')).Trim(); }
        catch { return path.Trim(); }
    }

    private static void TrimCache()
    {
        if (Cache.Count < MaxCacheEntries) return;
        int remove = Cache.Count - MaxCacheEntries + 1;
        foreach (string key in Cache.Keys.Take(remove).ToArray()) Cache.TryRemove(key, out _);
    }

    // ---- Real probes ---------------------------------------------------------

    private sealed class EmptyRegistryProbe : IRegistryProbe
    {
        internal static readonly EmptyRegistryProbe Instance = new();
        public IReadOnlyDictionary<string, string> ReadValues(string vendor, string game) => Empty;
        public IReadOnlyList<string> ReadSubKeyNames(string vendor, string game) => Array.Empty<string>();
        public IReadOnlyDictionary<string, string> ReadValues(string vendor, string game, string subKey) => Empty;
        private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();
    }

    private sealed class EmptyKnownFolderProbe : IKnownFolderProbe
    {
        internal static readonly EmptyKnownFolderProbe Instance = new();
        public string? Resolve(KnownFolder folder) => null;
    }
}

internal sealed class Win32RegistryProbe : IRegistryProbe
{
    internal static readonly Win32RegistryProbe Instance = new();
    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> ReadValues(string vendor, string game) => ReadKey(@"Software\" + vendor + @"\" + game);

    public IReadOnlyList<string> ReadSubKeyNames(string vendor, string game)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\" + vendor + @"\" + game);
            return key?.GetSubKeyNames() ?? Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }
    }

    public IReadOnlyDictionary<string, string> ReadValues(string vendor, string game, string subKey) =>
        ReadKey(@"Software\" + vendor + @"\" + game + @"\" + subKey);

    private static IReadOnlyDictionary<string, string> ReadKey(string path)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            if (key == null) return Empty;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in key.GetValueNames())
                map[name] = key.GetValue(name)?.ToString() ?? "";
            return map;
        }
        catch { return Empty; }
    }
}

internal sealed class Win32KnownFolderProbe : IKnownFolderProbe
{
    internal static readonly Win32KnownFolderProbe Instance = new();

    public string? Resolve(KnownFolder folder)
    {
        Guid id = folder switch
        {
            KnownFolder.SavedGames => new Guid("4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4"),
            KnownFolder.LocalAppData => new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091"),
            KnownFolder.LocalAppDataLow => new Guid("A520A1A4-1780-4FF6-BD18-167343C5AF16"),
            KnownFolder.RoamingAppData => new Guid("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D"),
            KnownFolder.ProgramData => new Guid("62AB5D82-FDC1-4DC3-A9DD-070D1D495D97"),
            KnownFolder.Documents => new Guid("FDD39AD0-238F-46AF-ADB4-6C85480369C7"),
            _ => Guid.Empty
        };
        if (id == Guid.Empty) return null;
        try
        {
            int result = SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out IntPtr pointer);
            if (result != 0 || pointer == IntPtr.Zero) return null;
            try
            {
                string? path = Marshal.PtrToStringUni(pointer);
                return string.IsNullOrWhiteSpace(path) ? null : path;
            }
            finally { Marshal.FreeCoTaskMem(pointer); }
        }
        catch { return null; }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
}

internal sealed class FileSystemSaveEvidenceProbe : ISaveEvidenceProbe
{
    internal static readonly FileSystemSaveEvidenceProbe Instance = new();

    private static readonly HashSet<string> SaveExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".sav", ".save", ".dat", ".es3", ".ck", ".rvdata2", ".json", ".slot", ".profile", ".bin" };

    private static readonly HashSet<string> SaveDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
        { "save", "saves", "saved", "savegames", "remote", "local storage", "leveldb", "persistentdata", "userdata" };

    private static readonly HashSet<string> ConfigHintNames = new(StringComparer.OrdinalIgnoreCase)
        { "engine.ini", "gameusersettings.ini", "input.ini", "scalability.ini", "savegame" };

    public SaveEvidence Inspect(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return SaveEvidence.Missing;
        try
        {
            if (!Directory.Exists(root)) return SaveEvidence.Missing;
            bool hasSave = false, nonEmpty = false, hint = false;
            var notes = new List<string>();
            foreach (string entry in Directory.EnumerateFileSystemEntries(root))
            {
                if (Directory.Exists(entry))
                {
                    string directoryName = Path.GetFileName(entry);
                    if (SaveDirectoryNames.Contains(directoryName))
                    {
                        SaveEvidence inner = ScanDirectory(entry, 2);
                        hasSave |= inner.HasSaveFile;
                        nonEmpty |= inner.HasNonEmptySaveFile;
                        hint |= inner.HasConfigHint;
                        if (inner.HasSaveFile) notes.Add("save directory '" + directoryName + "'");
                    }
                    continue;
                }
                string fileName = Path.GetFileName(entry);
                string extension = Path.GetExtension(fileName);
                if (SaveExtensions.Contains(extension))
                {
                    hasSave = true;
                    try { if (new FileInfo(entry).Length > 0) nonEmpty = true; } catch { }
                    notes.Add("save file '" + fileName + "'");
                }
                if (ConfigHintNames.Contains(fileName) || fileName.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
                {
                    hint = true;
                    notes.Add("config '" + fileName + "'");
                }
            }
            string detail = notes.Count == 0
                ? "directory exists but holds no recognizable save data"
                : "found " + string.Join(", ", notes.Distinct().Take(4));
            return new SaveEvidence(true, hasSave, nonEmpty, hint, null, detail);
        }
        catch (UnauthorizedAccessException ex) { return new SaveEvidence(false, false, false, false, ex.Message, "permission denied: " + ex.Message); }
        catch (System.Security.SecurityException ex) { return new SaveEvidence(false, false, false, false, ex.Message, "permission denied: " + ex.Message); }
        catch (IOException ex) { return new SaveEvidence(false, false, false, false, ex.Message, "io error: " + ex.Message); }
    }

    private static SaveEvidence ScanDirectory(string directory, int depth)
    {
        bool hasSave = false, nonEmpty = false, hint = false;
        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (Directory.Exists(entry))
                {
                    if (depth > 1)
                    {
                        SaveEvidence inner = ScanDirectory(entry, depth - 1);
                        hasSave |= inner.HasSaveFile;
                        nonEmpty |= inner.HasNonEmptySaveFile;
                        hint |= inner.HasConfigHint;
                    }
                    continue;
                }
                string fileName = Path.GetFileName(entry);
                string extension = Path.GetExtension(fileName);
                if (SaveExtensions.Contains(extension))
                {
                    hasSave = true;
                    try { if (new FileInfo(entry).Length > 0) nonEmpty = true; } catch { }
                }
                if (ConfigHintNames.Contains(fileName) || fileName.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) hint = true;
            }
        }
        catch { }
        return new SaveEvidence(true, hasSave, nonEmpty, hint, null, "");
    }
}
