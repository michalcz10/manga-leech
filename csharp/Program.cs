// Samostatná zkušební C# varianta. Vyžaduje .NET SDK 10:
// dotnet run --project csharp -- "https://weebcentral.com/series/..." --list
// dotnet run --project csharp -- --chapter "https://weebcentral.com/chapters/..."
// NuGet při prvním spuštění stáhne HtmlAgilityPack (MIT) a ImageSharp
// (Six Labors Split License). Python verze ani její závislosti nejsou potřeba.
// ImageSharp 4 upozorňuje na chybějící licenci; Release sestavení vyžaduje
// platnou licenci, viz https://sixlabors.com/pricing/.
// Cache má stejné názvy jako v Pythonu; ověření a PDF manifest mají vlastní verzi.
// PDF úlohy používají omezený počet vláken, nikoli recyklované podprocesy.
// --pdf-tasks-per-child proto nemá v C# ekvivalent a není přijímán.

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using HtmlAgilityPack;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

return await MangaLeech.MainAsync(args);

record Chapter(string Title, string Url);

sealed class Options
{
    public string? Source, Chapter;
    public string Output = "downloads", UserAgent = "MangaLeech.CSharp/0.1";
    public bool List, Newest, Oldest, Force, Rebuild, Manifest, Help;
    public int Workers = 4, PdfWorkers = 1;
    public int? Limit, JpegQuality;
    public double Timeout = 60, Delay = .5;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException("Chybi hodnota parametru.");
            int Integer() => int.Parse(Value(), CultureInfo.InvariantCulture);
            double Number() => double.Parse(Value(), CultureInfo.InvariantCulture);
            switch (args[i])
            {
                case "-h": case "--help": o.Help = true; break;
                case "--chapter": o.Chapter = Value(); break;
                case "-o": case "--output": o.Output = Value(); break;
                case "--user-agent": o.UserAgent = Value(); break;
                case "--list": o.List = true; break;
                case "--limit": o.Limit = Integer(); break;
                case "--oldest-first": o.Oldest = true; break;
                case "--newest-first": o.Newest = true; break;
                case "--force": o.Force = true; break;
                case "--rebuild": o.Rebuild = true; break;
                case "--write-manifest": o.Manifest = true; break;
                case "--jpeg-quality": o.JpegQuality = Integer(); break;
                case "--workers": o.Workers = Integer(); break;
                case "--pdf-workers": o.PdfWorkers = Integer(); break;
                case "--timeout": o.Timeout = Number(); break;
                case "--delay": o.Delay = Number(); break;
                case "--pdf-tasks-per-child":
                    throw new ArgumentException("C# pouziva PDF vlakna, ne podprocesy. Vynechte --pdf-tasks-per-child.");
                default:
                    if (args[i].StartsWith('-') || o.Source != null)
                        throw new ArgumentException($"Neznamy argument: {args[i]}");
                    o.Source = args[i]; break;
            }
        }
        if (o.Help) return o;
        if ((o.Source == null) == (o.Chapter == null)) throw new ArgumentException("Zadejte RSS / stranku serie, nebo --chapter URL.");
        if (o.Newest && o.Oldest) throw new ArgumentException("Vyberte pouze jeden smer razeni.");
        if (!double.IsFinite(o.Timeout) || o.Timeout <= 0 || o.Timeout > TimeSpan.MaxValue.TotalSeconds / 2 ||
            !double.IsFinite(o.Delay) || o.Delay < 0 || o.Delay > TimeSpan.MaxValue.TotalSeconds / 2 ||
            o.Workers < 1 || o.PdfWorkers < 1 || o.PdfWorkers > 1024 || o.Limit is <= 0)
            throw new ArgumentException("Neplatny timeout, delay, workers, pdf-workers nebo limit.");
        if (o.JpegQuality is < 1 or > 95) throw new ArgumentException("jpeg-quality musi byt 1 az 95.");
        return o;
    }
}

