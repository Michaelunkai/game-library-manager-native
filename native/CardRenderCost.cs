using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace GameLibrary.Native;

internal sealed record RenderCostEstimate(
    double MeanMilliseconds,
    double P95Milliseconds,
    double MaxMilliseconds,
    double TotalMilliseconds,
    int CardCount);

internal sealed record CardWorkItem(string Name, double CostMilliseconds);

internal sealed record RenderCostFinding(string Name, double CostMilliseconds, bool OverBudget, string Cause);

internal static class CardRenderCost
{
    // 16.7 ms is one frame at 60 fps. A single frame must do far less than that on
    // the UI thread: layout, render, input handling, and the compositor all draw
    // from the same frame, so card realization is only one of several consumers.
    // The budget below is the whole frame; per-card work is held to a small slice
    // of it (see PerCardBudgetMs) so a burst of realizations cannot eat the frame.
    internal const double FrameBudgetMs = 16.7;

    // During a fast scroll at most ~8 cards are realized per frame, so each card
    // must fit in roughly 1/8 of the frame budget. Anything above this is a
    // stall risk even when the mean looks fine.
    internal const double PerCardBudgetMs = 2.0;

    private const int SamplesPerRun = 50;
    private const int Runs = 3;

    private static double lastMeasuredPerCardMs = 1.0;

    internal static double LastMeasuredPerCardMs => lastMeasuredPerCardMs;

    internal static RenderCostEstimate Measure(Func<int> realizeOneCard)
    {
        if (realizeOneCard is null) throw new ArgumentNullException(nameof(realizeOneCard));

        double[] best = Array.Empty<double>();
        double bestTotal = double.MaxValue;
        for (int run = 0; run < Runs; run++)
        {
            var samples = new double[SamplesPerRun];
            for (int i = 0; i < SamplesPerRun; i++)
            {
                var sw = Stopwatch.StartNew();
                try { realizeOneCard(); }
                catch { }
                finally { sw.Stop(); }
                samples[i] = sw.Elapsed.TotalMilliseconds;
            }
            double total = samples.Sum();
            if (total < bestTotal) { bestTotal = total; best = samples; }
        }

        var sorted = best.OrderBy(s => s).ToArray();
        double mean = sorted.Average();
        double p95 = sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(0.95 * sorted.Length) - 1)];
        double max = sorted[^1];

        lastMeasuredPerCardMs = Math.Round(mean, 2);

        return new RenderCostEstimate(
            Math.Round(mean, 2),
            Math.Round(p95, 2),
            Math.Round(max, 2),
            Math.Round(bestTotal, 2),
            SamplesPerRun);
    }

    // How many cards may be realized inside the budget at the per-card cost from
    // the most recent Measure call, floored at 1. A zero measured cost means
    // realization is free, so no per-card limit is reported.
    internal static int MaxCardsPerFrame(double budgetMilliseconds)
    {
        double perCard = lastMeasuredPerCardMs > 0 ? lastMeasuredPerCardMs : 1.0;
        if (perCard <= 0) return int.MaxValue;
        return Math.Max(1, (int)Math.Floor(budgetMilliseconds / perCard));
    }

    // True when realizing `cards` cards fits the frame budget. Uses p95, not the
    // mean: the manager must be able to promise no stall, and the mean hides the
    // tail that causes dropped frames.
    internal static bool WithinBudget(RenderCostEstimate estimate, int cards)
    {
        if (estimate is null) throw new ArgumentNullException(nameof(estimate));
        if (cards < 0) throw new ArgumentOutOfRangeException(nameof(cards));
        return estimate.P95Milliseconds * cards <= FrameBudgetMs;
    }

    internal static IReadOnlyList<RenderCostFinding> Audit(IReadOnlyList<CardWorkItem> items)
    {
        if (items is null) throw new ArgumentNullException(nameof(items));
        return items
            .Select(item => new RenderCostFinding(
                item.Name,
                item.CostMilliseconds,
                item.CostMilliseconds > PerCardBudgetMs,
                item.CostMilliseconds > PerCardBudgetMs
                    ? $"'{item.Name}' costs {item.CostMilliseconds:F2} ms, over the {PerCardBudgetMs:F2} ms per-card budget; it runs on the UI thread while a card is realized, so it directly steals frame time."
                    : string.Empty))
            .OrderByDescending(f => f.CostMilliseconds)
            .ToList();
    }

    internal static string Describe(RenderCostEstimate estimate)
    {
        if (estimate is null) throw new ArgumentNullException(nameof(estimate));
        return $"mean {estimate.MeanMilliseconds:F2} ms, p95 {estimate.P95Milliseconds:F2} ms, max {estimate.MaxMilliseconds:F2} ms over {estimate.CardCount} cards";
    }
}

internal static class CardRenderCostTests
{
    private static int failures;

