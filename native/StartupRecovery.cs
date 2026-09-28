using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;

namespace GameLibrary.Native;

public partial class MainWindow
{
    private void RetryStartup(object sender, RoutedEventArgs e) => ObserveUiOperation("Retry library loading", InitializeAsync);

    private void ImportStartupBackup(object sender, RoutedEventArgs e) => ObserveUiOperation("Recover library backup", ImportStartupBackupAsync);

    private async Task ImportStartupBackupAsync()
    {
        if (!startupFailed || initializing || ready || closing) return;
        StartupRecovery.IsEnabled = false;
        try
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Title = "Recover library backup", Filter = "Complete library backup|*.json" };
            if (picker.ShowDialog(this) != true) return;
            var document = JsonNode.Parse(File.ReadAllText(picker.FileName))?.AsObject() ?? throw new FormatException("Invalid backup document.");
            await RecoverStartupStateAsync(document);
        }
        finally { if (!closing) StartupRecovery.IsEnabled = true; }
    }

    private async Task RecoverStartupStateAsync(JsonObject document)
    {
        if (!startupFailed || initializing || ready || closing) throw new InvalidOperationException("Recovery is available after library loading has stopped.");
        if (document["schemaVersion"] == null) throw new FormatException("Choose a complete library backup. A settings-only file cannot recover your saved library.");
        // Use the normal import transaction and rollback copy, but do not render
        // or save an uninitialized window. Normal initialization must finish first.
        await Sync.ImportStateAsync(() => State,
            _ => DataJson.Read<UserState>(document.ToJsonString()),
            imported => State = imported, lifetime.Token);
        await InitializeAsync();
    }
}
