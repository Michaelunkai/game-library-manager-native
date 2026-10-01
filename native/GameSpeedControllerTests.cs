using System;
using System.Collections.Generic;
using System.Linq;

namespace GameLibrary.Native;

// Dependency-free unit coverage for the managed speed-control model. The
// manager registers this in SelfTests; it needs no LibraryStore, no WPF and no
// native layer. Any failed assertion throws InvalidOperationException.
internal static class GameSpeedControllerTests
{
    internal static void Run(string root)
    {
        _ = root;
        LadderShape();
        ClampGuards();
        StepRoundTrips();
        TieBreaksLower();
        HotkeyBoost();
        HotkeyReduce();
        HotkeyNormal();
        HotkeyBounds();
        NoDriftAcrossRepeats();
        NavigationLandsOnLadder();
        DescribeLabels();
        Persistence();
        Resolution();
        ApplyGate();
    }

    private static void LadderShape()
    {
        IReadOnlyList<double> steps = GameSpeedController.DefaultSteps();
        Require(steps.Count > 0, "The speed ladder must not be empty.");
        Require(steps.Count(step => Near(step, 1.0)) == 1, "The ladder must contain 1.0 exactly once.");
        Require(steps.All(step => !Near(step, 0.0)), "0.0x must never appear on the ladder.");
        Require(steps.All(step => double.IsFinite(step) && step > 0), "Every ladder step must be finite and positive.");
        Require(steps.Any(step => step > 1.0), "The ladder must include faster-than-normal steps.");
        Require(steps.Any(step => step < 1.0), "The ladder must include slower-than-normal steps.");
        for (int index = 1; index < steps.Count; index++)
            Require(steps[index] > steps[index - 1], "The ladder must be strictly ascending.");
        Require(Near(GameSpeedController.MinMultiplier, steps[0]), "MinMultiplier must be the first ladder step.");
        Require(Near(GameSpeedController.MaxMultiplier, steps[^1]), "MaxMultiplier must be the last ladder step.");
    }

    private static void ClampGuards()
    {
        Require(Near(GameSpeedController.Clamp(double.NaN), 1.0), "Clamp(NaN) must fall back to 1.0.");
        Require(Near(GameSpeedController.Clamp(double.PositiveInfinity), 1.0), "Clamp(+Infinity) must fall back to 1.0.");
        Require(Near(GameSpeedController.Clamp(double.NegativeInfinity), 1.0), "Clamp(-Infinity) must fall back to 1.0.");
        Require(Near(GameSpeedController.Clamp(0.0), GameSpeedController.MinMultiplier), "Clamp below the minimum must bound to the minimum.");
        Require(Near(GameSpeedController.Clamp(-100.0), GameSpeedController.MinMultiplier), "Clamp far below the minimum must bound to the minimum.");
        Require(Near(GameSpeedController.Clamp(1000.0), GameSpeedController.MaxMultiplier), "Clamp far above the maximum must bound to the maximum.");
        Require(Near(GameSpeedController.Clamp(1.3), 1.3), "Clamp must not snap a value already inside the range.");
    }

    private static void StepRoundTrips()
    {
        IReadOnlyList<double> steps = GameSpeedController.DefaultSteps();
        for (int index = 0; index < steps.Count; index++)
        {
            Require(Near(GameSpeedController.StepAt(index), steps[index]), "StepAt must return the ladder value at its index.");
            Require(GameSpeedController.IndexOfNearest(steps[index]) == index, "IndexOfNearest must round-trip every ladder value.");
        }
        Require(Near(GameSpeedController.StepAt(-5), steps[0]), "StepAt below range must clamp to the first step.");
        Require(Near(GameSpeedController.StepAt(999), steps[^1]), "StepAt above range must clamp to the last step.");
    }

    private static void TieBreaksLower()
    {
        IReadOnlyList<double> steps = GameSpeedController.DefaultSteps();
        int two = GameSpeedController.IndexOfNearest(2.5);
        Require(Near(steps[two], 2.0), "A tie between 2.0 and 3.0 must break toward the lower step.");
        int one = GameSpeedController.IndexOfNearest(1.25);
        Require(Near(steps[one], 1.0), "A tie between 1.0 and 1.5 must break toward the lower step.");
        int threeQuarters = GameSpeedController.IndexOfNearest(0.875);
        Require(Near(steps[threeQuarters], 0.75), "A tie between 0.75 and 1.0 must break toward the lower step.");
    }

