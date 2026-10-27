namespace MangaLeech.Core;

internal sealed class WeebCentralSite : IMangaSite
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public async Task<Chapter> GetChapter(Client client, string url, CancellationToken ct)
    {
        string html = await client.GetText(url, null, ct);
        client.Prefetched[url] = html;
        string title = Text(Html(html).DocumentNode.SelectSingleNode("//title"));
        var parts = title.Split(" | ").Select(p => p.Trim()).ToArray();
        if (parts.Length >= 3 && Label.Match(parts[0]) is { Success: true } m && m.Length == parts[0].Length)
            title = parts[1] + " " + parts[0];
        else if (title.EndsWith(" | Weeb Central"))
            title = title[..^15].Trim();
        if (title.Length == 0)
            throw new InvalidDataException("Cannot determine a chapter title for PDF metadata.");
        return new Chapter(title, url);
    }

    public async Task<List<string>> GetImages(Client client, string url, CancellationToken ct) =>
        ImageUrls(await ChapterHtml(client, url, ct), url);
    private async Task<string> ChapterHtml(Client client, string url, CancellationToken ct)
    {
        string html = client.Prefetched.TryRemove(url, out var cached) ? cached : await client.GetText(url, null, ct);
        if (Html(html).GetElementbyId("chapter-images")?.Descendants("img").Any() == true)
            return html;
        var match = Regex.Match(html, "[\"']([^\"']+/images\\?is_prev=False)[\"']");
        return match.Success ? await client.GetText(SameHost(WebUtility.HtmlDecode(match.Groups[1].Value), url) + "&reading_style=long_strip&current_page=1", url, ct) : html;
    }

    public async Task<List<Chapter>> GetChapters(Client client, string url, CancellationToken ct)
    {
        string path = new Uri(url).AbsolutePath.TrimEnd('/');
        if (!path.StartsWith("/series/") || path.EndsWith("/rss"))
            return Feed(await client.Get(url, null, ct), url);
        var doc = Html(await client.GetText(url, null, ct));
        string name = Text(doc.DocumentNode.SelectSingleNode("//title")).Split(" | ")[0].Trim();
        var endpoint = doc.DocumentNode.Descendants().Select(n => n.GetAttributeValue("hx-get", "")).FirstOrDefault(s => s.Length > 0 && new Uri(new Uri(url), WebUtility.HtmlDecode(s)).AbsolutePath.EndsWith("/full-chapter-list"));
        HtmlNode root;
        if (endpoint != null)
            root = Html(await client.GetText(SameHost(WebUtility.HtmlDecode(endpoint), url), url, ct)).DocumentNode;
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
