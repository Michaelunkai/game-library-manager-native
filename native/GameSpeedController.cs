using System;
using System.Collections.Generic;
using System.Globalization;

namespace GameLibrary.Native;

// Managed half of the OpenSpeedy-style per-game speed control.
//
// Boundary: this targets ONLY processes the user launched through this library
// on their own PC, for offline / single-player use. It refuses any process it
// cannot positively identify as one of this library's own games and it never
// attempts to defeat, hide from, or bypass anti-cheat or any online-service
// integrity check. Every refusal is an ordinary, non-throwing outcome.
//
// The type is deliberately UI-free and WPF-free so the whole model, hotkey
// arbitration and process targeting can be unit-tested without a window.

internal enum SpeedHotkey
{
    Boost,
    Reduce,
    Normal
}

internal enum TargetRefusalReason
{
    NotRunning,
    Ambiguous,
    NotALibraryGame,
    ProcessExited,
    Unchanged
}

// One position on the speed bar: the multiplier plus its ladder index.
internal sealed record SpeedSetting(double Multiplier, int Step);

// The pinned process identity a speed change is allowed to act on. The window
// handle is part of the identity so a recycled PID can never be mistaken for
// the game that was resolved a moment earlier.
internal sealed record SpeedTarget(int ProcessId, string ProcessName, IntPtr WindowHandle, string GameId);

// A running process the app is aware of. IsLibraryGame is true only for a game
// this library launched or tracks; IsForeground marks the single window that
// currently owns the foreground.
internal sealed record GameProcessCandidate(
    int ProcessId,
    string ProcessName,
    IntPtr WindowHandle,
    string GameId,
    bool IsLibraryGame,
    bool IsForeground = false);

// Result of re-verifying a pinned target immediately before acting.
internal readonly record struct SpeedTargetState(bool Alive, bool IsLibraryGame, double CurrentMultiplier);

// Resolution outcome. Reason is only meaningful when Target is null.
internal readonly record struct SpeedTargetResolution(SpeedTarget? Target, TargetRefusalReason Reason);

internal sealed record SpeedApplyResult(double Multiplier, bool Applied, string Message, TargetRefusalReason? Refusal)
{
    internal bool Refused => !Applied;
}

// The seam over the app's own running-game tracking. The manager implements it
// over activePlays; it must only return processes the library itself launched.
internal interface IGameProcessProbe
{
    IReadOnlyList<GameProcessCandidate> GetRunningGames();
}

// The seam slot 8's native layer implements. Inspect must re-verify the pinned
// ProcessId AND WindowHandle still identify the same live process (no TOCTOU),
// and report whether it is one of this library's games. ApplySpeed must refuse
// to touch anything it cannot verify as such.
internal interface ISpeedApplier
{
    SpeedTargetState Inspect(SpeedTarget target);
    double ApplySpeed(SpeedTarget target, double multiplier);
}

internal static class GameSpeedController
{
    private const double NormalMultiplier = 1.0;
    private const double Epsilon = 1e-9;

    // Fixed, ordered ladder. 1.0x is present exactly once; 0.0x is never
    // present. Both slower-than-normal and faster-than-normal positions exist.
    private static readonly double[] Steps =
    {
        0.1, 0.25, 0.5, 0.75, 1.0, 1.5, 2.0, 3.0, 4.0, 6.0, 8.0, 10.0, 16.0
    };

    private static readonly IReadOnlyList<double> StepList = Array.AsReadOnly(Steps);

    internal static IReadOnlyList<double> DefaultSteps() => StepList;

    internal static double MinMultiplier => Steps[0];

    internal static double MaxMultiplier => Steps[^1];

    // Clamp to the ladder's [min, max]. Non-finite input (NaN / +/-Infinity) is
    // rejected back to normal rather than silently becoming a number.
    internal static double Clamp(double multiplier)
    {
        if (!double.IsFinite(multiplier)) return NormalMultiplier;
        if (multiplier < Steps[0]) return Steps[0];
        if (multiplier > Steps[^1]) return Steps[^1];
        return multiplier;
    }

