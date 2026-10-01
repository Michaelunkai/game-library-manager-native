using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace GameLibrary.Native;

/// <summary>
/// Dependency-free proof for the completion estimator: no LibraryStore, no WPF, no SelfTests.
/// Every failed assertion throws InvalidOperationException naming what the user would have seen.
/// </summary>
internal static class CompletionProgressTests
{
    internal static void Run(string root)
    {
        ExactCompletionIsOneHundredPercent();
        FinishedCategoryIsHonouredAndItsProtectedSiblingsAreNot();
        TrophyRatioIsTheStrongestAutomaticSignal();
        PlayedAgainstTotal();
        OverplayClampsToOneHundredPercent();
        UnplayedWithKnownTotalIsZero();
        WishlistAndUnstartedAreZero();
        NoSignalsFabricateNothing();
        PercentClamps();
        HoursLeftIsNeverNegative();
        DenominatorChoiceAndBacklogAwareness();
        BadSignalsAreIgnoredRatherThanClamped();
        DeriveAccumulatesMonotonically();
        SmoothingIsStableAndResetEscapesIt();
        FutureGameNeedsNoSpecialCasing();
        HistoryBelongsToOneGame();
        HistorySurvivesAProfileRoundTrip(root);
        FormatMatchesTheDocumentedStrings();
        EstimateIsPureAndDeterministic();
    }