static class MangaLeech
{
    internal const string CheckVersion = "csharp-imagesharp-v1", PdfVersion = "csharp-lossless-v1";
    internal static readonly Regex Label = new(@"\b(?:Chapter|Page|Volume|Vol|Episode|Kapitel)\.?\s+(\d+(?:\.\d+)?)(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    internal static void Log(string value) => Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} {value}");
    internal static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    internal static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));
    internal static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    internal static string HttpUrl(string value, string? basis = null)
    {
        var uri = basis == null ? new Uri(value, UriKind.Absolute) : new Uri(new Uri(basis), value);
        if (uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
            throw new ArgumentException($"Neplatna HTTP(S) adresa: {value}");
        return uri.AbsoluteUri;
    }
    internal static string SameHost(string value, string basis)
    {
        string url = HttpUrl(value, basis);
        if (!string.Equals(new Uri(url).Authority, new Uri(basis).Authority, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Seznam odkazuje na jinou domenu.");
        return url;
    }
    internal static HtmlDocument Html(string text) { var doc = new HtmlDocument(); doc.LoadHtml(text); return doc; }
    internal static string Text(HtmlNode? node) => WebUtility.HtmlDecode(node?.InnerText ?? "").Trim();
    internal static Match? LastLabel(string title) => Label.Matches(title).Cast<Match>().LastOrDefault();
    internal static (string Title, string? Number) Metadata(string title)
    {
        var m = LastLabel(title);
        return m != null && title[..m.Index].Trim() is { Length: > 0 } series
            ? (series, m.Groups[1].Value) : (title, null);
    }
    static string Clean(string title)
    {
        title = Regex.Replace(title.Normalize(NormalizationForm.FormKC), "[<>:\"/\\\\|?*\\x00-\\x1f]", "_").Trim(' ', '.');
        return title.Length > 120 ? title[..120] : title.Length == 0 ? "chapter" : title;
    }
    internal static string CacheName(Chapter ch) => $"{Clean(ch.Title)}-{Hash(ch.Url)[..10]}";
    internal static string PdfName(Chapter ch)
    {
        var (series, number) = Metadata(ch.Title);
        if (number == null) return CacheName(ch);
        var parts = number.Split('.');
        string index = BigInteger.Parse(parts[0], CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture).PadLeft(3, '0');
        if (parts.Length > 1) index += "." + parts[1];
        string kind = LastLabel(ch.Title)!.Value.StartsWith("Vol", StringComparison.OrdinalIgnoreCase) ? "Vol" : "Ch";
        return $"{Clean(series)} - {kind} {index}";
    }
    // Porovnání libovolně dlouhých desetinných indexů bez zaokrouhlení na double.
    static int CompareChapters(Chapter a, Chapter b)
    {
        var ma = LastLabel(a.Title); var mb = LastLabel(b.Title);
        if (ma == null || mb == null) return ma == null ? (mb == null ? 0 : 1) : -1;
        var x = ma.Groups[1].Value.Split('.'); var y = mb.Groups[1].Value.Split('.');
        int c = BigInteger.Parse(x[0]).CompareTo(BigInteger.Parse(y[0]));
        if (c != 0) return c;
        string xf = x.Length > 1 ? x[1] : "", yf = y.Length > 1 ? y[1] : "";
        int n = Math.Max(xf.Length, yf.Length);
        return string.CompareOrdinal(xf.PadRight(n, '0'), yf.PadRight(n, '0'));
    }
    internal static List<Chapter> Feed(byte[] data, string basis)
    {
        using var stream = new MemoryStream(data);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var root = XDocument.Load(reader);
        var result = new List<Chapter>(); var seen = new HashSet<string>();
        foreach (var item in root.Descendants().Where(n => n.Name.LocalName is "item" or "entry"))
        {
            string title = item.Elements().FirstOrDefault(n => n.Name.LocalName == "title")?.Value.Trim() ?? "";
            var link = item.Elements().LastOrDefault(n => n.Name.LocalName == "link" && ((string?)n.Attribute("rel") ?? "alternate") == "alternate");
            string? href = ((string?)link?.Attribute("href") ?? link?.Value)?.Trim();
            if (string.IsNullOrEmpty(href)) continue;
            string url = HttpUrl(href, basis);
            if (seen.Add(url)) result.Add(new Chapter(title.Length > 0 ? title : new Uri(url).Segments.Last(), url));
        }
        if (result.Count == 0) throw new InvalidDataException("Feed neobsahuje zadne kapitoly s odkazem.");
        return result;
    }
    internal static List<string> ImageUrls(string html, string basis)
    {
        var images = Html(html).GetElementbyId("chapter-images")?.Descendants("img") ?? Enumerable.Empty<HtmlNode>();
        var urls = images.Select(n => n.GetAttributeValue("data-src", "") is { Length: > 0 } src ? src : n.GetAttributeValue("src", ""))
            .Where(s => s.Length > 0).Select(s => HttpUrl(WebUtility.HtmlDecode(s), basis)).Distinct().ToList();
        if (urls.Count == 0) throw new InvalidDataException("V #chapter-images nejsou obrazky.");
        return urls;
    }
    internal static void AtomicText(string path, string text)
    {
        string part = path + ".part";
        try { File.WriteAllText(part, text, new UTF8Encoding(false)); File.Move(part, path, true); }
        finally { File.Delete(part); }
    }
    static void ValidateImage(string path)
    {
        // ImageSharp může některé uříznuté JPEG tolerovat; vyžadujeme také koncovou značku.
        using (var stream = File.OpenRead(path))
        {
            if (stream.ReadByte() == 0xff && stream.ReadByte() == 0xd8)
            {
                if (stream.Length < 4) throw new InvalidDataException("Neuplny JPEG.");
                stream.Seek(-2, SeekOrigin.End);
                if (stream.ReadByte() != 0xff || stream.ReadByte() != 0xd9) throw new InvalidDataException("JPEG nema koncovou znacku EOI.");
            }
        }
        using var image = Image.Load(path); // Skutečně dekóduje obsah, nikoli pouze hlavičku.
        if (image.Width < 1 || image.Height < 1) throw new InvalidDataException("Prazdny obrazek.");
    }
    static bool ValidImage(string path)
    {
        try
        {
            string digest = FileHash(path), receipt = Path.ChangeExtension(path, ".verified");
            if (File.Exists(receipt) && File.ReadAllText(receipt) == CheckVersion + "\n" + digest) return true;
            ValidateImage(path); AtomicText(receipt, CheckVersion + "\n" + digest); return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or UnknownImageFormatException or InvalidImageContentException or NotSupportedException) { return false; }
    }
    static async Task<string[]> DownloadImages(Client client, Chapter chapter, List<string> urls, Options o, CancellationToken ct)
    {
        string cache = Path.Combine(o.Output, ".images", CacheName(chapter)); Directory.CreateDirectory(cache);
        var paths = new string[urls.Count]; int completed = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, urls.Count), new ParallelOptions { MaxDegreeOfParallelism = o.Workers, CancellationToken = ct }, async (i, token) =>
        {
            string url = urls[i], path = Path.Combine(cache, $"{i + 1:00000}-{Hash(url)[..12]}.img");
            try
            {
                if (o.Force || !ValidImage(path))
                {
                    string part = Path.ChangeExtension(path, ".part");
                    try
                    {
                        await using (var stream = File.Create(part)) await client.Transfer(url, chapter.Url, stream, token);
                        ValidateImage(part); string digest = FileHash(part);
                        File.Move(part, path, true); AtomicText(Path.ChangeExtension(path, ".verified"), CheckVersion + "\n" + digest);
                    }
                    finally { File.Delete(part); }
                }
                paths[i] = path;
                Log($"{chapter.Title}: hotovo {Interlocked.Increment(ref completed)}/{urls.Count} (obrazek {i + 1})");
            }
            catch (Exception e) when (e is not OperationCanceledException) { throw new IOException($"Obrazek {i + 1}/{urls.Count} ({url}): {e.Message}", e); }
        });
        return paths;
    }
    static async Task<int> Run(Client client, List<Chapter> chapters, Options o, CancellationToken ct)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chapter in chapters)
        {
            string name = PdfName(chapter);
            if (names.TryGetValue(name, out var url) && url != chapter.Url) throw new InvalidDataException($"Dve kapitoly maji stejny vystupni nazev: {name}.pdf");
            names[name] = chapter.Url;
        }
        int failures = 0;
        using var pdfSlots = new SemaphoreSlim(o.PdfWorkers);
        var pending = new List<Task>();
        try
        {
            foreach (var chapter in chapters)
            {
                ct.ThrowIfCancellationRequested();
                pending.RemoveAll(t => t.IsCompleted);
                if (pending.Count >= o.PdfWorkers * 2) await Task.WhenAny(pending);
                string pdf = Path.Combine(o.Output, PdfName(chapter) + ".pdf");
                try
                {
                    if (File.Exists(pdf) && !o.Force && !o.Rebuild && !o.Manifest) { Log($"Preskakuji: {pdf}"); continue; }
                    var urls = ImageUrls(await client.ChapterHtml(chapter.Url, ct), chapter.Url);
                    string manifest = Path.ChangeExtension(pdf, ".pages.sha256");
                    string signature = Hash(PdfVersion + $"\njpeg_quality={o.JpegQuality}\n" + chapter.Title + "\n" + string.Join("\n", urls));
                    if (File.Exists(pdf) && o.Manifest && File.Exists(manifest) && File.ReadAllText(manifest) == signature && !o.Force && !o.Rebuild)
                    { Log($"Preskakuji: {pdf}"); continue; }
                    var paths = await DownloadImages(client, chapter, urls, o, ct);
                    pending.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await pdfSlots.WaitAsync(ct);
                            try
                            {
                                PdfWriter.Make(paths, pdf, chapter.Title, o.JpegQuality, ct);
                                if (o.Manifest) AtomicText(manifest, signature); else File.Delete(manifest);
                                string legacy = Path.Combine(o.Output, CacheName(chapter) + ".pdf");
                                if (!string.Equals(legacy, pdf, StringComparison.OrdinalIgnoreCase))
                                { File.Delete(legacy); File.Delete(Path.ChangeExtension(legacy, ".pages.sha256")); }
                                Log($"Hotovo: {pdf}");
                            }
                            finally { pdfSlots.Release(); }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                        catch (Exception e) { Interlocked.Increment(ref failures); Log($"Tvorba PDF {chapter.Title} selhala: {e.Message}"); }
                    }));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception e) { Interlocked.Increment(ref failures); Log($"Kapitola {chapter.Title} selhala: {e.Message}"); }
            }
        }
        finally { await Task.WhenAll(pending); }
        ct.ThrowIfCancellationRequested();
        return failures > 0 ? 1 : 0;
    }
    public static async Task<int> MainAsync(string[] args)
    {
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var o = Options.Parse(args);
            if (o.Help)
            {
                Console.WriteLine("""
                    Manga Leech C# (.NET 10) — RSS/Atom nebo serie -> jedno PDF na vydani.
                    dotnet run --project csharp -- URL [volby]
                    dotnet run --project csharp -- --chapter URL [volby]
                      -o, --output DIR      Vystupni slozka (downloads)
                      --list                Pouze vypsat vydani
                      --limit N             Prvnich N vydani po serazeni
                      --oldest-first        Od nejnizsiho cisla (vychozi)
                      --newest-first        Od nejvyssiho cisla
                      --workers N           Soubezne prenosy obrazku (4)
                      --pdf-workers N       Soubezne PDF ulohy ve vlaknech (1)
                      --timeout SECONDS     Limit celeho pokusu o prenos (60)
                      --delay SECONDS       Pauza pred kazdym pozadavkem (0.5)
                      --user-agent VALUE    Vlastni HTTP User-Agent
                      --force               Znovu stahnout i hotove obrazky a PDF
                      --rebuild             Znovu vytvorit PDF z overene cache
                      --write-manifest      Kontrolovat otisk URL, metadat a generatoru
                      --jpeg-quality 1-95   Volitelna ztratova komprese RGB/gray; JPEG se nemeni
                    --pdf-tasks-per-child neni podporovano: PDF vznikaji ve vlaknech.
                    """);
                return 0;
            }
            using var client = new Client(o);
            List<Chapter> chapters;
            if (o.Chapter != null)
            {
                string url = HttpUrl(o.Chapter), html = await client.GetText(url, null, cancel.Token);
                client.Prefetched[url] = html;
                string title = Text(Html(html).DocumentNode.SelectSingleNode("//title"));
                var parts = title.Split(" | ").Select(p => p.Trim()).ToArray();
                if (parts.Length >= 3 && Label.Match(parts[0]) is { Success: true } m && m.Length == parts[0].Length) title = parts[1] + " " + parts[0];
                else if (title.EndsWith(" | Weeb Central")) title = title[..^15].Trim();
                if (title.Length == 0) throw new InvalidDataException("Nelze zjistit nazev kapitoly pro metadata PDF.");
                chapters = [new Chapter(title, url)];
            }
            else
            {
                string url = HttpUrl(o.Source!); string path = new Uri(url).AbsolutePath.TrimEnd('/');
                chapters = path.StartsWith("/series/") && !path.EndsWith("/rss")
                    ? await client.Series(url, cancel.Token) : Feed(await client.Get(url, null, cancel.Token), url);
                // OrderBy je stabilní i pro vydání bez rozpoznatelného čísla.
                var comparer = Comparer<Chapter>.Create((a, b) => o.Newest ? CompareChapters(b, a) : CompareChapters(a, b));
                chapters = chapters.OrderBy(ch => ch, comparer).ToList();
                Log($"Zdroj obsahuje {chapters.Count} vydani.");
            }
            if (o.Limit.HasValue) chapters = chapters.Take(o.Limit.Value).ToList();
            if (o.List) { foreach (var ch in chapters) Console.WriteLine($"{ch.Title}\t{ch.Url}"); return 0; }
            Directory.CreateDirectory(o.Output);
            return await Run(client, chapters, o, cancel.Token);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { Log("Preruseno. Dalsi spusteni navaze na cache."); return 130; }
        catch (Exception e) { Log($"Chyba: {e.Message}"); return 1; }
        finally { Console.CancelKeyPress -= handler; }
    }
}