    // Nearest ladder index. Ties break toward the LOWER step deterministically
    // (strictly-less comparison while scanning ascending).
    internal static int IndexOfNearest(double multiplier)
    {
        double value = Clamp(multiplier);
        int best = 0;
        double bestDistance = Math.Abs(value - Steps[0]);
        for (int index = 1; index < Steps.Length; index++)
        {
            double distance = Math.Abs(value - Steps[index]);
            if (distance < bestDistance)
            {
                best = index;
                bestDistance = distance;
            }
        }
        return best;
    }

    internal static double StepAt(int index)
    {
        if (index < 0) return Steps[0];
        if (index >= Steps.Length) return Steps[^1];
        return Steps[index];
    }

    // Bar navigation. Always lands exactly on a ladder value, so repeated
    // presses cannot accumulate rounding drift.
    internal static double NextFaster(double current)
    {
        int index = IndexOfNearest(current);
        return index >= Steps.Length - 1 ? Steps[^1] : Steps[index + 1];
    }

    internal static double NextSlower(double current)
    {
        int index = IndexOfNearest(current);
        return index <= 0 ? Steps[0] : Steps[index - 1];
    }

    // Exact user semantics:
    //   Boost (F1)  = current + 0.5, then snapped to the nearest ladder step.
    //   Reduce (F2) = current - 0.5, then snapped to the nearest ladder step.
    //   Normal (F3) = exactly 1.0.
    // When the +0.5/-0.5 shift lands closer to the current step than to the
    // next one (the ladder is wider than 1.0 above 4.0x), snapping alone would
    // stall the bar. In that case the bar advances to the adjacent ladder step
    // so a press always makes progress and never drifts off the ladder.
    internal static double ApplyHotkey(double current, SpeedHotkey key)
    {
        return key switch
        {
            SpeedHotkey.Normal => NormalMultiplier,
            SpeedHotkey.Boost => Shift(current, 0.5),
            SpeedHotkey.Reduce => Shift(current, -0.5),
            _ => Clamp(current)
        };
    }

    private static double Shift(double current, double delta)
    {
        double value = Clamp(current);
        double snapped = StepAt(IndexOfNearest(value + delta));
        if (delta > 0)
            return snapped > value + Epsilon ? snapped : NextFaster(value);
        if (delta < 0)
            return snapped < value - Epsilon ? snapped : NextSlower(value);
        return value;
    }

    internal static bool ShouldApply(SpeedTarget? target) => target is not null;

    internal static SpeedTarget? ResolveTarget(IGameProcessProbe probe, string? preferredGameId)
        => ResolveTargetDetailed(probe, preferredGameId).Target;

    // Fail-closed resolution. Nothing running -> NotRunning. Several unrelated
    // candidates with no preferred id and no single foreground window ->
    // Ambiguous. A candidate the library does not own -> NotALibraryGame.
    internal static SpeedTargetResolution ResolveTargetDetailed(IGameProcessProbe probe, string? preferredGameId)
    {
        ArgumentNullException.ThrowIfNull(probe);
        IReadOnlyList<GameProcessCandidate> candidates = probe.GetRunningGames() ?? Array.Empty<GameProcessCandidate>();
        if (candidates.Count == 0) return new SpeedTargetResolution(null, TargetRefusalReason.NotRunning);

        GameProcessCandidate? chosen = null;
        if (!string.IsNullOrWhiteSpace(preferredGameId))
        {
            foreach (GameProcessCandidate candidate in candidates)
            {
                if (!string.Equals(candidate.GameId, preferredGameId, StringComparison.OrdinalIgnoreCase)) continue;
                chosen = Better(chosen, candidate);
            }
        }

        if (chosen is null)
        {
            GameProcessCandidate? foreground = null;
            int foregroundCount = 0;
            foreach (GameProcessCandidate candidate in candidates)
            {
                if (!candidate.IsForeground) continue;
                foregroundCount++;
                foreground = Better(foreground, candidate);
            }

            if (foregroundCount == 1) chosen = foreground;
            else if (candidates.Count == 1) chosen = candidates[0];
            else return new SpeedTargetResolution(null, TargetRefusalReason.Ambiguous);
        }

        if (chosen is null)
            return new SpeedTargetResolution(null, TargetRefusalReason.NotRunning);
        if (!chosen.IsLibraryGame)
            return new SpeedTargetResolution(null, TargetRefusalReason.NotALibraryGame);

        return new SpeedTargetResolution(
            new SpeedTarget(chosen.ProcessId, chosen.ProcessName, chosen.WindowHandle, chosen.GameId), default);
    }

