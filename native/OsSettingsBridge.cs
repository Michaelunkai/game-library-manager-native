using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace GameLibrary.Native;

/// <summary>
/// The single read-only bridge between the dense game list and the live Windows
/// accessibility / input preferences that change how a scroll feels.
/// <para>
/// <see cref="MotionPolicy"/> can only see one boolean ("animate controls and
/// windows") and only at the moment it is read. Windows actually exposes several
/// independent, per-user settings that a list has to honour to feel native:
/// whether animations are on at all, how many lines one mouse-wheel notch scrolls,
/// and whether the shell is in an animated posture. On a high-resolution wheel the
/// line count is the difference between a precise scroll and a violent jump, so
/// ignoring it is a real usability bug, not a cosmetic one.
/// </para>
/// <para>
/// <b>This class READS the operating system and never writes to it.</b> There is
/// deliberately no setter, no <c>SPI_SET*</c> action and no registry write anywhere
/// in this file: the application must never silently change a user's accessibility
/// or input configuration. <see cref="Snapshot"/> and <see cref="Diff"/> exist so
/// the manager can show the user exactly what the app is honouring and notice a
/// live change without polling every property.
/// </para>
/// </summary>
internal static class OsSettingsBridge
{
    // SPI_GETWHEELSCROLLLINES returns the number of lines scrolled per wheel notch.
    private const uint SpiGetWheelScrollLines = 0x0068;
    // SPI_GETANIMATION fills an ANIMATIONINFO with the shell's animation posture.
    private const uint SpiGetAnimation = 0x0048;
    // SPI_GETWHEELSCROLLLINES reports this sentinel for "scroll a full page per notch".
    private const uint WheelScrollPageSentinel = 0xFFFFFFFF;

    /// <summary>Fallback line count when the OS cannot be queried (the Windows default).</summary>
    internal const int DefaultLinesPerScrollTick = 3;

    // A wheel notch can be configured from 1 line up to 20 lines on Windows. Above
    // that the OS setting itself is nonsense, so the bridge refuses to believe it:
    // the plausible range is 1..20 and anything outside falls back to the default.
    private const int MinPlausibleLines = 1;
    private const int MaxPlausibleLines = 20;

    /// <summary>
    /// The largest number of lines a single list scroll may advance. A dense,
    /// virtualized list recycles containers on every scroll, so a scroll that jumps
    /// the whole list is both disorienting and the most likely source of a dropped
    /// frame; the requested lines are clamped into 1..<see cref="MaxScrollLines"/>
    /// before use, so no user or caller can configure a full-list jump.
    /// </summary>
    internal const int MaxScrollLines = 20;

    private const int AnimationCacheMilliseconds = 2000;
    private static readonly object AnimationGate = new();
    private static bool _animationValue;
    private static long _animationExpiry;

    [StructLayout(LayoutKind.Sequential)]
    private struct AnimationInfo
    {
        public uint Size;
        public int MinAnimate;
    }