    private static void HotkeyBoost()
    {
        double atNormal = GameSpeedController.ApplyHotkey(1.0, SpeedHotkey.Boost);
        Require(Near(atNormal, 1.5), "F1 at 1.0 must give exactly 1.5x, not 2.0x.");
        Require(!Near(atNormal, 2.0), "F1 at 1.0 must not double the speed.");

        double second = GameSpeedController.ApplyHotkey(atNormal, SpeedHotkey.Boost);
        Require(Near(second, 2.0), "A second F1 from 1.0 must give exactly 2.0x.");
        double third = GameSpeedController.ApplyHotkey(second, SpeedHotkey.Boost);
        Require(Near(third, 3.0), "A third F1 from 1.0 must give exactly 3.0x.");
    }

    private static void HotkeyReduce()
    {
        double atNormal = GameSpeedController.ApplyHotkey(1.0, SpeedHotkey.Reduce);
        Require(Near(atNormal, 0.5), "F2 at 1.0 must give exactly 0.5x.");
        double second = GameSpeedController.ApplyHotkey(atNormal, SpeedHotkey.Reduce);
        Require(Near(second, 0.1) || Near(second, 0.25), "A second F2 from 0.5 must land on the next lower ladder step (0.25 or 0.1).");
        Require(second >= GameSpeedController.MinMultiplier, "F2 must never go below the ladder minimum.");
    }

    private static void HotkeyNormal()
    {
        foreach (double start in new[] { 16.0, 0.1, 0.5, 2.0, 1.5, 1.0, double.NaN, double.PositiveInfinity })
        {
            Require(Near(GameSpeedController.ApplyHotkey(start, SpeedHotkey.Normal), 1.0),
                "F3 must always give exactly 1.0x, including from " + start + ".");
        }
    }

    private static void HotkeyBounds()
    {
        Require(Near(GameSpeedController.ApplyHotkey(GameSpeedController.MaxMultiplier, SpeedHotkey.Boost),
            GameSpeedController.MaxMultiplier), "F1 at the maximum must not exceed the maximum.");
        Require(Near(GameSpeedController.ApplyHotkey(GameSpeedController.MinMultiplier, SpeedHotkey.Reduce),
            GameSpeedController.MinMultiplier), "F2 at the minimum must not go below the minimum.");
        double fromNaN = GameSpeedController.ApplyHotkey(double.NaN, SpeedHotkey.Boost);
        Require(double.IsFinite(fromNaN) && fromNaN >= GameSpeedController.MinMultiplier && fromNaN <= GameSpeedController.MaxMultiplier,
            "A hotkey applied to non-finite input must produce a finite in-range value.");
    }

    private static void NoDriftAcrossRepeats()
    {
        IReadOnlyList<double> steps = GameSpeedController.DefaultSteps();

        double faster = 1.0;
        var seen = new List<double>();
        for (int press = 0; press < 20; press++)
        {
            faster = GameSpeedController.ApplyHotkey(faster, SpeedHotkey.Boost);
            Require(steps.Any(step => Near(step, faster)), "F1 must always land on a ladder value (no drift).");
            if (seen.Count > 0) Require(faster >= seen[^1] - 1e-9, "F1 must never move the bar backwards.");
            seen.Add(faster);
        }
        Require(Near(seen[0], 1.5) && Near(seen[1], 2.0) && Near(seen[2], 3.0),
            "Repeated F1 from 1.0 must produce 1.5, 2.0, 3.0 with no accumulated rounding.");
        Require(Near(seen[^1], GameSpeedController.MaxMultiplier), "Repeated F1 must eventually reach the ladder maximum and hold there.");

        double slower = 1.0;
        for (int press = 0; press < 20; press++)
        {
            slower = GameSpeedController.ApplyHotkey(slower, SpeedHotkey.Reduce);
            Require(slower >= GameSpeedController.MinMultiplier, "F2 must never go below the ladder minimum.");
            Require(slower > 0, "F2 must never produce a zero or negative multiplier.");
            Require(steps.Any(step => Near(step, slower)), "F2 must always land on a ladder value (no drift).");
        }
        Require(Near(slower, GameSpeedController.MinMultiplier), "Repeated F2 must settle at the ladder minimum.");
    }

    private static void NavigationLandsOnLadder()
    {
        IReadOnlyList<double> steps = GameSpeedController.DefaultSteps();
        for (int index = 0; index < steps.Count; index++)
        {
            double value = steps[index];
            double faster = GameSpeedController.NextFaster(value);
            double slower = GameSpeedController.NextSlower(value);
            Require(steps.Any(step => Near(step, faster)), "NextFaster must land on a ladder value.");
            Require(steps.Any(step => Near(step, slower)), "NextSlower must land on a ladder value.");
            if (index < steps.Count - 1) Require(Near(faster, steps[index + 1]), "NextFaster must move exactly one step up.");
            else Require(Near(faster, steps[^1]), "NextFaster at the maximum must hold the maximum.");
            if (index > 0) Require(Near(slower, steps[index - 1]), "NextSlower must move exactly one step down.");
            else Require(Near(slower, steps[0]), "NextSlower at the minimum must hold the minimum.");
        }
    }

