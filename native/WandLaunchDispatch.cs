using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

internal enum WandCdpLaunchDisposition
{
    KnownNoDispatch,
    PlayDispatched,
    Unknown,
    SafetyBlocked
}

internal sealed record WandCdpLaunchResult(WandCdpLaunchDisposition Disposition, bool NavigationAttempted, string Reason);

internal sealed record WandNewGameDispatchResult<T>(
    T? Process,
    bool UsedProtocol,
    bool DispatchAttempted,
    bool Blocked,
    bool OutcomeUnknown,
    DateTime DispatchStartedUtc,
    string Reason,
    WandProtocolPreflight? BlockedPreflight = null) where T : class;

internal static class WandNewGameDispatchCoordinator
{
    internal const int MaximumReadOnlyPreflightAttempts = 3;

    internal static async Task<WandProtocolPreflight> RetryReadOnlyPreflightAsync(
        Func<Task<WandProtocolPreflight>> inspect,
        Func<Task> delay,
        CancellationToken cancellation)
    {
        WandProtocolPreflight? last = null;
        for (int attempt = 1; attempt <= MaximumReadOnlyPreflightAttempts; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            last = await inspect();
            if (last.SafeToDispatch || last.TrainerBusy) return last;
            if (attempt < MaximumReadOnlyPreflightAttempts) await delay();
        }
        return last ?? new WandProtocolPreflight(false, false,
            "trainer-state-unavailable-before-navigation", null, null);
    }

    internal static async Task<WandNewGameDispatchResult<T>> DispatchOnceAsync<T>(
        Func<HashSet<int>> snapshotProcessIds,
        Func<T?> findExactProcess,
        Func<HashSet<int>, DateTime, Task<WandCdpLaunchResult>> dispatchCdp,
        Func<Task<WandProtocolPreflight>> inspectProtocol,
        Func<HashSet<int>, DateTime, Task> dispatchProtocol,
        Func<HashSet<int>, TimeSpan, Task<T?>> waitForExactProcess,
        TimeSpan observationWindow,
        CancellationToken cancellation) where T : class
    {
        cancellation.ThrowIfCancellationRequested();
        var beforeCdp = snapshotProcessIds();
        if (beforeCdp.Count > 0)
        {
            T? process = findExactProcess();
            return new WandNewGameDispatchResult<T>(process, false, false,
                process == null, false, DateTime.MinValue,
                process == null ? "exact-process-present-before-cdp-dispatch-handle-unavailable" : "exact-process-appeared-before-cdp-dispatch");
        }

        var dispatchStarted = DateTime.UtcNow;
        WandCdpLaunchResult cdp = await dispatchCdp(beforeCdp, dispatchStarted);

        // Once CDP may have mutated the autoLaunch route, only observe the exact
        // executable. Even a nominal "no dispatch" result is ambiguous after a
        // route attempt because the renderer may have accepted it before CDP
        // disconnected.
        if (cdp.NavigationAttempted
            || cdp.Disposition is WandCdpLaunchDisposition.PlayDispatched or WandCdpLaunchDisposition.Unknown)
        {
            T? process = await waitForExactProcess(beforeCdp, observationWindow);
            bool unknown = cdp.Disposition == WandCdpLaunchDisposition.Unknown || process == null;
            return new WandNewGameDispatchResult<T>(process, false, true, false, unknown,
                dispatchStarted, cdp.Reason);
        }

        // CDP proved it did not navigate or dispatch Play (KnownNoDispatch /
        // SafetyBlocked). The exact-game wemod://play URI is the reliable Wand
        // handoff: Wand routes by gameId, so it still works while another game's
        // trainer is busy. It is single-shot; do not resend if the exact game
        // already appeared.
        var beforeProtocol = snapshotProcessIds();
        if (beforeProtocol.Count > 0)
        {
            T? process = findExactProcess();
            return new WandNewGameDispatchResult<T>(process, false, false,
                process == null, false, DateTime.MinValue,
                process == null ? "exact-process-present-before-protocol-dispatch-handle-unavailable" : "exact-process-appeared-before-protocol-dispatch");
        }

        dispatchStarted = DateTime.UtcNow;
        try
        {
            await dispatchProtocol(beforeProtocol, dispatchStarted);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Shell URI delivery may fail after the local handler accepted it.
            // Preserve the one-shot rule by observing only; never resend it.
            T? uncertainProcess = await waitForExactProcess(beforeProtocol, observationWindow);
            return new WandNewGameDispatchResult<T>(uncertainProcess, true, true, false,
                uncertainProcess == null, dispatchStarted,
                "protocol-dispatch-outcome-unknown: " + ex.Message);
        }
        T? protocolProcess = await waitForExactProcess(beforeProtocol, observationWindow);
        return new WandNewGameDispatchResult<T>(protocolProcess, true, true, false,
            protocolProcess == null, dispatchStarted,
            protocolProcess == null ? "protocol-outcome-unconfirmed" : "protocol-process-observed");
    }

}
