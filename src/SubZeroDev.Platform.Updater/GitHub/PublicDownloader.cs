using System.Net;
using Velopack.Sources;

namespace SubZeroDev.Platform.Updater;

/// <summary>A downloader that can refuse responses larger than a known size.</summary>
internal interface ISizeLimitedDownloader
{
    long? MaximumDownloadBytes { set; }
}

/// <summary>The public release repository does not exist, or is private.</summary>
internal sealed class RepositoryNotFoundException(Exception inner) : HttpRequestException("The release repository was not found.", inner, HttpStatusCode.NotFound);

/// <summary>GitHub asked the client to slow down; RetryAfter is its requested wait, when it said.</summary>
internal sealed class RateLimitedException(string message, System.Net.HttpStatusCode status, TimeSpan? retryAfter) : HttpRequestException(message, null, status)
{
    internal TimeSpan? RetryAfter { get; } = retryAfter;
}

internal sealed class PublicDownloader : IFileDownloader, ISizeLimitedDownloader, IDisposable
{
    private const int MaximumRedirects = 5;
    private static readonly string UserAgent = "SubZeroDev.Platform.Updater/" +
        (typeof(PublicDownloader).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "0");
    private readonly HttpClient client;
    private readonly TimeSpan timeout;

    internal PublicDownloader(TimeSpan timeout, HttpMessageHandler? handler = null)
    {
        this.timeout = timeout;
        // Redirects are followed by SendAsync so every hop is checked against the GitHub allowlist.
        client = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal CancellationToken OperationToken { get; set; }
    public long? MaximumDownloadBytes { get; set; }

    private static Uri CheckedUri(Uri uri)
    {
        if (uri.Scheme != "https" || (uri.Host != "github.com" && uri.Host != "api.github.com" && !uri.Host.EndsWith(".githubusercontent.com", StringComparison.Ordinal)))
            throw new InvalidDataException("Release downloads must use GitHub HTTPS URLs.");
        return uri;
    }

    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken token)
    {
        var uri = CheckedUri(new Uri(url));
        // One bounded retry for transient server errors. 403/429 are surfaced immediately.
        for (int attempt = 0, redirects = 0; ; ) {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308) {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null || ++redirects > MaximumRedirects) throw new InvalidDataException("Release download redirected too many times.");
                uri = CheckedUri(location.IsAbsoluteUri ? location : new Uri(uri, location));
                continue;
            }
            if ((int)response.StatusCode >= 500 && attempt++ == 0) {
                response.Dispose();
                await Task.Delay(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);
                continue;
            }
            if (!response.IsSuccessStatusCode) {
                var status = response.StatusCode;
                var retryAfter = RetryAfter(response);
                response.Dispose();
                if (status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) throw new RateLimitedException($"GitHub returned HTTP {(int)status}.", status, retryAfter);
                throw new HttpRequestException($"GitHub returned HTTP {(int)status}.", null, status);
            }
            return response;
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is { } retry) return retry.Delta ?? (retry.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        // A primary rate limit reports when its window resets instead.
        if (response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0" &&
            response.Headers.TryGetValues("x-ratelimit-reset", out var reset) && long.TryParse(reset.FirstOrDefault(), out var seconds))
            return DateTimeOffset.FromUnixTimeSeconds(seconds) - DateTimeOffset.UtcNow;
        return null;
    }

    // The timeout bounds the wait for response headers and, through the restart callback, each stall while reading the body.
    private async Task<T> RequestAsync<T>(string url, CancellationToken extra, Func<HttpResponseMessage, CancellationToken, Action, Task<T>> consume)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(OperationToken, extra);
        linked.CancelAfter(timeout);
        try {
            using var response = await SendAsync(url, linked.Token).ConfigureAwait(false);
            return await consume(response, linked.Token, () => linked.CancelAfter(timeout)).ConfigureAwait(false);
        } catch (OperationCanceledException) when (!OperationToken.IsCancellationRequested && !extra.IsCancellationRequested) {
            throw new TimeoutException("The update request timed out.");
        }
    }

    public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30) =>
        // Metadata is small, so one deadline covers the whole response.
        RequestAsync(url, default, async (r, token, _) => {
            if (r.Content.Headers.ContentLength > 8 * 1024 * 1024) throw new InvalidDataException("Release metadata is too large.");
            await using var input = await r.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var output = new MemoryStream();
            byte[] buffer = new byte[8192]; int count;
            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0) {
                if (output.Length + count > 8 * 1024 * 1024) throw new InvalidDataException("Release metadata is too large.");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        });

    public async Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30) =>
        System.Text.Encoding.UTF8.GetString(await DownloadBytes(url, headers, timeout).ConfigureAwait(false));

    public Task DownloadFile(string url, string targetFile, Action<int> progress, IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default) =>
        // Packages can be large, so the deadline restarts whenever data arrives and only a stalled transfer times out.
        RequestAsync(url, cancelToken, async (r, token, progressed) => {
            await using var input = await r.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var output = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            long total = r.Content.Headers.ContentLength ?? 0;
            long? limit = MaximumDownloadBytes;
            if (limit is { } declared && total > declared) throw new InvalidDataException("The package is larger than its release declares.");
            byte[] buffer = new byte[81920]; int count;
            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0) {
                if (limit is { } maximum && output.Length + count > maximum) throw new InvalidDataException("The package is larger than its release declares.");
                progressed();
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                if (total > 0) progress((int)Math.Min(100, output.Length * 100 / total));
            }
            progress(100);
            return true;
        });

    public void Dispose() => client.Dispose();
}
