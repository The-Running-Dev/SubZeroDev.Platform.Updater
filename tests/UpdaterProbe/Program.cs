using System.Reflection;
using System.Text.Json;
using SubZeroDev.Platform.Updater;
using Velopack;
using Velopack.Locators;

internal static class Program
{
private static void Main()
{
    VelopackApp.Build().Run();
    RunAsync().GetAwaiter().GetResult();
}
private static async Task RunAsync()
{
// Test fixture only. This executable is never included in a product distribution.
string controlPath = Environment.GetEnvironmentVariable("UPDATER_PROBE_CONTROL") ?? throw new InvalidOperationException("Missing probe control file.");
var control = JsonSerializer.Deserialize<Control>(File.ReadAllText(controlPath))!;
Directory.CreateDirectory(control.Data);
var locator = VelopackLocator.Current;
string version = locator.CurrentlyInstalledVersion?.ToString() ?? "unpackaged";
// The compiled version proves the relaunched binary was replaced, not only the package metadata.
string binary = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";
File.AppendAllText(Path.Combine(control.Data, "runs.log"), $"{version}|{binary}|{Environment.ProcessPath}|{locator.RootAppDir}{Environment.NewLine}");
var options = new UpdaterOptions(control.AppId, new(control.Repository), control.Data) { NetworkTimeout = TimeSpan.FromMinutes(5), PackageSigningKey = control.SigningKey };
var restart = new Restart();
await using var client = control.Feed is null
    ? await UpdaterClient.CreateAsync(options, restart, message => File.AppendAllText(Path.Combine(control.Data, "diagnostics.log"), message + Environment.NewLine))
    : new UpdaterClient(options, new VelopackEngine(options, sourceFactory: _ => new SignedFileSource(new(control.Feed))), new PreferencesStore(control.Data, null), restart,
        await new PreferencesStore(control.Data, null).LoadAsync(default));
if (control.Feed is not null) await client.VerifyLastInstallAsync();
client.StateChanged += (_, state) => File.AppendAllText(Path.Combine(control.Data, "states.log"), $"{version}|{state.Stage}|{state.DownloadPercent}|{state.Message}{Environment.NewLine}");
if (control.Target == version) { File.WriteAllText(Path.Combine(control.Data, "complete"), version); return; }
var result = await client.CheckAsync(CheckOrigin.Manual);
if (result.Candidate is not { } candidate) throw new InvalidOperationException($"{result.Kind}: {result.Message}");
if (candidate.TargetVersion != control.Target) throw new InvalidOperationException($"Expected {control.Target}, found {candidate.TargetVersion}");
await client.InstallAsync(candidate, rememberAutomaticConsent: true);
}
}

record Control(string AppId, string Repository, string Data, string Target, string? Feed, string? SigningKey);
sealed class Restart : IUpdateRestartCoordinator
{
    public Task<RestartDecision> RequestRestartAsync(CancellationToken cancellationToken) => Task.FromResult(RestartDecision.Ready);
    public Task RestartAbortedAsync() => Task.CompletedTask;
}