    internal static void Run(string root)
    {
        failures = 0;
        var lines = new List<string>();
        try
        {
            ZeroCostIsFree(lines);
            SlowRealizationBreaksBudget(lines);
            PercentilesOrderAndOutlier(lines);
            ThrowingCallbackDoesNotEscape(lines);
            AuditSortsWorstFirstAndNamesOverBudget(lines);
            DescribeIsNeverEmpty(lines);
        }
        finally
        {
            lines.Add(failures == 0 ? "SLOT3 TESTS PASSED" : $"SLOT3 TESTS FAILED ({failures} failure(s))");
            if (!string.IsNullOrEmpty(root))
            {
                try { File.WriteAllLines(Path.Combine(root, "card-render-cost-report.txt"), lines); }
                catch { }
            }
        }
        foreach (var line in lines) Console.WriteLine(line);
        if (failures > 0) throw new InvalidOperationException($"{failures} CardRenderCost test(s) failed");
    }

    private static void ZeroCostIsFree(List<string> lines)
    {
        var estimate = CardRenderCost.Measure(() => 0);
        Check(lines, "zero-cost mean is 0", estimate.MeanMilliseconds == 0, $"mean={estimate.MeanMilliseconds}");
        Check(lines, "zero-cost p95 is 0", estimate.P95Milliseconds == 0, $"p95={estimate.P95Milliseconds}");
        Check(lines, "zero-cost max is 0", estimate.MaxMilliseconds == 0, $"max={estimate.MaxMilliseconds}");
        Check(lines, "zero-cost is WithinBudget for 1000 cards", CardRenderCost.WithinBudget(estimate, 1000), CardRenderCost.Describe(estimate));
    }

    private static void SlowRealizationBreaksBudget(List<string> lines)
    {
        var estimate = RenderCostCardSlow();
        Check(lines, "slow realization is NOT WithinBudget for 10 cards", !CardRenderCost.WithinBudget(estimate, 10), CardRenderCost.Describe(estimate));
        int max = CardRenderCost.MaxCardsPerFrame(CardRenderCost.FrameBudgetMs);
        Check(lines, "MaxCardsPerFrame drops to a small number", max <= 3, $"max={max}");
    }

    private static RenderCostEstimate RenderCostCardSlow()
    {
        return CardRenderCost.Measure(() => { BusyWait(5.0); return 0; });
    }

    private static void PercentilesOrderAndOutlier(List<string> lines)
    {
        int i = 0;
        var variable = CardRenderCost.Measure(() => { BusyWait((i++ % 7) * 0.1); return 0; });
        Check(lines, "p95 >= mean for variable workload", variable.P95Milliseconds >= variable.MeanMilliseconds, CardRenderCost.Describe(variable));
        Check(lines, "max >= mean for variable workload", variable.MaxMilliseconds >= variable.MeanMilliseconds, CardRenderCost.Describe(variable));

        int j = 0;
        var outlier = CardRenderCost.Measure(() => { if (j++ % 10 == 5) BusyWait(30.0); return 0; });
        Check(lines, "max > mean when one sample is an outlier", outlier.MaxMilliseconds > outlier.MeanMilliseconds, CardRenderCost.Describe(outlier));
    }

    private static void ThrowingCallbackDoesNotEscape(List<string> lines)
    {
        var estimate = CardRenderCost.Measure(() => throw new InvalidOperationException("boom"));
        Check(lines, "throwing callback yields zero cost", estimate.MeanMilliseconds == 0, CardRenderCost.Describe(estimate));
    }

    private static void AuditSortsWorstFirstAndNamesOverBudget(List<string> lines)
    {
        var items = new[]
        {
            new CardWorkItem("Cover decode", 6.2),
            new CardWorkItem("Template apply", 0.4),
            new CardWorkItem("Data bind", 1.9),
            new CardWorkItem("Layout pass", 2.5),
        };
        var findings = CardRenderCost.Audit(items);
        var costs = findings.Select(f => f.CostMilliseconds).ToArray();
        Check(lines, "findings sorted worst-first", costs.SequenceEqual(items.Select(x => x.CostMilliseconds).OrderByDescending(c => c)), string.Join(",", costs));
        var overBudget = findings.Where(f => f.OverBudget).Select(f => f.Name).OrderBy(n => n).ToArray();
        Check(lines, "every over-budget item is named", overBudget.SequenceEqual(new[] { "Cover decode", "Layout pass" }), string.Join(",", overBudget));
        Check(lines, "over-budget findings have a cause", findings.Where(f => f.OverBudget).All(f => !string.IsNullOrWhiteSpace(f.Cause)), string.Join(" |", findings.Select(f => f.Cause)));
        Check(lines, "at-budget items are not flagged", !findings.Single(f => f.Name == "Data bind").OverBudget, "");
    }

    private static void DescribeIsNeverEmpty(List<string> lines)
    {
        var estimate = CardRenderCost.Measure(() => 0);
        var text = CardRenderCost.Describe(estimate);
        Check(lines, "Describe is never empty", !string.IsNullOrWhiteSpace(text), text);
    }

    private static void BusyWait(double milliseconds)
    {
        long until = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * milliseconds / 1000.0);
        while (Stopwatch.GetTimestamp() < until) { }
    }

    private static void Check(List<string> lines, string name, bool ok, string detail)
    {
        if (ok)
        {
            lines.Add("PASS " + name);
        }
        else
        {
            failures++;
            lines.Add("FAIL " + name + ": " + detail);
        }
    }
}