    private static void DescribeLabels()
    {
        Require(GameSpeedController.Describe(1.5) == "1.5x", "Describe(1.5) must be 1.5x.");
        Require(GameSpeedController.Describe(1.0) == "normal (1.0x)", "Describe(1.0) must be the normal label.");
        Require(GameSpeedController.Describe(2.0) == "2.0x", "Describe(2.0) must be 2.0x.");
        Require(GameSpeedController.Describe(0.25) == "0.25x", "Describe(0.25) must be 0.25x.");
        Require(GameSpeedController.Describe(16.0) == "16.0x", "Describe(16.0) must be 16.0x.");
        Require(GameSpeedController.Describe(double.NaN) == "normal (1.0x)", "Describe(non-finite) must fall back to the normal label.");
    }

    private static void Persistence()
    {
        var stored = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["game-a"] = 1.5,
            ["game-b"] = 0.5,
            ["broken"] = double.NaN,
            ["out-of-range"] = 999.0,
            [""] = 3.0
        };

        IReadOnlyDictionary<string, double> loaded = GameSpeedController.LoadPerGame(stored);
        Require(Near(loaded["game-a"], 1.5), "LoadPerGame must preserve a valid remembered speed.");
        Require(Near(loaded["broken"], 1.0), "LoadPerGame must reject a non-finite remembered speed.");
        Require(Near(loaded["out-of-range"], GameSpeedController.MaxMultiplier), "LoadPerGame must bound an out-of-range speed.");
        Require(!loaded.ContainsKey(""), "LoadPerGame must drop an empty game id.");

