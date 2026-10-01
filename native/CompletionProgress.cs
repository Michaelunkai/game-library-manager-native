using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace GameLibrary.Native;

/// <summary>
/// Data-driven completion estimation for every game card, now and for games added later.
///
/// The engine is driven only by <see cref="CompletionSignals"/>, and every signal is a value
/// this app already has: the playtime ledger (<c>UserState.PlayTimeSeconds</c> as
/// <c>Game.PlayedHours</c>), the catalog duration (<c>Game.Time</c> / <c>Game.TimeVerifiedAt</c>),
/// the metadata evidence cache (<c>MetadataField.MainStoryHours</c>,
/// <c>MainPlusExtrasHours</c>, <c>CompletionistHours</c>), the user's category assignment
/// (<c>Game.Category</c>), wishlist membership (<c>Game.Wishlisted</c> /
/// <c>UserState.Wishlist</c>), and the catalog add/last-played stamps. There is no per-title
/// branch, no title table, and no lookup anywhere in this file, so a game added tomorrow is
/// estimated by exactly the same code path as one present today.
///
/// Honesty rules that this class enforces:
/// a percentage is never invented, hours left are never negative, and the confidence that
/// justifies a number is always returned next to it together with a human-readable basis.
/// </summary>
internal static class CompletionProgress
{
    // Real category identities in this app.
    //
    // "finished" is the completion category: GameClassification.BuiltInProtected is
    // { "finished", "not_for_me", "meh", "hyperv" } and protected assignments are never
    // rewritten by classification, so a game sitting in "finished" is the user's own record
    // that it is done. "not_for_me", "meh", and "hyperv" are protected for the same reason
    // but explicitly do NOT mean completion, so they are never treated as 100%.
    //
    // "new" is the unstarted category: CategoryVisibility.CompleteDefinitions always offers
    // Add("new", "New arrivals"), and Game.Category defaults to "new".
    //
    // "all", "wishlist", and "installed" are never category assignments (they are filtered out
    // of the category list in CategoryVisibility.CompleteDefinitions and Editors.cs); wishlist
    // membership is the separate Game.Wishlisted flag backed by UserState.Wishlist.
    internal const string FinishedCategoryId = "finished";
    internal const string UnstartedCategoryId = "new";
    internal const string WishlistTabId = "wishlist";

    /// <summary>Playing this far past the known total is unambiguous completionist evidence.</summary>
    private const double OverplayConfidenceThreshold = 1.5;
    /// <summary>Share of the newest raw estimate applied by one smoothing step, before confidence.</summary>
    private const double SmoothingBase = 0.5;
    private const double SmoothingPerConfidenceStep = 0.1;
    private const string UnknownBasis = "No play time and no sourced completion-time estimate: completion is unknown.";

    /// <summary>How much the returned number can be trusted. Never a hidden guess.</summary>
    internal enum CompletionConfidence
    {
        /// <summary>Nothing is known; the percentage is a placeholder, not a measurement.</summary>
        Unknown = 0,
        /// <summary>A real total exists but no play time, or the total is an over-sized denominator.</summary>
        Low = 1,
        /// <summary>Played hours against a sourced main-story total.</summary>
        Medium = 2,
        /// <summary>An independent completion ratio, or clear over-play.</summary>
        High = 3,
        /// <summary>The user's own statement that the game is finished.</summary>
        Exact = 4
    }

    /// <summary>
    /// One displayed completion number. PercentComplete is always an int in 0..100 and
    /// HoursLeft is always &gt;= 0; Basis names the signals that produced both.
    /// </summary>
    internal sealed record CompletionEstimate(
        int PercentComplete,
        double HoursLeft,
        double? HoursTotal,
        double? HoursElapsed,
        CompletionConfidence Confidence,
        string Basis);

