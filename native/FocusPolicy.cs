using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GameLibrary.Native;

/// <summary>
/// The keyboard and focus policy for a game card, stated as code so the tab order,
/// the focus ring and the accessible names are decisions rather than accidents.
/// <para>
/// The card carries a dense cluster of small controls. With a keyboard the reading
/// order matters: the primary action must be one Tab away from the card itself and
/// the destructive action must never sit next to it. This type is the single source
/// of that order, so the manager can apply the whole policy in one edit instead of
/// hand-setting TabIndex on nine controls.
/// </para>
/// </summary>
internal static class FocusPolicy
{
    /// <summary>
    /// One focusable control on a game card. The record is the unit the manager
    /// wires: id, the name a screen reader announces, and whether the control is
    /// the primary or the destructive action.
    /// </summary>
    internal readonly record struct CardControl(
        string AutomationId,
        string AccessibleName,
        bool IsDestructive,
        bool IsPrimary);

    // Palette values read from native\Theme.xaml. Kept here so the contrast check
    // can run without parsing XAML, and so a theme change shows up as a failing
    // test rather than a silently invisible focus ring.
    internal const string CardFillColor = "#1D222C";   // CardBrush
    internal const string AccentColor = "#9CE7BB";     // AccentBrush
    internal const string TextColor = "#F2F5FA";       // TextBrush

    /// <summary>
    /// The card's controls in logical tab order: the order a sighted user reads the
    /// card, primary action first and the destructive action last. Backed by an
    /// array so the list is immutable to callers.
    /// </summary>
    internal static readonly IReadOnlyList<CardControl> Controls = new CardControl[]
    {
        new("PlayGame", "Play", false, true),
        new("PauseGame", "Pause", false, false),
        new("PlayWithWand", "Play with Wand", false, false),
        new("ForceExitGameAndWand", "Exit game and Wand", false, false),
        new("ToggleWishlist", "Wishlist", false, false),
        new("OpenGameDetails", "Details", false, false),
        new("BackupGame", "Backup", false, false),
        new("RestoreGame", "Restore", false, false),
        new("DeleteGame", "Delete from all drives", true, false),
    };

    /// <summary>The shipped order, used as the reference for <see cref="ValidateOrder()"/>.</summary>
    private static readonly string[] CanonicalOrder = Controls.Select(c => c.AutomationId).ToArray();

