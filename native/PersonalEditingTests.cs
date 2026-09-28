using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace GameLibrary.Native;

public partial class MainWindow
{
    private void VerifyStartupEditingProtection(Action<string, bool> check)
    {
        var store = new LibraryStore(Path.Combine(Store.Root, "startup-edit-proof-" + Guid.NewGuid().ToString("N")));
        var saved = new UserState();
        saved.Wishlist.Add("retained-before-load");
        saved.Ratings["retained-before-load"] = 4;
        saved.LocalCatalog["gameCategories"] = new JsonObject { ["retained-before-load"] = "action" };
        store.Save(saved);
        string original = File.ReadAllText(store.StatePath);
        var window = new MainWindow(store, offline: true);
        bool controlsBlocked = !window.CategoryList.IsEnabled;
        bool saveRejected = false;
        try { window.Save(); }
        catch (InvalidOperationException) { saveRejected = true; }
        bool profileIntact = File.ReadAllText(store.StatePath) == original;
        window.Close();
        var results = new[] {
            new { name = "Editing controls stay disabled until the saved profile loads", passed = controlsBlocked },
            new { name = "Saving before initialization is rejected", passed = saveRejected },
            new { name = "Premature save and close preserve the existing profile byte for byte", passed = profileIntact && File.ReadAllText(store.StatePath) == original }
        };
        LibraryStore.AtomicWrite(Path.Combine(Store.Root, "startup-edit-proof.json"), DataJson.Write(results));
        foreach (var result in results) check(result.name, result.passed);
    }

    private async Task VerifyPersonalEditingPersistence(Action<string, bool> check)
    {
        if (Program.TestReport == null) throw new InvalidOperationException("Personal-edit proof requires an isolated UI-test profile.");
        var previous = DataJson.Read<UserState>(DataJson.Write(State));
        var results = new List<(string Name, bool Passed)>();
        EditorWindow? dialog = null;
        string id = Games.First(game => !game.IsLocal && !game.IsNonGame).Id;
        try
        {
            void Reset()
            {
                State.Ratings[id] = 1;
                State.GameTags[id] = new() { "original-personal-tag" };
                State.InstalledGames.Remove(id);
                LocalCatalogEdits.Save(Store, State, new PendingEdit { Section = "gameCategories", Key = id, After = JsonValue.Create("new") });
                Reload();
            }
            Reset();
            var game = Games.Single(candidate => candidate.Id == id);
            _ = Dispatcher.BeginInvoke(new Action(() => Details(game)));
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (dialog == null && DateTime.UtcNow < deadline)
            {
                dialog = System.Windows.Application.Current.Windows.OfType<EditorWindow>().SingleOrDefault(window => window.IsVisible && window.Title == game.Name);
                if (dialog == null) await Task.Delay(50);
            }
            if (dialog == null) throw new InvalidOperationException("The Details dialog did not open.");
            var category = dialog.Fields.Children.OfType<ComboBox>().Single(field => AutomationProperties.GetAutomationId(field) == "GameCategory");
            var rating = dialog.Fields.Children.OfType<ComboBox>().Single(field => field != category);
            var tags = dialog.Fields.Children.OfType<TextBox>().Single(field => AutomationProperties.GetAutomationId(field) == "GameTags");
            var installed = dialog.Fields.Children.OfType<CheckBox>().Single(field => (string)field.Content == "Mark as installed");
            var save = dialog.Fields.Children.OfType<Button>().Single(field => AutomationProperties.GetAutomationId(field) == "SaveGameDetails");
            var originalCategory = category.Items.Cast<Category>().Single(item => item.Id == "new");
            var alternate = category.Items.Cast<Category>().First(item => item.Id != "new");
            void ClickSave() => save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            string CategoryOf(UserState state) => DataJson.Text(state.LocalCatalog["gameCategories"]?[id]);
            category.SelectedItem = alternate; ClickSave();
            results.Add(("Details saves the first category change", CategoryOf(Store.LoadState()) == alternate.Id));
            category.SelectedItem = originalCategory; ClickSave();
            results.Add(("Details can move a game back in the same open dialog", CategoryOf(Store.LoadState()) == "new"));

            Reset();
            string beforeInvalid = DataJson.Write(State);
            rating.SelectedIndex = 4; tags.Text = new string('x', 81); ClickSave();
            results.Add(("Invalid tags leave every in-memory personal field unchanged", DataJson.Write(State) == beforeInvalid && dialog.Notice.Text.Contains("80", StringComparison.Ordinal)));
            Save(); // A later periodic save must not leak a rejected rating change.
            results.Add(("Rejected personal edits cannot leak into a later save", DataJson.Write(Store.LoadState()) == beforeInvalid));

            Reset();
            string beforeFailure = DataJson.Write(State);
            rating.SelectedIndex = 4; tags.Text = "verified-tag, another-tag"; installed.IsChecked = true; category.SelectedItem = alternate;
            using (var locked = new FileStream(Store.StatePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                ClickSave();
                results.Add(("A failed Details save leaves all personal fields unchanged", DataJson.Write(State) == beforeFailure && dialog.Notice.Text != "Saved on this PC."));
            }
            results.Add(("A failed Details save leaves the durable profile unchanged", DataJson.Write(Store.LoadState()) == beforeFailure));
            ClickSave();
            bool Matches(UserState state) => state.Ratings.GetValueOrDefault(id) == 4 && state.GameTags[id].SequenceEqual(new[] { "verified-tag", "another-tag" })
                && state.InstalledGames.Contains(id) && CategoryOf(state) == alternate.Id;
            results.Add(("Retry commits rating tags installed flag and category together", dialog.Notice.Text == "Saved on this PC." && Matches(State) && Matches(Store.LoadState())));
            await Refresh(false); await Refresh(true);
            var restarted = new LibraryStore(Store.Root).LoadState();
            results.Add(("Saved Details edits survive refresh and an independent profile load", Matches(restarted)));
            LibraryStore.AtomicWrite(Path.Combine(Store.Root, "personal-edit-proof.json"), DataJson.Write(results.Select(result => new { name = result.Name, passed = result.Passed }).ToArray()));
            foreach (var result in results) check(result.Name, result.Passed);
        }
        finally
        {
            dialog?.Close();
            RestoreImportedState(previous);
        }
    }
}
