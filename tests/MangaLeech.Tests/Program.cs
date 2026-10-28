using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Xml.Linq;
using MangaLeech.Core;
using SkiaSharp;
using static MangaLeech.Core.ChapterParser;

if (args.Length >= 2 && args[0] == "--benchmark")
    return Benchmark.Run(args[1..]);

var tests = new (string Name, Func<Task> Run)[]
{
    ("CLI defaults and validation", TestOptions),
    ("RSS/Atom, image selection, ordering and metadata", TestParsing),
    ("Site selection and MangaFire languages, releases, decimal chapters and reader pages", TestMangaFire),
    ("MangaFire signed HTTP discovery, paging, concurrency and metadata prefetch", TestMangaFireHttp),
    ("Native MangaFire signer reference vectors, UTF-8 and query ordering", TestMangaFireSigner),
    ("PDF streams, transparency, page order, XMP and xref", TestPdf),
    ("Strict image validation and cache repair", TestValidation),
    ("PNG palette and bit depth preservation", TestPng),
    ("Large images and shared memory budget", TestLargeImages),
    ("Memory budget waits atomically and cancels", TestMemoryBudget),
    ("Parallel chapters, global image limit, manifests and rebuild", TestPipeline),
    ("Slow image retries do not block later chapters", TestSlowTail),
    ("Sequential chapters and successful cache cleanup", TestSequential),
    ("Chapter failures preserve successful output", TestFailure),
    ("PDF failure retains image cache", TestPdfFailure),
    ("Output collisions fail before downloads", TestCollision),
    ("Cancellation drains workers and removes partial files", TestCancellation),
    ("Retry-After retry and maximum wait", TestRetries),
    ("Series discovery and direct chapter CLI", TestApplication)
};
if (args.Length == 2 && args[0] == "--published")
{
    string executable = Path.GetFullPath(args[1]);
    tests = [.. tests, ("Published executable with native image decoder", () => TestPublished(executable))];
}
int failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception}");
    }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
return failures == 0 ? 0 : 1;

static void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}

static byte[] ImageBytes(SKEncodedImageFormat format = SKEncodedImageFormat.Png, bool alpha = false, int width = 8, int height = 16)
{
    using var bitmap = new SKBitmap(width, height);
    bitmap.Erase(alpha ? new SKColor(20, 80, 120, 100) : new SKColor(20, 80, 120));
    using var data = bitmap.Encode(format, 90);
    return data.ToArray();
}

static Options Settings(string output, params string[] arguments) =>
    Options.Parse(["https://example.test/rss", "-o", output, "--delay", "0", .. arguments]);

static List<Chapter> Chapters(LocalServer server, int count = 6) =>
    Enumerable.Range(1, count).Select(i => new Chapter($"Test Series Chapter {i}", $"{server.Url}/chapters/{i}")).ToList();

static Task TestOptions()
{
    var options = Options.Parse(["https://example.test/rss"]);
    Check(options.Workers == 8 && options.PdfWorkers == 2 && options.ChapterWorkers == 2 && options.MemoryLimitMiB == 512, "Incorrect defaults.");
    foreach (string argument in new[] { "--workers", "--pdf-workers", "--chapter-workers", "--limit", "--memory-limit-mib" })
    {
        Throws<ArgumentException>(() => Options.Parse(["https://example.test/rss", argument, "0"]));
    }
    Throws<ArgumentException>(() => Options.Parse(["https://example.test/rss", "--timeout", "NaN"]));
    Throws<ArgumentException>(() => Options.Parse(["https://example.test/rss", "--timeout", "5000000"]));
    Throws<ArgumentException>(() => Options.Parse(["https://example.test/rss", "--jpeg-quality", "96"]));
    Throws<ArgumentException>(() => Options.Parse(["https://example.test/rss", "--memory-limit-mib", "32769"]));
    Throws<ArgumentException>(() => Options.Parse(["https://example.test/rss", "--chapter", "https://example.test/chapter"]));
    Throws<ArgumentException>(() => Options.Parse(["https://example.test/rss", "--oldest-first", "--newest-first"]));
    return Task.CompletedTask;
}