    /// <summary>
    /// Every input the estimator is allowed to see. Defaults are "not known", so a caller can
    /// pass only what it has.
    /// </summary>
    /// <param name="GameId">Game identity, used only to keep one game's sessions apart.</param>
    /// <param name="PlayedHours">Game.PlayedHours (UserState.PlayTimeSeconds / 3600).</param>
    /// <param name="CatalogMainStoryHours">Game.Time, the catalog main-story duration.</param>
    /// <param name="CatalogHoursVerified">Game.TimeVerifiedAt != default.</param>
    /// <param name="MainStoryHours">MetadataField.MainStoryHours evidence value.</param>
    /// <param name="MainStoryHoursVerified">That evidence passed MetadataEvidence.Evaluate.</param>
    /// <param name="MainPlusExtrasHours">MetadataField.MainPlusExtrasHours evidence value.</param>
    /// <param name="CompletionHours">MetadataField.CompletionistHours evidence value.</param>
    /// <param name="LastPlayedUtc">Game.LastPlayedUtc (UserState.LastPlayedUtc).</param>
    /// <param name="Added">Game.Added, the catalog add date.</param>
    /// <param name="CategoryId">Game.Category, a category assignment id.</param>
    /// <param name="Wishlisted">Game.Wishlisted (UserState.Wishlist).</param>
    /// <param name="UserMarkedComplete">The user's explicit completion action.</param>
    /// <param name="AchievementRatio">Unlocked achievements/trophies as 0..1, when sourced.</param>
    /// <param name="BacklogAware">Known unplayed backlog content is not in the known total.</param>
    /// <param name="IsNonGame">Game.IsNonGame: a utility or backup image.</param>
    internal sealed record CompletionSignals(
        string? GameId = null,
        double? PlayedHours = null,
        double? CatalogMainStoryHours = null,
        bool CatalogHoursVerified = false,
        double? MainStoryHours = null,
        bool MainStoryHoursVerified = false,
        double? MainPlusExtrasHours = null,
        double? CompletionHours = null,
        DateTime? LastPlayedUtc = null,
        DateTime? Added = null,
        string? CategoryId = null,
        bool Wishlisted = false,
        bool UserMarkedComplete = false,
        double? AchievementRatio = null,
        bool BacklogAware = false,
        bool IsNonGame = false);

    /// <summary>The one denominator chosen for this game, with the confidence it can support.</summary>
    private sealed record Denominator(double? Hours, string Source, string Note, CompletionConfidence Ceiling);

    /// <summary>True only for the category this app uses to record a finished game.</summary>
    internal static bool IsFinishedCategory(string? categoryId) =>
        categoryId is not null && string.Equals(categoryId.Trim(), FinishedCategoryId, StringComparison.OrdinalIgnoreCase);

