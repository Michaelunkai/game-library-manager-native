using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

internal enum GameSpeedArchitecture
{
    Unknown = 0,
    X86 = 32,
    X64 = 64
}

/// <summary>
/// A positively identified injection target. Nothing is injected into a process
/// that does not produce one of these, which is the enforced half of the safety
/// boundary in <see cref="GameSpeedNative"/>.
/// </summary>
internal sealed record GameSpeedTarget(
    int ProcessId,
    string ImagePath,
    string CreationFileTime,
    int SessionId,
    GameSpeedArchitecture Architecture,
    int IntegrityLevel,
    int OwnIntegrityLevel);

internal sealed record GameSpeedStatus(
    int ProcessId,
    bool Injected,
    bool HooksInstalled,
    bool Faulted,
    int HookCount,
    double AppliedFactor);

/// <summary>
/// The scaled clock, modelled in managed code so its arithmetic can be proven by
/// the test suite without a running game. This is a line-for-line mirror of the
/// integrator in gamespeed.c; the native accumulator is a 128-bit value while this
/// model uses a 64-bit one, which is identical for every magnitude the tests and
/// the UI exercise.
/// <para>
/// The single correctness rule: on a factor change the OLD factor is settled
/// exactly up to the instant of the change and the real reference is then re-based
/// to that same instant, so the clock advances by zero ticks at the change and
/// only intervals after it are charged at the new rate.
/// </para>
/// </summary>
internal sealed class ScaledClock
{
    internal const int FractionalBits = 16;
    internal const long FractionOne = 1L << FractionalBits;
    internal const long FactorMicroScale = 1_000_000L;
    internal const double MinimumFactor = 0.05;
    internal const double MaximumFactor = 20.0;

    private readonly long _anchor;
    private readonly long _maxStep;
    private long _factorFixed = FractionOne;
    private long _realLast;
    private long _virtualFixed;
    /// <summary>
    /// Highest RAW (anchor-relative) virtual value ever published. A game that
    /// computes frame deltas will produce a negative delta - and misbehave - if the
    /// clock ever rewinds, and slowing down necessarily pulls a fast virtual clock
    /// back toward real time. This is kept anchor-relative so it is directly
    /// comparable with the accumulator; mixing the two spaces would fold the anchor
    /// into elapsed time and inject a huge spurious jump.
    /// </summary>
    private long _lastVirtualRaw = long.MinValue;

    internal ScaledClock(long anchorReference, double referenceFrequencyPerSecond)
    {
        if (referenceFrequencyPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(referenceFrequencyPerSecond));
        _anchor = anchorReference;
        _realLast = anchorReference;
        // Matches GS_MAX_STEP_100NS: a gap longer than a minute is a suspend or a
        // resume, not elapsed simulation time, and must never be caught up.
        _maxStep = (long)(referenceFrequencyPerSecond * 60.0);
        if (_maxStep < 1) _maxStep = 1;
    }

    internal double Factor => (double)_factorFixed / FractionOne;
    internal bool IsTransparent => _factorFixed == FractionOne;
    internal long RealLast => _realLast;
    internal long VirtualFixedPoint => _virtualFixed;
    internal long Anchor => _anchor;

    internal static bool ValidateFactor(double factor) =>
        !double.IsNaN(factor) && !double.IsInfinity(factor) &&
        factor >= MinimumFactor && factor <= MaximumFactor;

    internal static long ToFixed(double factor) =>
        factor == 1.0 ? FractionOne : (long)(factor * FractionOne + (factor < 0 ? -0.5 : 0.5));

    internal static long ToMicro(double factor) => (long)(factor * FactorMicroScale + 0.5);

    internal static double FromMicro(long micro) => (double)micro / FactorMicroScale;

    /// <summary>Integrates up to <paramref name="realNow"/> and returns the absolute virtual value.</summary>
    internal long Read(long realNow)
    {
        Integrate(realNow);
        return _anchor + Publish(_virtualFixed >> FractionalBits);
    }

    /// <summary>Integrates up to <paramref name="realNow"/> and returns the absolute virtual value.</summary>
    internal long Elapsed100ns(long realNow)
    {
        Integrate(realNow);
        return Publish(_virtualFixed >> FractionalBits);
    }

    /// <summary>Clamps a raw virtual reading so the game never observes time running backwards.</summary>
    private long Publish(long raw)
    {
        if (raw > _lastVirtualRaw) _lastVirtualRaw = raw;
        return _lastVirtualRaw;
    }

    /// <summary>
    /// Adopts a factor with no time jump. A factor that is rejected by
    /// <see cref="ValidateFactor"/> throws and leaves the factor in force untouched.
    /// </summary>
    internal void SetFactor(double factor, long realNow)
    {
        if (!ValidateFactor(factor))
            throw new ArgumentOutOfRangeException(nameof(factor), factor,
                "A speed factor must be finite and between " +
                MinimumFactor.ToString(CultureInfo.InvariantCulture) + " and " +
                MaximumFactor.ToString(CultureInfo.InvariantCulture) + ".");

        long target = ToFixed(factor);
        if (target == _factorFixed) return;

    // Integrate to the change point, then adopt the new rate. This is the identity
    // case at exactly 1.0 - delta * FractionOne is delta - so there is no separate
    // "normal speed" shortcut: re-seeding the accumulator from the anchor would make
    // 0.5x -> 1.0x fast-forward the game by the whole accumulated deficit, and
    // 3.0x -> 1.0x rewind it. Integrating keeps the clock continuous in both
    // directions, which is what a frame-delta consumer requires.
    Integrate(realNow);
    _realLast = realNow;
    _factorFixed = target;
    // Slowing down necessarily pulls a fast virtual clock back toward real time. The
    // game must never see the clock rewind - it would compute a negative frame delta -
    // so lift the integer part to the last published value, carrying the sub-tick
    // fraction across. The clock then continues at the new rate immediately instead of
    // freezing while real time caught up, and no error accumulates.
    long projected = _virtualFixed >> FractionalBits;
    if (projected < _lastVirtualRaw)
        _virtualFixed = (_lastVirtualRaw << FractionalBits) | (_virtualFixed & (FractionOne - 1));
   }