    private static void ExactCompletionIsOneHundredPercent()
    {
        var flagged = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "exact-flag", PlayedHours: 41.5, CatalogMainStoryHours: 40, CatalogHoursVerified: true,
            LastPlayedUtc: new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc), CategoryId: "rpg",
            UserMarkedComplete: true));
        Require(flagged.PercentComplete == 100 && flagged.HoursLeft == 0
            && flagged.Confidence == CompletionProgress.CompletionConfidence.Exact,
            "An explicit user completion flag did not produce an exact 100 percent with 0 hours left.");
        Require(flagged.HoursTotal == 40 && flagged.HoursElapsed == 41.5 && flagged.HoursLeft >= 0,
            "An exact completion lost its total or its elapsed hours.");
        Require(flagged.Basis.Contains("marked this game as finished", StringComparison.Ordinal),
            "An exact completion did not name the user flag in its basis: " + flagged.Basis);

        var moved = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "exact-category", PlayedHours: 30, CatalogMainStoryHours: 40, CatalogHoursVerified: true,
            CategoryId: "finished"));
        Require(moved.PercentComplete == 100 && moved.HoursLeft == 0
            && moved.Confidence == CompletionProgress.CompletionConfidence.Exact,
            "The finished category did not produce an exact 100 percent with 0 hours left.");

        // Completion outranks every weaker signal, including a conflicting category and a low ratio.
        var conflicted = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "exact-wins", PlayedHours: 3, CatalogMainStoryHours: 400, CategoryId: "not_for_me",
            AchievementRatio: 0.04, BacklogAware: true, UserMarkedComplete: true));
        Require(conflicted.PercentComplete == 100 && conflicted.HoursLeft == 0
            && conflicted.Confidence == CompletionProgress.CompletionConfidence.Exact,
            "A user completion flag did not outrank weaker automatic signals.");
        Require(CompletionProgress.Format(flagged) == "100% - finished",
            "An exact completion did not format as finished: " + CompletionProgress.Format(flagged));
    }

    private static void FinishedCategoryIsHonouredAndItsProtectedSiblingsAreNot()
    {
        Require(CompletionProgress.IsFinishedCategory("finished") && CompletionProgress.IsFinishedCategory("Finished")
            && CompletionProgress.IsFinishedCategory(" finished "),
            "The finished category id is not matched as Models.cs defines it.");
        Require(!CompletionProgress.IsFinishedCategory(null) && !CompletionProgress.IsFinishedCategory("")
            && !CompletionProgress.IsFinishedCategory("new") && !CompletionProgress.IsFinishedCategory("wishlist")
            && !CompletionProgress.IsFinishedCategory("installed") && !CompletionProgress.IsFinishedCategory("all")
            && !CompletionProgress.IsFinishedCategory("finished-games"),
            "A non-finished category identity was accepted as finished.");
        Require(CompletionProgress.IsUnstartedCategory("new") && !CompletionProgress.IsUnstartedCategory("finished"),
            "The unstarted category id is not matched as CategoryVisibility defines it.");
        Require(CompletionProgress.FinishedCategoryId == "finished" && CompletionProgress.UnstartedCategoryId == "new"
            && CompletionProgress.WishlistTabId == "wishlist",
            "The exported category identities do not match the app's own ids.");

        foreach (string sibling in new[] { "not_for_me", "meh", "hyperv", "2d", "personal", "rpg", "Action" })
        {
            var estimate = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
                GameId: "protected-" + sibling, PlayedHours: 30, CatalogMainStoryHours: 40,
                CatalogHoursVerified: true, CategoryId: sibling));
            Require(estimate.PercentComplete == 75
                && estimate.Confidence == CompletionProgress.CompletionConfidence.Medium,
                "Category '" + sibling + "' was wrongly treated as a finished game (" + estimate.PercentComplete + " percent).");
        }
    }

    private static void TrophyRatioIsTheStrongestAutomaticSignal()
    {
        var estimate = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "trophy", PlayedHours: 10, CatalogMainStoryHours: 50, CatalogHoursVerified: true,
            LastPlayedUtc: new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), AchievementRatio: 0.42));
        Require(estimate.PercentComplete == 42,
            "A 0.42 trophy ratio must be 42 percent, was " + estimate.PercentComplete + ".");
        Require(estimate.Confidence == CompletionProgress.CompletionConfidence.High,
            "A trophy ratio must report High confidence, was " + estimate.Confidence + ".");
        Require(estimate.HoursTotal == 50 && Near(estimate.HoursLeft, 29),
            "A trophy ratio did not derive hours left from the total: " + estimate.HoursLeft + ".");
        Require(estimate.Basis.Contains("42% of the trophy set is unlocked", StringComparison.Ordinal),
            "The basis did not name the trophy signal: " + estimate.Basis);
        Require(CompletionProgress.Format(estimate) == "42% - about 29.0 h left",
            "The trophy format string is wrong: " + CompletionProgress.Format(estimate));

        var full = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "trophy-full", PlayedHours: 60, CatalogMainStoryHours: 50, CatalogHoursVerified: true,
            AchievementRatio: 1d));
        Require(full.PercentComplete == 100 && full.HoursLeft == 0
            && full.Confidence == CompletionProgress.CompletionConfidence.High,
            "A complete trophy set did not report 100 percent with 0 hours left.");
        Require(CompletionProgress.Format(full) == "100% - finished",
            "A complete trophy set did not format as finished.");

        var bare = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(GameId: "trophy-bare", AchievementRatio: 0.25));
        Require(bare.PercentComplete == 25 && bare.HoursTotal is null && bare.HoursLeft == 0
            && bare.Confidence == CompletionProgress.CompletionConfidence.High
            && bare.Basis.Contains("hours left are unknown", StringComparison.Ordinal),
            "A trophy ratio without any total invented an hours-left figure: " + bare.Basis);
    }

    private static void PlayedAgainstTotal()
    {
        var estimate = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "thirty-of-forty", PlayedHours: 30, CatalogMainStoryHours: 40, CatalogHoursVerified: true,
            LastPlayedUtc: new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc),
            Added: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        Require(estimate.PercentComplete == 75,
            "30 of 40 played hours must be 75 percent, was " + estimate.PercentComplete + ".");
        Require(Near(estimate.HoursLeft, 10),
            "30 of 40 played hours must leave 10 hours, left " + estimate.HoursLeft + ".");
        Require(estimate.Confidence == CompletionProgress.CompletionConfidence.Medium,
            "Played hours against a sourced total must report Medium confidence, was " + estimate.Confidence + ".");
        Require(estimate.HoursElapsed == 30 && estimate.HoursTotal == 40 && estimate.HoursLeft >= 0,
            "Played hours against a sourced total lost its hour figures.");
        Require(estimate.Basis.Contains("30.0 h played of a 40.0 h", StringComparison.Ordinal),
            "The basis did not show the arithmetic: " + estimate.Basis);
        Require(CompletionProgress.Percent(30, 40) == 75,
            "Percent disagreed with Estimate for 30 of 40 hours.");

        // A real last-played stamp counts as a session even without a ledger value.
        var stampOnly = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "stamp-only", CatalogMainStoryHours: 40, CatalogHoursVerified: true,
            LastPlayedUtc: new DateTime(2026, 4, 4, 0, 0, 0, DateTimeKind.Utc)));
        Require(stampOnly.PercentComplete == 0
            && stampOnly.Confidence == CompletionProgress.CompletionConfidence.Medium,
            "A last-played stamp without ledger hours did not produce a played game: " + stampOnly.Basis);

        // Verified main-story metadata outranks the catalog figure.
        var evidence = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "evidence-wins", PlayedHours: 12, CatalogMainStoryHours: 100, CatalogHoursVerified: true,
            MainStoryHours: 24, MainStoryHoursVerified: true));
        Require(evidence.PercentComplete == 50 && evidence.HoursTotal == 24 && Near(evidence.HoursLeft, 12),
            "Verified main-story metadata did not become the denominator: " + evidence.Basis);
    }

    private static void OverplayClampsToOneHundredPercent()
    {
        var estimate = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "overplay", PlayedHours: 55, CatalogMainStoryHours: 40, CatalogHoursVerified: true,
            LastPlayedUtc: new DateTime(2026, 6, 6, 0, 0, 0, DateTimeKind.Utc)));
        Require(estimate.PercentComplete == 100 && estimate.HoursLeft == 0,
            "Playing 55 of 40 hours must clamp to 100 percent and 0 hours left, was "
            + estimate.PercentComplete + " percent and " + estimate.HoursLeft + " hours.");
        Require(estimate.Confidence >= CompletionProgress.CompletionConfidence.Medium,
            "Over-play must keep at least Medium confidence, was " + estimate.Confidence + ".");
        Require(estimate.Basis.Contains("55.0 h played past the 40.0 h", StringComparison.Ordinal),
            "The basis did not name over-play: " + estimate.Basis);
        Require(CompletionProgress.Format(estimate) == "100% - played past the 40.0 h estimate",
            "An over-play completion did not say why it reads as finished: " + CompletionProgress.Format(estimate));
        Require(CompletionProgress.Percent(55, 40) == 100,
            "Percent did not clamp 55 of 40 hours to 100.");

        var deep = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "overplay-high", PlayedHours: 90, CatalogMainStoryHours: 40, CatalogHoursVerified: true));
        Require(deep.PercentComplete == 100 && deep.HoursLeft == 0
            && deep.Confidence == CompletionProgress.CompletionConfidence.High,
            "Clear over-play must reach High confidence, was " + deep.Confidence + ".");
        Require(CompletionProgress.Format(deep) == "100% - finished",
            "A High-confidence completion did not format as finished.");
    }

    private static void UnplayedWithKnownTotalIsZero()
    {
        var estimate = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "unplayed", CatalogMainStoryHours: 34, CatalogHoursVerified: true,
            Added: new DateTime(2026, 1, 9, 0, 0, 0, DateTimeKind.Utc)));
        Require(estimate.PercentComplete == 0,
            "An unplayed game with a known total must be 0 percent, was " + estimate.PercentComplete + ".");
        Require(Near(estimate.HoursLeft, 34),
            "An unplayed game must report the whole total as hours left, was " + estimate.HoursLeft + ".");
        Require(estimate.Confidence == CompletionProgress.CompletionConfidence.Low,
            "An unplayed game with only a total must report Low confidence, was " + estimate.Confidence + ".");
        Require(estimate.HoursTotal == 34 && estimate.HoursElapsed is null
            && estimate.Basis.Contains("still ahead", StringComparison.Ordinal),
            "An unplayed game invented an elapsed figure or hid its basis: " + estimate.Basis);
        Require(CompletionProgress.Format(estimate) == "0% - about 34.0 h left",
            "The unplayed format string is wrong: " + CompletionProgress.Format(estimate));

        var zeroLedger = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "zero-ledger", PlayedHours: 0, CatalogMainStoryHours: 34, CatalogHoursVerified: true));
        Require(zeroLedger.PercentComplete == 0
            && zeroLedger.Confidence == CompletionProgress.CompletionConfidence.Low,
            "A zero playtime ledger must be treated as unplayed: " + zeroLedger.Basis);
    }

    private static void WishlistAndUnstartedAreZero()
    {
        var wishlist = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "wishlist", CatalogMainStoryHours: 12, CatalogHoursVerified: true, Wishlisted: true));
        Require(wishlist.PercentComplete == 0 && Near(wishlist.HoursLeft, 12),
            "A wishlisted unstarted game must be 0 percent with the full total left: " + wishlist.Basis);
        Require(wishlist.Basis.Contains("wishlist", StringComparison.Ordinal),
            "The wishlist signal was not named in the basis: " + wishlist.Basis);

        var unstarted = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "unstarted", CategoryId: "new"));
        Require(unstarted.PercentComplete == 0
            && unstarted.Confidence == CompletionProgress.CompletionConfidence.Unknown,
            "An unstarted New-arrivals game with no total must be 0 percent and Unknown, was " + unstarted.Confidence + ".");
        Require(unstarted.HoursLeft == 0 && unstarted.HoursTotal is null,
            "An unstarted game with no known total fabricated a total.");

        var startedWishlist = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "wishlist-started", PlayedHours: 6, CatalogMainStoryHours: 12, CatalogHoursVerified: true,
            Wishlisted: true));
        Require(startedWishlist.PercentComplete == 50 && Near(startedWishlist.HoursLeft, 6),
            "A started wishlisted game must still be measured: " + startedWishlist.Basis);
    }

    private static void NoSignalsFabricateNothing()
    {
        CompletionProgress.CompletionEstimate[] unknowns =
        {
            CompletionProgress.Estimate(null),
            CompletionProgress.Estimate(new CompletionProgress.CompletionSignals())
        };
        foreach (var unknown in unknowns)
        {
            Require(unknown.PercentComplete == 0 && unknown.HoursLeft == 0 && unknown.HoursTotal is null
                && unknown.HoursElapsed is null,
                "An unknown game fabricated a percentage, an hours-left figure, or a total: " + unknown.Basis);
            Require(unknown.Confidence == CompletionProgress.CompletionConfidence.Unknown,
                "An unknown game must report Unknown confidence, was " + unknown.Confidence + ".");
            Require(unknown.Basis.Contains("unknown", StringComparison.OrdinalIgnoreCase),
                "An unknown game did not say so in its basis: " + unknown.Basis);
            Require(CompletionProgress.Format(unknown) == "0% - no sourced estimate",
                "An unknown game did not format as having no sourced estimate.");
        }

        var playedNoTotal = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(GameId: "no-total", PlayedHours: 12));
        Require(playedNoTotal.PercentComplete == 0 && playedNoTotal.HoursTotal is null
            && playedNoTotal.Confidence == CompletionProgress.CompletionConfidence.Unknown
            && playedNoTotal.Basis.Contains("no sourced completion-time estimate", StringComparison.Ordinal),
            "Playtime without a denominator fabricated a percentage: " + playedNoTotal.Basis);
        Require(CompletionProgress.Format(playedNoTotal) == "0% - no sourced estimate",
            "An uncomputable game did not format as having no sourced estimate: " + CompletionProgress.Format(playedNoTotal));

        var utility = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "utility", IsNonGame: true, PlayedHours: 4, CatalogMainStoryHours: 40, CatalogHoursVerified: true));
        Require(utility.PercentComplete == 0 && utility.HoursTotal is null
            && utility.Confidence == CompletionProgress.CompletionConfidence.Unknown,
            "A utility or backup entry was given a campaign percentage.");

        var emptyHistory = CompletionProgress.EstimateFor(Array.Empty<CompletionProgress.CompletionSignals>());
        Require(emptyHistory.PercentComplete == 0 && emptyHistory.HoursTotal is null
            && emptyHistory.Confidence == CompletionProgress.CompletionConfidence.Unknown
            && emptyHistory.Basis.Contains("No play session", StringComparison.Ordinal),
            "An empty play history did not surface as unknown: " + emptyHistory.Basis);
    }

    private static void PercentClamps()
    {
        Require(CompletionProgress.Percent(-5, 10) == 0, "Percent accepted a negative played value.");
        Require(CompletionProgress.Percent(500, 4) == 100, "Percent accepted a percentage above 100.");
        Require(CompletionProgress.Percent(10, 0) == 0 && CompletionProgress.Percent(10, -1) == 0,
            "Percent accepted a non-positive total.");
        Require(CompletionProgress.Percent(null, 10) == 0 && CompletionProgress.Percent(10, null) == 0,
            "Percent invented a value out of a missing figure.");
        Require(CompletionProgress.Percent(0, 10) == 0, "Percent accepted a zero played value.");
        Require(CompletionProgress.Percent(double.NaN, 10) == 0 && CompletionProgress.Percent(10, double.NaN) == 0,
            "Percent accepted a NaN hour figure.");
        Require(CompletionProgress.Percent(double.PositiveInfinity, 10) == 0
            && CompletionProgress.Percent(10, double.PositiveInfinity) == 0,
            "Percent accepted a non-finite hour figure.");
        Require(CompletionProgress.Percent(1, 3) == 33 && CompletionProgress.Percent(2, 3) == 67
            && CompletionProgress.Percent(30, 40) == 75,
            "Percent no longer rounds the way the card has always rounded.");
        Require(CompletionProgress.Percent(39.9, 40) == 100 && CompletionProgress.Percent(40, 40) == 100,
            "Percent failed to round just under and exactly at the total.");
        Require(CompletionProgress.Percent(double.MaxValue, 0.0001) == 100,
            "Percent overflowed instead of clamping.");
    }

    private static void HoursLeftIsNeverNegative()
    {
        double?[] playedValues = { null, 0d, 0.4d, 5d, 40d, 120d, -3d, double.NaN };
        double?[] totalValues = { null, 0d, -2d, 0.4d, 12d, 40d, double.PositiveInfinity };
        double?[] ratioValues = { null, 0d, 0.42d, 1d, 1.5d, -0.2d };
        string?[] categoryValues = { null, "finished", "new", "wishlist", "installed", "meh", "2d" };
        int checkedCount = 0;
        foreach (double? hours in playedValues)
            foreach (double? total in totalValues)
                foreach (double? ratio in ratioValues)
                    foreach (string? category in categoryValues)
                        foreach (bool flagged in new[] { false, true })
                        {
                            var estimate = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
                                GameId: "matrix", PlayedHours: hours, CatalogMainStoryHours: total,
                                MainStoryHours: total, MainPlusExtrasHours: total, CompletionHours: total,
                                AchievementRatio: ratio, CategoryId: category, UserMarkedComplete: flagged,
                                Wishlisted: flagged, BacklogAware: !flagged, CatalogHoursVerified: flagged,
                                LastPlayedUtc: flagged ? new DateTime(2026, 7, 7, 0, 0, 0, DateTimeKind.Utc) : null,
                                Added: new DateTime(2025, 7, 7, 0, 0, 0, DateTimeKind.Utc)));
                            checkedCount++;
                            string context = "played=" + hours + " total=" + total + " ratio=" + ratio
                                + " category=" + category + " flagged=" + flagged;
                            Require(estimate.PercentComplete >= 0 && estimate.PercentComplete <= 100,
                                "Estimate produced " + estimate.PercentComplete + " percent for " + context + ".");
                            Require(estimate.HoursLeft >= 0 && double.IsFinite(estimate.HoursLeft),
                                "Hours left was " + estimate.HoursLeft + " for " + context + ".");
                            Require(estimate.PercentComplete < 100 || estimate.HoursLeft == 0,
                                "A complete game reported hours left for " + context + ".");
                            Require(estimate.PercentComplete == 0
                                || estimate.Confidence >= CompletionProgress.CompletionConfidence.Low,
                                "A non-zero percentage was published without at least Low confidence for " + context + ".");
                            Require(!string.IsNullOrWhiteSpace(estimate.Basis),
                                "An estimate was published without a basis naming its signals for " + context + ".");
                            string formatted = CompletionProgress.Format(estimate);
                            Require(formatted.Contains(estimate.PercentComplete.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
                                "The user-facing string does not show the percentage: " + formatted);
                        }
        Require(checkedCount >= 3000, "The invariant sweep only covered " + checkedCount + " signal combinations.");

        var negativeTotal = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "negative-guard", PlayedHours: 1d, CatalogMainStoryHours: -40d, MainStoryHours: 0d, CompletionHours: -1d));
        Require(negativeTotal.PercentComplete == 0 && negativeTotal.HoursLeft == 0 && negativeTotal.HoursTotal is null,
            "A negative total produced an hours-left figure: " + negativeTotal.Basis);
    }

    private static void DenominatorChoiceAndBacklogAwareness()
    {
        var backlog = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "backlog", PlayedHours: 10, CatalogMainStoryHours: 40, CatalogHoursVerified: true, BacklogAware: true));
        Require(backlog.PercentComplete == 25 && backlog.Confidence == CompletionProgress.CompletionConfidence.Low,
            "Backlog-aware play must be reported as Low confidence, was " + backlog.Confidence + ".");
        Require(backlog.Basis.Contains("backlog", StringComparison.Ordinal),
            "The basis did not say that backlog content is excluded: " + backlog.Basis);

        var unverified = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "unverified", PlayedHours: 20, CatalogMainStoryHours: 40, CatalogHoursVerified: false));
        Require(unverified.PercentComplete == 50 && unverified.Confidence == CompletionProgress.CompletionConfidence.Low
            && unverified.Basis.Contains("unverified", StringComparison.Ordinal),
            "An unverified catalog duration was published as a Medium-confidence percentage: " + unverified.Basis);

        var withExtras = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "extras", PlayedHours: 30, CatalogMainStoryHours: 40, CatalogHoursVerified: true, MainPlusExtrasHours: 80));
        Require(withExtras.PercentComplete == 75 && withExtras.Confidence == CompletionProgress.CompletionConfidence.Medium,
            "A main-plus-extras figure must not outrank the catalog main-story figure: " + withExtras.Basis);

        var completionistOnly = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "completionist", PlayedHours: 30, CompletionHours: 120));
        Require(completionistOnly.PercentComplete == 25
            && completionistOnly.Confidence == CompletionProgress.CompletionConfidence.Low
            && completionistOnly.Basis.Contains("completionist", StringComparison.Ordinal),
            "A completionist-only figure was published as a reliable percentage: " + completionistOnly.Basis);
        Require(completionistOnly.HoursTotal == 120 && Near(completionistOnly.HoursLeft, 90),
            "A completionist-only figure reported the wrong hours left: " + completionistOnly.HoursLeft + ".");
    }

    private static void BadSignalsAreIgnoredRatherThanClamped()
    {
        var tooHigh = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "ratio-too-high", PlayedHours: 20, CatalogMainStoryHours: 40, CatalogHoursVerified: true, AchievementRatio: 1.5d));
        Require(tooHigh.PercentComplete == 50 && tooHigh.Confidence == CompletionProgress.CompletionConfidence.Medium,
            "A ratio above 1 was turned into a fake percentage instead of being ignored: " + tooHigh.Basis);

        var negative = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "ratio-negative", PlayedHours: 20, CatalogMainStoryHours: 40, CatalogHoursVerified: true, AchievementRatio: -0.2d));
        Require(negative.PercentComplete == 50 && negative.Confidence == CompletionProgress.CompletionConfidence.Medium,
            "A negative ratio was used as evidence: " + negative.Basis);

        var notFinite = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "ratio-nan", PlayedHours: 20, CatalogMainStoryHours: 40, CatalogHoursVerified: true, AchievementRatio: double.NaN));
        Require(notFinite.PercentComplete == 50, "A NaN ratio was treated as evidence: " + notFinite.Basis);

        var unverifiedEvidence = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "evidence-unverified", PlayedHours: 10, MainStoryHours: 40, MainStoryHoursVerified: false));
        Require(unverifiedEvidence.PercentComplete == 25
            && unverifiedEvidence.Confidence == CompletionProgress.CompletionConfidence.Low
            && unverifiedEvidence.Basis.Contains("not passed metadata evidence verification", StringComparison.Ordinal),
            "Unverified metadata evidence was published as a Medium-confidence percentage: " + unverifiedEvidence.Basis);

        var garbageTotal = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "garbage-total", PlayedHours: 10, CatalogMainStoryHours: double.NaN, MainPlusExtrasHours: double.PositiveInfinity));
        Require(garbageTotal.PercentComplete == 0 && garbageTotal.HoursTotal is null
            && garbageTotal.Confidence == CompletionProgress.CompletionConfidence.Unknown,
            "A non-finite total produced a percentage: " + garbageTotal.Basis);
    }

    private static void DeriveAccumulatesMonotonically()
    {
        var history = new List<CompletionProgress.CompletionSignals>();
        var start = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (double session in new[] { 5d, 10d, 20d, 30d, 40d, 45d, 55d, 60d })
            history.Add(new CompletionProgress.CompletionSignals(
                GameId: "sessions", PlayedHours: session, CatalogMainStoryHours: 40, CatalogHoursVerified: true,
                LastPlayedUtc: start.AddHours(session)));

        CompletionProgress.CompletionEstimate? previous = null;
        int previousPercent = -1;
        double previousLeft = double.MaxValue;
        var seen = new List<int>();
        foreach (var session in history)
        {
            previous = CompletionProgress.Derive(previous, session);
            seen.Add(previous.PercentComplete);
            Require(previous.PercentComplete >= previousPercent,
                "Derived completion went backwards: " + previousPercent + " percent then "
                + previous.PercentComplete + " percent at " + session.PlayedHours + " hours played.");
            Require(previous.HoursLeft >= 0,
                "Derived hours left was negative at " + session.PlayedHours + " hours played.");
            Require(previous.HoursLeft <= previousLeft + 0.0001,
                "Derived hours left jumped up from " + previousLeft + " to " + previous.HoursLeft
                + " at " + session.PlayedHours + " hours played.");
            previousPercent = previous.PercentComplete;
            previousLeft = previous.HoursLeft;
        }
        Require(seen.Count == 8 && seen[0] == 13 && seen[4] == 100 && seen[7] == 100,
            "The derived sequence is not the documented one: " + string.Join(", ", seen));
        Require(previous!.PercentComplete == 100 && previous.HoursLeft == 0
            && previous.Confidence >= CompletionProgress.CompletionConfidence.Medium,
            "Deriving past the total did not settle on 100 percent with 0 hours left: " + previous.Basis);

        var folded = CompletionProgress.EstimateFor(history);
        Require(folded.PercentComplete == previous.PercentComplete && Near(folded.HoursLeft, previous.HoursLeft),
            "EstimateFor and a Derive fold disagreed: " + folded.PercentComplete
            + " percent against " + previous.PercentComplete + " percent.");

        var stickyFinished = CompletionProgress.Derive(
            CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(GameId: "sticky", CategoryId: "finished")),
            new CompletionProgress.CompletionSignals(GameId: "sticky", PlayedHours: 2,
                CatalogMainStoryHours: 40, CatalogHoursVerified: true));
        Require(stickyFinished.PercentComplete == 100
            && stickyFinished.Confidence == CompletionProgress.CompletionConfidence.Exact,
            "A finished-category completion was lost when playtime kept arriving: " + stickyFinished.Basis);

        var stickyFlag = CompletionProgress.Derive(
            CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(GameId: "sticky-flag", UserMarkedComplete: true)),
            new CompletionProgress.CompletionSignals(GameId: "sticky-flag", PlayedHours: 2));
        Require(stickyFlag.PercentComplete == 100
            && stickyFlag.Confidence == CompletionProgress.CompletionConfidence.Exact,
            "A user's completion flag was lost on the next derive.");
    }

    private static void SmoothingIsStableAndResetEscapesIt()
    {
        // A one-session gap in a weaker signal must not pull the displayed number down.
        CompletionProgress.CompletionEstimate? running = null;
        foreach (var session in new[]
        {
            new CompletionProgress.CompletionSignals(GameId: "smooth", PlayedHours: 20, CatalogMainStoryHours: 40, CatalogHoursVerified: true),
            new CompletionProgress.CompletionSignals(GameId: "smooth", PlayedHours: 21, CatalogMainStoryHours: 40, CatalogHoursVerified: true),
            new CompletionProgress.CompletionSignals(GameId: "smooth"),
            new CompletionProgress.CompletionSignals(GameId: "smooth", PlayedHours: 22, CatalogMainStoryHours: 40, CatalogHoursVerified: true)
        })
            running = CompletionProgress.Derive(running, session);
        Require(running!.PercentComplete >= 50,
            "Smoothing collapsed on a weaker signal: " + running.PercentComplete + " percent.");

        // One strong session shows its true percentage immediately: no lagging first read.
        var single = CompletionProgress.EstimateFor(new[]
        {
            new CompletionProgress.CompletionSignals(GameId: "stable", PlayedHours: 30, CatalogMainStoryHours: 40, CatalogHoursVerified: true)
        });
        Require(single.PercentComplete == 75,
            "A single strong session must show its true percentage, was " + single.PercentComplete + ".");
        Require(single.Basis.Contains("30.0 h played", StringComparison.Ordinal),
            "A single-session basis lost the arithmetic: " + single.Basis);

        // The user's reset: the play ledger moves backwards, so the raw estimate is restored.
        CompletionProgress.CompletionEstimate? beforeReset = null;
        foreach (double hours in new[] { 5d, 20d, 30d })
            beforeReset = CompletionProgress.Derive(beforeReset, new CompletionProgress.CompletionSignals(
                GameId: "reset", PlayedHours: hours, CatalogMainStoryHours: 40, CatalogHoursVerified: true));
        var afterReset = CompletionProgress.Derive(beforeReset, new CompletionProgress.CompletionSignals(
            GameId: "reset", PlayedHours: 2, CatalogMainStoryHours: 40, CatalogHoursVerified: true));
        Require(afterReset.PercentComplete == 5,
            "A progress reset did not restore the raw estimate: " + afterReset.PercentComplete
            + " percent - " + afterReset.Basis);
        Require(afterReset.Basis.Contains("reset", StringComparison.Ordinal),
            "A progress reset was not named in the basis: " + afterReset.Basis);

        // An unsorted history still smooths deterministically.
        var unsorted = new[]
        {
            new CompletionProgress.CompletionSignals(GameId: "order", PlayedHours: 30, CatalogMainStoryHours: 40,
                CatalogHoursVerified: true, Added: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            new CompletionProgress.CompletionSignals(GameId: "order", PlayedHours: 5, CatalogMainStoryHours: 40,
                CatalogHoursVerified: true, Added: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc))
        };
        Require(CompletionProgress.EstimateFor(unsorted) == CompletionProgress.EstimateFor(unsorted.Reverse()),
            "Smoothing an unsorted history did not order the sessions deterministically.");
    }

    private static void FutureGameNeedsNoSpecialCasing()
    {
        // Identities this estimator has never been shown, estimated by the same rules.
        var brandNew = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "steam:9999999", PlayedHours: 5, MainStoryHours: 20, MainStoryHoursVerified: true,
            LastPlayedUtc: new DateTime(2030, 12, 25, 0, 0, 0, DateTimeKind.Utc),
            Added: new DateTime(2030, 12, 20, 0, 0, 0, DateTimeKind.Utc)));
        Require(brandNew.PercentComplete == 25,
            "A brand-new game with only a total was estimated at " + brandNew.PercentComplete + " percent.");
        Require(Near(brandNew.HoursLeft, 15),
            "A brand-new game reported " + brandNew.HoursLeft + " hours left.");
        Require(brandNew.Confidence == CompletionProgress.CompletionConfidence.Medium,
            "A brand-new game with verified main-story evidence reported " + brandNew.Confidence + ".");

        var otherNew = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "local:9f86d081884c7d659a2feaa0c55ad015", PlayedHours: 3, MainStoryHours: 12, MainStoryHoursVerified: true));
        Require(otherNew.PercentComplete == 25 && Near(otherNew.HoursLeft, 9),
            "A second unseen identity was estimated differently, which would mean per-game special cases: " + otherNew.Basis);

        var unstartedTomorrow = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "brand-new-unstarted", CatalogMainStoryHours: 47.5, CatalogHoursVerified: true));
        Require(unstartedTomorrow.PercentComplete == 0 && Near(unstartedTomorrow.HoursLeft, 47.5),
            "A game added tomorrow with only a known total was not reported as unstarted: " + unstartedTomorrow.Basis);

        var finishedTomorrow = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "brand-new-finished", CategoryId: "finished"));
        Require(finishedTomorrow.PercentComplete == 100
            && finishedTomorrow.Confidence == CompletionProgress.CompletionConfidence.Exact,
            "A game moved to the finished category tomorrow would not be exact.");

        // The Game-to-signals wiring the manager will use.
        var game = new Game
        {
            Id = "harness",
            Name = "Harness",
            Category = "rpg",
            Time = 40,
            TimeVerifiedAt = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc),
            PlayedHours = 30,
            LastPlayedUtc = new DateTime(2026, 3, 3, 0, 0, 0, DateTimeKind.Utc),
            Added = new DateTime(2025, 12, 12, 0, 0, 0, DateTimeKind.Utc)
        };
        var wired = CompletionProgress.Estimate(CompletionProgress.FromGame(game));
        Require(wired.PercentComplete == 75 && wired.Confidence == CompletionProgress.CompletionConfidence.Medium
            && wired.HoursTotal == 40 && wired.HoursElapsed == 30,
            "FromGame did not carry the real game fields into the estimate: " + wired.Basis);
        var wishlisted = CompletionProgress.Estimate(CompletionProgress.FromGame(new Game { Id = "w", Wishlisted = true }));
        Require(wishlisted.PercentComplete == 0
            && wishlisted.Confidence == CompletionProgress.CompletionConfidence.Unknown,
            "A wishlisted game with nothing else was not surfaced as unknown.");
        var utilityGame = CompletionProgress.Estimate(CompletionProgress.FromGame(new Game { Id = "u", IsNonGame = true, Time = 40 }));
        Require(utilityGame.PercentComplete == 0
            && utilityGame.Confidence == CompletionProgress.CompletionConfidence.Unknown,
            "A utility image was given a campaign percentage.");
    }

    private static void HistoryBelongsToOneGame()
    {
        bool mixedRejected = false;
        try
        {
            CompletionProgress.EstimateFor(new[]
            {
                new CompletionProgress.CompletionSignals(GameId: "game-a", PlayedHours: 1, CatalogMainStoryHours: 10, CatalogHoursVerified: true),
                new CompletionProgress.CompletionSignals(GameId: "game-b", PlayedHours: 9, CatalogMainStoryHours: 10, CatalogHoursVerified: true)
            });
        }
        catch (ArgumentException) { mixedRejected = true; }
        Require(mixedRejected, "A history mixing two games was smoothed into one number.");

        var anonymous = CompletionProgress.EstimateFor(new[]
        {
            new CompletionProgress.CompletionSignals(PlayedHours: 10, CatalogMainStoryHours: 40, CatalogHoursVerified: true),
            new CompletionProgress.CompletionSignals(PlayedHours: 20, CatalogMainStoryHours: 40, CatalogHoursVerified: true)
        });
        Require(anonymous.PercentComplete > 25 && anonymous.PercentComplete < 50,
            "An anonymous history did not smooth between its first and last reading: "
            + anonymous.PercentComplete + " percent.");

        bool nullRejected = false;
        try { CompletionProgress.EstimateFor(null!); }
        catch (ArgumentNullException) { nullRejected = true; }
        Require(nullRejected, "A null history was accepted.");
    }

    private static void HistorySurvivesAProfileRoundTrip(string root)
    {
        string folder = Path.Combine(root, "completion-progress");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "sessions.csv");
        var sessions = new List<CompletionProgress.CompletionSignals>();
        var lines = new List<string>();
        var stamp = new DateTime(2025, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (double hours in new[] { 2.5, 9.25, 18d, 27.5, 33d })
        {
            stamp = stamp.AddDays(7);
            sessions.Add(new CompletionProgress.CompletionSignals(
                GameId: "round-trip", PlayedHours: hours, CatalogMainStoryHours: 40, CatalogHoursVerified: true,
                LastPlayedUtc: stamp, Added: new DateTime(2025, 11, 1, 0, 0, 0, DateTimeKind.Utc), CategoryId: "rpg"));
            lines.Add(string.Join(";", "round-trip", hours.ToString("0.####", CultureInfo.InvariantCulture),
                "40", stamp.ToString("O")));
        }
        File.WriteAllLines(path, lines);

        var replayed = new List<CompletionProgress.CompletionSignals>();
        foreach (string line in File.ReadAllLines(path))
        {
            string[] parts = line.Split(';');
            Require(parts.Length == 4, "The persisted play-session fixture is malformed.");
            replayed.Add(new CompletionProgress.CompletionSignals(
                GameId: parts[0],
                PlayedHours: double.Parse(parts[1], CultureInfo.InvariantCulture),
                CatalogMainStoryHours: double.Parse(parts[2], CultureInfo.InvariantCulture),
                CatalogHoursVerified: true,
                LastPlayedUtc: DateTime.Parse(parts[3], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Added: new DateTime(2025, 11, 1, 0, 0, 0, DateTimeKind.Utc),
                CategoryId: "rpg"));
        }
        Require(replayed.Count == sessions.Count,
            "The persisted play-session fixture did not survive the profile round trip.");
        var original = CompletionProgress.EstimateFor(sessions);
        var restored = CompletionProgress.EstimateFor(replayed);
        Require(restored.PercentComplete == original.PercentComplete && Near(restored.HoursLeft, original.HoursLeft),
            "Replaying a persisted history produced a different number: " + restored.PercentComplete
            + " percent against " + original.PercentComplete + " percent.");
        Require(original.PercentComplete >= 75 && original.PercentComplete <= 85,
            "Five sessions of a 40 hour game did not land near its true completion: "
            + original.PercentComplete + " percent.");
        Require(restored.Confidence == CompletionProgress.CompletionConfidence.Medium,
            "A replayed history lost its confidence: " + restored.Confidence + ".");
    }

    private static void FormatMatchesTheDocumentedStrings()
    {
        var zero = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "format-zero", CatalogMainStoryHours: 34, CatalogHoursVerified: true));
        Require(CompletionProgress.Format(zero) == "0% - about 34.0 h left",
            "The documented 0 percent string is wrong: " + CompletionProgress.Format(zero));

        var middle = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "format-middle", PlayedHours: 8.8, CatalogMainStoryHours: 14.2, CatalogHoursVerified: true));
        Require(middle.PercentComplete == 62 && Near(middle.HoursLeft, 5.396),
            "The mid-range fixture no longer produces 62 percent and 5.4 hours: "
            + middle.PercentComplete + " percent and " + middle.HoursLeft + " hours.");
        Require(CompletionProgress.Format(middle) == "62% - about 5.4 h left",
            "The documented mid string is wrong: " + CompletionProgress.Format(middle));

        var finished = CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "format-finished", CategoryId: "finished", CatalogMainStoryHours: 22));
        Require(CompletionProgress.Format(finished) == "100% - finished",
            "The documented 100 percent string is wrong: " + CompletionProgress.Format(finished));

        Require(CompletionProgress.Format(null) == "0% - no sourced estimate",
            "A missing estimate did not format as unknown.");
        Require(CompletionProgress.Format(CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
            GameId: "format-ratio", AchievementRatio: 0.42, CatalogMainStoryHours: 14.2))) == "42% - about 8.2 h left",
            "A trophy-based string is wrong: "
            + CompletionProgress.Format(CompletionProgress.Estimate(new CompletionProgress.CompletionSignals(
                GameId: "format-ratio", AchievementRatio: 0.42, CatalogMainStoryHours: 14.2))));
        foreach (CompletionProgress.CompletionEstimate estimate in new[]
        {
            zero, middle, finished, CompletionProgress.Estimate(new CompletionProgress.CompletionSignals())
        })
            Require(estimate.Basis.Length > 0,
                "A formatted estimate has no Basis to show as a tooltip.");
    }

    private static void EstimateIsPureAndDeterministic()
    {
        var signals = new CompletionProgress.CompletionSignals(
            GameId: "pure", PlayedHours: 17.5, CatalogMainStoryHours: 35, CatalogHoursVerified: true,
            LastPlayedUtc: new DateTime(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc), CategoryId: "rpg");
        var first = CompletionProgress.Estimate(signals);
        var second = CompletionProgress.Estimate(signals);
        Require(first == second, "Estimate is not deterministic for identical signals.");
        Require(signals.PlayedHours == 17.5 && signals.CategoryId == "rpg" && signals.GameId == "pure",
            "Estimate mutated the signals it was given.");
        Require(first.PercentComplete == CompletionProgress.Percent(signals.PlayedHours, signals.CatalogMainStoryHours),
            "Percent and Estimate disagree for the same two figures.");
        for (int i = 0; i < 3; i++)
            Require(CompletionProgress.EstimateFor(new[] { signals }) == first,
                "EstimateFor is not stable across repeated calls.");
    }

    private static bool Near(double value, double expected) => Math.Abs(value - expected) < 0.0001;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
