using System;
using System.Collections.Generic;
using System.Globalization;

namespace GameLibrary.Native;

/// <summary>
/// Turns one <see cref="CompletionProgress.CompletionEstimate"/> into exactly what a game card
/// shows, so the manager binds a card in one line per field instead of re-deriving text itself.
///
/// This is the presentation layer only. The engine in <see cref="CompletionProgress"/> owns the
/// arithmetic, the honesty rules and the wording of the ordinary strings; this class decides
/// what is allowed to reach a dense card and keeps the one line short enough not to wrap.
///
/// The rules that matter to a card:
/// a card shows at most <see cref="MaxLabelCharacters"/> characters of label, because a wrapped
/// label was a real source of layout thrash while the list virtualises;
/// an Unknown estimate never renders a "0%" or an empty progress bar, because both claim the
/// user has achieved nothing when in fact nothing is known;
/// the number, the confidence and the evidence all travel together, so the tooltip explains WHY
/// a number says what it says rather than asking the user to trust it.
///
/// Every method is UI-free and WPF-free: no dispatcher, no control, no binding, no culture
/// dependency, no file or network access. It is therefore unit testable without a window.
/// </summary>
internal static class CompletionPresenter
{
    // ---- card text -----------------------------------------------------------------------------

    /// <summary>
    /// Shown when completion is Unknown and there is nothing at all to report. Deliberately short
    /// and deliberately not a percentage: an empty line would make the card jump as the list
    /// scrolls, and "0%" would be a fabricated measurement. "Completion unknown" is a neutral
    /// 18-character string that fits the card's 120-pixel minimum column with room to spare.
    /// </summary>
    internal const string UnknownLabel = "Completion unknown";

    /// <summary>The one line for a completion that is genuinely done.</summary>
    internal const string FinishedLabel = "100% - finished";

    /// <summary>
    /// The one line for a 100% that only reads as complete because playtime ran past the total
    /// figure on weaker evidence. It still says finished, but names the reason instead of hiding
    /// it; the caveat that the engine writes into the Basis is repeated in the tooltip.
    /// </summary>
    internal const string FinishedPastEstimateLabel = "100% - finished past estimate";

    /// <summary>
    /// Hard cap on the card line. The card's text column has MinWidth="120" in MainWindow.xaml,
    /// which fits 17 characters at the 12 DIP card font, so 32 characters is the widest label a
    /// card may hold before the manager is expected to call <see cref="FitsInline"/> and decide.
    /// Nothing this class produces exceeds it: hours are rounded to whole units above 1000 and
    /// an off-scale total drops its hours clause instead of printing 300 digits.
    /// </summary>
    internal const int MaxLabelCharacters = 32;

    /// <summary>Beyond this many hours the label stops printing a decimal place.</summary>
    private const double HoursWholeUnitThreshold = 1000d;

    /// <summary>A total at or above this is treated as off the scale and not printed.</summary>
    private const double HoursOffScaleThreshold = 100000d;

    private const string Separator = " - ";

    // ---- width budget --------------------------------------------------------------------------

    /// <summary>
    /// Average advance width of one character of the card's label font, in device-independent
    /// pixels. The card label is 12 DIP Segoe UI (MainWindow.xaml, Grid.Column="2"), where digits
    /// and lowercase letters advance roughly 6.4-6.6 px and spaces about 3.4 px; the mix inside
    /// "62% - about 5.4 h left" averages out near 6.5 px. 6.5 is used rather than a precise
    /// per-glyph measurement because a card must be uniform in height for every game, so the
    /// budget has to be a worst-case average that never under-measures the strings produced here.
    /// </summary>
    internal const double AverageCharWidthPixels = 6.5;

    /// <summary>
    /// Pixels held back from the column before comparing, covering the card's own horizontal
    /// margin and the rounded-pixel rendering of a 6.5 px average. A rounding difference of half a
    /// pixel must never be the thing that makes a card taller than its neighbours.
    /// </summary>
    internal const int ColumnSafetyMarginPixels = 4;

    /// <summary>Widest hour figure printed in a label before it is shortened.</summary>
    private const double MaxHoursLeftPrinted = 99999d;

    // ---- public entry points -------------------------------------------------------------------

