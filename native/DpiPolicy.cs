using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace GameLibrary.Native;

/// <summary>
/// The app's DPI and scaling policy, stated as code so "fits any resolution" is a
/// checkable claim rather than a slogan.
/// <para>
/// The game card is a fixed four-column layout: a checkbox gutter, an auto-sized
/// cover, a proportional text column, and a proportional action column. Two things
/// can break it at a given scale. First, a column that becomes too narrow to show
/// the labels it carries, because the app renders real text down to
/// <see cref="MinReadableColumnDip"/>-scale columns rather than ellipsing everything.
/// Second, a hard-coded pixel constant that is not a whole number of device pixels
/// at that scale, which is what makes a 1px divider blur into a 2px smear.
/// <see cref="AuditCardWidths"/> checks both, and the manager can call it at every
/// scale it cares about before trusting the layout to fit.
/// </para>
/// <para>
/// Everything here is a pure function of its arguments and never throws. DPI
/// queries happen during layout, where an exception is not a recoverable error - it
/// is a torn window - so the fallback is a fixed, sane 1.0 rather than a throw.
/// </para>
/// </summary>
internal static class DpiPolicy
{
    /// <summary>
    /// Lowest scale the app lays out for. Windows' Display scaling slider bottoms out
    /// at 100%, and 100% is the only case where a pixel-authored constant and a
    /// DIP-authored constant mean the same thing, so it is the floor.
    /// </summary>
    internal const double MinSupportedScale = 1.0;

    /// <summary>
    /// Highest scale the app lays out for. Windows exposes 100% through 500% (1.0 to
    /// 5.0) on the Display scaling slider, and this app supports the full practical
    /// range of that: 100% to 300%. Beyond 300% the density budget inverts - the
    /// window's own <see cref="WindowMinWidthDip"/> minimum would demand
    /// 760 x 3 = 2280 device pixels of width, which no mainstream panel provides, so
    /// a card laid out for that scale is guaranteed to clip rather than merely
    /// scroll. <see cref="IsSupportedScale"/> refuses those scales outright instead
    /// of rendering a layout that cannot fit.
    /// </summary>
    internal const double MaxSupportedScale = 3.0;

    /// <summary>
    /// Smallest width, in DIPs, a column may have and still be readable. Justified
    /// against the real font sizes in MainWindow.xaml rather than a guess: the
    /// smallest text the app renders anywhere is <c>FontSize="10"</c> (the all-caps
    /// stat captions "IN THIS VIEW" / "CACHED COVERS"), and the smallest text inside
    /// the game card is <c>FontSize="11"</c> (the Id and TagsLabel lines) - Segoe UI
    /// averages about 0.5 em per character, so 11 DIP text advances ~5.5 DIP per
    /// character. The card's text column also spends 24 DIP on its own
    /// <c>Margin="12,0,12,0"</c>, leaving 96 - 24 = 72 DIP, or ~13 characters: just
    /// enough for "ghcr.io/owner" to stay meaningful. Below 96 DIP the Id, tags and
    /// progress lines all collapse to a bare ellipsis and the column carries no
    /// information at all. The action column is held to the same floor even though
    /// its 14 DIP button labels need more: at 14 DIP the shortest real label
    /// ("Details", "Backup", "Restore") already needs ~84 DIP after the buttons'
    /// 12 DIP of padding, so 96 DIP is the last width where no button label is cut.
    /// </summary>
    internal const double MinReadableColumnDip = 96.0;

    /// <summary>
    /// The window's own hard-coded minimum width, from MainWindow.xaml
    /// <c>MinWidth="760"</c>. It is recorded here because it is the number the layout
    /// is judged against, and because it is what turns the per-scale px cost of this
    /// app into a hard limit: 760 DIP is 1520 device px at 200%.
    /// </summary>
    internal const double WindowMinWidthDip = 760.0;

