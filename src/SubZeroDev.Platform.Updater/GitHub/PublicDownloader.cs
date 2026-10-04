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
    private readonly TimeSpan packageDownloadTimeout;

    internal PublicDownloader(TimeSpan timeout, HttpMessageHandler? handler = null, TimeSpan? packageDownloadTimeout = null)
    {
        this.timeout = timeout;
        this.packageDownloadTimeout = packageDownloadTimeout ?? TimeSpan.FromMinutes(30);
        // Redirects are followed by SendAsync so every hop is checked against the GitHub allowlist.
        client = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal CancellationToken OperationToken { get; set; }
    public long? MaximumDownloadBytes { get; set; }

    private static Uri CheckedUri(Uri uri)
    {
        if (uri.Scheme != "https" || (uri.Host != "github.com" && uri.Host != "api.github.com" && !uri.Host.EndsWith(".githubusercontent.com", StringComparison.Ordinal)))
            throw new InvalidDataException("Release downloads must use GitHub HTTPS URLs.");
        return uri;
    }

    private async Task<HttpResponseMessage> SendAsync(string url, IDictionary<string, string>? headers, CancellationToken token)
    {
        var uri = CheckedUri(new Uri(url));
        var forwardSensitiveHeaders = true;
        // One bounded retry for transient server errors. 403/429 are surfaced immediately.
        for (int attempt = 0, redirects = 0; ; ) {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            if (headers is not null) foreach (var header in headers) {
                // Host is determined by the checked URL. Unknown custom headers may carry credentials.
                if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
                if (!forwardSensitiveHeaders && !header.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase) &&
                    !header.Key.Equals("Range", StringComparison.OrdinalIgnoreCase) && !header.Key.Equals("If-Range", StringComparison.OrdinalIgnoreCase)) continue;
                if (header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) request.Headers.UserAgent.Clear();
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308) {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null || ++redirects > MaximumRedirects) throw new InvalidDataException("Release download redirected too many times.");
                var next = CheckedUri(location.IsAbsoluteUri ? location : new Uri(uri, location));
                forwardSensitiveHeaders &= uri.Authority.Equals(next.Authority, StringComparison.OrdinalIgnoreCase);
                uri = next;
                continue;
            }
            if ((int)response.StatusCode >= 500 && attempt++ == 0) {
                response.Dispose();
                await Task.Delay(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);
                continue;
            }
            if (!response.IsSuccessStatusCode) {
                using (response) {
                    var status = response.StatusCode;
                    if (status == HttpStatusCode.TooManyRequests || status == HttpStatusCode.Forbidden &&
                        (response.Headers.RetryAfter is not null || Exhausted(response) || await HasRateLimitMessageAsync(response, token).ConfigureAwait(false)))
                        throw new RateLimitedException($"GitHub returned HTTP {(int)status}.", status, RetryAfter(response));
                    throw new HttpRequestException($"GitHub returned HTTP {(int)status}.", null, status);
                }
            }
            return response;
        }
    }

    private static bool Exhausted(HttpResponseMessage response) =>
        response.Headers.TryGetValues("x-ratelimit-remaining", out var values) && values.FirstOrDefault()?.Trim() == "0";

    private static async Task<bool> HasRateLimitMessageAsync(HttpResponseMessage response, CancellationToken token)
    {
        // Secondary throttling may have only a JSON message. Bound untrusted error bodies and keep cancellation intact.
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new byte[8193];
        var count = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, token).ConfigureAwait(false);
        if (count == buffer.Length) return false;
        try {
            using var json = System.Text.Json.JsonDocument.Parse(buffer.AsMemory(0, count));
            if (json.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("message", out var message) || message.ValueKind != System.Text.Json.JsonValueKind.String) return false;
            var text = message.GetString()!;
            return text.Contains("rate limit", StringComparison.OrdinalIgnoreCase) || text.Contains("abuse detection", StringComparison.OrdinalIgnoreCase);
        } catch (System.Text.Json.JsonException) { return false; }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is { } retry) return retry.Delta ?? (retry.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        // A primary rate limit reports when its window resets instead.
        if (response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0" &&
            response.Headers.TryGetValues("x-ratelimit-reset", out var reset) && long.TryParse(reset.FirstOrDefault(), out var seconds))
            if (seconds is >= -62135596800 and <= 253402300799) return DateTimeOffset.FromUnixTimeSeconds(seconds) - DateTimeOffset.UtcNow;
        return null;
    }

    // The timeout bounds the wait for response headers and, through the restart callback, each stall while reading the body.
    private async Task<T> RequestAsync<T>(string url, IDictionary<string, string>? headers, double timeoutMinutes, CancellationToken extra, Func<HttpResponseMessage, CancellationToken, Action, Task<T>> consume, TimeSpan? totalTimeout = null)
    {
        // IFileDownloader specifies minutes and a maximum completion time, including for metadata.
        if (!double.IsFinite(timeoutMinutes) || timeoutMinutes <= 0 || timeoutMinutes > TimeSpan.FromMilliseconds(uint.MaxValue - 1).TotalMinutes)
            throw new ArgumentOutOfRangeException(nameof(timeoutMinutes));
        var callerTimeout = TimeSpan.FromMinutes(timeoutMinutes);
        var stallTimeout = callerTimeout < timeout ? callerTimeout : timeout;
        var total = totalTimeout is { } configured && configured < callerTimeout ? configured : callerTimeout;
        using var deadline = new CancellationTokenSource(total);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(OperationToken, extra, deadline.Token);
        linked.CancelAfter(stallTimeout);
        try {
            using var response = await SendAsync(url, headers, linked.Token).ConfigureAwait(false);
            return await consume(response, linked.Token, () => linked.CancelAfter(stallTimeout)).ConfigureAwait(false);
        } catch (OperationCanceledException) when (!OperationToken.IsCancellationRequested && !extra.IsCancellationRequested) {
            throw new TimeoutException("The update request timed out.");
        }
    }

    public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30) =>
        // Metadata is small, so one deadline covers the whole response.
        RequestAsync(url, headers, timeout, default, async (r, token, _) => {
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
        // Progress resets the stall timer, while a separate total deadline bounds a trickling transfer.
        RequestAsync(url, headers, timeout, cancelToken, async (r, token, progressed) => {
            await using var input = await r.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var output = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            long total = r.Content.Headers.ContentLength ?? 0;
            long? limit = MaximumDownloadBytes;
            if (limit is { } declared && total > declared) throw new InvalidDataException("The package is larger than its release declares.");
            byte[] buffer = new byte[81920]; int count;
            int lastProgress = -1;
            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0) {
                if (limit is { } maximum && output.Length + count > maximum) throw new InvalidDataException("The package is larger than its release declares.");
                progressed();
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                if (total > 0) {
                    // Reserve completion for EOF, even when Content-Length understates the body.
                    int percent = (int)Math.Min(99, (double)output.Length / total * 100);
                    if (percent > lastProgress) { lastProgress = percent; progress(percent); }
                }
            }
            progress(100);
            return true;
        }, packageDownloadTimeout);

    public void Dispose() => client.Dispose();
}
