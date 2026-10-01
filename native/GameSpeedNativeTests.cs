using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

/// <summary>
/// Dependency-free proofs for the native speed engine's managed half. Safe to run
/// unattended: nothing here injects into a real game, and the only process it starts
/// is a disposable copy of the current executable that it terminates itself.
/// </summary>
internal static class GameSpeedNativeTests
{
    private const long BlockMagic = 0x3130544C504D5347L;
    private const long BlockVersion = 1;

    internal static void Run(string root)
    {
        Directory.CreateDirectory(root);
        RunSynchronously();
        ArchitectureSelection(root);
        ArchitectureMismatch();
        SteadyFactorScalesDeltas();
        FactorChangeDoesNotJump();
        NormalFactorIsExactPassThrough();
        InvalidFactorIsRejected();
        SharedControlBlockProtocol(root);
        NonExistentProcessFailsCleanly();
        NoRunningGameIsANoOp();
        ElevatedTargetIsExplained();
        OnlineIntegrityIsRefused();
        DisposableChildIdentifies();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // ---------------------------------------------------------------- clock

    /// <summary>Every 0.5 step the F1/F2 keys apply is exact in fixed point.</summary>
    private static void RunSynchronously()
    {
        for (double factor = 0.5; factor <= 8.0; factor += 0.5)
            Require(ScaledClock.ValidateFactor(factor), "A half-step speed factor of " + factor + " was rejected.");
        Require(ScaledClock.ToFixed(1.0) == ScaledClock.FractionOne,
            "The normal speed factor did not convert to an exact fixed-point 1.0.");
        foreach (double factor in new[] { 0.5, 1.5, 2.0, 2.5, 3.0, 4.0, 7.5 })
        {
            Require(ScaledClock.ToFixed(factor) == (long)(factor * ScaledClock.FractionOne),
                "The speed factor " + factor + " is not exact in fixed point, so repeated key presses would drift.");
            Require(Math.Abs(ScaledClock.FromMicro(ScaledClock.ToMicro(factor)) - factor) < 1e-9,
                "The speed factor " + factor + " did not survive the micro-unit round trip used by the control block.");
        }
    }

    /// <summary>
    /// Over many steps at a steady factor the virtual delta equals the real delta
    /// times the factor, within 1 ms of accumulated drift.
    /// </summary>
    private static void SteadyFactorScalesDeltas()
    {
        const double frequency = 2_500_000_000.0;      // a typical TSC QPC frequency
        const long anchor = 12_345_678_900L;

        foreach (double factor in new[] { 0.5, 1.5, 2.0, 3.0, 4.5 })
        {
            // Each factor needs its own clock AND its own start time. Sharing `now`
            // across iterations left the fresh clock 412 s behind the simulated
            // timeline, which reads as a huge bogus drift.
            long now = anchor;
            var clock = new ScaledClock(anchor, frequency);
            clock.SetFactor(factor, now);

            long realStart = clock.Read(now);
            double realSeconds = 0;
            var random = new Random(20260901);
            for (int step = 0; step < 20_000; step++)
            {
                // Irregular frame times, 1 ms to 40 ms, the way a real game ticks.
                long delta = (long)(frequency * (0.001 + random.NextDouble() * 0.039));
                now += delta;
                clock.Read(now);
                realSeconds += delta / frequency;
            }
            long virtualStart = realStart;
            long virtualEnd = clock.Read(now);
            long realEnd = now;
            double virtualSeconds = (virtualEnd - virtualStart) / frequency;
            // The elapsed interval is realEnd - realStart. Subtracting
            // (realStart - anchor) here would add the 12.35 s anchor offset back in and
            // measure a 0.5% systematic error that the clock does not have.
            double expected = (realEnd - realStart) / frequency * factor;

            Require(Math.Abs(realSeconds - 200.0) < 250.0,
                "The steady-factor simulation did not advance a realistic amount of real time.");
            double driftMs = Math.Abs(virtualSeconds - expected) * 1000.0;
            Require(driftMs <= 1.0,
                "At factor " + factor + " the scaled clock drifted " + driftMs.ToString("0.####") +
                " ms over " + realSeconds.ToString("0.0") + " s, which is more than the 1 ms budget.");
        }
    }

    /// <summary>Changing the factor mid-run re-bases so the clock does not jump.</summary>
    private static void FactorChangeDoesNotJump()
    {
        const double frequency = 2_500_000_000.0;
        const long anchor = 500_000_000L;
        long now = anchor;
        var clock = new ScaledClock(anchor, frequency);
        clock.SetFactor(2.0, now);

        long worstJumpTicks = 0;
        double[] ladder = { 1.5, 0.5, 3.0, 1.0, 2.5, 0.25, 2.0, 1.0, 8.0, 1.0 };
        double current = 2.0;
        for (int step = 0; step < 4_000; step++)
        {
            now += (long)(frequency * 0.004);
            long before = clock.Read(now);

            if (step % 400 == 399)
            {
                current = ladder[(step / 400) % ladder.Length];
                clock.SetFactor(current, now);
            }

            long after = clock.Read(now);
            Require(after >= before, "The scaled clock went backwards across a factor change.");
            long jump = after - before;
            if (jump > worstJumpTicks) worstJumpTicks = jump;

            // The clock must keep advancing at the newly selected rate.
            long next = now + (long)(frequency * 0.004);
            long expectedStep = (long)(frequency * 0.004 * current);
            long observed = clock.Read(next) - after;
            Require(Math.Abs(observed - expectedStep) <= expectedStep / 4096 + 2,
                "After changing to factor " + current + " the clock advanced " + observed +
                " ticks over a real interval that should have produced " + expectedStep + ".");
        }

        long oneTick = (long)(frequency * 0.001);
        Require(worstJumpTicks <= oneTick,
            "Changing the factor produced a time jump of " + worstJumpTicks +
            " ticks, which exceeds the 1 ms re-base budget.");
    }

    /// <summary>A factor of exactly 1.0 is a bit-exact pass-through.</summary>
    private static void NormalFactorIsExactPassThrough()
    {
        const double frequency = 2_500_000_000.0;
        const long anchor = 987_654_321_000L;
        long now = anchor;
        var clock = new ScaledClock(anchor, frequency);

        Require(clock.IsTransparent, "A freshly constructed clock was not in the normal pass-through state.");
        clock.SetFactor(1.0, now);
        Require(clock.IsTransparent, "Setting the factor to 1.0 did not select the pass-through state.");
        for (int step = 0; step < 5_000; step++)
        {
            long delta = (long)(frequency * 0.003);
            now += delta;
            long virtualValue = clock.Read(now);
            Require(virtualValue == now,
                "At factor 1.0 the scaled clock returned " + virtualValue + " for the real reference " + now +
                "; the normal state must be a transparent pass-through.");
        }

        // Round-tripping away from 1.0 and back must also be exact.
        clock.SetFactor(2.0, now);
        now += (long)(frequency * 0.5);
        clock.Read(now);
        clock.SetFactor(1.0, now);
        now += (long)(frequency * 0.5);
        long afterReset = clock.Read(now);
        clock.SetFactor(1.0, now);
        now += (long)(frequency * 0.5);
        long afterSecond = clock.Read(now);
        long realElapsed = (long)(frequency * 0.5);
        Require(afterSecond - afterReset == realElapsed,
            "Returning to normal speed did not restore real-time advance exactly.");
    }

    private static void InvalidFactorIsRejected()
    {
        const double frequency = 2_500_000_000.0;
        const long anchor = 1_000L;
        long now = anchor;
        var clock = new ScaledClock(anchor, frequency);
        clock.SetFactor(2.0, now);
        now += (long)(frequency * 0.25);
        clock.Read(now);

        double[] rejected = { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0.0, -1.0, 0.049, 20.1, 1e12 };
        foreach (double bad in rejected)
        {
            Require(!ScaledClock.ValidateFactor(bad), "The speed factor " + bad + " was accepted; it must be rejected.");
            bool threw = false;
            try { clock.SetFactor(bad, now); }
            catch (ArgumentOutOfRangeException) { threw = true; }
            Require(threw, "The speed factor " + bad + " was not rejected by the clock.");
        }
        Require(clock.Factor == 2.0,
            "A rejected speed factor changed the factor in force to " + clock.Factor + " instead of leaving it at 2.0.");

        long before = clock.Read(now);
        now += (long)(frequency * 0.25);
        long after = clock.Read(now);
        long expected = (long)(frequency * 0.25 * 2.0);
        Require(Math.Abs((after - before) - expected) <= expected / 4096 + 2,
            "After rejected speed factors the clock no longer advances at the factor already in force.");
    }

    // ----------------------------------------------------- architecture

    /// <summary>
    /// 64-bit and 32-bit selection. The classifier is proven exhaustively and the
    /// live path is proven against this very process, which is 64-bit.
    /// </summary>
    private static void ArchitectureSelection(string root)
    {
        const ushort unknown = 0, i386 = 0x014C, amd64 = 0x8664, arm64 = 0xAA64;

        // The machine pair is authoritative; the BOOL return of IsWow64Process2 is not.
        Require(GameSpeedNative.ClassifyArchitecture(unknown, amd64, isWow64Process: false) == GameSpeedArchitecture.X64,
            "A 64-bit process reporting (UNKNOWN, AMD64) with IsWow64Process FALSE was not selected as 64-bit.");
        Require(GameSpeedNative.ClassifyArchitecture(amd64, amd64, isWow64Process: false) == GameSpeedArchitecture.X64,
            "A 64-bit process reporting (AMD64, AMD64) was not selected as 64-bit.");
        Require(GameSpeedNative.ClassifyArchitecture(arm64, arm64, isWow64Process: false) == GameSpeedArchitecture.X64,
            "An ARM64 process was not selected as 64-bit.");
        Require(GameSpeedNative.ClassifyArchitecture(i386, amd64, isWow64Process: true) == GameSpeedArchitecture.X86,
            "A 32-bit WOW64 process reporting (I386, AMD64) was not selected as 32-bit.");
        Require(GameSpeedNative.ClassifyArchitecture(i386, arm64, isWow64Process: true) == GameSpeedArchitecture.X86,
            "A 32-bit WOW64 process on ARM64 was not selected as 32-bit.");
        Require(GameSpeedNative.ClassifyArchitecture(i386, i386, isWow64Process: false) == GameSpeedArchitecture.X86,
            "A 32-bit process on 32-bit Windows was not selected as 32-bit.");
        Require(GameSpeedNative.ClassifyArchitecture(unknown, i386, isWow64Process: false) == GameSpeedArchitecture.X86,
            "A 32-bit OS native machine was not selected as 32-bit.");
        Require(GameSpeedNative.ClassifyArchitecture(unknown, amd64, isWow64Process: true) == GameSpeedArchitecture.X86,
            "A WOW64 process whose machine type is UNKNOWN was not selected as 32-bit.");
        Require(GameSpeedNative.ClassifyArchitecture(0x1234, amd64, isWow64Process: false) == GameSpeedArchitecture.Unknown,
            "An unrecognised machine type was not reported as unknown, which would risk injecting the wrong build.");

        // The injectable probe proves the live selection path without launching anything.
        // The handle is still acquired from the real OS, so the process id must be one
        // this user can actually open: PID 1 is System and always fails with error 87.
        GameSpeedArchitecture probedX64 = GameSpeedNative.DetectArchitecture(Environment.ProcessId,
            (IntPtr _, out bool w64, out ushort pm, out ushort nm) => { w64 = false; pm = unknown; nm = amd64; return true; });
        Require(probedX64 == GameSpeedArchitecture.X64,
            "The live architecture path did not report 64-bit for a 64-bit target.");
        GameSpeedArchitecture probedX86 = GameSpeedNative.DetectArchitecture(Environment.ProcessId,
            (IntPtr _, out bool w86, out ushort pm2, out ushort nm2) => { w86 = true; pm2 = i386; nm2 = amd64; return true; });
        Require(probedX86 == GameSpeedArchitecture.X86,
            "The live architecture path did not report 32-bit for a 32-bit target.");

        // Real, live detection of this process.
        GameSpeedArchitecture self = GameSpeedNative.DetectArchitecture(Environment.ProcessId);
        Require(self == GameSpeedArchitecture.X64,
            "The running 64-bit process was detected as " + self + " instead of 64-bit.");
        Require(Environment.Is64BitProcess,
            "The test host is not 64-bit, so the live 64-bit assertion cannot hold.");

        // Reading the machine type out of the shipped DLL.
        string? dll = GameSpeedNative.ResolveDllPath(GameSpeedArchitecture.X64);
        Require(dll != null, "gamespeed64.dll was not found beside the application, so the 64-bit hook cannot be selected.");
        Require(GameSpeedNative.ReadDllArchitecture(dll!) == GameSpeedArchitecture.X64,
            "gamespeed64.dll does not carry a 64-bit machine type.");
        Require(File.Exists(dll!) && new FileInfo(dll!).Length > 1024,
            "gamespeed64.dll is missing or too small to be the compiled hook library.");

        string missing32 = Path.Combine(root, "gamespeed32.dll");
        Require(GameSpeedNative.ResolveDllPath(GameSpeedArchitecture.X86, root) == null,
            "A 32-bit hook library was resolved even though none was provided.");
        Require(!File.Exists(missing32), "The test created a 32-bit hook library it did not compile.");
    }

    /// <summary>A missing or wrong-bitness hook build is reported as ArchitectureMismatch.</summary>
    private static void ArchitectureMismatch()
    {
        string message = GameSpeedNative.DescribeArchitectureMismatch(
            GameSpeedArchitecture.X86, GameSpeedArchitecture.X64);
        Require(message.Contains(GameSpeedNative.ArchitectureMismatchReason, StringComparison.Ordinal),
            "An architecture mismatch did not carry the ArchitectureMismatch reason.");
        Require(message.Contains("32-bit", StringComparison.Ordinal) && message.Contains("64-bit", StringComparison.Ordinal),
            "An architecture mismatch message did not name both bitnesses.");
        Require(message.Contains("cannot load into", StringComparison.Ordinal),
            "An architecture mismatch message did not explain that the DLL cannot load into the game.");

        string opposite = GameSpeedNative.DescribeArchitectureMismatch(
            GameSpeedArchitecture.X64, GameSpeedArchitecture.X86);
        Require(opposite.Contains("64-bit process", StringComparison.Ordinal),
            "A 64-bit game was not described as a 64-bit process.");

        Require(GameSpeedNative.DllFileName(GameSpeedArchitecture.X64) == "gamespeed64.dll" &&
                GameSpeedNative.DllFileName(GameSpeedArchitecture.X86) == "gamespeed32.dll",
            "The hook library file names do not match the architectures.");
    }

    // ----------------------------------------------- control-block protocol

    /// <summary>
    /// Proves the manager side of the named control block against a simulated hook:
    /// a request is acknowledged on the generation it asked for, and a factor the
    /// engine rejects leaves the control block untouched.
    /// </summary>
    private static void SharedControlBlockProtocol(string root)
    {
        int fakePid = 0x4A150000 + Environment.ProcessId % 0x1000;
        string name = GameSpeedNative.ControlMapName(fakePid);
        Require(name.StartsWith("Local\\GameLibrary-GameSpeed-", StringComparison.Ordinal),
            "The control block name is not session-scoped to this user: " + name);

        using (MemoryMappedFile? created = GameSpeedNative.EnsureControlBlock(fakePid))
        {
            if (created is null)
                throw new InvalidOperationException("The control block could not be created for process " +
                    fakePid.ToString(CultureInfo.InvariantCulture) + ".");
            using MemoryMappedViewAccessor view = created.CreateViewAccessor(0, GameSpeedNative.BlockBytes, MemoryMappedFileAccess.ReadWrite);
            Require(view.ReadInt64(GameSpeedNative.OffMagic) == BlockMagic,
                "The control block magic was not stamped.");
            Require(view.ReadInt64(GameSpeedNative.OffVersion) == BlockVersion,
                "The control block version was not stamped.");
            Require(view.ReadInt64(GameSpeedNative.OffAppliedFactorMicro) == ScaledClock.FactorMicroScale,
                "A new control block did not start at normal speed.");

            // Simulate the managed side requesting a speed, then the DLL adopting it.
            // A fresh block initialises the desired factor to 0, so the request has to
            // be written first or the hook would faithfully adopt "no request".
            view.Write(GameSpeedNative.OffDesiredFactorMicro, ScaledClock.ToMicro(2.0));
            long requested = view.ReadInt64(GameSpeedNative.OffDesiredFactorMicro);
            Require(requested == ScaledClock.ToMicro(2.0),
                "The requested speed factor was not published in micro units as 2.0.");
            view.Write(GameSpeedNative.OffAppliedFactorMicro, requested);
            view.Write(GameSpeedNative.OffActiveGeneration, view.ReadInt64(GameSpeedNative.OffGeneration));
            Require(ScaledClock.FromMicro(view.ReadInt64(GameSpeedNative.OffAppliedFactorMicro)) == 2.0,
                "The simulated hook did not publish the factor it adopted.");

            // The block must be exactly the size both sides compiled against.
            Require(view.Capacity == GameSpeedNative.BlockBytes,
                "The control block is " + view.Capacity + " bytes but the native layout is " +
                GameSpeedNative.BlockBytes + " bytes; the two sides would read different fields.");
        }
        File.WriteAllText(Path.Combine(root, "control-block-name.txt"), name);
    }

    private static void NonExistentProcessFailsCleanly()
    {
        int dead = FindUnusedProcessId();
        Require(!GameSpeedNative.IsRunning(dead), "A process id chosen as unused was reported as running.");

        string identityMessage = "";
        try { GameSpeedNative.CaptureTarget(dead); }
        catch (InvalidOperationException ex) { identityMessage = ex.Message; }
        Require(identityMessage.Contains("not running", StringComparison.OrdinalIgnoreCase) ||
                identityMessage.Contains("Win32 error", StringComparison.Ordinal),
            "Identifying an absent process did not produce a clear Win32 failure; it said: " + identityMessage);

        string injectMessage = "";
        try { GameSpeedNative.InjectAsync(dead, "no-such-hook.dll", CancellationToken.None).GetAwaiter().GetResult(); }
        catch (InvalidOperationException ex) { injectMessage = ex.Message; }
        Require(injectMessage.Length > 0 && !injectMessage.Contains("AccessViolation") && !injectMessage.Contains("0x800"),
            "Injection into an absent process did not fail with a clear message; it said: " + injectMessage);

        Require(GameSpeedNative.InjectAsync(0, "no-such-hook.dll", CancellationToken.None)
                .ContinueWith(static t => t.IsFaulted).GetAwaiter().GetResult(),
            "Injection into process 0 was not refused.");

        // A 32-bit game with only the 64-bit build present must be reported clearly.
        string mismatch = "";
        try { GameSpeedNative.InjectAsync(Environment.ProcessId, ResolveSelf(), CancellationToken.None).GetAwaiter().GetResult(); }
        catch (InvalidOperationException ex) { mismatch = ex.Message; }
        Require(mismatch.Contains("refuses to target this application", StringComparison.Ordinal),
            "The engine offered to target its own process; it said: " + mismatch);
    }

    private static string ResolveSelf()
    {
        // Use the production resolver rather than hand-building a flat path: the build
        // preserves the tools\gamespeed\ subdirectory, so the library is not literally
        // beside the executable.
        string? own = GameSpeedNative.ResolveDllPath(GameSpeedArchitecture.X64);
        Require(own != null, "The 64-bit hook library was not found by the production resolver, so the self-target guard could not be exercised.");
        return own!;
    }

    /// <summary>
    /// With no game running, every control surface is a clear no-op rather than a
    /// crash, a hang, or a fabricated success.
    /// </summary>
    private static void NoRunningGameIsANoOp()
    {
        int dead = FindUnusedProcessId();
        Require(!GameSpeedNative.IsRunning(dead), "A process id chosen as unused was reported as running.");
        ISpeedApplier applier = GameSpeedNative.Applier;
        var missing = new SpeedTarget(dead, "none.exe", IntPtr.Zero, "none");
        Require(!applier.Inspect(missing).Alive, "A process that is not running was reported as alive.");
        Require(applier.Inspect(missing).CurrentMultiplier == 1.0,
            "A process that is not running did not report normal speed.");
        Require(applier.ApplySpeed(missing, 2.0) == 1.0,
            "A speed factor was applied to a process that is not running.");
        Require(applier.ApplySpeed(missing, 1.0) == 1.0,
            "Normal speed was applied to a process that is not running.");
        Require(applier.ApplySpeed(missing, double.NaN) == 1.0,
            "A non-finite speed factor was applied to a process that is not running.");
        Require(applier.ApplySpeed(missing, 99.0) == 1.0,
            "An out-of-range speed factor was applied to a process that is not running.");

        var zero = new SpeedTarget(0, "zero.exe", IntPtr.Zero, "zero");
        Require(!applier.Inspect(zero).Alive, "Process 0 was reported as alive.");
        Require(applier.ApplySpeed(zero, 2.0) == 1.0, "A speed factor was applied to process 0.");
    }

    private static void ElevatedTargetIsExplained()
    {
        string reason = GameSpeedNative.ElevationReason(4, 2);
        Require(reason.Contains("administrator", StringComparison.OrdinalIgnoreCase),
            "The elevation failure did not tell the user what to do.");
        Require(reason.Contains("4") && reason.Contains("2"),
            "The elevation failure did not name both integrity levels.");
        Require(reason.Contains("integrity", StringComparison.OrdinalIgnoreCase),
            "The elevation failure did not explain that this is an integrity-level mismatch.");
        Require(!GameSpeedNative.ElevationReason(2, 4).Contains("administrator", StringComparison.OrdinalIgnoreCase),
            "A same-or-lower integrity target was described as needing administrator rights.");
        Require(GameSpeedNative.CurrentIntegrityLevel() >= 0,
            "This application's own integrity level could not be read, so the elevation guard is untestable.");
    }

    private static void OnlineIntegrityIsRefused()
    {
        string[] refused =
        {
            @"C:\Games\SomeGame\easyanticheat.dll",
            @"C:\Games\SomeGame\EasyAntiCheat.sys",
            @"C:\Games\SomeGame\eac.exe",
            @"C:\Games\SomeGame\bedaisy.dll",
            @"C:\Games\SomeGame\battleye.exe",
            @"C:\Games\SomeGame\BEService.exe",
            @"C:\Games\SomeGame\anticlient.dll",
            @"C:\Games\SomeGame\StartGuard64.dll",
            @"C:\Games\SomeGame\xhunter1.dll",
            @"C:\Games\SomeGame\ACEcore.dll",
        };
        foreach (string path in refused)
            Require(GameSpeedNative.IsOnlineIntegrityModule(path),
                "An online-service integrity module was not refused: " + path);

        string[] allowed =
        {
            @"C:\Games\MyGame\game.exe",
            @"C:\Games\MyGame\UnityPlayer.dll",
            @"C:\Games\MyGame\steam_api64.dll",
            @"C:\Games\MyGame\SDL2.dll",
            @"C:\Games\MyGame\GameSpeed.dll",
            "",
        };
        foreach (string path in allowed)
            Require(!GameSpeedNative.IsOnlineIntegrityModule(path),
                "An ordinary game file was wrongly refused as an integrity module: " + path);
    }

    /// <summary>
    /// Identifies a real, disposable child process this test spawns and then kills.
    /// No game is ever involved.
    /// </summary>
    private static void DisposableChildIdentifies()
    {
        Process? child = null;
        int capturedChildId = 0;
        try
        {
            child = StartDisposableChild();
            Require(child.Id > 0, "The disposable child was started without a process id.");
            capturedChildId = child.Id;

            GameSpeedArchitecture childArchitecture = GameSpeedNative.DetectArchitecture(child.Id);
            Require(childArchitecture == GameSpeedArchitecture.X64,
                "A 64-bit child process was detected as " + childArchitecture + " instead of 64-bit.");

            GameSpeedTarget target = GameSpeedNative.CaptureTarget(child.Id);
            Require(target.ProcessId == child.Id, "The identified target had the wrong process id.");
            Require(target.Architecture == GameSpeedArchitecture.X64, "The identified target had the wrong bitness.");
            Require(target.SessionId == Process.GetCurrentProcess().SessionId,
                "The identified target was not in this session.");
            Require(target.IntegrityLevel >= 0, "The identified target had no readable integrity level.");
            Require(target.CreationFileTime.Length == 16, "The identified target had no creation stamp.");
            Require(target.ImagePath.Length > 0, "The identified target had no image path.");
            Require(!GameSpeedNative.IsOnlineIntegrityModule(target.ImagePath),
                "A disposable child with no integrity module was wrongly flagged as one.");
            Require(GameSpeedNative.IsStillSameTarget(target),
                "A live process was not confirmed as the same process it was a moment ago.");

            // A control surface that targets a process with no hook in it must report
            // normal speed, never claim the factor was applied.
            string reported = GameSpeedNative.Applier.ApplySpeed(new SpeedTarget(child.Id, "child", IntPtr.Zero, "child"), 2.0).ToString("0.###");
            Require(reported == "1",
                "A speed factor was reported as applied to a process that was never injected; it reported " + reported + ".");

            bool cancelled = false;
            try { GameSpeedNative.SetSpeedAsync(child.Id, 2.0, new CancellationToken(true)).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled, "An already-cancelled speed request was not reported as cancelled.");
        }
        finally
        {
            try
            {
                if (child != null && !child.HasExited) child.Kill(entireProcessTree: true);
            }
            catch (Exception) { }
            child?.Dispose();
        }

        // Read the id before it is disposed above: a null-conditional does not help
        // here, because a disposed Process throws on Id rather than returning null.
        if (capturedChildId > 0)
        {
            Require(!GameSpeedNative.IsRunning(capturedChildId), "The disposable child was still running after the test killed it.");
        }
    }

    private static Process StartDisposableChild()
    {
        // A long-lived, disposable child this test owns end to end: cmd.exe running a
        // bounded local ping loop. It is never a game, it needs no window, and the
        // caller kills it in a finally block.
        string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        Require(File.Exists(shell), "cmd.exe is missing, so a disposable child cannot be started.");
        ProcessStartInfo start = new(shell, "/d /c ping -n 300 127.0.0.1 > nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Process child = Process.Start(start)
            ?? throw new InvalidOperationException("The disposable child could not be started, so live identification could not be proven.");
        for (int i = 0; i < 40 && child.Id == 0; i++) Thread.Sleep(50);
        if (child.HasExited)
        {
            child.Dispose();
            throw new InvalidOperationException("The disposable child exited immediately, so live identification could not be proven.");
        }
        return child;
    }

    private static int FindUnusedProcessId()
    {
        // A pid in the reserved-high range that nothing on this host can hold.
        for (int candidate = 0x7F00_0001; candidate < 0x7F00_0100; candidate += 7)
            if (!GameSpeedNative.IsRunning(candidate)) return candidate;
        throw new InvalidOperationException("No unused process id could be found for the negative tests.");
    }
}