    /// <summary>
    /// The single line a card shows. Never null and never empty, for every input including a
    /// missing or Unknown estimate, so a bound TextBlock can never render a blank row or a
    /// measured-looking zero. Examples: "62% - about 5.4 h left", "100% - finished",
    /// "Completion unknown".
    /// </summary>
    internal static string Label(CompletionProgress.CompletionEstimate estimate)
    {
        CompletionProgress.CompletionEstimate? value = estimate;
        if (value is null) return UnknownLabel;

        int percent = ClampPercent(value.PercentComplete);
        if (percent >= 100)
            return value.Confidence >= CompletionProgress.CompletionConfidence.High
                ? FinishedLabel
                : FinishedPastEstimateLabel;

        if (value.Confidence == CompletionProgress.CompletionConfidence.Unknown)
        {
            if (percent > 0) return percent + "%" + Separator + "completion unknown";
            if (value.HoursElapsed is double elapsed && elapsed > 0)
            {
                string? played = HoursWithinBudget(elapsed);
                return (played is null ? "Played" : "Played " + played + " h") + Separator + "total unknown";
            }
            if (value.HoursTotal is double known && known > 0)
            {
                string? ahead = HoursWithinBudget(known);
                return ahead is null ? "0%" : "0%" + Separator + "about " + ahead + " h left";
            }
            return UnknownLabel;
        }

        // The engine already owns the wording of the ordinary strings; borrowing it here is what
        // keeps the card and the estimator from drifting apart.
        string formatted = Compact(CompletionProgress.Format(value));
        if (formatted.Length > 0 && formatted.Length <= MaxLabelCharacters) return formatted;

        // Only an off-scale hour figure gets this far. Re-render it inside the budget rather than
        // print a 300-digit number across a dense card.
        if (value.HoursTotal is not > 0) return percent + "%" + Separator + "hours left unknown";
        string? left = HoursWithinBudget(value.HoursLeft);
        return left is null
            ? percent + "%" + Separator + "hours left over " + HoursOffScaleThreshold.ToString("0", CultureInfo.InvariantCulture)
            : percent + "%" + Separator + "about " + left + " h left";
    }

    /// <summary>
    /// The tooltip. Its first line is always exactly <see cref="Label"/>, then the confidence in
    /// plain words, then the remaining-time arithmetic when it is known, then the evidence the
    /// number came from. The user can therefore see WHY a percentage says what it says instead of
    /// having to trust it. Line breaks are intended here and only here; this is a tooltip, not the
    /// card line, so the no-wrap rule does not apply.
    /// </summary>
    internal static string Detail(CompletionProgress.CompletionEstimate estimate)
    {
        CompletionProgress.CompletionEstimate? value = estimate;
        var lines = new List<string>(4);
        lines.Add(value is null ? UnknownLabel : Label(value));

        if (value is null)
        {
            lines.Add(ConfidenceLine(CompletionProgress.CompletionConfidence.Unknown));
            lines.Add("Based on: no estimate was supplied for this game.");
            return string.Join("\n", lines);
        }

        lines.Add(ConfidenceLine(value.Confidence));

        int percent = ClampPercent(value.PercentComplete);
        if (percent >= 100)
        {
            if (value.HoursElapsed is double done && done > 0 && value.HoursTotal is double total && total > 0)
                lines.Add("About " + Hours(done) + " h played against a " + Hours(total) + " h estimate.");
            else if (value.HoursTotal is double only && only > 0)
                lines.Add("About " + Hours(only) + " h total estimate; no separate playtime figure is recorded.");
        }
        else if (value.HoursTotal is double ahead && ahead > 0)
        {
            string left = Hours(value.HoursLeft);
            if (value.HoursLeft > 0)
                lines.Add("About " + left + " h of a " + Hours(ahead) + " h estimate is still ahead.");
            else
                lines.Add("The " + Hours(ahead) + " h estimate has no remaining time recorded.");
        }
        else if (value.Confidence == CompletionProgress.CompletionConfidence.Unknown)
        {
            lines.Add("No completion-time estimate is sourced, so no percentage is claimed.");
        }
        else
        {
            lines.Add("No sourced completion-time total, so remaining hours cannot be estimated.");
        }

        lines.Add("Based on: " + Basis(value.Basis));
        return string.Join("\n", lines);
    }

    /// <summary>
    /// The caption for a progress bar, or null when there is nothing honest to draw. Unknown is
    /// always null: a 0% bar for an unknown game implies the user has achieved nothing, which is
    /// a different claim from "nothing is known", and the card collapses the row instead.
    /// </summary>
    internal static string? Bar(CompletionProgress.CompletionEstimate estimate)
    {
        CompletionProgress.CompletionEstimate? value = estimate;
        if (value is null || value.Confidence == CompletionProgress.CompletionConfidence.Unknown) return null;
        int percent = ClampPercent(value.PercentComplete);
        return value.HoursTotal is > 0
            ? percent + "% complete" + Separator + Hours(value.HoursTotal.Value) + " h total"
            : percent + "% complete";
    }