    /// <summary>True for the always-present unstarted category.</summary>
    internal static bool IsUnstartedCategory(string? categoryId) =>
        categoryId is not null && string.Equals(categoryId.Trim(), UnstartedCategoryId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds signals straight from a loaded game plus its metadata evidence, so the manager
    /// cannot accidentally wire a field this engine never reads.
    /// </summary>
    internal static CompletionSignals FromGame(Game game, double? mainStoryHours = null, bool mainStoryHoursVerified = false,
        double? mainPlusExtrasHours = null, double? completionHours = null, double? achievementRatio = null,
        bool userMarkedComplete = false, bool backlogAware = false)
    {
        ArgumentNullException.ThrowIfNull(game);
        return new CompletionSignals(
            GameId: game.Id,
            PlayedHours: game.PlayedHours,
            CatalogMainStoryHours: game.Time,
            CatalogHoursVerified: game.TimeVerifiedAt != default,
            MainStoryHours: mainStoryHours,
            MainStoryHoursVerified: mainStoryHoursVerified,
            MainPlusExtrasHours: mainPlusExtrasHours,
            CompletionHours: completionHours,
            LastPlayedUtc: game.LastPlayedUtc == default ? null : game.LastPlayedUtc,
            Added: game.Added == default ? null : game.Added,
            CategoryId: game.Category,
            Wishlisted: game.Wishlisted,
            UserMarkedComplete: userMarkedComplete,
            AchievementRatio: achievementRatio,
            BacklogAware: backlogAware,
            IsNonGame: game.IsNonGame);
    }

    /// <summary>
    /// Estimates one game's completion from its signals, in strict evidence order:
    /// the user's own statement, then an independent completion ratio, then played hours
    /// against a sourced total. Never returns a fabricated number: when nothing is known the
    /// percentage is 0 with <see cref="CompletionConfidence.Unknown"/>.
    /// </summary>
    internal static CompletionEstimate Estimate(CompletionSignals? signals)
    {
        if (signals is null)
            return Materialize(0, null, null, CompletionConfidence.Unknown, "No signals were supplied, so completion is unknown.");
        if (signals.IsNonGame)
            return Materialize(0, null, null, CompletionConfidence.Unknown,
                "Utility or backup entry: campaign completion does not apply.");

        double? played = Positive(signals.PlayedHours);
        // A ledger entry can be zero while the game has a real last-played stamp, and a
        // missing ledger value can still come with a session, so either one means "played".
        bool everPlayed = played.HasValue || IsStamp(signals.LastPlayedUtc);
        Denominator total = ResolveTotal(signals);

        // 1. The user's own statement is the only evidence that is exact.
        if (signals.UserMarkedComplete)
            return Exact(signals, played, total, "You marked this game as finished");
        if (IsFinishedCategory(signals.CategoryId))
            return Exact(signals, played, total, $"Category '{FinishedCategoryId}' records this game as finished");

        // 2. An independent completion ratio is the strongest automatic signal.
        if (signals.AchievementRatio is double ratio && double.IsFinite(ratio) && ratio >= 0 && ratio <= 1)
        {
            int trophyPercent = Percent(ratio * 100d, 100d);
            string basis = trophyPercent + "% of the trophy set is unlocked"
                + (total.Hours is > 0
                    ? $"; hours left use the {Hours(total.Hours.Value)} h {total.Source}"
                    : "; hours left are unknown without a sourced completion-time estimate");
            return Materialize(trophyPercent, total.Hours, ElapsedFor(signals, trophyPercent, total.Hours), CompletionConfidence.High, basis);
        }

        // 3. No denominator at all: completion cannot be computed, and is not guessed.
        if (total.Hours is not > 0)
            return played is > 0
                ? Materialize(0, null, played, CompletionConfidence.Unknown,
                    $"{Hours(played.Value)} h played but no sourced completion-time estimate exists, so completion cannot be computed.")
                : Materialize(0, null, null, CompletionConfidence.Unknown, UnknownBasis);

        // 4. A real total and no play time: not started, so 0% with the whole total ahead.
        if (!everPlayed)
        {
            string state = signals.Wishlisted ? "On the wishlist and not played"
                : IsUnstartedCategory(signals.CategoryId) ? "In New arrivals and not played"
                : "Not played";
            return Materialize(0, total.Hours, null, CompletionConfidence.Low,
                state + $"; the {Hours(total.Hours.Value)} h {total.Source} is still ahead");
        }

        // 5. A real total and real play time.
        double hours = played ?? 0d;
        int percent = Percent(hours, total.Hours);
        CompletionConfidence confidence = total.Ceiling;
        string math = $"{Hours(hours)} h played of a {Hours(total.Hours.Value)} h {total.Source}";
        if (hours > total.Hours.Value)
        {
            // Over-play is real evidence of completion, so it never lowers confidence.
            math = $"{Hours(hours)} h played past the {Hours(total.Hours.Value)} h {total.Source}";
            if (hours >= total.Hours.Value * OverplayConfidenceThreshold && confidence < CompletionConfidence.High)
                confidence = CompletionConfidence.High;
        }
        if (signals.BacklogAware)
        {
            // The known total omits backlog content, so the ratio under-reaches real completion.
            if (confidence > CompletionConfidence.Low) confidence = CompletionConfidence.Low;
            math += " · unplayed backlog content is not in this estimate, so completion is under-reported";
        }
        if (total.Note.Length > 0) math += " · " + total.Note;
        return Materialize(percent, total.Hours, hours, confidence, math);
    }

    /// <summary>
    /// Smooths one game's estimate across its own play sessions so the displayed number is
    /// stable instead of jumping. The result never decreases while the play ledger grows, and
    /// returns to the raw estimate when the ledger moves backwards, which is the user's reset.
    /// Sessions belonging to different games are rejected rather than blended.
    /// </summary>
    internal static CompletionEstimate EstimateFor(IEnumerable<CompletionSignals> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var samples = history.ToList();
        if (samples.Count == 0)
            return Materialize(0, null, null, CompletionConfidence.Unknown, "No play session has been recorded yet.");

        foreach (var sample in samples)
            if (sample?.GameId is string id && samples[0]?.GameId is string first
                && !string.Equals(id, first, StringComparison.Ordinal))
                throw new ArgumentException(
                    $"A completion history must hold one game's sessions; '{first}' and '{id}' were mixed.", nameof(history));

        // Stable ordering: an unordered history still smooths deterministically.
        var ordered = samples.Where(sample => sample is not null)
            .Select((sample, index) => (Sample: sample, Index: index))
            .OrderBy(entry => entry.Sample.Added ?? DateTime.MinValue)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Sample)
            .ToList();

        CompletionEstimate? running = null;
        foreach (var sample in ordered) running = Smooth(running, Estimate(sample));
        return running ?? Materialize(0, null, null, CompletionConfidence.Unknown, UnknownBasis);
    }

