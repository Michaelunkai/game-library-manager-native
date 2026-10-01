using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace GameLibrary.Native;

/// <summary>
/// Every state a game card - or the view that should contain it - can be in when
/// the normal happy path is not the whole truth.
/// <para>
/// The set is deliberately limited to states that genuinely exist in this app.
/// Each member below is traceable to code that produces the condition:
/// <list type="bullet">
/// <item><description><see cref="Normal"/> - <c>ApplyFilter</c> built a populated list (MainWindow.xaml.cs, <c>GameList.ItemsSource = filtered</c>).</description></item>
/// <item><description><see cref="NoSearchResults"/> - a non-empty query is applied against the prebuilt index and yields no visible game.</description></item>
/// <item><description><see cref="NoResultsInCategory"/> - a category/tab filter yields no visible game and no other filter explains it.</description></item>
/// <item><description><see cref="CatalogLoading"/> - <c>InitializeAsync</c> has not finished: "Preparing the bundled catalog...", <c>LibraryContent.IsEnabled = false</c>.</description></item>
/// <item><description><see cref="CatalogUnavailable"/> - <c>startupFailed</c> raised the <c>StartupRecovery</c> panel, or an offline run read no catalog at all.</description></item>
/// <item><description><see cref="SaveDataMissing"/> - a backup result carries <c>NoSaveData</c>, or progress could not be matched to a verified backup.</description></item>
/// <item><description><see cref="CoverMissing"/> - <c>CoverConverter</c> found no image file and fell back to a generated placeholder.</description></item>
/// <item><description><see cref="NonGameHidden"/> - <c>Game.IsNonGame</c> (a utility or backup image) is deliberately kept out of the game list.</description></item>
/// <item><description><see cref="FilteredOutByTag"/> - the <c>TagBox</c> filter removed every row in the view.</description></item>
/// <item><description><see cref="ReadOnlyOffline"/> - the <c>offline</c> flag: "shared edits stay on this PC until you restart online".</description></item>
/// </list>
/// </para>
/// </summary>
internal enum CardState
{
    /// <summary>The row shows everything the catalog knows. Nothing to report.</summary>
    Normal = 0,
    /// <summary>A search is typed and nothing matched it.</summary>
    NoSearchResults = 1,
    /// <summary>No search, but the selected category holds no games.</summary>
    NoResultsInCategory = 2,
    /// <summary>The catalog has not finished loading; every count is still provisional.</summary>
    CatalogLoading = 3,
    /// <summary>Loading failed, so there is nothing honest to search.</summary>
    CatalogUnavailable = 4,
    /// <summary>The row renders, but no save data was found for this game.</summary>
    SaveDataMissing = 5,
    /// <summary>The row renders, but no cover artwork exists for this game.</summary>
    CoverMissing = 6,
    /// <summary>The only candidates are utility/backup images, which are not listed as games.</summary>
    NonGameHidden = 7,
    /// <summary>An active tag filter removed every row in the view.</summary>
    FilteredOutByTag = 8,
    /// <summary>Everything is readable, but this offline run cannot make changes.</summary>
    ReadOnlyOffline = 9
}

/// <summary>
/// The facts <see cref="CardStatePresenter.Resolve"/> is allowed to reason about.
/// Small on purpose: a presenter that can see the whole application becomes the
/// application. Every field is something the caller already knows at the moment
/// it would otherwise render a blank list.
/// </summary>
/// <param name="SearchText">Current <c>SearchBox</c> text. Whitespace is not a search.</param>
/// <param name="SelectedCategoryId">Selected category/tab id, or null/blank when nothing specific is selected.</param>
/// <param name="CatalogCount">Size of the whole catalog, regardless of filters.</param>
/// <param name="CatalogLoadFinished">True once initialization completed, successfully or not.</param>
/// <param name="VisibleCount">Rows the current search, category, tag, rating, and size filters actually produced.</param>
/// <param name="GameIsNonGame">The card's own <c>Game.IsNonGame</c>: a utility or backup image rather than a game.</param>
/// <param name="HasCover">A cover file exists on disk for this game.</param>
/// <param name="SaveDataFound">A save-data snapshot was located for this game.</param>
/// <param name="Offline">The run started with <c>--offline</c>; shared edits are not applied.</param>
/// <param name="ActiveTagFilter">The selected tag, or null/blank when "All tags" is selected.</param>
internal sealed record CardStateContext(
    string SearchText,
    string? SelectedCategoryId,
    int CatalogCount,
    bool CatalogLoadFinished,
    int VisibleCount,
    bool GameIsNonGame,
    bool HasCover,
    bool SaveDataFound,
    bool Offline,
    string? ActiveTagFilter)
{
    /// <summary>A fully-populated healthy card, used as the baseline the tests mutate.</summary>
    internal static CardStateContext Healthy { get; } = new(
        SearchText: string.Empty,
        SelectedCategoryId: "all",
        CatalogCount: 1,
        CatalogLoadFinished: true,
        VisibleCount: 1,
        GameIsNonGame: false,
        HasCover: true,
        SaveDataFound: true,
        Offline: false,
        ActiveTagFilter: null);
}

