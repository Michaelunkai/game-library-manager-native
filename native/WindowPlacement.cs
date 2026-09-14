using System;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace GameLibrary.Native;

public enum StartupMonitorMode
{
    Auto,
    Primary,
    Secondary
}

internal readonly record struct StartupPlacement(StartupMonitorMode Mode, Point Pointer, Point? ForegroundCenter);

internal static class WindowPlacement
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);

    internal static StartupPlacement Capture(StartupMonitorMode mode)
    {
        Point pointer = GetCursorPos(out var nativePointer) ? new Point(nativePointer.X, nativePointer.Y) : Point.Empty;
        Point? foregroundCenter = null;
        IntPtr foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && GetWindowRect(foreground, out var rect) && rect.Right > rect.Left && rect.Bottom > rect.Top)
            foregroundCenter = new Point(rect.Left + (rect.Right - rect.Left) / 2, rect.Top + (rect.Bottom - rect.Top) / 2);
        return new StartupPlacement(mode, pointer, foregroundCenter);
    }

    internal static int SelectIndex(Rectangle[] workAreas, Point pointer, Point? foregroundCenter, int primaryIndex, StartupMonitorMode mode)
    {
        if (workAreas == null || workAreas.Length == 0) return -1;
        int primary = primaryIndex >= 0 && primaryIndex < workAreas.Length ? primaryIndex : 0;
        if (mode == StartupMonitorMode.Primary) return primary;
        if (mode == StartupMonitorMode.Secondary)
        {
            int secondary = Enumerable.Range(0, workAreas.Length)
                .Where(index => index != primary)
                .OrderBy(index => workAreas[index].Left)
                .ThenBy(index => workAreas[index].Top)
                .FirstOrDefault(-1);
            return secondary >= 0 ? secondary : primary;
        }
        int pointerIndex = Array.FindIndex(workAreas, area => area.Contains(pointer));
        if (pointerIndex >= 0) return pointerIndex;
        if (foregroundCenter is Point foreground)
        {
            int foregroundIndex = Array.FindIndex(workAreas, area => area.Contains(foreground));
            if (foregroundIndex >= 0) return foregroundIndex;
        }
        return primary;
    }

    internal static Forms.Screen Resolve(StartupPlacement placement)
    {
        var screens = Forms.Screen.AllScreens;
        int primary = Array.FindIndex(screens, screen => screen.Primary);
        int selected = SelectIndex(screens.Select(screen => screen.WorkingArea).ToArray(), placement.Pointer, placement.ForegroundCenter, primary, placement.Mode);
        return selected >= 0 && selected < screens.Length ? screens[selected] : Forms.Screen.PrimaryScreen ?? screens[0];
    }
}