    /// <summary>
    /// The update rule the manager calls as the user keeps playing: accuracy accumulates,
    /// the displayed number never goes backwards, and an explicit completion always sticks.
    /// </summary>
    internal static CompletionEstimate Derive(CompletionEstimate? previous, CompletionSignals next)
        => Smooth(previous, Estimate(next));

    /// <summary>
    /// The single place a completion percentage is computed from two hour figures.
    /// A negative or unknown value is 0; anything above the total is 100.
    /// </summary>
    internal static int Percent(double? playedHours, double? totalHours)
    {
        if (totalHours is not double total || !double.IsFinite(total) || total <= 0) return 0;
        if (playedHours is not double played || !double.IsFinite(played) || played <= 0) return 0;
        // Compare before multiplying: an extreme played figure would otherwise overflow to
        // infinity and read as no progress at all.
        if (played >= total) return 100;
        double ratio = 100d * played / total;
        if (double.IsPositiveInfinity(ratio)) return 100;
        if (!double.IsFinite(ratio) || ratio <= 0) return 0;
        if (ratio >= 100) return 100;
        return (int)Math.Round(ratio, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The user-facing string: "62% - about 5.4 h left", "0% - about 34.0 h left",
    /// "100% - finished". The Basis is surfaced separately as a tooltip.
    /// </summary>
    internal static string Format(CompletionEstimate? estimate)
    {
        if (estimate is null) return "0% - no sourced estimate";
        if (estimate.PercentComplete >= 100)
            return estimate is { HoursTotal: > 0 } && estimate.Confidence < CompletionConfidence.High
                ? $"100% - played past the {Hours(estimate.HoursTotal.Value)} h estimate"
                : "100% - finished";
        if (estimate.Confidence == CompletionConfidence.Unknown) return estimate.PercentComplete + "% - no sourced estimate";
        if (estimate.HoursTotal is not > 0) return estimate.PercentComplete + "% - hours left unknown";
        return estimate.PercentComplete + "% - about " + Hours(estimate.HoursLeft) + " h left";
    }

    /// <summary>
    /// One smoothing step. The blend keeps the previous number as a floor, which makes the
    /// displayed percentage non-decreasing, and a shorter elapsed figure than the previous
    /// estimate means the play ledger was reset, so the raw estimate wins again.
    /// </summary>
    private static CompletionEstimate Smooth(CompletionEstimate? previous, CompletionEstimate next)
    {
        if (previous is null) return next;
        if (previous.Confidence == CompletionConfidence.Exact)
            return new CompletionEstimate(100, 0, previous.HoursTotal ?? next.HoursTotal,
                previous.HoursElapsed ?? next.HoursElapsed, CompletionConfidence.Exact, previous.Basis);
        if (next.Confidence == CompletionConfidence.Exact) return next;
        // A raw 100% is never held back: a blend would converge on 100% asymptotically and
        // keep showing 97% for a game that is already finished.
        if (next.PercentComplete >= 100) return next;
        if (next.HoursElapsed is double elapsed && previous.HoursElapsed is double before && elapsed < before)
            return next with { Basis = next.Basis + " · play progress was reset, so earlier smoothing was dropped" };

        double weight = Math.Clamp(SmoothingBase + SmoothingPerConfidenceStep * (int)next.Confidence, 0d, 1d);
        double blended = weight * next.PercentComplete + (1d - weight) * previous.PercentComplete;
        int percent = Math.Clamp(Math.Max(previous.PercentComplete, (int)Math.Round(blended, MidpointRounding.AwayFromZero)), 0, 100);
        string basis = percent == previous.PercentComplete
            ? $"Held at {percent}% across play sessions; latest signals: {next.Basis}"
            : $"Smoothed {previous.PercentComplete}% to {percent}% across play sessions; latest signals: {next.Basis}";
        return Materialize(percent, next.HoursTotal ?? previous.HoursTotal, next.HoursElapsed ?? previous.HoursElapsed, next.Confidence, basis);
    }

    /// <summary>Clamps and derives hours left so every produced estimate is well-formed.</summary>
    private static CompletionEstimate Materialize(int percent, double? total, double? elapsed, CompletionConfidence confidence, string basis)
    {
        percent = Math.Clamp(percent, 0, 100);
        double? hoursTotal = total is double t && double.IsFinite(t) && t > 0 ? t : null;
        double? hoursElapsed = elapsed is double e && double.IsFinite(e) && e > 0 ? e : percent >= 100 ? hoursTotal : null;
        double hoursLeft = percent >= 100 ? 0d
            : hoursTotal is double h ? Math.Max(0d, h - h * percent / 100d)
            : 0d;
        return new CompletionEstimate(percent, hoursLeft, hoursTotal, hoursElapsed, confidence, basis);
    }

    private static CompletionEstimate Exact(CompletionSignals signals, double? played, Denominator total, string why)
    {
        string detail = total.Hours is > 0
            ? played is > 0
                ? $" · {Hours(played.Value)} h played against a {Hours(total.Hours.Value)} h {total.Source}"
                : $" · no play time recorded against a {Hours(total.Hours.Value)} h {total.Source}"
            : "";
        return Materialize(100, total.Hours, played ?? total.Hours, CompletionConfidence.Exact, why + detail + ".");
    }

    /// <summary>
    /// Picks the one denominator for this game: verified main story, then the catalog main-story
    /// figure, then main-plus-extras, then a completionist figure. A denominator that includes
    /// content most players skip can only ever support low confidence.
    /// </summary>
    private static Denominator ResolveTotal(CompletionSignals signals)
    {
        if (Positive(signals.MainStoryHours) is double evidenceMain)
            return new Denominator(evidenceMain, "main-story estimate",
                signals.MainStoryHoursVerified ? "" : "this figure has not passed metadata evidence verification",
                signals.MainStoryHoursVerified ? CompletionConfidence.Medium : CompletionConfidence.Low);
        if (Positive(signals.CatalogMainStoryHours) is double catalogMain)
            return new Denominator(catalogMain, "catalog main-story estimate",
                signals.CatalogHoursVerified ? "" : "the catalog duration is an unverified estimate",
                signals.CatalogHoursVerified ? CompletionConfidence.Medium : CompletionConfidence.Low);
        if (Positive(signals.MainPlusExtrasHours) is double withExtras)
            return new Denominator(withExtras, "main-plus-extras estimate",
                "side content is included, so a main-story run reads lower", CompletionConfidence.Low);
        if (Positive(signals.CompletionHours) is double completionist)
            return new Denominator(completionist, "completionist estimate",
                "a completionist run is assumed, so a normal playthrough reads much lower", CompletionConfidence.Low);
        return new Denominator(null, "", "", CompletionConfidence.Unknown);
    }

    private static double? ElapsedFor(CompletionSignals signals, int percent, double? total) =>
        Positive(signals.PlayedHours) ?? (percent >= 100 && total is > 0 ? total : null);

    private static double? Positive(double? value) =>
        value is double number && double.IsFinite(number) && number > 0 ? number : null;

    private static bool IsStamp(DateTime? value) => value is DateTime stamp && stamp > DateTime.MinValue;

    private static string Hours(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}