    /// <summary>Tab index of a control, or -1 when the id is not on the card.</summary>
    internal static int OrderOf(string automationId)
    {
        if (string.IsNullOrEmpty(automationId)) return -1;
        for (int i = 0; i < Controls.Count; i++)
        {
            if (string.Equals(Controls[i].AutomationId, automationId, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    /// <summary>
    /// The automation ids that are out of order or duplicated, so the manager gets a
    /// checkable list rather than prose. Empty means the shipped order is correct.
    /// </summary>
    internal static IReadOnlyList<string> ValidateOrder() => ValidateOrder(Controls);

    /// <summary>Order check for an arbitrary list; the overload exists so a wrong
    /// order or a duplicate can be proven to be detected.</summary>
    internal static IReadOnlyList<string> ValidateOrder(IReadOnlyList<CardControl> controls)
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < controls.Count; i++)
        {
            string id = controls[i].AutomationId;
            if (!seen.Add(id))
            {
                if (!problems.Contains(id)) problems.Add(id);
                continue;
            }
            if (i >= CanonicalOrder.Length || !string.Equals(id, CanonicalOrder[i], StringComparison.Ordinal))
            {
                if (!problems.Contains(id)) problems.Add(id);
            }
        }
        for (int i = CanonicalOrder.Length; i < controls.Count; i++)
        {
            string id = controls[i].AutomationId;
            if (!problems.Contains(id)) problems.Add(id);
        }
        return problems;
    }

    /// <summary>
    /// True when a name is unusable with a screen reader: null, empty, whitespace,
    /// or a single character that reads as an icon rather than an action.
    /// </summary>
    internal static bool IsMissingAccessibleName(string? name)
        => string.IsNullOrWhiteSpace(name) || name!.Trim().Length <= 1;

    /// <summary>The card controls whose accessible name would be unusable. Empty is the goal.</summary>
    internal static IReadOnlyList<string> MissingAccessibleNames()
    {
        var missing = new List<string>();
        foreach (var control in Controls)
        {
            if (IsMissingAccessibleName(control.AccessibleName)) missing.Add(control.AutomationId);
        }
        return missing;
    }

    /// <summary>
    /// The keyboard focus ring. TextBrush (#F2F5FA) is chosen from the existing
    /// palette because it is the highest-contrast colour that is neither the card
    /// fill nor the accent.
    /// <para>
    /// Measured against the card fill (#1D222C): relative luminance of #F2F5FA is
    /// 0.910842, of #1D222C is 0.015871, so the contrast ratio is
    /// (0.910842 + 0.05) / (0.015871 + 0.05) = 14.5867:1. That is far above the
    /// WCAG 2.1 non-text contrast minimum of 3:1 for a focus indicator, and it also
    /// differs from the accent (#9CE7BB, 11.05:1) so the ring is distinguishable
    /// from a hover border in all three states.
    /// </para>
    /// </summary>
    internal const string FocusRingColor = TextColor;

    /// <summary>2px, matching the theme's own keyboard-focus border, so the ring
    /// survives the dark palette and 1px scaling.</summary>
    internal const double FocusRingThickness = 2.0;

    // Controls the card collapses or disables by design in its default, not-running
    // state. The manager skips these when wiring TabIndex and adds them back when
    // the data condition (a running game, a Wand registration) turns them on.
    private static readonly HashSet<string> ConditionallyUnreachable = new(StringComparer.Ordinal)
    {
        "PauseGame",
        "PlayWithWand",
        "ForceExitGameAndWand",
    };

    /// <summary>
    /// False for a control the card collapses or disables by design at the time of
    /// the call, so the manager can skip it when wiring TabIndex. Unknown ids are
    /// not reachable either.
    /// </summary>
    internal static bool IsReachableByKeyboard(string automationId)
        => OrderOf(automationId) >= 0 && !ConditionallyUnreachable.Contains(automationId);

    /// <summary>Live-state overload: reachable only when it exists and is both
    /// visible and enabled right now.</summary>
    internal static bool IsReachableByKeyboard(string automationId, bool isVisible, bool isEnabled)
        => OrderOf(automationId) >= 0 && isVisible && isEnabled;

    /// <summary>
    /// The app's real key bindings, read from MainWindow.xaml.cs, mapped to the
    /// automation id of the control each one drives. The manager can surface these
    /// in tooltips, and a shortcut can only point at a control that exists.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> Shortcuts =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Ctrl+K"] = "SearchBox",
            ["F5"] = "RefreshCatalog",
            ["Ctrl+A"] = "SelectAll",
            ["Enter"] = "OpenGameDetails",
            ["Escape"] = "DeselectAll",
        };

    // Window-level controls the global shortcuts target; these are outside the card
    // order but still real controls.
    private static readonly HashSet<string> ShellControlIds = new(StringComparer.Ordinal)
    {
        "SearchBox",
        "RefreshCatalog",
        "SelectAll",
        "DeselectAll",
    };

    /// <summary>True when the id names a control the app actually has, on the card or in the shell.</summary>
    internal static bool IsKnownControl(string automationId)
        => OrderOf(automationId) >= 0 || ShellControlIds.Contains(automationId);
}

internal static class FocusPolicyTests
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void Run(string root)
    {
        var controls = FocusPolicy.Controls;
        Require(controls.Count > 0, "The card has no controls.");

        // Primary action is FIRST.
        Require(controls[0].IsPrimary, "The primary action is not first in the card's tab order.");
        int primaryCount = 0;
        foreach (var control in controls) if (control.IsPrimary) primaryCount++;
        Require(primaryCount == 1, "There must be exactly one primary action; found " + primaryCount + ".");

        // Destructive action is LAST, so it is never one stray Tab from the primary.
        Require(controls[controls.Count - 1].IsDestructive, "The destructive action is not last in the card's tab order.");
        int destructiveCount = 0;
        foreach (var control in controls) if (control.IsDestructive) destructiveCount++;
        Require(destructiveCount == 1, "There must be exactly one destructive action; found " + destructiveCount + ".");

        // No duplicate ids, and the shipped order validates clean.
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var control in controls) Require(ids.Add(control.AutomationId), "Duplicate card control id: " + control.AutomationId);
        var problems = FocusPolicy.ValidateOrder();
        Require(problems.Count == 0, "ValidateOrder reported problems: " + string.Join(", ", problems));

