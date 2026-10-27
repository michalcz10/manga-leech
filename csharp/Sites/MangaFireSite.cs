using System.Text.Json;
using Microsoft.Playwright;

namespace MangaLeech.Core;

// Let the site's application make its own signed API requests. No copied signing
// algorithm or JavaScript bundle is stored in the downloader.
internal sealed class MangaFireSite(Options options) : IMangaSite
{
    private IPlaywright? playwright;
    private IBrowser? browser;
    private IBrowserContext? context;
    private readonly SemaphoreSlim initialize = new(1);
    private readonly ConcurrentDictionary<string, List<string>> prefetched = new();

    private async Task<IBrowserContext> Context(CancellationToken ct)
    {
        await initialize.WaitAsync(ct);
        try
        {
            if (context != null) return context;
            playwright ??= await Playwright.CreateAsync().WaitAsync(ct);
            browser = await playwright.Chromium.LaunchAsync(new()
            {
                Channel = options.BrowserChannel,
                Headless = !options.BrowserHeaded
            }).WaitAsync(ct);
            context = await browser.NewContextAsync(new());
            context.SetDefaultTimeout((float)(options.Timeout * 1000));
            return context;
        }
        catch (PlaywrightException e)
        {
            throw new InvalidOperationException("MangaFire needs a Playwright Chromium browser. On Windows install Edge, or install Chromium with the generated playwright.ps1 and use --browser-channel chromium. " + e.Message, e);
        }
        finally { initialize.Release(); }
    }

    internal static (string Slug, string? ChapterId) ParseUrl(string url)
    {
        var match = Regex.Match(new Uri(url).AbsolutePath.TrimEnd('/'), @"^/title/([\w-]+)(?:/chapter/([\w-]+))?$", RegexOptions.CultureInvariant);
        if (!match.Success)
            throw new ArgumentException("MangaFire URL must be /title/SLUG or /title/SLUG/chapter/ID.");
        return (match.Groups[1].Value, match.Groups[2].Success ? match.Groups[2].Value : null);
    }

    private async Task<JsonDocument> Response(IPage page, string path, Func<Task> action, CancellationToken ct, int? pageNumber = null)
    {

        var pending = page.WaitForResponseAsync(response =>
        {
            var uri = new Uri(response.Url);
            return uri.Authority == new Uri(page.Url).Authority && uri.AbsolutePath == path &&
                (pageNumber == null || Regex.IsMatch(uri.Query, $@"[?&]page={pageNumber}(?:&|$)"));
        });
        try
        {
            await action().WaitAsync(ct);
            var response = await pending.WaitAsync(ct);
            if (!response.Ok)
                throw new InvalidDataException($"MangaFire API returned HTTP {response.Status}. Try --browser-headed if the website requires a browser challenge.");
            return JsonDocument.Parse(await response.TextAsync().WaitAsync(ct));
        }
        catch (System.TimeoutException e)
        {

            throw new InvalidDataException("MangaFire did not return chapter data in time. Try --browser-headed to inspect a browser challenge, or increase --timeout. " + e.Message, e);
        }
        finally
        {
            // Observe a waiter rejected when navigation/cancellation closes the page.
            if (!pending.IsCompletedSuccessfully)
                _ = pending.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    public async Task<List<Chapter>> GetChapters(Client client, string url, CancellationToken ct)
    {
        var (slug, chapterId) = ParseUrl(url);
        string titleId = slug.Split('-')[0];
        if (chapterId != null)
            throw new ArgumentException("Use --chapter for a MangaFire chapter URL.");
        var page = await (await Context(ct)).NewPageAsync().WaitAsync(ct);
        try
        {
            // Switch the app's default language filter to All, then filter locally
            // after every page so releases can be selected consistently.
            using var initial = await Response(page, $"/api/titles/{titleId}/chapters",
                async () => { await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded }); }, ct);
            await page.Locator("button.select").Filter(new() { HasTextRegex = new Regex("^Lang", RegexOptions.IgnoreCase) })
                .DispatchEventAsync("click").WaitAsync(ct);
            using var first = await Response(page, $"/api/titles/{titleId}/chapters", async () =>
            {
                await page.GetByRole(AriaRole.Button, new() { Name = "All", Exact = true }).DispatchEventAsync("click");
            }, ct, 1);
            string name = (await page.Locator("h1").First.InnerTextAsync().WaitAsync(ct)).Trim();
            if (name.Length == 0) throw new InvalidDataException("MangaFire title is empty.");
            var selected = new Dictionary<string, (Chapter Chapter, bool Official)>();
            AddChapters(first.RootElement, name, url, selected);
            Log($"MangaFire: received chapter-list page {first.RootElement.GetProperty("meta").GetProperty("page")} of {first.RootElement.GetProperty("meta").GetProperty("lastPage")}");
            int last = first.RootElement.GetProperty("meta").GetProperty("lastPage").GetInt32();
            int current = first.RootElement.GetProperty("meta").GetProperty("page").GetInt32();
            if (current != 1 || last < 1 || last > 10000)
                throw new InvalidDataException("Unexpected MangaFire pagination metadata.");
            for (int number = 2; number <= last; number++)
            {
                await page.Locator(".title-detail__chapters-pager [aria-current=page]").Filter(new() { HasTextRegex = new Regex($"^{number - 1}$") }).WaitForAsync().WaitAsync(ct);
                await Task.Delay(TimeSpan.FromSeconds(options.Delay), ct);
                // Dispatch to the actual React button: the site's advertisement
                // overlay can intercept physical clicks near the last page.
                using var next = await Response(page, $"/api/titles/{titleId}/chapters", async () =>
                {
                    await page.Locator(".title-detail__chapters-pager")
                        .GetByRole(AriaRole.Button, new() { Name = number.ToString(CultureInfo.InvariantCulture), Exact = true }).DispatchEventAsync("click");
                }, ct, number);
                if (next.RootElement.GetProperty("meta").GetProperty("page").GetInt32() != number)
                    throw new InvalidDataException("MangaFire returned an unexpected chapter page.");
                AddChapters(next.RootElement, name, url, selected);
                Log($"MangaFire: received chapter-list page {number} of {last}");
            }
            if (selected.Count == 0)
                throw new InvalidDataException($"MangaFire has no chapters in language '{options.Language}'. Use --language CODE.");
            return selected.Values.Select(value => value.Chapter).ToList();
        }
        finally { await page.CloseAsync(); }
    }

