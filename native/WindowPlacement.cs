using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace GameLibrary.Native;

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsUsable => Width > 0 && Height > 0;
    public bool Intersects(PixelRect other) => X < other.Right && Right > other.X && Y < other.Bottom && Bottom > other.Y;
}

public sealed record MonitorPlacementInfo(string DeviceName, PixelRect Bounds, PixelRect WorkArea, int DpiX, int DpiY, bool IsPrimary);

public sealed record WindowPlacementRequest(
    string PreferredMonitorDeviceName,
    string SelectionSource,
    PixelRect InitialBounds,
    double SavedWidthDip,
    double SavedHeightDip,
    string SavedMonitorDeviceName,
    PixelRect SavedBounds);

public sealed record WindowPlacementSnapshot(string MonitorDeviceName, PixelRect Bounds);

/// <summary>Captures a launch monitor before WPF can change foreground focus and applies window geometry once.</summary>
public static class WindowPlacement
{
    private const uint MonitorDefaultToNearest = 2;
    private const int MonitorDpiTypeEffective = 0;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW", SetLastError = true)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    public static WindowPlacementRequest Capture(string[] args, string statePath)
    {
        var monitors = EnumerateMonitors();
        var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
        MonitorPlacementInfo? preferred = null;
        string source = "primary fallback";

        string? explicitChoice = null;
        foreach (var arg in args)
        {
            if (arg == "--main-monitor") explicitChoice = "main";
            else if (arg == "--second-monitor") explicitChoice = "second";
        }
        if (explicitChoice == "main")
        {
            preferred = primary;
            source = "explicit main-monitor flag";
        }
        else if (explicitChoice == "second")
        {
            preferred = monitors.Where(m => !m.IsPrimary).OrderBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y).FirstOrDefault() ?? primary;
            source = preferred?.IsPrimary == true ? "explicit second-monitor flag (primary fallback)" : "explicit second-monitor flag";
        }
        else
        {
            IntPtr foreground = GetForegroundWindow();
            string foregroundDevice = foreground == IntPtr.Zero ? "" : MonitorDeviceForWindow(foreground);
            preferred = monitors.FirstOrDefault(m => string.Equals(m.DeviceName, foregroundDevice, StringComparison.OrdinalIgnoreCase));
            if (preferred != null) source = "foreground window";
            if (preferred == null && GetCursorPos(out var pointer))
            {
                string pointerDevice = MonitorDeviceForPoint(pointer);
                preferred = monitors.FirstOrDefault(m => string.Equals(m.DeviceName, pointerDevice, StringComparison.OrdinalIgnoreCase));
                if (preferred != null) source = "pointer";
            }
            preferred ??= primary;
        }

