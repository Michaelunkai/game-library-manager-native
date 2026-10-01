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

        // Repeated F1 must walk the ladder and land on exact values, with no drift,
        // and every press must really reach the process.
        {
            var runtime = new FakeSpeedRuntime { Injected = true };
            double current = 1.0;
            var seen = new List<double>();
            for (int press = 0; press < 8; press++)
            {
                var step = GameSpeedCommand.ApplyHotkeyAsync(pid, current, SpeedHotkey.Boost, runtime, ct).GetAwaiter().GetResult();
                Require(step.Succeeded, "F1 press " + press + " failed: " + step.Message);
                current = step.Factor;
                seen.Add(current);
            }
            double[] expected = { 1.5, 2.0, 3.0, 4.0, 6.0, 8.0, 10.0, 16.0 };
            for (int i = 0; i < expected.Length; i++)
                Require(Math.Abs(seen[i] - expected[i]) < 1e-9,
                    "F1 ladder press " + i + " produced " + seen[i] + " instead of " + expected[i] + ".");
            int setCount = runtime.Calls.FindAll(call => call.StartsWith("Set:", StringComparison.Ordinal)).Count;
            Require(setCount == 8, "Eight F1 presses produced " + setCount + " set calls, so some presses never reached the game.");
        }

        // Repeated F2 must never go below the floor or negative.
        {
            var runtime = new FakeSpeedRuntime { Injected = true };
            double current = 1.0;
            for (int press = 0; press < 12; press++)
            {
                var step = GameSpeedCommand.ApplyHotkeyAsync(pid, current, SpeedHotkey.Reduce, runtime, ct).GetAwaiter().GetResult();
                Require(step.Succeeded, "F2 press " + press + " failed: " + step.Message);
                current = step.Factor;
                Require(current > 0, "F2 drove the speed to " + current + ".");
            }
            Require(current >= GameSpeedController.MinMultiplier,
                "F2 settled at " + current + ", below the supported floor.");
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
