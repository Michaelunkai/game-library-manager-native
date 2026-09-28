using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

// Compatibility entrypoint for existing GameL commands. Never reads or writes
// a profile; every invocation resolves the currently installed native build.
internal static class LegacyLauncher
{
    private static string Quote(string value)
    {
        var quoted = new StringBuilder("\"");
        int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { slashes++; continue; }
            quoted.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            quoted.Append(character);
            slashes = 0;
        }
        quoted.Append('\\', slashes * 2);
        return quoted.Append('"').ToString();
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string root = Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).FullName;
            string target = Path.Combine(root, "native", "dist", "GameLibrary.exe");
            if (!File.Exists(target)) throw new FileNotFoundException("The current Game Library executable is missing.", target);
            using (var process = Process.Start(new ProcessStartInfo(target, string.Join(" ", args.Select(Quote)))
            {
                UseShellExecute = false,
                WorkingDirectory = root
            }))
            {
                process.WaitForExit();
                return process.ExitCode;
            }
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "Game Library could not start", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