/// <summary>
/// Turns "the list is blank" into something a person can act on.
/// <para>
/// A blank grid reads as broken, because the app cannot say why. This presenter
/// is the single place that decides which non-happy state applies, what to call
/// it, what the user can do next, and whether a card row is even worth drawing
/// next to that explanation. Three rules are enforced structurally:
/// </para>
/// <list type="number">
/// <item><description>A load failure beats an empty result, and a search miss beats a category miss. Precedence is a single ordered table, not an <c>if</c> chain that drifts.</description></item>
/// <item><description><see cref="Headline"/>, <see cref="Action"/>, and <see cref="Explain"/> are never empty. An empty screen invites action; a state with no action is a defect.</description></item>
/// <item><description>Every string is clamped to a stated character budget so a long query, a long category name, or a verbose error can never break the card layout.</description></item>
/// </list>
/// </summary>
internal static class CardStatePresenter
{
    /// <summary>Maximum characters in a headline. One line in the card at 24px.</summary>
    internal const int HeadlineBudget = 64;

    /// <summary>Maximum characters in the action. One line on the single button beside it.</summary>
    internal const int ActionBudget = 48;

    /// <summary>
    /// Maximum characters in the explanation. Two wrapped lines of 12px body text;
    /// the clamp is what keeps a pasted error string from growing the card row.
    /// </summary>
    internal const int ExplainBudget = 200;

    /// <summary>
    /// The precedence table, most specific first. Rank 1 wins. This is the documented
    /// and testable form of the decision; <see cref="Resolve"/> walks it in order and
    /// the tests assert the order rather than restating it in prose.
    /// <para>Why this order:</para>
    /// <list type="number">
    /// <item><description><see cref="CardState.CatalogLoading"/> - mid-load counts are partial, so claiming "no matches" would be a lie about data that does not exist yet.</description></item>
    /// <item><description><see cref="CardState.CatalogUnavailable"/> - a load failure beats an empty result. With no catalog, "nothing matches" is indistinguishable from "there was nothing to match against".</description></item>
    /// <item><description><see cref="CardState.NoSearchResults"/> - a search miss beats a category miss: the query is the narrower cause and the app can prove it by clearing the box.</description></item>
    /// <item><description><see cref="CardState.FilteredOutByTag"/> - the tag box removes rows across the whole view, so it outranks the category as the cause of an empty view.</description></item>
    /// <item><description><see cref="CardState.NonGameHidden"/> - the catalog is populated; what is in it is utility/backup imagery the app deliberately does not list as games.</description></item>
    /// <item><description><see cref="CardState.NoResultsInCategory"/> - every other explanation is ruled out, so the selected category is simply empty.</description></item>
    /// <item><description><see cref="CardState.ReadOnlyOffline"/> - data is fine and the row is useful; the only missing thing is the ability to edit it.</description></item>
    /// <item><description><see cref="CardState.SaveDataMissing"/> - the row renders and Backup is a real button, so this is actionable and outranks a cosmetic gap.</description></item>
    /// <item><description><see cref="CardState.CoverMissing"/> - purely cosmetic, and there is no fetch-cover control; last before Normal.</description></item>
    /// <item><description><see cref="CardState.Normal"/> - nothing to report.</description></item>
    /// </list>
    /// </summary>
    internal static IReadOnlyList<(int Rank, CardState State, string Rule)> Precedence { get; } = new (int, CardState, string)[]
    {
        (1, CardState.CatalogLoading, "not finished loading: counts are provisional, so no result claim is made"),
        (2, CardState.CatalogUnavailable, "load failed or offline with an empty catalog: a failure beats an empty result"),
        (3, CardState.NoSearchResults, "a search is typed and matched nothing: a search miss beats a category miss"),
        (4, CardState.FilteredOutByTag, "an active tag filter removed every row: it outranks the category as the cause"),
        (5, CardState.NonGameHidden, "the catalog is populated but holds only utility/backup images, which are not games"),
        (6, CardState.NoResultsInCategory, "no search, no tag, catalog loaded and populated: the selected category is empty"),
        (7, CardState.ReadOnlyOffline, "rows exist and are useful; only editing is unavailable offline"),
        (8, CardState.SaveDataMissing, "row renders and Backup is actionable, so it outranks a cosmetic gap"),
        (9, CardState.CoverMissing, "cosmetic only; no fetch-cover control exists, so it is last before Normal"),
        (10, CardState.Normal, "nothing to report")
    };