    internal void AddChapters(JsonElement root, string name, string url, Dictionary<string, (Chapter Chapter, bool Official)> selected)
    {
        foreach (var item in root.GetProperty("items").EnumerateArray())
        {
            if (item.GetProperty("language").GetString() != options.Language) continue;
            string number = Number(item.GetProperty("number"));
            string id = item.GetProperty("id").ToString();
            if (!Regex.IsMatch(id, @"^[\w-]+$")) throw new InvalidDataException("Invalid MangaFire chapter ID.");
            bool official = item.TryGetProperty("type", out var type) && type.GetString() == "official";
            var chapter = new Chapter($"{name} Chapter {number}", SameHost($"/title/{ParseUrl(url).Slug}/chapter/{id}", url));
            // Multiple releases of a number would otherwise collide as PDF names.
            if (!selected.TryGetValue(number, out var existing) || (official && !existing.Official))
                selected[number] = (chapter, official);
        }
    }

    private static string Number(JsonElement value)
    {
        string number = value.ToString();
        if (!Regex.IsMatch(number, @"^\d+(?:\.\d+)?$"))
            throw new InvalidDataException($"Invalid MangaFire chapter number: {number}");
        return decimal.Parse(number, CultureInfo.InvariantCulture).ToString("0.############################", CultureInfo.InvariantCulture);
    }

    private async Task<(Chapter Chapter, List<string> Images)> ReadChapter(string url, CancellationToken ct)
    {
        var (_, id) = ParseUrl(url);
        if (id == null) throw new ArgumentException("--chapter requires a MangaFire chapter URL.");
        var page = await (await Context(ct)).NewPageAsync().WaitAsync(ct);
        try
        {
            using var response = await Response(page, $"/api/chapters/{id}",
                async () => { await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded }); }, ct);
            return ParseChapter(response.RootElement, url);
        }
        finally { await page.CloseAsync(); }
    }

    internal static (Chapter Chapter, List<string> Images) ParseChapter(JsonElement root, string url)
    {
        var data = root.GetProperty("data");
        string name = data.GetProperty("title").GetProperty("name").GetString()?.Trim() ?? "";
        if (name.Length == 0) throw new InvalidDataException("MangaFire chapter title is empty.");
        var images = data.GetProperty("pages").EnumerateArray()
            .Select(page => HttpUrl(page.GetProperty("url").GetString()!, url)).ToList();
        if (images.Count == 0) throw new InvalidDataException("MangaFire chapter contains no pages.");
        return (new Chapter($"{name} Chapter {Number(data.GetProperty("number"))}", url), images);
    }

    public async Task<Chapter> GetChapter(Client client, string url, CancellationToken ct)
    {
        var result = await ReadChapter(url, ct);
        prefetched[url] = result.Images;
        return result.Chapter;
    }

    public async Task<List<string>> GetImages(Client client, string url, CancellationToken ct) =>
        prefetched.TryRemove(url, out var images) ? images : (await ReadChapter(url, ct)).Images;

    public async ValueTask DisposeAsync()
    {
        if (browser != null) await browser.CloseAsync();
        playwright?.Dispose();
        initialize.Dispose();
    }
}
