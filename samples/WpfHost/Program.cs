using System.IO;
using System.Windows;
using System.Windows.Controls;
using SubZeroDev.Platform.Updater;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Velopack.VelopackApp.Build().Run();
        var application = new Application();
        var window = new Window { Title = "WPF updater host", Width = 520, Height = 260 };
        var panel = new StackPanel { Margin = new Thickness(24) };
        var status = new TextBlock { Text = "Ready", TextWrapping = TextWrapping.Wrap };
        var check = new Button { Content = "Check for updates", Margin = new Thickness(0, 12, 0, 0) };
        var remember = new CheckBox { Content = "Install future updates automatically", IsChecked = false };
        var install = new Button { Content = "Install and restart", IsEnabled = false };
        panel.Children.Add(status); panel.Children.Add(check); panel.Children.Add(remember); panel.Children.Add(install); window.Content = panel;
        IUpdaterClient? updater = null; UpdateCandidate? candidate = null;
        window.Loaded += async (_, _) => {
            // Async void handler: an escaping exception would terminate the app, so report it instead.
            try {
                updater = await UpdaterClient.CreateAsync(new("SubZeroDev.UpdaterProbe", new("https://github.com/The-Running-Dev/SubZeroDev.UpdaterProbe.Releases"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpdaterWpfSample")), new Restart(window));
                updater.StateChanged += (_, state) => window.Dispatcher.BeginInvoke(() => {
                    status.Text = $"{state.Stage} {state.DownloadPercent}";
                    if (state.Stage == UpdateStage.Completed) window.Close();
                });
                if (Environment.GetEnvironmentVariable("UPDATER_SAMPLE_SMOKE") is { Length: > 0 } resultPath) {
                    var result = await updater.CheckAsync(CheckOrigin.Manual);
                    await File.WriteAllTextAsync(resultPath, result.Kind.ToString());
                    window.Close();
                } else Show(await updater.StartAutomaticCheckAsync());
            } catch (Exception ex) { status.Text = ex.Message; }
        };
        void Show(CheckResult result) { status.Text = result.Message ?? result.Kind.ToString(); candidate = result.Candidate; install.IsEnabled = result.ShouldPrompt; }
        check.Click += async (_, _) => { if (updater is not null) { check.IsEnabled = false; try { Show(await updater.CheckAsync(CheckOrigin.Manual)); } catch (Exception ex) { status.Text = ex.Message; } finally { check.IsEnabled = true; } } };
        install.Click += async (_, _) => { if (updater is not null && candidate is not null) { try { await updater.InstallAsync(candidate, remember.IsChecked == true); } catch (Exception ex) { status.Text = ex.Message; } } };
        application.Run(window);
        updater?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    private sealed class Restart(Window window) : IUpdateRestartCoordinator
    {
        public async Task<RestartDecision> RequestRestartAsync(CancellationToken cancellationToken) {
            await window.Dispatcher.InvokeAsync(() => window.IsEnabled = false, System.Windows.Threading.DispatcherPriority.Normal, cancellationToken);
            return RestartDecision.Ready;
        }
        public Task RestartAbortedAsync() => window.Dispatcher.InvokeAsync(() => window.IsEnabled = true).Task;
    }
}