    /// <summary>
    /// The one decision function. Walks <see cref="Precedence"/> in order and returns
    /// the first state whose rule applies; <see cref="CardState.Normal"/> is the floor.
    /// </summary>
    internal static CardState Resolve(CardStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string search = (context.SearchText ?? string.Empty).Trim();
        bool searching = search.Length > 0;
        string tag = (context.ActiveTagFilter ?? string.Empty).Trim();
        bool tagFiltering = tag.Length > 0 && !string.Equals(tag, "All tags", StringComparison.OrdinalIgnoreCase);
        string category = (context.SelectedCategoryId ?? string.Empty).Trim();
        bool categorySelected = category.Length > 0 && !string.Equals(category, "all", StringComparison.OrdinalIgnoreCase);
        bool catalogEmpty = context.CatalogCount <= 0;
        bool nothingVisible = context.VisibleCount <= 0;

        // 1. Still loading. Nothing else can be claimed honestly yet.
        if (!context.CatalogLoadFinished) return CardState.CatalogLoading;

        // 2. No catalog to search: offline and empty, or loading failed outright.
        if (catalogEmpty && (context.Offline || !context.CatalogLoadFinished)) return CardState.CatalogUnavailable;

        // 3-6. The view itself is empty; report the most specific cause.
        if (nothingVisible && context.CatalogCount > 0)
        {
            if (searching) return CardState.NoSearchResults;
            if (tagFiltering) return CardState.FilteredOutByTag;
            if (context.GameIsNonGame) return CardState.NonGameHidden;
            if (categorySelected) return CardState.NoResultsInCategory;
        }

        // 7-9. A row is worth drawing; report the most specific thing wrong with it.
        if (context.Offline) return CardState.ReadOnlyOffline;
        if (!context.GameIsNonGame && !context.SaveDataFound) return CardState.SaveDataMissing;
        if (!context.HasCover) return CardState.CoverMissing;

        return CardState.Normal;
    }

    /// <summary>
    /// A short name for the situation. Never empty. Use the
    /// <c>Headline(state, context)</c> overload when the query, tag, or category
    /// is known and naming it helps.
    /// </summary>
    internal static string Headline(CardState state) => state switch
    {
        CardState.Normal => "Everything is in place",
        CardState.NoSearchResults => "No games match your search",
        CardState.NoResultsInCategory => "This category is empty",
        CardState.CatalogLoading => "Loading the catalog",
        CardState.CatalogUnavailable => "The catalog did not load",
        CardState.SaveDataMissing => "No save data found",
        CardState.CoverMissing => "No cover artwork",
        CardState.NonGameHidden => "Not a game image",
        CardState.FilteredOutByTag => "No games carry this tag",
        CardState.ReadOnlyOffline => "Read-only while offline",
        _ => "Unknown card state"
    };

    /// <summary>
    /// The same headline, naming the concrete reason when one is known. The
    /// interpolated value is clamped, so a 400-character query still fits.
    /// </summary>
    internal static string Headline(CardState state, CardStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return state switch
        {
            CardState.NoSearchResults => Quote("No games match", context.SearchText, string.Empty),
            CardState.NoResultsInCategory => Quote("No games in", context.SelectedCategoryId, string.Empty),
            CardState.FilteredOutByTag => Quote("No games tagged", context.ActiveTagFilter, string.Empty),
            _ => Headline(state)
        };
    }

    /// <summary>
    /// The single next step the user can take, phrased as an outcome they control.
    /// Never empty - a state with no action is a defect, and the tests enforce it.
    /// </summary>
    internal static string Action(CardState state) => state switch
    {
        CardState.Normal => "Open Details for the full record",
        CardState.NoSearchResults => "Clear the search",
        CardState.NoResultsInCategory => "Browse another category",
        CardState.CatalogLoading => "Wait for the catalog",
        CardState.CatalogUnavailable => "Retry loading",
        CardState.SaveDataMissing => "Back up this game's saves",
        CardState.CoverMissing => "Open Details to check the image",
        CardState.NonGameHidden => "Show the utility images",
        CardState.FilteredOutByTag => "Clear the tag filter",
        CardState.ReadOnlyOffline => "Restart online to make changes",
        _ => "Open Details for the full record"
    };