    private void Integrate(long realNow)
    {
        long delta = realNow - _realLast;
        if (delta <= 0)
        {
            if (delta < 0) _realLast = realNow;   // never rewind
            return;
        }
        if (delta > _maxStep) delta = _maxStep;
        _virtualFixed += delta * _factorFixed;
        _realLast += delta;
    }
}

/// <summary>
/// Loads the in-process time-scale hook DLL into one of the user's own offline
/// single-player games and steers its speed factor.
/// <para>
/// SAFETY BOUNDARY: this engine is for the user's own offline, single-player games
/// launched from this library. It does not attempt to defeat, evade or hide from
/// anti-cheat or any online-service integrity system, and it refuses to target any
/// process it cannot positively identify, or one that has an online-service
/// integrity module loaded.
/// </para>
/// The remote thread is only ever LoadLibraryW on the hook DLL; the factor is then
/// steered through a named memory-mapped control block rather than a second remote
/// call. That was chosen over marshalling a double into the target because the
/// calling convention for a floating-point argument differs between x86 (stack)
/// and x64 (xmm0), which would mean shipping two machine-code stubs and taking
/// three extra remote operations for every F1/F2/F3 press, while the control block
/// is a single architecture-neutral store.
/// <para>
/// The target process is never suspended, and this loader never unloads the DLL:
/// restoring patched bytes while other threads are mid-call is a crash risk. The
/// detach path returns the clock to 1.0, where every hook is a transparent
/// pass-through, so the game keeps running correctly at real speed.
/// </para>
/// </summary>
internal static class GameSpeedNative
{
/// <summary>
    /// INTEGRATION NOTE: <see cref="ISpeedApplier"/> is declared once, in
    /// GameSpeedController.cs. This file declared its own copy first, because that
    /// file did not exist yet; the duplicate has been deleted. Nothing here
    /// references GameSpeedController, so the two files can be edited in parallel
    /// without a build race, and both compile against the single declaration.
    /// </summary>
    internal const string ArchitectureMismatchReason = "ArchitectureMismatch";

    internal const double MinimumFactor = ScaledClock.MinimumFactor;
    internal const double MaximumFactor = ScaledClock.MaximumFactor;
    internal const double NormalFactor = 1.0;
    internal const int BlockBytes = 96;

    // Control-block field offsets, byte-identical to GS_BLOCK in gamespeed.h.
    internal const int OffMagic = 0;
    internal const int OffVersion = 8;
    internal const int OffFlags = 16;
    internal const int OffHookCount = 24;
    internal const int OffOwnerHeartbeat = 32;
    internal const int OffDesiredFactorMicro = 40;
    internal const int OffGeneration = 48;
    internal const int OffActiveGeneration = 56;
    internal const int OffAppliedFactorMicro = 64;
    internal const int OffVirtualElapsedQpc = 72;
    internal const int OffAnchorQpc = 80;
    internal const int OffQpcFrequency = 88;

    private const long BlockMagic = 0x3130544C504D5347L;      // "GSMPLT01"
    private const long BlockVersion = 1;
    private const long FlagHooks = 0x1;
    private const long FlagFaulted = 0x2;
    private const long FlagDetached = 0x4;
    private const long FlagBlock = 0x8;

    private const int TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;

    private const uint ProcessCreateThread = 0x0002;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;

    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;

