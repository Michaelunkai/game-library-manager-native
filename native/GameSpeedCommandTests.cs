using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

/// <summary>
/// Records exactly what the speed command asks the game to do.
/// </summary>
internal sealed class FakeSpeedRuntime : ISpeedRuntime
{
    internal readonly List<string> Calls = new();
    internal bool Running = true;
    internal bool Injected;
    internal double Current = 1.0;
    internal Exception? InjectFailure;
    /// <summary>Set when a factor request itself must fail, to exercise the failure path.</summary>
    internal Exception? SetFailure = null;
    /// <summary>When true, Set confirms a different factor than requested, as a real hook may clamp.</summary>
    internal double? ConfirmOverride;

    public bool IsRunning(int processId) { Calls.Add("IsRunning:" + processId); return Running; }
    public Task EnsureInjectedAsync(int processId, CancellationToken cancellation)
    {
        Calls.Add("Inject:" + processId);
        if (InjectFailure is not null) return Task.FromException(InjectFailure);
        Injected = true;
        return Task.CompletedTask;
    }
    public Task<double> SetAsync(int processId, double factor, CancellationToken cancellation)
    {
        Calls.Add("Set:" + factor.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
        if (SetFailure is not null) return Task.FromException<double>(SetFailure);
        Current = ConfirmOverride ?? factor;
        return Task.FromResult(Current);
    }
    public Task<double> GetAsync(int processId, CancellationToken cancellation) { Calls.Add("Get"); return Task.FromResult(Current); }
    public bool IsInjected(int processId) { Calls.Add("IsInjected:" + processId); return Injected; }
}

internal static class GameSpeedCommandTests
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void Run(string root)
    {
        const int pid = 4242;
        var ct = CancellationToken.None;

        // The regression that made the whole feature do nothing: the hotkey path
        // stored the number in state and updated a label, and never touched the
        // process at all. These assertions are the reason it cannot happen again.
        {
            var runtime = new FakeSpeedRuntime();
            var result = GameSpeedCommand.ApplyHotkeyAsync(pid, 1.0, SpeedHotkey.Boost, runtime, ct).GetAwaiter().GetResult();
            Require(result.Succeeded, "F1 from 1.0x did not succeed: " + result.Message);
            Require(runtime.Calls.Contains("Inject:" + pid), "F1 never injected the hook: " + string.Join(", ", runtime.Calls));
            Require(runtime.Calls.Contains("Set:1.5"), "F1 from 1.0x did not send 1.5x: " + string.Join(", ", runtime.Calls));
            Require(result.Factor == 1.5, "F1 from 1.0x reported " + result.Factor + " instead of 1.5.");
        }

        // Injection must happen before the set: the real SetSpeedAsync waits two
        // seconds for the hook to confirm, so setting first can only ever time out.
        {
            var runtime = new FakeSpeedRuntime();
            GameSpeedCommand.ApplyAsync(pid, 2.0, runtime, ct).GetAwaiter().GetResult();
            int injectAt = runtime.Calls.FindIndex(call => call.StartsWith("Inject:", StringComparison.Ordinal));
            int setAt = runtime.Calls.FindIndex(call => call.StartsWith("Set:", StringComparison.Ordinal));
            Require(injectAt >= 0 && setAt >= 0, "The hook was not injected and set: " + string.Join(", ", runtime.Calls));
            Require(injectAt < setAt, "The factor was set before the hook was injected: " + string.Join(", ", runtime.Calls));
        }

        // Re-injection must not happen on every press once the hook is present.
        {
            var runtime = new FakeSpeedRuntime { Injected = true };
            GameSpeedCommand.ApplyAsync(pid, 1.5, runtime, ct).GetAwaiter().GetResult();
            Require(!runtime.Calls.Exists(call => call.StartsWith("Inject:", StringComparison.Ordinal)),
                "The hook was injected again although it was already present: " + string.Join(", ", runtime.Calls));
            Require(runtime.Calls.Contains("Set:1.5"), "An already-injected game did not receive the factor: " + string.Join(", ", runtime.Calls));
        }

        // F1 / F2 / F3 semantics exactly as specified.
        {
            var runtime = new FakeSpeedRuntime { Injected = true };
            var boost = GameSpeedCommand.ApplyHotkeyAsync(pid, 1.0, SpeedHotkey.Boost, runtime, ct).GetAwaiter().GetResult();
            Require(boost.Factor == 1.5, "F1 at 1.0x gave " + boost.Factor + " instead of exactly 1.5.");
            var reduce = GameSpeedCommand.ApplyHotkeyAsync(pid, 1.0, SpeedHotkey.Reduce, runtime, ct).GetAwaiter().GetResult();
            Require(reduce.Factor == 0.5, "F2 at 1.0x gave " + reduce.Factor + " instead of exactly 0.5.");
            var normal = GameSpeedCommand.ApplyNormalAsync(pid, runtime, ct).GetAwaiter().GetResult();
            Require(normal.Factor == 1.0, "F3 gave " + normal.Factor + " instead of exactly 1.0.");
        }

        // Repeated F1 must be EXACTLY +0.5 every press, with no snapping to the ladder
        // and no accumulated drift, and every press must really reach the process.
        // The user asked for 1.0 -> 1.5 -> 2.0 -> 2.5 -> 3.0 ... however many presses.
        {
            var runtime = new FakeSpeedRuntime { Injected = true };
            double current = 1.0;
            for (int press = 0; press < 20; press++)
            {
                double before = current;
                var step = GameSpeedCommand.ApplyHotkeyAsync(pid, current, SpeedHotkey.Boost, runtime, ct).GetAwaiter().GetResult();
                Require(step.Succeeded, "F1 press " + press + " failed: " + step.Message);
                Require(Math.Abs(step.Factor - (before + 0.5)) < 1e-9,
                    "F1 press " + press + " gave " + step.Factor + " instead of " + (before + 0.5) + ".");
                current = step.Factor;
            }
            Require(Math.Abs(current - 11.0) < 1e-9,
                "Twenty F1 presses from 1.0x landed on " + current + " instead of exactly 11.0x.");
            int setCount = runtime.Calls.FindAll(call => call.StartsWith("Set:", StringComparison.Ordinal)).Count;
            Require(setCount == 20, "Twenty F1 presses produced " + setCount + " set calls, so some presses never reached the game.");
        }

        // Repeated F2 must be EXACTLY -0.5 every press, and must settle on the floor
        // rather than reaching zero, which would freeze a game solid.
        {
            var runtime = new FakeSpeedRuntime { Injected = true };
            double current = 1.0;
            for (int press = 0; press < 10; press++)
            {
                double before = current;
                var step = GameSpeedCommand.ApplyHotkeyAsync(pid, current, SpeedHotkey.Reduce, runtime, ct).GetAwaiter().GetResult();
                Require(step.Succeeded, "F2 press " + press + " failed: " + step.Message);
                Require(Math.Abs(step.Factor - Math.Max(GameSpeedController.HotkeyMinMultiplier, before - 0.5)) < 1e-9,
                    "F2 press " + press + " gave " + step.Factor + " instead of the expected half step down.");
                current = step.Factor;
                Require(current > 0, "F2 drove the speed to " + current + ".");
            }
            Require(Math.Abs(current - GameSpeedController.HotkeyMinMultiplier) < 1e-9,
                "Ten F2 presses settled at " + current + " instead of the floor " + GameSpeedController.HotkeyMinMultiplier + ".");
        }

        // The hotkey range must not be the ladder range: the ladder tops out at 16x,
        // which would stop F1 long before the hook's own ceiling.
        Require(GameSpeedController.HotkeyMaxMultiplier > 16.0,
            "The hotkey ceiling is not above the ladder's top step, so F1 would stop early.");
        Require(GameSpeedController.HotkeyMinMultiplier > 0.0 && GameSpeedController.HotkeyMinMultiplier < 0.1,
            "The hotkey floor must be above zero so a game is never frozen solid.");
        // F1 must keep climbing past every ladder step.
        {
            double current = 1.0;
            for (int press = 0; press < 32; press++) current = GameSpeedController.ApplyHotkey(current, SpeedHotkey.Boost);
            Require(current > 16.0, "F1 stopped at " + current + ", which is the top of the old ladder.");
        }
        // F3 must return to exactly 1.0 from anywhere.
        {
            Require(Math.Abs(GameSpeedController.ApplyHotkey(16.5, SpeedHotkey.Normal) - 1.0) < 1e-9, "F3 did not reset exactly to 1.0.");
            Require(Math.Abs(GameSpeedController.ApplyHotkey(GameSpeedController.HotkeyMinMultiplier, SpeedHotkey.Normal) - 1.0) < 1e-9,
                "F3 did not reset exactly to 1.0 from the floor.");
        }

        // A game that is not running must be a clean no-op that changes nothing.
        {
            var runtime = new FakeSpeedRuntime { Running = false, Injected = true };
            var result = GameSpeedCommand.ApplyHotkeyAsync(pid, 1.0, SpeedHotkey.Boost, runtime, ct).GetAwaiter().GetResult();
            Require(result.Outcome == SpeedCommandOutcome.NotRunning, "A dead game was not reported as not running.");
            Require(!result.Succeeded, "A speed was reported as applied to a game that is not running.");
            Require(!runtime.Calls.Exists(call => call.StartsWith("Set:", StringComparison.Ordinal)),
                "A factor was sent to a game that is not running: " + string.Join(", ", runtime.Calls));
        }

        // A refused injection must never be reported as applied, so the UI cannot
        // claim a speed the game is not running at.
        {
            var runtime = new FakeSpeedRuntime { InjectFailure = new UnauthorizedAccessException("higher integrity") };
            var result = GameSpeedCommand.ApplyAsync(pid, 2.0, runtime, ct).GetAwaiter().GetResult();
            Require(result.Outcome == SpeedCommandOutcome.Refused, "An elevation failure was not reported as a refusal.");
            Require(result.Message.Contains("permission", StringComparison.OrdinalIgnoreCase),
                "The elevation failure did not explain the permission problem: " + result.Message);
            Require(result.Factor == GameSpeedNative.NormalFactor,
                "A refused change reported factor " + result.Factor + " instead of normal.");
        }

        // A hook that never answers must fail loudly rather than silently succeed.
        {
            var runtime = new FakeSpeedRuntime { InjectFailure = new TimeoutException("the speed hook is not responding") };
            var result = GameSpeedCommand.ApplyAsync(pid, 2.0, runtime, ct).GetAwaiter().GetResult();
            Require(result.Outcome == SpeedCommandOutcome.Failed, "A timeout was not reported as a failure.");
            Require(!result.Succeeded, "A timeout was reported as a successfully applied speed.");
        }

        // An out-of-range factor must be rejected before anything is injected.
        {
            var runtime = new FakeSpeedRuntime { Injected = true };
            var result = GameSpeedCommand.ApplyAsync(pid, double.NaN, runtime, ct).GetAwaiter().GetResult();
            Require(result.Outcome == SpeedCommandOutcome.Refused, "A non-finite factor was not refused.");
            Require(!runtime.Calls.Exists(call => call.StartsWith("Set:", StringComparison.Ordinal)),
                "A non-finite factor was sent to the game.");
            var outOfRange = GameSpeedCommand.ApplyAsync(pid, 10000.0, runtime, ct).GetAwaiter().GetResult();
            Require(outOfRange.Outcome == SpeedCommandOutcome.Refused, "An out-of-range factor was not refused.");
        }

        // A hook that confirms a different factor than requested must report what
        // the game is actually running at, so stored state stays truthful.
        {
            var runtime = new FakeSpeedRuntime { Injected = true, ConfirmOverride = 1.25 };
            var result = GameSpeedCommand.ApplyAsync(pid, 2.0, runtime, ct).GetAwaiter().GetResult();
            Require(result.Succeeded, "A confirming hook was reported as a failure.");
            Require(Math.Abs(result.Factor - 1.25) < 1e-9,
                "The confirmed factor was not reported back: " + result.Factor + " instead of 1.25.");
        }

        // Messages must be non-empty so the status line never goes blank.
        {
            var runtime = new FakeSpeedRuntime { Injected = true };
            foreach (var key in new[] { SpeedHotkey.Boost, SpeedHotkey.Reduce, SpeedHotkey.Normal })
            {
                var result = GameSpeedCommand.ApplyHotkeyAsync(pid, 1.0, key, runtime, ct).GetAwaiter().GetResult();
                Require(!string.IsNullOrWhiteSpace(result.Message), key + " produced an empty status message.");
            }
        }
    }
}