sealed class Client : IDisposable
{
    readonly HttpClient http;
    readonly Options options;
    readonly ConcurrentDictionary<string, DateTimeOffset> cooldowns = new();
    public readonly Dictionary<string, string> Prefetched = new();
    public Client(Options options)
    {
        this.options = options;
        http = new HttpClient(new SocketsHttpHandler { CookieContainer = new CookieContainer(), UseCookies = true, AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None, MaxConnectionsPerServer = options.Workers + 1,
            ConnectTimeout = TimeSpan.FromSeconds(Math.Min(10, options.Timeout)) }) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", options.UserAgent);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "identity");
    }
    public void Dispose() => http.Dispose();
    static async Task Pause(double seconds, CancellationToken ct)
    {
        // Task.Delay má omezený rozsah; dlouhá uživatelská pauza zůstává přerušitelná.
        while (seconds > 0) { double step = Math.Min(seconds, 60); await Task.Delay(TimeSpan.FromSeconds(step), ct); seconds -= step; }
    }
    public async Task Transfer(string url, string? referer, Stream target, CancellationToken ct)
    {
        string host = new Uri(MangaLeech.HttpUrl(url)).Authority;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await Pause(options.Delay, ct);
            while (cooldowns.TryGetValue(host, out var until) && until > DateTimeOffset.UtcNow)
                await Pause(Math.Min(1, (until - DateTimeOffset.UtcNow).TotalSeconds), ct);
            target.Position = 0; target.SetLength(0);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.Timeout));
            double backoff = Math.Pow(2, attempt + 1);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (referer != null) request.Headers.Referrer = new Uri(referer);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    int status = (int)response.StatusCode;
                    if (attempt == 2 || status is not (429 or 500 or 502 or 503 or 504)) response.EnsureSuccessStatusCode();
                    double pause = response.Headers.RetryAfter?.Delta?.TotalSeconds ??
                        (response.Headers.RetryAfter?.Date is { } date ? (date - DateTimeOffset.UtcNow).TotalSeconds : backoff);
                    pause = double.IsFinite(pause) ? Math.Max(0, pause) : backoff;
                    if (pause > 120) throw new InvalidDataException($"HTTP {status}: Retry-After {pause:F0} s presahuje limit 120 s.");
                    if (status is 429 or 503) cooldowns.AddOrUpdate(host, DateTimeOffset.UtcNow.AddSeconds(pause), (_, old) => old > DateTimeOffset.UtcNow.AddSeconds(pause) ? old : DateTimeOffset.UtcNow.AddSeconds(pause));
                    MangaLeech.Log($"HTTP {status}, pokus {attempt + 1}/3: {url}; opakuji za {pause:F1} s");
                    response.Dispose(); await Pause(pause, ct); continue;
                }
                await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
                await source.CopyToAsync(target, timeout.Token);
                if (response.Content.Headers.ContentLength is long expected && target.Position != expected)
                    throw new IOException("Neuplna odpoved: nesouhlasi Content-Length.");
                return;
            }
            catch (Exception e) when ((e is HttpRequestException h && h.StatusCode == null) || e is IOException || (e is OperationCanceledException && !ct.IsCancellationRequested))
            {
                if (attempt == 2) throw new IOException($"Prenos selhal ({url}): {e.Message}", e);
                MangaLeech.Log($"Chyba prenosu {url}, pokus {attempt + 1}/3: {e.Message}");
                await Pause(backoff, ct);
            }
        }
        throw new IOException("Stazeni selhalo.");
    }
    public async Task<byte[]> Get(string url, string? referer, CancellationToken ct)
    { using var stream = new MemoryStream(); await Transfer(url, referer, stream, ct); return stream.ToArray(); }
    public async Task<string> GetText(string url, string? referer, CancellationToken ct) => Encoding.UTF8.GetString(await Get(url, referer, ct)).TrimStart('\ufeff');
    public async Task<string> ChapterHtml(string url, CancellationToken ct)
    {
        string html = Prefetched.Remove(url, out var cached) ? cached : await GetText(url, null, ct);
        if (MangaLeech.Html(html).GetElementbyId("chapter-images")?.Descendants("img").Any() == true) return html;
        var match = Regex.Match(html, "[\"']([^\"']+/images\\?is_prev=False)[\"']");
        return match.Success ? await GetText(MangaLeech.SameHost(WebUtility.HtmlDecode(match.Groups[1].Value), url) + "&reading_style=long_strip&current_page=1", url, ct) : html;
    }
    public async Task<List<Chapter>> Series(string url, CancellationToken ct)
    {
        var doc = MangaLeech.Html(await GetText(url, null, ct));
        string name = MangaLeech.Text(doc.DocumentNode.SelectSingleNode("//title")).Split(" | ")[0].Trim();
        var endpoint = doc.DocumentNode.Descendants().Select(n => n.GetAttributeValue("hx-get", ""))
            .FirstOrDefault(s => s.Length > 0 && new Uri(new Uri(url), WebUtility.HtmlDecode(s)).AbsolutePath.EndsWith("/full-chapter-list"));
        HtmlNode root;
        if (endpoint != null) root = MangaLeech.Html(await GetText(MangaLeech.SameHost(WebUtility.HtmlDecode(endpoint), url), url, ct)).DocumentNode;
        else root = doc.GetElementbyId("chapter-list") ?? throw new InvalidDataException("Chybi uplny seznam kapitol i #chapter-list.");
        var result = new List<Chapter>(); var seen = new HashSet<string>();
        foreach (var a in root.Descendants("a"))
        {
            string href = WebUtility.HtmlDecode(a.GetAttributeValue("href", ""));
            if (href.Length == 0) continue;
            string resolved = MangaLeech.HttpUrl(href, url);
            if (!Regex.IsMatch(new Uri(resolved).AbsolutePath, @"^/chapters/[^/]+$")) continue;
            var label = MangaLeech.Label.Match(Regex.Replace(MangaLeech.Text(a), @"\s+", " "));
            if (!label.Success) continue;
            string link = MangaLeech.SameHost(resolved, url);
            if (seen.Add(link)) result.Add(new Chapter($"{name} {label.Value}".Trim(), link));
        }
        if (result.Count == 0) throw new InvalidDataException("Uplny seznam serie neobsahuje zadne kapitoly.");
        return result;
    }
}

