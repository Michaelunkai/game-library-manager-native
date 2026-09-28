using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

internal static class WandLaunchDispatchTests
{
    internal static int Run()
    {
        int checks = 0;
        void Require(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(message);
        }

        var idle = new WandProtocolPreflight(true, false, "idle-sidebar-state", null, null);
        var busy = new WandProtocolPreflight(false, true, "trainer-was-already-launching", "57393", 2212);
        var unknown = new WandProtocolPreflight(false, false, "trainer-state-unavailable-before-navigation", null, null);

        int cdpCalls = 0;
        int uriCalls = 0;
        int preflightCalls = 0;
        int lateProcessPolls = 0;
        int lateProcessSnapshots = 0;
        var dispatches = new List<string>();
        var lateProcess = WandNewGameDispatchCoordinator.DispatchOnceAsync(
            () =>
            {
                lateProcessSnapshots++;
                return new HashSet<int>();
            },
            () => null,
            (observed, _) =>
            {
                cdpCalls++;
                dispatches.Add("cdp");
                return Task.FromResult(new WandCdpLaunchResult(
                    WandCdpLaunchDisposition.KnownNoDispatch, false, "cdp-unavailable-before-route"));
            },
            () =>
            {
                preflightCalls++;
                return Task.FromResult(idle);
            },
            (_, _) =>
            {
                uriCalls++;
                dispatches.Add("uri");
                return Task.CompletedTask;
            },
            async (_, timeout) =>
            {
                Require(timeout == TimeSpan.FromSeconds(20), "The exact-process observation window changed unexpectedly.");
                for (int poll = 0; poll < 3; poll++)
                {
                    lateProcessPolls++;
                    await Task.Yield();
                }
                return "late-exact-game-process";
            },
            TimeSpan.FromSeconds(20),
            CancellationToken.None).GetAwaiter().GetResult();
        Require(lateProcess.Process == "late-exact-game-process" && lateProcess.UsedProtocol
            && lateProcess.DispatchAttempted && !lateProcess.Blocked && !lateProcess.OutcomeUnknown,
            "A late exact process after the single URI fallback was not retained as the launch result.");
        Require(cdpCalls == 1 && preflightCalls == 0 && uriCalls == 1 && lateProcessPolls == 3
            && lateProcessSnapshots == 2
            && string.Join(",", dispatches) == "cdp,uri",
            "A late process after the URI no-op caused a duplicate launch dispatch.");

        int processBeforeCdpSnapshots = 0;
        int processBeforeCdpDispatches = 0;
        int processBeforeCdpUriCalls = 0;
        var processBeforeCdp = WandNewGameDispatchCoordinator.DispatchOnceAsync(
            () =>
            {
                processBeforeCdpSnapshots++;
                return new HashSet<int> { 75 };
            },
            () => "game-appeared-before-cdp",
            (_, _) =>
            {
                processBeforeCdpDispatches++;
                return Task.FromResult(new WandCdpLaunchResult(
                    WandCdpLaunchDisposition.KnownNoDispatch, false, "unexpected-cdp-dispatch"));
            },
            () => Task.FromResult(idle),
            (_, _) =>
            {
                processBeforeCdpUriCalls++;
                return Task.CompletedTask;
            },
            (_, _) => Task.FromResult<string?>("game-appeared-before-cdp"),
            TimeSpan.FromSeconds(20), CancellationToken.None).GetAwaiter().GetResult();
        Require(processBeforeCdp.Process == "game-appeared-before-cdp" && !processBeforeCdp.DispatchAttempted
            && processBeforeCdpSnapshots == 1 && processBeforeCdpDispatches == 0 && processBeforeCdpUriCalls == 0,
            "An exact game that appeared before CDP dispatch was not observed without another launch action.");
        Require(processBeforeCdp.Reason == "exact-process-appeared-before-cdp-dispatch",
            "The pre-CDP process race was not recorded distinctly from a dispatched launch.");

        int processBeforeUriSnapshots = 0;
        int processBeforeUriCalls = 0;
        var processBeforeUri = WandNewGameDispatchCoordinator.DispatchOnceAsync(
            () => ++processBeforeUriSnapshots == 1 ? new HashSet<int>() : new HashSet<int> { 76 },
            () => "game-appeared-before-uri",
            (_, _) => Task.FromResult(new WandCdpLaunchResult(
                WandCdpLaunchDisposition.KnownNoDispatch, false, "cdp-unavailable-before-route")),
            () => Task.FromResult(idle),
            (_, _) =>
            {
                processBeforeUriCalls++;
                return Task.CompletedTask;
            },
            (_, _) => Task.FromResult<string?>("game-appeared-before-uri"),
            TimeSpan.FromSeconds(20), CancellationToken.None).GetAwaiter().GetResult();
        Require(processBeforeUri.Process == "game-appeared-before-uri" && !processBeforeUri.DispatchAttempted
            && processBeforeUriCalls == 0 && processBeforeUriSnapshots == 2,
            "An exact process appearing during the fresh URI preflight did not suppress the URI dispatch.");
        Require(processBeforeUri.Reason == "exact-process-appeared-before-protocol-dispatch",
            "The pre-URI process race was not recorded distinctly from a dispatched launch.");

        int unknownUriCalls = 0;
        int unknownFallbackChecks = 0;
        var uncertainCdp = WandNewGameDispatchCoordinator.DispatchOnceAsync(
            () => new HashSet<int>(),
            () => null,
            (_, _) => Task.FromResult(new WandCdpLaunchResult(
                WandCdpLaunchDisposition.Unknown, true, "helper-timeout-after-route")),
            () =>
            {
                unknownFallbackChecks++;
                return Task.FromResult(idle);
            },
            (_, _) =>
            {
                unknownUriCalls++;
                return Task.CompletedTask;
            },
            (_, _) => Task.FromResult<string?>(null),
            TimeSpan.FromSeconds(20), CancellationToken.None).GetAwaiter().GetResult();
        Require(uncertainCdp.OutcomeUnknown && uncertainCdp.DispatchAttempted
            && unknownFallbackChecks == 0 && unknownUriCalls == 0,
            "An ambiguous CDP launch outcome was followed by a protocol redispatch.");

        int cdpPlayUriCalls = 0;
        var dispatchedCdp = WandNewGameDispatchCoordinator.DispatchOnceAsync(
            () => new HashSet<int>(),
            () => null,
            (_, _) => Task.FromResult(new WandCdpLaunchResult(
                WandCdpLaunchDisposition.PlayDispatched, true, "play-dispatched")),
            () => Task.FromResult(idle),
            (_, _) =>
            {
                cdpPlayUriCalls++;
                return Task.CompletedTask;
            },
            async (_, _) =>
            {
                await Task.Yield();
                return "late-cdp-game-process";
            },
            TimeSpan.FromSeconds(20), CancellationToken.None).GetAwaiter().GetResult();
        Require(dispatchedCdp.Process == "late-cdp-game-process" && !dispatchedCdp.UsedProtocol
            && cdpPlayUriCalls == 0,
            "A verified CDP Play dispatch was followed by a URI launch.");

        int routeAttemptUriCalls = 0;
        var routeAttempt = WandNewGameDispatchCoordinator.DispatchOnceAsync(
            () => new HashSet<int>(),
            () => null,
            (_, _) => Task.FromResult(new WandCdpLaunchResult(
                WandCdpLaunchDisposition.KnownNoDispatch, true, "navigation-attempted")),
            () => Task.FromResult(idle),
            (_, _) =>
            {
                routeAttemptUriCalls++;
                return Task.CompletedTask;
            },
            (_, _) => Task.FromResult<string?>(null),
            TimeSpan.FromSeconds(20), CancellationToken.None).GetAwaiter().GetResult();
        Require(routeAttempt.OutcomeUnknown && routeAttemptUriCalls == 0,
            "A route mutation labeled as a no-op was followed by a URI launch.");

        foreach (var busyOrUnknown in new[] { busy, unknown })
        {
            int blockedUriCalls = 0;
            var blockedFallback = WandNewGameDispatchCoordinator.DispatchOnceAsync(
                () => new HashSet<int>(),
                () => null,
                (_, _) => Task.FromResult(new WandCdpLaunchResult(
                    WandCdpLaunchDisposition.KnownNoDispatch, false, "no-cdp-route")),
                () => Task.FromResult(busyOrUnknown),
                (_, _) =>
                {
                    blockedUriCalls++;
                    return Task.CompletedTask;
                },
                (_, _) => Task.FromResult<string?>(null),
                TimeSpan.FromSeconds(20), CancellationToken.None).GetAwaiter().GetResult();
            Require(!blockedFallback.Blocked && blockedFallback.UsedProtocol && blockedFallback.DispatchAttempted
                && blockedFallback.OutcomeUnknown && blockedUriCalls == 1,
                "Busy or unknown trainer state must still send the exact-game URI handoff.");
        }

        int noProcessUriCalls = 0;
        var noProcessAfterUri = WandNewGameDispatchCoordinator.DispatchOnceAsync(
            () => new HashSet<int>(),
            () => null,
            (_, _) => Task.FromResult(new WandCdpLaunchResult(
                WandCdpLaunchDisposition.KnownNoDispatch, false, "cdp-unavailable-before-route")),
            () => Task.FromResult(idle),
            (_, _) =>
            {
                noProcessUriCalls++;
                return Task.CompletedTask;
            },
            (_, _) => Task.FromResult<string?>(null),
            TimeSpan.FromSeconds(20), CancellationToken.None).GetAwaiter().GetResult();
        Require(noProcessAfterUri.UsedProtocol && noProcessAfterUri.OutcomeUnknown
            && noProcessUriCalls == 1,
            "A protocol URI that yielded no process was retried instead of returning an unknown outcome.");

        int throwingUriCalls = 0;
        var throwingUri = WandNewGameDispatchCoordinator.DispatchOnceAsync(
            () => new HashSet<int>(),
            () => null,
            (_, _) => Task.FromResult(new WandCdpLaunchResult(
                WandCdpLaunchDisposition.KnownNoDispatch, false, "cdp-unavailable-before-route")),
            () => Task.FromResult(idle),
            (_, _) =>
            {
                throwingUriCalls++;
                throw new InvalidOperationException("Shell reported an ambiguous handoff result.");
            },
            (_, _) => Task.FromResult<string?>(null),
            TimeSpan.FromSeconds(20), CancellationToken.None).GetAwaiter().GetResult();
        Require(throwingUri.UsedProtocol && throwingUri.OutcomeUnknown && throwingUri.DispatchAttempted
            && throwingUriCalls == 1,
            "An ambiguous shell URI result was treated as a known no-op or retried.");

        int safetyBlockedUriCalls = 0;
        var safetyBlockedCdp = WandNewGameDispatchCoordinator.DispatchOnceAsync(
            () => new HashSet<int>(),
            () => null,
            (_, _) => Task.FromResult(new WandCdpLaunchResult(
                WandCdpLaunchDisposition.SafetyBlocked, false, "trainer-was-already-launching")),
            () => throw new InvalidOperationException("A safety block must not gate protocol fallback."),
            (_, _) =>
            {
                safetyBlockedUriCalls++;
                return Task.CompletedTask;
            },
            (_, _) => Task.FromResult<string?>(null),
            TimeSpan.FromSeconds(20), CancellationToken.None).GetAwaiter().GetResult();
        Require(!safetyBlockedCdp.Blocked && safetyBlockedCdp.UsedProtocol && safetyBlockedUriCalls == 1,
            "A CDP busy-trainer safety block must still send the exact-game URI handoff.");

        int readinessAttempts = 0;
        var eventualIdle = WandNewGameDispatchCoordinator.RetryReadOnlyPreflightAsync(
            () =>
            {
                readinessAttempts++;
                return Task.FromResult(readinessAttempts < 3 ? unknown : idle);
            },
            () => Task.CompletedTask,
            CancellationToken.None).GetAwaiter().GetResult();
        Require(eventualIdle.SafeToDispatch && readinessAttempts == 3,
            "Cold CDP readiness did not retry the read-only preflight within its bound.");

        int busyAttempts = 0;
        var immediateBusy = WandNewGameDispatchCoordinator.RetryReadOnlyPreflightAsync(
            () =>
            {
                busyAttempts++;
                return Task.FromResult(busy);
            },
            () => Task.CompletedTask,
            CancellationToken.None).GetAwaiter().GetResult();
        Require(immediateBusy.TrainerBusy && busyAttempts == 1,
            "Cold-readiness retry continued after observing a busy trainer.");

        int unknownAttempts = 0;
        var stillUnknown = WandNewGameDispatchCoordinator.RetryReadOnlyPreflightAsync(
            () =>
            {
                unknownAttempts++;
                return Task.FromResult(unknown);
            },
            () => Task.CompletedTask,
            CancellationToken.None).GetAwaiter().GetResult();
        Require(!stillUnknown.SafeToDispatch && unknownAttempts == 3,
            "Unknown status did not remain blocked after the bounded read-only retries.");

        var cdpResult = WandIntegration.InterpretCdpLaunchResult(
            "{\"navigated\":true,\"playDispatched\":true,\"dispatchOutcome\":\"dispatched\"}", 0);
        Require(cdpResult.Disposition == WandCdpLaunchDisposition.PlayDispatched,
            "A verified CDP Play result was not recognized as the one dispatched action.");
        var uncertainRouteResult = WandIntegration.InterpretCdpLaunchResult(
            "{\"navigated\":true,\"playDispatched\":false,\"dispatchOutcome\":\"unknown\"}", 3);
        Require(uncertainRouteResult.Disposition == WandCdpLaunchDisposition.Unknown
            && uncertainRouteResult.NavigationAttempted,
            "A route attempt with no Play acknowledgement was not retained as an ambiguous dispatch.");
        var knownNoDispatch = WandIntegration.InterpretCdpLaunchResult(
            "{\"navigated\":false,\"playDispatched\":false,\"dispatchOutcome\":\"not-dispatched\"}", 1);
        Require(knownNoDispatch.Disposition == WandCdpLaunchDisposition.KnownNoDispatch,
            "A helper result proving no route or Play action was not eligible for a fresh-gated fallback.");
        var cdpBusy = WandIntegration.InterpretCdpLaunchResult(
            "{\"ok\":false,\"state\":\"blocked\",\"navigated\":false,\"playDispatched\":false,\"reason\":\"trainer-was-already-launching\"}", 3);
        Require(cdpBusy.Disposition == WandCdpLaunchDisposition.SafetyBlocked,
            "A busy trainer reported by the CDP helper did not block protocol fallback.");
        var malformedCdp = WandIntegration.InterpretCdpLaunchResult("{}", 0);
        Require(malformedCdp.Disposition == WandCdpLaunchDisposition.Unknown,
            "Malformed CDP output was treated as a known no-op.");

        return checks;
    }
}