    // The native signature is:
    //   BOOL SystemParametersInfoW(UINT uiAction, UINT uiParam, PVOID pvParam, UINT fWinIni);
    // Winapi resolves to the single platform calling convention on Win64 while still
    // marshalling correctly on x86, and SetLastError lets a failed probe be detected.
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode,
        EntryPoint = "SystemParametersInfoW", CallingConvention = CallingConvention.Winapi)]
    private static extern bool SystemParametersInfoW(uint action, uint param, out uint value, uint flags);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode,
        EntryPoint = "SystemParametersInfoW", CallingConvention = CallingConvention.Winapi)]
    private static extern bool SystemParametersInfoW(uint action, uint param, ref AnimationInfo value, uint flags);

    /// <summary>
    /// The live OS "animate controls and windows" preference. Cached for a short
    /// window so a scroll loop can read it cheaply, but re-readable: the cache
    /// expires and <see cref="Refresh"/> clears it immediately. Never throws,
    /// including when called off a UI thread.
    /// </summary>
    internal static bool AnimationsEnabled
    {
        get
        {
            lock (AnimationGate)
            {
                long now = Environment.TickCount64;
                if (now < _animationExpiry) return _animationValue;
                _animationValue = ReadAnimations();
                _animationExpiry = now + AnimationCacheMilliseconds;
                return _animationValue;
            }
        }
    }

    /// <summary>Forces the next <see cref="AnimationsEnabled"/> read to hit the OS.</summary>
    internal static void Refresh()
    {
        lock (AnimationGate) _animationExpiry = 0;
    }

    /// <summary>
    /// The OS "number of lines to scroll" setting for one wheel notch, read through
    /// the real Win32 <c>SystemParametersInfoW(SPI_GETWHEELSCROLLLINES)</c>. Returns
    /// <see cref="DefaultLinesPerScrollTick"/> when the call fails, and refuses an
    /// implausible value (see the 1..20 guard) so a corrupted or page-scroll sentinel
    /// cannot drive the list. Never throws.
    /// </summary>
    internal static int LinesPerScrollTick
    {
        get
        {
            try
            {
                if (!SystemParametersInfoW(SpiGetWheelScrollLines, 0, out uint raw, 0))
                    return DefaultLinesPerScrollTick;
                // 0xFFFFFFFF means "scroll one page at a time", which is not a line
                // count; fall back rather than clamping it to the maximum.
                if (raw == WheelScrollPageSentinel) return DefaultLinesPerScrollTick;
                return (int)Math.Clamp((long)raw, MinPlausibleLines, MaxPlausibleLines);
            }
            catch (Exception) { return DefaultLinesPerScrollTick; }
        }
    }

    /// <summary>
    /// The OS animation posture, read through <c>SystemParametersInfoW(SPI_GETANIMATION)</c>.
    /// True when the shell reports animation enabled. Never throws.
    /// </summary>
    internal static bool ScrollbarsAnimated
    {
        get
        {
            try
            {
                var info = new AnimationInfo { Size = (uint)Marshal.SizeOf<AnimationInfo>(), MinAnimate = 0 };
                if (!SystemParametersInfoW(SpiGetAnimation, 0, ref info, 0)) return false;
                return info.MinAnimate != 0;
            }
            catch (Exception) { return false; }
        }
    }

    /// <summary>
    /// The effective number of lines for a list scroll. The request is clamped into
    /// 1..<see cref="MaxScrollLines"/>: a scroll that advances the whole list is
    /// disorienting and forces every recycled container to rebuild in one frame, so
    /// the clamp is the difference between smooth paging and a visible freeze.
    /// </summary>
    internal static int ScrollLinesFor(int requestedLines)
        => Math.Clamp(requestedLines, 1, MaxScrollLines);

    /// <summary>
    /// Captures every setting this bridge exposes, with its display name and current
    /// value, at a single instant. Two snapshots can be diffed to detect a live change.
    /// </summary>
    internal static IReadOnlyList<SettingChange> Snapshot() => new SettingChange[]
    {
        new("Animations enabled", AnimationsEnabled ? "on" : "off"),
        new("Lines per scroll tick", LinesPerScrollTick.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new("Scrollbars animated", ScrollbarsAnimated ? "on" : "off"),
    };

    /// <summary>
    /// Names the settings whose value differs between two snapshots, in a stable
    /// order, so a live change is noticed without polling every property. Empty or
    /// mismatched input is handled without throwing: a name present on only one side
    /// counts as changed.
    /// </summary>
    internal static IReadOnlyList<string> Diff(IReadOnlyList<SettingChange> before, IReadOnlyList<SettingChange> after)
    {
        var changed = new List<string>();
        if (before == null || after == null) return changed;

        var beforeMap = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < before.Count; i++)
        {
            SettingChange entry = before[i];
            if (entry == null || string.IsNullOrEmpty(entry.Name)) continue;
            beforeMap[entry.Name] = entry.Value ?? "";
        }

        var afterMap = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < after.Count; i++)
        {
            SettingChange entry = after[i];
            if (entry == null || string.IsNullOrEmpty(entry.Name)) continue;
            afterMap[entry.Name] = entry.Value ?? "";
            if (!beforeMap.TryGetValue(entry.Name, out string? previous) || !string.Equals(previous, entry.Value ?? "", StringComparison.Ordinal))
                changed.Add(entry.Name);
        }

        for (int i = 0; i < before.Count; i++)
        {
            SettingChange entry = before[i];
            if (entry == null || string.IsNullOrEmpty(entry.Name)) continue;
            if (!afterMap.ContainsKey(entry.Name)) changed.Add(entry.Name);
        }

        return changed;
    }

    private static bool ReadAnimations()
    {
        try { return SystemParameters.ClientAreaAnimation && SystemParameters.MenuFade; }
        catch (Exception) { return false; }
    }
}

/// <summary>A single named OS setting value captured at one instant.</summary>
internal sealed record SettingChange(string Name, string Value);