static Task TestParsing()
{
    byte[] feed = Encoding.UTF8.GetBytes("<rss><channel><item><title>Series Chapter 10</title><link>/chapters/10</link></item><item><title>Series Chapter 2.5</title><link>/chapters/2</link></item></channel></rss>");
    var chapters = Feed(feed, "https://example.test/rss");
    Check(CompareChapters(chapters[0], chapters[1]) > 0, "Chapter sorting is not numeric.");
    Check(PdfName(chapters[1]) == "Series - Ch 002.5", "Decimal filename is wrong.");
    Check(Metadata("Series Chapter 2.5") == ("Series", "2.5"), "Incorrect metadata.");
    var atom = Feed(Encoding.UTF8.GetBytes("<feed xmlns='http://www.w3.org/2005/Atom'><entry><title>A Chapter 1</title><link rel='alternate' href='/chapters/1'/></entry></feed>"), "https://example.test/rss");
    Check(atom.Count == 1 && atom[0].Url.EndsWith("/chapters/1"), "Atom parsing failed.");
    var images = ImageUrls("<img src='/ad'/><div id='chapter-images'><img src='/1'/><img data-src='/2' src='/placeholder'/></div>", "https://example.test/chapter");
    Check(images.SequenceEqual(new[] { "https://example.test/1", "https://example.test/2" }), "Wrong image selection/order.");
    return Task.CompletedTask;
}

static async Task TestMangaFire()
{
    const string url = "https://mangafire.to/title/52l90-domination";
    var options = Options.Parse([url]);
    Check(options.Language == "en", "MangaFire should default to English.");
    Check(Options.Parse([url, "--language", "PT-BR"]).Language == "pt-br", "Regional language code rejected.");
    Throws<ArgumentException>(() => Options.Parse([url, "--language", "../en"]));
    await using var site = new MangaFireSite(options);
    await using var registered = SiteRegistry.For(url, options);
    await using var other = SiteRegistry.For("https://example.test/rss", options);
    Check(registered is MangaFireSite && other is WeebCentralSite, "Wrong site selected.");
    await using var lookalike = SiteRegistry.For("https://mangafire.to.example.test/title/test", options);
    Check(lookalike is WeebCentralSite, "MangaFire host match is too broad.");
    Check(MangaFireSite.ParseUrl(url) == ("52l90-domination", null), "Title URL failed.");
    Check(MangaFireSite.ParseUrl(url + "/chapter/123/") == ("52l90-domination", "123"), "Chapter URL failed.");
    Throws<ArgumentException>(() => MangaFireSite.ParseUrl(url + "/volume/123"));
    using var first = JsonDocument.Parse("""
        {"items":[
          {"id":10,"number":2.5,"language":"en","type":"unofficial"},
          {"id":20,"number":2.5,"language":"fr","type":"official"},
          {"id":11,"number":1,"language":"en","type":"unofficial"}]}
        """);
    using var second = JsonDocument.Parse("""
        {"items":[
          {"id":30,"number":"2.50","language":"en","type":"official"},
          {"id":31,"number":2.5,"language":"en","type":"unofficial"},
          {"id":11,"number":1,"language":"en","type":"unofficial"}]}
        """);
    var selected = new Dictionary<string, (Chapter Chapter, bool Official)>();
    site.AddChapters(first.RootElement, "Domination", url, selected);
    site.AddChapters(second.RootElement, "Domination", url, selected);
    Check(selected.Count == 2 && selected["2.5"].Chapter.Url == url + "/chapter/30", "Language, cross-page deduplication or official preference failed.");
    Check(PdfName(selected["2.5"].Chapter) == "Domination - Ch 002.5", "Decimal MangaFire filename failed.");
    using var reader = JsonDocument.Parse("""
        {"data":{"title":{"name":"Domination"},"number":2.5,"pages":[
          {"url":"/second.png"},{"url":"https://cdn.example.test/first.png"},{"url":"/second.png"}]}}
        """);
    var (chapter, images) = MangaFireSite.ParseChapter(reader.RootElement, url + "/chapter/30");
    Check(chapter.Title == "Domination Chapter 2.5" && images.SequenceEqual(new[] {
        "https://mangafire.to/second.png", "https://cdn.example.test/first.png", "https://mangafire.to/second.png"
    }), "MangaFire reader order or repeated pages changed.");
    using var empty = JsonDocument.Parse("""{"data":{"title":{"name":"Domination"},"number":1,"pages":[]}}""");
    Throws<InvalidDataException>(() => MangaFireSite.ParseChapter(empty.RootElement, url));
    using var unsafeReader = JsonDocument.Parse("""{"data":{"title":{"name":"Domination"},"number":1,"pages":[{"url":"file:///etc/passwd"}]}}""");
    Throws<ArgumentException>(() => MangaFireSite.ParseChapter(unsafeReader.RootElement, url));
}