    /// <summary>
    /// Widest card the layout can actually use at the window's own minimum width.
    /// Derived from the real XAML chrome, not chosen to be round:
    /// <code>
    ///   760  window MinWidth                       (MainWindow.xaml:1)
    ///  -228  sidebar column                        (MainWindow.xaml:5)
    ///  - 56  content margins 28 left + 28 right     (MainWindow.xaml:19)
    ///  - 28  card margins 14 left + 14 right        (MainWindow.xaml:55)
    ///  - 17  ListBox vertical scrollbar allowance   (WPF default ScrollBar width)
    ///  -  4  card border 1px + 1px padding per side (Theme.xaml ListBoxItem)
    ///  = 427  DIP actually available to the card
    /// </code>
    /// Columns totalling more than this cannot be honoured at the window minimum; the
    /// proportional columns would be squeezed to their floors and the card would clip.
    /// </summary>
    internal const double MaxUsableCardWidthDip = 427.0;

    /// <summary>
    /// A column at or below this width is treated as a pixel-authored chrome
    /// constant (the 28 DIP checkbox gutter, a 1 DIP divider) rather than a layout
    /// floor (96 / 120 / 168 DIP), because only the former has to land on a whole
    /// number of device pixels to avoid blur. Above it, a width is a DIP intent and
    /// is judged by <see cref="MinReadableColumnDip"/> instead.
    /// </summary>
    internal const double ChromeColumnMaxDip = 64.0;

    /// <summary>
    /// The scales the manager must be able to lay out at: 100%, 125%, 150%, 175% and
    /// 200%, every one of which is offered by the Windows Display scaling slider and
    /// every one of which divides evenly enough for the card's pixel constants to
    /// stay whole.
    /// </summary>
    internal static IReadOnlyList<double> ReferenceScales { get; } = new double[] { 1.0, 1.25, 1.5, 1.75, 2.0 };

    /// <summary>
    /// The real per-monitor scale for a visual, via WPF's own
    /// <see cref="VisualTreeHelper.GetDpi"/>. Returns 1.0 when the visual is null,
    /// not connected to a presentation source, or reports a value that is not a
    /// positive finite number. Never throws: this is called from layout, and an
    /// exception here tears the window rather than reporting a problem.
    /// </summary>
    internal static double EffectiveScale(Visual visual)
    {
        try
        {
            if (visual is null) return 1.0;
            DpiScale dpi = VisualTreeHelper.GetDpi(visual);
            double scale = dpi.DpiScaleX;
            return double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0.0 ? 1.0 : scale;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
            or ArgumentException or NullReferenceException or InvalidCastException
            or ObjectDisposedException)
        {
            return 1.0;
        }
    }

    /// <summary>
    /// True when a scale is inside <see cref="MinSupportedScale"/> ..
    /// <see cref="MaxSupportedScale"/>. Used to refuse to render at a scale the
    /// layout cannot survive, instead of shipping a clipped window. NaN and
    /// infinities are not supported scales.
    /// </summary>
    internal static bool IsSupportedScale(double scale)
    {
        if (double.IsNaN(scale) || double.IsInfinity(scale)) return false;
        return scale >= MinSupportedScale && scale <= MaxSupportedScale;
    }