    private static readonly ConcurrentDictionary<int, SemaphoreSlim> Gates =
        new ConcurrentDictionary<int, SemaphoreSlim>();
    private static readonly ConcurrentDictionary<int, byte> Tracked = new ConcurrentDictionary<int, byte>();
    private static readonly System.Threading.Timer Heartbeat =
        new System.Threading.Timer(_ => BeatAll(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

/// <summary>
    /// A static class cannot implement an interface, so <c>Applier</c> is the single
    /// <see cref="ISpeedApplier"/> instance this assembly hands to GameSpeedController.
    /// </summary>
    internal static ISpeedApplier Applier { get; } = new NativeSpeedApplier();

    // -------------------------------------------------------------- naming

    internal static string ControlMapName(int processId) =>
        "Local\\GameLibrary-GameSpeed-" + processId.ToString(CultureInfo.InvariantCulture);

    // ------------------------------------------------- architecture handling

/// <summary>
    /// Maps the architecture probe result onto a target bitness. Pure, so the test
    /// suite can prove 64-bit and 32-bit selection without launching anything.
    /// <para>
    /// The machine PAIR is authoritative and the BOOL return of IsWow64Process2 is
    /// deliberately ignored: on this class of Windows it reports TRUE for ordinary
    /// 64-bit processes, so treating TRUE as "32-bit WOW64" classifies every 64-bit
    /// game as 32-bit and the correct hook then refuses to load. UNKNOWN for
    /// processMachine means "native bitness", which only IsWow64Process separates,
    /// so that is the tiebreak.
    /// </para>
    /// </summary>
    internal static GameSpeedArchitecture ClassifyArchitecture(ushort processMachine, ushort nativeMachine, bool isWow64Process)
    {
        const ushort unknown = 0;
        const ushort i386 = 0x014C;
        const ushort amd64 = 0x8664;
        const ushort arm64 = 0xAA64;

        if (processMachine == amd64 || processMachine == arm64) return GameSpeedArchitecture.X64;
        if (processMachine == i386) return GameSpeedArchitecture.X86;
        if (processMachine == unknown)
        {
            // A 32-bit WOW64 process is the only case where IsWow64Process is TRUE.
            if (isWow64Process) return GameSpeedArchitecture.X86;
            return nativeMachine == i386 ? GameSpeedArchitecture.X86 : GameSpeedArchitecture.X64;
        }
        return GameSpeedArchitecture.Unknown;
    }

internal delegate bool Wow64Probe(IntPtr process, out bool isWow64, out ushort processMachine, out ushort nativeMachine);

    internal static GameSpeedArchitecture DetectArchitecture(int processId, Wow64Probe probe)
    {
        if (probe == null) throw new ArgumentNullException(nameof(probe));
        if (processId <= 0) return GameSpeedArchitecture.Unknown;
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("Architecture of process " +
                processId.ToString(CultureInfo.InvariantCulture) + " could not be read: " + DescribeWin32("OpenProcess"));
        try
        {
            if (!probe(handle, out bool isWow64, out ushort processMachine, out ushort nativeMachine))
            {
                // Pre-1511 Windows has no IsWow64Process2; fall back to the legacy flag.
                if (!IsWow64Process(handle, out bool wow))
                    throw new InvalidOperationException("Architecture of process " +
                        processId.ToString(CultureInfo.InvariantCulture) + " could not be determined: " + DescribeWin32("IsWow64Process2"));
                return wow ? GameSpeedArchitecture.X86 : GameSpeedArchitecture.X64;
            }
            // The probe's own WOW64 result is the discriminator; see ClassifyArchitecture.
            return ClassifyArchitecture(processMachine, nativeMachine, isWow64);
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>
    /// A P/Invoke method cannot be converted to a managed delegate, so the live path
    /// goes through this wrapper. The USHORT out-parameters are read exactly as the
    /// native API writes them; widening them to uint would read two bytes the call
    /// never wrote. The BOOL return of IsWow64Process2 is not used; only its machine
    /// outputs are, plus IsWow64Process for the UNKNOWN tiebreak.
    /// </summary>
    private static bool Wow64ProbeImpl(IntPtr process, out bool isWow64, out ushort processMachine, out ushort nativeMachine)
    {
        isWow64 = IsWow64Process(process, out bool wow) && wow;
        return IsWow64Process2(process, out processMachine, out nativeMachine);
    }

    internal static GameSpeedArchitecture DetectArchitecture(int processId) =>
        DetectArchitecture(processId, Wow64ProbeImpl);
    /// <summary>Reads the machine type out of a PE COFF header. Pure and testable.</summary>
    internal static GameSpeedArchitecture ReadDllArchitecture(string dllPath)
    {
        byte[] header = new byte[512];
        int read;
        using (FileStream stream = File.Open(dllPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            read = stream.Read(header, 0, header.Length);
        if (read < 0x40) return GameSpeedArchitecture.Unknown;
        if (header[0] != (byte)'M' || header[1] != (byte)'Z') return GameSpeedArchitecture.Unknown;
        int peOffset = BitConverter.ToInt32(header, 0x3C);
        if (peOffset < 0 || peOffset + 6 > read) return GameSpeedArchitecture.Unknown;
        if (header[peOffset] != (byte)'P' || header[peOffset + 1] != (byte)'E' ||
            header[peOffset + 2] != 0 || header[peOffset + 3] != 0) return GameSpeedArchitecture.Unknown;
        ushort machine = BitConverter.ToUInt16(header, peOffset + 4);
        if (machine == 0x8664) return GameSpeedArchitecture.X64;
        if (machine == 0x014C) return GameSpeedArchitecture.X86;
        if (machine == 0xAA64) return GameSpeedArchitecture.X64;   // treated as 64-bit class
        return GameSpeedArchitecture.Unknown;
    }

    internal static string DescribeArchitectureMismatch(GameSpeedArchitecture target, GameSpeedArchitecture dll) =>
        ArchitectureMismatchReason + ": the game is a " + Describe(target) +
        " process but " + Describe(dll) + " was selected. A 64-bit hook cannot load into a 32-bit game and a 32-bit hook cannot load into a 64-bit game.";

    private static string Describe(GameSpeedArchitecture architecture) => architecture switch
    {
        GameSpeedArchitecture.X64 => "64-bit",
        GameSpeedArchitecture.X86 => "32-bit",
        _ => "unknown-bitness"
    };

    internal static string DllFileName(GameSpeedArchitecture architecture) =>
        architecture == GameSpeedArchitecture.X86 ? "gamespeed32.dll" : "gamespeed64.dll";

    /// <summary>
    /// Finds the hook DLL matching <paramref name="architecture"/>. Returns null when
    /// the matching build is absent, which the caller reports as ArchitectureMismatch
    /// rather than as a cryptic Win32 error.
    /// </summary>
    internal static string? ResolveDllPath(GameSpeedArchitecture architecture, string? baseDirectory = null)
    {
        if (architecture != GameSpeedArchitecture.X86 && architecture != GameSpeedArchitecture.X64) return null;
        string fileName = DllFileName(architecture);
        var roots = new List<string>(3);
        if (!string.IsNullOrEmpty(baseDirectory)) roots.Add(baseDirectory!);
        roots.Add(AppContext.BaseDirectory);
        roots.Add(AppDomain.CurrentDomain.BaseDirectory);
        foreach (string root in roots)
        {
            foreach (string relative in new[] { "tools" + Path.DirectorySeparatorChar + "gamespeed",
                                                "gamespeed",
                                                Path.Combine("..", "..", "tools", "gamespeed") })
            {
                try
                {
                    string candidate = Path.GetFullPath(Path.Combine(root, relative, fileName));
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
                catch (PathTooLongException) { }
            }
        }
        return null;
    }

    // ---------------------------------------------------- safety boundary

    private static readonly string[] OnlineIntegrityPrefixes =
    {
        "easyanticheat", "eac", "eaclauncher", "eac_background", "battleye", "bedaisy",
        "beclient", "beservice", "anticlient", "startguard", "xhunter", "acecore",
        "anticheatexpert", "nprotect", "gameguard", "xigncode", "punkbuster"
    };

    private static readonly string[] OnlineIntegrityExactNames =
    {
        "eac.exe", "eac.sys", "eaclauncher.exe", "easyanticheat.sys",
        "battleye.exe", "battleye.sys", "battleye.beclient", "bedaisy.dll", "beservice.exe",
        "anticlient.dll", "startguard32.dll", "startguard64.dll", "npgcore.dll",
        "gameguard.des", "ggengine.dll", "xigncode.dll", "x3.xp"
    };

    /// <summary>
    /// Pure matcher for online-service integrity modules. The engine refuses to
    /// touch these; it has no mechanism to hide from or defeat them and does not
    /// try. Pure so the refusal is provable in the test suite.
    /// </summary>
    internal static bool IsOnlineIntegrityModule(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        string name = Path.GetFileName(fileName).ToLowerInvariant();
        int dot = name.LastIndexOf('.');
        if (dot >= 0) name = name.Substring(0, dot);
        foreach (string exact in OnlineIntegrityExactNames)
            if (string.Equals(name, exact, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (string prefix in OnlineIntegrityPrefixes)
            if (name.StartsWith(prefix, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool HasOnlineIntegrityModule(IntPtr handle, out string? offender)
    {
        offender = null;
        IntPtr[] modules = new IntPtr[1024];
        int needed = 0;
        if (!EnumProcessModules(handle, modules, modules.Length * IntPtr.Size, out needed))
            return false;
        int count = Math.Min(modules.Length, needed / IntPtr.Size);
        for (int i = 0; i < count; i++)
        {
            IntPtr name = Marshal.AllocHGlobal(1024);
            try
            {
                if (GetModuleBaseNameA(handle, modules[i], name, 1024) != 0 &&
                    IsOnlineIntegrityModule(Marshal.PtrToStringAnsi(name) ?? ""))
                {
                    offender = Marshal.PtrToStringAnsi(name);
                    return true;
                }
            }
            finally { Marshal.FreeHGlobal(name); }
        }
        return false;
    }

    // ------------------------------------------------------------ identity

    /// <summary>
    /// Positively identifies the target: a live process in this session whose image
    /// path, creation stamp, bitness and integrity level can all be read, and which
    /// has no online-service integrity module loaded. Nothing is injected otherwise.
    /// </summary>
    internal static GameSpeedTarget CaptureTarget(int processId)
    {
        if (processId <= 0)
            throw new InvalidOperationException("A speed factor can only be applied to a running game process.");
        if (processId == Environment.ProcessId)
            throw new InvalidOperationException("The speed engine refuses to target this application; it only targets a launched game.");

        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation | ProcessQueryInformation, false, (uint)processId);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("Process " + processId.ToString(CultureInfo.InvariantCulture) +
                " is not running: " + DescribeWin32("OpenProcess"));

        try
        {
            string image = ReadImagePath(handle, processId);
            if (string.IsNullOrWhiteSpace(image))
                throw new InvalidOperationException("The image of process " + processId.ToString(CultureInfo.InvariantCulture) +
                    " could not be positively identified, so the speed engine refuses to target it.");
            if (IsOnlineIntegrityModule(image))
                throw new InvalidOperationException("The speed engine refuses to target an online-service integrity process (" +
                    image + "). It is for offline single-player games only.");

            if (HasOnlineIntegrityModule(handle, out string? offender))
                throw new InvalidOperationException("The speed engine refuses to target a process with an online-service " +
                    "integrity module loaded (" + offender + "). It is for offline single-player games only.");

            uint session = 0xFFFFFFFF;
            if (!ProcessIdToSessionId((uint)processId, out session))
                throw new InvalidOperationException("The session of process " + processId.ToString(CultureInfo.InvariantCulture) +
                    " could not be read: " + DescribeWin32("ProcessIdToSessionId"));
            int ownSession = Process.GetCurrentProcess().SessionId;
            if ((int)session != ownSession)
                throw new InvalidOperationException("Process " + processId.ToString(CultureInfo.InvariantCulture) +
                    " runs in session " + session.ToString(CultureInfo.InvariantCulture) +
                    " and this application runs in session " + ownSession.ToString(CultureInfo.InvariantCulture) +
                    "; the speed engine refuses to target another session.");

            string creation = ReadCreationStamp(processId);
            if (creation.Length == 0)
                throw new InvalidOperationException("The creation time of process " + processId.ToString(CultureInfo.InvariantCulture) +
                    " could not be positively identified, so the speed engine refuses to target it.");

            GameSpeedArchitecture architecture = DetectArchitecture(processId, Wow64ProbeImpl);
            if (architecture == GameSpeedArchitecture.Unknown)
                throw new InvalidOperationException("The bitness of process " + processId.ToString(CultureInfo.InvariantCulture) +
                    " could not be determined, so the speed engine refuses to target it.");

            int integrity = ReadIntegrityLevel(handle);
            int ownIntegrity = ReadIntegrityLevel(GetCurrentProcess());
            if (integrity > ownIntegrity)
                throw new InvalidOperationException(ElevationReason(integrity, ownIntegrity));
            return new GameSpeedTarget(processId, image, creation, (int)session, architecture, integrity, ownIntegrity);
        }
        finally { CloseHandle(handle); }
    }

    internal static string ElevationReason(int targetIntegrity, int ownIntegrity) =>
        // Total, not just for the raising case: describing a same-or-lower target as
        // needing administrator rights would send the user down a pointless detour.
        targetIntegrity > ownIntegrity
            ? "The game is running at a higher integrity level (game " +
              targetIntegrity.ToString(CultureInfo.InvariantCulture) + ", this application " +
              ownIntegrity.ToString(CultureInfo.InvariantCulture) + "). Start this application as administrator, or start the game " +
              "normally, so both run at the same level."
            : "No elevation is needed: the game runs at integrity level " +
              targetIntegrity.ToString(CultureInfo.InvariantCulture) + " and this application at " +
              ownIntegrity.ToString(CultureInfo.InvariantCulture) + ".";

    internal static async Task<GameSpeedTarget> IdentifyAsync(int processId, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return await Task.Run(() => CaptureTarget(processId), cancellation).ConfigureAwait(false);
    }

    /// <summary>Verifies a target has not been replaced by a different process since capture.</summary>
    internal static bool IsStillSameTarget(GameSpeedTarget target)
    {
        if (target.ProcessId <= 0) return false;
        string creation = ReadCreationStamp(target.ProcessId);
        return creation.Length > 0 && string.Equals(creation, target.CreationFileTime, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadImagePath(IntPtr handle, int processId)
    {
        IntPtr buffer = Marshal.AllocHGlobal(32768);
        try
        {
            int size = 32768;
            if (QueryFullProcessImageName(handle, 0, buffer, ref size)) return Marshal.PtrToStringAnsi(buffer) ?? "";
        }
        finally { Marshal.FreeHGlobal(buffer); }

        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.MainModule?.FileName ?? "";
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
        {
            return "";
        }
    }

    private static string ReadCreationStamp(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime().ToFileTimeUtc().ToString("X16", CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
        {
            return "";
        }
    }

    internal static int ReadIntegrityLevel(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return -1;
        IntPtr token = IntPtr.Zero;
        if (!OpenProcessToken(handle, TokenQuery, out token)) return -1;
        try
        {
            int needed = 0;
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out needed);
            if (needed <= 0) return -1;
            IntPtr buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, needed, out _)) return -1;
                IntPtr sid = Marshal.ReadIntPtr(buffer);
                if (sid == IntPtr.Zero) return -1;
                byte count = Marshal.ReadByte(sid, 1);
                if (count == 0) return -1;
                return Marshal.ReadInt32(sid, 8 + (count - 1) * 4);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(token); }
    }

    internal static int CurrentIntegrityLevel() => ReadIntegrityLevel(GetCurrentProcess());

    /// <summary>True only while a live process with that id exists. The speed engine is a no-op otherwise.</summary>
    internal static bool IsRunning(int processId)
    {
        if (processId <= 0) return false;
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------- injection

    /// <summary>
    /// Injects the hook DLL into a running game: OpenProcess, VirtualAllocEx,
    /// WriteProcessMemory, then a remote LoadLibraryW. The target is never suspended,
    /// and cancellation is observed between steps and while waiting for the loader
    /// thread, so no remote memory is freed while the loader may still be reading it.
    /// </summary>
    internal static async Task InjectAsync(int processId, string dllPath, CancellationToken cancellation)
    {
        if (string.IsNullOrEmpty(dllPath))
            throw new InvalidOperationException("The speed engine needs the path of its hook library.");

        GameSpeedTarget target = CaptureTarget(processId);

        if (!File.Exists(dllPath))
            throw new InvalidOperationException(ArchitectureMismatchReason + ": " + dllPath + " does not exist, so the " +
                Describe(target.Architecture) + " speed hook for game " +
                processId.ToString(CultureInfo.InvariantCulture) + " is unavailable.");

        GameSpeedArchitecture dllArchitecture = ReadDllArchitecture(dllPath);
        if (dllArchitecture == GameSpeedArchitecture.Unknown)
            throw new InvalidOperationException(ArchitectureMismatchReason + ": " + dllPath +
                " is not a readable Windows image, so it cannot be loaded into the game.");
        if (dllArchitecture != target.Architecture)
            throw new InvalidOperationException(DescribeArchitectureMismatch(target.Architecture, dllArchitecture));

        cancellation.ThrowIfCancellationRequested();

        await Task.Run(() => InjectCore(target, dllPath, cancellation), cancellation).ConfigureAwait(false);

        if (!IsStillSameTarget(target))
            throw new InvalidOperationException("The game process " + processId.ToString(CultureInfo.InvariantCulture) +
                " exited or was replaced while the speed hook was being loaded.");

        Tracked[processId] = 1;
        EnsureControlBlock(processId);
    }

    /// <summary>Injects the hook build matching the game's own bitness.</summary>
    internal static async Task InjectAsync(int processId, CancellationToken cancellation)
    {
        GameSpeedTarget target = CaptureTarget(processId);
        string? dllPath = ResolveDllPath(target.Architecture);
        if (dllPath == null)
            throw new InvalidOperationException(ArchitectureMismatchReason + ": no " + DllFileName(target.Architecture) +
                " was found beside this application, so the " + Describe(target.Architecture) +
                " speed hook for game " + processId.ToString(CultureInfo.InvariantCulture) + " is unavailable.");
        await InjectAsync(processId, dllPath, cancellation).ConfigureAwait(false);
    }

    private static void InjectCore(GameSpeedTarget target, string dllPath, CancellationToken cancellation)
    {
        IntPtr process = OpenProcess(
            ProcessCreateThread | ProcessQueryInformation | ProcessVmOperation | ProcessVmRead |
            ProcessVmWrite | Synchronize, false, (uint)target.ProcessId);
        if (process == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 5)
                throw new InvalidOperationException(ElevationReason(target.IntegrityLevel, target.OwnIntegrityLevel) +
                    " (OpenProcess failed with Win32 error 5, ERROR_ACCESS_DENIED.)");
            throw new InvalidOperationException("The game process " +
                target.ProcessId.ToString(CultureInfo.InvariantCulture) + " could not be opened for speed control: " +
                DescribeWin32("OpenProcess"));
        }

        byte[] remoteName = new byte[0];
        IntPtr remote = IntPtr.Zero;
        IntPtr thread = IntPtr.Zero;
        try
        {
            byte[] pathBytes = System.Text.Encoding.Unicode.GetBytes(Path.GetFullPath(dllPath) + "\0");
            remoteName = pathBytes;
            remote = VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)pathBytes.Length, MemCommit | MemReserve, PageReadWrite);
            if (remote == IntPtr.Zero)
                throw new InvalidOperationException("Memory could not be reserved inside the game process for the speed hook: " +
                    DescribeWin32("VirtualAllocEx"));
            if (!WriteProcessMemory(process, remote, pathBytes, (UIntPtr)pathBytes.Length, out UIntPtr written) || written != (UIntPtr)pathBytes.Length)
                throw new InvalidOperationException("The speed hook path could not be written into the game process: " +
                    DescribeWin32("WriteProcessMemory"));

            cancellation.ThrowIfCancellationRequested();

            IntPtr loadLibrary = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryW");
            if (loadLibrary == IntPtr.Zero)
                throw new InvalidOperationException("LoadLibraryW could not be located in kernel32.dll: " + DescribeWin32("GetProcAddress"));

            thread = CreateRemoteThread(process, IntPtr.Zero, 0, loadLibrary, remote, 0, out _);
            if (thread == IntPtr.Zero)
                throw new InvalidOperationException("The speed hook loader thread could not be started in the game process: " +
                    DescribeWin32("CreateRemoteThread"));

            uint exit = 0;
            WaitRemoteThread(thread, cancellation, TimeSpan.FromSeconds(15), out exit);
            if (exit == 0)
            {
                // LoadLibraryW reported failure inside the target. ERROR_BAD_EXE_FORMAT
                // is the definitive bitness mismatch; anything else is reported as-is.
                string detail = DescribeWin32("LoadLibraryW (in the game process)", exit);
                throw new InvalidOperationException(ArchitectureMismatchReason +
                    ": the speed hook library was rejected by the game process, " + detail + ". " +
                    DescribeArchitectureMismatch(target.Architecture, ReadDllArchitecture(dllPath)));
            }
        }
        finally
        {
            // Remote memory may only be freed once the loader thread has finished.
            if (thread != IntPtr.Zero) CloseHandle(thread);
            if (remote != IntPtr.Zero) VirtualFreeEx(process, remote, UIntPtr.Zero, MemRelease);
            CloseHandle(process);
        }
    }

    private static void WaitRemoteThread(IntPtr thread, CancellationToken cancellation, TimeSpan timeout, out uint exitCode)
    {
        exitCode = 0;
        DateTime deadline = DateTime.UtcNow + timeout;
        for (;;)
        {
            uint waited = WaitForSingleObject(thread, 100);
            if (waited == 0)
            {
                if (!GetExitCodeThread(thread, out exitCode))
                    throw new InvalidOperationException("The speed hook loader result could not be read: " + DescribeWin32("GetExitCodeThread"));
                return;
            }
            if (waited != 258 /* WAIT_TIMEOUT */)
                throw new InvalidOperationException("Waiting for the speed hook loader failed: " + DescribeWin32("WaitForSingleObject"));
            if (cancellation.IsCancellationRequested)
                throw new OperationCanceledException(cancellation);
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The game did not finish loading the speed hook within " +
                    ((int)timeout.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " seconds.");
        }
    }

    // ------------------------------------------------------ control block

    /// <summary>Opens or creates the control block and stamps its identity fields.</summary>
    internal static MemoryMappedFile EnsureControlBlock(int processId)
    {
        string name = ControlMapName(processId);
        MemoryMappedFile file = MemoryMappedFile.CreateOrOpen(name, BlockBytes, MemoryMappedFileAccess.ReadWrite);
        MemoryMappedViewAccessor view = file.CreateViewAccessor();
        try
        {
            if (view.ReadInt64(OffMagic) != BlockMagic || view.ReadInt64(OffVersion) != BlockVersion)
            {
                view.Write(OffFlags, 0L);
                view.Write(OffHookCount, 0L);
                view.Write(OffDesiredFactorMicro, 0L);
                view.Write(OffGeneration, 0L);
                view.Write(OffActiveGeneration, 0L);
                view.Write(OffAppliedFactorMicro, ScaledClock.FactorMicroScale);
                view.Write(OffVirtualElapsedQpc, 0L);
                view.Write(OffAnchorQpc, 0L);
                view.Write(OffQpcFrequency, 0L);
                view.Write(OffVersion, BlockVersion);
                view.Write(OffMagic, BlockMagic);
            }
            view.Write(OffOwnerHeartbeat, Interlocked.Increment(ref HeartbeatCounter));
            return file;
        }
        finally { view.Dispose(); }
    }

    private static long HeartbeatCounter;

    private static void BeatAll()
    {
        foreach (int pid in Tracked.Keys)
        {
            try
            {
                using MemoryMappedFile file = MemoryMappedFile.OpenExisting(ControlMapName(pid), MemoryMappedFileRights.ReadWrite);
                using MemoryMappedViewAccessor view = file.CreateViewAccessor(0, BlockBytes, MemoryMappedFileAccess.ReadWrite);
                view.Write(OffOwnerHeartbeat, Interlocked.Increment(ref HeartbeatCounter));
            }
            catch (FileNotFoundException) { }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
    }

    /// <summary>
    /// Requests a factor and waits for the hook to confirm it. A factor the engine
    /// will not accept is rejected without touching the factor already in force.
    /// </summary>
    internal static async Task<double> SetSpeedAsync(int processId, double factor, CancellationToken cancellation)
    {
if (!ScaledClock.ValidateFactor(factor))
            throw new InvalidOperationException("A speed factor must be a finite number between " +
                MinimumFactor.ToString(CultureInfo.InvariantCulture) + " and " +
                MaximumFactor.ToString(CultureInfo.InvariantCulture) + " and was rejected; the current speed is unchanged.");
        if (!IsRunning(processId))
            throw new InvalidOperationException("Process " + processId.ToString(CultureInfo.InvariantCulture) +
                " is not running, so there is no game to speed up; the speed factor only applies while a game is running.");

        SemaphoreSlim gate = Gates.GetOrAdd(processId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            using MemoryMappedFile file = EnsureControlBlock(processId);
            using MemoryMappedViewAccessor view = file.CreateViewAccessor();
            view.Write(OffDesiredFactorMicro, ScaledClock.ToMicro(factor));
            long generation = view.ReadInt64(OffGeneration) + 1;
            view.Write(OffGeneration, generation);
            view.Write(OffOwnerHeartbeat, Interlocked.Increment(ref HeartbeatCounter));
            Tracked[processId] = 1;

            long deadline = Environment.TickCount64 + 2000;
            while (Environment.TickCount64 < deadline)
            {
                cancellation.ThrowIfCancellationRequested();
                if (view.ReadInt64(OffActiveGeneration) == generation)
                    return ScaledClock.FromMicro(view.ReadInt64(OffAppliedFactorMicro));
                await Task.Delay(20, cancellation).ConfigureAwait(false);
            }
            throw new TimeoutException("The game did not confirm speed " +
                factor.ToString("0.###", CultureInfo.InvariantCulture) +
                " within two seconds; the speed hook is not responding in process " +
                processId.ToString(CultureInfo.InvariantCulture) + ".");
        }
        finally { gate.Release(); }
    }

    /// <summary>Returns the game to normal speed by re-basing, with no time jump.</summary>
    internal static Task ResetAsync(int processId, CancellationToken cancellation) =>
        SetSpeedAsync(processId, NormalFactor, cancellation);

    internal static async Task<double> GetSpeedAsync(int processId, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return await Task.Run(() =>
        {
            using MemoryMappedFile file = MemoryMappedFile.OpenExisting(ControlMapName(processId), MemoryMappedFileRights.ReadWrite);
            using MemoryMappedViewAccessor view = file.CreateViewAccessor(0, BlockBytes, MemoryMappedFileAccess.ReadWrite);
            if (view.ReadInt64(OffMagic) != BlockMagic)
                throw new InvalidOperationException("The speed hook is not present in process " +
                    processId.ToString(CultureInfo.InvariantCulture) + ".");
            return ScaledClock.FromMicro(view.ReadInt64(OffAppliedFactorMicro));
        }, cancellation).ConfigureAwait(false);
    }

    internal static async Task<GameSpeedStatus> GetStatusAsync(int processId, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return await Task.Run(() =>
        {
            using MemoryMappedFile file = MemoryMappedFile.OpenExisting(ControlMapName(processId), MemoryMappedFileRights.ReadWrite);
            using MemoryMappedViewAccessor view = file.CreateViewAccessor(0, BlockBytes, MemoryMappedFileAccess.ReadWrite);
            long flags = view.ReadInt64(OffMagic) == BlockMagic ? view.ReadInt64(OffFlags) : 0;
            return new GameSpeedStatus(processId,
                view.ReadInt64(OffMagic) == BlockMagic,
                (flags & FlagHooks) != 0,
                (flags & FlagFaulted) != 0,
                (int)view.ReadInt64(OffHookCount),
                ScaledClock.FromMicro(view.ReadInt64(OffAppliedFactorMicro)));
        }, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Detaches from a running game. The clock is re-based to 1.0, where every hook
    /// is a transparent pass-through, so the game keeps running at real speed. The
    /// hook library itself is deliberately left loaded: restoring patched bytes while
    /// another thread is executing through a hook is a crash risk, and at 1.0 the
    /// remaining cost is one compare on a cached line per clock query.
    /// </summary>
    internal static async Task<bool> UninjectAsync(int processId, CancellationToken cancellation)
    {
        SemaphoreSlim gate = Gates.GetOrAdd(processId, _ => new SemaphoreSlim(1, 1));
        bool released = false;
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            try
            {
                using MemoryMappedFile file = MemoryMappedFile.OpenExisting(ControlMapName(processId), MemoryMappedFileRights.ReadWrite);
                using MemoryMappedViewAccessor view = file.CreateViewAccessor(0, BlockBytes, MemoryMappedFileAccess.ReadWrite);
                view.Write(OffDesiredFactorMicro, 0L);
                long generation = view.ReadInt64(OffGeneration) + 1;
                view.Write(OffGeneration, generation);
                view.Write(OffFlags, view.ReadInt64(OffFlags) | FlagDetached);
                view.Write(OffOwnerHeartbeat, Interlocked.Increment(ref HeartbeatCounter));
                long deadline = Environment.TickCount64 + 2000;
                while (Environment.TickCount64 < deadline)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (view.ReadInt64(OffActiveGeneration) == generation) { released = true; break; }
                    await Task.Delay(20, cancellation).ConfigureAwait(false);
                }
            }
            catch (FileNotFoundException)
            {
                // The game already exited and took its mapping with it.
                released = true;
            }
            catch (UnauthorizedAccessException) { released = false; }
            return released;
        }
        finally
        {
            Tracked.TryRemove(processId, out _);
            gate.Release();
        }
    }

    /// <summary>Best-effort release for application shutdown. Synchronous by design.</summary>
    internal static void ReleaseAll()
    {
        foreach (int pid in Tracked.Keys)
        {
            try
            {
                using MemoryMappedFile file = MemoryMappedFile.OpenExisting(ControlMapName(pid), MemoryMappedFileRights.ReadWrite);
                using MemoryMappedViewAccessor view = file.CreateViewAccessor(0, BlockBytes, MemoryMappedFileAccess.ReadWrite);
                view.Write(OffDesiredFactorMicro, 0L);
                view.Write(OffFlags, view.ReadInt64(OffFlags) | FlagDetached);
                view.Write(OffGeneration, view.ReadInt64(OffGeneration) + 1);
            }
            catch (FileNotFoundException) { }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        Tracked.Clear();
    }

/// <summary>
    /// Implements slot 7's <see cref="ISpeedApplier"/>: the native half behind
    /// GameSpeedController. A static class cannot implement an interface, so this
    /// tiny shim carries the contract and forwards to <see cref="GameSpeedNative"/>.
    /// Inspect and ApplySpeed are deliberately non-async - the controller drives them
    /// from a UI tick and cannot afford to await - and both honour "only while a
    /// game is running" by reporting the not-applied state rather than throwing.
    /// </summary>
   private sealed class NativeSpeedApplier : ISpeedApplier
   {
    // Nothing injected means normal speed. Reporting the struct default here would
    // report 0.0x for a process that is not running, which is both false and a value
    // no consumer of a speed bar should ever display.
    private static readonly SpeedTargetState NotApplied =
        new(Alive: false, IsLibraryGame: false, CurrentMultiplier: GameSpeedNative.NormalFactor);

    public SpeedTargetState Inspect(SpeedTarget target)
    {
    if (target == null || !IsRunning(target.ProcessId)) return NotApplied;
    try
    {
    GameSpeedStatus status = GetStatusAsync(target.ProcessId, CancellationToken.None).GetAwaiter().GetResult();
    if (!status.Injected) return NotApplied;
    double current = double.IsFinite(status.AppliedFactor) && status.AppliedFactor > 0
        ? status.AppliedFactor : GameSpeedNative.NormalFactor;
    return new SpeedTargetState(Alive: true, IsLibraryGame: status.HooksInstalled, CurrentMultiplier: current);
    }
    catch (Exception ex) when (ex is InvalidOperationException || ex is IOException || ex is TimeoutException)
    {
    return NotApplied;
    }
    }

        public double ApplySpeed(SpeedTarget target, double multiplier)
        {
            if (target == null || !ScaledClock.ValidateFactor(multiplier) || !IsRunning(target.ProcessId))
                return NormalFactor;
            try
            {
                return SetSpeedAsync(target.ProcessId, multiplier, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is IOException ||
                                       ex is TimeoutException || ex is UnauthorizedAccessException)
            {
                return NormalFactor;
            }
        }
    }

    // ------------------------------------------------------------ Win32

    internal static string DescribeWin32(string operation, uint? processExitCode = null)
    {
        string code;
        if (processExitCode.HasValue)
        {
            uint exit = processExitCode.Value;
            if (exit == 193) code = "ERROR_BAD_EXE_FORMAT (193) - the file is the wrong bitness for this process";
            else if (exit == 1114) code = "ERROR_DLL_INIT_FAILED (1114)";
            else if (exit == 126) code = "ERROR_MOD_NOT_FOUND (126)";
            else if (exit == 5) code = "ERROR_ACCESS_DENIED (5)";
            else code = "return code 0x" + exit.ToString("X8", CultureInfo.InvariantCulture);
            return operation + " reported " + code + ".";
        }
        int last = Marshal.GetLastWin32Error();
        return operation + " failed with Win32 error " + last.ToString(CultureInfo.InvariantCulture) + ": " + new System.ComponentModel.Win32Exception(last).Message;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process(IntPtr process, [MarshalAs(UnmanagedType.Bool)] out bool wow64Process);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, IntPtr exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr baseAddress, byte[] buffer, UIntPtr size, out UIntPtr bytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr threadAttributes, UIntPtr stackSize, IntPtr startAddress, IntPtr parameter, uint creationFlags, out IntPtr threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeThread(IntPtr thread, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procName);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumProcessModules(IntPtr process, IntPtr[] modules, int size, out int needed);

    [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern uint GetModuleBaseNameA(IntPtr process, IntPtr module, IntPtr buffer, int size);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);
}

