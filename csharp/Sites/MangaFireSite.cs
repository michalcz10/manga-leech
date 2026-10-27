using System.Text.Json;

namespace MangaLeech.Core;

internal sealed class MangaFireSite(Options options) : IMangaSite
{
    private readonly ConcurrentDictionary<string, List<string>> prefetched = new();

    internal static (string Slug, string? ChapterId) ParseUrl(string url)
    {
        var match = Regex.Match(new Uri(url).AbsolutePath.TrimEnd('/'), @"^/title/([\w-]+)(?:/chapter/([\w-]+))?$", RegexOptions.CultureInvariant);
        if (!match.Success)
            throw new ArgumentException("MangaFire URL must be /title/SLUG or /title/SLUG/chapter/ID.");
        return (match.Groups[1].Value, match.Groups[2].Success ? match.Groups[2].Value : null);
    }

    private async Task<JsonDocument> Api(Client client, string basis, string path, Dictionary<string, string> parameters, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string signature = MangaFireSigner.Sign(path, parameters);
        string query = string.Join("&", parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        string url = SameHost("/api" + path, basis) + "?" + (query.Length > 0 ? query + "&" : "") + "vrf=" + Uri.EscapeDataString(signature);
        try
        {
            return JsonDocument.Parse(await client.GetText(url, basis, ct));
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new InvalidDataException("MangaFire rejected the signed HTTP request (403). Its signature or browser protection may have changed; no browser is started automatically.", e);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("MangaFire returned non-JSON API data, possibly a browser challenge.", e);
        }
    }

    public async Task<List<Chapter>> GetChapters(Client client, string url, CancellationToken ct)
    {
        var (slug, chapterId) = ParseUrl(url);
        if (chapterId != null) throw new ArgumentException("Use --chapter for a MangaFire chapter URL.");
        string titleId = slug.Split('-')[0];
        using var detail = await Api(client, url, $"/titles/{titleId}", [], ct);
        string name = detail.RootElement.GetProperty("data").GetProperty("title").GetString()?.Trim() ?? "";
        if (name.Length == 0) throw new InvalidDataException("MangaFire title is empty.");
        var selected = new Dictionary<string, (Chapter Chapter, bool Official)>();
        int last = 1;
        for (int number = 1; number <= last; number++)
        {
            using var response = await Api(client, url, $"/titles/{titleId}/chapters", new()
            {
                ["language"] = options.Language, ["sort"] = "number", ["order"] = "asc",
                ["limit"] = "100", ["page"] = number.ToString(CultureInfo.InvariantCulture)
            }, ct);
            var root = response.RootElement;
            int page = root.GetProperty("meta").GetProperty("page").GetInt32();
            int reportedLast = root.GetProperty("meta").GetProperty("lastPage").GetInt32();
            if (page != number || reportedLast < 1 || reportedLast > 10000 || (number > 1 && reportedLast != last))
                throw new InvalidDataException("Unexpected MangaFire pagination metadata.");
            last = reportedLast;
            AddChapters(root, name, url, selected);
            Log($"MangaFire: received chapter-list page {page} of {last}");
        }
        if (selected.Count == 0)
            throw new InvalidDataException($"MangaFire has no chapters in language '{options.Language}'. Use --language CODE.");
        return selected.Values.Select(value => value.Chapter).ToList();
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

    private async Task<(Chapter Chapter, List<string> Images)> ReadChapter(Client client, string url, CancellationToken ct)
    {
        var (_, id) = ParseUrl(url);
        if (id == null) throw new ArgumentException("--chapter requires a MangaFire chapter URL.");
        using var response = await Api(client, url, $"/chapters/{id}", [], ct);
        return ParseChapter(response.RootElement, url);
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
        var result = await ReadChapter(client, url, ct);
        prefetched[url] = result.Images;
        return result.Chapter;
    }

    public async Task<List<string>> GetImages(Client client, string url, CancellationToken ct) =>
        prefetched.TryRemove(url, out var images) ? images : (await ReadChapter(client, url, ct)).Images;

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