static async Task TestMangaFireHttp()
{
    using var scratch = new Scratch();
    await using var server = new LocalServer(ImageBytes());
    var options = Settings(scratch.Path);
    using var client = new Client(options);
    await using var site = new MangaFireSite(options);
    string url = server.Url + "/title/fixture-series";
    var chapters = await site.GetChapters(client, url, CancellationToken.None);
    Check(server.Connections == 1, "Sequential API requests did not reuse their HTTP connection.");
    Check(chapters.Count == 2 && chapters[0].Url.EndsWith("/chapter/2"), "HTTP paging or official release selection failed.");
    Check(chapters[1].Title == "HTTP Series Chapter 2.5", "HTTP metadata failed.");
    var direct = await site.GetChapter(client, chapters[0].Url, CancellationToken.None);
    int requests = server.Hits.Values.Sum();
    Check((await site.GetImages(client, direct.Url, CancellationToken.None)).Count == 1, "Direct chapter images were not prefetched.");
    Check(server.Hits.Values.Sum() == requests, "Prefetch repeated the chapter API request.");
    var results = await Task.WhenAll(chapters.Select(chapter => site.GetImages(client, chapter.Url, CancellationToken.None)));
    Check(results.All(images => images.Single() == server.Url + "/image/1/1"), "Concurrent signing failed.");
    Check(server.Hits.Keys.All(path => path.StartsWith("/api/")), "Native signing fetched HTML or JavaScript.");
    var oldestOptions = Settings(scratch.Path, "--limit", "1");
    using var oldestClient = new Client(oldestOptions);
    await using var oldestSite = new MangaFireSite(oldestOptions);
    var oldest = await oldestSite.GetChapters(oldestClient, server.Url + "/title/limit-demo", CancellationToken.None);
    Check(oldest.Count == 2 && oldest.Any(ch => ch.Title.EndsWith("Chapter 1")), "Ascending limit boundary failed.");
    var newestOptions = Settings(scratch.Path, "--limit", "1", "--newest-first");
    using var newestClient = new Client(newestOptions);
    await using var newestSite = new MangaFireSite(newestOptions);
    var newest = await newestSite.GetChapters(newestClient, server.Url + "/title/limit-demo", CancellationToken.None);
    Check(newest.Count == 2 && newest.Any(ch => ch.Title.EndsWith("Chapter 4")), "Descending limit boundary failed.");
    Check(!server.Hits.Keys.Any(path => path.StartsWith("/api/titles/limit/chapters") && path.Contains("page=2")), "Limit still fetched unneeded pages.");
}

