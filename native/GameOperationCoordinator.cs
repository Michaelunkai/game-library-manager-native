using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

internal enum GameOperationKind { Gameplay, Download, Promotion, Backup, Restore }

internal sealed record RunningInstallationProcess(int ProcessId, long CreationFileTimeUtc, string ExecutablePath, string ProcessName);

/// <summary>
/// Coordinates app-owned operations by canonical game, installation path, and save target.
/// Gameplay and a staged download share installation access; promotion and restore take exclusive access.
/// </summary>
internal sealed class GameOperationCoordinator
{
    private readonly KeyedAsyncReaderWriterLock locks = new();

    internal Task<IDisposable> AcquireGameplayAsync(string canonicalGameId, string installationPath, string? saveTargetPath = null, CancellationToken cancellation = default)
    {
        var requests = new List<LockRequest>
        {
            new(GameKey(canonicalGameId), Exclusive: false),
            new(InstallationKey(installationPath), Exclusive: false)
        };
        if (!string.IsNullOrWhiteSpace(saveTargetPath)) requests.Add(new LockRequest(SaveKey(canonicalGameId, saveTargetPath), Exclusive: false));
        return AcquireManyAsync(requests, cancellation);
    }

    /// <summary>Reserves one destination and permits its download while the installed copy is being played.</summary>
    internal Task<IDisposable> AcquireDownloadAsync(string canonicalGameId, string destinationPath, CancellationToken cancellation = default)
    {
        return AcquireManyAsync(new[]
        {
            new LockRequest(InstallationKey(destinationPath), Exclusive: false),
            new LockRequest(DownloadKey(canonicalGameId, destinationPath), Exclusive: true)
        }, cancellation);
    }

    /// <summary>Serializes the short replacement window against gameplay and same-game file operations.</summary>
    internal Task<IDisposable> AcquirePromotionAsync(string canonicalGameId, string installationPath, CancellationToken cancellation = default)
    {
        return AcquireManyAsync(new[]
        {
            new LockRequest(InstallationKey(installationPath), Exclusive: true)
        }, cancellation);
    }

    internal Task<IDisposable> AcquireBackupAsync(string canonicalGameId, string saveTargetPath, CancellationToken cancellation = default)
    {
        return AcquireManyAsync(new[]
        {
            new LockRequest(GameKey(canonicalGameId), Exclusive: true),
            new LockRequest(SaveKey(canonicalGameId, saveTargetPath), Exclusive: true)
        }, cancellation);
    }

    internal Task<IDisposable> AcquireRestoreAsync(string canonicalGameId, string installationPath, string saveTargetPath, CancellationToken cancellation = default)
    {
        return AcquireManyAsync(new[]
        {
            new LockRequest(GameKey(canonicalGameId), Exclusive: true),
            new LockRequest(InstallationKey(installationPath), Exclusive: true),
            new LockRequest(SaveKey(canonicalGameId, saveTargetPath), Exclusive: true)
        }, cancellation);
    }