    /// <summary>
    /// The 0..100 value a meter binds to, always clamped, and 0 for Unknown so an unbound meter
    /// cannot render a fabricated partial fill.
    /// </summary>
    internal static int PercentForMeter(CompletionProgress.CompletionEstimate estimate)
    {
        CompletionProgress.CompletionEstimate? value = estimate;
        if (value is null || value.Confidence == CompletionProgress.CompletionConfidence.Unknown) return 0;
        return ClampPercent(value.PercentComplete);
    }

    /// <summary>
    /// Whether the card should show the completion line at all. False only for an Unknown
    /// estimate that carries no evidence, so the card omits the row rather than showing a
    /// misleading zero. This is the "never fabricate" rule at the presentation layer: a percentage
    /// is only rendered when something justifies it.
    /// </summary>
    internal static bool ShouldShow(CompletionProgress.CompletionEstimate estimate)
    {
        CompletionProgress.CompletionEstimate? value = estimate;
        return value is not null && HasEvidence(value);
    }

    /// <summary>
    /// Whether a label will fit on one line in a column of the given width without wrapping, so
    /// cards stay uniform in height. Uses <see cref="AverageCharWidthPixels"/> plus
    /// <see cref="ColumnSafetyMarginPixels"/>. A label containing a line break can never fit
    /// inline, and an empty label always fits because there is nothing to draw.
    /// </summary>
    internal static bool FitsInline(string label, int columnWidthPixels)
    {
        if (string.IsNullOrEmpty(label)) return true;
        if (label.IndexOf('\n') >= 0 || label.IndexOf('\r') >= 0) return false;
        if (columnWidthPixels <= 0) return false;
        return label.Length * AverageCharWidthPixels + ColumnSafetyMarginPixels <= columnWidthPixels;
    }

    /// <summary>True when something justifies showing the line: any known figure at all.</summary>
    internal static bool HasEvidence(CompletionProgress.CompletionEstimate estimate)
    {
        CompletionProgress.CompletionEstimate? value = estimate;
        if (value is null) return false;
        if (value.Confidence != CompletionProgress.CompletionConfidence.Unknown) return true;
        return value.PercentComplete > 0 || value.HoursTotal is > 0 || value.HoursElapsed is > 0;
    }

    // ---- internals -----------------------------------------------------------------------------

    private static int ClampPercent(int percent) => Math.Clamp(percent, 0, 100);

    /// <summary>The confidence in plain words, always on its own line of the tooltip.</summary>
    private static string ConfidenceLine(CompletionProgress.CompletionConfidence confidence) => confidence switch
    {
        CompletionProgress.CompletionConfidence.Exact =>
            "Confidence: exact" + Separator + "your own record says this game is finished, so no estimate was needed.",
        CompletionProgress.CompletionConfidence.High =>
            "Confidence: high" + Separator + "an independent completion ratio, or playtime clearly past the total.",
        CompletionProgress.CompletionConfidence.Medium =>
            "Confidence: medium" + Separator + "played hours measured against a sourced completion-time estimate.",
        CompletionProgress.CompletionConfidence.Low =>
            "Confidence: low" + Separator + "only one figure is known, so treat this number as a guide.",
        _ =>
            "Confidence: unknown" + Separator + "this is not a measurement, so no percentage and no progress bar are claimed."
    };

    /// <summary>Flattens the engine's basis so it can sit on one tooltip line, or names its absence.</summary>
    private static string Basis(string? basis)
    {
        if (string.IsNullOrWhiteSpace(basis)) return "no evidence was recorded for this game.";
        string flat = basis.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
        while (flat.Contains("  ")) flat = flat.Replace("  ", " ");
        return flat.Length == 0 ? "no evidence was recorded for this game." : flat;
    }

    /// <summary>Collapses engine output into a single safe line, never returning empty.</summary>
    private static string Compact(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return UnknownLabel;
        string flat = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length == 0 ? UnknownLabel : flat;
    }

    /// <summary>One decimal place below a thousand hours, whole units above it.</summary>
    private static string Hours(double value) => !double.IsFinite(value) || value < 0 ? "0.0"
        : value < HoursWholeUnitThreshold ? value.ToString("0.0", CultureInfo.InvariantCulture)
        : Math.Min(value, HoursOffScaleThreshold).ToString("0", CultureInfo.InvariantCulture);

    /// <summary>
    /// The label-length-bounded hour figure, or null when the value is off the scale and must not
    /// be printed at all. This is what stops an absurd sourced total from producing a 300-character
    /// card label.
    /// </summary>
    private static string? HoursWithinBudget(double? value)
        => value is not double hours || !double.IsFinite(hours) || hours < 0 ? "0.0"
        : hours >= MaxHoursLeftPrinted ? null
        : Hours(hours);
}
