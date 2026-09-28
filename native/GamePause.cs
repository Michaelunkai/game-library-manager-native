using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace GameLibrary.Native;

// Windows 11 state-change objects own the suspension. The kernel releases it
// when the final handle closes, including abrupt helper termination. The pipe
// additionally releases it when the library closes or crashes.
internal sealed class GamePause : IDisposable
{
    private readonly NamedPipeServerStream pipe;
    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    private readonly Process helper;
    private bool disposed;
    public bool IsPaused => !disposed && !helper.HasExited;
    internal int GuardianId => helper.Id;

    private GamePause(NamedPipeServerStream pipe, Process helper)
    {
        this.pipe = pipe; this.helper = helper;
        reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
        writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true);
    }

    public static async Task<GamePause> PauseAsync(Process game, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        game.Refresh();
        if (game.HasExited) throw new InvalidOperationException("The game has already exited.");
        int pid = game.Id;
        long ticks = game.StartTime.ToUniversalTime().Ticks;
        string name = "GameLibrary-Pause-" + Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        GamePause? result = null;
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--pause-guardian"); start.ArgumentList.Add(name);
            start.ArgumentList.Add(pid.ToString()); start.ArgumentList.Add(ticks.ToString());
            var helper = Process.Start(start) ?? throw new IOException("The pause helper could not start.");
            result = new GamePause(pipe, helper);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await pipe.WaitForConnectionAsync(timeout.Token);
            result.writer.AutoFlush = true;
            string? reply = await result.reader.ReadLineAsync(timeout.Token);
            if (reply != "paused") throw new InvalidOperationException(reply ?? "The pause helper disconnected.");
            game.Refresh();
            if (game.HasExited) throw new InvalidOperationException("The game exited while it was being paused.");
            return result;
        }
        catch { if (result != null) result.Dispose(); else pipe.Dispose(); throw; }
    }

    public async Task ResumeAsync(CancellationToken cancellation = default)
    {
        if (disposed) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        // On timeout the pipe closes; the guardian still resumes in finally.
        try
        {
            await writer.WriteLineAsync("resume".AsMemory(), timeout.Token);
            if (await reader.ReadLineAsync(timeout.Token) != "resumed")
                throw new IOException("The pause helper could not confirm resume.");
        }
        finally { Dispose(); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        pipe.Dispose(); // First: wake the guardian even if buffered wrappers fail.
        try { reader.Dispose(); } catch (IOException) { }
        try { writer.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        helper.Dispose();
    }

    public static int RunGuardian(string[] args)
    {
        if (args.Length != 4 || !args[1].StartsWith("GameLibrary-Pause-", StringComparison.Ordinal)
            || !int.TryParse(args[2], out int pid) || !long.TryParse(args[3], out long ticks)) return 2;
        using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var suspension = new OwnedSuspension();
        try
        {
            pipe.Connect(15000);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint owner)) throw new Win32Exception();
            try
            {
                suspension.SuspendTree(pid, ticks, checked((int)owner));
                writer.WriteLine("paused");
                reader.ReadLine(); // Resume on any command, EOF, or broken pipe.
                suspension.Resume();
                writer.WriteLine("resumed");
                return 0;
            }
            catch (Exception ex)
            {
                suspension.Resume();
                try { writer.WriteLine("Pause failed: " + ex.Message.Replace('\r', ' ').Replace('\n', ' ')); } catch { }
                return 1;
            }
        }
        catch { return 1; }
        // OwnedSuspension.Dispose releases kernel-owned suspension objects.
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}

internal sealed class OwnedSuspension : IDisposable
{
    private readonly List<SafeWaitHandle> states = new();
    private readonly List<Process> processes = new();