    // Deterministic pick between candidates of the SAME game (never between
    // unrelated games): prefer a real window handle, then the lowest PID.
    private static GameProcessCandidate Better(GameProcessCandidate? current, GameProcessCandidate candidate)
    {
        if (current is null) return candidate;
        bool currentWindow = current.WindowHandle != IntPtr.Zero;
        bool candidateWindow = candidate.WindowHandle != IntPtr.Zero;
        if (candidateWindow != currentWindow) return candidateWindow ? candidate : current;
        return candidate.ProcessId < current.ProcessId ? candidate : current;
    }

    // Re-verify then act. A null target, an exited process, a non-library
    // process, or a request that changes nothing are all ordinary refusals.
    internal static SpeedApplyResult Apply(SpeedTarget? target, double multiplier, ISpeedApplier applier)
    {
        ArgumentNullException.ThrowIfNull(applier);
        double requested = Clamp(multiplier);

        if (target is null)
            return Refuse(requested, TargetRefusalReason.NotRunning, "No game is running; the speed control is inactive.");

        SpeedTargetState state;
        try { state = applier.Inspect(target); }
        catch (Exception ex)
        {
            return Refuse(requested, TargetRefusalReason.ProcessExited,
                "The game process could not be verified: " + ex.Message);
        }

        if (!state.Alive)
            return Refuse(requested, TargetRefusalReason.ProcessExited,
                "The game process exited before the speed could be applied.");
        if (!state.IsLibraryGame)
            return Refuse(requested, TargetRefusalReason.NotALibraryGame,
                "The speed control only applies to games launched from this library.");
        if (Math.Abs(state.CurrentMultiplier - requested) < Epsilon)
            return Refuse(requested, TargetRefusalReason.Unchanged, "The game is already at " + Describe(requested) + ".");

        double applied;
        try { applied = applier.ApplySpeed(target, requested); }
        catch (Exception ex)
        {
            return Refuse(requested, TargetRefusalReason.ProcessExited,
                "The game exited while the speed was being applied: " + ex.Message);
        }

        double result = Clamp(applied);
        string name = string.IsNullOrWhiteSpace(target.ProcessName) ? "the game" : target.ProcessName;
        return new SpeedApplyResult(result, true, Describe(result) + " applied to " + name + ".", null);
    }

    private static SpeedApplyResult Refuse(double multiplier, TargetRefusalReason reason, string message)
        => new(multiplier, false, message, reason);

    // Sanitizing snapshot of the remembered per-game speeds.
    internal static IReadOnlyDictionary<string, double> LoadPerGame(IReadOnlyDictionary<string, double>? stored)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (stored is null) return result;
        foreach (KeyValuePair<string, double> entry in stored)
        {
            if (string.IsNullOrWhiteSpace(entry.Key)) continue;
            result[entry.Key] = Clamp(entry.Value);
        }
        return result;
    }

    // Each game keeps its own remembered speed; an unknown id falls back safely.
    internal static double ForGame(string gameId, IReadOnlyDictionary<string, double>? stored, double fallback = NormalMultiplier)
    {
        if (!string.IsNullOrWhiteSpace(gameId) && stored is not null && stored.TryGetValue(gameId, out double remembered))
            return Clamp(remembered);
        return Clamp(fallback);
    }

    internal static string Describe(double multiplier)
    {
        double value = Clamp(multiplier);
        if (Math.Abs(value - NormalMultiplier) < Epsilon) return "normal (1.0x)";
        return value.ToString("0.0##", CultureInfo.InvariantCulture) + "x";
    }
}
