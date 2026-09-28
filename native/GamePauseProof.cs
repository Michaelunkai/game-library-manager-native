using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace GameLibrary.Native;

internal static class GamePauseProof
{
    public static void Run(string root)
    {
        RunAsync(root).GetAwaiter().GetResult();
    }

    private static async Task RunAsync(string root)
    {
        string folder = Path.Combine(root, "native-pause-proof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string script = Path.Combine(folder, "fixture.ps1");
        File.WriteAllText(script, """
            param([string]$Root, [switch]$Child)
            $ErrorActionPreference = 'Stop'
            if (-not $Child) {
                $code = "& '" + $PSCommandPath.Replace("'", "''") + "' -Root '" + $Root.Replace("'", "''") + "' -Child"
                $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($code))
                $childProcess = Start-Process -FilePath "$PSHOME\powershell.exe" -ArgumentList @('-NoProfile', '-EncodedCommand', $encoded) -WindowStyle Hidden -PassThru
                [IO.File]::WriteAllText((Join-Path $Root 'child.pid'), [string]$childProcess.Id)
            }
            $name = if ($Child) { 'child.tick' } else { 'parent.tick' }
            while ($true) {
                [IO.File]::AppendAllText((Join-Path $Root $name), ([string][DateTime]::UtcNow.Ticks + "`n"))
                Start-Sleep -Milliseconds 30
            }
            """);
        string command = "& '" + script.Replace("'", "''") + "' -Root '" + folder.Replace("'", "''") + "'";
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));
        using var fixture = Process.Start(start) ?? throw new IOException("Pause fixture did not start.");
        GamePause? pause = null;
        string parentTick = Path.Combine(folder, "parent.tick"), childTick = Path.Combine(folder, "child.tick");
        try
        {
            string initialParent = "", initialChild = "";
            await WaitFor(() => long.TryParse(initialParent = ReadTick(parentTick), out _) && long.TryParse(initialChild = ReadTick(childTick), out _), "Fixture processes did not become ready.");
            await WaitFor(() => Newer(parentTick, initialParent) && Newer(childTick, initialChild), "Both current fixture heartbeats must advance before the proof.");
            using (var stale = new OwnedSuspension())
            {
                bool rejected = false;
                try { stale.SuspendTree(fixture.Id, fixture.StartTime.ToUniversalTime().Ticks + 1, Environment.ProcessId); }
                catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "A stale process identity was accepted.");
            }
            pause = await GamePause.PauseAsync(fixture);
            await Task.Delay(100);
            string parentBefore = ReadTick(parentTick), childBefore = ReadTick(childTick);
            Require(parentBefore.Length > 0 && childBefore.Length > 0, "Fixture heartbeat was incomplete.");
            await Task.Delay(450);
            Require(ReadTick(parentTick) == parentBefore && ReadTick(childTick) == childBefore,
                "Parent or child continued to execute while paused.");
            await pause.ResumeAsync(); pause = null;
            await WaitFor(() => Newer(parentTick, parentBefore) && Newer(childTick, childBefore), "Resume did not restart both processes.");

            pause = await GamePause.PauseAsync(fixture);
            parentBefore = ReadTick(parentTick); childBefore = ReadTick(childTick);
            pause.Dispose(); pause = null; // Same pipe disconnect caused by library shutdown/crash.
            await WaitFor(() => Newer(parentTick, parentBefore) && Newer(childTick, childBefore), "Guardian did not recover after connection loss.");

            pause = await GamePause.PauseAsync(fixture);
            parentBefore = ReadTick(parentTick); childBefore = ReadTick(childTick);
            using (var guardian = Process.GetProcessById(pause.GuardianId))
            { guardian.Kill(); await guardian.WaitForExitAsync(); }
            // Do not dispose or request resume before proving kernel recovery.
            await WaitFor(() => Newer(parentTick, parentBefore) && Newer(childTick, childBefore), "Kernel did not resume after abrupt guardian termination.");
            pause.Dispose(); pause = null;
        }
        finally
        {
            pause?.Dispose();
            if (!fixture.HasExited) { fixture.Kill(entireProcessTree: true); await fixture.WaitForExitAsync(); }
        }
    }

    private static string ReadTick(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            long latest = 0;
            while (reader.ReadLine() is string line) if (long.TryParse(line, out long tick)) latest = Math.Max(latest, tick);
            return latest > 0 ? latest.ToString() : "";
        }
        catch (IOException) { return ""; }
    }
    private static bool Newer(string path, string previous) => long.TryParse(previous, out long before)
        && long.TryParse(ReadTick(path), out long current) && current > before;
    private static async Task WaitFor(Func<bool> predicate, string failure)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(15))
        { if (predicate()) return; await Task.Delay(40); }
        throw new InvalidOperationException(failure);
    }
    private static void Require(bool result, string message) { if (!result) throw new InvalidOperationException(message); }
}
