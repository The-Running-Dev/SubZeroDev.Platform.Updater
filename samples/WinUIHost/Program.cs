using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SubZeroDev.Platform.Updater;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Velopack.VelopackApp.Build().Run();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(initialization => {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new HostApplication();
        });
    }
}

internal sealed class HostApplication : Application, IUpdateRestartCoordinator
{
    private Window? window;
    private UpdateCandidate? candidate;
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new Window { Title = "WinUI updater host" };
        var status = new TextBlock { Text = "Ready", TextWrapping = TextWrapping.Wrap };
        var check = new Button { Content = "Check for updates" };
        var remember = new CheckBox { Content = "Install future updates automatically", IsChecked = false };
        var install = new Button { Content = "Install and restart", IsEnabled = false };
        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(24) };
        panel.Children.Add(status); panel.Children.Add(check); panel.Children.Add(remember); panel.Children.Add(install);
        window.Content = panel; window.Activate();
        // Async void handler: an escaping exception would terminate the app, so report it instead.
        try {
            var client = await UpdaterClient.CreateAsync(new("SubZeroDev.UpdaterProbe", new("https://github.com/The-Running-Dev/SubZeroDev.UpdaterProbe.Releases"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpdaterWinUISample")), this);
            client.StateChanged += (_, state) => window.DispatcherQueue.TryEnqueue(() => {
                status.Text = $"{state.Stage} {state.DownloadPercent}";
                if (state.Stage == UpdateStage.Completed) window.Close();
            });
            void Show(CheckResult result) { status.Text = result.Message ?? result.Kind.ToString(); candidate = result.Candidate; install.IsEnabled = result.ShouldPrompt; }
            check.Click += async (_, _) => { check.IsEnabled = false; try { Show(await client.CheckAsync(CheckOrigin.Manual)); } catch (Exception ex) { status.Text = ex.Message; } finally { check.IsEnabled = true; } };
            install.Click += async (_, _) => { if (candidate is not null) try { await client.InstallAsync(candidate, remember.IsChecked == true); } catch (Exception ex) { status.Text = ex.Message; } };
            window.Closed += async (_, _) => await client.DisposeAsync();
            if (Environment.GetEnvironmentVariable("UPDATER_SAMPLE_SMOKE") is { Length: > 0 } resultPath) {
                var result = await client.CheckAsync(CheckOrigin.Manual);
                await File.WriteAllTextAsync(resultPath, result.Kind.ToString());
                window.Close();
            } else Show(await client.StartAutomaticCheckAsync());
        } catch (Exception ex) { status.Text = ex.Message; }
    }
    public Task<RestartDecision> RequestRestartAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<RestartDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        window!.DispatcherQueue.TryEnqueue(() => {
            foreach (var control in ((StackPanel)window.Content).Children.OfType<Control>()) control.IsEnabled = false;
            completion.SetResult(RestartDecision.Ready);
        });
        return completion.Task.WaitAsync(cancellationToken);
    }
    public Task RestartAbortedAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!window!.DispatcherQueue.TryEnqueue(() => {
            foreach (var control in ((StackPanel)window.Content).Children.OfType<Control>()) control.IsEnabled = true;
            completion.SetResult();
        })) completion.SetResult();
        return completion.Task;
    }
}
