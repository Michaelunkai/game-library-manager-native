using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace GameLibrary.Native;

/// <summary>
/// The library-wide set of tags to migrate. <see cref="GameTags"/> maps each
/// game's catalog identity id to that game's tags, exactly as they are stored in
/// <see cref="UserState.GameTags"/>. When <see cref="HiddenCategoryIds"/> is
/// omitted the seven hidden destinations from <see cref="TagTaxonomy"/> are used.
/// </summary>
public sealed record TagMigrationRequest(
    IReadOnlyDictionary<string, IReadOnlyList<string>> GameTags,
    IReadOnlySet<string>? HiddenCategoryIds = null);

/// <summary>
/// A reviewable, applyable tag-migration plan: the taxonomy moves, which games
/// each move touches, the destination categories involved, and how many games
/// would change versus stay put.
/// </summary>
/// <param name="Moves">The moves produced by <see cref="TagTaxonomy.PlanMoves"/>.</param>
/// <param name="AffectedGameIds">Games that own at least one moveable tag.</param>
/// <param name="GamesByMove">Game ids touched by each move, keyed by the move's normalized tag.</param>
/// <param name="DestinationByGame">The single hidden destination chosen for each affected game; when a game owns tags for several destinations the canonical <see cref="TagTaxonomy.DestinationCategoryIds"/> order wins.</param>
/// <param name="DestinationCategoryIds">The distinct hidden destinations this plan writes to.</param>
/// <param name="ChangedGameCount">Number of games whose category would change.</param>
/// <param name="UnchangedGameCount">Number of games left untouched.</param>
public sealed record MigrationPlan(
    IReadOnlyList<TagMove> Moves,
    IReadOnlyList<string> AffectedGameIds,
    IReadOnlyDictionary<string, IReadOnlyList<string>> GamesByMove,
    IReadOnlyDictionary<string, string> DestinationByGame,
    IReadOnlyList<string> DestinationCategoryIds,
    int ChangedGameCount,
    int UnchangedGameCount);

/// <summary>
/// Turns <see cref="TagTaxonomy"/>'s tag plan into safe, reviewable, applyable
/// category edits so the manager can wire one confirmation button to it.
/// Planning is deterministic and idempotent, and unknown tags fail closed.
/// </summary>
public static class TagMigrationService
{
    /// <summary>
    /// Plans the non-game tag moves for an entire library. The moves are exactly
    /// what <see cref="TagTaxonomy.PlanMoves(IEnumerable{string}, IReadOnlySet{string})"/>
    /// returns for the library's distinct tags, augmented with the games each
    /// move actually touches. Re-planning a library whose non-game tags have
    /// already been replaced by their hidden destination ids returns an empty
    /// plan because destination ids are never move sources.
    /// </summary>
    public static MigrationPlan Plan(TagMigrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyDictionary<string, IReadOnlyList<string>> games = request.GameTags
            ?? throw new ArgumentException("A migration request requires a game-tag map.", nameof(request));
        IReadOnlySet<string> hidden = request.HiddenCategoryIds
            ?? new HashSet<string>(TagTaxonomy.DestinationCategoryIds, StringComparer.Ordinal);

        string[] gameIds = games.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray();

        var allTags = new List<string>();
        var seenTags = new HashSet<string>(StringComparer.Ordinal);
        foreach (string gameId in gameIds)
        {
            IReadOnlyList<string>? tags = games[gameId];
            if (tags == null) continue;
            foreach (string raw in tags)
            {
                string tag = TagTaxonomy.Normalize(raw);
                if (tag.Length > 0 && seenTags.Add(tag)) allTags.Add(tag);
            }
        }
        allTags.Sort(StringComparer.Ordinal);

        IReadOnlyList<TagMove> moves = TagTaxonomy.PlanMoves(allTags, hidden);

        var gamesByMove = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (TagMove move in moves)
        {
            var affected = new List<string>();
            foreach (string gameId in gameIds)
            {
                IReadOnlyList<string>? tags = games[gameId];
                if (tags == null) continue;
                if (tags.Any(tag => string.Equals(TagTaxonomy.Normalize(tag), move.Tag, StringComparison.Ordinal)))
                    affected.Add(gameId);
            }
            gamesByMove[move.Tag] = affected;
        }

        // A game can own tags for more than one destination but only one category
        // can be assigned. The canonical DestinationCategoryIds order decides the
        // winner (movies, tv, anime, audio, books, software, other), then tag order.
        TagMove[] rankedMoves = moves
            .OrderBy(move => DestinationRank(move.To))
            .ThenBy(move => move.Tag, StringComparer.Ordinal)
            .ToArray();

        var affectedGameIds = new List<string>();
        var destinationByGame = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string gameId in gameIds)
        {
            foreach (TagMove move in rankedMoves)
            {
                if (gamesByMove[move.Tag].Contains(gameId, StringComparer.Ordinal))
                {
                    affectedGameIds.Add(gameId);
                    destinationByGame[gameId] = move.To;
                    break;
                }
            }
        }

