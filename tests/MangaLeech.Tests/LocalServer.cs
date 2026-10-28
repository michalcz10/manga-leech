using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MangaLeech.Core;

internal sealed class LocalServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentBag<Task> requests = new();
    private readonly Task accepting;
    private int activeImages;
    private int connections;
    private readonly Dictionary<string, int> activeChapters = [];
    private readonly object gate = new();
    public ConcurrentDictionary<string, int> Hits { get; } = new();
    public int MaxImages { get; private set; }
    public int Connections => Volatile.Read(ref connections);
    public int MaxChapters { get; private set; }
    public bool WrongReferer { get; private set; }
    public bool FailChapter { get; set; }
    public bool RateLimitOnce { get; set; }
    public int ImageDelay { get; set; } = 40;
    public bool StallFirstTailImages { get; set; }
    public string Url { get; }
    private readonly byte[] image;

    public LocalServer(byte[] image)
    {
        this.image = image;
        listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        accepting = Accept();
    }

    // Reference signatures from the original JavaScript, not the code under test.
    private static readonly Dictionary<string, string> MangaSignatures = new()
    {
        ["/titles/fixture"] = "8sK3xtqdFdv6BM031c2U",
        ["/titles/fixture/chapters?language=en&limit=100&order=asc&page=1&sort=number"] = "8sK3xtqdFdv6BM031c2U0rWQQ6vJu9rm5Msk-Sb6UX-Iy_I6tm2YFvnMRWBx5oC4ARwU6TDfdvmcnPYwWfLNlp-wDodS6tfgDCOn",
        ["/titles/fixture/chapters?language=en&limit=100&order=asc&page=2&sort=number"] = "8sK3xtqdFdv6BM031c2U0rWQQ6vJu9rm5Msk-Sb6UX-Iy_I6tm2YFvnMRWBx5oC4ARwU6TDfdvmcnPYwWfJ3FWUF0HfkqgjwLA9C",
        ["/chapters/2"] = "8vPRXa1JjvVTxjA",
        ["/chapters/3"] = "8vPRXa1JjvVTxuY",
    };

    private async Task Accept()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                Interlocked.Increment(ref connections);
                requests.Add(Respond(client));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private async Task Respond(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                while (!stop.IsCancellationRequested)
                {
                string? request = await reader.ReadLineAsync(stop.Token);
                if (request == null) return;
                string path = request.Split(' ')[1];
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 } line)
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0) headers[line[..colon]] = line[(colon + 1)..].Trim();
                }
                int hit = Hits.AddOrUpdate(path, 1, (_, value) => value + 1);
                byte[] body;
                int status = 200;
                string extra = "";
                if (path.StartsWith("/api/"))
                {
                    var requestUri = new Uri(Url + path);
                    var parameters = requestUri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2))
                        .ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => Uri.UnescapeDataString(parts[1]));
                    string apiPath = requestUri.AbsolutePath[4..];
                    parameters.TryGetValue("page", out string? page);
                    string canonical = string.Join("&", parameters.Where(pair => pair.Key != "vrf")
                        .OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value));
                    string signedInput = apiPath + (canonical.Length == 0 ? "" : "?" + canonical);
                    string? signature = MangaSignatures.TryGetValue(signedInput, out string? known) ? known
                        : apiPath.StartsWith("/titles/limit", StringComparison.Ordinal) ? MangaFireSigner.Sign(apiPath, parameters.Where(pair => pair.Key != "vrf").ToDictionary()) : null;
                    if (signature == null ||
                        !parameters.TryGetValue("vrf", out string? supplied) || supplied != signature)
                    {
                        status = 403;
                        body = Encoding.UTF8.GetBytes("{\"message\":\"Bad signature\"}");
                    }
                    else if (apiPath == "/titles/fixture")
                        body = Encoding.UTF8.GetBytes("{\"data\":{\"title\":\"HTTP Series\"}}");
                    else if (apiPath == "/titles/limit")
                        body = Encoding.UTF8.GetBytes("{\"data\":{\"title\":\"Limit Series\"}}");
                    else if (apiPath == "/titles/limit/chapters")
                    {
                        bool newest = parameters.TryGetValue("order", out string? order) && order == "desc";
                        string items = page switch
                        {
                            "1" when newest => "{\"id\":4,\"number\":4,\"language\":\"en\",\"type\":\"official\"},{\"id\":3,\"number\":3,\"language\":\"en\",\"type\":\"official\"}",
                            "1" => "{\"id\":1,\"number\":1,\"language\":\"en\",\"type\":\"official\"},{\"id\":2,\"number\":2,\"language\":\"en\",\"type\":\"official\"}",
                            "2" when newest => "{\"id\":2,\"number\":2,\"language\":\"en\",\"type\":\"official\"}",
                            "2" => "{\"id\":3,\"number\":3,\"language\":\"en\",\"type\":\"official\"}",
                            _ when newest => "{\"id\":1,\"number\":1,\"language\":\"en\",\"type\":\"official\"}",
                            _ => "{\"id\":4,\"number\":4,\"language\":\"en\",\"type\":\"official\"}"
                        };
                        body = Encoding.UTF8.GetBytes($"{{\"items\":[{items}],\"meta\":{{\"page\":{page},\"lastPage\":3}}}}");
                    }
                    else if (apiPath == "/titles/fixture/chapters")
                    {
                        string items = page == "1"
                            ? "{\"id\":1,\"number\":1,\"language\":\"en\",\"type\":\"unofficial\"}"
                            : "{\"id\":2,\"number\":1,\"language\":\"en\",\"type\":\"official\"},{\"id\":3,\"number\":2.5,\"language\":\"en\",\"type\":\"unofficial\"}";
                        body = Encoding.UTF8.GetBytes($"{{\"items\":[{items}],\"meta\":{{\"page\":{page},\"lastPage\":2}}}}");
                    }
                    else
                        body = Encoding.UTF8.GetBytes("{\"data\":{\"title\":{\"name\":\"HTTP Series\"},\"number\":1,\"pages\":[{\"url\":\"/image/1/1\"}]}}");
                }
                else if (path.StartsWith("/image/"))
                {
                    string chapter = path.Split('/')[2];
                    lock (gate)
                    {
                        activeImages++;
                        activeChapters.TryGetValue(chapter, out int chapterRequests);
                        activeChapters[chapter] = chapterRequests + 1;
                        MaxImages = Math.Max(MaxImages, activeImages);
                        MaxChapters = Math.Max(MaxChapters, activeChapters.Count);
                        if (!headers.TryGetValue("Referer", out string? referer) || referer != $"{Url}/chapters/{chapter}")
                            WrongReferer = true;
                    }
                    int delay = StallFirstTailImages && hit == 1 && path.EndsWith("/3") && (chapter is "1" or "2") ? 500 : ImageDelay;
                    try { await Task.Delay(delay, stop.Token); }
                    finally
                    {
                        lock (gate)
                        {
                            activeImages--;
                            if (--activeChapters[chapter] == 0) activeChapters.Remove(chapter);
                        }
                    }
                    body = image;
                }
                else if (path.StartsWith("/chapters/"))
                {
                    string chapter = path.Split('/')[2];
                    if (FailChapter && chapter == "2")
                    {
                        status = 404;
                        body = [];
                    }
                    else
                    {
                        string images = string.Concat(Enumerable.Range(1, 3).Select(page => $"<img src='/image/{chapter}/{page}'/>"));
                        body = Encoding.UTF8.GetBytes($"<title>Chapter {chapter} | Test Series | Weeb Central</title><div id='chapter-images'>{images}</div>");
                    }
                }
                else if (path == "/always-retry" || (path == "/retry" && RateLimitOnce && hit == 1))
                {
                    status = 429;
                    extra = "Retry-After: 1\r\n";
                    body = [];
                }
                else if (path == "/long-retry")
                {
                    status = 503;
                    extra = "Retry-After: 121\r\n";
                    body = [];
                }
                else if (path == "/series/test")
                {
                    body = Encoding.UTF8.GetBytes("<title>Test Series | Weeb Central</title><button hx-get='/series/test/full-chapter-list'>Show All</button>");
                }
                else if (path == "/series/test/full-chapter-list")
                {
                    body = Encoding.UTF8.GetBytes("<a href='/chapters/3'>Chapter 3</a><a href='/chapters/1'>Chapter 1</a><a href='/chapters/2'>Chapter 2</a>");
                }
                else
                {
                    body = Encoding.UTF8.GetBytes("ok");
                }
                byte[] response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Length: {body.Length}\r\nConnection: keep-alive\r\n{extra}\r\n");
                await stream.WriteAsync(response, stop.Token);
                await stream.WriteAsync(body, stop.Token);
                if (headers.TryGetValue("Connection", out string? connection) && connection.Equals("close", StringComparison.OrdinalIgnoreCase)) return;
                }
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or SocketException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener.Stop();
        await accepting;
        await Task.WhenAll(requests);
        stop.Dispose();
    }
}