        var saved = ReadSavedPlacement(statePath);
        var initialBounds = preferred == null ? default : CalculateInitialBounds(preferred, saved.WidthDip, saved.HeightDip, saved.MonitorDeviceName, saved.Bounds);
        return new WindowPlacementRequest(preferred?.DeviceName ?? "", source, initialBounds,
            saved.WidthDip, saved.HeightDip, saved.MonitorDeviceName, saved.Bounds);
    }

    public static (double WidthDip, double HeightDip, string MonitorDeviceName, PixelRect Bounds) ReadSavedPlacement(string statePath)
    {
        try
        {
            using var stream = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object)
                return (1440, 900, "", default);
            double width = Number(settings, "windowWidth", 1440);
            double height = Number(settings, "windowHeight", 900);
            string device = Text(settings, "windowMonitorDeviceName");
            var bounds = new PixelRect(Integer(settings, "windowBoundsLeftPixels"), Integer(settings, "windowBoundsTopPixels"),
                Integer(settings, "windowBoundsWidthPixels"), Integer(settings, "windowBoundsHeightPixels"));
            return (ValidSize(width, 1440), ValidSize(height, 900), device, bounds.IsUsable ? bounds : default);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return (1440, 900, "", default);
        }
    }

    public static bool ApplyInitial(Window window, WindowPlacementRequest request)
    {
        var monitors = EnumerateMonitors();
        var monitor = monitors.FirstOrDefault(m => string.Equals(m.DeviceName, request.PreferredMonitorDeviceName, StringComparison.OrdinalIgnoreCase))
            ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
        if (monitor == null) return false;
        int dpiX = Math.Max(96, monitor.DpiX), dpiY = Math.Max(96, monitor.DpiY);
        double maxWidthDip = monitor.WorkArea.Width * 96d / dpiX;
        double maxHeightDip = monitor.WorkArea.Height * 96d / dpiY;
        window.MinWidth = Math.Min(window.MinWidth, maxWidthDip);
        window.MinHeight = Math.Min(window.MinHeight, maxHeightDip);
        double widthDip = Math.Clamp(ValidSize(request.SavedWidthDip, 1440), Math.Max(1, window.MinWidth), Math.Max(1, maxWidthDip));
        double heightDip = Math.Clamp(ValidSize(request.SavedHeightDip, 900), Math.Max(1, window.MinHeight), Math.Max(1, maxHeightDip));
        int widthPx = Math.Max(1, (int)Math.Round(widthDip * dpiX / 96d));
        int heightPx = Math.Max(1, (int)Math.Round(heightDip * dpiY / 96d));

        PixelRect initial;
        if (request.InitialBounds.IsUsable && string.Equals(request.PreferredMonitorDeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase))
            initial = Clamp(request.InitialBounds, monitor.WorkArea);
        else if (request.SavedBounds.IsUsable && string.Equals(request.SavedMonitorDeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase))
            initial = Clamp(request.SavedBounds, monitor.WorkArea);
        else
            initial = Center(widthPx, heightPx, monitor.WorkArea);

        // Keep WPF dimensions in device-independent units and use physical pixels only at the native boundary.
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Width = initial.Width * 96d / dpiX;
        window.Height = initial.Height * 96d / dpiY;
        var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        if (source == null) return false;
        return SetWindowPos(source.Handle, IntPtr.Zero, initial.X, initial.Y, initial.Width, initial.Height,
            SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
    }

    public static WindowPlacementSnapshot? ReadWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect)) return null;
        string device = MonitorDeviceForWindow(handle);
        var bounds = FromNative(rect);
        return device.Length > 0 && bounds.IsUsable ? new WindowPlacementSnapshot(device, bounds) : null;
    }

    /// <summary>Moves a window only when its current rectangle is wholly outside every current working area.</summary>
    public static bool RecoverIfInaccessible(IntPtr handle, out WindowPlacementSnapshot? snapshot)
    {
        snapshot = ReadWindow(handle);
        if (snapshot == null) return false;
        var current = snapshot;
        var monitors = EnumerateMonitors();
        if (monitors.Any(m => current.Bounds.Intersects(m.WorkArea))) return false;
        var nearest = monitors.OrderBy(m => DistanceSquared(current.Bounds, m.WorkArea)).FirstOrDefault();
        if (nearest == null) return false;
        var corrected = Clamp(current.Bounds, nearest.WorkArea);
        bool moved = SetWindowPos(handle, IntPtr.Zero, corrected.X, corrected.Y, corrected.Width, corrected.Height,
            SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
        if (moved) snapshot = new WindowPlacementSnapshot(nearest.DeviceName, corrected);
        return moved;
    }

    public static (double WidthDip, double HeightDip) ToDip(WindowPlacementSnapshot snapshot)
    {
        var monitor = EnumerateMonitors().FirstOrDefault(m => string.Equals(m.DeviceName, snapshot.MonitorDeviceName, StringComparison.OrdinalIgnoreCase));
        double dpiX = Math.Max(96, monitor?.DpiX ?? 96), dpiY = Math.Max(96, monitor?.DpiY ?? 96);
        return (snapshot.Bounds.Width * 96d / dpiX, snapshot.Bounds.Height * 96d / dpiY);
    }

    public static IReadOnlyList<MonitorPlacementInfo> EnumerateMonitors()
    {
        var output = new List<MonitorPlacementInfo>();
        foreach (var screen in Forms.Screen.AllScreens)
        {
            var bounds = new PixelRect(screen.Bounds.Left, screen.Bounds.Top, screen.Bounds.Width, screen.Bounds.Height);
            var work = new PixelRect(screen.WorkingArea.Left, screen.WorkingArea.Top, screen.WorkingArea.Width, screen.WorkingArea.Height);
            var point = new NativePoint { X = bounds.X + Math.Max(0, bounds.Width / 2), Y = bounds.Y + Math.Max(0, bounds.Height / 2) };
            IntPtr handle = MonitorFromPoint(point, MonitorDefaultToNearest);
            int dpiX = 96, dpiY = 96;
            if (handle != IntPtr.Zero && GetDpiForMonitor(handle, MonitorDpiTypeEffective, out uint dx, out uint dy) == 0)
            { dpiX = (int)dx; dpiY = (int)dy; }
            output.Add(new MonitorPlacementInfo(screen.DeviceName, bounds, work, dpiX, dpiY, screen.Primary));
        }
        return output;
    }

    public static PixelRect Clamp(PixelRect rect, PixelRect work)
    {
        int width = Math.Clamp(rect.Width, 1, work.Width);
        int height = Math.Clamp(rect.Height, 1, work.Height);
        int x = Math.Clamp(rect.X, work.X, work.Right - width);
        int y = Math.Clamp(rect.Y, work.Y, work.Bottom - height);
        return new PixelRect(x, y, width, height);
    }

    public static PixelRect Center(int width, int height, PixelRect work)
    {
        width = Math.Clamp(width, 1, work.Width);
        height = Math.Clamp(height, 1, work.Height);
        return new PixelRect(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height);
    }

    private static PixelRect CalculateInitialBounds(MonitorPlacementInfo monitor, double widthDip, double heightDip,
        string savedDeviceName, PixelRect savedBounds)
    {
        if (savedBounds.IsUsable && string.Equals(savedDeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase))
            return Clamp(savedBounds, monitor.WorkArea);
        int dpiX = Math.Max(96, monitor.DpiX), dpiY = Math.Max(96, monitor.DpiY);
        double maxWidthDip = monitor.WorkArea.Width * 96d / dpiX;
        double maxHeightDip = monitor.WorkArea.Height * 96d / dpiY;
        double minWidthDip = Math.Min(1100, maxWidthDip), minHeightDip = Math.Min(660, maxHeightDip);
        double width = Math.Clamp(ValidSize(widthDip, 1440), Math.Max(1, minWidthDip), Math.Max(1, maxWidthDip));
        double height = Math.Clamp(ValidSize(heightDip, 900), Math.Max(1, minHeightDip), Math.Max(1, maxHeightDip));
        return Center(Math.Max(1, (int)Math.Round(width * dpiX / 96d)), Math.Max(1, (int)Math.Round(height * dpiY / 96d)), monitor.WorkArea);
    }

    private static string MonitorDeviceForWindow(IntPtr window)
    {
        if (window == IntPtr.Zero) return "";
        IntPtr monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        return MonitorDeviceName(monitor);
    }

    private static string MonitorDeviceForPoint(NativePoint point) => MonitorDeviceName(MonitorFromPoint(point, MonitorDefaultToNearest));

    private static string MonitorDeviceName(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero) return "";
        var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>(), DeviceName = "" };
        return GetMonitorInfo(monitor, ref info) ? info.DeviceName ?? "" : "";
    }

    private static PixelRect FromNative(NativeRect rect) => new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    private static double Number(JsonElement obj, string name, double fallback) => obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : fallback;
    private static int Integer(JsonElement obj, string name) => obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;
    private static string Text(JsonElement obj, string name) => obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static double ValidSize(double size, double fallback) => double.IsFinite(size) && size > 0 ? size : fallback;
    private static double DistanceSquared(PixelRect a, PixelRect b)
    {
        int ax = a.X + a.Width / 2, ay = a.Y + a.Height / 2;
        int bx = Math.Clamp(ax, b.X, b.Right), by = Math.Clamp(ay, b.Y, b.Bottom);
        long dx = (long)ax - bx, dy = (long)ay - by;
        return (double)(dx * dx + dy * dy);
    }
}
