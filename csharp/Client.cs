namespace MangaLeech.Core;

sealed class Client : IDisposable
{
    readonly HttpClient http;
    readonly Options options;
    readonly ConcurrentDictionary<string, DateTimeOffset> cooldowns = new();
    public readonly ConcurrentDictionary<string, string> Prefetched = new();
    public Client(Options options)
    {
        this.options = options;
        http = new HttpClient(new SocketsHttpHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = options.Workers + options.ChapterWorkers,
            ConnectTimeout = TimeSpan.FromSeconds(Math.Min(10, options.Timeout))
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", options.UserAgent);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "identity");
    }

    public void Dispose() => http.Dispose();
    static async Task Pause(double seconds, CancellationToken ct)
    {
        // Long user delays remain cancellable and stay within Task.Delay limits.
        while (seconds > 0)
        {
            double step = Math.Min(seconds, 60);
            await Task.Delay(TimeSpan.FromSeconds(step), ct);
            seconds -= step;
        }
    }

    public async Task<string?> Transfer(string url, string? referer, Stream target, CancellationToken ct, bool calculateHash = false)
    {
        string host = new Uri(HttpUrl(url)).Authority;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await Pause(options.Delay, ct);
            while (cooldowns.TryGetValue(host, out var until) && until > DateTimeOffset.UtcNow)
                await Pause(Math.Min(1, (until - DateTimeOffset.UtcNow).TotalSeconds), ct);
            target.Position = 0;
            target.SetLength(0);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.Timeout));
            double backoff = Math.Pow(2, attempt + 1);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (referer != null)
                    request.Headers.Referrer = new Uri(referer);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    int status = (int)response.StatusCode;
                    if (status is not (429 or 500 or 502 or 503 or 504))
                        response.EnsureSuccessStatusCode();
                    double pause = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? (response.Headers.RetryAfter?.Date is { } date ? (date - DateTimeOffset.UtcNow).TotalSeconds : backoff);
                    pause = double.IsFinite(pause) ? Math.Max(0, pause) : backoff;
                    if (pause > 120)
                        throw new InvalidDataException($"HTTP {status}: Retry-After {pause:F0} s exceeds the 120 s limit.");
                    if (status is 429 or 503)
                    {
                        // Even an exhausted request must pause other chapters on this host.
                        var resumeAt = DateTimeOffset.UtcNow.AddSeconds(pause);
                        cooldowns.AddOrUpdate(host, resumeAt, (_, old) => old > resumeAt ? old : resumeAt);
                    }
                    if (attempt == 2)
                        response.EnsureSuccessStatusCode();
                    Log($"HTTP {status}, attempt {attempt + 1}/3: {url}; retrying in {pause:F1} s");
                    response.Dispose();
                    await Pause(pause, ct);
                    continue;
                }

                await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var hash = calculateHash ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
                byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
                try
                {
                    int read;
                    while ((read = await source.ReadAsync(buffer, timeout.Token)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                        hash?.AppendData(buffer, 0, read);
                    }
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                }
                if (response.Content.Headers.ContentLength is long expected && target.Position != expected)
                    throw new IOException("Incomplete response: Content-Length mismatch.");
                return hash == null ? null : Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }
            catch (Exception e) when ((e is HttpRequestException h && h.StatusCode == null) || e is IOException || (e is OperationCanceledException && !ct.IsCancellationRequested))
            {
                if (attempt == 2)
                    throw new IOException($"Transfer failed ({url}): {e.Message}", e);
                Log($"Transfer error {url}, attempt {attempt + 1}/3: {e.Message}");
                await Pause(backoff, ct);
            }
        }

        throw new IOException("Download failed.");
    }

    public async Task<byte[]> Get(string url, string? referer, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        await Transfer(url, referer, stream, ct);
        return stream.ToArray();
    }

    public async Task<string> GetText(string url, string? referer, CancellationToken ct) => Encoding.UTF8.GetString(await Get(url, referer, ct)).TrimStart('\ufeff');
}