        string[] destinations = moves
            .Select(move => move.To)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        int changed = affectedGameIds.Count;
        return new MigrationPlan(moves, affectedGameIds, gamesByMove, destinationByGame, destinations, changed, gameIds.Length - changed);
    }

    /// <summary>
    /// Projects a plan into the app's existing <see cref="PendingEdit"/> shape for
    /// <see cref="LocalCatalogEdits.Save"/>.
    /// <para>
    /// Every edit is <c>Section = "gameCategories"</c>, <c>Key = the game's catalog
    /// identity id</c> (the exact id used as the key in <see cref="UserState.GameTags"/>),
    /// and <c>After = the hidden destination category id</c>. That key is chosen
    /// because <c>gameCategories</c> is the local catalog's only per-game
    /// category-assignment map, so moving a non-game tag out of a game is expressed
    /// as reassigning that game's category to the hidden destination rather than
    /// deleting the tag or editing a tab.
    /// </para>
    /// Emits exactly one edit per affected game.
    /// </summary>
    public static IReadOnlyList<PendingEdit> ToEdits(MigrationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var edits = new List<PendingEdit>(plan.AffectedGameIds.Count);
        foreach (string gameId in plan.AffectedGameIds)
        {
            if (!plan.DestinationByGame.TryGetValue(gameId, out string? destination) || string.IsNullOrWhiteSpace(destination))
                throw new InvalidOperationException($"Migration plan has no destination category for affected game '{gameId}'.");
            edits.Add(new PendingEdit
            {
                Section = "gameCategories",
                Key = gameId,
                After = JsonValue.Create(destination)
            });
        }
        return edits;
    }

    /// <summary>
    /// A plain-text, user-facing preview for a confirmation dialog: every
    /// destination category, the tags moving into it, and the games affected.
    /// </summary>
    public static string Describe(MigrationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var text = new StringBuilder();
        text.Append("Tag migration preview: ")
            .Append(plan.Moves.Count).Append(" tag(s) move into ")
            .Append(plan.DestinationCategoryIds.Count).Append(" hidden category(ies); ")
            .Append(plan.ChangedGameCount).Append(" game(s) will change.");
        if (plan.Moves.Count == 0)
        {
            text.Append(Environment.NewLine).Append("  (nothing to migrate)");
            return text.ToString();
        }
        foreach (string destination in plan.DestinationCategoryIds)
        {
            string[] tags = plan.Moves
                .Where(move => string.Equals(move.To, destination, StringComparison.Ordinal))
                .Select(move => move.Tag)
                .OrderBy(tag => tag, StringComparer.Ordinal)
                .ToArray();
            string[] affected = plan.AffectedGameIds
                .Where(gameId => string.Equals(plan.DestinationByGame.GetValueOrDefault(gameId), destination, StringComparison.Ordinal))
                .OrderBy(gameId => gameId, StringComparer.Ordinal)
                .ToArray();
            text.Append(Environment.NewLine)
                .Append("  ").Append(destination)
                .Append(": tags ").Append(string.Join(", ", tags))
                .Append("; ").Append(affected.Length).Append(" game(s): ").Append(string.Join(", ", affected));
        }
        return text.ToString();
    }

    /// <summary>
    /// The last-moment guard for the "only under hidden categories" requirement:
    /// true only when every move and every affected-game destination is one of the
    /// seven hidden destination category ids. An empty plan is safe.
    /// </summary>
    public static bool IsSafe(MigrationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        foreach (TagMove move in plan.Moves)
        {
            if (!TagTaxonomy.IsDestinationCategoryId(move.To)) return false;
        }
        foreach (KeyValuePair<string, string> assignment in plan.DestinationByGame)
        {
            if (!TagTaxonomy.IsDestinationCategoryId(assignment.Value)) return false;
        }
        return true;
    }

    private static int DestinationRank(string destination)
    {
        for (int index = 0; index < TagTaxonomy.DestinationCategoryIds.Count; index++)
        {
            if (string.Equals(TagTaxonomy.DestinationCategoryIds[index], destination, StringComparison.Ordinal)) return index;
        }
        return int.MaxValue;
    }
}