static Task TestMangaFireSigner()
{
    // These outputs were recorded from the original website module, independently
    // of the C# implementation. See the source module hash in MangaFireSigner.
    var vectors = new (string Input, string Expected)[]
    {
        ("", ""),
        ("A", "3A"),
        ("AAAA", "3A3ibg"),
        ("/titles/52l90", "8sK3xtqdFdtKnGAytg"),
        ("/chapters/6249894", "8vPRXa1JjvVTxuDiS4eXfTo"),
        ("\u017elu\u0165ou\u010dk\u00fd \ud83d\ude00", "a8dIQZvOxUmaR-_0yLitE-oc"),
        ("\u65e5\u672c\u8a9e", "6SmDqDRPSHaO"),
        ("a&b = c", "z5YsefP3Mw"),
        ("\u0000\u00ff", "48PA"),
    };
    foreach (var (input, expected) in vectors)
        Check(MangaFireSigner.Encode(input) == expected, "Native signature mismatch for " + JsonSerializer.Serialize(input));
    using var digest = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
    for (int k = 0; k < 1000; k++)
    {
        string input = k < 9 ? vectors[k].Input : k == 9 ? new string('x', 1024)
            : new string(Enumerable.Range(0, k % 150).Select(i => (char)(32 + (k * 13 + i * 53) % 90)).ToArray());
        digest.AppendData(Encoding.UTF8.GetBytes(MangaFireSigner.Encode(input) + "\n"));
    }
    Check(Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant() == "f39ec1c20940ada08af5391b258e72728c7ba2f18f002b80e04158254ef377ee",
        "Native signer differs from the 1000-input JavaScript reference corpus.");
    var query = new Dictionary<string, string> { ["b"] = "space here", ["a"] = "x+y&z", ["vrf"] = "ignored" };
    Check(MangaFireSigner.Sign("/titles/52l90/chapters", query) == "8sK3xtqdFdtKnGAytlnP4U-wU_buolkNPe0tmRKhzXWaSWjpFejysi5u7Q",
        "Query values must be signed in sorted, decoded form and must exclude vrf.");
    Check(MangaFireSigner.Sign("/titles/52l90/chapters", new Dictionary<string, string> { ["z"] = "~!*()'_", ["a"] = "žluťoučký 😀" }) == "8sK3xtqdFdtKnGAytlnP4U-wU_buolkNPZI3fu0jAy0l827AtmbiIMdoFxtzZrlHuxoNF_s",
        "Unicode query signing failed.");
    return Task.CompletedTask;
}

static Task TestPdf()
{
    using var scratch = new Scratch();
    byte[][] bytes = [ImageBytes(SKEncodedImageFormat.Jpeg), ImageBytes(alpha: true, width: 4, height: 12), ImageBytes(SKEncodedImageFormat.Webp)];
    string[] paths = bytes.Select((data, index) =>
    {
        string path = Path.Combine(scratch.Path, $"{index}.img");
        File.WriteAllBytes(path, data);
        return path;
    }).ToArray();
    string pdf = Path.Combine(scratch.Path, "result.pdf");
    PdfWriter.Make([paths[0], paths[1], paths[2], paths[0]], pdf, "Test & Series Chapter 2.5", 85, CancellationToken.None);
    byte[] output = File.ReadAllBytes(pdf);
    string text = Encoding.Latin1.GetString(output);
    Check(output.AsSpan().IndexOf(bytes[0]) >= 0, "Original JPEG bytes were changed.");
    Check(Regex.Matches(text, "/Filter /DCTDecode").Count >= 1, "Missing JPEG stream.");
    Check(text.Contains("/SMask") && text.Contains("/Count 4"), "Transparency or duplicate pages lost.");
    var rgbStream = Regex.Match(text, @"/BitsPerComponent 8 /ColorSpace /DeviceRGB /Filter /FlateDecode(?: /SMask \d+ 0 R)? /Length (\d+) >>\nstream\n");
    Check(rgbStream.Success, "Transparent image RGB stream is missing.");
    using (var compressed = new MemoryStream(output, rgbStream.Index + rgbStream.Length, int.Parse(rgbStream.Groups[1].Value)))
    using (var inflater = new ZLibStream(compressed, CompressionMode.Decompress))
    using (var original = ImageCache.DecodeImage(paths[1]))
    {
        Span<byte> pixel = stackalloc byte[3];
        inflater.ReadExactly(pixel);
        SKColor expected = original.GetPixel(0, 0);
        Check(pixel[0] == expected.Red && pixel[1] == expected.Green && pixel[2] == expected.Blue, "Decoded RGB channels changed in the PDF.");
    }
    var boxes = Regex.Matches(text, @"/MediaBox \[0 0 ([\d.]+) ([\d.]+)\]");
    Check(boxes.Count == 4 && boxes[1].Groups[1].Value == "3" && boxes[1].Groups[2].Value == "9", "Page order or aspect ratio changed.");
    int xmlStart = text.IndexOf("<x:xmpmeta", StringComparison.Ordinal);
    int xmlEnd = text.IndexOf("</x:xmpmeta>", xmlStart, StringComparison.Ordinal) + "</x:xmpmeta>".Length;
    var xml = XDocument.Parse(text[xmlStart..xmlEnd]);
    Check(xml.Descendants().Any(e => e.Name.LocalName == "series" && e.Value == "Test & Series"), "Missing Kavita series metadata.");
    Check(xml.Descendants().Any(e => e.Name.LocalName == "series_index" && e.Value == "2.5"), "Missing series index.");
    foreach (Match entry in Regex.Matches(text, @"(?m)^(\d{10}) 00000 n"))
    {
        int offset = int.Parse(entry.Groups[1].Value);
        Check(Regex.IsMatch(text[offset..], @"^\d+ 0 obj"), "Invalid cross-reference offset.");
    }
    string cancelled = Path.Combine(scratch.Path, "cancelled.pdf");
    Throws<OperationCanceledException>(() => PdfWriter.Make(paths, cancelled, "A Chapter 1", null, new CancellationToken(true)));
    Check(!File.Exists(cancelled) && !File.Exists(cancelled + ".part"), "Cancelled PDF was published.");
    return Task.CompletedTask;
}

