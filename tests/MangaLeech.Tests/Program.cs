using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MangaLeech.Core;
using SkiaSharp;
using static MangaLeech.Core.ChapterParser;

var tests = new (string Name, Func<Task> Run)[]
{
    ("CLI defaults and validation", TestOptions),
    ("RSS/Atom, image selection, ordering and metadata", TestParsing),
    ("PDF streams, transparency, page order, XMP and xref", TestPdf),
    ("Strict image validation and cache repair", TestValidation),
    ("PNG palette and bit depth preservation", TestPng),
    ("Parallel chapters, global image limit, manifests and rebuild", TestPipeline),
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
    Check(options.Workers == 8 && options.PdfWorkers == 4 && options.ChapterWorkers == 3, "Incorrect defaults.");
    foreach (string argument in new[] { "--workers", "--pdf-workers", "--chapter-workers", "--limit" })
    {
        Throws<ArgumentException>(() => Options.Parse(["https://example.test/rss", argument, "0"]));
    }
    Throws<ArgumentException>(() => Options.Parse(["https://example.test/rss", "--timeout", "NaN"]));
    Throws<ArgumentException>(() => Options.Parse(["https://example.test/rss", "--timeout", "5000000"]));
    Throws<ArgumentException>(() => Options.Parse(["https://example.test/rss", "--jpeg-quality", "96"]));
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
    return Task.CompletedTask;
}

static Task TestPng()
{
    using var scratch = new Scratch();
    var cases = new[]
    {
        (Data: PngFixture.Make(1, 0, [0x40]), Bits: 1, Palette: false),
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

static async Task TestPipeline()
{
    using var scratch = new Scratch();
    await using var server = new LocalServer(ImageBytes());
    var options = Settings(scratch.Path, "--workers", "2", "--chapter-workers", "3", "--pdf-workers", "2", "--write-manifest");
    using var client = new Client(options);
    Check(await DownloadPipeline.Run(client, Chapters(server, 12), options, CancellationToken.None) == 0, "Pipeline failed.");
    Check(server.MaxImages == 2 && server.MaxChapters >= 2 && server.MaxChapters <= 3, "Concurrency budgets were not respected.");
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