// Malý PDF writer zapisuje binární obrazové streamy přímo na disk. JPEG zůstává
// beze změny, neprokládané PNG bez průhlednosti zachovává IDAT, paletu i 16 bitů.
// Zbylé formáty převede ImageSharp na lossless RGB + samostatnou alpha masku.
// Tato náhradní větev používá 8 bitů na kanál; 16bitové PNG s alpha/tRNS
// proto na rozdíl od přímé PNG větve nezachová původní bitovou hloubku.
sealed class PdfWriter : IDisposable
{
    readonly FileStream output;
    readonly List<long> offsets = [0];
    PdfWriter(string path) { output = File.Create(path); Write("%PDF-1.7\n%binary\n"); }
    public void Dispose() => output.Dispose();
    int Reserve() { offsets.Add(0); return offsets.Count - 1; }
    void Write(string text) => output.Write(Encoding.ASCII.GetBytes(text));
    void Object(int id, string value) { offsets[id] = output.Position; Write($"{id} 0 obj\n{value}\nendobj\n"); }
    int Stream(string dictionary, byte[] bytes)
    {
        int id = Reserve(); offsets[id] = output.Position;
        Write($"{id} 0 obj\n<< {dictionary} /Length {bytes.Length} >>\nstream\n"); output.Write(bytes); Write("\nendstream\nendobj\n"); return id;
    }
    static byte[] Compress(byte[] data)
    { using var stream = new MemoryStream(); using (var z = new ZLibStream(stream, CompressionLevel.Optimal, true)) z.Write(data); return stream.ToArray(); }
    record Embedded(int Id, int Width, int Height);
    Embedded Embed(string path, int? quality)
    {
        byte[] data = File.ReadAllBytes(path);
        var info = Image.Identify(data) ?? throw new InvalidDataException("Neznamy obrazek.");
        int w = info.Width, h = info.Height;
        string common = $"/Type /XObject /Subtype /Image /Width {w} /Height {h}";
        if (data.Length > 2 && data[0] == 0xff && data[1] == 0xd8)
        {
            // Značka SOF udává původní počet komponent; CMYK Adobe je invertovaný.
            int components = 0; bool adobe = false;
            for (int p = 2; p + 3 < data.Length;)
            {
                if (data[p++] != 0xff) continue;
                while (p < data.Length && data[p] == 0xff) p++;
                if (p >= data.Length) break;
                byte marker = data[p++];
                if (marker is 0xda or 0xd9) break;
                if (marker is 0x01 or >= 0xd0 and <= 0xd8) continue;
                if (p + 2 > data.Length) break;
                int len = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p, 2));
                if (len < 2 || p + len > data.Length) break;
                if (marker == 0xee && len >= 7 && Encoding.ASCII.GetString(data, p + 2, 5) == "Adobe") adobe = true;
                if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc) && len >= 8) components = data[p + 7];
                p += len;
            }
            string cs = components switch { 1 => "/DeviceGray", 3 => "/DeviceRGB", 4 => "/DeviceCMYK", _ => throw new InvalidDataException("Nepodporovany JPEG colorspace.") };
            string decode = components == 4 && adobe ? "/Decode [1 0 1 0 1 0 1 0]" : "";
            return new(Stream($"{common} /BitsPerComponent 8 /ColorSpace {cs} /Filter /DCTDecode {decode}", data), w, h);
        }
        // Volitelný JPEG se použije jen tehdy, když je menší než lossless stream.
        byte[]? jpeg = null;
        bool png = data.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        int color = -1, bits = 8, interlace = 1; bool transparent = false; byte[]? palette = null;
        using var idat = new MemoryStream();
        if (png)
        {
            for (int p = 8; p + 12 <= data.Length;)
            {
                uint raw = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p, 4));
                if (raw > int.MaxValue || raw > data.Length - p - 12) throw new InvalidDataException("Neuplny PNG chunk.");
                int len = (int)raw; string kind = Encoding.ASCII.GetString(data, p + 4, 4); int start = p + 8;
                if (kind == "IHDR" && len == 13) { bits = data[start + 8]; color = data[start + 9]; interlace = data[start + 12]; }
                if (kind == "PLTE") palette = data.AsSpan(start, len).ToArray();
                if (kind == "tRNS") transparent = true;
                if (kind == "IDAT") idat.Write(data, start, len);
                p += len + 12;
                if (kind == "IEND") break;
            }
        }
        if (quality.HasValue && (!png || (color is 0 or 2 && bits == 8 && !transparent)))
        {
            using var image = Image.Load<Rgba32>(data);
            bool alpha = false;
            image.ProcessPixelRows(rows => { for (int y = 0; y < rows.Height && !alpha; y++) foreach (var pixel in rows.GetRowSpan(y)) if (pixel.A != 255) { alpha = true; break; } });
            if (!alpha) { using var encoded = new MemoryStream(); image.Save(encoded, new JpegEncoder { Quality = quality.Value }); jpeg = encoded.ToArray(); }
        }
        if (png && !transparent && interlace == 0 && color is 0 or 2 or 3 && idat.Length > 0)
        {
            string cs = color == 2 ? "/DeviceRGB" : "/DeviceGray";
            if (color == 3)
            {
                if (palette == null || palette.Length == 0) throw new InvalidDataException("PNG nema paletu.");
                cs = $"[/Indexed /DeviceRGB {palette.Length / 3 - 1} <{Convert.ToHexString(palette)}>]";
            }
            byte[] compressed = idat.ToArray();
            if (jpeg != null && jpeg.Length < compressed.Length) return new(Stream($"{common} /BitsPerComponent 8 /ColorSpace /DeviceRGB /Filter /DCTDecode", jpeg), w, h);
            return new(Stream($"{common} /BitsPerComponent {bits} /ColorSpace {cs} /Filter /FlateDecode /DecodeParms << /Predictor 15 /Colors {(color == 2 ? 3 : 1)} /BitsPerComponent {bits} /Columns {w} >>", compressed), w, h);
        }
        using var rgba = Image.Load<Rgba32>(data);
        byte[] rgb = new byte[checked(w * h * 3)], alphaBytes = new byte[checked(w * h)]; bool hasAlpha = false;
        rgba.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < h; y++)
            {
                var row = rows.GetRowSpan(y);
                for (int x = 0; x < w; x++)
                {
                    int p = y * w + x; var pixel = row[x];
                    rgb[p * 3] = pixel.R; rgb[p * 3 + 1] = pixel.G; rgb[p * 3 + 2] = pixel.B;
                    alphaBytes[p] = pixel.A; hasAlpha |= pixel.A != 255;
                }
            }
        });
        byte[] lossless = Compress(rgb);
        if (jpeg != null && !hasAlpha && jpeg.Length < lossless.Length) return new(Stream($"{common} /BitsPerComponent 8 /ColorSpace /DeviceRGB /Filter /DCTDecode", jpeg), w, h);
        string mask = hasAlpha ? $"/SMask {Stream($"{common} /BitsPerComponent 8 /ColorSpace /DeviceGray /Filter /FlateDecode", Compress(alphaBytes))} 0 R" : "";
        return new(Stream($"{common} /BitsPerComponent 8 /ColorSpace /DeviceRGB /Filter /FlateDecode {mask}", lossless), w, h);
    }
    internal static void Make(string[] paths, string path, string title, int? quality, CancellationToken ct)
    {
        string part = path + ".part";
        try
        {
            using (var pdf = new PdfWriter(part))
            {
                int catalog = pdf.Reserve(), pages = pdf.Reserve(), info = pdf.Reserve();
                var (series, number) = MangaLeech.Metadata(title);
                XNamespace x = "adobe:ns:meta/", rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#", dc = "http://purl.org/dc/elements/1.1/",
                    calibre = "http://calibre-ebook.com/xmp-namespace", index = "http://calibre-ebook.com/xmp-namespace-series-index";
                var desc = new XElement(rdf + "Description", new XAttribute(rdf + "about", ""),
                    new XElement(dc + "title", new XElement(rdf + "Alt", new XElement(rdf + "li", new XAttribute(XNamespace.Xml + "lang", "x-default"), series))));
                if (number != null) desc.Add(new XElement(calibre + "series", series), new XElement(index + "series_index", number));
                var xml = new XElement(x + "xmpmeta", new XAttribute(XNamespace.Xmlns + "x", x), new XAttribute(XNamespace.Xmlns + "rdf", rdf),
                    new XAttribute(XNamespace.Xmlns + "dc", dc), new XAttribute(XNamespace.Xmlns + "calibre", calibre), new XAttribute(XNamespace.Xmlns + "calibreSI", index), new XElement(rdf + "RDF", desc));
                int metadata = pdf.Stream("/Type /Metadata /Subtype /XML", Encoding.UTF8.GetBytes(xml.ToString()));
                pdf.Object(info, $"<< /Title <FEFF{Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(series))}> /Creator (Manga Leech CSharp) >>");
                var kids = new List<int>(); var images = new Dictionary<string, Embedded>();
                foreach (string imagePath in paths)
                {
                    ct.ThrowIfCancellationRequested();
                    string digest = MangaLeech.FileHash(imagePath);
                    if (!images.TryGetValue(digest, out var image)) { image = pdf.Embed(imagePath, quality); images[digest] = image; }
                    double scale = Math.Min(.75, 14000.0 / Math.Max(image.Width, image.Height));
                    string w = (image.Width * scale).ToString("0.########", CultureInfo.InvariantCulture), h = (image.Height * scale).ToString("0.########", CultureInfo.InvariantCulture);
                    int content = pdf.Stream("", Encoding.ASCII.GetBytes($"q {w} 0 0 {h} 0 0 cm /Im0 Do Q\n"));
                    int page = pdf.Reserve(); kids.Add(page);
                    pdf.Object(page, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 {w} {h}] /Resources << /XObject << /Im0 {image.Id} 0 R >> >> /Contents {content} 0 R >>");
                }
                if (kids.Count == 0) throw new InvalidDataException("Nelze vytvorit PDF bez stran.");
                pdf.Object(pages, $"<< /Type /Pages /Count {kids.Count} /Kids [{string.Join(" ", kids.Select(id => $"{id} 0 R"))}] >>");
                pdf.Object(catalog, $"<< /Type /Catalog /Pages {pages} 0 R /Metadata {metadata} 0 R >>");
                long xref = pdf.output.Position;
                pdf.Write($"xref\n0 {pdf.offsets.Count}\n0000000000 65535 f \n");
                foreach (long offset in pdf.offsets.Skip(1)) pdf.Write(offset.ToString("0000000000", CultureInfo.InvariantCulture) + " 00000 n \n");
                pdf.Write($"trailer\n<< /Size {pdf.offsets.Count} /Root {catalog} 0 R /Info {info} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            }
            ct.ThrowIfCancellationRequested(); File.Move(part, path, true);
        }
        finally { File.Delete(part); }
    }
}