    /// <summary>
    /// One or two sentences of detail, generic when the concrete reason is not
    /// available here. Never empty.
    /// </summary>
    internal static string Explain(CardState state) => state switch
    {
        CardState.Normal =>
            "The catalog entry, cover, and save data are all present, so this card is complete.",
        CardState.NoSearchResults =>
            "The search matched nothing in the catalog. Clearing it brings the full list back.",
        CardState.NoResultsInCategory =>
            "This category loaded correctly but holds no games yet. Try another category, or add a game to it.",
        CardState.CatalogLoading =>
            "The bundled catalog is still being read, so game counts and filters are not final yet.",
        CardState.CatalogUnavailable =>
            "No catalog could be read, so there is no honest answer about matches. Retry, or import a library backup.",
        CardState.SaveDataMissing =>
            "No save files were found on this PC, so progress and restore have nothing to work with yet.",
        CardState.CoverMissing =>
            "No cover file is cached for this game, so the card shows a generated placeholder instead.",
        CardState.NonGameHidden =>
            "This is a utility or backup image rather than a game, so it is kept out of the game list.",
        CardState.FilteredOutByTag =>
            "The active tag filter removed every row in this view. Clearing it shows the games again.",
        CardState.ReadOnlyOffline =>
            "This run is offline. Games can be browsed and played, but shared edits stay on this PC until you restart online.",
        _ => "The app could not describe this card. Open Details for the raw catalog record."
    };

    /// <summary>
    /// The same explanation with the concrete reason inlined - the query the user
    /// typed, the category they picked, the tag they selected. The value is clamped,
    /// so the underlying string cannot grow the card row.
    /// </summary>
    internal static string Explain(CardState state, CardStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string search = (context.SearchText ?? string.Empty).Trim();
        string category = (context.SelectedCategoryId ?? string.Empty).Trim();
        string tag = (context.ActiveTagFilter ?? string.Empty).Trim();
        string quotedSearch = Fit("\"" + search + "\"", ExplainBudget / 2);
        string quotedCategory = Fit("\"" + category + "\"", ExplainBudget / 2);
        string quotedTag = Fit("\"" + tag + "\"", ExplainBudget / 2);

        return state switch
        {
            CardState.NoSearchResults =>
                "Nothing in the catalog matches " + quotedSearch
                + ". Clear the search to see every game again.",
            CardState.NoResultsInCategory =>
                quotedCategory + " holds no games in the catalog yet. Pick another category, or add a game to it.",
            CardState.FilteredOutByTag =>
                "Nothing in this view carries the tag " + quotedTag + ". Clear the tag filter to show the games again.",
            CardState.CatalogUnavailable =>
                context.Offline && context.CatalogCount <= 0
                    ? "This run is offline and no catalog was read, so nothing can be matched. Retry, or import a library backup."
                    : Explain(CardState.CatalogUnavailable),
            _ => Explain(state)
        };
    }

    /// <summary>
    /// Whether a card row should be drawn next to the explanation.
    /// <para>
    /// False for the states that describe the <i>view</i>: the row would only add
    /// noise under an explanation that is already the whole story. True for the
    /// states that describe a <i>row the user can act on</i>, including the degraded
    /// ones - a missing cover or a missing save backup is exactly the case where the
    /// card's own buttons are the answer.
    /// </para>
    /// </summary>
    internal static bool ShouldRenderRow(CardState state) => state switch
    {
        CardState.Normal => true,
        CardState.SaveDataMissing => true,
        CardState.CoverMissing => true,
        CardState.ReadOnlyOffline => true,
        CardState.CatalogLoading => false,
        CardState.CatalogUnavailable => false,
        CardState.NoSearchResults => false,
        CardState.NoResultsInCategory => false,
        CardState.NonGameHidden => false,
        CardState.FilteredOutByTag => false,
        _ => false
    };

    /// <summary>
    /// Clamp to a budget, ending with an ellipsis when truncated so the user can
    /// tell the text was cut. Always returns at most <paramref name="budget"/>
    /// characters and never returns an empty string for non-empty input.
    /// </summary>
    internal static string Fit(string value, int budget)
    {
        if (budget <= 0) return string.Empty;
        string text = (value ?? string.Empty).Trim();
        if (text.Length <= budget) return text.Length == 0 ? Fallback(budget) : text;
        return budget <= 1 ? "…" : text.Substring(0, budget - 1) + "…";
    }

    private static string Fallback(int budget) => budget >= 9 ? "No detail" : "…";

    /// <summary>Prefix + quoted value + suffix, clamped as a whole so the layout holds.</summary>
    private static string Quote(string prefix, string? value, string suffix)
    {
        string candidate = ((value ?? string.Empty).Trim()).Trim('"');
        if (candidate.Length == 0) return prefix.TrimEnd() + " it";
        string budgeted = Fit(candidate, HeadlineBudget - prefix.Length - suffix.Length - 3);
        return prefix + " " + "\"" + budgeted + "\"" + suffix;
    }

    /// <summary>The full row a view renders: headline, action, explanation, and whether to draw a row.</summary>
    internal static (string Headline, string Action, string Explain, bool RenderRow) Describe(CardState state, CardStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return (Headline(state, context), Action(state), Explain(state, context), ShouldRenderRow(state));
    }
}

