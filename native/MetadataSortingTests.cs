using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GameLibrary.Native;

public partial class MainWindow
{
    private void VerifyAutomaticMetadataSorting(Action<string, bool> check)
    {
        if (Program.TestReport == null) throw new InvalidOperationException("Sorting proof requires an isolated UI-test profile.");
        var previous = DataJson.Read<UserState>(DataJson.Write(State));
        var results = new List<(string Name, bool Passed)>();
        try
        {
            ResetFilters(this, new());
            tab = "all";
            foreach (string sort in new[] { "Time to Beat (Low–High)", "Time to Beat (High–Low)" })
            {
                bool ascending = sort == "Time to Beat (Low–High)";
                var changed = new Game { Id = "sort-proof-changing", Name = "Metadata reorder changing", Category = "new", Time = ascending ? 20 : 5, Selected = true };
                var other = new Game { Id = "sort-proof-other", Name = "Metadata reorder other", Category = "new", Time = 10 };
                Games = new() { changed, other, new Game { Id = "sort-proof-filtered", Name = "Excluded row", Category = "new", Time = 1 } };
                SortBox.SelectedItem = sort;
                SearchBox.Text = "Metadata reorder";
                searchDelay.Stop(); ApplyFilter();
                if (filtered.Count != 2 || filtered[0] != other) throw new InvalidOperationException("Sorting fixture did not establish its starting order.");
                string saved = DataJson.Write(State);
                ApplyAutomaticMetadata(changed, new Game { Id = changed.Id, Name = changed.Name, Category = changed.Category, Time = ascending ? 5 : 20, TimeVerifiedAt = DateTime.UtcNow });
                results.Add(($"Automatic completion-time updates reorder {sort}", filtered.Count == 2 && filtered[0] == changed && GameList.Items[0] == changed));
                results.Add(($"Automatic {sort} reorder retains search selection and personal state", SearchBox.Text == "Metadata reorder" && changed.Selected && !other.Selected && DataJson.Write(State) == saved));
            }
        }
        finally { searchDelay.Stop(); RestoreImportedState(previous); }
        LibraryStore.AtomicWrite(Path.Combine(Store.Root, "metadata-sorting-proof.json"), DataJson.Write(results.Select(r => new { name = r.Name, passed = r.Passed }).ToArray()));
        foreach (var result in results) check(result.Name, result.Passed);
    }
}