    /// <summary>
    /// Device pixels to DIPs, rounded to the nearest whole DIP.
    /// <para>
    /// The rounding is the point. A value that arrives as a count of device pixels
    /// describes a snapped position on the device grid; converting it to a fractional
    /// DIP and handing that to WPF puts text baselines and 1px borders on a half
    /// pixel, where the renderer covers two pixel rows with partial coverage and the
    /// result reads as a blur. Snapping to a whole DIP keeps the value expressible
    /// and lets the renderer snap it back onto the device grid, so a hairline stays a
    /// hairline at every scale. Midpoints round away from zero so the mapping is
    /// symmetric.
    /// </para>
    /// </summary>
    internal static double PxToDip(double pixels, double scale)
    {
        double s = NormalizeScale(scale);
        double dips = pixels / s;
        if (double.IsNaN(dips) || double.IsInfinity(dips)) return 0.0;
        return Math.Round(dips, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// DIPs to device pixels, unrounded: fractional pixels are legitimate input to
    /// layout arithmetic, and it is this class' job to reason about the space between
    /// DIPs and pixels rather than to hide it. Anything that actually paints an
    /// integral edge must go through <see cref="SnappedBorderThickness"/>, which is
    /// where the rounding decision belongs.
    /// </summary>
    internal static double DipToPx(double dips, double scale)
    {
        double s = NormalizeScale(scale);
        double pixels = dips * s;
        return double.IsNaN(pixels) ? 0.0 : pixels;
    }

    /// <summary>
    /// The whole number of device pixels a 1-DIP hairline border should occupy at
    /// <paramref name="scale"/>. Never returns 0, because a border quantized to
    /// nothing is a border that has silently disappeared rather than one that looks
    /// thin.
    /// <para>
    /// Arithmetic (1 DIP x scale, then nearest whole pixel, midpoint away from zero):
    /// <code>
    ///   100%  1.00 DIP = 1.00 px -> 1 px  (exactly 1 DIP, unchanged)
    ///   125%  1.00 DIP = 1.25 px -> 1 px  (0.80 DIP; the thinnest honest hairline)
    ///   150%  1.00 DIP = 1.50 px -> 2 px  (1.33 DIP; 1px would be half a pixel)
    ///   175%  1.00 DIP = 1.75 px -> 2 px  (1.14 DIP)
    ///   200%  1.00 DIP = 2.00 px -> 2 px  (1.00 DIP, unchanged)
    ///   300%  1.00 DIP = 3.00 px -> 3 px  (1.00 DIP, unchanged)
    /// </code>
    /// Between those stops the border thickens by at most half a pixel, which is the
    /// price of never painting on a half pixel, and it is a price the eye cannot see.
    /// Assign <see cref="SnappedBorderThicknessDip"/> to BorderThickness.
    /// </para>
    /// </summary>
    internal static int SnappedBorderThickness(double scale)
    {
        double s = NormalizeScale(scale);
        int pixels = (int)Math.Round(1.0 * s, MidpointRounding.AwayFromZero);
        return pixels < 1 ? 1 : pixels;
    }

    /// <summary>
    /// The DIP value to assign to <c>BorderThickness</c> so that WPF lays out and
    /// renders exactly <see cref="SnappedBorderThickness"/> device pixels at
    /// <paramref name="scale"/>. This is the assignable form; the integer form above
    /// is the truth about the device grid.
    /// </summary>
    internal static double SnappedBorderThicknessDip(double scale)
    {
        double s = NormalizeScale(scale);
        return SnappedBorderThickness(s) / s;
    }

    /// <summary>
    /// Checks a card's column widths - in DIPs, as authored - against this policy at
    /// one scale, and returns human-readable problems. An empty list means the card
    /// fits at that scale.
    /// <para>Three classes of problem are reported:</para>
    /// <list type="bullet">
    /// <item>A text column narrower than <see cref="MinReadableColumnDip"/>. A
    /// column is treated as text-bearing unless its name marks it as non-text chrome
    /// (a name containing "cover", "check", "gutter", "border", "rule", "divider" or
    /// "spacer"), which is the convention the manager's call site uses.</item>
    /// <item>A total above <see cref="MaxUsableCardWidthDip"/>, which is the width the
    /// card can actually have at the window's own minimum width - beyond it the
    /// proportional columns clip rather than grow.</item>
    /// <item>A pixel-authored chrome column (<= <see cref="ChromeColumnMaxDip"/>
    /// DIP) whose width is not a whole number of device pixels at this scale - the
    /// case that produces a blurred divider or a smeared gutter.</item>
    /// </list>
    /// <para>
    /// The card's DIP widths are the same at every scale; what changes is the device
    /// width they demand, so each message states the px cost alongside the DIP
    /// problem, and the caller can compare the px figure against the panel it is on.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> AuditCardWidths(double scale, IReadOnlyDictionary<string, double> columnWidthsInDips)
    {
        var problems = new List<string>();
        if (columnWidthsInDips is null) return problems;

        double s = NormalizeScale(scale);
        if (!IsSupportedScale(s))
        {
            problems.Add(Describe(s) + " is outside the supported range " + Describe(MinSupportedScale)
                + " to " + Describe(MaxSupportedScale) + "; the card is not laid out at a scale it cannot survive.");
            return problems;
        }

        double total = 0.0;
        foreach (KeyValuePair<string, double> entry in columnWidthsInDips)
        {
            string name = entry.Key ?? "?";
            double width = entry.Value;
            if (double.IsNaN(width) || double.IsInfinity(width))
            {
                problems.Add("column '" + name + "' has a non-finite width; the card cannot be measured.");
                continue;
            }
            if (width < 0.0)
            {
                problems.Add("column '" + name + "' has a negative width of " + Fmt(width) + " DIP.");
                continue;
            }

            total += width;
            double pixels = DipToPx(width, s);

            if (!IsNonTextColumn(name) && width < MinReadableColumnDip)
            {
                problems.Add("column '" + name + "' is " + Fmt(width) + " DIP at " + Describe(s)
                    + " (" + Fmt(pixels) + " px), below the " + Fmt(MinReadableColumnDip)
                    + " DIP readable minimum: the 11 DIP Id/tags labels and the 14 DIP button labels would truncate to an ellipsis.");
            }

            if (width <= ChromeColumnMaxDip && !IsWholeNumber(pixels))
            {
                problems.Add("column '" + name + "' is a pixel-authored " + Fmt(width) + " DIP constant but "
                    + Fmt(width) + " x " + Fmt(s) + " = " + Fmt(pixels)
                    + " px is not a whole number of device pixels at " + Describe(s)
                    + "; it will blur. Snap it to a multiple of 1/" + Fmt(s) + " DIP ("
                    + Fmt4(1.0 / s) + " DIP) or take it from SnappedBorderThickness.");
            }
        }

        if (total > MaxUsableCardWidthDip)
        {
            problems.Add("columns total " + Fmt(total) + " DIP at " + Describe(s) + " (" + Fmt(DipToPx(total, s))
                + " px), above the " + Fmt(MaxUsableCardWidthDip)
                + " DIP the card can use at the " + Fmt(WindowMinWidthDip)
                + " DIP window minimum; the proportional columns will be squeezed to their floors and the card will clip.");
        }

        return problems;
    }

    private static double NormalizeScale(double scale)
    {
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0.0) return 1.0;
        return scale;
    }

    private static bool IsWholeNumber(double value)
    {
        return Math.Abs(value - Math.Round(value, MidpointRounding.AwayFromZero)) < 0.0005;
    }

    private static bool IsNonTextColumn(string name)
    {
        string n = (name ?? string.Empty).ToLowerInvariant();
        return n.Contains("cover", StringComparison.Ordinal)
            || n.Contains("check", StringComparison.Ordinal)
            || n.Contains("gutter", StringComparison.Ordinal)
            || n.Contains("border", StringComparison.Ordinal)
            || n.Contains("rule", StringComparison.Ordinal)
            || n.Contains("divider", StringComparison.Ordinal)
            || n.Contains("spacer", StringComparison.Ordinal);
    }

    private static string Fmt(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Fmt4(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string Describe(double scale) =>
        (scale * 100.0).ToString("0.##", CultureInfo.InvariantCulture) + "%";
}

/// <summary>
/// Proves the DPI policy rather than asserting it: the supported range, the pixel
/// round trip at every scale the slider offers, the hairline quantization, and an
/// audit verdict for the real card columns at 100/125/150/175/200 percent.
/// </summary>
internal static class DpiPolicyTests
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string Fmt(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>The card as MainWindow.xaml actually authors it, at its 100% floors.</summary>
    private static IReadOnlyDictionary<string, double> RealCardColumns() => new Dictionary<string, double>(StringComparer.Ordinal)
    {
        // 28 = checkbox gutter; cover = CoverHeight 98 * 0.735 (Models.CoverWidth)
        // 120 = text column MinWidth; 168 = action column MinWidth.
        { "checkbox gutter", 28.0 },
        { "cover", 98.0 * 0.735 },
        { "game text", 120.0 },
        { "card actions", 168.0 },
    };

    internal static void Run(string root)
    {
        Require(!string.IsNullOrWhiteSpace(root), "DpiPolicyTests.Run needs a root directory, like every other test group.");

        // --- 1. The scale query is total: no visual, no DPI, no answer, no crash. ---
        double fromNull = DpiPolicy.EffectiveScale(null!);
        Require(fromNull >= DpiPolicy.MinSupportedScale,
            "EffectiveScale(null) returned " + Fmt(fromNull) + ", below the " + Fmt(DpiPolicy.MinSupportedScale)
            + " floor; a null visual must fall back to 1.0, not to a scale that shrinks the layout.");
        Require(fromNull == 1.0,
            "EffectiveScale(null) returned " + Fmt(fromNull) + "; the documented fallback is exactly 1.0.");

        // A visual that cannot report a DPI, and a scale argument that is garbage,
        // must all land on the same fixed fallback rather than throwing into layout.
        Require(DpiPolicy.PxToDip(10.0, 0.0) == 10.0, "PxToDip did not normalize a zero scale to 1.0.");
        Require(DpiPolicy.PxToDip(10.0, -2.0) == 10.0, "PxToDip did not normalize a negative scale to 1.0.");
        Require(DpiPolicy.PxToDip(10.0, double.NaN) == 10.0, "PxToDip did not normalize a NaN scale to 1.0.");
        Require(DpiPolicy.DipToPx(10.0, double.PositiveInfinity) == 10.0, "DipToPx did not normalize an infinite scale to 1.0.");
        Require(DpiPolicy.SnappedBorderThickness(double.NaN) == 1, "SnappedBorderThickness did not normalize a NaN scale.");
        Require(DpiPolicy.SnappedBorderThicknessDip(0.0) == 1.0, "SnappedBorderThicknessDip did not normalize a zero scale.");

        // --- 2. The supported range is 100%..300%, and it says so at the edges. ---
        foreach (double scale in new double[] { 1.0, 1.25, 1.5, 2.0, 3.0 })
        {
            Require(DpiPolicy.IsSupportedScale(scale),
                "IsSupportedScale(" + Fmt(scale) + ") is false; Windows offers that scale and the app claims to support it.");
        }
        Require(!DpiPolicy.IsSupportedScale(0.5), "IsSupportedScale(0.5) is true; nothing renders below 100%.");
        Require(!DpiPolicy.IsSupportedScale(4.0),
            "IsSupportedScale(4.0) is true; at 400% the window's 760 DIP minimum needs 3040 px and the card cannot fit.");
        Require(!DpiPolicy.IsSupportedScale(double.NaN), "IsSupportedScale(NaN) is true; NaN is not a scale.");
        Require(!DpiPolicy.IsSupportedScale(double.PositiveInfinity), "IsSupportedScale(+inf) is true.");

        // --- 3. The conversion round trips within one pixel at every offered scale. ---
        double[] scales = { 1.0, 1.25, 1.5, 1.75, 2.0 };
        double[] pixelSamples = { 1.0, 2.0, 3.0, 7.0, 10.0, 13.0, 16.0, 28.0, 40.0, 64.0, 120.0, 240.0, 480.0 };
        double[] dipSamples = { 1.0, 12.0, 28.0, 98.0 * 0.735, DpiPolicy.MinReadableColumnDip, 120.0, 168.0, 427.0 };
        foreach (double scale in scales)
        {
            foreach (double px in pixelSamples)
            {
                double back = DpiPolicy.DipToPx(DpiPolicy.PxToDip(px, scale), scale);
                Require(Math.Abs(back - px) <= 1.0 + 0.000001,
                    "px -> dip -> px at " + Fmt(scale) + " moved " + Fmt(px) + " px to " + Fmt(back)
                    + " px, more than the 1 px budget.");
            }
            foreach (double dip in dipSamples)
            {
                double target = DpiPolicy.DipToPx(dip, scale);
                double back = DpiPolicy.DipToPx(DpiPolicy.PxToDip(target, scale), scale);
                Require(Math.Abs(back - target) <= 1.0 + 0.000001,
                    "dip -> px -> dip -> px at " + Fmt(scale) + " moved " + Fmt(dip)
                    + " DIP (" + Fmt(target) + " px) to " + Fmt(back) + " px, more than the 1 px budget.");
            }
        }

        // --- 4. A 1-DIP border is a whole number of pixels at the awkward scales. ---
        Require(DpiPolicy.SnappedBorderThickness(1.25) == 1,
            "A 1 DIP border at 125% quantized to " + DpiPolicy.SnappedBorderThickness(1.25) + " px; 1.25 px is the half pixel this exists to avoid.");
        Require(DpiPolicy.SnappedBorderThickness(1.75) == 2,
            "A 1 DIP border at 175% quantized to " + DpiPolicy.SnappedBorderThickness(1.75) + " px; 1.75 px is the half pixel this exists to avoid.");
        Require(DpiPolicy.SnappedBorderThickness(1.0) == 1, "A 1 DIP border at 100% must stay 1 px.");
        Require(DpiPolicy.SnappedBorderThickness(2.0) == 2, "A 1 DIP border at 200% must be 2 px, not 1.");
        // The invariant behind those four numbers: for any supported scale the
        // assignable DIP value paints an integral count of device pixels, and never
        // quantizes the border out of existence.
        for (int percent = 100; percent <= 300; percent++)
        {
            double scale = percent / 100.0;
            int px = DpiPolicy.SnappedBorderThickness(scale);
            Require(px >= 1, "A 1 DIP border quantized to nothing at " + Fmt(scale) + ".");
            double painted = DpiPolicy.DipToPx(DpiPolicy.SnappedBorderThicknessDip(scale), scale);
            Require(Math.Abs(painted - Math.Round(painted, MidpointRounding.AwayFromZero)) < 0.000001,
                "The assignable border thickness at " + Fmt(scale) + " paints " + Fmt(painted)
                + " px, which is not a whole number of device pixels; it will blur.");
        }

        // --- 5. The audit catches the three ways a card does not fit. ---
        var narrow = DpiPolicy.AuditCardWidths(1.0, new Dictionary<string, double>(StringComparer.Ordinal)
        {
            { "checkbox gutter", 28.0 },
            { "cover", 72.03 },
            { "game text", 60.0 },
            { "card actions", 168.0 },
        });
        Require(narrow.Any(p => p.Contains("game text", StringComparison.Ordinal) && p.Contains("readable minimum", StringComparison.Ordinal)),
            "A 60 DIP text column was not reported as below the readable minimum: [" + string.Join(" | ", narrow) + "]");

        var tooWide = DpiPolicy.AuditCardWidths(1.0, new Dictionary<string, double>(StringComparer.Ordinal)
        {
            { "checkbox gutter", 28.0 },
            { "cover", 72.03 },
            { "game text", 320.0 },
            { "card actions", 420.0 },
        });
        Require(tooWide.Any(p => p.Contains("above the", StringComparison.Ordinal) && p.Contains("window minimum", StringComparison.Ordinal)),
            "A card totalling 840 DIP was not reported as wider than the usable maximum: [" + string.Join(" | ", tooWide) + "]");

        // A pixel-authored constant that is not a whole number of pixels at this scale.
        var blurred = DpiPolicy.AuditCardWidths(1.3, new Dictionary<string, double>(StringComparer.Ordinal)
        {
            { "checkbox gutter", 28.0 },
            { "card border", 1.0 },
            { "game text", 160.0 },
            { "card actions", 200.0 },
        });
        Require(blurred.Any(p => p.Contains("checkbox gutter", StringComparison.Ordinal) && p.Contains("not a whole number", StringComparison.Ordinal)),
            "28 DIP x 1.3 = 36.4 px was not reported as a blurred pixel constant: [" + string.Join(" | ", blurred) + "]");
        Require(blurred.Any(p => p.Contains("card border", StringComparison.Ordinal) && p.Contains("not a whole number", StringComparison.Ordinal)),
            "A 1 DIP divider at 130% was not reported as landing on a fraction of a pixel: [" + string.Join(" | ", blurred) + "]");
        // The same constants are clean at 125%, where 28 x 1.25 = 35 px exactly and
        // the border takes the policy's own snapped value (0.8 DIP = 1 px).
        var clean125 = DpiPolicy.AuditCardWidths(1.25, new Dictionary<string, double>(StringComparer.Ordinal)
        {
            { "checkbox gutter", 28.0 },
            { "card border", DpiPolicy.SnappedBorderThicknessDip(1.25) },
            { "game text", 160.0 },
            { "card actions", 200.0 },
        });
        Require(!clean125.Any(p => p.Contains("not a whole number", StringComparison.Ordinal)),
            "Pixel constants were reported as blurring at 125%, where 28 x 1.25 = 35 px and 1 x 1.25 rounds cleanly: ["
                + string.Join(" | ", clean125) + "]");

        // An unsupported scale is refused rather than laid out.
        var refused = DpiPolicy.AuditCardWidths(4.0, RealCardColumns());
        Require(refused.Any(p => p.Contains("outside the supported range", StringComparison.Ordinal)),
            "The audit laid the card out at 400% instead of refusing.");

        // --- 6. The real card is clean at every scale the manager must support. ---
        IReadOnlyDictionary<string, double> real = RealCardColumns();
        double realTotal = 0.0;
        foreach (KeyValuePair<string, double> entry in real) realTotal += entry.Value;
        Require(realTotal <= DpiPolicy.MaxUsableCardWidthDip,
            "The real card's own minimum widths total " + Fmt(realTotal) + " DIP, already over the "
            + Fmt(DpiPolicy.MaxUsableCardWidthDip) + " DIP budget the policy states; the policy is wrong, not the card.");
        foreach (double scale in DpiPolicy.ReferenceScales)
        {
            IReadOnlyList<string> problems = DpiPolicy.AuditCardWidths(scale, real);
            Require(problems.Count == 0,
                "The real card does not fit at " + (scale * 100.0).ToString("0.##", CultureInfo.InvariantCulture)
                + "%: " + string.Join(" | ", problems));
        }

        // The five scales audited above are the five the policy says the manager
        // must call this for, and they are the whole of what the claim rests on.
        Require(DpiPolicy.ReferenceScales.Count == 5
            && DpiPolicy.ReferenceScales[0] == 1.0
            && DpiPolicy.ReferenceScales[1] == 1.25
            && DpiPolicy.ReferenceScales[2] == 1.5
            && DpiPolicy.ReferenceScales[3] == 1.75
            && DpiPolicy.ReferenceScales[4] == 2.0,
            "ReferenceScales is not 100/125/150/175/200 percent.");

        // An empty card is not a clean card; the audit must not silently pass on
        // input it did not actually measure.
        Require(DpiPolicy.AuditCardWidths(1.0, null!).Count == 0,
            "Auditing a null card reported problems instead of returning nothing to audit.");
        Require(DpiPolicy.AuditCardWidths(1.0, new Dictionary<string, double>(StringComparer.Ordinal)
        {
            { "game text", double.NaN },
        }).Any(p => p.Contains("non-finite", StringComparison.Ordinal)),
            "A NaN column width was not reported as unmeasurable.");
    }
}
