using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace GameLibrary.Native;

/// <summary>Durable job viewer. Closing this window never stops its independent worker.</summary>
internal sealed class InstallJobWindow : Window
{
    private readonly InstallJobStore store;
    private readonly InstallJobController controller;
    private readonly string operationId;
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBox output = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12 };
    private readonly Button pause = new() { Content = "Pause" };
    private readonly Button resume = new() { Content = "Resume" };
    private readonly Button retry = new() { Content = "Retry failed stage" };
    private readonly Button stop = new() { Content = "Stop" };
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private string lastRenderedEventKey = "";
    private InstallJobStatus? lastObservedStatus;
    private DateTime nextWorkerHealthCheckUtc = DateTime.MinValue;
    internal event Action<InstallJobRecord>? JobChanged;

    internal InstallJobWindow(InstallJobStore store, InstallJobController controller, string operationId)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.operationId = InstallJobStore.ValidateOperationId(operationId);
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "Game Library · Installation";
        Width = 900; Height = 600; MinWidth = 640; MinHeight = 420;

        var root = new DockPanel { Margin = new Thickness(18) };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        foreach (Button button in new[] { pause, resume, retry, stop })
        {
            button.Margin = new Thickness(0, 0, 8, 0);
            button.Click += async (_, _) => await SendAsync(button);
            controls.Children.Add(button);
        }
        DockPanel.SetDock(controls, Dock.Top); root.Children.Add(controls);
        var header = new StackPanel();
        header.Children.Add(summary);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        root.Children.Add(output);
        Content = root;

        Loaded += async (_, _) =>
        {
            try { controller.EnsureWorkerStarted(this.operationId); }
            catch (Exception ex) { summary.Text = "Worker could not start: " + ex.Message; }
            nextWorkerHealthCheckUtc = DateTime.UtcNow.AddSeconds(5);
            RefreshFromStore();
            refreshTimer.Tick += (_, _) => RefreshFromStore();
            refreshTimer.Start();
        };
        Closed += (_, _) => refreshTimer.Stop();
        RefreshFromStore();
    }

    private async Task SendAsync(Button button)
    {
        if (!button.IsEnabled) return;
        InstallJobControlAction action = button == pause ? InstallJobControlAction.Pause
            : button == resume ? InstallJobControlAction.Resume
            : button == retry ? InstallJobControlAction.Retry
            : InstallJobControlAction.Stop;
        SetButtons(false);
        try
        {
            controller.EnsureWorkerStarted(operationId);
            string requestId = controller.SendControl(operationId, action);
            InstallJobControlResponse? response = await controller.WaitForResponseAsync(operationId, requestId, TimeSpan.FromSeconds(30));
            if (response == null) summary.Text = "Control request was recorded; worker response is still pending. The job continues independently.";
            else if (!response.Accepted) summary.Text = "Control request was not accepted: " + response.Message;
            RefreshFromStore();
        }
        catch (Exception ex) { summary.Text = "Could not send the control request: " + ex.Message; }
        finally { SetButtons(true); RefreshFromStore(); }
    }

    private void RefreshFromStore()
    {
        try
        {
            InstallJobRecord job = store.Load(operationId);
            string? workerIssue = null;
            if (IsLoaded && DateTime.UtcNow >= nextWorkerHealthCheckUtc
                && job.Status is not (InstallJobStatus.Completed or InstallJobStatus.Failed or InstallJobStatus.Stopped))
            {
                nextWorkerHealthCheckUtc = DateTime.UtcNow.AddSeconds(5);
                try { controller.EnsureWorkerStarted(operationId); }
                catch (Exception ex) { workerIssue = "Worker recovery failed: " + ex.Message; }
            }
            summary.Text = job.Stage + " · " + job.Status + " · " + (string.IsNullOrWhiteSpace(job.FailureReason) ? "last activity " + job.LastMeaningfulActivityUtc.ToLocalTime().ToString("g") : job.FailureReason)
                + Environment.NewLine + "Digest: " + (string.IsNullOrWhiteSpace(job.PinnedDigest) ? "pending" : job.PinnedDigest)
                + Environment.NewLine + "Version folder: " + job.InstalledPath
                + (workerIssue == null ? "" : Environment.NewLine + workerIssue);
            pause.IsEnabled = job.Status == InstallJobStatus.Running;
            resume.IsEnabled = job.Status == InstallJobStatus.Paused;
            retry.IsEnabled = job.Status == InstallJobStatus.RetryableFailure;
            stop.IsEnabled = job.Status is InstallJobStatus.Queued or InstallJobStatus.Running or InstallJobStatus.PauseRequested or InstallJobStatus.Paused or InstallJobStatus.RetryableFailure or InstallJobStatus.ResumeRequested;
            if (lastObservedStatus != job.Status)
            {
                lastObservedStatus = job.Status;
                try { JobChanged?.Invoke(job); } catch { }
            }
            var events = store.ReadEvents(operationId, 200);
            string latestEventKey = events.Count == 0 ? "" : events[^1].AtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "|" + events[^1].Kind + "|" + events[^1].Message;
            if (!string.Equals(latestEventKey, lastRenderedEventKey, StringComparison.Ordinal))
            {
                output.Clear();
                foreach (InstallJobEvent item in events)
                    output.AppendText(item.AtUtc.ToLocalTime().ToString("HH:mm:ss") + "  [" + item.Stage + "/" + item.Kind + "] " + item.Message + Environment.NewLine);
                output.ScrollToEnd();
                lastRenderedEventKey = latestEventKey;
            }
        }
        catch (Exception ex) { summary.Text = "Job status is unavailable: " + ex.Message; }
    }

    private void SetButtons(bool enabled)
    {
        pause.IsEnabled = enabled && pause.IsEnabled;
        resume.IsEnabled = enabled && resume.IsEnabled;
        retry.IsEnabled = enabled && retry.IsEnabled;
        stop.IsEnabled = enabled && stop.IsEnabled;
    }
}
