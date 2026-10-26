namespace MangaLeech.Core;

internal static class ChapterParser
{
    internal static readonly Regex Label = new(@"\b(?:Chapter|Page|Volume|Vol|Episode|Kapitel)\.?\s+(\d+(?:\.\d+)?)(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    internal static Match? LastLabel(string title) => Label.Matches(title).Cast<Match>().LastOrDefault();
    internal static (string Title, string? Number) Metadata(string title)
    {
        var m = LastLabel(title);
        return m != null && title[..m.Index].Trim() is { Length: > 0 } series ? (series, m.Groups[1].Value) : (title, null);
    }

    internal static string Clean(string title)
    {
        title = Regex.Replace(title.Normalize(NormalizationForm.FormKC), "[<>:\"/\\\\|?*\\x00-\\x1f]", "_").Trim(' ', '.');
        return title.Length > 120 ? title[..120] : title.Length == 0 ? "chapter" : title;
    }

    internal static string CacheName(Chapter ch) => $"{Clean(ch.Title)}-{Hash(ch.Url)[..10]}";
    internal static string PdfName(Chapter ch)
    {
        var (series, number) = Metadata(ch.Title);
        if (number == null)
            return CacheName(ch);
        var parts = number.Split('.');
        string index = BigInteger.Parse(parts[0], CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture).PadLeft(3, '0');
        if (parts.Length > 1)
            index += "." + parts[1];
        string kind = LastLabel(ch.Title)!.Value.StartsWith("Vol", StringComparison.OrdinalIgnoreCase) ? "Vol" : "Ch";
        return $"{Clean(series)} - {kind} {index}";
    }

    internal static int CompareChapters(Chapter a, Chapter b)
    {
        var ma = LastLabel(a.Title);
        var mb = LastLabel(b.Title);
        if (ma == null || mb == null)
            return ma == null ? (mb == null ? 0 : 1) : -1;
        var x = ma.Groups[1].Value.Split('.');
        var y = mb.Groups[1].Value.Split('.');
        int c = BigInteger.Parse(x[0]).CompareTo(BigInteger.Parse(y[0]));
        if (c != 0)
            return c;
        string xf = x.Length > 1 ? x[1] : "", yf = y.Length > 1 ? y[1] : "";
        int n = Math.Max(xf.Length, yf.Length);
        return string.CompareOrdinal(xf.PadRight(n, '0'), yf.PadRight(n, '0'));
    }

    internal static List<Chapter> Feed(byte[] data, string basis)
    {
        using var stream = new MemoryStream(data);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var root = XDocument.Load(reader);
        var result = new List<Chapter>();
        var seen = new HashSet<string>();
        foreach (var item in root.Descendants().Where(n => n.Name.LocalName is "item" or "entry"))
        {
            string title = item.Elements().FirstOrDefault(n => n.Name.LocalName == "title")?.Value.Trim() ?? "";
            var link = item.Elements().LastOrDefault(n => n.Name.LocalName == "link" && ((string?)n.Attribute("rel") ?? "alternate") == "alternate");
            string? href = ((string?)link?.Attribute("href") ?? link?.Value)?.Trim();
            if (string.IsNullOrEmpty(href))
                continue;
            string url = HttpUrl(href, basis);
            if (seen.Add(url))
                result.Add(new Chapter(title.Length > 0 ? title : new Uri(url).Segments.Last(), url));
        }

        if (result.Count == 0)
            throw new InvalidDataException("The feed contains no chapter links.");
        return result;
    }

    internal static List<string> ImageUrls(string html, string basis)
    {
        var images = Html(html).GetElementbyId("chapter-images")?.Descendants("img") ?? Enumerable.Empty<HtmlNode>();
        var urls = images.Select(n => n.GetAttributeValue("data-src", "") is { Length: > 0 } src ? src : n.GetAttributeValue("src", "")).Where(s => s.Length > 0).Select(s => HttpUrl(WebUtility.HtmlDecode(s), basis)).Distinct().ToList();
        if (urls.Count == 0)
            throw new InvalidDataException("No images found in #chapter-images.");
        return urls;
    }
}
