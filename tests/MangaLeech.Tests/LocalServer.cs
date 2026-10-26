using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

internal sealed class LocalServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentBag<Task> requests = new();
    private readonly Task accepting;
    private int activeImages;
    private readonly Dictionary<string, int> activeChapters = [];
    private readonly object gate = new();
    public ConcurrentDictionary<string, int> Hits { get; } = new();
    public int MaxImages { get; private set; }
    public int MaxChapters { get; private set; }
    public bool WrongReferer { get; private set; }
    public bool FailChapter { get; set; }
    public bool RateLimitOnce { get; set; }
    public int ImageDelay { get; set; } = 40;
    public string Url { get; }
    private readonly byte[] image;

    public LocalServer(byte[] image)
    {
        this.image = image;
        listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        accepting = Accept();
    }

    private async Task Accept()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
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
                if (path.StartsWith("/image/"))
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
                    try { await Task.Delay(ImageDelay, stop.Token); }
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
                byte[] response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Length: {body.Length}\r\nConnection: close\r\n{extra}\r\n");
                await stream.WriteAsync(response, stop.Token);
                await stream.WriteAsync(body, stop.Token);
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
