using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace GameLibrary.Native;

/// <summary>
/// The pure, testable decision layer behind <see cref="TagMigrationDialog"/>:
/// every user-facing string, the emphasis line, whether the confirm action is
/// offered, the close label, and the exact edits a confirm would apply. Building
/// this without a window keeps the wording and the safety gate unit-testable.
/// </summary>
internal sealed record TagMigrationPresentation(
    string Title,
    string Emphasis,
    string Detail,
    string ConfirmLabel,
    string CloseLabel,
    bool ConfirmOffered,
    IReadOnlyList<PendingEdit> Edits);

/// <summary>
/// A real WPF confirmation window for a <see cref="MigrationPlan"/>, built on the
/// app's existing <see cref="EditorWindow"/> so it matches the native dialogs.
/// It states the exact number of games that would change, lists the plan via
/// <see cref="TagMigrationService.Describe"/>, and offers only confirm and cancel.
/// When <see cref="TagMigrationService.IsSafe"/> is false it refuses to confirm
/// and names the offending category; when nothing would change it offers only a
/// close action. No animation runs on open, per <see cref="MotionPolicy"/>.
/// </summary>
internal static class TagMigrationDialog
{
    /// <summary>
    /// Shows the migration plan to the user. The confirm action fires with
    /// <see cref="TagMigrationService.ToEdits"/> for the plan; the cancel action
    /// changes nothing.
    /// </summary>
    internal static void Show(Window owner, MigrationPlan plan, Action<IReadOnlyList<PendingEdit>> onConfirm)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(onConfirm);

        TagMigrationPresentation presentation = Present(plan);
        var dialog = new EditorWindow(owner, presentation.Title, presentation.Emphasis);

        // The count of games about to move is the one thing that matters here, so
        // the notice slot carries it at display size in primary ink. It is not the
        // accent: the accent fill is reserved for the single confirm action.
        dialog.Notice.FontSize = 22;
        dialog.Notice.FontWeight = FontWeights.SemiBold;
        dialog.Notice.Margin = new Thickness(0, 0, 0, 16);
        dialog.Notice.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var detail = new TextBlock
        {
            Text = presentation.Detail,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        dialog.Fields.Children.Add(detail);

        if (presentation.ConfirmOffered)
        {
            Button confirm = dialog.Action(presentation.ConfirmLabel, () =>
            {
                onConfirm(presentation.Edits);
                dialog.Close();
            }, "ConfirmTagMigration");
            confirm.Style = (Style)Application.Current.FindResource("PrimaryButton");
            confirm.IsDefault = true;
        }

        Button close = dialog.Action(presentation.CloseLabel, dialog.Close, "CancelTagMigration");
        close.IsCancel = true;

        dialog.ShowDialog();
    }

    /// <summary>
    /// Builds the exact user-facing presentation for a plan without touching WPF.
    /// Safety is checked first: an unsafe plan never offers confirm and names the
    /// category it found. A plan that changes nothing offers only a close action.
    /// </summary>
    internal static TagMigrationPresentation Present(MigrationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!TagMigrationService.IsSafe(plan))
        {
            var offences = new List<string>
            {
                "Refusing to migrate because this plan is not confined to the seven hidden categories."
            };
            foreach (TagMove move in plan.Moves)
            {
                if (!TagTaxonomy.IsDestinationCategoryId(move.To))
                    offences.Add($"'{move.Tag}' would move into '{move.To}', which is not a hidden category.");
            }
            foreach (string gameId in plan.AffectedGameIds)
            {
                if (plan.DestinationByGame.TryGetValue(gameId, out string? destination) && !TagTaxonomy.IsDestinationCategoryId(destination))
                    offences.Add($"'{gameId}' would land in '{destination}', which is not a hidden category.");
            }
            return new TagMigrationPresentation(
                "Tag migration blocked",
                "This plan touches a category it must not",
                string.Join(Environment.NewLine, offences),
                "",
                "Close without migrating",
                ConfirmOffered: false,
                Array.Empty<PendingEdit>());
        }

        if (plan.ChangedGameCount == 0)
        {
            return new TagMigrationPresentation(
                "Nothing to migrate",
                "No games need to move into hidden categories",
                "Every non-game tag already sits in its hidden category, so there is nothing to change.",
                "",
                "Close",
                ConfirmOffered: false,
                Array.Empty<PendingEdit>());
        }

        string outcome = $"Move {GameCount(plan.ChangedGameCount)} into hidden categories";
        return new TagMigrationPresentation(
            "Move non-game tags into hidden categories",
            outcome,
            TagMigrationService.Describe(plan),
            outcome,
            "Cancel",
            ConfirmOffered: true,
            TagMigrationService.ToEdits(plan));
    }

    private static string GameCount(int count) => count == 1 ? "1 game" : count + " games";
}
