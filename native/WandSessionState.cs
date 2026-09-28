using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace GameLibrary.Native;

internal enum WandSessionStatus
{
    Resolving,
    StartingWand,
    StartingGame,
    WaitingForGame,
    Attaching,
    Connected,
    UnsupportedBuild,
    RegistrationInvalid,
    GameFailedToStart,
    GameRunningConnectionUnconfirmed,
    ConnectionFailed,
    Disconnected,
    Cancelled
}

internal sealed record WandSessionSnapshot(
    string OperationId,
    string CanonicalGameId,
    string InstallationPath,
    string TitleId,
    string GameId,
    string WandVersion,
    WandSessionStatus Status,
    DateTime StartedUtc,
    DateTime UpdatedUtc,
    int? ProcessId,
    string? ProcessCreationFileTime,
    long? WindowHandle,
    string EvidenceSource,
    DateTime? EvidenceUtc,
    string Detail);

/// <summary>
/// Holds one exact game-installation launch session. A title alias or a different
/// installation gets its own state so an unrelated launch cannot reuse its result.
/// </summary>
internal sealed class WandSessionState
{
    private static readonly ConcurrentDictionary<string, WandSessionState> Sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();
    private WandSessionSnapshot snapshot;

    private WandSessionState(string canonicalGameId, string installationPath)
    {
        string fullPath = Path.GetFullPath(installationPath);
        var now = DateTime.UtcNow;
        snapshot = new WandSessionSnapshot(
            Guid.NewGuid().ToString("N"), canonicalGameId, fullPath, "", "", "",
            WandSessionStatus.Resolving, now, now, null, null, null, "", null,
            "Resolving the exact Wand registration and installation.");
    }

    internal static WandSessionState Begin(string canonicalGameId, string installationPath)
    {
        if (string.IsNullOrWhiteSpace(canonicalGameId)) throw new ArgumentException("A canonical game identity is required.", nameof(canonicalGameId));
        if (string.IsNullOrWhiteSpace(installationPath)) throw new ArgumentException("An exact installation path is required.", nameof(installationPath));
        var state = new WandSessionState(canonicalGameId.Trim(), installationPath);
        Sessions[Key(state.snapshot.CanonicalGameId, state.snapshot.InstallationPath)] = state;
        return state;
    }

    internal static bool TryGet(string canonicalGameId, string installationPath, out WandSessionSnapshot session)
    {
        session = null!;
        if (string.IsNullOrWhiteSpace(canonicalGameId) || string.IsNullOrWhiteSpace(installationPath)) return false;
        string fullPath;
        try { fullPath = Path.GetFullPath(installationPath); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return false; }
        if (!Sessions.TryGetValue(Key(canonicalGameId.Trim(), fullPath), out var state)) return false;
        session = state.Read();
        return true;
    }

    internal static WandSessionState? Find(string canonicalGameId, string installationPath)
    {
        if (string.IsNullOrWhiteSpace(canonicalGameId) || string.IsNullOrWhiteSpace(installationPath)) return null;
        string fullPath;
        try { fullPath = Path.GetFullPath(installationPath); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return null; }
        return Sessions.TryGetValue(Key(canonicalGameId.Trim(), fullPath), out var state) ? state : null;
    }

    internal WandSessionSnapshot Read()
    {
        lock (gate) return snapshot;
    }

    internal WandSessionSnapshot Resolve(string titleId, string gameId, string wandVersion)
    {
        lock (gate)
        {
            snapshot = snapshot with
            {
                TitleId = titleId ?? "",
                GameId = gameId ?? "",
                WandVersion = wandVersion ?? "",
                UpdatedUtc = DateTime.UtcNow,
                Detail = "The exact Wand registration and game build were resolved."
            };
            return snapshot;
        }
    }

    internal WandSessionSnapshot Transition(WandSessionStatus status, string detail, string evidenceSource = "", DateTime? evidenceUtc = null)
    {
        lock (gate)
        {
            if (!CanTransition(snapshot.Status, status))
                throw new InvalidOperationException("Invalid Wand session transition " + snapshot.Status + " -> " + status + ".");
            snapshot = snapshot with
            {
                Status = status,
                UpdatedUtc = DateTime.UtcNow,
                EvidenceSource = evidenceSource,
                EvidenceUtc = evidenceUtc,
                Detail = detail ?? ""
            };
            return snapshot;
        }
    }

