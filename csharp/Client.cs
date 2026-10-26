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

    public async Task Transfer(string url, string? referer, Stream target, CancellationToken ct)
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
                await source.CopyToAsync(target, timeout.Token);
                if (response.Content.Headers.ContentLength is long expected && target.Position != expected)
                    throw new IOException("Incomplete response: Content-Length mismatch.");
                return;
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
    public async Task<string> ChapterHtml(string url, CancellationToken ct)
    {
        string html = Prefetched.TryRemove(url, out var cached) ? cached : await GetText(url, null, ct);
        if (Html(html).GetElementbyId("chapter-images")?.Descendants("img").Any() == true)
            return html;
        var match = Regex.Match(html, "[\"']([^\"']+/images\\?is_prev=False)[\"']");
        return match.Success ? await GetText(SameHost(WebUtility.HtmlDecode(match.Groups[1].Value), url) + "&reading_style=long_strip&current_page=1", url, ct) : html;
    }

    public async Task<List<Chapter>> Series(string url, CancellationToken ct)
    {
        var doc = Html(await GetText(url, null, ct));
        string name = Text(doc.DocumentNode.SelectSingleNode("//title")).Split(" | ")[0].Trim();
        var endpoint = doc.DocumentNode.Descendants().Select(n => n.GetAttributeValue("hx-get", "")).FirstOrDefault(s => s.Length > 0 && new Uri(new Uri(url), WebUtility.HtmlDecode(s)).AbsolutePath.EndsWith("/full-chapter-list"));
        HtmlNode root;
        if (endpoint != null)
            root = Html(await GetText(SameHost(WebUtility.HtmlDecode(endpoint), url), url, ct)).DocumentNode;
        else
            root = doc.GetElementbyId("chapter-list") ?? throw new InvalidDataException("Neither a full chapter list nor #chapter-list was found.");
        var result = new List<Chapter>();
        var seen = new HashSet<string>();
        foreach (var a in root.Descendants("a"))
        {
            string href = WebUtility.HtmlDecode(a.GetAttributeValue("href", ""));
            if (href.Length == 0)
                continue;
            string resolved = HttpUrl(href, url);
            if (!Regex.IsMatch(new Uri(resolved).AbsolutePath, @"^/chapters/[^/]+$"))
                continue;
            var label = Label.Match(Regex.Replace(Text(a), @"\s+", " "));
            if (!label.Success)
                continue;
            string link = SameHost(resolved, url);
            if (seen.Add(link))
                result.Add(new Chapter($"{name} {label.Value}".Trim(), link));
        }

        if (result.Count == 0)
            throw new InvalidDataException("The full series list contains no chapters.");
        return result;
    }
}