    public void SuspendTree(int pid, long expectedTicks, int ownerPid)
    {
        if (states.Count != 0) throw new InvalidOperationException("Already suspended.");
        var root = Process.GetProcessById(pid);
        processes.Add(root);
        _ = root.Handle; // Pin the process instance before checking its creation time.
        if (root.HasExited || root.StartTime.ToUniversalTime().Ticks != expectedTicks)
            throw new InvalidOperationException("The game process identity has changed.");
        if (pid == Environment.ProcessId || pid == ownerPid)
            throw new InvalidOperationException("Cannot pause the library or its pause helper.");
        var known = new Dictionary<int, Process> { [pid] = root };
        SuspendProcess(root);
        // With parents suspended, descendants can no longer be spawned by them.
        // Repeat for descendants that were still starting during the snapshot.
        for (int pass = 0; pass < 32; pass++)
        {
            bool added = false;
            foreach (var entry in SnapshotProcesses())
            {
                if (known.ContainsKey(entry.Pid) || !known.TryGetValue(entry.Parent, out var parent)) continue;
                if (entry.Pid == Environment.ProcessId || entry.Pid == ownerPid)
                    throw new InvalidOperationException("The selected process contains the library; pause was cancelled.");
                Process child;
                try { child = Process.GetProcessById(entry.Pid); }
                catch (ArgumentException) { continue; }
                processes.Add(child);
                try { _ = child.Handle; }
                catch (InvalidOperationException) { continue; }
                // Revalidate the relationship using the opened process object,
                // not a possibly recycled PID from the earlier snapshot.
                int query = NtQueryInformationProcess(child.Handle, 0, out var info, Marshal.SizeOf<ProcessBasicInformation>(), out _);
                if (query < 0) throw new Win32Exception(unchecked((int)RtlNtStatusToDosError(query)));
                if (info.Parent.ToInt64() != entry.Parent) continue;
                // Toolhelp parent IDs may refer to a recycled PID. Check age too.
                if (child.StartTime.ToUniversalTime() < parent.StartTime.ToUniversalTime()) continue;
                SuspendProcess(child); known.Add(entry.Pid, child); added = true;
            }
            if (!added) return;
        }
        throw new InvalidOperationException("The game process tree did not settle; pause was cancelled.");
    }

    private void SuspendProcess(Process process)
    {
        if (process.HasExited) return;
        int status;
        SafeWaitHandle state;
        try { status = NtCreateProcessStateChange(out state, 1, IntPtr.Zero, process.Handle, 0); }
        catch (EntryPointNotFoundException) { throw new PlatformNotSupportedException("Safe game pause requires Windows 11 or later."); }
        if (status < 0) { state?.Dispose(); throw new Win32Exception(unchecked((int)RtlNtStatusToDosError(status))); }
        states.Add(state);
        status = NtChangeProcessState(state, process.Handle, 0, IntPtr.Zero, UIntPtr.Zero, 0);
        if (status < 0) throw new Win32Exception(unchecked((int)RtlNtStatusToDosError(status)));
    }

    public void Resume()
    {
        for (int i = states.Count - 1; i >= 0; i--) states[i].Dispose();
        states.Clear();
    }

    public void Dispose()
    {
        Resume();
        foreach (var process in processes) process.Dispose();
        processes.Clear();
    }

    private static List<(int Pid, int Parent)> SnapshotProcesses()
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new Win32Exception();
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        var result = new List<(int, int)>();
        if (!Process32FirstW(snapshot, ref entry)) throw new Win32Exception();
        do { result.Add(((int)entry.Pid, (int)entry.Parent)); } while (Process32NextW(snapshot, ref entry));
        if (Marshal.GetLastWin32Error() != 18) throw new Win32Exception();
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Pid;
        public UIntPtr Heap;
        public uint Module, Threads, Parent;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessBasicInformation
    { public IntPtr ExitStatus, Peb, Affinity, Priority, Pid, Parent; }
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int kind, out ProcessBasicInformation info, int size, out int returned);
    [DllImport("ntdll.dll")] private static extern int NtCreateProcessStateChange(out SafeWaitHandle state, uint access, IntPtr attributes, IntPtr process, uint reserved);
    [DllImport("ntdll.dll")] private static extern int NtChangeProcessState(SafeWaitHandle state, IntPtr process, int change, IntPtr info, UIntPtr length, uint reserved);
    [DllImport("ntdll.dll")] private static extern uint RtlNtStatusToDosError(int status);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeWaitHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(SafeWaitHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool Process32NextW(SafeWaitHandle snapshot, ref ProcessEntry entry);
}