    internal WandSessionSnapshot TrackProcess(Process process, string detail)
    {
        process.Refresh();
        if (process.HasExited) throw new InvalidOperationException("The exact game process exited before it could be tracked.");
        string creation = process.StartTime.ToUniversalTime().ToFileTimeUtc().ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
        long? hwnd = process.MainWindowHandle == IntPtr.Zero ? null : process.MainWindowHandle.ToInt64();
        lock (gate)
        {
            snapshot = snapshot with
            {
                ProcessId = process.Id,
                ProcessCreationFileTime = creation,
                WindowHandle = hwnd,
                UpdatedUtc = DateTime.UtcNow,
                Detail = detail
            };
            return snapshot;
        }
    }

    private static bool CanTransition(WandSessionStatus from, WandSessionStatus to)
    {
        if (from == to) return true;
        if (to is WandSessionStatus.Cancelled or WandSessionStatus.ConnectionFailed or WandSessionStatus.UnsupportedBuild
            or WandSessionStatus.RegistrationInvalid or WandSessionStatus.GameFailedToStart or WandSessionStatus.Disconnected)
            return from is not (WandSessionStatus.Cancelled or WandSessionStatus.RegistrationInvalid or WandSessionStatus.UnsupportedBuild);
        return from switch
        {
            WandSessionStatus.Resolving => to is WandSessionStatus.StartingWand or WandSessionStatus.RegistrationInvalid or WandSessionStatus.UnsupportedBuild,
            WandSessionStatus.StartingWand => to is WandSessionStatus.StartingGame or WandSessionStatus.WaitingForGame or WandSessionStatus.Attaching or WandSessionStatus.GameRunningConnectionUnconfirmed or WandSessionStatus.GameFailedToStart,
            WandSessionStatus.StartingGame => to is WandSessionStatus.WaitingForGame or WandSessionStatus.GameRunningConnectionUnconfirmed or WandSessionStatus.GameFailedToStart,
            WandSessionStatus.WaitingForGame => to is WandSessionStatus.StartingWand or WandSessionStatus.Attaching or WandSessionStatus.GameRunningConnectionUnconfirmed or WandSessionStatus.GameFailedToStart,
            WandSessionStatus.Attaching => to is WandSessionStatus.Connected or WandSessionStatus.GameRunningConnectionUnconfirmed,
            WandSessionStatus.GameRunningConnectionUnconfirmed => to is WandSessionStatus.Attaching or WandSessionStatus.Connected or WandSessionStatus.Disconnected,
            WandSessionStatus.ConnectionFailed => to is WandSessionStatus.Attaching or WandSessionStatus.Connected or WandSessionStatus.Disconnected,
            WandSessionStatus.Connected => to is WandSessionStatus.Disconnected or WandSessionStatus.Attaching
                or WandSessionStatus.GameRunningConnectionUnconfirmed,
            WandSessionStatus.Disconnected => to is WandSessionStatus.Attaching or WandSessionStatus.Connected,
            _ => false
        };
    }