static Task TestValidation()
{
    using var scratch = new Scratch();
    string path = Path.Combine(scratch.Path, "image.img");
    File.WriteAllBytes(path, ImageBytes(SKEncodedImageFormat.Jpeg));
    Check(ImageCache.ValidImage(path), "Valid JPEG rejected.");
    File.WriteAllBytes(path, File.ReadAllBytes(path)[..^10]);
    Check(!ImageCache.ValidImage(path), "Truncated JPEG accepted by cached receipt.");
    File.WriteAllBytes(path, ImageBytes(SKEncodedImageFormat.Webp));
    Check(ImageCache.ValidImage(path), "WebP validation failed.");
    using var budget = new MemoryBudget(64).Acquire(32L * 1024 * 1024, CancellationToken.None);
    Throws<MemoryBudgetExceededException>(() => new MemoryBudget(64).Acquire(80L * 1024 * 1024, CancellationToken.None));
    return Task.CompletedTask;
}

static Task TestPng()
{
    using var scratch = new Scratch();
    var cases = new[]
    {
        (Data: PngFixture.Make(1, 0, [0x40], splitIdat: true), Bits: 1, Palette: false),
        (Data: PngFixture.Make(1, 3, [0x40], [255, 0, 0, 0, 255, 0]), Bits: 1, Palette: true),
        (Data: PngFixture.Make(16, 0, [0x12, 0x34, 0xab, 0xcd]), Bits: 16, Palette: false)
    };
    foreach (var fixture in cases)
    {
        string image = Path.Combine(scratch.Path, "fixture.img");
        string pdf = Path.Combine(scratch.Path, "fixture.pdf");
        File.WriteAllBytes(image, fixture.Data);
        ImageCache.ValidateImage(image);
        PdfWriter.Make([image], pdf, "Series Chapter 1", 50, CancellationToken.None);
        string content = Encoding.Latin1.GetString(File.ReadAllBytes(pdf));
        Check(content.Contains($"/BitsPerComponent {fixture.Bits}"), "PNG bit depth was changed.");
        Check(content.Contains("/Indexed") == fixture.Palette, "PNG palette was changed.");
        Check(!content.Contains("/DCTDecode"), "Low-bit/16-bit PNG was converted to JPEG.");
    }
    return Task.CompletedTask;
}