    internal async Task<IReadOnlyList<RunningInstallationProcess>> FindProcessesUsingInstallationAsync(string installationPath, CancellationToken cancellation = default)
    {
        string root = CanonicalPath(installationPath);
        string rootPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        var result = new List<RunningInstallationProcess>();
        foreach (Process process in Process.GetProcesses())
        {
            cancellation.ThrowIfCancellationRequested();
            using (process)
            {
                try
                {
                    string? executable = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(executable)) continue;
                    string fullExecutable = CanonicalPath(executable);
                    if (!fullExecutable.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(fullExecutable, root, StringComparison.OrdinalIgnoreCase)) continue;
                    result.Add(new RunningInstallationProcess(process.Id, process.StartTime.ToUniversalTime().ToFileTimeUtc(), fullExecutable, process.ProcessName));
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
        }
        await Task.CompletedTask.ConfigureAwait(false);
        return result.OrderBy(item => item.ProcessId).ToArray();
    }

    internal async Task<IReadOnlyList<RunningInstallationProcess>> WaitForInstallationIdleAsync(
        string installationPath,
        TimeSpan pollInterval,
        Action<IReadOnlyList<RunningInstallationProcess>>? onWait = null,
        CancellationToken cancellation = default)
    {
        if (pollInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            IReadOnlyList<RunningInstallationProcess> active = await FindProcessesUsingInstallationAsync(installationPath, cancellation).ConfigureAwait(false);
            if (active.Count == 0) return active;
            try { onWait?.Invoke(active); } catch { }
            await Task.Delay(pollInterval, cancellation).ConfigureAwait(false);
        }
    }

    internal static string CanonicalPath(string path)
    {
        string full = Path.GetFullPath(path.Trim());
        try
        {
            FileSystemInfo info = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
            FileSystemInfo? resolved = info.ResolveLinkTarget(returnFinalTarget: true);
            if (resolved != null) full = Path.GetFullPath(resolved.FullName);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return Path.TrimEndingDirectorySeparator(full).ToUpperInvariant();
    }

    internal static string GameKey(string canonicalGameId)
    {
        if (string.IsNullOrWhiteSpace(canonicalGameId)) throw new ArgumentException("A canonical game identity is required.", nameof(canonicalGameId));
        return "game\0" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalGameId)));
    }

    internal static string InstallationKey(string installationPath) => "install\0" + CanonicalPath(installationPath);
    internal static string SaveKey(string canonicalGameId, string saveTargetPath) => "save\0" + GameKey(canonicalGameId) + "\0" + CanonicalPath(saveTargetPath);
    internal static string DownloadKey(string canonicalGameId, string destinationPath) => "download\0" + GameKey(canonicalGameId) + "\0" + CanonicalPath(destinationPath);

    private async Task<IDisposable> AcquireManyAsync(IEnumerable<LockRequest> requests, CancellationToken cancellation)
    {
        LockRequest[] unique = requests
            .GroupBy(request => request.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new LockRequest(group.Key, group.Any(request => request.Exclusive)))
            .OrderBy(request => request.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(request => request.Key, StringComparer.Ordinal)
            .ToArray();
        var held = new List<IDisposable>(unique.Length);
        try
        {
            foreach (LockRequest request in unique)
                held.Add(await locks.AcquireAsync(request.Key, request.Exclusive, cancellation).ConfigureAwait(false));
            return new CompositeLease(held);
        }
        catch
        {
            for (int i = held.Count - 1; i >= 0; i--) held[i].Dispose();
            throw;
        }
    }

    private sealed record LockRequest(string Key, bool Exclusive);

    private sealed class CompositeLease : IDisposable
    {
        private List<IDisposable>? leases;
        internal CompositeLease(List<IDisposable> leases) => this.leases = leases;
        public void Dispose()
        {
            List<IDisposable>? current = Interlocked.Exchange(ref leases, null);
            if (current == null) return;
            for (int i = current.Count - 1; i >= 0; i--) current[i].Dispose();
        }
    }

    private sealed class KeyedAsyncReaderWriterLock
    {
        private readonly object sync = new();
        private readonly Dictionary<string, Gate> gates = new(StringComparer.OrdinalIgnoreCase);

        internal Task<IDisposable> AcquireAsync(string key, bool exclusive, CancellationToken cancellation)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("A lock key is required.", nameof(key));
            cancellation.ThrowIfCancellationRequested();
            Waiter waiter;
            lock (sync)
            {
                if (!gates.TryGetValue(key, out Gate? gate)) gates.Add(key, gate = new Gate());
                if (gate.Waiters.Count == 0 && CanGrant(gate, exclusive))
                {
                    Grant(gate, exclusive);
                    return Task.FromResult<IDisposable>(new Lease(this, key, exclusive));
                }
                waiter = new Waiter(key, exclusive);
                gate.Waiters.Enqueue(waiter);
            }
            if (cancellation.CanBeCanceled) RegisterCancellation(waiter, cancellation);
            return waiter.Completion.Task;
        }

        private void RegisterCancellation(Waiter waiter, CancellationToken cancellation)
        {
            CancellationTokenRegistration registration = cancellation.Register(() => Cancel(waiter, cancellation));
            lock (sync)
            {
                if (waiter.Granted || waiter.Cancelled) registration.Unregister();
                else waiter.Registration = registration;
            }
        }

        private void Cancel(Waiter waiter, CancellationToken cancellation)
        {
            lock (sync)
            {
                if (waiter.Granted || waiter.Cancelled) return;
                waiter.Cancelled = true;
                waiter.Completion.TrySetCanceled(cancellation);
                if (gates.TryGetValue(waiter.Key, out Gate? gate)) Pump(waiter.Key, gate);
            }
        }

        private void Release(string key, bool exclusive)
        {
            lock (sync)
            {
                if (!gates.TryGetValue(key, out Gate? gate)) return;
                if (exclusive) gate.Writer = false;
                else if (gate.Readers > 0) gate.Readers--;
                Pump(key, gate);
            }
        }

        private void Pump(string key, Gate gate)
        {
            while (gate.Waiters.Count > 0 && gate.Waiters.Peek().Cancelled)
            {
                Waiter cancelled = gate.Waiters.Dequeue();
                cancelled.Registration.Unregister();
            }
            if (gate.Writer || gate.Waiters.Count == 0)
            {
                RemoveIfEmpty(key, gate);
                return;
            }
            if (gate.Readers > 0 && gate.Waiters.Peek().Exclusive) return;
            if (gate.Readers == 0 && gate.Waiters.Peek().Exclusive)
            {
                Waiter waiter = gate.Waiters.Dequeue();
                if (waiter.Cancelled) { Pump(key, gate); return; }
                gate.Writer = true;
                waiter.Granted = true;
                waiter.Registration.Unregister();
                waiter.Completion.TrySetResult(new Lease(this, key, exclusive: true));
                return;
            }
            while (gate.Waiters.Count > 0 && !gate.Waiters.Peek().Exclusive && !gate.Writer)
            {
                Waiter waiter = gate.Waiters.Dequeue();
                if (waiter.Cancelled) { waiter.Registration.Dispose(); continue; }
                gate.Readers++;
                waiter.Granted = true;
                waiter.Registration.Unregister();
                waiter.Completion.TrySetResult(new Lease(this, key, exclusive: false));
            }
        }

        private static bool CanGrant(Gate gate, bool exclusive) => !gate.Writer && (!exclusive ? true : gate.Readers == 0);
        private static void Grant(Gate gate, bool exclusive) { if (exclusive) gate.Writer = true; else gate.Readers++; }

        private void RemoveIfEmpty(string key, Gate gate)
        {
            if (gate.Writer || gate.Readers != 0 || gate.Waiters.Count != 0) return;
            gates.Remove(key);
        }

        private sealed class Gate { internal int Readers; internal bool Writer; internal Queue<Waiter> Waiters { get; } = new(); }
        private sealed class Waiter(string key, bool exclusive)
        {
            internal string Key { get; } = key;
            internal bool Exclusive { get; } = exclusive;
            internal bool Granted;
            internal bool Cancelled;
            internal TaskCompletionSource<IDisposable> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal CancellationTokenRegistration Registration;
        }
        private sealed class Lease(KeyedAsyncReaderWriterLock owner, string key, bool exclusive) : IDisposable
        {
            private KeyedAsyncReaderWriterLock? lockOwner = owner;
            public void Dispose() => Interlocked.Exchange(ref lockOwner, null)?.Release(key, exclusive);
        }
    }
}