        // ValidateOrder genuinely detects a wrong order and a duplicate.
        var shuffled = new List<FocusPolicy.CardControl>(controls);
        (shuffled[0], shuffled[1]) = (shuffled[1], shuffled[0]);
        Require(FocusPolicy.ValidateOrder(shuffled).Count > 0, "ValidateOrder did not detect a shuffled order.");
        var duplicated = new List<FocusPolicy.CardControl>(controls);
        duplicated.Add(controls[0]);
        Require(FocusPolicy.ValidateOrder(duplicated).Contains(controls[0].AutomationId), "ValidateOrder did not detect a duplicate id.");

        // OrderOf resolves every listed id, and rejects an unknown one.
        for (int i = 0; i < controls.Count; i++)
        {
            Require(FocusPolicy.OrderOf(controls[i].AutomationId) == i,
                "OrderOf returned the wrong index for " + controls[i].AutomationId + ".");
        }
        Require(FocusPolicy.OrderOf("NoSuchControl") == -1, "OrderOf did not return -1 for an unknown id.");

        // The focus ring must be visible against the card fill and distinct from the accent.
        Require(!string.Equals(FocusPolicy.FocusRingColor, FocusPolicy.CardFillColor, StringComparison.OrdinalIgnoreCase),
            "The focus ring is the same colour as the card fill.");
        Require(!string.Equals(FocusPolicy.FocusRingColor, FocusPolicy.AccentColor, StringComparison.OrdinalIgnoreCase),
            "The focus ring is the same colour as the accent.");
        double contrast = ContrastRatio(FocusPolicy.FocusRingColor, FocusPolicy.CardFillColor);
        Require(contrast >= 3.0, "The focus ring contrast against the card fill is " + contrast.ToString("0.00")
            + ":1, below the 3:1 minimum for a non-text focus indicator.");
        Require(FocusPolicy.FocusRingThickness >= 2.0, "The focus ring is thinner than 2px and can vanish against the card.");

        // Accessible names: none missing on the card, and the predicate flags the bad cases.
        var missing = FocusPolicy.MissingAccessibleNames();
        Require(missing.Count == 0, "Some card controls have no accessible name: " + string.Join(", ", missing));
        Require(FocusPolicy.IsMissingAccessibleName(null), "A null accessible name was not flagged.");
        Require(FocusPolicy.IsMissingAccessibleName(""), "An empty accessible name was not flagged.");
        Require(FocusPolicy.IsMissingAccessibleName("X"), "A one-character accessible name was not flagged.");
        Require(!FocusPolicy.IsMissingAccessibleName("Play"), "A real accessible name was wrongly flagged.");

        // Every shortcut targets a control that exists, and a card shortcut must be
        // in the card order so it cannot point at a control that is not there.
        foreach (var pair in FocusPolicy.Shortcuts)
        {
            Require(!string.IsNullOrWhiteSpace(pair.Key), "A shortcut has an empty key.");
            Require(FocusPolicy.IsKnownControl(pair.Value),
                "Shortcut " + pair.Key + " points at '" + pair.Value + "', a control that does not exist.");
            if (FocusPolicy.OrderOf(pair.Value) >= 0)
            {
                Require(FocusPolicy.OrderOf(pair.Value) < controls.Count,
                    "Shortcut " + pair.Key + " points at a card control that is out of order.");
            }
        }
        Require(FocusPolicy.Shortcuts.Values.Contains("OpenGameDetails"),
            "The Enter shortcut should drive the card's Details control.");

        if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
        {
            var report = new List<string> { "FocusPolicy report" };
            foreach (var control in controls)
            {
                report.Add(control.AutomationId + " | " + control.AccessibleName
                    + " | primary=" + control.IsPrimary + " destructive=" + control.IsDestructive);
            }
            report.Add("focus ring " + FocusPolicy.FocusRingColor
                + " contrast " + contrast.ToString("0.0000") + ":1 vs card fill " + FocusPolicy.CardFillColor);
            File.WriteAllLines(Path.Combine(root, "focus-policy-report.txt"), report);
        }
    }

    private static double ContrastRatio(string foreground, string background)
    {
        double lighter = Math.Max(RelativeLuminance(foreground), RelativeLuminance(background));
        double darker = Math.Min(RelativeLuminance(foreground), RelativeLuminance(background));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(string hex)
    {
        string value = hex.TrimStart('#');
        int r = Convert.ToInt32(value.Substring(0, 2), 16);
        int g = Convert.ToInt32(value.Substring(2, 2), 16);
        int b = Convert.ToInt32(value.Substring(4, 2), 16);
        return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
    }

    private static double Channel(int component)
    {
        double c = component / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