static Task TestLargeImages()
{
    using var scratch = new Scratch();
    string webp = Path.Combine(scratch.Path, "large.webp");
    string jpeg = Path.Combine(scratch.Path, "large.jpg");
    byte[] originalJpeg = ImageBytes(SKEncodedImageFormat.Jpeg, width: 2048, height: 3072);
    File.WriteAllBytes(webp, ImageBytes(SKEncodedImageFormat.Webp, width: 2048, height: 3072));
    File.WriteAllBytes(jpeg, originalJpeg);
    string rejected = Path.Combine(scratch.Path, "rejected.pdf");
    Throws<MemoryBudgetExceededException>(() => PdfWriter.Make([webp], rejected, "Large Chapter 1", null, CancellationToken.None, budget: new MemoryBudget(64)));
    Check(!File.Exists(rejected) && !File.Exists(rejected + ".part"), "Memory limit published a partial PDF.");
    string output = Path.Combine(scratch.Path, "large.pdf");
    PdfWriter.Make([jpeg, webp], output, "Large Chapter 1", null, CancellationToken.None, budget: new MemoryBudget(256));
    byte[] pdf = File.ReadAllBytes(output);
    Check(pdf.AsSpan().IndexOf(originalJpeg) >= 0, "Large JPEG was not embedded unchanged.");
    Check(Encoding.Latin1.GetString(pdf).Contains("/Count 2"), "Large pages were lost.");
    return Task.CompletedTask;
}

static async Task TestMemoryBudget()
{
    var budget = new MemoryBudget(64);
    using var held = budget.Acquire(48L * 1024 * 1024, CancellationToken.None);
    var pending = Task.Run(() => budget.Acquire(32L * 1024 * 1024, CancellationToken.None));
    await Task.Delay(50);
    Check(!pending.IsCompleted, "Memory budget admitted overlapping reservations above its capacity.");
    held.Dispose();
    using (await pending.WaitAsync(TimeSpan.FromSeconds(2))) { }

    using var full = budget.Acquire(64L * 1024 * 1024, CancellationToken.None);
    using var cancel = new CancellationTokenSource();
    var waiting = Task.Run(() => budget.Acquire(16L * 1024 * 1024, cancel.Token));
    await Task.Delay(50);
    cancel.Cancel();
    try
    {
        using var unexpected = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        throw new Exception("Cancelled memory reservation was admitted.");
    }
    catch (OperationCanceledException) { }
}

static async Task TestPipeline()
{
    using var scratch = new Scratch();
    await using var server = new LocalServer(ImageBytes());
    var options = Settings(scratch.Path, "--workers", "2", "--chapter-workers", "3", "--pdf-workers", "2", "--write-manifest");
    using var client = new Client(options);
    Check(await DownloadPipeline.Run(client, Chapters(server, 12), options, CancellationToken.None) == 0, "Pipeline failed.");
    Check(server.MaxImages == 2 && server.MaxChapters >= 2, "Image concurrency or chapter overlap was lost.");
    Check(!server.WrongReferer, "Image Referer is wrong.");
    Check(Directory.GetFiles(scratch.Path, "*.pdf").Length == 12, "PDF workers did not drain.");
    Check(Directory.GetFiles(scratch.Path, "*.pages.sha256").Length == 12, "Missing manifests.");
    int imagesBefore = server.Hits.Where(p => p.Key.StartsWith("/image/")).Sum(p => p.Value);
    var rebuild = Settings(scratch.Path, "--rebuild", "--write-manifest");
    using var rebuildClient = new Client(rebuild);
    Check(await DownloadPipeline.Run(rebuildClient, Chapters(server, 12), rebuild, CancellationToken.None) == 0, "Cache rebuild failed.");
    Check(server.Hits.Where(p => p.Key.StartsWith("/image/")).Sum(p => p.Value) == imagesBefore, "Rebuild downloaded valid cache.");
    string damaged = Directory.GetFiles(scratch.Path, "*.img", SearchOption.AllDirectories)[0];
    File.WriteAllText(damaged, "corrupt");
    Check(await DownloadPipeline.Run(rebuildClient, Chapters(server, 12), rebuild, CancellationToken.None) == 0, "Cache repair failed.");
    Check(server.Hits.Where(p => p.Key.StartsWith("/image/")).Sum(p => p.Value) == imagesBefore + 1, "Damaged cache was not selectively repaired.");
    int beforeSkip = server.Hits.Values.Sum();
    Check(await DownloadPipeline.Run(client, Chapters(server, 12), options, CancellationToken.None) == 0, "Manifest skip failed.");
    Check(server.Hits.Values.Sum() == beforeSkip + 12, "Manifest skip requested images.");
}

