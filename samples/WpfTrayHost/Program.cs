using System.IO;
using System.Windows;
using Application = System.Windows.Application;
using Forms = System.Windows.Forms;
using SubZeroDev.Platform.Updater;

// A WPF host with no main window: a tray icon owns the updater menu. WPF has no tray control, so the icon is a
// WinForms NotifyIcon, and the updater's StateChanged is marshalled onto the WPF dispatcher before touching it.
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // First operational line: Velopack hooks run through this same executable, before any UI exists.
        Velopack.VelopackApp.Build().Run();
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var host = new TrayHost(application);
        application.Startup += async (_, _) => await host.StartAsync();
        application.Run();
        host.DisposeUpdater();
    }
}

internal sealed class TrayHost : IUpdateRestartCoordinator, IDisposable
{
    // Users see these, never exception text, which can carry local paths.
    private const string Unavailable = "Updates are unavailable right now.";
    private const string InstallFailed = "The update could not be installed. The current version is still running.";

    private readonly Application application;
    private readonly Forms.NotifyIcon icon = new() { Icon = System.Drawing.SystemIcons.Application, Text = "WPF tray updater host", Visible = true };
    private readonly Forms.ToolStripMenuItem status = new("Ready") { Enabled = false };
    private readonly Forms.ToolStripMenuItem lastInstall = new() { Enabled = false, Visible = false };
    private readonly Forms.ToolStripMenuItem check = new("Check for updates…");
    private readonly Forms.ToolStripMenuItem automatic = new("Automatically check for updates");
    private readonly Forms.ToolStripMenuItem stable = new("Stable channel");
    private readonly Forms.ToolStripMenuItem preview = new("Preview channel");
    private readonly Forms.ToolStripMenuItem install = new("Install update and restart") { Enabled = false };
    private readonly Forms.ToolStripMenuItem remember = new("Install future updates without asking") { CheckOnClick = true };
    private IUpdaterClient? updater;
    private UpdateCandidate? candidate;

    internal TrayHost(Application application)
    {
        this.application = application;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.AddRange([status, lastInstall, new Forms.ToolStripSeparator(), check, install, remember, new Forms.ToolStripSeparator(), automatic, stable, preview,
            new Forms.ToolStripSeparator(), new Forms.ToolStripMenuItem("Quit", null, (_, _) => application.Shutdown())]);
        icon.ContextMenuStrip = menu;
        check.Click += async (_, _) => await CheckNowAsync();
        install.Click += async (_, _) => {
            if (updater is null || candidate is null) return;
            try { await updater.InstallAsync(candidate, remember.Checked); } catch (Exception) { status.Text = InstallFailed; }
        };
        automatic.Click += async (_, _) => await Save(p => p with { CheckAutomatically = !p.CheckAutomatically });
        stable.Click += async (_, _) => await ChooseChannel(UpdateChannel.Stable);
        preview.Click += async (_, _) => await ChooseChannel(UpdateChannel.Preview);
    }

    internal async Task StartAsync()
    {
        // An async void entry point: an escaping exception would terminate the app, so report it instead.
        try {
            updater = await UpdaterClient.CreateAsync(new("SubZeroDev.UpdaterProbe", new("https://github.com/The-Running-Dev/SubZeroDev.UpdaterProbe.Releases"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpdaterWpfTraySample")) {
                    PackageSigningKey = System.Reflection.CustomAttributeExtensions.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(Program).Assembly)
                        .FirstOrDefault(a => a.Key == "UpdaterPackageSigningKey")?.Value
                        ?? (Environment.GetEnvironmentVariable("UPDATER_SAMPLE_SMOKE") is { Length: > 0 } ? null
                            : throw new InvalidOperationException("Build with UpdaterPackageSigningKey before enabling updates."))
                }, this);
            if (updater.LastInstallOutcome is { } outcome) { lastInstall.Text = outcome.Message; lastInstall.Visible = true; }
            // StateChanged has no thread guarantee; the menu belongs to the dispatcher thread.
            updater.StateChanged += (_, state) => application.Dispatcher.BeginInvoke(() => {
                status.Text = state.Message ?? $"{state.Stage} {state.DownloadPercent}".Trim();
                if (state.Stage == UpdateStage.Completed) application.Shutdown();
            });
            Refresh();
            if (Environment.GetEnvironmentVariable("UPDATER_SAMPLE_SMOKE") is { Length: > 0 } resultPath) {
                var result = await updater.CheckAsync(CheckOrigin.Manual);
                await File.WriteAllTextAsync(resultPath, result.Kind.ToString());
                application.Shutdown();
            } else Show(await updater.StartAutomaticCheckAsync());
        } catch (Exception) { status.Text = Unavailable; }
    }

    private async Task CheckNowAsync()
    {
        if (updater is null) return;
        check.Enabled = false;
        try { Show(await updater.CheckAsync(CheckOrigin.Manual)); } catch (Exception) { status.Text = Unavailable; } finally { check.Enabled = true; }
    }

    private void Show(CheckResult result)
    {
        status.Text = result.Message ?? (result.Kind == CheckOutcomeKind.UpdateAvailable ? $"Update {result.Candidate?.TargetVersion} is available." : result.Kind.ToString());
        candidate = result.Candidate;
        install.Enabled = result.ShouldPrompt;
    }

    private void Refresh()
    {
        var preferences = updater!.Preferences;
        automatic.Checked = preferences.CheckAutomatically;
        stable.Checked = preferences.Channel == UpdateChannel.Stable;
        preview.Checked = preferences.Channel == UpdateChannel.Preview;
    }

    private async Task Save(Func<UpdaterPreferences, UpdaterPreferences> change)
    {
        if (updater is null) return;
        try { await updater.SavePreferencesAsync(change(updater.Preferences)); Refresh(); } catch (Exception) { status.Text = Unavailable; }
    }

    private async Task ChooseChannel(UpdateChannel channel)
    {
        await Save(p => p with { Channel = channel });
        // A channel switch is a manual-visible check for the new channel.
        await CheckNowAsync();
    }

    // Ready means the tray icon is released, so a restarting process never leaves a ghost icon behind.
    public async Task<RestartDecision> RequestRestartAsync(CancellationToken cancellationToken)
    {
        await application.Dispatcher.InvokeAsync(() => icon.Visible = false, System.Windows.Threading.DispatcherPriority.Normal, cancellationToken);
        return RestartDecision.Ready;
    }

    public Task RestartAbortedAsync() => application.Dispatcher.InvokeAsync(() => icon.Visible = true).Task;

    internal void DisposeUpdater() => updater?.DisposeAsync().AsTask().GetAwaiter().GetResult();

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
    }
}