        Require(Near(GameSpeedController.ForGame("game-a", stored), 1.5), "ForGame must return a known game's remembered speed.");
        Require(Near(GameSpeedController.ForGame("game-b", stored), 0.5), "A second game must keep an independent remembered speed.");
        Require(Near(GameSpeedController.ForGame("unknown", stored), 1.0), "ForGame must fall back to 1.0 for an unknown id.");
        Require(Near(GameSpeedController.ForGame("unknown", stored, 2.0), 2.0), "ForGame must honor a caller-supplied fallback.");
        Require(Near(GameSpeedController.ForGame("game-a", null), 1.0), "ForGame must fall back safely when no store exists.");
        Require(Near(GameSpeedController.ForGame("", stored), 1.0), "ForGame must fall back safely for an empty id.");
    }

    private static void Resolution()
    {
        var empty = new FakeProbe();
        Require(GameSpeedController.ResolveTarget(empty, null) is null, "No running process must resolve to null.");
        Require(GameSpeedController.ResolveTargetDetailed(empty, null).Reason == TargetRefusalReason.NotRunning,
            "No running process must report NotRunning.");

        var single = new FakeProbe(new GameProcessCandidate(100, "Game", (IntPtr)0x10, "game-a", true));
        SpeedTarget? target = GameSpeedController.ResolveTarget(single, null);
        Require(target is not null, "A single library game must resolve.");
        Require(target!.ProcessId == 100 && target.GameId == "game-a" && target.WindowHandle == (IntPtr)0x10,
            "The resolved target must pin the process id, game id and window handle.");

        var ambiguous = new FakeProbe(
            new GameProcessCandidate(1, "One", (IntPtr)0x1, "game-a", true),
            new GameProcessCandidate(2, "Two", (IntPtr)0x2, "game-b", true));
        Require(GameSpeedController.ResolveTarget(ambiguous, null) is null, "Multiple unrelated candidates must not be guessed.");
        Require(GameSpeedController.ResolveTargetDetailed(ambiguous, null).Reason == TargetRefusalReason.Ambiguous,
            "Multiple unrelated candidates must report Ambiguous.");

        var foreground = new FakeProbe(
            new GameProcessCandidate(1, "One", (IntPtr)0x1, "game-a", true),
            new GameProcessCandidate(2, "Two", (IntPtr)0x2, "game-b", true, IsForeground: true));
        SpeedTarget? fromForeground = GameSpeedController.ResolveTarget(foreground, null);
        Require(fromForeground is not null && fromForeground.ProcessId == 2,
            "A single unambiguous foreground game window must be selected.");

        var many = new FakeProbe(
            new GameProcessCandidate(1, "One", (IntPtr)0x1, "game-a", true),
            new GameProcessCandidate(2, "Two", (IntPtr)0x2, "game-b", true));
        SpeedTarget? preferred = GameSpeedController.ResolveTarget(many, "game-b");
        Require(preferred is not null && preferred.ProcessId == 2 && preferred.GameId == "game-b",
            "The preferred tracked game must win over an ambiguous set.");
        Require(GameSpeedController.ResolveTargetDetailed(many, "missing").Reason == TargetRefusalReason.Ambiguous,
            "An unmatched preferred id must still be ambiguous when several candidates exist.");

        var notLibrary = new FakeProbe(new GameProcessCandidate(7, "Tool", (IntPtr)0x7, "tool", false));
        Require(GameSpeedController.ResolveTarget(notLibrary, null) is null, "A non-library process must not resolve.");
        Require(GameSpeedController.ResolveTargetDetailed(notLibrary, null).Reason == TargetRefusalReason.NotALibraryGame,
            "A non-library process must report NotALibraryGame.");
        Require(GameSpeedController.ResolveTargetDetailed(notLibrary, "tool").Reason == TargetRefusalReason.NotALibraryGame,
            "A preferred id that is not a library game must report NotALibraryGame.");
    }

    private static void ApplyGate()
    {
        Require(!GameSpeedController.ShouldApply(null), "ShouldApply(null) must be false.");
        var applier = new FakeApplier();

        SpeedApplyResult noGame = GameSpeedController.Apply(null, 1.5, applier);
        Require(!noGame.Applied && noGame.Refusal == TargetRefusalReason.NotRunning,
            "Apply on a null target must be a no-op refusal naming NotRunning.");
        Require(applier.ApplyCount == 0, "A refused apply must never reach the applier.");
        Require(Near(noGame.Multiplier, 1.5), "A refusal must still report the clamped requested multiplier.");

        SpeedTarget target = new(200, "Game", (IntPtr)0x20, "game-a");
        Require(GameSpeedController.ShouldApply(target), "ShouldApply must be true for a resolved target.");

        applier.Alive = false;
        SpeedApplyResult exited = GameSpeedController.Apply(target, 1.5, applier);
        Require(!exited.Applied && exited.Refusal == TargetRefusalReason.ProcessExited,
            "A target that exits before apply must be refused with ProcessExited.");
        Require(applier.ApplyCount == 0, "An exited target must never reach the applier.");

        applier.Alive = true;
        applier.Library = false;
        SpeedApplyResult foreign = GameSpeedController.Apply(target, 1.5, applier);
        Require(!foreign.Applied && foreign.Refusal == TargetRefusalReason.NotALibraryGame,
            "A non-library target must be refused with NotALibraryGame.");
        Require(applier.ApplyCount == 0, "A non-library target must never reach the applier.");

        applier.Library = true;
        applier.Current = 1.5;
        SpeedApplyResult unchanged = GameSpeedController.Apply(target, 1.5, applier);
        Require(!unchanged.Applied && unchanged.Refusal == TargetRefusalReason.Unchanged,
            "A request that changes nothing must be refused with Unchanged.");

        applier.Current = 1.0;
        SpeedApplyResult applied = GameSpeedController.Apply(target, 1.5, applier);
        Require(applied.Applied && applied.Refusal is null, "A verified library target must apply.");
        Require(Near(applied.Multiplier, 1.5), "The applied result must report the new multiplier.");
        Require(applier.ApplyCount == 1 && Near(applier.AppliedMultiplier!.Value, 1.5), "The applier must receive the requested multiplier.");
        Require(applied.Message.Contains("1.5x", StringComparison.Ordinal), "The applied result must carry a user-facing message.");
        Require(applier.LastTarget == target, "The applier must receive the exact pinned target.");

        applier.Current = 2.0;
        SpeedApplyResult nonFinite = GameSpeedController.Apply(target, double.NaN, applier);
        Require(nonFinite.Applied && Near(nonFinite.Multiplier, 1.0), "Apply must reject a non-finite multiplier back to 1.0.");
    }

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 1e-9;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeProbe : IGameProcessProbe
    {
        private readonly IReadOnlyList<GameProcessCandidate> candidates;
        internal FakeProbe(params GameProcessCandidate[] candidates) { this.candidates = candidates; }
        public IReadOnlyList<GameProcessCandidate> GetRunningGames() => candidates;
    }

    private sealed class FakeApplier : ISpeedApplier
    {
        internal bool Alive = true;
        internal bool Library = true;
        internal double Current = 1.0;
        internal int ApplyCount;
        internal double? AppliedMultiplier;
        internal SpeedTarget? LastTarget;

        public SpeedTargetState Inspect(SpeedTarget target)
        {
            LastTarget = target;
            return new SpeedTargetState(Alive, Library, Current);
        }

        public double ApplySpeed(SpeedTarget target, double multiplier)
        {
            LastTarget = target;
            ApplyCount++;
            AppliedMultiplier = multiplier;
            Current = multiplier;
            return multiplier;
        }
    }
}
