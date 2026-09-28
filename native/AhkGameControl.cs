using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

internal enum AhkGameState
{
    Unknown,
    Running,
    Pausing,
    Paused,
    Resuming,
    Restoring,
    Failed
}

internal sealed record AhkGameControlResult(AhkGameState State, string RequestId, string Detail, int ProcessId, long WindowHandle);

/// <summary>
/// Talks to the user's current AHK controller over a same-user, same-session
/// named pipe. Requests carry only a fixed operation and a pinned process/window
/// identity; they cannot execute commands or name arbitrary files.
/// </summary>
internal static class AhkGameControl
{
    private const int PipeConnectionTimeoutMilliseconds = 2000;
    private static readonly TimeSpan ObservationWindow = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> TargetGates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim PipeGate = new(1, 1);

    internal static string GetPipeName()
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The current Windows user SID could not be resolved.");
        int sessionId = Process.GetCurrentProcess().SessionId;
        return "GameLibrary-AHK-Control-" + sid + "-" + sessionId.ToString(CultureInfo.InvariantCulture);
    }

    internal static Task<AhkGameControlResult> PauseAsync(Process game, IntPtr windowHandle = default, CancellationToken cancellation = default, string? frozenStatePath = null) =>
        ApplyAsync(game, windowHandle, "Pause", AhkGameState.Paused, cancellation, frozenStatePath);

    internal static Task<AhkGameControlResult> ResumeAsync(Process game, IntPtr windowHandle = default, CancellationToken cancellation = default, string? frozenStatePath = null) =>
        ApplyAsync(game, windowHandle, "Resume", AhkGameState.Running, cancellation, frozenStatePath);

    internal static async Task<AhkGameControlResult> GetStatusAsync(Process game, IntPtr windowHandle = default, CancellationToken cancellation = default, string? frozenStatePath = null)
    {
        var target = CaptureTarget(game, windowHandle, frozenStatePath);
        return await SendAsync("Status", Guid.NewGuid().ToString("N"), target, cancellation).ConfigureAwait(false);
    }

    /// <summary>Non-mutating live pipe proof. AHK must authenticate this native client and reject the impossible target.</summary>
    internal static async Task<AhkGameControlResult> ProbeAsync(CancellationToken cancellation = default)
    {
        var response = await SendAsync("Status", Guid.NewGuid().ToString("N"),
            new TargetIdentity(0, "0000000000000000", IntPtr.Zero), cancellation).ConfigureAwait(false);
        if (response.State != AhkGameState.Failed ||
            !response.Detail.Contains("process, creation stamp, window, or session", StringComparison.OrdinalIgnoreCase))
            throw new IOException("AHK did not authenticate the native client and reject the invalid target: " + response.Detail);
        return response;
    }

    private static async Task<AhkGameControlResult> ApplyAsync(Process game, IntPtr windowHandle, string operation, AhkGameState expected, CancellationToken cancellation, string? frozenStatePath)
    {
        var target = CaptureTarget(game, windowHandle, frozenStatePath);
        string key = target.ProcessId.ToString(CultureInfo.InvariantCulture) + ":" + target.CreationFileTime + ":" + target.WindowHandle.ToInt64().ToString(CultureInfo.InvariantCulture);
        var gate = TargetGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            string requestId = Guid.NewGuid().ToString("N");
            AhkGameControlResult initial;
            try
            {
                initial = await SendAsync(operation, requestId, target, cancellation).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A connect/read timeout does not prove that AHK did nothing.
                // Read the exact target state before deciding whether a retry is safe.
                var observed = await SendAsync("Status", Guid.NewGuid().ToString("N"), target, cancellation).ConfigureAwait(false);
                if (observed.State == expected) return observed;
                if (observed.State != (operation == "Pause" ? AhkGameState.Running : AhkGameState.Paused))
                    throw new IOException("AHK control timed out and the target state is " + observed.State + "; the operation was not retried.");
                requestId = Guid.NewGuid().ToString("N");
                initial = await SendAsync(operation, requestId, target, cancellation).ConfigureAwait(false);
            }

            if (initial.State == expected) return initial;
            if (initial.State == AhkGameState.Failed)
                throw new IOException("AHK rejected " + operation + " for PID " + target.ProcessId.ToString(CultureInfo.InvariantCulture) + ": " + initial.Detail);

            var clock = Stopwatch.StartNew();
            AhkGameControlResult current = initial;
            while (clock.Elapsed < ObservationWindow)
            {
                cancellation.ThrowIfCancellationRequested();
                await Task.Delay(PollInterval, cancellation).ConfigureAwait(false);
                try
                {
                    current = await SendAsync("Status", Guid.NewGuid().ToString("N"), target, cancellation).ConfigureAwait(false);
                }
                catch (TimeoutException) when (clock.Elapsed < ObservationWindow)
                {
                    continue;
                }
                if (current.State == expected) return current;
                if (current.State == AhkGameState.Failed)
                    throw new IOException("AHK could not complete " + operation + ": " + current.Detail);
            }

            throw new TimeoutException("AHK did not confirm " + operation + " within 15 seconds. Last observed state: " + current.State + ". " + current.Detail);
        }
        finally { gate.Release(); }
    }

    private static TargetIdentity CaptureTarget(Process game, IntPtr requestedWindow, string? frozenStatePath)
    {
        ArgumentNullException.ThrowIfNull(game);
        game.Refresh();
        if (game.HasExited) throw new InvalidOperationException("The selected game process has exited.");
        int pid = game.Id;
        string creation = game.StartTime.ToUniversalTime().ToFileTimeUtc().ToString("X16", CultureInfo.InvariantCulture);
        IntPtr hwnd = requestedWindow != IntPtr.Zero ? requestedWindow : game.MainWindowHandle;
        if (hwnd == IntPtr.Zero)
            hwnd = FrozenProcessState.FindWindowHandle(frozenStatePath ?? Preferences.DefaultFrozenProcessesPath, pid, creation);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("The selected game does not currently expose a usable window handle.");
        if (!IsWindow(hwnd)) throw new InvalidOperationException("The selected game's saved window handle is no longer valid.");
        _ = GetWindowThreadProcessId(hwnd, out uint windowPid);
        if (windowPid != (uint)pid) throw new InvalidOperationException("The selected window does not belong to the selected game process.");
        game.Refresh();
        if (game.HasExited || game.StartTime.ToUniversalTime().ToFileTimeUtc().ToString("X16", CultureInfo.InvariantCulture) != creation)
            throw new InvalidOperationException("The selected process identity changed while it was being inspected.");
        return new TargetIdentity(pid, creation, hwnd);
    }

    private static async Task<AhkGameControlResult> SendAsync(string operation, string requestId, TargetIdentity target, CancellationToken cancellation)
    {
        await PipeGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
        using var pipe = new NamedPipeClientStream(".", GetPipeName(), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        connectTimeout.CancelAfter(PipeConnectionTimeoutMilliseconds);
        try { await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException("The AHK game-control pipe did not accept a connection within two seconds.");
        }

        string request = string.Join('\t', "v1", requestId, operation,
            target.ProcessId.ToString(CultureInfo.InvariantCulture), target.CreationFileTime,
            target.WindowHandle.ToInt64().ToString(CultureInfo.InvariantCulture)) + "\n";
        byte[] bytes = Encoding.UTF8.GetBytes(request);
        using var replyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        replyTimeout.CancelAfter(PipeConnectionTimeoutMilliseconds);
        try
        {
            await pipe.WriteAsync(bytes, replyTimeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(replyTimeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 512, leaveOpen: true);
            string? line = await reader.ReadLineAsync(replyTimeout.Token).ConfigureAwait(false);
            if (line == null) throw new IOException("The AHK control pipe disconnected before returning a state.");
            string[] fields = line.Split('\t', 4);
            if (fields.Length < 3 || fields[0] != "v1" || fields[1] != requestId)
                throw new IOException("The AHK control pipe returned a malformed or mismatched response.");
            if (!Enum.TryParse(fields[2], ignoreCase: true, out AhkGameState state))
                throw new IOException("The AHK control pipe returned an unknown state.");
            return new AhkGameControlResult(state, requestId, fields.Length == 4 ? fields[3] : "", target.ProcessId, target.WindowHandle.ToInt64());
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException("The AHK control request did not return within two seconds.");
        }
        }
        finally { PipeGate.Release(); }
    }

    private sealed record TargetIdentity(int ProcessId, string CreationFileTime, IntPtr WindowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindow(IntPtr hWnd);
}