static async Task TestSlowTail()
{
    using var scratch = new Scratch();
    await using var server = new LocalServer(ImageBytes()) { StallFirstTailImages = true };
    var options = Settings(scratch.Path, "--workers", "3", "--chapter-workers", "2", "--timeout", "0.1");
    using var client = new Client(options);
    Check(await DownloadPipeline.Run(client, Chapters(server, 4), options, CancellationToken.None) == 0, "Timed-out images did not recover.");
    Check(server.MaxChapters >= 3, "Later chapters waited for slow image retries.");
    Check(server.Hits["/image/1/3"] >= 2 && server.Hits["/image/2/3"] >= 2, "Slow image fixture did not trigger retries.");
    Check(Directory.GetFiles(scratch.Path, "*.pdf").Length == 4, "The slow chapters were not completed.");
}

static async Task TestSequential()
{
    using var scratch = new Scratch();
    await using var server = new LocalServer(ImageBytes());
    var options = Settings(scratch.Path, "--chapter-workers", "1", "--delete-image-cache");
    using var client = new Client(options);
    Check(await DownloadPipeline.Run(client, Chapters(server), options, CancellationToken.None) == 0, "Sequential pipeline failed.");
    Check(server.MaxChapters == 1, "Sequential chapters overlapped.");
    Check(Directory.GetFiles(scratch.Path, "*.img", SearchOption.AllDirectories).Length == 0, "Cache was not cleaned.");
    Check(Directory.GetFiles(scratch.Path, "*.pages.sha256").Length == 0, "Checksum should be optional.");
    int before = server.Hits.Values.Sum();
    await DownloadPipeline.Run(client, Chapters(server), options, CancellationToken.None);
    Check(server.Hits.Values.Sum() == before, "Existing PDFs were not skipped before HTTP requests.");
}

static async Task TestFailure()
{
    using var scratch = new Scratch();
    await using var server = new LocalServer(ImageBytes()) { FailChapter = true };
    var options = Settings(scratch.Path);
    using var client = new Client(options);
    Check(await DownloadPipeline.Run(client, Chapters(server, 3), options, CancellationToken.None) == 1, "Failure exit code missing.");
    Check(Directory.GetFiles(scratch.Path, "*.pdf").Length == 2, "Good chapters did not complete after failure.");
    Check(!File.Exists(Path.Combine(scratch.Path, "Test Series - Ch 002.pdf")), "Failed chapter produced a PDF.");
}

static async Task TestCollision()
{
    using var scratch = new Scratch();
    await using var server = new LocalServer(ImageBytes());
    var options = Settings(scratch.Path);
    using var client = new Client(options);
    try
    {
        await DownloadPipeline.Run(client, [new("A Chapter 1", server.Url + "/chapters/1"), new("A Chapter 1", server.Url + "/chapters/2")], options, CancellationToken.None);
        throw new Exception("Collision was accepted.");
    }
    catch (InvalidDataException) { }
    Check(server.Hits.IsEmpty, "Collision failed after starting HTTP requests.");
}

static async Task TestPdfFailure()
{
    using var scratch = new Scratch();
    await using var server = new LocalServer(ImageBytes());
    Directory.CreateDirectory(Path.Combine(scratch.Path, "Test Series - Ch 001.pdf"));
    var options = Settings(scratch.Path, "--delete-image-cache");
    using var client = new Client(options);
    Check(await DownloadPipeline.Run(client, Chapters(server, 2), options, CancellationToken.None) == 1, "PDF failure was not reported.");
    Check(Directory.GetFiles(scratch.Path, "*.img", SearchOption.AllDirectories).Length == 3, "Failed PDF lost cache or successful PDF kept cache.");
    Check(!File.Exists(Path.Combine(scratch.Path, "Test Series - Ch 001.pdf.part")), "Failed PDF left a partial file.");
    Check(File.Exists(Path.Combine(scratch.Path, "Test Series - Ch 002.pdf")), "Other PDF work did not finish.");
}