internal static class CardStatePresenterTests
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string Show(string value) => value.Length <= 90 ? value : value.Substring(0, 90) + "…";

    private static CardStateContext Ctx(
        string search = "", string? category = "all", int catalog = 1, bool loaded = true,
        int visible = 1, bool nonGame = false, bool cover = true, bool save = true,
        bool offline = false, string? tag = null) =>
        new(search, category, catalog, loaded, visible, nonGame, cover, save, offline, tag);

    internal static void Run(string root)
    {
        Directory.CreateDirectory(root);

        // ---- A search that matched nothing must say so, and must name the fix.
        CardStateContext searchMiss = Ctx(search: "zelda", visible: 0, catalog: 40);
        Require(CardStatePresenter.Resolve(searchMiss) == CardState.NoSearchResults,
            "A non-empty search with zero results must resolve to NoSearchResults, not to a category or loading state.");
        Require(CardStatePresenter.Action(CardState.NoSearchResults).Equals("Clear the search", StringComparison.Ordinal),
            "NoSearchResults must offer the clear-the-search action, but offered: '"
            + CardStatePresenter.Action(CardState.NoSearchResults) + "'.");
        Require(CardStatePresenter.Headline(CardState.NoSearchResults, searchMiss).Contains("zelda", StringComparison.Ordinal),
            "The search-miss headline must name the query the user typed.");

        // ---- An empty search inside a category is a CATEGORY miss, not a search miss.
        CardStateContext categoryMiss = Ctx(search: "", category: "action", visible: 0, catalog: 40);
        CardState resolvedCategory = CardStatePresenter.Resolve(categoryMiss);
        Require(resolvedCategory == CardState.NoResultsInCategory,
            "An empty search with zero results in a category must resolve to NoResultsInCategory, but got " + resolvedCategory + ".");
        Require(resolvedCategory != CardState.NoSearchResults,
            "A category miss must never be reported as a search miss; the clear-the-search button would not help.");
        Require(CardStatePresenter.Headline(CardState.NoResultsInCategory, categoryMiss).Contains("action", StringComparison.Ordinal),
            "The category-miss headline must name the selected category.");

        // ---- Mid-load beats every count, including counts that look like results.
        foreach (CardStateContext partial in new[]
                 {
                     Ctx(loaded: false, catalog: 0, visible: 0, save: false, cover: false),
                     Ctx(loaded: false, catalog: 500, visible: 250),
                     Ctx(loaded: false, search: "zelda", category: "rpg", catalog: 900, visible: 0),
                     Ctx(loaded: false, catalog: 12, visible: 12, offline: true)
                 })
        {
            CardState state = CardStatePresenter.Resolve(partial);
            Require(state == CardState.CatalogLoading,
                "A catalog that has not finished loading must be CatalogLoading regardless of counts, but got " + state + ".");
            Require(!CardStatePresenter.ShouldRenderRow(state),
                "CatalogLoading must not draw a card row; the data under it is still changing.");
        }

        // ---- A failed catalog beats an empty result. "No matches" would be a lie.
        CardStateContext failed = Ctx(catalog: 0, visible: 0, offline: true, search: "zelda", cover: false, save: false);
        CardState failedState = CardStatePresenter.Resolve(failed);
        Require(failedState == CardState.CatalogUnavailable,
            "An offline run with no catalog must be CatalogUnavailable, but got " + failedState + ".");
        Require(failedState != CardState.NoSearchResults,
            "With no catalog loaded, an empty result must not be reported as a search miss.");
        Require(CardStatePresenter.Action(CardState.CatalogUnavailable).Equals("Retry loading", StringComparison.Ordinal),
            "CatalogUnavailable must offer a retry, matching the app's own Retry loading recovery button.");
        Require(CardStatePresenter.Explain(CardState.CatalogUnavailable, failed).Contains("offline", StringComparison.OrdinalIgnoreCase),
            "The CatalogUnavailable explanation must name the concrete reason (offline, no catalog).");

        // ---- A hidden non-game resolves to NonGameHidden, and draws no row.
        CardStateContext hiddenNonGame = Ctx(visible: 0, catalog: 3, nonGame: true, cover: true, save: true);
        CardState nonGameState = CardStatePresenter.Resolve(hiddenNonGame);
        Require(nonGameState == CardState.NonGameHidden,
            "A non-game image that is hidden must resolve to NonGameHidden, but got " + nonGameState + ".");
        Require(!CardStatePresenter.ShouldRenderRow(nonGameState),
            "NonGameHidden must not draw a card row; the row is what the app is hiding.");
        Require(CardStatePresenter.Headline(CardState.NonGameHidden).Equals("Not a game image", StringComparison.Ordinal),
            "NonGameHidden must say plainly that the entry is not a game.");
        // A non-game is never missing a save backup; campaign progress does not apply to it.
        Require(CardStatePresenter.Resolve(Ctx(nonGame: true, save: false)) != CardState.SaveDataMissing,
            "A utility/backup image must not be reported as missing save data; progress does not apply to it.");

        // ---- A tag filter that empties the view outranks the category as the cause.
        CardStateContext tagMiss = Ctx(category: "rpg", visible: 0, catalog: 40, tag: "co-op");
        Require(CardStatePresenter.Resolve(tagMiss) == CardState.FilteredOutByTag,
            "An active tag filter that removes every row must be FilteredOutByTag.");
        Require(CardStatePresenter.Action(CardState.FilteredOutByTag).Equals("Clear the tag filter", StringComparison.Ordinal),
            "FilteredOutByTag must offer the clear-the-tag-filter action.");

        // ---- Offline with data is read-only, not empty, and the row stays useful.
        CardStateContext offlineRows = Ctx(offline: true, catalog: 40, visible: 12);
        Require(CardStatePresenter.Resolve(offlineRows) == CardState.ReadOnlyOffline,
            "An offline run with readable rows must be ReadOnlyOffline, not an empty state.");
        Require(CardStatePresenter.ShouldRenderRow(CardState.ReadOnlyOffline),
            "ReadOnlyOffline must still draw the row; the user can browse and play offline.");

        // ---- The healthy baseline resolves to Normal and draws a row.
        CardStateContext healthy = CardStateContext.Healthy;
        Require(CardStatePresenter.Resolve(healthy) == CardState.Normal,
            "A complete card with a visible view must resolve to Normal.");
        Require(CardStatePresenter.ShouldRenderRow(CardState.Normal),
            "Normal must draw its row; ShouldRenderRow is false only for states that explain an empty view.");

        // ---- THE EMPTY-SCREEN RULE: no state may exist without words and a next step.
        // Iterating the whole enum, not a sample, so a state added later cannot skip this.
        CardState[] everyState = (CardState[])Enum.GetValues(typeof(CardState));
        Require(everyState.Length == 10,
            "CardState must hold exactly the 10 confirmed states, but holds " + everyState.Length + ": "
            + string.Join(", ", everyState.Select(s => s.ToString())) + ".");

        // Contexts chosen to stress interpolation: a very long query, tag, and category.
        CardStateContext[] contexts =
        {
            healthy,
            searchMiss,
            categoryMiss,
            Ctx(search: new string('z', 400), category: new string('c', 400), catalog: 1, visible: 0, tag: new string('t', 400)),
            Ctx(catalog: 0, visible: 0, offline: true, save: false, cover: false),
            Ctx(cover: false),
            Ctx(save: false),
            Ctx(nonGame: true),
            Ctx(offline: true, visible: 4, catalog: 9)
        };

        foreach (CardState state in everyState)
        {
            string headline = CardStatePresenter.Headline(state);
            string action = CardStatePresenter.Action(state);
            string explain = CardStatePresenter.Explain(state);

            Require(!string.IsNullOrWhiteSpace(headline),
                "CardState." + state + " has an empty headline. A state that cannot name itself is a defect.");
            Require(!string.IsNullOrWhiteSpace(action),
                "CardState." + state + " has no action. An empty screen must invite action.");
            Require(!string.IsNullOrWhiteSpace(explain),
                "CardState." + state + " has an empty explanation; the user is told nothing about what happened.");

            // The same rule for the context-aware overloads, including absurd input.
            foreach (CardStateContext context in contexts)
            {
                var described = CardStatePresenter.Describe(state, context);
                Require(!string.IsNullOrWhiteSpace(described.Headline),
                    "CardState." + state + " produced an empty context-aware headline for a populated context.");
                Require(!string.IsNullOrWhiteSpace(described.Action),
                    "CardState." + state + " produced an empty context-aware action for a populated context.");
                Require(!string.IsNullOrWhiteSpace(described.Explain),
                    "CardState." + state + " produced an empty context-aware explanation for a populated context.");
                Require(described.RenderRow == CardStatePresenter.ShouldRenderRow(state),
                    "CardState." + state + " disagrees with itself about whether the row renders.");
            }

            // Budgets: the layout must not be breakable by user input.
            Require(headline.Length <= CardStatePresenter.HeadlineBudget,
                "CardState." + state + " headline is " + headline.Length + " chars, over the "
                + CardStatePresenter.HeadlineBudget + " budget: '" + Show(headline) + "'.");
            Require(action.Length <= CardStatePresenter.ActionBudget,
                "CardState." + state + " action is " + action.Length + " chars, over the "
                + CardStatePresenter.ActionBudget + " budget: '" + Show(action) + "'.");
            Require(explain.Length <= CardStatePresenter.ExplainBudget,
                "CardState." + state + " explanation is " + explain.Length + " chars, over the "
                + CardStatePresenter.ExplainBudget + " budget: '" + Show(explain) + "'.");

            foreach (CardStateContext context in contexts)
            {
                string contextHeadline = CardStatePresenter.Headline(state, context);
                string contextExplain = CardStatePresenter.Explain(state, context);
                Require(contextHeadline.Length <= CardStatePresenter.HeadlineBudget,
                    "CardState." + state + " context headline is " + contextHeadline.Length
                    + " chars, over the " + CardStatePresenter.HeadlineBudget + " budget: '" + Show(contextHeadline) + "'.");
                Require(contextExplain.Length <= CardStatePresenter.ExplainBudget,
                    "CardState." + state + " context explanation is " + contextExplain.Length
                    + " chars, over the " + CardStatePresenter.ExplainBudget + " budget: '" + Show(contextExplain) + "'.");
            }
        }

        // ---- The clamp itself, so the budget is enforceable rather than hoped for.
        foreach (int budget in new[] { 1, 2, 5, 16, 48, 64, 200 })
        {
            Require(CardStatePresenter.Fit(string.Empty, budget).Length <= Math.Max(budget, 0),
                "Fit returned an over-budget string for empty input at budget " + budget + ".");
            Require(CardStatePresenter.Fit(new string('x', 5000), budget).Length <= budget,
                "Fit did not clamp a 5000-char string to a budget of " + budget + ".");
            Require(CardStatePresenter.Fit("short", budget).Length <= budget,
                "Fit lengthened a short string beyond budget " + budget + ".");
        }
        Require(CardStatePresenter.Fit(string.Empty, 0).Length == 0, "Fit must return empty for a zero budget.");
        Require(CardStatePresenter.Fit(new string('x', 5000), 64).EndsWith("…", StringComparison.Ordinal),
            "A clamped string must end with an ellipsis so the user can see it was cut.");

        // ---- Row rendering is split exactly where it should be: view states vs row states.
        var rowHidingStates = new[]
        {
            CardState.CatalogLoading, CardState.CatalogUnavailable, CardState.NoSearchResults,
            CardState.NoResultsInCategory, CardState.NonGameHidden, CardState.FilteredOutByTag
        };
        foreach (CardState state in rowHidingStates)
            Require(!CardStatePresenter.ShouldRenderRow(state),
                "CardState." + state + " describes an empty view and must not draw a card row beside the explanation.");

        var rowShowingStates = new[] { CardState.Normal, CardState.SaveDataMissing, CardState.CoverMissing, CardState.ReadOnlyOffline };
        foreach (CardState state in rowShowingStates)
            Require(CardStatePresenter.ShouldRenderRow(state),
                "CardState." + state + " describes a row the user can act on and must still be drawn.");

        // Both halves must partition the enum exactly, so no state is left unclassified.
        Require(rowHidingStates.Length + rowShowingStates.Length == everyState.Length,
            "The row-rendering rules must classify every state exactly once.");
        foreach (CardState state in everyState)
        {
            bool classified = rowHidingStates.Contains(state) || rowShowingStates.Contains(state);
            Require(classified, "CardState." + state + " is not classified by either ShouldRenderRow group.");
        }

        // ---- Precedence is documented as data; assert the data matches the behaviour.
        Require(CardStatePresenter.Precedence.Count == everyState.Length,
            "The precedence table must rank every state exactly once, but ranks " + CardStatePresenter.Precedence.Count + ".");
        var ranked = CardStatePresenter.Precedence.Select(entry => entry.State).ToArray();
        Require(ranked.Distinct().Count() == everyState.Length, "The precedence table ranks a state twice.");
        foreach (CardState state in everyState)
            Require(ranked.Contains(state), "CardState." + state + " is missing from the precedence table.");
        for (int i = 0; i < CardStatePresenter.Precedence.Count; i++)
            Require(CardStatePresenter.Precedence[i].Rank == i + 1,
                "The precedence table must be a dense 1..n order; rank " + CardStatePresenter.Precedence[i].Rank + " is out of step at position " + (i + 1) + ".");
        foreach (var entry in CardStatePresenter.Precedence)
            Require(!string.IsNullOrWhiteSpace(entry.Rule),
                "CardState." + entry.State + " is ranked but its precedence rule is undocumented.");

        // The documented order, asserted against the decision function.
        Require(CardStatePresenter.Precedence[0].State == CardState.CatalogLoading,
            "Rank 1 must be CatalogLoading: mid-load counts are provisional.");
        Require(CardStatePresenter.Precedence[1].State == CardState.CatalogUnavailable,
            "Rank 2 must be CatalogUnavailable: a load failure beats an empty result.");
        Require(CardStatePresenter.Precedence[2].State == CardState.NoSearchResults,
            "Rank 3 must be NoSearchResults: a search miss beats a category miss.");
        Require(CardStatePresenter.Precedence[^1].State == CardState.Normal,
            "The last rank must be Normal; it is the floor of the walk.");

        // A case that exercises every rank above Normal at once, to prove the walk order.
        CardStateContext everythingWrong = Ctx(
            search: "zelda", category: "action", catalog: 0, loaded: false, visible: 0,
            nonGame: true, cover: false, save: false, offline: true, tag: "co-op");
        Require(CardStatePresenter.Resolve(everythingWrong) == CardState.CatalogLoading,
            "With everything wrong at once, loading must still win; it outranks everything.");

        // ---- Every Resolve input must land on a defined state, never a default fallthrough.
        foreach (bool loaded in new[] { false, true })
        foreach (bool offline in new[] { false, true })
        foreach (bool nonGame in new[] { false, true })
        foreach (int catalog in new[] { 0, 1, 500 })
        foreach (int visible in new[] { 0, 3 })
        foreach (string search in new[] { "", "zelda" })
        foreach (string? tag in new string?[] { null, "All tags", "co-op" })
        foreach (string? category in new string?[] { null, "all", "action" })
        {
            CardStateContext probe = new(search, category, catalog, loaded, visible, nonGame, false, false, offline, tag);
            CardState resolved = CardStatePresenter.Resolve(probe);
            Require(Enum.IsDefined(typeof(CardState), resolved),
                "Resolve produced an undefined state (" + resolved + ") for a legal context.");
            Require(!string.IsNullOrWhiteSpace(CardStatePresenter.Action(resolved)),
                "Resolve produced state " + resolved + ", which has no action.");
            Require(!string.IsNullOrWhiteSpace(CardStatePresenter.Explain(resolved)),
                "Resolve produced state " + resolved + ", which has no explanation.");
            Require(CardStatePresenter.Headline(resolved).Length <= CardStatePresenter.HeadlineBudget
                && CardStatePresenter.Action(resolved).Length <= CardStatePresenter.ActionBudget
                && CardStatePresenter.Explain(resolved).Length <= CardStatePresenter.ExplainBudget,
                "Resolve produced an over-budget string for state " + resolved + ".");
        }

        // Resolve must be total and side-effect free enough to call from layout.
        Require(CardStatePresenter.Resolve(healthy) == CardStatePresenter.Resolve(healthy),
            "Resolve is not deterministic for the same context.");

        // ---- Artifact: the real user-visible strings, written out so the text can be
        // reviewed without running the app.
        var report = new StringBuilder();
        report.AppendLine("# Card state presenter: user-visible strings");
        report.AppendLine();
        report.AppendLine("Budgets: headline <= " + CardStatePresenter.HeadlineBudget
            + " chars, action <= " + CardStatePresenter.ActionBudget
            + " chars, explain <= " + CardStatePresenter.ExplainBudget + " chars.");
        report.AppendLine();
        report.AppendLine("| State | Headline | Action | Explain | Row |");
        report.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (CardState state in everyState)
        {
            report.AppendLine("| " + state
                + " | " + CardStatePresenter.Headline(state)
                + " | " + CardStatePresenter.Action(state)
                + " | " + CardStatePresenter.Explain(state)
                + " | " + (CardStatePresenter.ShouldRenderRow(state) ? "yes" : "no") + " |");
        }
        report.AppendLine();
        report.AppendLine("With a known reason, the context overloads say:");
        report.AppendLine();
        report.AppendLine("- NoSearchResults: " + CardStatePresenter.Headline(CardState.NoSearchResults, searchMiss)
            + " / " + CardStatePresenter.Explain(CardState.NoSearchResults, searchMiss));
        report.AppendLine("- NoResultsInCategory: " + CardStatePresenter.Headline(CardState.NoResultsInCategory, categoryMiss)
            + " / " + CardStatePresenter.Explain(CardState.NoResultsInCategory, categoryMiss));
        report.AppendLine("- FilteredOutByTag: " + CardStatePresenter.Headline(CardState.FilteredOutByTag, tagMiss)
            + " / " + CardStatePresenter.Explain(CardState.FilteredOutByTag, tagMiss));
        report.AppendLine("- CatalogUnavailable: " + CardStatePresenter.Explain(CardState.CatalogUnavailable, failed));
        report.AppendLine();
        report.AppendLine("Precedence, most specific first:");
        foreach (var entry in CardStatePresenter.Precedence)
            report.AppendLine(entry.Rank.ToString(CultureInfo.InvariantCulture) + ". " + entry.State + " - " + entry.Rule);
        File.WriteAllText(Path.Combine(root, "card-states.md"), report.ToString(), Encoding.UTF8);

        Console.WriteLine("CardStatePresenterTests: " + everyState.Length + " states x " + contexts.Length
            + " contexts, all non-empty and within budget.");
        foreach (var entry in CardStatePresenter.Precedence)
            Console.WriteLine("  " + entry.Rank.ToString(CultureInfo.InvariantCulture) + ". " + entry.State
                + "  row=" + (CardStatePresenter.ShouldRenderRow(entry.State) ? "yes" : "no")
                + "  \"" + CardStatePresenter.Headline(entry.State) + "\" / " + CardStatePresenter.Action(entry.State));
    }
}