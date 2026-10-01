using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace GameLibrary.Native;

/// <summary>
/// Dependency-free proof for the completion presenter: no LibraryStore, no WPF, no SelfTests.
/// Every failed assertion throws InvalidOperationException naming what the user would have seen.
///
/// The presenter is what a card binds, so these tests care about three things above all: a label
/// is never empty, a label never carries a newline (a newline is what reintroduced card wrapping
/// and the layout thrash that came with it), and an Unknown estimate never turns into a
/// measured-looking zero.
/// </summary>
internal static class CompletionPresenterTests
{
    internal static void Run(string root)
    {
        LabelForTheDocumentedPercentages();
        LabelIsNeverEmptyOrWrapped();
        LabelStaysInsideTheCardBudget();
        DetailExplainsTheNumberAndItsEvidence();
        BarDrawsOnlyForAKnownEstimate();
        MeterValueIsAlwaysClamped();
        ShouldShowHidesUnknownAndShowsRealProgress();
        FinishedLabelSaysFinished();
        InlineWidthBudgetBehaves();
        PresentedTextSurvivesAProfileRoundTrip(root);
        PresenterIsStaticAndUiFree();
    }

    /// <summary>The exact strings the cards will show. These are pinned, not described.</summary>
    private static void LabelForTheDocumentedPercentages()
    {
        var zero = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "presenter-zero", CatalogMainStoryHours: 34, CatalogHoursVerified: true));
        Require(CompletionPresenter.Label(zero) == "0% - about 34.0 h left",
            "The 0 percent card label is wrong: " + CompletionPresenter.Label(zero));

        var quarter = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "presenter-quarter", PlayedHours: 10, CatalogMainStoryHours: 40, CatalogHoursVerified: true));
        Require(CompletionPresenter.Label(quarter) == "25% - about 30.0 h left",
            "The 25 percent card label is wrong: " + CompletionPresenter.Label(quarter));

        var middle = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "presenter-middle", PlayedHours: 8.8, CatalogMainStoryHours: 14.2, CatalogHoursVerified: true));
        Require(CompletionPresenter.Label(middle) == "62% - about 5.4 h left",
            "The 62 percent card label is wrong: " + CompletionPresenter.Label(middle));

        var finished = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "presenter-finished", CategoryId: "finished", CatalogMainStoryHours: 22));
        Require(CompletionPresenter.Label(finished) == "100% - finished",
            "The 100 percent card label is wrong: " + CompletionPresenter.Label(finished));

        var unknown = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(GameId: "presenter-unknown"));
        Require(CompletionPresenter.Label(unknown) == "Completion unknown",
            "The unknown card label is wrong: " + CompletionPresenter.Label(unknown));

        // The label must agree with the number the engine computed, for every confidence level.
        foreach (var estimate in AllEstimates())
            Require(CompletionPresenter.Label(estimate).Contains(
                    Math.Clamp(estimate.PercentComplete, 0, 100).ToString(CultureInfo.InvariantCulture),
                    StringComparison.Ordinal)
                || estimate.Confidence == CompletionProgress.CompletionConfidence.Unknown
                || estimate.PercentComplete <= 0,
                "A card label hid its own percentage: " + CompletionPresenter.Label(estimate)
                + " for " + estimate.PercentComplete + " percent, " + estimate.Confidence + ".");
    }

    /// <summary>Every input, including the whole confidence enum, yields a printable one-liner.</summary>
    private static void LabelIsNeverEmptyOrWrapped()
    {
        int checkedCount = 0;
        foreach (var estimate in AllEstimates().Concat(new[] { (CompletionProgress.CompletionEstimate)null! }))
        {
            checkedCount++;
            // A null return is turned into an empty string so the very next assertion catches it,
            // rather than being silently tolerated by the tests themselves.
            string label = CompletionPresenter.Label(estimate) ?? "";
            Require(label.Length > 0, "Label returned null or an empty string for " + Describe(estimate) + ".");
            Require(label.Trim().Length == label.Length,
                "Label has stray whitespace for " + Describe(estimate) + ": '" + label + "'.");
            Require(label.IndexOf('\n') < 0 && label.IndexOf('\r') < 0,
                "Label contains a newline, which would wrap the card, for " + Describe(estimate)
                + ": '" + label + "'.");
            Require(label.Length <= CompletionPresenter.MaxLabelCharacters,
                "Label is " + label.Length + " characters, over the "
                + CompletionPresenter.MaxLabelCharacters + " character card budget, for " + Describe(estimate)
                + ": '" + label + "'.");

            string detail = CompletionPresenter.Detail(estimate) ?? "";
            Require(detail.Length > 0, "Detail returned null or an empty tooltip for " + Describe(estimate) + ".");
            Require(detail.Split('\n')[0] == label,
                "Detail does not open with the card label for " + Describe(estimate)
                + ": '" + detail.Split('\n')[0] + "' against '" + label + "'.");
        }
        Require(checkedCount >= 200, "The label sweep only covered " + checkedCount + " estimates.");

        // Every confidence level is presented, not sampled.
        foreach (CompletionProgress.CompletionConfidence confidence in
                 Enum.GetValues<CompletionProgress.CompletionConfidence>())
        {
            var probe = new CompletionProgress.CompletionEstimate(
                62, 5.4, 14.2, 8.8, confidence, "Probe basis for " + confidence + ".");
            string label = CompletionPresenter.Label(probe);
            Require(label.Length > 0 && label.IndexOf('\n') < 0 && label.IndexOf('\r') < 0,
                "Confidence " + confidence + " produced an unusable label: '" + label + "'.");
            Require(CompletionPresenter.Detail(probe).Length > 0,
                "Confidence " + confidence + " produced an empty tooltip.");
        }

        // An estimate with no basis must still say something rather than a dangling "Based on:".
        var basisless = new CompletionProgress.CompletionEstimate(62, 5.4, 14.2, 8.8,
            CompletionProgress.CompletionConfidence.Medium, "");
        Require(CompletionPresenter.Detail(basisless).Contains("no evidence was recorded",
                StringComparison.Ordinal),
            "A baseless estimate produced a tooltip that names no evidence: "
            + CompletionPresenter.Detail(basisless));
    }

    /// <summary>The width budget the manager uses to keep every card the same height.</summary>
    private static void LabelStaysInsideTheCardBudget()
    {
        Require(CompletionPresenter.MaxLabelCharacters == 32,
            "The documented card label budget changed to " + CompletionPresenter.MaxLabelCharacters + ".");
        Require(CompletionPresenter.AverageCharWidthPixels > 4 && CompletionPresenter.AverageCharWidthPixels <= 9,
            "The average character width is not a plausible pixel figure for 12 DIP card text: "
            + CompletionPresenter.AverageCharWidthPixels + ".");

        // A 120-pixel card column must at least hold the finished label, or every completed game
        // would wrap and the cards would lose their uniform height.
        Require(CompletionPresenter.FitsInline(CompletionPresenter.FinishedLabel, 120),
            "The finished label '" + CompletionPresenter.FinishedLabel + "' does not fit the card's"
            + " 120 pixel minimum column.");
        // The worst label this presenter produces must fit a plausible column, and none of them may
        // wrap a 200-pixel column.
        foreach (string label in DistinctLabels())
        {
            Require(CompletionPresenter.FitsInline(label, 220),
                "A produced label will not fit a 220 pixel column: '" + label + "'.");
            int needed = (int)Math.Ceiling(label.Length * CompletionPresenter.AverageCharWidthPixels
                + CompletionPresenter.ColumnSafetyMarginPixels);
            Require(CompletionPresenter.FitsInline(label, needed),
                "A label does not fit its own computed width: '" + label + "'.");
            Require(!CompletionPresenter.FitsInline(label, needed - 1),
                "A label still fits one pixel below its computed width: '" + label + "'.");
        }
    }

    /// <summary>The tooltip must show the number, the confidence in words, and the evidence.</summary>
    private static void DetailExplainsTheNumberAndItsEvidence()
    {
        var estimate = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "presenter-detail", PlayedHours: 8.8, CatalogMainStoryHours: 14.2, CatalogHoursVerified: true));
        string detail = CompletionPresenter.Detail(estimate);
        Require(detail.Contains("62%", StringComparison.Ordinal),
            "The tooltip does not show the percentage: " + detail);
        Require(detail.Contains("medium", StringComparison.OrdinalIgnoreCase),
            "The tooltip does not state the confidence in plain words: " + detail);
        Require(detail.Contains("Confidence:", StringComparison.Ordinal),
            "The tooltip does not label its confidence line: " + detail);
        Require(detail.Contains(estimate.Basis, StringComparison.Ordinal),
            "The tooltip does not carry the evidence the number came from: " + detail);
        Require(detail.Contains("5.4", StringComparison.Ordinal),
            "The tooltip does not show the remaining hours: " + detail);

        // Plain words per confidence, so the user never has to decode an enum name.
        (CompletionProgress.CompletionConfidence Confidence, string Word)[] expected =
        {
            (CompletionProgress.CompletionConfidence.Exact, "exact"),
            (CompletionProgress.CompletionConfidence.High, "high"),
            (CompletionProgress.CompletionConfidence.Medium, "medium"),
            (CompletionProgress.CompletionConfidence.Low, "low"),
            (CompletionProgress.CompletionConfidence.Unknown, "unknown")
        };
        foreach ((CompletionProgress.CompletionConfidence confidence, string word) in expected)
        {
            var probe = new CompletionProgress.CompletionEstimate(62, 5.4, 14.2, 8.8, confidence,
                "Probe basis for " + confidence + ".");
            string probeDetail = CompletionPresenter.Detail(probe);
            Require(probeDetail.Contains(word, StringComparison.OrdinalIgnoreCase),
                "Confidence " + confidence + " is not explained in plain words: " + probeDetail);
            Require(probeDetail.Contains("Probe basis for " + confidence + ".", StringComparison.Ordinal),
                "Confidence " + confidence + " lost its evidence: " + probeDetail);
        }

        string unknown = CompletionPresenter.Detail(
            CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(GameId: "presenter-unknown-detail")));
        Require(unknown.Contains("Completion unknown", StringComparison.Ordinal),
            "An unknown game's tooltip does not open with its card label: " + unknown);
        Require(unknown.Contains("unknown", StringComparison.OrdinalIgnoreCase),
            "An unknown game's tooltip does not say the number is not a measurement: " + unknown);
    }

    /// <summary>Unknown draws no bar, because an empty 0% bar claims the user achieved nothing.</summary>
    private static void BarDrawsOnlyForAKnownEstimate()
    {
        var unknown = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(GameId: "presenter-bar-unknown"));
        Require(CompletionPresenter.Bar(unknown) is null,
            "An unknown estimate produced a progress bar caption that would render an empty 0% bar.");
        Require(CompletionPresenter.Bar(null!) is null,
            "A missing estimate produced a progress bar caption.");
        foreach (CompletionProgress.CompletionConfidence confidence in
                 Enum.GetValues<CompletionProgress.CompletionConfidence>())
        {
            // Every confidence level is checked: exactly one of them draws no bar, and that one is
            // always Unknown.
            string? probe = CompletionPresenter.Bar(new CompletionProgress.CompletionEstimate(
                62, 5.4, 14.2, 8.8, confidence, "Bar probe."));
            if (confidence == CompletionProgress.CompletionConfidence.Unknown)
                Require(probe is null, "Confidence Unknown produced a progress bar caption.");
            else
                Require(probe is not null && probe.Length > 0,
                    "Confidence " + confidence + " produced no progress bar caption.");
        }

        var known = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "presenter-bar-known", PlayedHours: 8.8, CatalogMainStoryHours: 14.2, CatalogHoursVerified: true));
        string caption = CompletionPresenter.Bar(known) ?? "";
        Require(caption.Length > 0, "A known estimate produced no progress bar caption.");
        Require(caption.Contains("62%", StringComparison.Ordinal),
            "The progress bar caption does not show the percentage: " + caption);
        Require(caption.IndexOf('\n') < 0,
            "The progress bar caption contains a newline: " + caption);

        var finished = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "presenter-bar-finished", CategoryId: "finished"));
        Require(CompletionPresenter.Bar(finished)!.Contains("100%", StringComparison.Ordinal),
            "A finished game's progress bar caption does not show 100%: "
            + CompletionPresenter.Bar(finished));
    }

    /// <summary>The meter value is always inside 0..100, and 0 whenever nothing is known.</summary>
    private static void MeterValueIsAlwaysClamped()
    {
        Require(CompletionPresenter.PercentForMeter(
            new CompletionProgress.CompletionEstimate(-40, 5, 20, 25,
                CompletionProgress.CompletionConfidence.High, "Negative percent.")) == 0,
            "The meter accepted a negative percentage.");
        Require(CompletionPresenter.PercentForMeter(
            new CompletionProgress.CompletionEstimate(260, 0, 20, 25,
                CompletionProgress.CompletionConfidence.High, "Over 100 percent.")) == 100,
            "The meter accepted a percentage above 100.");
        Require(CompletionPresenter.PercentForMeter(
            CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(GameId: "presenter-meter-unknown"))) == 0,
            "An unknown estimate gave the meter a non-zero value.");
        Require(CompletionPresenter.PercentForMeter(null!) == 0,
            "A missing estimate gave the meter a non-zero value.");
        Require(CompletionPresenter.PercentForMeter(
            new CompletionProgress.CompletionEstimate(62, 5.4, 14.2, 8.8,
                CompletionProgress.CompletionConfidence.Medium, "Mid estimate.")) == 62,
            "The meter did not pass a real 62 percent through.");

        foreach (var estimate in AllEstimates())
        {
            int meter = CompletionPresenter.PercentForMeter(estimate);
            Require(meter >= 0 && meter <= 100,
                "The meter value " + meter + " is outside 0..100 for " + Describe(estimate) + ".");
            if (estimate.Confidence == CompletionProgress.CompletionConfidence.Unknown)
                Require(meter == 0, "An unknown estimate gave the meter " + meter + " for " + Describe(estimate) + ".");
        }
    }

    /// <summary>The presentation-layer "never fabricate" rule.</summary>
    private static void ShouldShowHidesUnknownAndShowsRealProgress()
    {
        var bare = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(GameId: "presenter-show-bare"));
        Require(!CompletionPresenter.ShouldShow(bare),
            "A bare unknown estimate was shown as a completion line, which fabricates a zero.");
        Require(!CompletionPresenter.ShouldShow(null!),
            "A missing estimate was shown as a completion line.");
        var utility = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "presenter-show-utility", IsNonGame: true, PlayedHours: 4,
            CatalogMainStoryHours: 40, CatalogHoursVerified: true));
        Require(!CompletionPresenter.ShouldShow(utility),
            "A utility image was shown a campaign completion line.");

        var mid = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "presenter-show-mid", PlayedHours: 8.8, CatalogMainStoryHours: 14.2, CatalogHoursVerified: true));
        Require(CompletionPresenter.ShouldShow(mid),
            "A real 62 percent estimate was hidden from the card.");
        var unstarted = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "presenter-show-unstarted", CatalogMainStoryHours: 34, CatalogHoursVerified: true));
        Require(CompletionPresenter.ShouldShow(unstarted),
            "A known 0 percent against a real total was hidden, though it carries real evidence.");
        var finished = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "presenter-show-finished", CategoryId: "finished"));
        Require(CompletionPresenter.ShouldShow(finished), "A finished game was hidden from the card.");

        // Playtime with no sourced total is still evidence, so the line shows without a percentage.
        var playedOnly = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "presenter-show-played", PlayedHours: 12));
        Require(CompletionPresenter.ShouldShow(playedOnly),
            "Recorded playtime with no total was hidden, discarding real evidence.");
        Require(CompletionPresenter.Label(playedOnly).Contains("total unknown", StringComparison.Ordinal),
            "Playtime with no total did not say the total is unknown: " + CompletionPresenter.Label(playedOnly));
        Require(!CompletionPresenter.Label(playedOnly).Contains("0%", StringComparison.Ordinal),
            "Playtime with no total published a misleading 0%: " + CompletionPresenter.Label(playedOnly));

        foreach (var estimate in AllEstimates())
            Require(CompletionPresenter.ShouldShow(estimate) == CompletionPresenter.HasEvidence(estimate),
                "ShouldShow and HasEvidence disagree for " + Describe(estimate) + ".");
    }

    /// <summary>A completed game must read as finished, whichever kind of evidence finished it.</summary>
    private static void FinishedLabelSaysFinished()
    {
        foreach (var finished in new[]
        {
            CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
                GameId: "presenter-fin-flag", UserMarkedComplete: true, CatalogMainStoryHours: 40)),
            CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
                GameId: "presenter-fin-category", CategoryId: "finished", CatalogMainStoryHours: 22)),
            CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
                GameId: "presenter-fin-ratio", AchievementRatio: 1d, CatalogMainStoryHours: 50)),
            CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
                GameId: "presenter-fin-overplay", PlayedHours: 90, CatalogMainStoryHours: 40, CatalogHoursVerified: true))
        })
        {
            Require(finished.PercentComplete == 100,
                "A finished fixture is not 100 percent, so the finished assertions would be meaningless.");
            Require(CompletionPresenter.Label(finished) == "100% - finished",
                "A finished game did not read as finished: " + CompletionPresenter.Label(finished));
            Require(CompletionPresenter.Label(finished).Contains("finished", StringComparison.Ordinal),
                "A finished game's label does not contain the word finished.");
            Require(CompletionPresenter.PercentForMeter(finished) == 100,
                "A finished game did not fill its meter.");
        }

        // A 100% reached only by over-play on weak evidence still says finished, and names why.
        var weak = new CompletionProgress.CompletionEstimate(100, 0, 4, 5,
            CompletionProgress.CompletionConfidence.Low, "5.0 h played past a 4.0 h estimate.");
        string weakLabel = CompletionPresenter.Label(weak);
        Require(weakLabel.Contains("finished", StringComparison.Ordinal),
            "A weak-evidence 100% does not say finished: " + weakLabel);
        Require(weakLabel.Contains("past estimate", StringComparison.Ordinal),
            "A weak-evidence 100% hides why it reads as finished: " + weakLabel);
        Require(CompletionPresenter.Detail(weak).Contains("5.0 h played past a 4.0 h estimate.",
                StringComparison.Ordinal),
            "A weak-evidence 100% lost its evidence from the tooltip: " + CompletionPresenter.Detail(weak));
    }

    /// <summary>FitsInline is the helper the manager uses to keep cards uniform in height.</summary>
    private static void InlineWidthBudgetBehaves()
    {
        const string Short = "100% - finished";
        const string Long = "99% - about 999999.0 h left";
        Require(CompletionPresenter.FitsInline(Short, 400), "A short label did not fit a 400 pixel column.");
        Require(!CompletionPresenter.FitsInline(Long, 120), "A long label fitted a 120 pixel column.");
        // 160 pixels is the width at which the budget must separate a real card label from a
        // runaway one: the short label needs 108 px, the long one 173 px.
        Require(CompletionPresenter.FitsInline(Short, 200) && !CompletionPresenter.FitsInline(Long, 160),
            "The width budget did not separate a short label from a long one: short needs "
            + (Short.Length * CompletionPresenter.AverageCharWidthPixels + CompletionPresenter.ColumnSafetyMarginPixels)
            + " px, long needs "
            + (Long.Length * CompletionPresenter.AverageCharWidthPixels + CompletionPresenter.ColumnSafetyMarginPixels) + " px.");

        // Case must not change the budget: only length is measured.
        foreach (string label in DistinctLabels())
        {
            for (int width = 0; width <= 320; width += 8)
            {
                Require(CompletionPresenter.FitsInline(label, width) == CompletionPresenter.FitsInline(
                        label.ToUpperInvariant(), width),
                    "The width budget changed with letter case at " + width + " pixels for '" + label + "'.");
                Require(CompletionPresenter.FitsInline(label, width) == CompletionPresenter.FitsInline(
                        label.ToLowerInvariant(), width),
                    "The width budget changed with letter case at " + width + " pixels for '" + label + "'.");
            }
        }
        Require(CompletionPresenter.FitsInline(label: "", columnWidthPixels: 0),
            "An empty label was reported as not fitting, although there is nothing to draw.");
        Require(!CompletionPresenter.FitsInline("62% - about 5.4 h left", 0),
            "A real label was reported as fitting a zero-pixel column.");
        Require(!CompletionPresenter.FitsInline("62%\n- about 5.4 h left", 400),
            "A label containing a newline was reported as fitting inline, which would wrap the card.");
        // The documented mid label needs 22 * 6.5 + 4 = 147 px, and must fail one pixel below that.
        const string midLabel = "62% - about 5.4 h left";
        Require(CompletionPresenter.Label(CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
                GameId: "presenter-inline", PlayedHours: 8.8, CatalogMainStoryHours: 14.2, CatalogHoursVerified: true))) == midLabel,
            "The mid label used for the width budget is not the documented one.");
        Require(CompletionPresenter.FitsInline(midLabel, 147) && !CompletionPresenter.FitsInline(midLabel, 146),
            "The documented mid label does not fit its own computed 147 pixel budget.");
    }

    /// <summary>Presented text has to survive being written into the user's profile.</summary>
    private static void PresentedTextSurvivesAProfileRoundTrip(string root)
    {
        string folder = Path.Combine(root, "completion-presenter");
        Directory.CreateDirectory(folder);
        var estimates = AllEstimates().Take(60).ToList();
        var rendered = new StringBuilder();
        foreach (var estimate in estimates)
        {
            rendered.Append(Escape(CompletionPresenter.Label(estimate))).Append('\n');
            rendered.Append(Escape(CompletionPresenter.Detail(estimate))).Append('\n');
            rendered.Append(CompletionPresenter.PercentForMeter(estimate).ToString(CultureInfo.InvariantCulture)).Append('\n');
            rendered.Append(CompletionPresenter.ShouldShow(estimate) ? "show" : "hide").Append('\n');
            rendered.Append(CompletionPresenter.Bar(estimate) is null ? "no-bar" : Escape(CompletionPresenter.Bar(estimate)!)).Append('\n');
        }
        string path = Path.Combine(folder, "cards.txt");
        File.WriteAllText(path, rendered.ToString());

        string[] lines = File.ReadAllLines(path);
        Require(lines.Length == estimates.Count * 5,
            "The card fixture wrote " + lines.Length + " lines for " + estimates.Count + " cards.");

        for (int i = 0; i < estimates.Count; i++)
        {
            var estimate = estimates[i];
            string[] row = lines.Skip(i * 5).Take(5).ToArray();
            Require(Unescape(row[0]) == CompletionPresenter.Label(estimate),
                "A card label did not survive the profile round trip: '" + Unescape(row[0]) + "'.");
            Require(Unescape(row[1]) == CompletionPresenter.Detail(estimate),
                "A tooltip did not survive the profile round trip: '" + Unescape(row[1]) + "'.");
            Require(int.Parse(row[2], CultureInfo.InvariantCulture) == CompletionPresenter.PercentForMeter(estimate),
                "A meter value did not survive the profile round trip.");
            Require((row[3] == "show") == CompletionPresenter.ShouldShow(estimate),
                "A visibility flag did not survive the profile round trip.");
            string? bar = row[4] == "no-bar" ? null : Unescape(row[4]);
            Require(bar == CompletionPresenter.Bar(estimate),
                "A bar caption did not survive the profile round trip: '" + row[4] + "'.");
        }

        // Rendering the same estimate twice must produce the same card, or the list would churn.
        var repeat = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "presenter-repeat", PlayedHours: 8.8, CatalogMainStoryHours: 14.2, CatalogHoursVerified: true));
        var first = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "presenter-repeat", PlayedHours: 8.8, CatalogMainStoryHours: 14.2, CatalogHoursVerified: true));
        for (int i = 0; i < 3; i++)
        {
            Require(CompletionPresenter.Label(repeat) == CompletionPresenter.Label(first)
                && CompletionPresenter.Detail(repeat) == CompletionPresenter.Detail(first)
                && CompletionPresenter.Bar(repeat) == CompletionPresenter.Bar(first)
                && CompletionPresenter.PercentForMeter(repeat) == CompletionPresenter.PercentForMeter(first),
                "Presenting the same estimate twice produced a different card, which would churn the list.");
        }
    }

    /// <summary>
    /// The presenter must stay a plain static class: no window, no control, no WPF type in any
    /// signature, so it can be unit tested and reused wherever the text is needed.
    /// </summary>
    private static void PresenterIsStaticAndUiFree()
    {
        Type presenter = typeof(CompletionPresenter);
        Require(presenter.IsAbstract && presenter.IsSealed,
            "CompletionPresenter is not a static class.");
        Require(!presenter.IsPublic && !presenter.IsNested,
            "CompletionPresenter is not an internal top-level type.");

        (string Name, Type Return)[] entryPoints =
        {
            ("Label", typeof(string)),
            ("Detail", typeof(string)),
            ("Bar", typeof(string)),
            ("PercentForMeter", typeof(int)),
            ("ShouldShow", typeof(bool)),
            ("FitsInline", typeof(bool))
        };
        foreach ((string name, Type expected) in entryPoints)
        {
            MethodInfo? method = presenter.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Require(method is not null, "CompletionPresenter." + name + " is missing.");
            Require(method!.ReturnType == expected,
                "CompletionPresenter." + name + " returns " + method.ReturnType.Name + ", expected "
                + expected.Name + ".");
            foreach (ParameterInfo parameter in method.GetParameters())
                Require(!IsUiType(parameter.ParameterType),
                    "CompletionPresenter." + name + " takes a UI type: " + parameter.ParameterType.FullName + ".");
            Require(!IsUiType(method.ReturnType),
                "CompletionPresenter." + name + " returns a UI type: " + method.ReturnType.FullName + ".");
        }

        MethodInfo label = presenter.GetMethod("Label", BindingFlags.NonPublic | BindingFlags.Static)!;
        ParameterInfo[] parameters = label.GetParameters();
        Require(parameters.Length == 1 && parameters[0].ParameterType == typeof(CompletionProgress.CompletionEstimate),
            "Label does not take exactly one CompletionEstimate, so the manager cannot bind it in one line.");
        Require(!label.ReturnType.IsGenericType,
            "Label returns a generic type, which a WPF binding cannot format as text.");
    }

    private static bool IsUiType(Type type)
    {
        string name = type.FullName ?? type.Name;
        return name.StartsWith("System.Windows", StringComparison.Ordinal)
            || name.StartsWith("System.Drawing", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.Win32", StringComparison.Ordinal);
    }

    /// <summary>
    /// Every estimate shape a caller could hand the presenter, across the whole confidence enum,
    /// including out-of-range and non-finite hour figures.
    /// </summary>
    private static IEnumerable<CompletionProgress.CompletionEstimate> AllEstimates()
    {
        int[] percents = { -40, -1, 0, 1, 13, 25, 42, 62, 75, 99, 100, 101, 260 };
        double?[] hoursTotals = { null, 0d, -2d, 4d, 14.2d, 40d, 1234d, 1e9, double.NaN, double.PositiveInfinity };
        double?[] hoursElapsed = { null, 0d, -1d, 5d, 8.8d, 40d, 1e9, double.NaN };
        string[] bases = { "", "   ", "30.0 h played of a 40.0 h catalog main-story estimate",
            "Utility or backup entry: campaign completion does not apply.",
            "Held at 62% across play sessions; latest signals: 8.8 h played of a 14.2 h catalog main-story estimate" };

        foreach (CompletionProgress.CompletionConfidence confidence in
                 Enum.GetValues<CompletionProgress.CompletionConfidence>())
            foreach (int percent in percents)
                foreach (double? total in hoursTotals)
                    foreach (double? elapsed in hoursElapsed)
                    {
                        double left = percent >= 100 ? 0d
                            : total is double t && double.IsFinite(t) && t > 0 ? Math.Max(0d, t - t * Math.Clamp(percent, 0, 100) / 100d)
                            : 0d;
                        int basisIndex = (int)(Math.Abs((long)percent + total.GetHashCode() + elapsed.GetHashCode()) % bases.Length);
                        yield return new CompletionProgress.CompletionEstimate(percent, left, total, elapsed, confidence,
                            bases[basisIndex]);
                    }

        // Real engine output has to travel through the same sweep.
        yield return CompletionProgress.Estimate(null);
        yield return CompletionProgress.Estimate(new CompletionProgress.CompletionSignals());
        yield return CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "sweep", PlayedHours: 8.8, CatalogMainStoryHours: 14.2, CatalogHoursVerified: true));
        yield return CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "sweep", PlayedHours: 90, CatalogMainStoryHours: 40, CatalogHoursVerified: true));
        yield return CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "sweep", CategoryId: "finished", PlayedHours: 30, CatalogMainStoryHours: 40));
        yield return CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "sweep", IsNonGame: true, PlayedHours: 9));
    }

    /// <summary>
    /// Every distinct label the presenter can produce, so the width budget is checked against real
    /// card text rather than a sample.
    /// </summary>
    private static List<string> DistinctLabels()
    {
        var labels = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var estimate in AllEstimates())
            if (seen.Add(CompletionPresenter.Label(estimate))) labels.Add(CompletionPresenter.Label(estimate));
        Require(labels.Count >= 12, "The sweep produced only " + labels.Count + " distinct labels.");
        return labels;
    }

    private static string Describe(CompletionProgress.CompletionEstimate? estimate) => estimate is null
        ? "a missing estimate"
        : estimate.PercentComplete + " percent, " + estimate.Confidence + ", total "
        + (estimate.HoursTotal?.ToString(CultureInfo.InvariantCulture) ?? "none") + ", elapsed "
        + (estimate.HoursElapsed?.ToString(CultureInfo.InvariantCulture) ?? "none");

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", string.Empty);

    private static string Unescape(string value) => value.Replace("\\n", "\n").Replace("\\\\", "\\");

    /// <summary>Throws with a specific message naming what the user would have seen.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