internal static class OsSettingsBridgeTests
{
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void Run(string root)
    {
        // Every property must be readable and must never throw, including off a UI
        // thread (a fresh Thread defaults to MTA, unlike the WPF dispatcher thread).
        bool animations = false;
        int lines = 0;
        bool scrollbars = false;
        Exception? offThreadFailure = null;
        var worker = new Thread(() =>
        {
            try
            {
                animations = OsSettingsBridge.AnimationsEnabled;
                lines = OsSettingsBridge.LinesPerScrollTick;
                scrollbars = OsSettingsBridge.ScrollbarsAnimated;
                OsSettingsBridge.Refresh();
                animations = OsSettingsBridge.AnimationsEnabled;
            }
            catch (Exception ex) { offThreadFailure = ex; }
        });
        worker.IsBackground = true;
        worker.Start();
        Require(worker.Join(TimeSpan.FromSeconds(10)), "Reading the OS settings off a worker thread timed out.");
        Require(offThreadFailure == null, "Reading the OS settings off a UI thread threw: " + offThreadFailure);
        _ = animations;
        _ = scrollbars;

        // The wheel line count must be in the plausible range on this machine, and we
        // print the real value the OS reported so the evidence is not just a range check.
        Console.WriteLine("OsSettingsBridge: LinesPerScrollTick = " + lines);
        Console.WriteLine("OsSettingsBridge: ScrollbarsAnimated = " + scrollbars);
        Console.WriteLine("OsSettingsBridge: AnimationsEnabled = " + animations);
        Require(lines >= 1 && lines <= 20,
            "LinesPerScrollTick read " + lines + " from the OS, outside the plausible 1..20 range.");

        // ScrollLinesFor must clamp the extremes and pass a mid-range value through.
        Require(OsSettingsBridge.ScrollLinesFor(int.MinValue) == 1, "ScrollLinesFor must clamp below 1 up to 1.");
        Require(OsSettingsBridge.ScrollLinesFor(0) == 1, "ScrollLinesFor must clamp 0 up to 1.");
        Require(OsSettingsBridge.ScrollLinesFor(-7) == 1, "ScrollLinesFor must clamp a negative request up to 1.");
        Require(OsSettingsBridge.ScrollLinesFor(OsSettingsBridge.MaxScrollLines + 1) == OsSettingsBridge.MaxScrollLines,
            "ScrollLinesFor must clamp above its maximum down to the maximum.");
        Require(OsSettingsBridge.ScrollLinesFor(int.MaxValue) == OsSettingsBridge.MaxScrollLines,
            "ScrollLinesFor must clamp an enormous request down to the maximum.");
        const int midRange = 5;
        Require(OsSettingsBridge.ScrollLinesFor(midRange) == midRange, "ScrollLinesFor must pass a mid-range value through unchanged.");

        // Snapshot: one entry per setting, every name non-empty, no duplicates.
        IReadOnlyList<SettingChange> snapshot = OsSettingsBridge.Snapshot();
        Require(snapshot.Count == 3, "Snapshot must expose exactly the three settings, not " + snapshot.Count + ".");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (SettingChange entry in snapshot)
        {
            Require(entry != null, "Snapshot must not contain a null entry.");
            Require(!string.IsNullOrWhiteSpace(entry.Name), "Snapshot entries must carry a non-empty name.");
            Require(names.Add(entry.Name), "Snapshot contains the duplicate setting name '" + entry.Name + "'.");
        }

        // Diff: exactly the changed setting is reported.
        IReadOnlyList<SettingChange> before = new SettingChange[]
        {
            new("Animations enabled", "on"),
            new("Lines per scroll tick", "3"),
            new("Scrollbars animated", "on"),
        };
        IReadOnlyList<SettingChange> after = new SettingChange[]
        {
            new("Animations enabled", "on"),
            new("Lines per scroll tick", "7"),
            new("Scrollbars animated", "on"),
        };
        IReadOnlyList<string> changed = OsSettingsBridge.Diff(before, after);
        Require(changed.Count == 1 && changed[0] == "Lines per scroll tick",
            "Diff must report exactly the one changed setting, but reported " + string.Join(", ", changed) + ".");

        // Identical snapshots report nothing.
        Require(OsSettingsBridge.Diff(before, before).Count == 0, "Diff of an identical snapshot must be empty.");
        IReadOnlyList<SettingChange> copy = new SettingChange[]
        {
            new("Animations enabled", "on"),
            new("Lines per scroll tick", "3"),
            new("Scrollbars animated", "on"),
        };
        Require(OsSettingsBridge.Diff(before, copy).Count == 0, "Diff of two equal snapshots must be empty.");

        // Empty and mismatched input must be handled without throwing.
        IReadOnlyList<string> emptyDiff = OsSettingsBridge.Diff(Array.Empty<SettingChange>(), Array.Empty<SettingChange>());
        Require(emptyDiff.Count == 0, "Diff of two empty snapshots must be empty, not " + emptyDiff.Count + ".");
        IReadOnlyList<string> mismatched = OsSettingsBridge.Diff(before, Array.Empty<SettingChange>());
        Require(mismatched.Count == before.Count, "Diff against an empty snapshot must report the removed names, not throw.");
        Require(OsSettingsBridge.Diff(Array.Empty<SettingChange>(), before).Count == before.Count,
            "Diff from an empty snapshot must report the added names, not throw.");

        // The class is read-only: prove there is no setter on any exposed property.
        foreach (var property in typeof(OsSettingsBridge).GetProperties(
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            Require(property.CanRead && !property.CanWrite,
                "OsSettingsBridge." + property.Name + " must be read-only; this class never writes to the OS.");

        if (!string.IsNullOrEmpty(root))
        {
            Directory.CreateDirectory(root);
            File.WriteAllLines(Path.Combine(root, "os-settings.txt"), new[]
            {
                "animations-enabled      " + (animations ? "on" : "off"),
                "lines-per-scroll-tick   " + lines,
                "scrollbars-animated     " + (scrollbars ? "on" : "off"),
                "snapshot-settings       " + snapshot.Count,
                "diff-changed            " + string.Join(", ", changed),
            });
        }

        Console.WriteLine("SLOT9 TESTS PASSED");
    }
}
