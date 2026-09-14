using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace GameLibrary.Native;

/// <summary>
/// Keeps Windows shell links and the running process on the same AppUserModel
/// identity.  This is deliberately small and uses the shell's documented
/// IShellLink/IPropertyStore contracts rather than editing .lnk bytes.
/// </summary>
internal static class ShortcutManager
{
    private const uint SlgpRawPath = 0x4;
    private const ushort VariantTypeLpwstr = 31;
    private static readonly Guid AppUserModelIdKey = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    internal static int RepairForExecutable(string executablePath)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            var result = 1;
            Exception? failure = null;
            var worker = new Thread(() =>
            {
                try { result = RepairForExecutableCore(executablePath); }
                catch (Exception ex) { failure = ex; }
            }) { IsBackground = true, Name = "GameLibrary.ShortcutRepair" };
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
            if (!worker.Join(TimeSpan.FromSeconds(10)))
            {
                Console.Error.WriteLine("Shortcut repair timed out.");
                return 1;
            }
            if (failure != null)
            {
                Console.Error.WriteLine("Shortcut repair failed: " + failure);
                return 1;
            }
            return result;
        }
        return RepairForExecutableCore(executablePath);
    }

    private static int RepairForExecutableCore(string executablePath)
    {
        try
        {
            executablePath = Path.GetFullPath(executablePath);
            if (!File.Exists(executablePath))
            {
                Console.Error.WriteLine("Shortcut repair skipped: executable does not exist: " + executablePath);
                return 1;
            }

            var repaired = 0;
            var inspected = 0;
            var failures = 0;
            foreach (var linkPath in CandidateLinks())
            {
                if (!File.Exists(linkPath)) continue;
                if (!TryGetTarget(linkPath, out var target)) continue;
                if (!PathsEqual(target, executablePath)) continue;
                inspected++;
                try
                {
                    RepairLink(linkPath, executablePath);
                    repaired++;
                }
                catch (Exception ex)
                {
                    failures++;
                    Console.Error.WriteLine($"Shortcut repair failed for {linkPath}: {ex.Message}");
                }
            }

            Console.WriteLine($"Shortcut repair: inspected {inspected}, repaired {repaired}, failures {failures}; AppUserModelId={Program.AppUserModelId}");
            return failures == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Shortcut repair failed: " + ex);
            return 1;
        }
    }

    private static IEnumerable<string> CandidateLinks()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var explicitLinks = new[]
        {
            Path.Combine(programs, "Game Library.lnk"),
            Path.Combine(desktop, "Game Library.lnk")
        };
        foreach (var path in explicitLinks)
        {
            if (seen.Add(path)) yield return path;
        }

        // Windows copies the display name of the Start-menu shortcut when it
        // creates a pin.  Repair the two canonical names directly instead of
        // walking every shell-managed link: Explorer can hold one of those
        // files open while it refreshes the pin database, and an all-links
        // scan can block an otherwise unrelated app launch.
        var taskbar = Path.Combine(appData, "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");
        foreach (var name in new[] { "Game Library.lnk", "GameLibrary.lnk" })
        {
            var path = Path.Combine(taskbar, name);
            if (seen.Add(path)) yield return path;
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(left, right, StringComparison.OrdinalIgnoreCase); }
    }

    private static bool TryGetTarget(string linkPath, out string target)
    {
        target = string.Empty;
        IShellLinkW? link = null;
        try
        {
            link = (IShellLinkW)(object)new ShellLink();
            var persist = (IPersistFile)link;
            ThrowIfFailed(persist.Load(linkPath, 0), "load shell link");
            var path = new StringBuilder(4096);
            var hr = link.GetPath(path, path.Capacity, IntPtr.Zero, SlgpRawPath);
            if (hr < 0 || path.Length == 0) return false;
            target = path.ToString();
            return true;
        }
        catch { return false; }
        finally
        {
            // Do not FinalReleaseComObject here: IPersistFile is another RCW
            // view over the same shell object, and force-releasing the RCW can
            // leave the following IPropertyStore call waiting forever.
            if (link != null) Marshal.ReleaseComObject(link);
        }
    }

    private static void RepairLink(string linkPath, string executablePath)
    {
        IShellLinkW? link = null;
        IPropertyStore? properties = null;
        try
        {
            // Recreate the link in memory instead of loading the existing
            // shell object.  Explorer's loaded-link property store can block
            // while it owns a pinned shortcut; a fresh link lets us write the
            // AppUserModelId atomically in one save.
            link = (IShellLinkW)(object)new ShellLink();
            var persist = (IPersistFile)link;
            var directory = Path.GetDirectoryName(executablePath) ?? string.Empty;
            var iconPath = Path.Combine(directory, "GameLibrary.ico");
            if (!File.Exists(iconPath)) iconPath = executablePath;
            ThrowIfFailed(link.SetPath(executablePath), "set shell link target");
            ThrowIfFailed(link.SetWorkingDirectory(directory), "set shell link working directory");
            ThrowIfFailed(link.SetIconLocation(iconPath, 0), "set shell link icon");
            ThrowIfFailed(link.SetDescription("Native Windows Game Library"), "set shell link description");

            properties = (IPropertyStore)link;
            SetStringProperty(properties, AppUserModelIdKey, Program.AppUserModelId);
            ThrowIfFailed(properties.Commit(), "commit shell link properties");
            ThrowIfFailed(persist.Save(linkPath, true), "save shell link");
            // Ask Explorer to invalidate the cached icon/AUMID for a pin that
            // was already materialized before this repair.
            SHChangeNotify(ShcneUpdateItem, ShcnfPathW, linkPath, IntPtr.Zero);
        }
        finally
        {
            if (properties != null) Marshal.ReleaseComObject(properties);
            if (link != null) Marshal.ReleaseComObject(link);
        }
    }

    private static void SetStringProperty(IPropertyStore properties, Guid keyId, string value)
    {
        var key = new PropertyKey { FormatId = keyId, PropertyId = 5 };
        var propVariant = Marshal.AllocHGlobal(32);
        var stringPointer = Marshal.StringToCoTaskMemUni(value);
        try
        {
            for (var i = 0; i < 32; i++) Marshal.WriteByte(propVariant, i, 0);
            Marshal.WriteInt16(propVariant, 0, (short)VariantTypeLpwstr);
            Marshal.WriteIntPtr(propVariant, 8, stringPointer);
            ThrowIfFailed(properties.SetValue(ref key, propVariant), "set shell link AppUserModelId");
        }
        finally
        {
            PropVariantClear(propVariant);
            // PropVariantClear owns and releases the VT_LPWSTR payload.  Do
            // not free stringPointer a second time: that double-free can
            // block the shell-link COM proxy before Commit is reached.
            Marshal.FreeHGlobal(propVariant);
        }
    }

    private static void ThrowIfFailed(int hResult, string operation)
    {
        if (hResult < 0) Marshal.ThrowExceptionForHR(hResult, new IntPtr(-1));
    }

    private const uint ShcneUpdateItem = 0x00002000;
    private const uint ShcnfPathW = 0x00000005;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint eventId, uint flags, string path, IntPtr item);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046"), ClassInterface(ClassInterfaceType.None)]
    private sealed class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath([Out] StringBuilder path, int capacity, IntPtr data, uint flags);
        [PreserveSig] int GetIDList(out IntPtr idList);
        [PreserveSig] int SetIDList(IntPtr idList);
        [PreserveSig] int GetDescription([Out] StringBuilder description, int capacity);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        [PreserveSig] int GetWorkingDirectory([Out] StringBuilder directory, int capacity);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        [PreserveSig] int GetArguments([Out] StringBuilder arguments, int capacity);
        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        [PreserveSig] int GetHotkey(out ushort hotkey);
        [PreserveSig] int SetHotkey(ushort hotkey);
        [PreserveSig] int GetShowCmd(out int showCommand);
        [PreserveSig] int SetShowCmd(int showCommand);
        [PreserveSig] int GetIconLocation([Out] StringBuilder path, int capacity, out int iconIndex);
        [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int iconIndex);
        [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        [PreserveSig] int Resolve(IntPtr windowHandle, uint flags);
        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        [PreserveSig] int GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        [PreserveSig] int Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        [PreserveSig] int Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, bool remember);
        [PreserveSig] int SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        [PreserveSig] int GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, IntPtr value);
        [PreserveSig] int SetValue(ref PropertyKey key, IntPtr value);
        [PreserveSig] int Commit();
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(IntPtr propVariant);
}