    private static string Key(string canonicalGameId, string installationPath) =>
        canonicalGameId + "\n" + Path.GetFullPath(installationPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

internal sealed record WandTrainerEvidence(bool Confirmed, string Source, DateTime? ObservedUtc, string Detail);

/// <summary>
/// Fails closed unless the installed Wand build exposes trainer-session evidence
/// in a recognized, game-specific source. Overlay IPC and graphics-hook logs are
/// deliberately excluded because they prove only that an overlay was injected.
/// </summary>
internal static class WandTrainerEvidenceAdapter
{
    // This contract was inspected in the installed 12.58.0 app.asar. A Wand
    // update must be inspected before its in-memory trainer state is trusted.
    private const string SupportedAsarSha256 = "683BB31A8C1A109AE71D58C0E043D6BF415FFC9118D389AF3A5DBCF1332052F8";
    private static readonly object AsarGate = new();
    private static long verifiedAsarLength = -1;
    private static DateTime verifiedAsarWriteUtc;
    private static bool verifiedAsar;

    internal static WandTrainerEvidence Inspect(string wandExecutable, string gameId, int processId,
        string processCreationFileTime, DateTime attemptStartedUtc, string gameExecutablePath)
    {
        string version = "unknown";
        try
        {
            if (File.Exists(wandExecutable))
                version = FileVersionInfo.GetVersionInfo(wandExecutable).ProductVersion
                    ?? FileVersionInfo.GetVersionInfo(wandExecutable).FileVersion
                    ?? "unknown";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }

        string asar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Wand", "app-12.58.0", "resources", "app.asar");
        string tools = Path.Combine(AppContext.BaseDirectory, "tools");
        string node = Path.Combine(tools, "node", "node.exe");
        string helper = Path.Combine(tools, "wand_trainer_status.js");
        string traceHelper = Path.Combine(tools, "wand_tophat_evidence.js");
        if (!VerifiedInstalledAsar(asar) || !File.Exists(node) || !File.Exists(helper) || !File.Exists(traceHelper))
            return new WandTrainerEvidence(false, "unsupported-wand-session-schema", null,
                "Installed Wand build " + version + " has no verified 12.58.0 trainer-state adapter. "
                + "Overlay IPC and hook markers cannot establish trainer attachment.");
        if (processId <= 0 || !long.TryParse(gameId, out _)
            || !System.Text.RegularExpressions.Regex.IsMatch(processCreationFileTime, "^[0-9A-Fa-f]{16}$")
            || attemptStartedUtc == default
            || string.IsNullOrWhiteSpace(gameExecutablePath))
            return new WandTrainerEvidence(false, "trainer-identity-invalid", null, "The exact trainer/game process identity is invalid.");
        string expectedProcessName;
        try { expectedProcessName = Path.GetFileNameWithoutExtension(Path.GetFullPath(gameExecutablePath)); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        { return new WandTrainerEvidence(false, "trainer-identity-invalid", null, "The exact trainer/game executable path is invalid."); }
        if (string.IsNullOrWhiteSpace(expectedProcessName) || expectedProcessName.Length > 128
            || expectedProcessName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return new WandTrainerEvidence(false, "trainer-identity-invalid", null, "The exact trainer/game process name is invalid.");

        try
        {
            bool exactProcessAtStart = ExactProcessStillMatches(processId, processCreationFileTime);
            bool supportedWandRunning = SupportedWandProcessIsRunning(asar);
            var start = new ProcessStartInfo(node)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(helper);
            start.ArgumentList.Add(gameId);
            start.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add(expectedProcessName);
            start.ArgumentList.Add(processCreationFileTime);
            start.ArgumentList.Add(attemptStartedUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            using var probe = Process.Start(start) ?? throw new IOException("The local Wand trainer probe could not start.");
            var outputTask = probe.StandardOutput.ReadToEndAsync();
            var errorTask = probe.StandardError.ReadToEndAsync();
            if (!probe.WaitForExit(3500))
            {
                probe.Kill(true);
                return new WandTrainerEvidence(false, "trainer-session-probe-unavailable", DateTime.UtcNow,
                    "The read-only Wand trainer probe timed out.");
            }
            string output = outputTask.GetAwaiter().GetResult();
            string error = errorTask.GetAwaiter().GetResult();
            bool exactProcessAtEnd = ExactProcessStillMatches(processId, processCreationFileTime);
            if (output.Length == 0 || output.Length > 16384)
                return new WandTrainerEvidence(false, "trainer-session-probe-unavailable", DateTime.UtcNow,
                    "Wand's local trainer state is unavailable: " + (error.Length > 200 ? error[..200] : error));
            return InterpretProbeOutput(output, gameId, processId, processCreationFileTime, attemptStartedUtc,
                expectedProcessName, exactProcessAtStart, exactProcessAtEnd, probe.ExitCode, error, supportedWandRunning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception or JsonException or KeyNotFoundException or FormatException)
        {
            return new WandTrainerEvidence(false, "trainer-session-probe-unavailable", DateTime.UtcNow,
                "Wand's read-only trainer state could not be inspected: " + ex.Message);
        }
    }

    internal static WandTrainerEvidence InterpretProbeOutput(string output, string gameId, int processId,
        string processCreationFileTime, DateTime attemptStartedUtc, string expectedProcessName,
        bool exactProcessAtStart, bool exactProcessAtEnd, int helperExitCode = 0, string helperError = "",
        bool supportedWandBuildRunning = true)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var result = document.RootElement;
            string reportedGameId = result.TryGetProperty("gameId", out var gameValue) ? gameValue.GetString() ?? "" : "";
            int reportedPid = result.TryGetProperty("processId", out var pidValue) && pidValue.TryGetInt32(out var parsedPid) ? parsedPid : 0;
            string reason = result.TryGetProperty("reason", out var reasonValue) ? reasonValue.GetString() ?? "" : "";
            string observedText = result.TryGetProperty("observedUtc", out var observedValue) ? observedValue.GetString() ?? "" : "";
            bool reportedIdentityMatches = string.Equals(reportedGameId, gameId, StringComparison.Ordinal)
                && reportedPid == processId;
            bool observedValid = DateTime.TryParse(observedText, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var observed)
                && observed.ToUniversalTime() >= attemptStartedUtc.AddSeconds(-1)
                && observed.ToUniversalTime() <= DateTime.UtcNow.AddSeconds(1);
            bool connected = result.TryGetProperty("connected", out var connectedValue)
                && connectedValue.ValueKind == JsonValueKind.True;
            if (connected && reportedIdentityMatches && observedValid && exactProcessAtStart && exactProcessAtEnd
                && supportedWandBuildRunning)
                return new WandTrainerEvidence(true, "wand-12.58.0-trainer-vm", observed.ToUniversalTime(),
                    "Wand's active trainer state matches the exact game ID and process PID.");

            if (TryReadTophatTrainerEvidence(result, processId, processCreationFileTime, attemptStartedUtc,
                expectedProcessName, out var traceObserved, out var traceDetail))
            {
                return exactProcessAtEnd
                    ? new WandTrainerEvidence(false, "trainer-command-observed", traceObserved,
                        traceDetail + " The exact process is still running, but Wand's current trainer state is not active.")
                    : new WandTrainerEvidence(false, "trainer-connected-before-exit", traceObserved,
                        traceDetail + " The exact process is no longer running; this is historical evidence, not a current connection.");
            }

            if (!exactProcessAtEnd)
                return new WandTrainerEvidence(false, "game-process-changed", DateTime.UtcNow,
                    "The exact game process exited or its PID now has a different creation identity before trainer attachment was confirmed.");
            if (!reportedIdentityMatches || !observedValid)
                return new WandTrainerEvidence(false, "trainer-session-identity-mismatch", DateTime.UtcNow,
                    "Wand's reported trainer state did not match this exact game process and launch attempt.");
            return new WandTrainerEvidence(false, helperExitCode == 0 ? "trainer-session-waiting" : "trainer-session-probe-unavailable",
                observed.ToUniversalTime(), "Wand has not reported an active trainer for this exact game process (" + reason + "). "
                + (helperError.Length > 200 ? helperError[..200] : helperError));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            return new WandTrainerEvidence(false, "trainer-session-probe-unavailable", DateTime.UtcNow,
                "Wand's read-only trainer state could not be inspected: " + ex.Message);
        }
    }

    private static bool TryReadTophatTrainerEvidence(JsonElement result, int processId, string processCreationFileTime,
        DateTime attemptStartedUtc, string expectedProcessName, out DateTime observedUtc, out string detail)
    {
        observedUtc = default;
        detail = "";
        if (!result.TryGetProperty("trainerTrace", out var trace) || trace.ValueKind != JsonValueKind.Object
            || !trace.TryGetProperty("trainerObserved", out var present) || present.ValueKind != JsonValueKind.True
            || !trace.TryGetProperty("processId", out var pidValue) || !pidValue.TryGetInt32(out var reportedPid)
            || reportedPid != processId
            || !trace.TryGetProperty("processName", out var nameValue)
            || !string.Equals(nameValue.GetString(), expectedProcessName, StringComparison.OrdinalIgnoreCase)
            || !TryReadUtc(trace, "sessionStartedUtc", out var sessionStarted)
            || !TryReadUtc(trace, "pluginLoadedUtc", out var pluginLoaded)
            || !TryReadUtc(trace, "observedUtc", out observedUtc)) return false;
        if (!long.TryParse(processCreationFileTime, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out long creationFileTime)) return false;
        DateTime processStarted;
        try { processStarted = DateTime.FromFileTimeUtc(creationFileTime); }
        catch (ArgumentOutOfRangeException) { return false; }
        DateTime now = DateTime.UtcNow;
        if (sessionStarted < processStarted.AddSeconds(-2) || sessionStarted > now.AddSeconds(2)
            || pluginLoaded < sessionStarted.AddSeconds(-1) || pluginLoaded > observedUtc
            || observedUtc < attemptStartedUtc.AddSeconds(-1) || observedUtc > now.AddSeconds(1)) return false;
        string fileName = trace.TryGetProperty("traceFileName", out var fileValue) ? fileValue.GetString() ?? "" : "";
        if (fileName.Length > 128 || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        detail = "Wand's Tophat trace contains a fresh successful trainerlib command for exact PID "
            + processId.ToString(System.Globalization.CultureInfo.InvariantCulture) + " (session "
            + sessionStarted.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture)
            + ", command " + observedUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture) + ").";
        return true;
    }

    private static bool TryReadUtc(JsonElement element, string name, out DateTime value)
    {
        value = default;
        return element.TryGetProperty(name, out var text)
            && text.ValueKind == JsonValueKind.String
            && DateTime.TryParse(text.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out value);
    }

    private static bool VerifiedInstalledAsar(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return false;
            lock (AsarGate)
            {
                if (file.Length == verifiedAsarLength && file.LastWriteTimeUtc == verifiedAsarWriteUtc)
                    return verifiedAsar;
                long beforeLength = file.Length;
                DateTime beforeWriteUtc = file.LastWriteTimeUtc;
                using var stream = file.OpenRead();
                verifiedAsar = string.Equals(Convert.ToHexString(SHA256.HashData(stream)), SupportedAsarSha256, StringComparison.Ordinal);
                file.Refresh();
                verifiedAsar = verifiedAsar && file.Length == beforeLength && file.LastWriteTimeUtc == beforeWriteUtc;
                verifiedAsarLength = file.Length;
                verifiedAsarWriteUtc = file.LastWriteTimeUtc;
                return verifiedAsar;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static bool SupportedWandProcessIsRunning(string asar)
    {
        string appRoot = Directory.GetParent(Path.GetDirectoryName(asar)!)?.FullName ?? "";
        string installRoot = Directory.GetParent(appRoot)?.FullName ?? "";
        bool found = false;
        bool conflictingBuild = false;
        foreach (var process in Process.GetProcessesByName("Wand").Concat(Process.GetProcessesByName("WeMod")))
        {
            using (process)
            {
                try
                {
                    if (process.HasExited) continue;
                    string executable = process.MainModule?.FileName ?? "";
                    if (string.Equals(executable, Path.Combine(appRoot, "Wand.exe"), StringComparison.OrdinalIgnoreCase)
                        || string.Equals(executable, Path.Combine(appRoot, "WeMod.exe"), StringComparison.OrdinalIgnoreCase)) found = true;
                    else if (executable.StartsWith(Path.Combine(installRoot, "app-"), StringComparison.OrdinalIgnoreCase)) conflictingBuild = true;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                    or UnauthorizedAccessException) { conflictingBuild = true; }
            }
        }
        return found && !conflictingBuild;
    }

    private static bool ExactProcessStillMatches(int processId, string creationStamp)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited && string.Equals(process.StartTime.ToUniversalTime().ToFileTimeUtc()
                .ToString("X16", System.Globalization.CultureInfo.InvariantCulture), creationStamp, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException or NotSupportedException) { return false; }
    }
}
