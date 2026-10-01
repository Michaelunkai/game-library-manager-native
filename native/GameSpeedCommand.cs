using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

/// <summary>
/// The runtime seam the speed command drives. Separating this from
/// <see cref="ISpeedApplier"/> is deliberate: applying a factor needs two distinct
/// steps - inject the hook DLL into the target, then ask it to adopt the factor -
/// and the existing applier only covers the second. Keeping them apart is what lets
/// the command be unit tested without touching a real process.
/// </summary>
internal interface ISpeedRuntime
{
    /// <summary>True when the target process is still alive. The gate for every action.</summary>
    bool IsRunning(int processId);
    /// <summary>Loads the hook into the target if it is not already there. Idempotent.</summary>
    Task EnsureInjectedAsync(int processId, CancellationToken cancellation);
    /// <summary>Asks the in-process hook to adopt <paramref name="factor"/> and confirm it.</summary>
    Task<double> SetAsync(int processId, double factor, CancellationToken cancellation);
    /// <summary>Reads back the factor the hook is actually running at.</summary>
    Task<double> GetAsync(int processId, CancellationToken cancellation);
    /// <summary>True when the hook is present and healthy.</summary>
    bool IsInjected(int processId);
}

/// <summary>Production runtime: the MinGW clock hook via <see cref="GameSpeedNative"/>.</summary>
internal sealed class NativeSpeedRuntime : ISpeedRuntime
{
    internal static readonly NativeSpeedRuntime Instance = new();
    public bool IsRunning(int processId) => GameSpeedNative.IsRunning(processId);
    public Task EnsureInjectedAsync(int processId, CancellationToken cancellation) =>
        GameSpeedNative.InjectAsync(processId, cancellation);
    public Task<double> SetAsync(int processId, double factor, CancellationToken cancellation) =>
        GameSpeedNative.SetSpeedAsync(processId, factor, cancellation);
    public Task<double> GetAsync(int processId, CancellationToken cancellation) =>
        GameSpeedNative.GetSpeedAsync(processId, cancellation);
    public bool IsInjected(int processId)
    {
        try { return GameSpeedNative.GetStatusAsync(processId, CancellationToken.None).GetAwaiter().GetResult().Injected; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException) { return false; }
    }
}

internal enum SpeedCommandOutcome { NotRunning, Applied, Refused, Failed }

internal sealed record SpeedCommandResult(SpeedCommandOutcome Outcome, double Factor, string Message)
{
    internal bool Succeeded => Outcome == SpeedCommandOutcome.Applied;
}

/// <summary>
/// Turns a speed intent into an actually-applied factor on a real process.
/// <para>
/// This exists because the hotkey path previously only wrote the number into saved
/// state and updated a label. Nothing was ever injected into the game, so the
/// feature did nothing at all however many times F1 was pressed. The whole point of
/// this type is that the number is only stored once the hook has confirmed it, so
/// the UI can never claim a speed the game is not running at.
/// </para>
/// </summary>
internal static class GameSpeedCommand
{
    /// <summary>
    /// Applies <paramref name="requested"/> to the target, injecting the hook first if
    /// needed. The stored value is only updated on success, so a failed injection can
    /// never leave the UI claiming a speed the game is not running at.
    /// </summary>
    internal static async Task<SpeedCommandResult> ApplyAsync(
        int processId, double requested, ISpeedRuntime runtime, CancellationToken cancellation)
    {
        if (processId <= 0 || !runtime.IsRunning(processId))
            return new SpeedCommandResult(SpeedCommandOutcome.NotRunning, GameSpeedNative.NormalFactor,
                "That game is no longer running, so the speed was not changed.");

        if (!ScaledClock.ValidateFactor(requested))
            return new SpeedCommandResult(SpeedCommandOutcome.Refused, GameSpeedNative.NormalFactor,
                "A speed of " + requested.ToString("0.###") + "x is outside the supported range and was rejected.");

        try
        {
            // Injection first: SetSpeedAsync waits two seconds for the hook to confirm
            // the factor, so setting a speed on a process the hook was never loaded
            // into can only ever time out.
            if (!runtime.IsInjected(processId)) await runtime.EnsureInjectedAsync(processId, cancellation).ConfigureAwait(false);

            double applied = await runtime.SetAsync(processId, requested, cancellation).ConfigureAwait(false);
            return new SpeedCommandResult(SpeedCommandOutcome.Applied, applied,
                "Speed " + GameSpeedController.Describe(applied) + ".");
        }
        catch (OperationCanceledException) { throw; }
        catch (UnauthorizedAccessException)
        {
            return new SpeedCommandResult(SpeedCommandOutcome.Refused, GameSpeedNative.NormalFactor,
                "That game is running with higher permissions. Restart this application, or the game, at the same level.");
        }
        catch (TimeoutException ex)
        {
            return new SpeedCommandResult(SpeedCommandOutcome.Failed, GameSpeedNative.NormalFactor,
                "The speed hook did not respond: " + ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return new SpeedCommandResult(SpeedCommandOutcome.Failed, GameSpeedNative.NormalFactor,
                "The speed could not be applied: " + ex.Message);
        }
    }

    /// <summary>
    /// Resolves the next factor for a hotkey and applies it. F1 adds 0.5x, F2 removes
    /// 0.5x, and F3 returns to exactly 1.0x - the factor is snapped to the ladder by
    /// <see cref="GameSpeedController.ApplyHotkey"/> so repeated presses land on exact values.
    /// </summary>
    internal static Task<SpeedCommandResult> ApplyHotkeyAsync(
        int processId, double current, SpeedHotkey key, ISpeedRuntime runtime, CancellationToken cancellation) =>
        ApplyAsync(processId, GameSpeedController.ApplyHotkey(current, key), runtime, cancellation);

    /// <summary>Returns the game to exactly normal speed.</summary>
    internal static Task<SpeedCommandResult> ApplyNormalAsync(
        int processId, ISpeedRuntime runtime, CancellationToken cancellation) =>
        ApplyAsync(processId, GameSpeedNative.NormalFactor, runtime, cancellation);
}
