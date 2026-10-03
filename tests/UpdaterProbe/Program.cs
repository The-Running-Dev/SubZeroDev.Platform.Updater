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
    : new UpdaterClient(options, new VelopackEngine(options, handler: new FakeGithub(new(control.Repository), control.Feed)), new PreferencesStore(control.Data, null), restart,
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

// Serves a local vpk output directory as if it were a GitHub repository's releases, so the probe runs the real
// ValidatedGithubSource, downloader, redirect allowlist and signature lookup. Package downloads go through a redirect
// to a githubusercontent host, as real GitHub does.
sealed class FakeGithub(Uri repository, string directory) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        string repo = repository.AbsolutePath.TrimEnd('/');
        if (uri.Host == "api.github.com" && uri.AbsolutePath == $"/repos{repo}/releases")
            return Task.FromResult(Json(uri.Query.Contains("page=1") ? Releases() : "[]"));
        if (uri.Host == "github.com" && uri.AbsolutePath.StartsWith($"{repo}/releases/download/", StringComparison.Ordinal)) {
            var redirect = new HttpResponseMessage(System.Net.HttpStatusCode.Found);
            redirect.Headers.Location = new Uri($"https://objects.githubusercontent.com/fake{uri.AbsolutePath}");
            return Task.FromResult(redirect);
        }
        if (uri.Host == "objects.githubusercontent.com" && uri.AbsolutePath.StartsWith("/fake", StringComparison.Ordinal)) {
            string path = Path.Combine(directory, Path.GetFileName(uri.AbsolutePath));
            return Task.FromResult(File.Exists(path)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(path)) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }

    private static HttpResponseMessage Json(string json) => new(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) };

    private string Releases()
    {
        string repo = repository.AbsolutePath.TrimEnd('/');
        var files = Directory.GetFiles(directory).Select(Path.GetFileName).ToArray();
        // vpk's feed lists every package; one release per distinct full-package version mirrors the publish pipeline.
        var feed = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "releases.win-stable.json")));
        var versions = feed.RootElement.GetProperty("Assets").EnumerateArray()
            .Where(a => a.GetProperty("Type").GetString() == "Full").Select(a => a.GetProperty("Version").GetString()!).Distinct();
        return JsonSerializer.Serialize(versions.Select(version => new {
            tag_name = "v" + version, prerelease = false, draft = false, published_at = "2026-10-01T00:00:00Z",
            assets = files.Select(name => new { name, browser_download_url = $"https://github.com{repo}/releases/download/v{version}/{name}" })
        }));
    }
}