static async Task TestCancellation()
{
    using var scratch = new Scratch();
    await using var server = new LocalServer(ImageBytes()) { ImageDelay = 500 };
    var options = Settings(scratch.Path);
    using var client = new Client(options);
    using var cancellation = new CancellationTokenSource(150);
    try
    {
        await DownloadPipeline.Run(client, Chapters(server, 20), options, cancellation.Token);
        throw new Exception("Cancellation was ignored.");
    }
    catch (OperationCanceledException) { }
    Check(Directory.GetFiles(scratch.Path, "*.part", SearchOption.AllDirectories).Length == 0, "Partial files remain after cancellation.");
}

static async Task TestRetries()
{
    using var scratch = new Scratch();
    await using var server = new LocalServer(ImageBytes()) { RateLimitOnce = true };
    using var client = new Client(Settings(scratch.Path));
    var timer = Stopwatch.StartNew();
    await client.GetText(server.Url + "/retry", null, CancellationToken.None);
    Check(server.Hits["/retry"] == 2 && timer.ElapsedMilliseconds >= 950, "Retry-After was ignored.");
    try
    {
        await client.GetText(server.Url + "/long-retry", null, CancellationToken.None);
        throw new Exception("Excessive Retry-After accepted.");
    }
    catch (InvalidDataException) { }
    try
    {
        await client.GetText(server.Url + "/always-retry", null, CancellationToken.None);
        throw new Exception("Persistent rate limiting was ignored.");
    }
    catch (HttpRequestException) { }
    timer.Restart();
    await client.GetText(server.Url + "/after-retry", null, CancellationToken.None);
    Check(timer.ElapsedMilliseconds >= 900, "Final rate-limited attempt did not pause other requests on its host.");
}

static async Task TestApplication()
{
    using var scratch = new Scratch();
    await using var server = new LocalServer(ImageBytes());
    int result = await Application.MainAsync([server.Url + "/series/test", "-o", scratch.Path, "--delay", "0", "--limit", "1"]);
    Check(result == 0 && File.Exists(Path.Combine(scratch.Path, "Test Series - Ch 001.pdf")), "Series discovery, sorting or limit failed.");
    result = await Application.MainAsync(["--chapter", server.Url + "/chapters/2", "-o", scratch.Path, "--delay", "0"]);
    Check(result == 0 && File.Exists(Path.Combine(scratch.Path, "Test Series - Ch 002.pdf")), "Direct chapter title normalization failed.");
}

static async Task TestPublished(string executable)
{
    using var scratch = new Scratch();
    await using var server = new LocalServer(ImageBytes(SKEncodedImageFormat.Webp));
    var start = new ProcessStartInfo(executable)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardError = true,
        RedirectStandardOutput = true
    };
    foreach (string argument in new[] { "--chapter", server.Url + "/chapters/1", "-o", scratch.Path, "--delay", "0" })
        start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new Exception("Could not launch published binary.");
    Task<string> errors = process.StandardError.ReadToEndAsync();
    Task<string> output = process.StandardOutput.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try { await process.WaitForExitAsync(timeout.Token); }
    catch (OperationCanceledException)
    {
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        throw;
    }
    Check(process.ExitCode == 0, "Published binary failed: " + await errors);
    await output;
    Check(File.Exists(Path.Combine(scratch.Path, "Test Series - Ch 001.pdf")), "Published binary did not create a PDF.");
}

internal sealed class Scratch : IDisposable
{
    private readonly string root = System.IO.Path.GetFullPath("tmp/csharp-tests");
    public string Path { get; }
    public Scratch()
    {
        Path = System.IO.Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }
    public void Dispose()
    {
        // Delete only this newly created test directory, after checking its absolute parent.
        if (System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path)) != root)
            throw new InvalidOperationException("Test cleanup escaped its temporary root.");
        Directory.Delete(Path, recursive: true);
    }
}
